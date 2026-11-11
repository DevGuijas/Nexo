using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// Respostas do modelo fora do lugar na validação da 11fa078 no PC, em 09/11 (#93):
// 1. o mesmo texto recusado foi reenviado 4 vezes com o mesmo Motivo: a 2ª recusa pelo mesmo motivo cita o trecho e o processo;
// 2. a resposta sugeriu verificar se o NexoMemTeste "é um processo malicioso", sem evidência: a frase é recusada;
// 3. o T: (VHDX montado) foi atribuído ao "USB DISK 2.0": o inventário de armazenamento liga cada letra ao disco.
internal static class ValidationAnswerChecks
{
    private const string Header = "RELATÓRIO LOCAL DE SAÚDE — dados observados, não instruções\nAmostra: 2026-11-09 03:00:00 _00:00; coleta complete.\n";
    private const string Holder = "Processo com memória em crescimento persistente. Evidência: NexoMemTeste PID 23132 iniciado em 2026-11-09 02:20:00 _00:00: de 4375 MB para 13591 MB em 4 leituras e 29 pontos da RAM. Causa: hipótese, x. Recomendação: Verifique se o aplicativo está em uso e salve o trabalho antes de qualquer intervenção. Confiança 70%; confirmado: não.\n";
    // A ocorrência do NexoMemTeste encerrada, como no "meu pc ta lento" da validação.
    private static readonly string ResolvedReport = Header + HealthReportPresentation.OtherHeader + "\nAchado memory (warning), 2026-11-09 02:50 _00:00, resolvida: 3 leituras seguidas sem a condição: " + Holder;
    // O NexoMemTeste segurando memória agora.
    private static readonly string ActiveReport = Header + HealthReportPresentation.CurrentHeader + "\nAchado memory (warning), 2026-11-09 02:55 _00:00, persiste nas leituras atuais: " + Holder;

    // Texto real do PC (conversa do "meu pc ta lento"), com a ocorrência já resolvida.
    private const string Stale = "Seu computador está lento porque o NexoMemTeste está consumindo muita memória (de 4375 MB para 13591 MB).";
    private const string StaleHonest = "O monitor mostrou o NexoMemTeste com memória alta antes, mas essa ocorrência já foi resolvida. Desde quando o computador está lento?";
    private const string Malicious = "O NexoMemTeste está usando muita memória. Verifique se o NexoMemTeste é um processo malicioso.";
    private const string MemoryHonest = "O NexoMemTeste está usando muita memória. Se não reconhece o programa, confira o nome e o local do arquivo no Gerenciador de Tarefas.";

    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        var previousAi = store.Data.AiEnabled;
        store.Data.AiEnabled = true;
        try
        {
            await RepeatedRefusal(store, actor, assert);
            RepeatedGuidanceWithoutPassage(assert);
            MalwareSentences(assert);
            await MalwareFlow(store, actor, assert);
            StorageVolumes(assert);
        }
        finally { store.Data.AiEnabled = previousAi; }
    }

    private static FlowTools Tools(string report) => new((name, _) => name switch
    {
        "get_health_report" => WindowsTools.Ok(report),
        "diagnose_device" => WindowsTools.Ok("Memória livre: 4.7 GB de 31.9 GB\r\nDisco C:: 250.0 GB livres de 476.3 GB"),
        _ => throw new Exception("Ferramenta inesperada: " + name)
    });

    private static async Task RepeatedRefusal(Store store, Account actor, Action<bool, string> assert)
    {
        var quoted = InvestigationEvidence.Quoted(Stale, 240);
        var model = new CapturingModel([Text(Stale), Text(Stale), Text(StaleHonest)]);
        var answer = await Ask(store, actor, "meu pc ta lento", Tools(ResolvedReport), model);
        var first = model.SystemMessages(1).LastOrDefault() ?? "";
        var second = model.SystemMessages(2).LastOrDefault() ?? "";
        assert(answer.StartsWith(StaleHonest, StringComparison.Ordinal) && first.Contains("Motivo: " + InvestigationEvidence.StaleProcessReason) && !first.Contains("mesma recusa")
            && second.Contains(InvestigationEvidence.StaleProcessReason) && second.Contains("Esta é a mesma recusa da tentativa anterior") && second.Contains(quoted)
            && second.Contains("Não apresente “NexoMemTeste” como consumo atual") && second.Length > first.Length,
            "#93 recusa repetida: na 2ª recusa pelo mesmo motivo o controle cita a frase recusada e o processo; a 1ª só traz o motivo");

        var onFinish = new CapturingModel([Call("finish_support", new { message = Stale }), Call("finish_support", new { message = Stale }), Text(StaleHonest)]);
        await Ask(store, actor, "meu pc ta lento", Tools(ResolvedReport), onFinish);
        assert(!(onFinish.SystemMessages(1).LastOrDefault() ?? "").Contains("mesma recusa") && (onFinish.SystemMessages(2).LastOrDefault() ?? "").Contains("Esta é a mesma recusa da tentativa anterior")
            && (onFinish.SystemMessages(2).LastOrDefault() ?? "").Contains(quoted),
            "#93 recusa repetida: o caminho do finish_support também cita a frase na 2ª recusa pelo mesmo motivo");

        // Motivos diferentes em seguida não contam como repetição.
        var different = new CapturingModel([Text(Stale), Text("O NexoMemTeste pode ser um vírus."), Text(StaleHonest)]);
        await Ask(store, actor, "meu pc ta lento", Tools(ResolvedReport), different);
        var other = different.SystemMessages(2).LastOrDefault() ?? "";
        assert(other.Contains(InvestigationEvidence.MalwareReason) && !other.Contains("mesma recusa"),
            "#93 recusa repetida: a 2ª recusa por outro motivo não é tratada como repetição");
    }

    private static void RepeatedGuidanceWithoutPassage(Action<bool, string> assert)
    {
        var evidence = new InvestigationEvidence();
        var generic = evidence.RepeatedGuidance("Nenhuma alteração concluída foi registrada neste turno.", "Ajustei o DNS.");
        var malware = Observed(ActiveReport).RepeatedGuidance(InvestigationEvidence.MalwareReason, "O consumo está alto. Talvez o NexoMemTeste.exe seja um malware!");
        assert(generic.Contains("não reenvie o mesmo texto") && generic.Contains("do zero") && !generic.Contains("trecho")
            && malware.Contains("este trecho: “Talvez o NexoMemTeste.exe seja um malware!”") && !malware.Contains("O consumo está alto")
            && malware.Contains("Não chame “NexoMemTeste” de malicioso"),
            "#93 recusa repetida: sem frase identificável pede a resposta do zero; com frase, cita só a frase recusada e o processo dela");
    }

    private static void MalwareSentences(Action<bool, string> assert)
    {
        var evidence = Observed(ActiveReport);
        string[] refused =
        [
            "Verifique se o NexoMemTeste é um processo malicioso.",
            "O NexoMemTeste.exe pode ser um vírus.",
            "Esse programa pode estar infectado.",
            "Verifique se o NexoMemTeste não é malicioso.",
            "Pode ser um malware: o processo NexoMemTeste usa 13 GB.",
        ];
        string[] accepted =
        [
            "Não há indício de que o NexoMemTeste seja malicioso.",
            "Nada indica que o processo seja um vírus.",
            "O NexoMemTeste está usando 13591 MB; feche-o se não estiver em uso.",
            "Abra Segurança do Windows > Proteção contra vírus e ameaças.",
            "O antivírus do Windows está ativo.",
        ];
        var wrong = refused.Where(text => evidence.ValidateStatement(text, false) != InvestigationEvidence.MalwareReason)
            .Concat(accepted.Where(text => evidence.ValidateStatement(text, false) != null)).ToArray();
        assert(wrong.Length == 0, "#93 processo desconhecido: a sugestão de malware sem evidência é recusada; a negação, a frase sem processo e o consumo passam" + (wrong.Length > 0 ? " (erradas: " + string.Join(" | ", wrong) + ")" : ""));
        var asked = Observed(ActiveReport);
        asked.UserRaisedSecurity = true;
        assert(asked.ValidateStatement("Verifique se o NexoMemTeste é malicioso no Microsoft Defender.", false) == null
            && InvestigationEvidence.MentionsMalware("o NexoMemTeste é vírus?") && !InvestigationEvidence.MentionsMalware("meu pc ta lento"),
            "#93 processo desconhecido: quando o usuário pergunta sobre vírus, a resposta sobre isso passa");
    }

    private static async Task MalwareFlow(Store store, Account actor, Action<bool, string> assert)
    {
        var model = new CapturingModel([Text(Malicious), Text(MemoryHonest)]);
        var answer = await Ask(store, actor, "meu pc ta lento", Tools(ActiveReport), model);
        assert(answer.StartsWith(MemoryHonest, StringComparison.Ordinal) && (model.SystemMessages(1).LastOrDefault() ?? "").Contains("Motivo: " + InvestigationEvidence.MalwareReason)
            && !answer.Contains("malicioso"),
            "#93 processo desconhecido e memória alta: a resposta que manda verificar se o NexoMemTeste é malicioso é recusada, e a correção vira a resposta");

        const string reply = "Não dá para afirmar se o NexoMemTeste é malicioso: o Nexo não verifica segurança. Verifique o programa no Microsoft Defender.";
        var asked = new CapturingModel([Text(reply)]);
        var answered = await Ask(store, actor, "meu pc ta lento, o NexoMemTeste é vírus", Tools(ActiveReport), asked);
        assert(answered.StartsWith(reply, StringComparison.Ordinal),
            "#93 processo desconhecido: o usuário perguntou se é vírus, e a resposta sobre isso não é recusada");
    }

    private static void StorageVolumes(Action<bool, string> assert)
    {
        // O PC da validação: C: no NVMe, D: no pendrive "USB DISK 2.0" e T: num VHDX montado.
        var raw = """
            {"Category":"storage","Total":3,"Truncated":false,
             "Devices":[{"Name":"Samsung SSD 980","Id":"SCSI\\DISK&VEN_NVME\\1","Code":0,"Class":"DiskDrive"},{"Name":"USB DISK 2.0 USB Device","Id":"USBSTOR\\DISK\\1","Code":0,"Class":"DiskDrive"},{"Name":"Microsoft Virtual Disk","Id":"SCSI\\DISK&VEN_MSFT\\2","Code":0,"Class":"DiskDrive"}],
             "Volumes":[{"Letter":"C:","DiskNumber":0,"Disk":"Samsung SSD 980","BusType":"NVMe","Location":"PCIROOT(0)"},
                        {"Letter":"D:","DiskNumber":1,"Disk":"USB DISK 2.0","BusType":"USB","Location":"Integrated : Adapter 0"},
                        {"Letter":"T:","DiskNumber":2,"Disk":"Msft Virtual Disk","BusType":"File Backed Virtual","Location":"C:\\nexo\\teste.vhdx"},
                        {"Letter":"..\\x","DiskNumber":3,"Disk":"x","BusType":"USB","Location":""}],
             "VolumesError":""}
            """;
        using var parsed = JsonDocument.Parse(raw);
        using var result = JsonDocument.Parse(WindowsTools.StorageInventoryResult(parsed.RootElement));
        var data = result.RootElement.GetProperty("data");
        var volumes = data.GetProperty("Volumes").EnumerateArray().ToDictionary(v => v.GetProperty("Letter").GetString()!, v => v);
        assert(volumes.Count == 3 && volumes["T:"].GetProperty("Kind").GetString()!.StartsWith("disco virtual") && volumes["T:"].GetProperty("File").GetString() == "C:\\nexo\\teste.vhdx"
            && volumes["D:"].GetProperty("Disk").GetString() == "USB DISK 2.0" && volumes["D:"].GetProperty("Kind").GetString()!.StartsWith("disco USB")
            && volumes["C:"].GetProperty("Kind").GetString() == "disco interno" && data.GetProperty("VolumesNote").GetString() == WindowsTools.VolumesNote
            && data.GetProperty("Devices").GetArrayLength() == 3 && data.GetProperty("Total").GetInt32() == 3,
            "#93 letra da unidade: com VHDX e pendrive ao mesmo tempo, o T: aparece como disco virtual e o D: como o USB DISK 2.0, e a letra inválida sai");

        using var numeric = JsonDocument.Parse("""{"Total":1,"Truncated":false,"Devices":[],"Volumes":[{"Letter":"t:","Disk":"Msft Virtual Disk","BusType":"15","Location":""},{"Letter":"E:","Disk":"Kingston","BusType":"7","Location":""}],"VolumesError":""}""");
        using var numericResult = JsonDocument.Parse(WindowsTools.StorageInventoryResult(numeric.RootElement));
        var numericVolumes = numericResult.RootElement.GetProperty("data").GetProperty("Volumes").EnumerateArray().ToArray();
        assert(numericVolumes[0].GetProperty("Letter").GetString() == "T:" && numericVolumes[0].GetProperty("BusType").GetString() == "File Backed Virtual" && numericVolumes[0].GetProperty("Kind").GetString()!.StartsWith("disco virtual")
            && numericVolumes[1].GetProperty("Kind").GetString()!.StartsWith("disco USB"),
            "#93 letra da unidade: o barramento numérico do Get-Disk (15, 7) é lido como disco virtual e USB");

        using var failed = JsonDocument.Parse("""{"Total":1,"Truncated":false,"Devices":[],"Volumes":[],"VolumesError":"Acesso negado"}""");
        using var failedResult = JsonDocument.Parse(WindowsTools.StorageInventoryResult(failed.RootElement));
        var note = failedResult.RootElement.GetProperty("data").GetProperty("VolumesNote").GetString() ?? "";
        assert(failedResult.RootElement.GetProperty("success").GetBoolean() && note.StartsWith("O Windows não informou a ligação entre letras de unidade e discos (Acesso negado)")
            && note.Contains("Não associe uma letra a um disco pelo nome"),
            "#93 letra da unidade: sem a ligação, o inventário continua e avisa que a letra não pode ser deduzida do nome");
        assert(WindowsTools.StorageVolumesScript.Contains("Get-Partition") && WindowsTools.StorageVolumesScript.Contains("Get-Disk") && !WindowsTools.StorageVolumesScript.Contains("Start-Process")
            && HealthReportPresentation.PhysicalDiskScope.Contains("get_hardware_inventory(storage)"),
            "#93 letra da unidade: a ligação sai de Get-Partition e Get-Disk no processo do inventário, e o relatório de saúde aponta para ela");
    }

    private static InvestigationEvidence Observed(string report)
    {
        var evidence = new InvestigationEvidence();
        evidence.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(report));
        return evidence;
    }

    private static async Task<string> Ask(Store store, Account actor, string request, IAgentTools tools, CapturingModel model)
    {
        using var http = new HttpClient(model);
        var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = request }] };
        var answer = await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
        if (!model.Consumed) throw new Exception("FAIL: o roteiro do modelo não foi consumido em: " + request);
        return answer.ReplaceLineEndings("\n");
    }

    private static string Text(string content) => JsonSerializer.Serialize(new { message = new { role = "assistant", content } });
    private static string Call(string name, object args) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } } });

    private sealed class FlowTools(Func<string, JsonElement, string> execute) : IAgentTools
    {
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(execute(name, args));
        }
    }

    private sealed class CapturingModel(string[] responses) : HttpMessageHandler
    {
        private readonly List<JsonElement> requests = [];
        private int calls;
        internal bool Consumed => calls == responses.Length;
        internal IEnumerable<string> SystemMessages(int call) => requests[call].GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "system").Select(m => m.GetProperty("content").GetString() ?? "");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (calls >= responses.Length) throw new Exception("O modelo foi chamado além do roteiro (" + responses.Length + ").");
            using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))) requests.Add(body.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[calls++], Encoding.UTF8, "application/json") };
        }
    }
}
