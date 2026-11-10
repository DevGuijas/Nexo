using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// Pontos da #88 que falharam na validação da 11fa078 no PC, em 09/11:
// 1. memória: a linha "Pelo monitor de saúde" sumia diante de qualquer "<n> MB/GB" (passo 1.3, conversa 250f905d, e passo 2.7);
// 2. disco com pouco espaço atual: a oferta de achar os maiores arquivos é recusada e o caminho do Armazenamento entra na
//    resposta aceita (passo 2.4, conversa ce60e9e9);
// 3. disco com a ocorrência encerrada: "Libere espaço no disco" com o C: 87% livre é recusado (passo 2.7, conversa 2f762bbb).
internal static class ValidationRetestChecks
{
    // Textos reais do PC.
    private const string MemoryAnswer = "O seu computador está lento porque o processo NexoMemTeste está usando muita memória. A memória livre está em torno de 4,7 GB. Verifique se o NexoMemTeste é necessário; se não for, salve o trabalho e feche-o.";
    private const string DiskOffer = "O disco T: está com 0,1 GB livres de um total de 2 GB. Para liberar espaço, exclua ou mova arquivos que não usa. Se precisar, posso ajudar a identificar quais arquivos estão ocupando mais espaço no disco T:. Deseja que eu faça isso?";
    private const string DiskOfferAgain = "O disco T: é um dispositivo de armazenamento. Se quiser, posso ajudar a identificar quais arquivos estão ocupando mais espaço no disco T:_. Deseja que eu faça isso?";
    private const string DiskHonest = "O disco T: está com 0,1 GB livres de um total de 2 GB. Para liberar espaço, exclua ou mova arquivos que não usa; o Nexo não apaga nem lista arquivos.";
    private const string ClosedDiskAnswer = "Seu computador pode estar lento por alguns motivos:\n1. Feche programas que não está usando.\n2. Libere espaço no disco: remova arquivos que não usa.\n3. Reinicie o computador.";
    private const string ClosedDiskHonest = "O monitor de saúde não mostra ocorrência ativa agora. O disco C: tem 87% livre, e o alerta de pouco espaço no T: está encerrado. Desde quando o computador está lento?";

    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        var previousAi = store.Data.AiEnabled;
        store.Data.AiEnabled = true;
        try
        {
            MemoryFigureOfTheProcess(assert);
            await MemoryFlow(store, actor, assert);
            FileSearchOffer(assert);
            await DiskOfferFlow(store, actor, assert);
            StorageLine(assert);
            ClosedDisk(assert);
            await ClosedDiskFlow(store, actor, assert);
            // Revisão do @qa no cff1b22.
            ActiveAndClosedDisk(assert);
            ReportReplaced(assert);
        }
        finally { store.Data.AiEnabled = previousAi; }
    }

    private static InvestigationEvidence Observed(string report)
    {
        var evidence = new InvestigationEvidence();
        evidence.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(report));
        return evidence;
    }

    private static void MemoryFigureOfTheProcess(Action<bool, string> assert)
    {
        var report = HolderReport(12055);
        assert(report.Contains("NexoMemTeste PID 21356") && report.Contains("segue com 12055 MB"),
            "Reteste memória: o relatório real tem o NexoMemTeste segurando 12055 MB");
        var evidence = Observed(report);
        const string line = "Pelo monitor de saúde: “NexoMemTeste” (PID 21356) segue com 12055 MB; no alerta, subiu de 278 MB para 12055 MB.";
        assert(evidence.WithMemoryFigures(MemoryAnswer).Contains(line),
            "Reteste memória (conversa 250f905d): a memória livre do sistema (4,7 GB) não tira da resposta os MB do NexoMemTeste");
        assert(evidence.WithMemoryFigures("O NexoMemTeste está pesado. O chrome usa 12055 MB.").Contains(line),
            "Reteste memória: o número de outra frase não conta como os MB do processo");
        assert(evidence.WithMemoryFigures("O NexoMemTeste segue com 12.055 MB.") == "O NexoMemTeste segue com 12.055 MB."
            && evidence.WithMemoryFigures("O processo PID 21356 está com 11,8 GB.") == "O processo PID 21356 está com 11,8 GB."
            && evidence.WithMemoryFigures("O NexoMemTeste.exe segura 12055MB agora.") == "O NexoMemTeste.exe segura 12055MB agora.",
            "Reteste memória: a frase que cita o processo (pelo nome ou PID) com os MB dele, em MB ou GB, fica como está");
        assert(evidence.WithMemoryFigures("O NexoMemTeste está com 6 GB.").Contains(line)
            && evidence.WithMemoryFigures("O NexoMemTeste tem 12055 MB livres para outros programas.").Contains(line),
            "Reteste memória: um valor longe da leitura, ou ligado a memória livre, não conta");

        // Passo 2.7: "NexoMemTeste (cerca de 10 GB)" com 10806 MB. 10 GB = 10240 MB, 5% longe: conta como citado.
        var rounded = Observed(HolderReport(10806));
        assert(rounded.WithMemoryFigures("O NexoMemTeste (cerca de 10 GB) segura memória.") == "O NexoMemTeste (cerca de 10 GB) segura memória."
            && rounded.WithMemoryFigures("O NexoMemTeste (cerca de 9 GB) segura memória.").Contains("“NexoMemTeste” (PID 21356) segue com 10806 MB"),
            "Reteste memória (passo 2.7): até 10% da leitura conta como os MB do processo (10 GB para 10806 MB); 9 GB (15%) não");

        // O "livre" de outra oração não tira o valor do processo; milhar com espaço comum ou não separável (@qa, item 6).
        const string clauses = "O NexoMemTeste (PID 21356) usa 10.806 MB, e a memória livre é de 4,7 GB.";
        assert(rounded.WithMemoryFigures(clauses) == clauses
            && rounded.WithMemoryFigures("Com 4 GB livres, o NexoMemTeste usa 10.806 MB.") == "Com 4 GB livres, o NexoMemTeste usa 10.806 MB."
            && rounded.WithMemoryFigures("O NexoMemTeste usa 10 806 MB.") == "O NexoMemTeste usa 10 806 MB."
            && rounded.WithMemoryFigures("O NexoMemTeste usa 10\u00A0806\u00A0MB.") == "O NexoMemTeste usa 10\u00A0806\u00A0MB.",
            "Reteste memória: o \"livre\" de outra oração não conta, e o milhar com espaço é lido");

        // A memória livre do sistema com o mesmo valor do processo continua sem contar.
        var same = Observed(HolderReport(4813));
        assert(same.WithMemoryFigures("O NexoMemTeste está pesado e a memória livre está em 4,7 GB.").Contains("“NexoMemTeste” (PID 21356) segue com 4813 MB")
            && same.WithMemoryFigures("O NexoMemTeste usa 4,7 GB; o total instalado é 32 GB.") == "O NexoMemTeste usa 4,7 GB; o total instalado é 32 GB.",
            "Reteste memória: um valor de memória livre nunca conta, mesmo igual ao do processo; o do processo, ao lado do total, conta");
        assert(evidence.WithMemoryFigures("O disco C: tem 250 GB livres.") == "O disco C: tem 250 GB livres.",
            "Reteste memória: a resposta que não fala de memória nem do processo continua sem a linha");
    }

    private static async Task MemoryFlow(Store store, Account actor, Action<bool, string> assert)
    {
        var report = HolderReport(12055);
        var tools = new FlowTools((name, _) => name switch
        {
            "get_health_report" => WindowsTools.Ok(report),
            "diagnose_device" => WindowsTools.Ok("Memória livre: 4.7 GB de 31.9 GB\r\nDisco C:: 415.0 GB livres de 476.3 GB"),
            _ => throw new Exception("Ferramenta inesperada: " + name)
        });
        var answer = await Ask(store, actor, "o pc ta lento", tools, new CapturingModel([Text(MemoryAnswer)]));
        assert(answer.StartsWith(MemoryAnswer + "\n\nPelo monitor de saúde: “NexoMemTeste” (PID 21356) segue com 12055 MB", StringComparison.Ordinal)
            && answer.EndsWith(TurnFacts.Prefix + " nenhuma.", StringComparison.Ordinal),
            "Reteste memória (conversa 250f905d): a resposta aceita ganha a linha do monitor antes da linha de fatos");
    }

    private static void FileSearchOffer(Action<bool, string> assert)
    {
        // Sem relatório: a oferta é recusada em qualquer turno, como o 2º e o 3º do PC ("sim").
        var empty = new InvestigationEvidence();
        var offers = new[]
        {
            DiskOffer, DiskOfferAgain,
            "Quer que eu liste os maiores arquivos do disco?",
            "Posso verificar o que está ocupando mais espaço no seu disco.",
            "Gostaria de ajuda para encontrar as pastas mais pesadas?",
            "Vou procurar os arquivos que ocupam mais espaço no T:.",
            // Revisão do @qa no ceb7f40: o caminho não isenta a oferta do próprio Nexo, e o ":" depois da oferta não a quebra.
            "Posso ajudar a identificar os maiores arquivos e depois você confere em Configurações > Sistema > Armazenamento.",
            "Posso, se quiser: identificar os maiores arquivos."
        };
        var refused = offers.Where(offer => empty.ValidateStatement(offer, false) == InvestigationEvidence.FileSearchReason).Count();
        assert(refused == offers.Length,
            "Reteste disco (conversa ce60e9e9): a oferta de achar os maiores arquivos é recusada, nas variações do PC (" + refused + " de " + offers.Length + ")");
        var allowed = new[]
        {
            InvestigationEvidence.StorageGuidance,
            "O Nexo não lista arquivos; não posso identificar os maiores arquivos.",
            "Você pode ver os maiores arquivos em Configurações > Sistema > Armazenamento.",
            "Posso ajudar com outra dúvida sobre o disco?",
            "O disco T: está com 0,1 GB livres de 2 GB.",
            // O que o motivo pede: levar ao caminho do Armazenamento (@qa, item 1).
            "Posso orientar você a ver o que ocupa espaço em Configurações > Sistema > Armazenamento.",
            "Se quiser, posso explicar como ver os maiores arquivos em Configurações > Sistema > Armazenamento no Windows.",
            "Eu ajudo: abra Configurações > Sistema > Armazenamento para ver o que está ocupando espaço.",
            // Não é oferta (@qa, item 4).
            "Vou verificar o relatório: os maiores arquivos não aparecem nele.",
            "Vou ser direto: o relatório mostra só o espaço livre, e os maiores arquivos ficam no Armazenamento do Windows."
        };
        var passed = allowed.Where(text => empty.ValidateStatement(text, false) == null).Count();
        assert(passed == allowed.Length,
            "Reteste disco: o caminho do Armazenamento, a negação e a orientação sem oferta passam (" + passed + " de " + allowed.Length + ")");
        assert(InvestigationEvidence.FileSearchReason.Contains("Configurações > Sistema > Armazenamento") && InvestigationEvidence.FileSearchReason.Contains("No lugar"),
            "Reteste disco: o motivo diz ao modelo o que escrever no lugar da oferta");
    }

    private static async Task DiskOfferFlow(Store store, Account actor, Action<bool, string> assert)
    {
        var before = store.Data.Audit.Count;
        var tools = new FlowTools((name, _) => name switch
        {
            "get_health_report" => WindowsTools.Ok(FinalAnswerChecks.DiskLowReport(false)),
            "diagnose_device" => WindowsTools.Ok("Memória livre: 20 GB de 31.9 GB\r\nDisco C:: 415.0 GB livres de 476.3 GB\r\nDisco T:: 0.1 GB livres de 2 GB"),
            _ => throw new Exception("Ferramenta inesperada no cenário de disco: " + name)
        });
        var model = new CapturingModel([Text(DiskOffer), Text(DiskHonest)]);
        var answer = await Ask(store, actor, "estou sem espaço no disco", tools, model);
        assert((model.SystemMessages(1).LastOrDefault() ?? "").Contains("Motivo: " + InvestigationEvidence.FileSearchReason),
            "Reteste disco (conversa ce60e9e9): o modelo recebe o motivo da recusa da oferta");
        assert(answer.StartsWith(DiskHonest + "\n\n" + InvestigationEvidence.StorageGuidance + "\n\n" + TurnFacts.Prefix, StringComparison.Ordinal)
            && tools.Calls.All(ToolCatalog.IsReadOnly),
            "Reteste disco: a resposta reescrita ganha o caminho do Armazenamento, sem nenhuma alteração");
        var audit = store.Data.Audit.Skip(before).Select(a => a.Action).Where(a => a.StartsWith("Controle de execução", StringComparison.Ordinal)).ToArray();
        assert(audit.SequenceEqual(["Controle de execução recusou texto sem ferramenta: " + InvestigationEvidence.FileSearchReason]),
            "Reteste disco: a auditoria guarda só o motivo fixo, sem o texto do modelo");
    }

    private static void StorageLine(Action<bool, string> assert)
    {
        var active = Observed(FinalAnswerChecks.DiskLowReport(false));
        const string plain = "O disco T: está com 0,1 GB livres de 2 GB.";
        assert(active.WithStorageGuidance(plain) == plain + "\n\n" + InvestigationEvidence.StorageGuidance,
            "Reteste disco: com pouco espaço atual, a resposta sobre o disco ganha o caminho do Armazenamento");
        assert(active.WithStorageGuidance("O disco T: tem 0,1 GB livres. Quer ajuda com outra coisa?")
                == "O disco T: tem 0,1 GB livres.\n\n" + InvestigationEvidence.StorageGuidance + "\n\nQuer ajuda com outra coisa?",
            "Reteste disco: a pergunta ao usuário continua sendo a última coisa da resposta");
        const string already = "O disco T: tem 0,1 GB livres; veja o uso em Configurações › Sistema › Armazenamento.";
        assert(active.WithStorageGuidance(already) == already && active.WithStorageGuidance("A memória está normal.") == "A memória está normal.",
            "Reteste disco: a resposta que já tem o caminho, ou que não fala de disco, fica como está");
        const string audio = "Aumente o volume do alto-falante e confira a unidade de som.";
        const string letter = "O T: tem 0,1 GB livres.";
        assert(active.WithStorageGuidance(audio) == audio && active.WithStorageGuidance(letter) == letter + "\n\n" + InvestigationEvidence.StorageGuidance,
            "Reteste disco: \"volume\" e \"unidade\" sozinhos não trazem a linha; a letra da unidade traz");
        var closed = Observed(ClosedDiskReport());
        assert(closed.WithStorageGuidance(plain) == plain && new InvestigationEvidence().WithStorageGuidance(plain) == plain,
            "Reteste disco: sem pouco espaço atual (ocorrência encerrada ou sem relatório), a linha não entra");
    }

    private static void ClosedDisk(Action<bool, string> assert)
    {
        var report = ClosedDiskReport();
        var t = report.Split('\n').FirstOrDefault(line => line.StartsWith("Achado disk", StringComparison.Ordinal)) ?? "";
        assert((t.Contains("resolvida") || t.Contains("ocorrência histórica")) && t.Contains("Pouco espaço no volume T:"),
            "Reteste disco (passo 2.7): o relatório real tem o alerta do T: encerrado");
        var closed = Observed(report);
        var refused = new[]
        {
            ClosedDiskAnswer,
            "Libere espaço no disco.",
            "O disco está quase cheio, o que deixa o PC lento.",
            "Há pouco espaço no disco T:.",
            // Depois de "mas", o trecho é avaliado sozinho (@qa, no ceb7f40).
            "O alerta de pouco espaço não está ativo, mas libere espaço no disco."
        };
        var refusedCount = refused.Count(text => closed.ValidateStatement(text, false) == InvestigationEvidence.ClosedDiskReason);
        assert(refusedCount == refused.Length,
            "Reteste disco (conversa 2f762bbb): com a ocorrência de disco encerrada, mandar liberar espaço é recusado (" + refusedCount + " de " + refused.Length + ")");
        var allowed = new[]
        {
            "Se o disco encher, libere espaço.",
            "O alerta de pouco espaço no T: foi resolvido.",
            "O alerta de pouco espaço no T: está encerrado.",
            "O disco não está cheio.",
            "Está faltando espaço no disco?",
            "O disco T: estava quase cheio antes.",
            "O disco C: tem 87% livre.",
            // O estado do alerta e a negação depois do trecho (@qa, item 2).
            "O alerta de pouco espaço no T: não está mais ativo.",
            "O alerta de pouco espaço no T: não está ativo agora.",
            "A ocorrência de pouco espaço no T: não é atual.",
            "O T: não existe mais; o aviso de pouco espaço dele saiu do relatório.",
            "Liberar espaço não é necessário: o C: tem 87% livre.",
            // Revisão do @qa no ceb7f40.
            "Não há necessidade de liberar espaço.",
            "Abrir espaço no disco não deve ajudar agora.",
            // Revisão do @qa no 2cbe6c7: o passado com "teve" e a isenção depois do contraste.
            "O disco T: teve pouco espaço, mas a ocorrência foi encerrada.",
            "O T: teve pouco espaço, porém o alerta foi resolvido.",
            "O disco T: teve pouco espaço ontem.",
            "O disco T: está com pouco espaço, mas a ocorrência foi encerrada."
        };
        var passed = allowed.Count(text => closed.ValidateStatement(text, false) == null);
        assert(passed == allowed.Length,
            "Reteste disco: condição, resolvido, negação, pergunta e passado passam (" + passed + " de " + allowed.Length + ")");
        var active = Observed(FinalAnswerChecks.DiskLowReport(false));
        assert(active.ValidateStatement("Libere espaço no disco T:.", false) == null && new InvestigationEvidence().ValidateStatement("Libere espaço no disco.", false) == null,
            "Reteste disco: com pouco espaço atual, ou sem relatório, mandar liberar espaço passa");
        // Um volume ainda abaixo de 10% livre na leitura atual: a falta de espaço pode ser real, mesmo sem alerta aberto.
        var low = System.Text.RegularExpressions.Regex.Replace(report, @"^Volume C:_: [\d.,]+% livre\.", "Volume C:_: 4,0% livre.", System.Text.RegularExpressions.RegexOptions.Multiline);
        assert(low != report && Observed(low).ValidateStatement("Libere espaço no disco.", false) == null,
            "Reteste disco: com um volume abaixo de 10% livre no relatório, mandar liberar espaço passa");
    }

    // T: com pouco espaço atual e outro volume com alerta encerrado; o limite configurado (20%) deixa o T: acima de 10% (@qa, item 3).
    private static void ActiveAndClosedDisk(Action<bool, string> assert)
    {
        var report = MixedDiskReport();
        var lines = report.Split('\n').Where(line => line.StartsWith("Achado disk", StringComparison.Ordinal)).ToArray();
        assert(lines.Any(line => line.Contains("Pouco espaço no volume T:") && line.Contains("persiste")) && lines.Any(line => line.Contains("Pouco espaço no volume X:") && !line.Contains("persiste")),
            "Reteste disco: o relatório tem o T: atual e o X: encerrado");
        var evidence = Observed(report);
        assert(evidence.ValidateStatement("Libere espaço no disco T:.", false) == null,
            "Reteste disco: com o T: atual e outro volume encerrado, mandar liberar espaço no T: passa");
    }

    // Dois relatórios no mesmo turno: o segundo substitui o primeiro, inclusive o menor espaço livre (@qa, item 7).
    private static void ReportReplaced(Action<bool, string> assert)
    {
        var closed = ClosedDiskReport();
        var low = System.Text.RegularExpressions.Regex.Replace(closed, @"^Volume C:_: [\d.,]+% livre\.", "Volume C:_: 4,0% livre.", System.Text.RegularExpressions.RegexOptions.Multiline);
        var evidence = Observed(low);
        assert(evidence.ValidateStatement("Libere espaço no disco.", false) == null, "Reteste disco: no 1º relatório, um volume a 4% livre deixa passar");
        evidence.Observe("get_health_report", "h2", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(closed));
        assert(evidence.ValidateStatement("Libere espaço no disco.", false) == InvestigationEvidence.ClosedDiskReason,
            "Reteste disco: o 2º relatório do turno, com 87% livre, substitui o 1º; o espaço baixo de antes não vaza");
    }

    private static string MixedDiskReport()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-88b-mixed-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new OperationalStore(root);
            var now = DateTimeOffset.UtcNow;
            var thresholds = new HealthThresholds { DiskLowFreePercent = 20 };
            HealthSnapshot Sample(DateTimeOffset at, bool old) => new() { Time = at, CpuPercent = 12, MemoryUsedPercent = 40, CollectionStatus = "complete",
                Disks = old ? [new DiskHealth("C:\\", 435_000, 500_000), new DiskHealth("X:\\", 100, 2_000)] : [new DiskHealth("C:\\", 435_000, 500_000), new DiskHealth("T:\\", 300, 2_000)] };
            var before = new[] { Sample(now.AddMinutes(-30), true), Sample(now.AddMinutes(-29), true) };
            foreach (var sample in before) db.SaveSnapshot(sample);
            var oldFound = HealthRules.Evaluate(before, thresholds);
            foreach (var finding in oldFound) finding.Id += ":s1";
            var current = Enumerable.Range(0, 3).Select(i => Sample(now.AddSeconds(-30 * (2 - i)), false)).ToArray();
            foreach (var sample in current) db.SaveSnapshot(sample);
            var newFound = HealthRules.Evaluate(current, thresholds);
            foreach (var finding in newFound) finding.Id += ":s5";
            db.SaveFindings([.. oldFound, .. newFound]);
            return HealthReportPresentation.FromStore(db, thresholds);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task ClosedDiskFlow(Store store, Account actor, Action<bool, string> assert)
    {
        var report = ClosedDiskReport();
        var tools = new FlowTools((name, _) => name switch
        {
            "get_health_report" => WindowsTools.Ok(report),
            "diagnose_device" => WindowsTools.Ok("Memória livre: 20 GB de 31.9 GB\r\nDisco C:: 415.0 GB livres de 476.3 GB"),
            _ => throw new Exception("Ferramenta inesperada: " + name)
        });
        var model = new CapturingModel([Text(ClosedDiskAnswer), Text(ClosedDiskHonest)]);
        var answer = await Ask(store, actor, "meu pc ta lento", tools, model);
        assert(answer.StartsWith(ClosedDiskHonest, StringComparison.Ordinal) && (model.SystemMessages(1).LastOrDefault() ?? "").Contains("Motivo: " + InvestigationEvidence.ClosedDiskReason)
            && !answer.Contains(InvestigationEvidence.StorageGuidance),
            "Reteste disco (conversa 2f762bbb): a resposta que manda liberar espaço é recusada com o motivo, e a correção vira a resposta, sem o caminho do Armazenamento");
    }

    // Relatório real do HealthReportPresentation: NexoMemTeste PID 21356 subiu de 278 MB e segura "megabytes" agora.
    private static string HolderReport(long megabytes)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-88b-mem-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new OperationalStore(root);
            var now = DateTimeOffset.UtcNow;
            const long Mb = 1024 * 1024;
            var start = now.AddMinutes(-10);
            foreach (var i in Enumerable.Range(0, 5))
                db.SaveSnapshot(new HealthSnapshot
                {
                    Time = now.AddSeconds(-30 * (4 - i)), CpuPercent = 10, MemoryUsedPercent = 86, CollectionStatus = "complete",
                    Disks = [new DiskHealth("C:\\", 435_000, 500_000)],
                    Processes = [new ProcessHealth("NexoMemTeste", 21356, megabytes * Mb, 0, start)]
                });
            db.SaveFindings([new() { Id = $"process-memory-growing:21356:{start.UtcTicks}:s1", Time = now.AddMinutes(-3), Category = "memory", Severity = "warning",
                Problem = "Processo com memória em crescimento persistente", Evidence = $"NexoMemTeste PID 21356 iniciado em {start:yyyy-MM-dd HH:mm:ss zzz}: de 278 MB para {megabytes} MB em 5 leituras e 36 pontos da RAM",
                Cause = "Crescimento observado neste processo", Recommendation = "Verifique se o aplicativo está em uso e salve o trabalho antes de qualquer intervenção", Confidence = 0.7,
                MemoryGrowth = new("NexoMemTeste", 278 * Mb, megabytes * Mb) }]);
            return HealthReportPresentation.FromStore(db);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // O alerta do T: abriu com o VHDX quase cheio; depois o VHDX saiu e só o C:, com 87% livre, aparece nas leituras.
    internal static string ClosedDiskReport()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-88b-disk-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new OperationalStore(root);
            var now = DateTimeOffset.UtcNow;
            HealthSnapshot Sample(DateTimeOffset at, bool withVhdx) => new() { Time = at, CpuPercent = 12, MemoryUsedPercent = 40, CollectionStatus = "complete",
                Disks = withVhdx ? [new DiskHealth("C:\\", 435_000, 500_000), new DiskHealth("T:\\", 140, 2_000)] : [new DiskHealth("C:\\", 435_000, 500_000)] };
            var old = new[] { Sample(now.AddMinutes(-30), true), Sample(now.AddMinutes(-29), true) };
            foreach (var sample in old) db.SaveSnapshot(sample);
            var found = HealthRules.Evaluate(old, new HealthThresholds());
            foreach (var finding in found) finding.Id += ":s1";
            db.SaveFindings(found);
            foreach (var i in Enumerable.Range(0, 4)) db.SaveSnapshot(Sample(now.AddSeconds(-30 * (3 - i)), false));
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
