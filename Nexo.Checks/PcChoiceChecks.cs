using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// #70, validação no PC (Windows 10, 4 impressoras virtuais sem trabalhos): no turno da resposta, o modelo foi direto para
// clear_print_queue com o nome que o usuário deu, sem consultar de novo, e a trava pedia o nome que o usuário já tinha dado.
// Agora o próprio Nexo consulta o componente e reavalia a mesma chamada, que continua dependendo da aprovação. Os testes
// reproduzem o PC com a mesma Conversation nos dois turnos.
internal static class PcChoiceChecks
{
    private const string Facts = "Alterações feitas pelo Nexo neste turno:";
    private static readonly string[] PcPrinters = ["Fax", "Microsoft Print to PDF", "Microsoft XPS Document Writer", "OneNote"];
    private const string Denied = """{"success":false,"denied":true,"error":"Recusado pelo usuário."}""";

    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        var previousAi = store.Data.AiEnabled;
        store.Data.AiEnabled = true;
        try
        {
            Triage(assert);
            await NameReachesApproval(store, actor, assert);
            await VanishedAndWrongTarget(store, actor, assert);
            await OnlyOnce(store, actor, assert);
            await Device(store, actor, assert);
        }
        finally { store.Data.AiEnabled = previousAi; }
    }

    private static void Triage(Action<bool, string> assert)
    {
        foreach (var said in new[] { "minha impressora nao ta imprimindo", "minha impressora não está imprimindo", "não tá imprimindo", "nao esta imprimindo nada",
            "a impressora parou de imprimir", "não sai a impressão", "nao sai impressao" })
            assert(SupportIntake.InitialTopic(said) == "printer", "Triagem: “" + said + "” abre a consulta de impressoras");
        foreach (var said in new[] { "estou imprimindo um relatório", "a impressão saiu borrada no papel", "ela tá imprimindo normal agora" })
            assert(SupportIntake.InitialTopic(said) == null, "Triagem: “" + said + "” não é falha de impressão");
        // Gate do 21835a3: o verbo sozinho não é tema de impressora. Num incidente de rede, o tema dela liberaria limpar a fila.
        foreach (var (said, topics, initial) in new[]
        {
            ("estou imprimindo um relatório e o Wi-Fi caiu", "network", (string?)null),
            ("ela tá imprimindo normal agora", "", null),
            ("não consigo acessar a internet, estava imprimindo", "network", null),
            ("o pc travou imprimindo", "", "system"),
            ("o Word trava quando estou imprimindo", "", "system"),
        })
            assert(string.Join(",", SupportIntake.ComponentTopics(said).Order()) == topics && SupportIntake.InitialTopic(said) == initial,
                "Triagem: “" + said + "” mantém o tema da base (" + string.Join(",", SupportIntake.ComponentTopics(said).Order()) + " / " + SupportIntake.InitialTopic(said) + ")");
        assert(SupportIntake.ComponentTopics("não tá imprimindo").Contains("printer") && SupportIntake.ComponentTopics("nao sai impressao").Contains("printer")
            && string.Join(",", SupportIntake.ConversationTopics(["Fax", "não tá imprimindo"])) == "printer",
            "Triagem: a falha negada (“não tá imprimindo”) é tema de impressora e é herdada pela resposta com o nome");
    }

    private static string Printers(IEnumerable<string> names) => WindowsTools.Ok(JsonSerializer.Serialize(names.Select(name => new { Name = name, Jobs = 0, Status = "Normal" }).ToArray()));

    private static async Task NameReachesApproval(Store store, Account actor, Action<bool, string> assert)
    {
        foreach (var name in new[] { "Fax", "Microsoft Print to PDF" })
        {
            string Execute(string tool, JsonElement args) => tool switch
            {
                "get_printers" => Printers(PcPrinters),
                "clear_print_queue" when args.GetProperty("printer_name").GetString() == name => Denied,
                _ => throw new Exception("Alvo não escolhido chegou ao executor: " + tool + " " + args.GetRawText())
            };
            var conversation = New(actor);
            var first = await Turn(store, actor, conversation, "minha impressora não imprime", Execute);
            assert(first.Answer.StartsWith("Encontrei mais de uma impressora: “Fax”;", StringComparison.Ordinal) && !first.Answer.Contains("indício de falha"),
                "Pergunta sem indício de falha não diz que há indício (" + first.Answer.Split('\n')[0] + ")");
            var second = await Turn(store, actor, conversation, name, Execute, Call("clear_print_queue", new { printer_name = name }));
            assert(second.Tools.Calls.SequenceEqual(["get_printers", "clear_print_queue"]) && second.Answer.Contains("Você recusou a alteração proposta")
                && second.Answer.EndsWith(Facts + " nenhuma. Não executadas: limpeza da fila de “" + name + "” (recusada por você).", StringComparison.Ordinal),
                "Resposta só com o nome (“" + name + "”) e alteração sem consulta: o Nexo consulta, a chamada chega ao pedido de aprovação e a recusa fica na linha (" + second.Answer + ")");
        }
    }

    private static async Task VanishedAndWrongTarget(Store store, Account actor, Action<bool, string> assert)
    {
        // A Fax some entre a pergunta e a resposta: a explicação diz isso, sem pedir de novo o nome que o usuário já deu.
        var queries = 0;
        string Vanishing(string tool, JsonElement args) => tool == "get_printers"
            ? Printers(++queries == 1 ? PcPrinters : PcPrinters.Where(name => name != "Fax"))
            : throw new Exception("Alteração chegou ao executor: " + tool);
        var conversation = New(actor);
        await Turn(store, actor, conversation, "minha impressora não imprime", Vanishing);
        var gone = await Turn(store, actor, conversation, "Fax", Vanishing, Call("clear_print_queue", new { printer_name = "Fax" }), Finish("A fila da Fax foi limpa."));
        assert(gone.Tools.Calls.SequenceEqual(["get_printers"]) && gone.Answer.Contains("porque “Fax” não aparece na consulta de impressoras deste turno")
            && !gone.Answer.Contains(InvestigationEvidence.ReplyWithName) && !gone.Answer.Contains("só com o nome") && !gone.Answer.Contains("foi limpa")
            && gone.Answer.EndsWith(Facts + " nenhuma. Não executadas: limpeza da fila de “Fax” (barrada pelo Nexo).", StringComparison.Ordinal),
            "Nome que sumiu na consulta automática: explicação com o motivo real, sem ciclo de “responda só com o nome” (" + gone.Answer + ")");

        // O usuário nomeou a Fax e o modelo pediu a OneNote: a explicação diz a diferença.
        string Execute(string tool, JsonElement args) => tool == "get_printers" ? Printers(PcPrinters) : throw new Exception("Alteração chegou ao executor: " + tool);
        var other = New(actor);
        await Turn(store, actor, other, "minha impressora não imprime", Execute);
        var wrong = await Turn(store, actor, other, "Fax", Execute, Call("clear_print_queue", new { printer_name = "OneNote" }), Finish("Pronto."));
        assert(wrong.Tools.Calls.SequenceEqual(["get_printers"]) && wrong.Answer.Contains("porque você indicou “Fax”, mas a alteração pedida era para “OneNote”")
            && !wrong.Answer.Contains("só com o nome") && wrong.Answer.EndsWith("limpeza da fila de “OneNote” (barrada pelo Nexo).", StringComparison.Ordinal),
            "Alvo diferente do nome dado: a explicação diz qual foi indicado e qual foi pedido (" + wrong.Answer + ")");
    }

    private static async Task OnlyOnce(Store store, Account actor, Action<bool, string> assert)
    {
        // Gate do 21835a3: uma consulta automática por consulta no turno, não por alvo. Com dez impressoras inventadas pelo
        // modelo, o get_printers roda uma vez só (ele já mostra todas as filas).
        string Listing(string tool, JsonElement args) => tool == "get_printers" ? Printers(PcPrinters) : throw new Exception("Alteração chegou ao executor: " + tool);
        var invented = New(actor);
        await Turn(store, actor, invented, "minha impressora não imprime", Listing);
        var names = Enumerable.Range(1, 10).Select(index => "Impressora inventada " + index).ToArray();
        var many = await Turn(store, actor, invented, "Fax", Listing, [.. names.Select(name => Call("clear_print_queue", new { printer_name = name })), Finish("Pronto.")]);
        assert(many.Tools.Calls.SequenceEqual(["get_printers"]), "Dez alvos inventados: uma consulta automática só no turno (" + string.Join(",", many.Tools.Calls) + ")");
        // No limite de 24 ações do turno, a consulta automática não roda: ela também conta no limite.
        var reads = new List<object>();
        foreach (var service in new[] { "Audiosrv", "Spooler", "bthserv", "WlanSvc" }) reads.Add(new { function = new { name = "get_recent_errors", arguments = (object)new { service } } });
        reads.Add(new { function = new { name = "get_recent_errors", arguments = (object)new { } } });
        foreach (var topic in new[] { "camera", "audio", "bluetooth", "system" }) reads.Add(new { function = new { name = "diagnose_device", arguments = (object)new { topic } } });
        foreach (var category in new[] { "problem", "display", "network", "storage", "usb", "camera", "audio", "bluetooth", "printer" }) reads.Add(new { function = new { name = "get_hardware_inventory", arguments = (object)new { category } } });
        foreach (var tool in new[] { "get_services", "get_processes", "get_health_report", "get_network", "get_boot_status", "get_driver_store" })
            reads.Add(new { function = new { name = tool, arguments = (object)(tool == "get_driver_store" ? new { device_id = "USB\\X" } : new { }) } });
        var batch = JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = reads.Take(24).ToArray() } });
        string Anything(string tool, JsonElement args) => tool == "get_printers" ? Printers(PcPrinters) : ToolCatalog.IsMutation(tool) ? throw new Exception("Alteração chegou ao executor: " + tool) : WindowsTools.Ok(new { });
        var full = New(actor);
        await Turn(store, actor, full, "minha impressora não imprime", Anything);
        var limited = await Turn(store, actor, full, "Fax", Anything, batch, Call("clear_print_queue", new { printer_name = "Fax" }), Finish("Pronto."));
        assert(limited.Tools.Calls.Count == 24 && !limited.Tools.Calls.Contains("get_printers") && !limited.Tools.Calls.Contains("clear_print_queue"),
            "No limite de 24 ações, a consulta automática não roda e a alteração não chega ao executor (" + limited.Tools.Calls.Count + ": " + string.Join(",", limited.Tools.Calls.Distinct()) + ")");
        var queries = 0;
        string Execute(string tool, JsonElement args) => tool == "get_printers"
            ? Printers(++queries == 1 ? PcPrinters : PcPrinters.Where(name => name != "Fax"))
            : throw new Exception("Alteração chegou ao executor: " + tool);
        var conversation = New(actor);
        await Turn(store, actor, conversation, "minha impressora não imprime", Execute);
        var repeated = await Turn(store, actor, conversation, "Fax", Execute, Call("clear_print_queue", new { printer_name = "Fax" }),
            Call("clear_print_queue", new { printer_name = "Fax" }), Call("clear_print_queue", new { printer_name = "Fax" }));
        assert(repeated.Tools.Calls.SequenceEqual(["get_printers"]) && repeated.Answer.Split('\n').Count(line => line.StartsWith(Facts, StringComparison.Ordinal)) == 1,
            "A consulta automática acontece uma vez por alvo no turno; a alteração repetida encerra pela trava (" + string.Join(",", repeated.Tools.Calls) + ")");
    }

    private static async Task Device(Store store, Account actor, Action<bool, string> assert)
    {
        const string fifine = "USB\\VID_0C76&PID_161F\\FIFINE", headset = "USB\\VID_046D&PID_0A44\\HEADSET";
        string Execute(string tool, JsonElement args) => tool switch
        {
            "diagnose_device" => WindowsTools.Ok(new { count = 2, devices = new[] { new Nexo.Support.Device("Fifine Microphone", fifine, 22, "Error"), new Nexo.Support.Device("Headset USB", headset, 22, "Error") } }),
            "enable_device" when args.GetProperty("device_id").GetString() == fifine => Denied,
            _ => throw new Exception("Alvo não escolhido chegou ao executor: " + tool)
        };
        var conversation = New(actor);
        var first = await Turn(store, actor, conversation, "meu microfone parou de funcionar", Execute);
        assert(first.Answer.Contains("“Fifine Microphone”") && first.Answer.Contains(InvestigationEvidence.ReplyWithName), "Dispositivo: a pergunta lista os dois desabilitados");
        var second = await Turn(store, actor, conversation, "Fifine Microphone", Execute, Call("enable_device", new { device_id = fifine }));
        assert(second.Tools.Calls.SequenceEqual(["diagnose_device", "enable_device"]) && second.Answer.Contains("Você recusou a alteração proposta")
            && second.Answer.EndsWith(Facts + " nenhuma. Não executadas: habilitação do dispositivo “Fifine Microphone” (recusada por você).", StringComparison.Ordinal),
            "Dispositivo: resposta só com o nome e alteração sem consulta chegam ao pedido de aprovação depois da consulta automática (" + second.Answer + ")");
        var wrong = New(actor);
        await Turn(store, actor, wrong, "meu microfone parou de funcionar", Execute);
        var other = await Turn(store, actor, wrong, "Fifine Microphone", Execute, Call("enable_device", new { device_id = headset }), Finish("Pronto."));
        assert(other.Tools.Calls.SequenceEqual(["diagnose_device"]) && other.Answer.Contains("porque você indicou “Fifine Microphone”, mas a alteração pedida era para “Headset USB”")
            && !other.Answer.Contains("só com o nome"), "Dispositivo: alvo diferente do nome dado é barrado com a explicação certa (" + other.Answer + ")");
        // Fora do componente relatado, o Nexo não consulta nada antes da trava: a limpeza de fila num atendimento de microfone
        // é barrada pelo tópico, sem consulta de impressoras.
        var offTopic = New(actor);
        await Turn(store, actor, offTopic, "meu microfone parou de funcionar", Execute);
        var printer = await Turn(store, actor, offTopic, "Fifine Microphone", Execute, Call("clear_print_queue", new { printer_name = "Fax" }), Finish("Pronto."));
        assert(printer.Tools.Calls.Count == 0 && printer.Answer.Contains("o seu pedido não cita"),
            "Consulta automática só no componente do incidente: fora dele, a trava de tópico barra sem consultar (" + string.Join(",", printer.Tools.Calls) + ")");
    }

    private static Conversation New(Account actor) => new() { UserId = actor.Id, Entries = [] };

    private static async Task<(FlowTools Tools, string Answer)> Turn(Store store, Account actor, Conversation conversation, string text, Func<string, JsonElement, string> execute, params string[] responses)
    {
        conversation.Entries.Add(new Entry { Role = "user", Text = text });
        var tools = new FlowTools(execute);
        using var model = new ModelSequence(responses);
        using var http = new HttpClient(model);
        var answer = await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
        if (model.Calls != responses.Length) throw new Exception($"FAIL: o roteiro do modelo não foi consumido ({model.Calls} de {responses.Length}) em: {text}");
        conversation.Entries.Add(new Entry { Role = "assistant", Text = answer });
        return (tools, answer);
    }

    private static string Call(string name, object args) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } } });
    private static string Finish(string text) => Call("finish_support", new { message = text });

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
