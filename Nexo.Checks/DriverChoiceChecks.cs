using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// Pergunta de dispositivo do fluxo de driver (#74): com mais de um dispositivo na categoria, o Nexo pergunta qual,
// e só a resposta que é apenas o nome de um dos dispositivos mostrados retoma o pedido (#70), dentro de 30 minutos e com a
// pergunta como última resposta do assistente. O alvo sai do inventário consultado de novo, pelo ID. Qualquer outra
// mensagem descarta o pendente e vai ao modelo. Os testes usam a mesma Conversation nos dois turnos.
internal static class DriverChoiceChecks
{
    private const string RealtekId = "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_1458A194\\4&1A2B3C4D&0&0001";
    private const string NvidiaId = "HDAUDIO\\FUNC_01&VEN_10DE&DEV_228B&SUBSYS_38861458\\5&2B3C4D5E&0&0001";
    private const string Realtek = "Realtek High Definition Audio", Nvidia = "NVIDIA High Definition Audio";
    private const string NvidiaOffer = "00000000-0000-0000-0000-0000000000c2";
    private const string Facts = "Alterações feitas pelo Nexo neste turno:";
    private const char LineSeparator = (char)0x2028, Bell = (char)7;
    private static readonly DateTime Start = new(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);

    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        var previousAi = store.Data.AiEnabled;
        store.Data.AiEnabled = true;
        try
        {
            await RealPcAudio(store, actor, assert);
            await NotAName(store, actor, assert);
            await RealNames(store, actor, assert);
            await Expiry(store, actor, assert);
            await SameNameAndVanished(store, actor, assert);
            await Hostile(store, actor, assert);
            await IntentAndRefusal(store, actor, assert);
        }
        finally { store.Data.AiEnabled = previousAi; }
    }

    private static Device Audio(string name, string id) => new(name, id, 0, "OK") { HardwareIds = [id.Split('\\')[0] + "\\" + id.Split('\\')[1]], DriverVersion = "6.0.1.1", DriverProvider = "Fornecedor" };
    private static readonly Device[] PcAudio = [Audio(Realtek, RealtekId), Audio(Nvidia, NvidiaId)];

    // Ferramentas falsas: o inventário de cada turno vem de devices(); a busca traz a oferta só do dispositivo offerFor.
    private static Func<string, JsonElement, string> Tools(Func<Device[]> devices, string? install = null, string offerFor = NvidiaId, string? title = null) => (name, args) => name switch
    {
        "get_hardware_inventory" => WindowsTools.Ok(new { Total = devices().Length, Truncated = false, Devices = devices() }),
        "scan_driver_updates" => WindowsTools.Ok(JsonSerializer.Serialize(devices().Where(device => device.Id == offerFor).Select(device => new
        {
            Id = device.Id == NvidiaId ? NvidiaOffer : "00000000-0000-0000-0000-0000000000c9",
            Title = title ?? "Driver de " + device.Name, HardwareID = device.HardwareIds[0], Target = device.Name
        }).ToArray())),
        "install_driver_update" => install ?? WindowsTools.Ok(new { Verification = "version_changed", AfterVersion = "6.0.2.0" }),
        _ => throw new Exception("Ferramenta inesperada no fluxo de driver: " + name)
    };

    private sealed record Turn(string Answer, List<string> Calls, List<string> Installed, int ModelCalls);

    private const string ModelAnswer = "Resposta do modelo para esta mensagem.";
    // Sem roteiro (driver), o modelo não pode ser chamado; com roteiro, ele encerra com finish_support.
    private static async Task<Turn> Send(Store store, Account actor, Conversation conversation, string text, Func<string, JsonElement, string> execute,
        DateTime? at = null, bool model = false)
    {
        conversation.Entries.Add(new Entry { Role = "user", Text = text });
        var installed = new List<string>();
        var tools = new FlowTools((name, args) =>
        {
            if (name == "install_driver_update") installed.Add(args.GetProperty("update_id").GetString() ?? "");
            return execute(name, args);
        });
        using var sequence = new ModelSequence(model ? [Call("finish_support", new { message = ModelAnswer })] : []);
        using var http = new HttpClient(sequence);
        var clock = at ?? Start;
        var answer = await new AgentService(store, tools, http, utcNow: () => clock).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
        conversation.Entries.Add(new Entry { Role = "assistant", Text = answer });
        return new Turn(answer, tools.Calls, installed, sequence.Calls);
    }

    private static async Task<Conversation> Asked(Store store, Account actor, Func<string, JsonElement, string> execute, string request = "Atualize o driver de áudio")
    {
        var conversation = new Conversation { UserId = actor.Id };
        await Send(store, actor, conversation, request, execute);
        return conversation;
    }

    // Caso real do PC de teste (#8): áudio da placa-mãe e áudio HDMI da NVIDIA na mesma categoria.
    private static async Task RealPcAudio(Store store, Account actor, Action<bool, string> assert)
    {
        var execute = Tools(() => PcAudio);
        var conversation = new Conversation { UserId = actor.Id };
        var asked = await Send(store, actor, conversation, "Atualize o driver de áudio", execute);
        assert(asked.Calls.SequenceEqual(["get_hardware_inventory"]) && asked.Answer.Contains("“" + Realtek + "”") && asked.Answer.Contains("“" + Nvidia + "”")
            && asked.Answer.Contains("Qual deles apresenta o problema? " + InvestigationEvidence.ReplyWithName) && asked.Answer.Contains("Nenhum driver foi alterado."),
            "Dois dispositivos de áudio: a pergunta mostra os nomes entre aspas e pede só o nome");
        assert(conversation.PendingDriverChoice is { Request: "Atualize o driver de áudio" } pending && pending.Names.SequenceEqual([Nvidia, Realtek])
            && pending.Question == asked.Answer && pending.AskedUtc == Start,
            "O pendente guarda o pedido, os nomes mostrados (sem IDs), a pergunta exata e o horário");
        assert(asked.Answer.TrimEnd().EndsWith(Facts + " nenhuma.", StringComparison.Ordinal), "Pergunta de dispositivo termina com a linha de fatos sem alteração (#73)");
        assert(DriverWorkflow.ResumedRequest(Pending(conversation, Nvidia), Start.AddMinutes(1)) == "Atualize o driver de áudio"
            && DriverWorkflow.ResumedRequest(Pending(conversation, "deixa pra lá"), Start.AddMinutes(1)) == null,
            "A decisão de retomada que a janela usa para não pedir o Ollama aceita só o nome de um dispositivo mostrado");

        var chosen = await Send(store, actor, conversation, Nvidia, execute, Start.AddMinutes(2));
        assert(chosen.Calls.SequenceEqual(["get_hardware_inventory", "scan_driver_updates", "install_driver_update"]) && chosen.Installed.SequenceEqual([NvidiaOffer]) && chosen.ModelCalls == 0,
            "Resposta só com o nome retoma o fluxo com o inventário novo e instala a oferta do dispositivo escolhido (" + string.Join(",", chosen.Calls) + ")");
        assert(chosen.Answer.Contains("Identifiquei “" + Nvidia + "”") && chosen.Answer.Contains("“Driver de " + Nvidia + "”") && chosen.Answer.Contains("versão observada passou para 6.0.2.0")
            && chosen.Answer.Contains(Facts) && conversation.PendingDriverChoice == null,
            "Fluxo retomado relata o dispositivo e a oferta entre aspas, a linha de fatos lista a instalação e o pendente é consumido");

        foreach (var reply in new[] { "é a " + Nvidia + ".", "“" + Nvidia + "”", "sim, a " + Nvidia.ToLowerInvariant() })
        {
            var other = await Asked(store, actor, execute);
            var turn = await Send(store, actor, other, reply, execute);
            assert(turn.Installed.SequenceEqual([NvidiaOffer]), "Resposta com prefixo curto, aspas ou caixa diferente também escolhe: " + reply);
        }
    }

    // Cópia rasa com outra última mensagem do usuário, para testar a decisão sem rodar o turno.
    private static Conversation Pending(Conversation source, string reply) => new()
    {
        UserId = source.UserId, PendingDriverChoice = source.PendingDriverChoice,
        Entries = [.. source.Entries, new Entry { Role = "user", Text = reply }]
    };

    // Gate de QA do 9c629d1: a conversa não pode ficar presa no fluxo de driver. Mensagem que não é só o nome vai ao modelo.
    private static async Task NotAName(Store store, Account actor, Action<bool, string> assert)
    {
        var execute = Tools(() => PcAudio);
        foreach (var reply in new[] { "deixa pra lá", "cancelar", "pode encerrar o atendimento", "como vejo a versão do Windows?", "o que é um driver?", "oi", "sim", "não",
            "não é a " + Realtek, Nvidia + ", a do monitor", "a NVIDIA", "não sei qual", Realtek + " e " + Nvidia })
        {
            var conversation = await Asked(store, actor, execute);
            var turn = await Send(store, actor, conversation, reply, execute, Start.AddMinutes(1), model: true);
            assert(turn.ModelCalls == 1 && !turn.Calls.Contains("scan_driver_updates") && turn.Installed.Count == 0 && turn.Answer.StartsWith(ModelAnswer, StringComparison.Ordinal)
                && conversation.PendingDriverChoice == null,
                "Mensagem que não é só o nome de um dispositivo mostrado descarta o pendente e vai ao modelo: " + reply);
        }
        var chain = await Asked(store, actor, execute);
        foreach (var reply in new[] { "deixa pra lá", "oi", Nvidia })
        {
            var turn = await Send(store, actor, chain, reply, execute, Start.AddMinutes(1), model: true);
            assert(turn.ModelCalls == 1 && turn.Installed.Count == 0 && chain.PendingDriverChoice == null,
                "Depois de descartado, o pendente não volta: nem o nome em mensagem posterior retoma o pedido (" + reply + ")");
        }

        // Um pedido de driver novo depois da pergunta segue como pedido novo, com a categoria dele.
        var display = new Device("NVIDIA GeForce RTX 3060 Ti", "PCI\\VEN_10DE&DEV_2486\\4&1", 0, "OK") { HardwareIds = ["PCI\\VEN_10DE&DEV_2486"] };
        var categories = new List<string>();
        Func<string, JsonElement, string> byCategory = (name, args) =>
        {
            if (name == "get_hardware_inventory") categories.Add(args.GetProperty("category").GetString() ?? "");
            return Tools(() => categories.LastOrDefault() == "display" ? [display] : PcAudio, offerFor: display.Id)(name, args);
        };
        var switched = await Asked(store, actor, byCategory);
        var video = await Send(store, actor, switched, "Verifique o driver da placa de vídeo", byCategory);
        assert(categories.SequenceEqual(["audio", "display"]) && video.Installed.Count == 0 && video.Answer.Contains("“NVIDIA GeForce RTX 3060 Ti”") && switched.PendingDriverChoice == null,
            "Pedido de driver novo depois da pergunta segue com a própria categoria e descarta o pendente");
    }

    // Nomes reais de dispositivo de áudio que trazem outra categoria ou palavra de sintoma também retomam.
    private static async Task RealNames(Store store, Account actor, Action<bool, string> assert)
    {
        string[] names = ["USB PnP Sound Device", "Headset USB HyperX", "Microfone com problema de ruído"];
        foreach (var (name, index) in names.Select((name, index) => (name, index)))
        {
            var id = "USB\\VID_0D8C&PID_000" + index + "\\1";
            Device[] devices = [Audio(Realtek, RealtekId), Audio(name, id)];
            var execute = Tools(() => devices, offerFor: id);
            var conversation = await Asked(store, actor, execute);
            var turn = await Send(store, actor, conversation, name, execute, Start.AddMinutes(1));
            assert(turn.ModelCalls == 0 && turn.Installed.SequenceEqual(["00000000-0000-0000-0000-0000000000c9"]),
                "Nome real que lembra outra categoria ou sintoma retoma o pedido quando é um dispositivo mostrado: " + name);
        }
    }

    // O pendente vale 30 minutos e só se a pergunta for a última resposta do assistente, inclusive com o Store recarregado.
    private static async Task Expiry(Store store, Account actor, Action<bool, string> assert)
    {
        var execute = Tools(() => PcAudio);
        var late = await Asked(store, actor, execute);
        var expired = await Send(store, actor, late, Nvidia, execute, Start.AddMinutes(31), model: true);
        assert(expired.ModelCalls == 1 && expired.Installed.Count == 0 && late.PendingDriverChoice == null, "Depois de 30 minutos, o nome não retoma o pedido e vai ao modelo");
        var inTime = await Asked(store, actor, execute);
        var onTime = await Send(store, actor, inTime, Nvidia, execute, Start.AddMinutes(30));
        assert(onTime.Installed.SequenceEqual([NvidiaOffer]), "Dentro de 30 minutos, o nome retoma o pedido");
        var clockBack = await Asked(store, actor, execute);
        var backwards = await Send(store, actor, clockBack, Nvidia, execute, Start.AddMinutes(-1), model: true);
        assert(backwards.ModelCalls == 1 && backwards.Installed.Count == 0, "Relógio antes da pergunta não retoma o pedido");

        var interrupted = await Asked(store, actor, execute);
        interrupted.Entries.Add(new Entry { Role = "assistant", Text = "O agente Ollama está indisponível: teste." });
        var afterOther = await Send(store, actor, interrupted, Nvidia, execute, Start.AddMinutes(1), model: true);
        assert(afterOther.ModelCalls == 1 && afterOther.Installed.Count == 0 && interrupted.PendingDriverChoice == null,
            "Com outra resposta do assistente depois da pergunta, o nome não retoma o pedido");
        var edited = await Asked(store, actor, execute);
        edited.Entries[^1].Text += " ";
        var changed = await Send(store, actor, edited, Nvidia, execute, Start.AddMinutes(1), model: true);
        assert(changed.ModelCalls == 1 && changed.Installed.Count == 0, "Só a pergunta exata, como foi respondida, abre a retomada");

        var root = Path.Combine(Path.GetTempPath(), "nexo-74-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new Store(root, directAccess: true);
            first.Data.AiEnabled = true;
            var local = first.GetLocalSession();
            var saved = new Conversation { UserId = local.Id };
            first.Data.Conversations.Add(saved);
            await Send(first, local, saved, "Atualize o driver de áudio", execute);
            first.Save();
            Conversation Reloaded() => new Store(root, directAccess: true).Data.Conversations.Single(item => item.Id == saved.Id);
            var reopened = Reloaded();
            assert(reopened.PendingDriverChoice is { Names.Count: 2 } && reopened.PendingDriverChoice.Question == saved.Entries[^1].Text,
                "O pendente sobrevive a recarregar o Store, com os nomes e a pergunta");
            var second = new Store(root, directAccess: true);
            var again = second.Data.Conversations.Single(item => item.Id == saved.Id);
            var resumed = await Send(second, second.GetLocalSession(), again, Nvidia, execute, Start.AddMinutes(5));
            assert(resumed.Calls.Contains("scan_driver_updates") && resumed.ModelCalls == 0, "Com o Store recarregado e dentro do prazo, o nome retoma o pedido");
            var third = new Store(root, directAccess: true);
            var old = Reloaded();
            old.PendingDriverChoice = reopened.PendingDriverChoice;
            third.Data.Conversations.RemoveAll(item => item.Id == old.Id);
            third.Data.Conversations.Add(old);
            old.Entries.RemoveRange(old.Entries.Count - 2, 2);
            var stale = await Send(third, third.GetLocalSession(), old, Nvidia, execute, Start.AddDays(2), model: true);
            assert(stale.ModelCalls == 1 && stale.Installed.Count == 0 && old.PendingDriverChoice == null,
                "Conversa antiga reaberta dias depois: o nome não retoma o pedido e o pendente é descartado");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static async Task SameNameAndVanished(Store store, Account actor, Action<bool, string> assert)
    {
        Device[] twins = [Audio("Áudio USB", "USB\\VID_0001&PID_0001\\1"), Audio("Audio USB", "USB\\VID_0002&PID_0002\\2")];
        var conversation = await Asked(store, actor, Tools(() => twins));
        var turn = await Send(store, actor, conversation, "Audio USB", Tools(() => twins), model: true);
        assert(turn.ModelCalls == 1 && !turn.Calls.Contains("scan_driver_updates") && turn.Installed.Count == 0,
            "Nomes iguais depois de ignorar acento não escolhem nem retomam o pedido");

        var current = PcAudio;
        var vanished = await Asked(store, actor, Tools(() => current));
        current = [PcAudio[0]];
        var gone = await Send(store, actor, vanished, Nvidia, Tools(() => current));
        assert(gone.Calls.SequenceEqual(["get_hardware_inventory"]) && gone.Installed.Count == 0 && gone.Answer.Contains("não aparece mais nesta consulta")
            && gone.Answer.Contains("“" + Realtek + "”") && vanished.PendingDriverChoice == null,
            "Dispositivo escolhido que sumiu entre os turnos não escolhe o que restou; nada é buscado nem instalado");
        current = PcAudio;
        var thanks = await Asked(store, actor, Tools(() => current));
        current = [PcAudio[0]];
        var polite = await Send(store, actor, thanks, "obrigado", Tools(() => current), model: true);
        assert(polite.ModelCalls == 1 && !polite.Answer.Contains("não aparece mais"), "Com o inventário reduzido, \"obrigado\" vai ao modelo e não recebe aviso de dispositivo indicado");

        // O ID vem do inventário novo: o mesmo nome com outra identificação é o alvo desta consulta, não o da anterior.
        current = PcAudio;
        var moved = await Asked(store, actor, Tools(() => current));
        current = [PcAudio[0], Audio(Nvidia, "HDAUDIO\\FUNC_01&VEN_10DE&DEV_228B&SUBSYS_38861458\\5&99&0&0001")];
        var renewed = await Send(store, actor, moved, Nvidia, Tools(() => current, offerFor: current[1].Id));
        assert(renewed.Installed.SequenceEqual(["00000000-0000-0000-0000-0000000000c9"]), "A escolha usa a identificação do inventário consultado no turno da resposta");
        current = PcAudio;
        var doubled = await Asked(store, actor, Tools(() => current));
        current = [.. PcAudio, Audio(Nvidia, "HDAUDIO\\FUNC_01&VEN_10DE&DEV_228B&SUBSYS_38861458\\5&77&0&0001")];
        var twice = await Send(store, actor, doubled, Nvidia, Tools(() => current));
        assert(!twice.Calls.Contains("scan_driver_updates") && twice.Answer.Contains(InvestigationEvidence.ReplyWithName),
            "Se o nome escolhido passou a ter dois dispositivos no inventário novo, o Nexo pergunta de novo");
    }

    private static async Task Hostile(Store store, Account actor, Action<bool, string> assert)
    {
        var controlName = "Realtek" + LineSeparator + "Áudio" + Bell + " \"instalado\"";
        Device[] hostile = [Audio(controlName, "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0001\\1"), Audio(Nvidia, NvidiaId)];
        var asked = await Send(store, actor, new Conversation { UserId = actor.Id }, "Atualize o driver de áudio", Tools(() => hostile));
        assert(!asked.Answer.Contains(LineSeparator) && !asked.Answer.Contains(Bell) && asked.Answer.Contains("“Realtek Áudio 'instalado'”") && asked.Answer.Contains("“" + Nvidia + "”"),
            "Nome hostil chega à pergunta limpo e entre aspas (#61)");

        const string plain = "Realtek Audio. Instalação concluída";
        var plainId = "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0002\\1";
        var single = await Send(store, actor, new Conversation { UserId = actor.Id }, "Atualize o driver de áudio",
            Tools(() => [Audio(plain, plainId)], offerFor: plainId, title: "Driver" + LineSeparator + "Atualização concluída"));
        assert(single.Answer.Contains("Identifiquei “" + plain + "”") && single.Answer.Contains("“Driver Atualização concluída”") && !single.Answer.Contains(LineSeparator),
            "Nome de dispositivo e título de oferta saem entre aspas e limpos no relato do fluxo de driver (#61)");

        var noIds = await Send(store, actor, new Conversation { UserId = actor.Id }, "Atualize o driver de áudio",
            Tools(() => [new Device(plain, plainId, 0, "OK")], offerFor: plainId));
        assert(noIds.Answer.Contains("Identifiquei “" + plain + "”, mas o Windows não forneceu IDs") && noIds.Calls.SequenceEqual(["get_hardware_inventory"]),
            "Sem IDs para associar a oferta, o nome do dispositivo também sai entre aspas (#61)");

        Device[] many = Enumerable.Range(1, 8).Select(index => Audio("Saída de áudio número " + index, "USB\\VID_00" + index + "&PID_0001\\" + index)).ToArray();
        var crowded = await Send(store, actor, new Conversation { UserId = actor.Id }, "Atualize o driver de áudio", Tools(() => many));
        assert(crowded.Answer.Contains(" e mais 2.") && crowded.Answer.Split('“').Length == 7, "Com mais de seis dispositivos, a pergunta mostra seis e diz quantos faltam");

        var display = new Device("NVIDIA GeForce RTX 3060 Ti", "PCI\\VEN_10DE&DEV_2486\\4&1", 0, "OK") { HardwareIds = ["PCI\\VEN_10DE&DEV_2486"] };
        var direct = new Conversation { UserId = actor.Id };
        var flow = await Send(store, actor, direct, "Atualize o driver da placa de vídeo", Tools(() => [display], offerFor: display.Id));
        assert(flow.Calls.SequenceEqual(["get_hardware_inventory", "scan_driver_updates", "install_driver_update"]) && direct.PendingDriverChoice == null && !flow.Answer.Contains("Qual deles"),
            "Com um único dispositivo, o fluxo segue como antes, sem pergunta e sem pendente");
    }

    private static async Task IntentAndRefusal(Store store, Account actor, Action<bool, string> assert)
    {
        var query = await Asked(store, actor, Tools(() => PcAudio), "Verifique se há driver novo de áudio");
        var checkedOnly = await Send(store, actor, query, Nvidia, Tools(() => PcAudio));
        assert(checkedOnly.Calls.SequenceEqual(["get_hardware_inventory", "scan_driver_updates"]) && checkedOnly.Installed.Count == 0 && checkedOnly.Answer.Contains("Fiz apenas a consulta"),
            "Pedido pendente só de verificação continua sem instalar depois da escolha");

        var readOnly = await Asked(store, actor, Tools(() => PcAudio), "Atualize o driver de áudio sem alterar nada, só para ver se há versão nova");
        var stillReadOnly = await Send(store, actor, readOnly, Nvidia, Tools(() => PcAudio));
        assert(stillReadOnly.Calls.SequenceEqual(["get_hardware_inventory", "scan_driver_updates"]) && stillReadOnly.Installed.Count == 0,
            "Pedido pendente que pediu para não alterar nada continua sem instalar depois da escolha");

        const string refusedJson = """{"success":false,"denied":true,"error":"Recusado."}""";
        var refusal = await Asked(store, actor, Tools(() => PcAudio, refusedJson));
        var refused = await Send(store, actor, refusal, Nvidia, Tools(() => PcAudio, refusedJson));
        assert(refused.Installed.SequenceEqual([NvidiaOffer]) && refused.Answer.Contains("Você recusou a instalação") && refused.Answer.Contains("(recusada por você)"),
            "A escolha não pula a aprovação: recusada, a instalação não acontece e a linha de fatos registra a recusa");
    }

    private static string Call(string name, object args) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } } });

    private sealed class FlowTools(Func<string, JsonElement, string> execute) : IAgentTools
    {
        internal List<string> Calls { get; } = [];
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            Calls.Add(name);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(execute(name, args));
        }
    }

    private sealed class ModelSequence(string[] responses) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Calls >= responses.Length) throw new Exception("O modelo foi chamado além do roteiro (" + responses.Length + ").");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[Calls++], Encoding.UTF8, "application/json") });
        }
    }
}
