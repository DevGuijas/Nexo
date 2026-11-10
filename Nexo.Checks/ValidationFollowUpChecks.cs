using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// Ajustes da validação da aa62db9 no PC, em 08 e 09/11 (#88):
// 1. disco cheio: a recusa da ação anunciada cita o trecho e a saída sem ferramenta, e a parada traz um caminho real;
// 2. reinício de serviço: a janela de aprovação mostra o estado observado antes, nunca o do modelo;
// 3. memória: os MB do processo que segura memória chegam à resposta;
// 4. toda ferramenta do catálogo tem rótulo de progresso próprio;
// 5. ocorrência encerrada (resolvida ou de estado atual desconhecido) não é apresentada como atual; qualquer outro
//    estado, inclusive um que ainda não existe, conta como atual.
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
            // Revisão do @qa no 27d9a0a.
            await GuidanceOnFinish(store, actor, assert);
            await ServiceStateAge(store, actor, assert);
            await RestartRefreshesState(assert);
            SyntheticState(assert);
            ConsumptionSentences(assert);
            await QuestionStaysLast(store, actor, assert);
            // Junção com a #57 (PR #90).
            ServiceStopLabel(assert);
            SupersededStopOutOfReport(assert);
            // Junção com a #71 (PR #91): o patamar estável real.
            StablePlateau(assert);
            InformativeOnlyByLabel(assert);
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
        var chrome = report.IndexOf("chrome PID 4120", StringComparison.Ordinal);
        var chromeLine = report.Split('\n').Single(line => line.StartsWith("Achado", StringComparison.Ordinal) && line.Contains("chrome PID 4120"));
        assert(current >= 0 && other > current && holder > current && holder < other && llama > other,
            "Relatório: a ocorrência que persiste fica na parte atual, e a resolvida na parte que não é atual");
        assert(chromeLine.Contains("ocorrência histórica; estado atual desconhecido") && chrome > other && !chromeLine.Contains("Recomendação:"),
            "Relatório: a ocorrência de estado atual desconhecido fica fora da parte atual, sem recomendação");
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
            && evidence.ValidateStatement("Agora o llama-server consome 9 GB.", false) == InvestigationEvidence.StaleProcessReason
            && evidence.ValidateStatement("O chrome está consumindo memória.", false) == InvestigationEvidence.StaleProcessReason
            && evidence.ValidateStatement("O firefox está consumindo memória.", false) == null,
            "Ocorrência encerrada: negação, passado e processo fora do relatório passam; o consumo no presente do processo resolvido ou de estado desconhecido é recusado");
        assert(!evidence.Report("").Contains("llama-server"),
            "Ocorrência resolvida: o resumo do turno não lista a ocorrência resolvida");
        evidence.Observe("get_processes", "p", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(JsonSerializer.Serialize(new[] { new { Name = "llama-server", Id = 9100, MemoryMB = 9000, Responding = true } })));
        assert(evidence.ValidateStatement(stale, false) == null,
            "Ocorrência resolvida: se a consulta de processos do turno mostra o processo, ele pode ser citado como atual");
    }

    // A recusa no caminho do finish_support também leva o trecho recusado (QM6 do @qa).
    private static async Task GuidanceOnFinish(Store store, Account actor, Action<bool, string> assert)
    {
        var model = new CapturingModel([Call("finish_support", new { message = DiskText }), Text(DiskFixed)]);
        var answer = await Ask(store, actor, "estou sem espaço no disco T:", DiskTools(), model);
        assert(answer.StartsWith(DiskFixed, StringComparison.Ordinal) && (model.SystemMessages(1).LastOrDefault() ?? "").Contains("Controle de execução: " + AgentProtocol.PendingActionReason + " Trecho recusado: “vamos verificar”."),
            "Disco: a recusa do finish_support também leva o trecho recusado ao modelo");
    }

    // O estado observado vale por 5 minutos, pelo relógio do executor (QM4 do @qa).
    private static async Task ServiceStateAge(Store store, Account actor, Action<bool, string> assert)
    {
        var clock = new DateTimeOffset(2026, 11, 9, 3, 0, 0, TimeSpan.Zero);
        var requests = new List<ChangeRequest>();
        var tools = new WindowsTools(store, actor, request => { requests.Add(request); return Task.FromResult(false); }, new Progress<string>())
        {
            PrinterReader = (_, _) => Task.FromResult("""{"Spooler":"Stopped","StartType":"Automatic"}"""),
            ServiceEventsReader = (_, _) => throw new InvalidOperationException("Eventos indisponíveis no teste."),
            ServiceEventsClock = () => clock
        };
        await tools.Execute("get_printers", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        clock = clock.AddMinutes(5);
        await tools.Execute("restart_service", JsonSerializer.SerializeToElement(new { service = "Spooler" }), CancellationToken.None);
        clock = clock.AddMinutes(1);
        await tools.Execute("restart_service", JsonSerializer.SerializeToElement(new { service = "Spooler" }), CancellationToken.None);
        assert(requests.Count == 2 && requests[0].Details.Contains("\nEstado atual: parado (início automático).\n") && !requests[1].Details.Contains("Estado atual"),
            "Spooler: a leitura do estado vale por 5 minutos; mais velha, a janela não a mostra");
    }

    // Depois de um reinício concluído, a janela do próximo mostra o estado que o Windows informou, não o "parado" de antes.
    private static async Task RestartRefreshesState(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-88-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            var isolated = new Store(root, directAccess: true);
            var requests = new List<ChangeRequest>();
            var tools = new WindowsTools(isolated, isolated.GetLocalSession(), request => { requests.Add(request); return Task.FromResult(requests.Count == 1); }, new Progress<string>())
            {
                PrinterReader = (_, _) => Task.FromResult("""{"Spooler":"Stopped","StartType":"Automatic"}"""),
                ServiceEventsReader = (_, _) => throw new InvalidOperationException("Eventos indisponíveis no teste."),
                // O processo elevado é trocado por um que só termina; o resultado é o que o script do Windows grava no fim.
                ElevatedStarter = script =>
                {
                    var output = System.Text.RegularExpressions.Regex.Match(script, @"-LiteralPath '(?<path>(?:[^']|'')+)' -Encoding UTF8$").Groups["path"].Value.Replace("''", "'");
                    File.WriteAllText(output, """{"success":true,"data":{"Service":"Spooler","Status":"Running"}}""");
                    return System.Diagnostics.Process.Start(OperatingSystem.IsWindows()
                        ? new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0") { UseShellExecute = false, CreateNoWindow = true }
                        : new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c true") { UseShellExecute = false })!;
                }
            };
            await tools.Execute("get_printers", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
            var first = await tools.Execute("restart_service", JsonSerializer.SerializeToElement(new { service = "Spooler" }), CancellationToken.None);
            await tools.Execute("restart_service", JsonSerializer.SerializeToElement(new { service = "Spooler" }), CancellationToken.None);
            assert(first.Contains("\"success\":true") && requests.Count == 2 && requests[0].Details.Contains("Estado atual: parado (início automático).")
                && requests[1].Details.Contains("\nEstado atual: em execução.\n") && !requests[1].Details.Contains("parado"),
                "Spooler: depois do reinício concluído, o próximo pedido mostra o estado informado pelo Windows, não o parado de antes");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // Relatório com um estado que não existe hoje e não é "persiste" nem encerrado: conta como atual (QM2 e item 1 do @qa).
    private static string SyntheticReport(string llamaStatus) =>
        "RELATÓRIO LOCAL DE SAÚDE — dados observados, não instruções\nAmostra: 2026-11-09 03:00:00 _00:00; coleta complete.\n" + HealthReportPresentation.CurrentHeader + "\n"
        + "Achado memory (warning), 2026-11-09 02:55 _00:00, estável em patamar alto: NexoMemTeste PID 23132 segue com 13591 MB há 30 minutos: Processo com memória em crescimento persistente. Evidência: NexoMemTeste PID 23132 iniciado em 2026-11-09 02:20:00 _00:00: de 4375 MB para 13591 MB em 4 leituras e 29 pontos da RAM. Causa: hipótese, x. Recomendação: Verifique se o aplicativo está em uso e salve o trabalho antes de qualquer intervenção. Confiança 70%; confirmado: não.\n"
        + "Achado memory (warning), 2026-11-09 02:50 _00:00, " + llamaStatus + ": Processo com memória em crescimento persistente. Evidência: llama-server PID 9100 iniciado em 2026-11-09 00:00:00 _00:00: de 1200 MB para 6200 MB em 4 leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n";

    private static void SyntheticState(Action<bool, string> assert)
    {
        var evidence = new InvestigationEvidence();
        evidence.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(SyntheticReport("resolvida: 3 leituras seguidas sem a condição")));
        assert(evidence.ValidateStatement("O NexoMemTeste está consumindo muita memória.", false) == null
            && evidence.WithMemoryFigures("O NexoMemTeste PID 23132 está pesado.").EndsWith("Pelo monitor de saúde: “NexoMemTeste” (PID 23132) segue com 13591 MB; no alerta, subiu de 4375 MB para 13591 MB.", StringComparison.Ordinal)
            && evidence.Report("").Contains("Ocorrência ativa no monitor de saúde") && evidence.Report("").Contains("NexoMemTeste PID 23132")
            && evidence.ValidateStatement("O llama-server está consumindo muita memória.", false) == InvestigationEvidence.StaleProcessReason,
            "Estado novo: um estado que não é \"persiste\" nem encerrado conta como atual (citação, MB e resumo); o resolvido continua não atual");
        var both = new InvestigationEvidence();
        both.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(SyntheticReport("resolvida: 3 leituras seguidas sem a condição")
            + "Achado memory (warning), 2026-11-09 02:58 _00:00, persiste nas leituras atuais: Processo com memória em crescimento persistente. Evidência: llama-server PID 9100 iniciado em 2026-11-09 00:00:00 _00:00: de 6200 MB para 9000 MB em 4 leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n"));
        assert(both.ValidateStatement("O llama-server está consumindo muita memória.", false) == null,
            "Estado novo: um processo que também aparece numa ocorrência atual pode ser citado como atual");
    }

    // As sondas do @qa (qa88r1-probe) sobre a ocorrência resolvida do llama-server: as legítimas passam, as afirmações no
    // presente são recusadas.
    private static void ConsumptionSentences(Action<bool, string> assert)
    {
        var evidence = new InvestigationEvidence();
        evidence.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(
            "RELATÓRIO LOCAL DE SAÚDE — dados observados, não instruções\nAmostra: 2026-11-09 03:00:00 _00:00; coleta complete.\n" + HealthReportPresentation.OtherHeader + "\n"
            + "Achado memory (warning), 2026-11-09 02:50 _00:00, resolvida: 3 leituras seguidas sem a condição: Processo com memória em crescimento persistente. Evidência: llama-server PID 9100 iniciado em 2026-11-09 00:00:00 _00:00:_ de 1200 MB para 6200 MB em 4 leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n"));
        bool Refused(string sentence) => evidence.ValidateStatement(sentence, false) == InvestigationEvidence.StaleProcessReason;
        string[] accepted =
        [
            "O llama-server do Ollama ocupa memória enquanto o modelo está carregado, o que é normal.",
            "O llama-server já foi resolvido e agora está usando pouca memória.",
            "Antes, o llama-server estava consumindo memória; agora está normal.",
            "O llama-server continua usando memória?",
            "O llama-server está consumindo muita memória, não é?",
            "O llama-server consome memória só quando você usa o assistente.",
            "Se o llama-server estiver consumindo memória de novo, avise.",
            "Quando o llama-server ocupa muita memória, o PC fica lento; hoje isso já foi resolvido.",
            "O llama-server não está consumindo memória agora.",
            "O llama-server nem está usando memória."
        ];
        string[] refused =
        [
            "O llama-server tá consumindo memória.",
            "O llama-server.exe está consumindo memória.",
            "O LLAMA-SERVER está consumindo memória.",
            "Os processos llama-server e chrome estão consumindo memória.",
            "O llama-server, que não é do sistema, está consumindo memória.",
            "A memória está ocupada porque o llama-server ocupa 6 GB."
        ];
        var wrongAccepted = accepted.Where(Refused).ToArray();
        var wrongRefused = refused.Where(sentence => !Refused(sentence)).ToArray();
        assert(wrongAccepted.Length == 0 && wrongRefused.Length == 0,
            "Ocorrência encerrada: pergunta, condição, \"resolvido/normal\" e negação ligada ao verbo passam; \"tá consumindo\", \".exe\" e negação longe do verbo são recusados"
            + string.Concat(wrongAccepted.Select(x => " [recusada: " + x + "]")) + string.Concat(wrongRefused.Select(x => " [aceita: " + x + "]")));
    }

    // ask_user: a pergunta continua sendo a última coisa da resposta; a leitura em MB entra antes dela.
    private static async Task QuestionStaysLast(Store store, Account actor, Action<bool, string> assert)
    {
        const string statement = "O NexoMemTeste PID 23132 continua crescendo.";
        const string question = "Você está usando esse programa agora?";
        var report = LlamaReport(holding: true);
        var tools = new FlowTools((name, _) => name switch
        {
            "get_health_report" => WindowsTools.Ok(report),
            "diagnose_device" => WindowsTools.Ok("Memória livre: 4 GB de 31.9 GB\r\nDisco C:: 250.0 GB livres de 476.3 GB"),
            _ => throw new Exception("Ferramenta inesperada: " + name)
        });
        var answer = await Ask(store, actor, "meu pc ta lento", tools,
            new CapturingModel([Call("ask_user", new { message = statement + " " + question, reason = "missing_information" })]));
        const string figures = "Pelo monitor de saúde: “NexoMemTeste” (PID 23132) segue com 13591 MB; no alerta, subiu de 4375 MB para 13591 MB.";
        assert(answer.StartsWith(statement + "\n\n" + figures + "\n\n" + question + "\n\n" + TurnFacts.Prefix, StringComparison.Ordinal),
            "Memória: no ask_user, a leitura em MB entra antes da pergunta, que continua sendo a última frase antes da linha de fatos");
        var evidence = new InvestigationEvidence();
        evidence.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(report));
        assert(evidence.WithMemoryFigures("O NexoMemTeste segue alto. Você usa esse programa? Posso ajudar a decidir?") == "O NexoMemTeste segue alto.\n\n" + figures + "\n\nVocê usa esse programa? Posso ajudar a decidir?"
            && evidence.WithMemoryFigures("O NexoMemTeste está usando a memória?") == figures + "\n\nO NexoMemTeste está usando a memória?",
            "Memória: várias perguntas no fim ficam juntas depois da leitura, e uma resposta que é só pergunta começa pela leitura");
    }

    // O estado da #57 ("Persiste: <serviço> segue parado depois de ter sido visto em execução") é lido inteiro: a ocorrência
    // é atual e o problema da linha é o texto da regra, não o meio do estado.
    private static void ServiceStopLabel(Action<bool, string> assert)
    {
        var evidence = new InvestigationEvidence();
        evidence.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(
            "RELATÓRIO LOCAL DE SAÚDE — dados observados, não instruções\nAmostra: 2026-11-09 03:00:00 _00:00; coleta complete.\n" + HealthReportPresentation.CurrentHeader + "\n"
            + "Achado service (warning), 2026-11-09 02:55 _00:00, persiste: Spooler segue parado depois de ter sido visto em execução: Serviço automático interrompido. Evidência: Spooler parado em 2 leituras_ depois de pelo menos 10 min em execução observada. Causa: hipótese, Causa da parada ainda não identificada. Recomendação: Consulte eventos do serviço antes de propor reinício. Confiança 85%; confirmado: não.\n"));
        var report = evidence.Report("");
        assert(report.Contains("Ocorrência ativa no monitor de saúde: Serviço automático interrompido (evidência: “Spooler parado em 2 leituras")
            && !report.Contains("segue parado depois de ter sido visto em execução: Serviço"),
            "Junção com a #57: o serviço que segue parado depois de visto em execução entra no resumo como ocorrência ativa, com o problema da regra");
    }

    // Ocorrência de serviço parado superada pela leitura atual (#57): não vai ao modelo, nem na parte "não atual".
    private static void SupersededStopOutOfReport(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-88-57-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new OperationalStore(root);
            var now = DateTimeOffset.UtcNow;
            const string google = "GoogleUpdaterService157.0.8089.0";
            var stopped = new ServiceHealth(google, "Stopped", "Automatic", DelayedAutoStart: false, TriggerStart: false, ExitCode: 0);
            var samples = new[] { now.AddSeconds(-30), now }.Select(at => new HealthSnapshot
            {
                Time = at, CpuPercent = 10, MemoryUsedPercent = 40, CollectionStatus = "complete", Disks = [new DiskHealth("C:\\", 250_000, 500_000)],
                Processes = [new ProcessHealth("llama-server", 9100, 900L * 1024 * 1024, 1, now.AddHours(-3))], Services = [stopped]
            }).ToArray();
            foreach (var sample in samples) db.SaveSnapshot(sample);
            db.SaveFindings([
                // Gravado antes da regra da #57, sem a prova da execução: a leitura atual o supera.
                new HealthFinding { Id = "service-stopped:" + google + ":s0", Time = now.AddHours(-2), Category = "service", Severity = "warning", Problem = "Serviço automático interrompido",
                    Evidence = google + " parado em 2 leituras", Cause = "Causa da parada ainda não identificada", Recommendation = "Consulte eventos do serviço antes de propor reinício", Confidence = 0.85 },
                new HealthFinding { Id = $"process-memory-growing:9100:{now.AddHours(-3).UtcTicks}:s0", Time = now.AddMinutes(-4), Category = "memory", Severity = "warning",
                    Problem = "Processo com memória em crescimento persistente", Evidence = "llama-server PID 9100: de 1200 MB para 6200 MB em 4 leituras", Cause = "Crescimento observado neste processo",
                    Recommendation = "Verifique se o aplicativo está em uso e salve o trabalho antes de qualquer intervenção", Confidence = 0.7, MemoryGrowth = new("llama-server", 1200L * 1024 * 1024, 6200L * 1024 * 1024) }
            ]);
            var report = HealthReportPresentation.FromStore(db);
            assert(report.Contains(HealthReportPresentation.OtherHeader) && report.Contains("llama-server PID 9100") && !report.Contains("GoogleUpdater") && !report.Contains("Serviço automático interrompido"),
                "Junção com a #57: a parada superada pela leitura atual não vai ao modelo, nem na parte que não é atual");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // Patamar estável real da #71 (Navegador em 14000 MB por 2 h, como nos testes dela): atual e informativo. O processo pode
    // ser citado como atual, ganha os MB, fica na parte atual sem recomendação de ação, e o resumo não diz que é problema
    // ativo nem que foi resolvido.
    private static void StablePlateau(Action<bool, string> assert)
    {
        const long Mb = 1024 * 1024;
        var limits = new HealthThresholds { ProcessMemoryGrowthPoints = 7, ProcessMemorySamples = 5 };
        var end = DateTimeOffset.UtcNow.AddSeconds(-5);
        var start = end.AddDays(-5);
        var series = Enumerable.Range(0, 242).Select(i => new HealthSnapshot
        {
            Time = end.AddSeconds(-30 * (241 - i)), CollectionStatus = "complete", MemoryUsedPercent = 60, MemoryTotalBytes = 32L * 1024 * Mb, MemoryUsedBytes = 32L * 1024 * Mb * 6 / 10,
            Processes = [new("Navegador", 20248, 14000 * Mb, null, start), new("explorer", 6400, 310 * Mb, null, start)]
        }).ToArray();
        var root = Path.Combine(Path.GetTempPath(), "nexo-88-71-" + Guid.NewGuid().ToString("N"));
        string report;
        try
        {
            using var db = new OperationalStore(root);
            db.SaveFindings([
                new HealthFinding { Id = "memory-growing:s0", Time = series[0].Time, Category = "memory", Severity = "warning", Problem = "Memória em crescimento persistente",
                    Evidence = "De 45% para 60% em três leituras", Cause = "Processo responsável ainda não identificado", Recommendation = "Compare o uso por processo antes de intervir" },
                // A evidência no formato da regra (HealthRules): "<processo> PID <n> iniciado em …: de X MB para Y MB…".
                new HealthFinding { Id = $"process-memory-growing:20248:{start.UtcTicks}:s1", Time = series[1].Time, Category = "memory", Severity = "warning",
                    Problem = "Processo com memória em crescimento persistente", Evidence = $"Navegador PID 20248 iniciado em {start:yyyy-MM-dd HH:mm:ss zzz}: de 2000 MB para 16000 MB em 5 leituras e 40 pontos da RAM",
                    Cause = "Crescimento observado neste processo; vazamento e carga legítima não distinguidos", Recommendation = "Verifique se o aplicativo está em uso e salve o trabalho antes de qualquer intervenção",
                    Confidence = 0.7, MemoryGrowth = new("Navegador", 2000 * Mb, 16000 * Mb) }
            ]);
            foreach (var sample in series) { db.SaveSnapshot(sample); HealthOccurrences.RecordPlateaus(db, sample, limits); }
            report = HealthReportPresentation.FromStore(db, limits).ReplaceLineEndings("\n");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        var lines = report.Split('\n');
        var current = Array.IndexOf(lines, HealthReportPresentation.CurrentHeader);
        var other = Array.IndexOf(lines, HealthReportPresentation.OtherHeader);
        var process = Array.FindIndex(lines, line => line.StartsWith("Achado memory (info)", StringComparison.Ordinal) && line.Contains("estável em patamar alto (informativo, não é falha ativa): Navegador PID 20248 segue com 14000 MB"));
        assert(current >= 0 && process > current && (other < 0 || process < other) && lines[process].Contains("Recomendação: Nenhuma ação recomendada")
            && !report.Contains("Verifique se o aplicativo") && !report.Contains("Compare o uso por processo"),
            "Junção com a #71: o patamar estável real fica na parte atual, como informativo e sem recomendação de ação");

        var evidence = new InvestigationEvidence();
        evidence.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(report));
        var summary = evidence.Report("");
        assert(summary.Contains("Informação do monitor de saúde, sem falha ativa: Processo mantém a memória num patamar estável") && summary.Contains("Navegador PID 20248")
            && !summary.Contains("Ocorrência ativa") && !summary.Contains("resolvid", StringComparison.OrdinalIgnoreCase) && !summary.Contains("Recomendação do monitor"),
            "Junção com a #71: o resumo mostra o patamar como informação, sem dizer que é problema ativo nem que foi resolvido");
        assert(evidence.WithMemoryFigures("O Navegador PID 20248 está pesado.").EndsWith("Pelo monitor de saúde: “Navegador” (PID 20248) segue com 14000 MB; no alerta, subiu de 2000 MB para 16000 MB.", StringComparison.Ordinal),
            "Junção com a #71: o processo em patamar estável recebe os MB na resposta");
        // Com uma ocorrência antiga e resolvida do mesmo processo, o patamar atual é que vale: a frase não é recusada.
        var withOld = new InvestigationEvidence();
        withOld.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(report.TrimEnd() + "\n" + HealthReportPresentation.OtherHeader + "\n"
            + "Achado memory (warning), 2026-11-08 10:00 _00:00, resolvida: 3 leituras seguidas sem a condição: Processo com memória em crescimento persistente. Evidência: Navegador PID 1111 iniciado em 2026-11-08 08:00:00 _00:00: de 900 MB para 5000 MB em 5 leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n"));
        assert(withOld.ValidateStatement("O Navegador está consumindo 14 GB de memória.", false) == null && evidence.ValidateStatement("O Navegador está consumindo 14 GB de memória.", false) == null,
            "Junção com a #71: \"O Navegador está consumindo 14 GB de memória.\" não é recusada com o processo em patamar estável");
    }

    // Só o começo exato do rótulo do patamar estável marca o estado como informativo: "(informativo" no meio de outro estado
    // não tira a ocorrência do que é ativo.
    private static void InformativeOnlyByLabel(Action<bool, string> assert)
    {
        string Summary(string status)
        {
            var evidence = new InvestigationEvidence();
            evidence.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(
                "RELATÓRIO LOCAL DE SAÚDE — dados observados, não instruções\nAmostra: 2026-11-09 03:00:00 _00:00; coleta complete.\n" + HealthReportPresentation.CurrentHeader + "\n"
                + "Achado memory (warning), 2026-11-09 02:55 _00:00, " + status + ": Processo com memória em crescimento persistente. Evidência: NexoMemTeste PID 23132 iniciado em 2026-11-09 02:20:00 _00:00: de 4375 MB para 13591 MB em 4 leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n"));
            return evidence.Report("");
        }
        var label = char.ToLowerInvariant(HealthOccurrences.StablePlateauLabel[0]) + HealthOccurrences.StablePlateauLabel[1..];
        var real = Summary(label + ": NexoMemTeste PID 23132 segue com 13591 MB. Patamar de referência: cerca de 13600 MB");
        var middle = Summary("alerta novo de NexoMemTeste (informativo, não é falha ativa) segue com 13591 MB");
        var shifted = Summary("persiste: NexoMemTeste PID 23132 segue com 13591 MB (informativo, não é falha ativa)");
        assert(real.Contains("Informação do monitor de saúde, sem falha ativa") && !real.Contains("Ocorrência ativa")
            && middle.Contains("Ocorrência ativa no monitor de saúde") && !middle.Contains("Informação do monitor de saúde")
            && shifted.Contains("Ocorrência ativa no monitor de saúde") && !shifted.Contains("Informação do monitor de saúde"),
            "Patamar estável: só o rótulo exato marca a ocorrência como informativa; \"(informativo\" no meio de outro estado continua ativo");
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
                // O chrome aparece sem início legível: não dá para confirmar que liberou a memória, e o estado fica desconhecido.
                Processes = [new ProcessHealth("llama-server", 9100, 900 * Mb, 1, llamaStart), new ProcessHealth("chrome", 4120, 700 * Mb, 1),
                    .. holding ? new[] { new ProcessHealth("NexoMemTeste", 23132, 13591 * Mb, 0, testStart) } : []]
            }).ToArray();
            foreach (var sample in samples) db.SaveSnapshot(sample);
            var found = new List<HealthFinding>
            {
                new() { Id = $"process-memory-growing:9100:{llamaStart.UtcTicks}:s0", Time = now.AddMinutes(-4), Category = "memory", Severity = "warning",
                    Problem = "Processo com memória em crescimento persistente", Evidence = $"llama-server PID 9100 iniciado em {llamaStart:yyyy-MM-dd HH:mm:ss zzz}: de 1200 MB para 6200 MB em 4 leituras e 15 pontos da RAM",
                    Cause = "Crescimento observado neste processo", Recommendation = "Verifique se o aplicativo está em uso e salve o trabalho antes de qualquer intervenção", Confidence = 0.7,
                    MemoryGrowth = new("llama-server", 1200 * Mb, 6200 * Mb) },
                new() { Id = $"process-memory-growing:4120:{now.AddHours(-1).UtcTicks}:s0", Time = now.AddMinutes(-5), Category = "memory", Severity = "warning",
                    Problem = "Processo com memória em crescimento persistente", Evidence = $"chrome PID 4120 iniciado em {now.AddHours(-1):yyyy-MM-dd HH:mm:ss zzz}: de 400 MB para 2400 MB em 4 leituras e 6 pontos da RAM",
                    Cause = "Crescimento observado neste processo", Recommendation = "Verifique se o aplicativo está em uso e salve o trabalho antes de qualquer intervenção", Confidence = 0.7,
                    MemoryGrowth = new("chrome", 400 * Mb, 2400 * Mb) }
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
