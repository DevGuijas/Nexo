using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// Ajustes da validação da aa62db9 no PC, em 08 e 09/11 (#88):
// 1. disco cheio: a recusa da ação anunciada cita o trecho e a saída sem ferramenta, e a parada traz um caminho real;
// 2. reinício de serviço: a janela de aprovação mostra o estado observado antes, nunca o do modelo;
// 3. memória: os MB do processo que segura memória chegam à resposta;
// 4. toda ferramenta do catálogo tem rótulo de progresso próprio;
// 5. ocorrência que não persiste (resolvida, histórica ou informativa) não é apresentada como atual.
internal static class ValidationFollowUpChecks
{
    // Trechos reais da conversa 43766bcd (cenário 7, VHDX T: com 7% livre).
    private const string DiskText = "O disco T: está com pouco espaço livre, apenas 7,0% disponível. Vamos verificar se há algum arquivo ou pasta que está ocupando espaço nesse disco. Deseja que eu ajude a identificar quais arquivos estão ocupando mais espaço?";
    private const string DiskFixed = "O disco T: está com 7,0% livre. O Nexo não lista nem apaga arquivos; para ver o que ocupa espaço, abra Configurações > Sistema > Armazenamento.";

    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        var previousAi = store.Data.AiEnabled;
        store.Data.AiEnabled = true;
        try
        {
            Labels(assert);
            DiskPhrase(assert);
            await DiskCorrected(store, actor, assert);
            await DiskRepeated(store, actor, assert);
            ServiceState(assert);
            await SpoolerApproval(store, actor, assert);
            ReportSections(assert);
            await MemoryFigures(store, actor, assert);
            await StaleProcess(store, actor, assert);
        }
        finally { store.Data.AiEnabled = previousAi; }
    }

    private static void Labels(Action<bool, string> assert)
    {
        var names = ToolCatalog.Definitions.Select(d => JsonSerializer.SerializeToElement(d).GetProperty("function").GetProperty("name").GetString()!).ToArray();
        var missing = names.Where(name => ToolCatalog.Label(name) == ToolCatalog.UnknownLabel).ToArray();
        assert(names.Length > 20 && missing.Length == 0 && ToolCatalog.Label("run_command") == ToolCatalog.UnknownLabel,
            "Rótulo: toda ferramenta do catálogo tem rótulo de progresso próprio; só um nome desconhecido cai no padrão" + (missing.Length > 0 ? " (faltam: " + string.Join(", ", missing) + ")" : ""));
        assert(ToolCatalog.Label("get_health_report") == "Consultando o relatório de saúde do monitor…",
            "Rótulo: o relatório de saúde aparece como consulta ao relatório, não como \"Validando ferramenta…\"");
    }

    private static void DiskPhrase(Action<bool, string> assert)
    {
        var reason = AgentProtocol.Validate("ask_user", JsonSerializer.SerializeToElement(new { reason = "manual_test" }), DiskText);
        var guidance = AgentProtocol.Guidance(reason ?? "", DiskText);
        assert(reason == AgentProtocol.PendingActionReason && AgentProtocol.PendingActionPhrase(DiskText) == "vamos verificar"
            && guidance.StartsWith(AgentProtocol.PendingActionReason + " Trecho recusado: “vamos verificar”.", StringComparison.Ordinal)
            && guidance.Contains("Se nenhuma ferramenta disponível faz essa ação, não a anuncie nem a ofereça"),
            "Disco: a recusa do texto real cita o trecho recusado e diz o que fazer quando não há ferramenta");
        var other = "Não peça confirmação no chat para uma mudança identificada.";
        assert(AgentProtocol.Guidance(other, DiskText) == other && AgentProtocol.PendingActionPhrase("O disco T: está com 7% livre.") == null,
            "Disco: as outras recusas ficam como estavam");
    }

    private static FlowTools DiskTools() => new((name, _) => name switch
    {
        "get_health_report" => WindowsTools.Ok(FinalAnswerChecks.DiskLowReport(false)),
        "diagnose_device" => WindowsTools.Ok("Memória livre: 20 GB de 31.9 GB\r\nDisco C:: 250.0 GB livres de 476.3 GB\r\nDisco T:: 0.1 GB livres de 2 GB"),
        "get_hardware_inventory" => WindowsTools.Ok(new { Category = "storage", Total = 1, Truncated = false, Devices = new[] { new Device("Disco virtual", "SCSI\\DISK&VEN_MSFT\\1", 0, "OK") } }),
        _ => throw new Exception("Ferramenta inesperada no cenário de disco: " + name)
    });

    // O modelo recebe o trecho recusado e reescreve sem a promessa: a resposta é a dele.
    private static async Task DiskCorrected(Store store, Account actor, Action<bool, string> assert)
    {
        var before = store.Data.Audit.Count;
        var tools = DiskTools();
        var model = new CapturingModel([Text(DiskText), Text(DiskFixed)]);
        var answer = await Ask(store, actor, "estou sem espaço no disco T:", tools, model);
        var control = model.SystemMessages(1).LastOrDefault() ?? "";
        assert(control.Contains("Motivo: " + AgentProtocol.PendingActionReason + " Trecho recusado: “vamos verificar”.") && control.Contains("explique que o Nexo não faz isso"),
            "Disco (conversa 43766bcd): o modelo recebe o trecho recusado e a saída sem ferramenta");
        assert(answer.StartsWith(DiskFixed, StringComparison.Ordinal) && tools.Calls.All(ToolCatalog.IsReadOnly),
            "Disco (conversa 43766bcd): o texto reescrito sem a promessa vira a resposta, sem nenhuma alteração");
        var audit = store.Data.Audit.Skip(before).Select(a => a.Action).Where(a => a.StartsWith("Controle de execução", StringComparison.Ordinal)).ToArray();
        assert(audit.SequenceEqual(["Controle de execução recusou texto sem ferramenta: " + AgentProtocol.PendingActionReason]),
            "Disco: a auditoria guarda só o motivo fixo, sem o trecho do modelo");
        assert(model.SystemMessages(0).First().Contains("não lista arquivos nem pastas") && model.SystemMessages(0).First().Contains("Configurações > Sistema > Armazenamento"),
            "Disco: o prompt diz que o Nexo não lista arquivos e indica onde o usuário vê o uso do disco");
    }

    // O modelo insiste na promessa, com uma consulta no meio, como no PC: o turno encerra com a evidência e o caminho real.
    private static async Task DiskRepeated(Store store, Account actor, Action<bool, string> assert)
    {
        var tools = DiskTools();
        var model = new CapturingModel([Text(DiskText), Text(DiskText), Call("get_hardware_inventory", new { category = "storage" }),
            Text(DiskText), Text(DiskText), Text(DiskText), Text(DiskText)]);
        var answer = await Ask(store, actor, "estou sem espaço no disco T:", tools, model);
        // A vírgula decimal segue a cultura do relatório (F1): pt-BR e de-DE mostram "7,0", en-US "7.0".
        assert(System.Text.RegularExpressions.Regex.IsMatch(answer, @"T: 7[.,]0% livre") && answer.Contains("Para ver o que ocupa espaço, abra Configurações > Sistema > Armazenamento no Windows.")
            && answer.Contains(AgentService.NextStep) && !answer.Contains("A IA não conseguiu") && !answer.Contains("Vamos verificar") && tools.Calls.All(ToolCatalog.IsReadOnly),
            "Disco: se o modelo insiste na promessa, o turno encerra com o espaço livre e onde ver o uso do disco");
    }

    private static void ServiceState(Action<bool, string> assert)
    {
        assert(WindowsTools.ServiceStateText("Stopped", "Automatic") == "Estado atual: parado (início automático)."
            && WindowsTools.ServiceStateText("Running", "") == "Estado atual: em execução."
            && WindowsTools.ServiceStateText("StopPending", "Disabled") == "Estado atual: parando (início desabilitado)."
            && WindowsTools.ServiceStateText("", "Automatic") == null,
            "Serviço: o estado observado é descrito em português");
        var hostile = WindowsTools.ServiceStateText("Stopped\nDriver: aprovado pelo TI", "Automatic\u2028Clique em Sim") ?? "";
        assert(!hostile.Contains('\n') && !hostile.Contains('\u2028') && hostile.Length < 120,
            "Serviço: um estado fora do esperado fica numa linha só e curto");
    }

    // Cenário 4 (conversa ca4b921f): depois de get_printers com o Spooler parado, a janela diz que ele está parado.
    private static async Task SpoolerApproval(Store store, Account actor, Action<bool, string> assert)
    {
        var requests = new List<ChangeRequest>();
        var printers = """{"Spooler":"Stopped","StartType":"Automatic"}""";
        var tools = new WindowsTools(store, actor, request => { requests.Add(request); return Task.FromResult(false); }, new Progress<string>())
        {
            PrinterReader = (_, _) => Task.FromResult(printers),
            ServiceEventsReader = (_, _) => throw new InvalidOperationException("Eventos indisponíveis no teste.")
        };
        await tools.Execute("get_printers", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        await tools.Execute("restart_service", JsonSerializer.SerializeToElement(new { service = "Spooler" }), CancellationToken.None);
        var lines = requests.Single().Details.ReplaceLineEndings("\n").Split('\n');
        assert(requests[0].Title == "Reiniciar serviço" && lines.Length == 3 && lines[1] == "Estado atual: parado (início automático)."
            && lines[0].StartsWith("Reiniciar Spooler.", StringComparison.Ordinal) && lines[2].StartsWith("Não foi possível consultar os eventos do serviço Spooler", StringComparison.Ordinal),
            "Spooler: a janela de aprovação mostra o estado observado antes do reinício");

        requests.Clear();
        var fresh = new WindowsTools(store, actor, request => { requests.Add(request); return Task.FromResult(false); }, new Progress<string>())
        { ServiceEventsReader = (_, _) => throw new InvalidOperationException("Eventos indisponíveis no teste.") };
        await fresh.Execute("restart_service", JsonSerializer.SerializeToElement(new { service = "Spooler" }), CancellationToken.None);
        assert(!requests.Single().Details.Contains("Estado atual"),
            "Spooler: sem observação anterior, a janela não mostra estado");

        // Leitura posterior com o Spooler em execução: a de antes, parado, deixa de valer.
        requests.Clear();
        printers = """[{"Name":"Fax","Status":"Normal","Jobs":[]}]""";
        await tools.Execute("get_printers", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        await tools.Execute("restart_service", JsonSerializer.SerializeToElement(new { service = "Spooler" }), CancellationToken.None);
        assert(!requests.Single().Details.Contains("Estado atual"),
            "Spooler: depois de uma consulta com o Spooler em execução, o estado parado anterior não aparece");
    }

    // O relatório separa o que persiste do que não é atual, pelo estado; a parte não atual não traz recomendação.
    private static void ReportSections(Action<bool, string> assert)
    {
        var report = LlamaReport(holding: true);
        var current = report.IndexOf(HealthReportPresentation.CurrentHeader, StringComparison.Ordinal);
        var other = report.IndexOf(HealthReportPresentation.OtherHeader, StringComparison.Ordinal);
        var llama = report.IndexOf("llama-server PID 9100", StringComparison.Ordinal);
        var holder = report.IndexOf("NexoMemTeste PID 23132", StringComparison.Ordinal);
        var llamaLine = report.Split('\n').Single(line => line.StartsWith("Achado", StringComparison.Ordinal) && line.Contains("llama-server"));
        assert(current >= 0 && other > current && holder > current && holder < other && llama > other,
            "Relatório: a ocorrência que persiste fica na parte atual, e a resolvida na parte que não é atual");
        assert(llamaLine.Contains("resolvida: 3 leituras seguidas sem a condição") && !llamaLine.Contains("Recomendação:")
            && report.Split('\n').Single(line => line.StartsWith("Achado", StringComparison.Ordinal) && line.Contains("NexoMemTeste")).Contains("Recomendação:"),
            "Relatório: só a ocorrência atual traz recomendação");
    }

    // Cenário 5 (conversa b28f9cf1): a resposta cita o processo e o PID, sem os MB; o Nexo acrescenta a leitura do monitor.
    private static async Task MemoryFigures(Store store, Account actor, Action<bool, string> assert)
    {
        const string reply = "O alerta de memória continua ativo: o NexoMemTeste PID 23132 está com memória em crescimento persistente. Salve o trabalho nele e feche-o se não estiver em uso.";
        var report = LlamaReport(holding: true);
        var tools = new FlowTools((name, _) => name switch
        {
            "get_health_report" => WindowsTools.Ok(report),
            "diagnose_device" => WindowsTools.Ok("Memória livre: 4 GB de 31.9 GB\r\nDisco C:: 250.0 GB livres de 476.3 GB"),
            _ => throw new Exception("Ferramenta inesperada: " + name)
        });
        var answer = (await Ask(store, actor, "meu pc ta lento", tools, new CapturingModel([Text(reply)]))).ReplaceLineEndings("\n");
        assert(answer.StartsWith(reply + "\n\nPelo monitor de saúde: “NexoMemTeste” (PID 23132) segue com 13591 MB; no alerta, subiu de 4375 MB para 13591 MB.\n\n", StringComparison.Ordinal)
            && answer.EndsWith(TurnFacts.Prefix + " nenhuma.", StringComparison.Ordinal),
            "Memória: os MB reais (4375 → 13591) entram na resposta que citou só o processo e o PID, antes da linha de fatos");

        var evidence = new InvestigationEvidence();
        evidence.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(report));
        assert(evidence.WithMemoryFigures("O NexoMemTeste segue com 13.591 MB.") == "O NexoMemTeste segue com 13.591 MB."
            && evidence.WithMemoryFigures("O disco C: está com bastante espaço.") == "O disco C: está com bastante espaço."
            && evidence.WithMemoryFigures("A memória está alta por causa de um programa.").Contains("“NexoMemTeste” (PID 23132) segue com 13591 MB"),
            "Memória: a resposta que já tem o número fica como está, a que não fala de memória também");
        assert(evidence.Report("").Contains("Ocorrência ativa no monitor de saúde") && evidence.Report("").Contains("NexoMemTeste PID 23132"),
            "Memória: a ocorrência do processo que segura memória (\"persiste: … segue com N MB\") entra no resumo como ativa");
    }

    // Conversa f88928c4: a resposta dizia que o llama-server "está consumindo" memória, com a ocorrência dele resolvida.
    private static async Task StaleProcess(Store store, Account actor, Action<bool, string> assert)
    {
        const string stale = "Seu computador está lento porque o llama-server está consumindo uma quantidade significativa de memória.";
        const string honest = "O monitor mostrou o llama-server com memória alta antes, mas essa ocorrência já foi resolvida. Desde quando o computador está lento?";
        var report = LlamaReport(holding: false);
        FlowTools Tools() => new((name, _) => name switch
        {
            "get_health_report" => WindowsTools.Ok(report),
            "diagnose_device" => WindowsTools.Ok("Memória livre: 20 GB de 31.9 GB\r\nDisco C:: 250.0 GB livres de 476.3 GB"),
            _ => throw new Exception("Ferramenta inesperada: " + name)
        });
        var model = new CapturingModel([Text(stale), Text(honest)]);
        var answer = await Ask(store, actor, "meu pc ta lento", Tools(), model);
        assert(answer.StartsWith(honest, StringComparison.Ordinal) && (model.SystemMessages(1).LastOrDefault() ?? "").Contains(InvestigationEvidence.StaleProcessReason),
            "Ocorrência resolvida: a resposta que apresenta o llama-server como atual é recusada com o motivo, e a correção vira a resposta");

        var evidence = new InvestigationEvidence();
        evidence.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(report));
        assert(evidence.ValidateStatement("O llama-server não está consumindo memória agora.", false) == null
            && evidence.ValidateStatement("O llama-server estava consumindo memória mais cedo.", false) == null
            && evidence.ValidateStatement("O chrome está consumindo memória.", false) == null
            && evidence.ValidateStatement("Agora o llama-server consome 9 GB.", false) == InvestigationEvidence.StaleProcessReason,
            "Ocorrência resolvida: negação, passado e outro processo passam; o consumo no presente do processo resolvido é recusado");
        assert(!evidence.Report("").Contains("llama-server"),
            "Ocorrência resolvida: o resumo do turno não lista a ocorrência resolvida");
        evidence.Observe("get_processes", "p", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(JsonSerializer.Serialize(new[] { new { Name = "llama-server", Id = 9100, MemoryMB = 9000, Responding = true } })));
        assert(evidence.ValidateStatement(stale, false) == null,
            "Ocorrência resolvida: se a consulta de processos do turno mostra o processo, ele pode ser citado como atual");
    }

    // Relatório real do HealthReportPresentation: o llama-server teve um alerta de memória já resolvido; com holding, o
    // NexoMemTeste segura a memória (13591 MB) depois de subir de 4375 MB.
    private static string LlamaReport(bool holding)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-88-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new OperationalStore(root);
            var now = DateTimeOffset.UtcNow;
            const long Mb = 1024 * 1024;
            var llamaStart = now.AddHours(-3);
            var testStart = now.AddMinutes(-20);
            var samples = Enumerable.Range(0, 5).Select(i => new HealthSnapshot
            {
                Time = now.AddSeconds(-30 * (4 - i)), CpuPercent = 10, MemoryUsedPercent = holding ? 60 : 40, CollectionStatus = "complete",
                Disks = [new DiskHealth("C:\\", 250_000, 500_000)],
                Processes = [new ProcessHealth("llama-server", 9100, 900 * Mb, 1, llamaStart), .. holding ? new[] { new ProcessHealth("NexoMemTeste", 23132, 13591 * Mb, 0, testStart) } : []]
            }).ToArray();
            foreach (var sample in samples) db.SaveSnapshot(sample);
            var found = new List<HealthFinding>
            {
                new() { Id = $"process-memory-growing:9100:{llamaStart.UtcTicks}:s0", Time = now.AddMinutes(-4), Category = "memory", Severity = "warning",
                    Problem = "Processo com memória em crescimento persistente", Evidence = $"llama-server PID 9100 iniciado em {llamaStart:yyyy-MM-dd HH:mm:ss zzz}: de 1200 MB para 6200 MB em 4 leituras e 15 pontos da RAM",
                    Cause = "Crescimento observado neste processo", Recommendation = "Verifique se o aplicativo está em uso e salve o trabalho antes de qualquer intervenção", Confidence = 0.7,
                    MemoryGrowth = new("llama-server", 1200 * Mb, 6200 * Mb) }
            };
            if (holding)
                found.Add(new() { Id = $"process-memory-growing:23132:{testStart.UtcTicks}:s1", Time = now.AddMinutes(-3), Category = "memory", Severity = "warning",
                    Problem = "Processo com memória em crescimento persistente", Evidence = $"NexoMemTeste PID 23132 iniciado em {testStart:yyyy-MM-dd HH:mm:ss zzz}: de 4375 MB para 13591 MB em 4 leituras e 29 pontos da RAM",
                    Cause = "Crescimento observado neste processo", Recommendation = "Verifique se o aplicativo está em uso e salve o trabalho antes de qualquer intervenção", Confidence = 0.7,
                    MemoryGrowth = new("NexoMemTeste", 4375 * Mb, 13591 * Mb) });
            db.SaveFindings(found);
            return HealthReportPresentation.FromStore(db);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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
        internal List<string> Calls { get; } = [];
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            Calls.Add(name);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(execute(name, args));
        }
    }

    private sealed class CapturingModel(string[] responses) : HttpMessageHandler
    {
        private readonly List<JsonElement> requests = [];
        private int calls;
        internal bool Consumed => calls == responses.Length;
        // As mensagens de sistema que o modelo recebeu numa chamada: o prompt primeiro, a instrução do controle por último.
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
