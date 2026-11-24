using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// Validação da #19 no PC com AMD-RAID, em 22/11/2026 (faff7e7), #135:
// 1. o array aparece como "Disco 1 AMD-RAID Array 1 (SSD, RAID): sem alerta…", sem dizer que o estado é o do volume RAID e que
//    o dos discos-membro não é visível pelo Windows Storage;
// 2. "como está a saúde dos meus discos?": 4 respostas recusadas pelo motivo do conteúdo de unidade (elas sugeriam "verificar
//    se há arquivos ou aplicativos que possam estar ocupando espaço" no T: já resolvido) e o relatório final sem a linha
//    "Discos físicos";
// 3. "qual a temperatura da CPU?": resposta sem consulta, sem o motivo (PawnIO) e mandando ao Gerenciador de Tarefas.
internal static class RaidTemperatureChecks
{
    private static readonly DateTimeOffset Read = DateTimeOffset.UtcNow;
    private static readonly PhysicalDiskReading Raid = new("1", "AMD-RAID Array 1", "SSD", "RAID", "Healthy", ["OK"], "healthy");
    private static readonly PhysicalDiskReading Usb = new("0", "USB DISK 2.0", "", "USB", "Healthy", ["OK"], "healthy");
    private static readonly PhysicalDiskReading Nvme = new("2", "Samsung SSD 980", "SSD", "NVMe", "Healthy", ["OK"], "healthy");
    private const string PawnIo = "O driver PawnIO não está instalado. A temperatura da CPU está indisponível.";
    // Textos reais do PC: as respostas recusadas começavam certas e sugeriam olhar o que ocupa espaço no T: encerrado.
    private const string DiskStart = "Os discos físicos estão sem alertas informados pelo Windows.";
    private static readonly string[] DiskAnswers =
    [
        DiskStart + " O volume T: teve um alerta de pouco espaço. Recomendo verificar se há arquivos ou aplicativos que possam estar ocupando espaço no T:.",
        DiskStart + " Para o T:, recomendo verificar se há arquivos ou aplicativos que possam estar ocupando espaço.",
        DiskStart + " No T:, vale verificar se há arquivos ou aplicativos que possam estar ocupando espaço.",
        DiskStart + " Sugiro verificar se há arquivos ou aplicativos que possam estar ocupando espaço no volume T:.",
    ];
    private const string TemperatureAnswer = "A temperatura da CPU não está disponível nas ferramentas que posso usar. Para verificar a temperatura da CPU, você pode usar o Gerenciador de Tarefas ou terceiros como o HWMonitor.";

    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        var previousAi = store.Data.AiEnabled;
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        store.Data.AiEnabled = true;
        try
        {
            foreach (var culture in new[] { "pt-BR", "en-US", "de-DE" })
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                RaidScope(assert, culture);
                ClosedDiskSuggestion(assert, culture);
                TaskManagerTemperature(assert, culture);
                Questions(assert, culture);
                QaEdges(assert, culture);
                RaidInAnswer(assert, culture);
                ClosedDiskUsageRetest(assert, culture);
                SensorDriverOffer(assert, culture);
                DiskUsageOffer(assert, culture);
                NoteBeforeOffer(assert, culture);
                OnlyTemperatureRequest(assert, culture);
            }
            await DiskHealthFlow(store, actor, assert);
            await TemperatureFlow(store, actor, assert);
            await TemperatureOnlyReport(store, actor, assert);
        }
        finally { store.Data.AiEnabled = previousAi; System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    }

    // 1. Com barramento RAID (nome ou número 8), o disco diz que o estado é o do volume RAID; os outros ficam iguais.
    private static void RaidScope(Action<bool, string> assert, string culture)
    {
        var numeric = Raid with { BusType = "8" };
        var shown = new[] { HealthReportPresentation.DescribeDisk(Raid), HealthReportPresentation.DescribeDisk(Raid, true), HealthReportPresentation.DescribeDisk(numeric, true) };
        var others = new[] { HealthReportPresentation.DescribeDisk(Usb), HealthReportPresentation.DescribeDisk(Nvme, true) };
        var report = PcReport().ReplaceLineEndings("\n");
        var raidLine = report.Split('\n').FirstOrDefault(line => line.StartsWith("Disco 1 ", StringComparison.Ordinal)) ?? "";
        assert(shown.All(text => text.Contains(HealthReportPresentation.RaidScope, StringComparison.Ordinal))
            && others.All(text => !text.Contains("RAID", StringComparison.Ordinal) && !text.Contains(HealthReportPresentation.RaidScope, StringComparison.Ordinal))
            && HealthReportPresentation.RaidScope.Contains("Volume RAID") && HealthReportPresentation.RaidScope.Contains("discos-membro") && HealthReportPresentation.RaidScope.Contains("Windows Storage")
            && raidLine.Contains(HealthReportPresentation.RaidScope, StringComparison.Ordinal)
            && HealthReportPresentation.DescribeDisk(Nvme) == "Disco 2 Samsung SSD 980 (SSD, NVMe): sem alerta informado pelo Windows; HealthStatus Healthy, OperationalStatus OK",
            "#135, " + culture + ": disco com barramento RAID (nome ou 8) diz que o estado é o do volume RAID e que os discos-membro não são visíveis; NVMe e USB ficam iguais · " + raidLine);
    }

    // 2. A sugestão de olhar o que ocupa espaço no T: encerrado é a falta de espaço apresentada como atual, não o conteúdo da unidade.
    private static void ClosedDiskSuggestion(Action<bool, string> assert, string culture)
    {
        var closed = Observed(PcReport());
        var wrong = DiskAnswers.Where(text => closed.ValidateStatement(text, false) != InvestigationEvidence.ClosedDiskReason).ToArray();
        // Sem nada encerrado, a mesma frase continua recusada (nenhuma trava afrouxa); a afirmação de conteúdo continua com o próprio motivo.
        var plain = new InvestigationEvidence();
        var stillRefused = DiskAnswers.All(text => plain.ValidateStatement(text, false) != null);
        var contents = closed.ValidateStatement("O D: não tem arquivos.", false);
        // Afirmação de conteúdo e falta de espaço em frases diferentes: o conteúdo continua com o próprio motivo.
        var mixed = closed.ValidateStatement("O D: não tem arquivos. Libere espaço no T:.", false);
        // Sem a ocorrência encerrada, a falta de espaço ativa não muda o motivo do conteúdo.
        var active = Observed(PcReport().Replace("resolvida", "persiste nas leituras atuais")).ValidateStatement(DiskAnswers[0], false);
        var honest = closed.ValidateStatement(DiskStart + " O alerta de pouco espaço no T: foi resolvido.", false);
        // A 2ª recusa cita a frase da sugestão.
        var repeated = closed.RepeatedGuidance(InvestigationEvidence.ClosedDiskReason, DiskAnswers[0]);
        assert(wrong.Length == 0 && stillRefused && contents == InvestigationEvidence.DriveContentsReason && honest == null
            && mixed == InvestigationEvidence.DriveContentsReason && active == InvestigationEvidence.DriveContentsReason
            && repeated.Contains("ocupando espaço no T:", StringComparison.Ordinal) && !repeated.Contains(DiskStart, StringComparison.Ordinal),
            "#135, " + culture + ": \"verificar se há arquivos… ocupando espaço\" no T: encerrado é recusado pelo motivo do disco encerrado; sem ocorrência encerrada continua recusado; conteúdo de unidade mantém o motivo próprio"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong.Select(text => closed.ValidateStatement(text, false))) : "") + " · conteúdo: " + contents + " · honesta: " + honest + " · mista: " + mixed + " · ativa: " + active);
    }

    // 3. O Gerenciador de Tarefas não mostra a temperatura da CPU; a explicação e o uso de memória passam.
    private static void TaskManagerTemperature(Action<bool, string> assert, string culture)
    {
        var evidence = new InvestigationEvidence();
        string[] refused =
        [
            TemperatureAnswer,
            TemperatureAnswer.Replace(". Para", ".\r\nPara"),
            "Veja a temperatura do processador no Gerenciador de Tarefas.",
            "Abra o Gerenciador de Tarefas e confira a temperatura da CPU na aba Desempenho.",
        ];
        string[] accepted =
        [
            "O Gerenciador de Tarefas não mostra a temperatura da CPU.",
            "Abra o Gerenciador de Tarefas para ver o uso de memória.",
            "A temperatura da CPU está indisponível porque o driver PawnIO não está instalado.",
            // Decisão da #135: programa de terceiros não é recusado; a linha do monitor garante a informação do Nexo.
            "Se quiser acompanhar a temperatura, o HWMonitor mostra os sensores da placa.",
        ];
        var wrong = refused.Where(text => evidence.ValidateStatement(text, false) != InvestigationEvidence.TemperatureSourceReason)
            .Concat(accepted.Where(text => evidence.ValidateStatement(text, false) != null)).ToArray();
        // Na 2ª recusa, o controle cita só a frase do Gerenciador de Tarefas.
        var repeated = evidence.RepeatedGuidance(InvestigationEvidence.TemperatureSourceReason, TemperatureAnswer);
        wrong = [.. wrong, .. repeated.Contains("“Para verificar a temperatura da CPU, você pode usar o Gerenciador de Tarefas ou terceiros como o HWMonitor.”", StringComparison.Ordinal)
            && !repeated.Contains("nas ferramentas que posso usar", StringComparison.Ordinal) ? [] : new[] { "2ª recusa: " + repeated }];
        assert(wrong.Length == 0 && InvestigationEvidence.TemperatureSourceReason.Contains("relatório de saúde"),
            "#135, " + culture + ": mandar ao Gerenciador de Tarefas para ver a temperatura da CPU é recusado; negação, memória, motivo e programa de terceiros passam"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // Casos de borda do @qa: afirmação de conteúdo continua com o próprio motivo; explicação genérica sobre o que ocupa espaço
    // passa com o T: encerrado, como antes da #135; GPU e memória no Gerenciador de Tarefas passam; a negação fora do trecho
    // não isenta; "quantos graus" conta como pergunta de temperatura e "aquecedor" não.
    private static void QaEdges(Action<bool, string> assert, string culture)
    {
        var closed = Observed(PcReport());
        var plain = new InvestigationEvidence();
        (string Text, string? Closed, string? Plain)[] cases =
        [
            ("Há arquivos grandes ocupando espaço no D:.", InvestigationEvidence.DriveContentsReason, InvestigationEvidence.DriveContentsReason),
            ("Há arquivos grandes ocupando espaço no D:, apague-os.", InvestigationEvidence.DriveContentsReason, InvestigationEvidence.DriveContentsReason),
            ("Recomendo verificar se o T: está com pouco espaço.", null, null),
            ("O T: tem pouco espaço, verifique se há arquivos ocupando espaço.", InvestigationEvidence.ClosedDiskReason, InvestigationEvidence.DriveContentsReason),
            ("Os arquivos temporários ocupam espaço no disco.", null, null),
            ("O Windows Update ocupa espaço em disco.", null, null),
            ("Verifique se algum aplicativo está consumindo espaço no C:.", InvestigationEvidence.ClosedDiskReason, null),
            ("Libere espaço no T:.", InvestigationEvidence.ClosedDiskReason, null),
            ("Posso identificar os maiores arquivos do T: para você.", InvestigationEvidence.FileSearchReason, InvestigationEvidence.FileSearchReason),
            ("Abra o Gerenciador de Tarefas para ver a temperatura da GPU.", null, null),
            ("No Gerenciador de Tarefas, na aba Desempenho, a GPU mostra a temperatura.", null, null),
            ("A temperatura está indisponível e o Gerenciador de Tarefas mostra o uso de memória.", null, null),
            ("Abra o Gerenciador de Tarefas para ver a temperatura, já que o Nexo não mostra esse dado.", InvestigationEvidence.TemperatureSourceReason, InvestigationEvidence.TemperatureSourceReason),
            ("Use o Gerenciador de Tarefas para conferir a temperatura do processador.", InvestigationEvidence.TemperatureSourceReason, InvestigationEvidence.TemperatureSourceReason),
            ("O Gerenciador de Tarefas não mostra a temperatura da CPU nem da GPU.", null, null),
        ];
        var wrong = cases.Where(c => closed.ValidateStatement(c.Text, false) != c.Closed || plain.ValidateStatement(c.Text, false) != c.Plain)
            .Select(c => c.Text + " → " + closed.ValidateStatement(c.Text, false) + " / " + plain.ValidateStatement(c.Text, false)).ToList();
        (string Text, bool Temperature)[] asks = [("quantos graus está a CPU?", true), ("quantos graus o processador", true), ("o aquecedor do quarto", false), ("girar a imagem 90 graus", false)];
        if (SupportIntake.AsksDiskHealth("meu disco está cheio, qual o estado dele?")) wrong.Add("pedido: disco cheio contou como saúde do disco");
        wrong.AddRange(asks.Where(a => SupportIntake.AsksTemperature(a.Text) != a.Temperature).Select(a => "pedido: " + a.Text));
        assert(wrong.Count == 0, "#135 (@qa), " + culture + ": afirmação de conteúdo mantém o motivo; explicação genérica do que ocupa espaço passa; GPU e memória no Gerenciador passam; \"quantos graus\" conta"
            + (wrong.Count > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // Pedidos sobre temperatura e saúde dos discos; falta de espaço e unidade sem saúde não contam.
    private static void Questions(Action<bool, string> assert, string culture)
    {
        (string Text, bool Temperature, bool Disks)[] cases =
        [
            ("qual a temperatura da CPU?", true, false),
            ("meu pc ta esquentando muito", true, false),
            ("o processador está muito quente?", true, false),
            ("como está a saúde dos meus discos?", false, true),
            ("o HD ta falhando?", false, true),
            ("qual o estado do SSD?", false, true),
            ("meu disco ta cheio", false, false),
            ("o que é o D:?", false, false),
            ("meu pc ta lento", false, false),
            ("como está a saúde do PC e a temperatura?", true, false),
        ];
        var wrong = cases.Where(c => SupportIntake.AsksTemperature(c.Text) != c.Temperature || SupportIntake.AsksDiskHealth(c.Text) != c.Disks).Select(c => c.Text).ToArray();
        assert(wrong.Length == 0, "#135, " + culture + ": pedido de temperatura e de saúde dos discos é reconhecido; falta de espaço e lentidão não"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // 2. O fluxo real: 4 respostas diferentes recusadas e o relatório final com a linha dos discos físicos e o limite do RAID.
    private static async Task DiskHealthFlow(Store store, Account actor, Action<bool, string> assert)
    {
        var report = PcReport();
        var called = new List<string>();
        var tools = new FlowTools((name, _) => { called.Add(name); return name == "get_health_report" ? WindowsTools.Ok(report) : throw new Exception("Ferramenta inesperada: " + name); });
        var model = new CapturingModel([.. DiskAnswers.Select(Text)]);
        var answer = await Ask(store, actor, [User("como está a saúde dos meus discos?")], tools, model);
        var reason = model.SystemMessages(1).LastOrDefault() ?? "";
        assert(called.FirstOrDefault() == "get_health_report" && answer.Contains("Discos físicos: sem alerta informado pelo Windows", StringComparison.Ordinal)
            && answer.Contains(HealthReportPresentation.RaidScope, StringComparison.Ordinal) && answer.Contains("AMD-RAID Array 1", StringComparison.Ordinal)
            && !answer.Contains("A IA não conseguiu", StringComparison.Ordinal) && !answer.Contains("ocupando espaço", StringComparison.Ordinal)
            && reason.Contains("Motivo: " + InvestigationEvidence.ClosedDiskReason, StringComparison.Ordinal),
            "#135: \"como está a saúde dos meus discos?\" consulta o relatório, a recusa usa o motivo do disco encerrado e o fim traz a linha dos discos físicos com o limite do RAID"
            + " · " + answer.Replace("\n", " ")[..Math.Min(answer.Length, 260)]);
        // Resposta aceita que não fala do estado dos discos ganha a linha do monitor; a que já fala fica como está.
        var bare = await Ask(store, actor, [User("como está a saúde dos meus discos?")], tools, new CapturingModel([Text("Não encontrei problemas.")]));
        var cited = await Ask(store, actor, [User("como está a saúde dos meus discos?")], tools, new CapturingModel([Text(DiskStart + " O AMD-RAID Array 1 está Healthy.")]));
        assert(bare.Contains("Pelo monitor de saúde: discos físicos sem alerta informado pelo Windows", StringComparison.Ordinal) && bare.Contains(HealthReportPresentation.RaidScope, StringComparison.Ordinal)
            && cited.StartsWith(DiskStart, StringComparison.Ordinal) && !cited.Contains("Pelo monitor de saúde: discos", StringComparison.Ordinal),
            "#135: a resposta aceita sobre a saúde dos discos ganha a linha do monitor quando não cita o estado; quando cita, fica como está · " + bare.Replace("\n", " "));
    }

    // 3. O fluxo real da temperatura: o Nexo consulta o relatório, recusa o Gerenciador de Tarefas e garante o motivo.
    private static async Task TemperatureFlow(Store store, Account actor, Action<bool, string> assert)
    {
        var called = new List<string>();
        var tools = new FlowTools((name, _) => { called.Add(name); return name == "get_health_report" ? WindowsTools.Ok(PcReport()) : throw new Exception("Ferramenta inesperada: " + name); });
        var model = new CapturingModel([Text(TemperatureAnswer), Text("A temperatura da CPU não está disponível no momento.")]);
        var answer = await Ask(store, actor, [User("qual a temperatura da CPU?")], tools, model);
        assert(called.FirstOrDefault() == "get_health_report" && (model.SystemMessages(1).LastOrDefault() ?? "").Contains("Motivo: " + InvestigationEvidence.TemperatureSourceReason, StringComparison.Ordinal)
            && answer.Contains("Pelo monitor de saúde: temperatura da CPU indisponível, O driver PawnIO não está instalado", StringComparison.Ordinal)
            && !answer.Contains("Gerenciador de Tarefas", StringComparison.Ordinal),
            "#135: \"qual a temperatura da CPU?\" consulta o relatório, recusa o Gerenciador de Tarefas e a resposta traz o motivo da indisponibilidade · " + answer.Replace("\n", " "));
        // Com leitura do sensor, a resposta que cita o valor fica como está; a que não cita ganha o valor.
        var readTools = new FlowTools((name, _) => name == "get_health_report" ? WindowsTools.Ok(PcReport(new TemperatureReading("read", 63.8, "Core (Tctl/Tdie)", HardwareSensors.TemperatureSource, Read)))
            : throw new Exception("Ferramenta inesperada: " + name));
        var cites = await Ask(store, actor, [User("qual a temperatura da CPU?")], readTools, new CapturingModel([Text("A CPU está em 64 °C.")]));
        var omits = await Ask(store, actor, [User("qual a temperatura da CPU?")], readTools, new CapturingModel([Text("A temperatura está normal.")]));
        assert(cites.StartsWith("A CPU está em 64 °C.", StringComparison.Ordinal) && !cites.Contains("Pelo monitor de saúde: temperatura", StringComparison.Ordinal)
            && omits.Contains("Pelo monitor de saúde: temperatura da CPU 64 °C, sensor Core", StringComparison.Ordinal),
            "#135: com o sensor lido, a resposta que cita o valor fica como está e a que não cita ganha o valor do monitor · " + omits.Replace("\n", " "));
        // Sem pergunta de temperatura, nada muda.
        var other = await Ask(store, actor, [User("o que é o D:?")], new FlowTools((name, _) => throw new Exception("Ferramenta inesperada: " + name)), new CapturingModel([Text("O D: é um pendrive.")]));
        assert(other.StartsWith("O D: é um pendrive.", StringComparison.Ordinal) && !other.Contains("Pelo monitor", StringComparison.Ordinal),
            "#135: pedido que não é de temperatura nem de saúde dos discos não ganha consulta nem linha do monitor");
    }

    // Revalidação no PC (afeefb7, 22/11, #135 reaberta): as duas respostas reais sobre a saúde dos discos resumiram com "sem
    // alertas informados" e não citaram o limite do RAID; o CitesStorage dava o estado por citado e a nota do RAID, que só ia
    // junto com a linha do estado, se perdia.
    private const string Answer21 = "Os discos físicos do seu sistema estão sem alertas informados pelo Windows, o que indica que eles estão em bom estado. Aqui está um resumo:\n\n"
        + "1. **Estado dos discos**: sem alertas.\n2. **Espaço livre**: o C: tem 77,9% livre.\n3. **Limitações do relatório**:\n"
        + "   - A temperatura da CPU não está disponível devido ao driver PawnIO não estar instalado.\n"
        + "   - O estado dos discos é informado pelo Windows Storage, que não é uma leitura SMART completa.\n\nSe precisar de mais alguma coisa, estou à disposição.";
    private const string Answer21bStart = "Os discos do seu computador estão sem alertas informados pelo Windows. Ambos os discos físicos (USB e SSD em RAID) estão em bom estado, com status \"Healthy\" e \"OperationalStatus OK\".\n\n"
        + "**Volume T:_**: Há um alerta de pouco espaço (7,0% livre), mas ele já foi resolvido em leituras recentes.";
    private const string Answer21bSuggestion = " Continue a monitorar esse volume… pode ser útil verificar o uso do disco com o gerenciador de armazenamento do Windows ou revisar o conteúdo dos volumes.";

    private static void RaidInAnswer(Action<bool, string> assert, string culture)
    {
        var disks = Observed(PcReport()); disks.AsksDiskHealth = true;
        var wrong = new List<string>();
        foreach (var text in new[] { Answer21, Answer21bStart, Answer21.Replace("\n", "\r\n") })
        {
            var shown = disks.WithSensorFigures(text).ReplaceLineEndings("\n");
            if (Occurrences(shown, HealthReportPresentation.RaidScope) != 1 || shown.Contains("Pelo monitor de saúde: discos físicos", StringComparison.Ordinal)
                || !shown.Contains("“AMD-RAID Array 1”", StringComparison.Ordinal) || shown.Contains("“USB DISK 2.0”", StringComparison.Ordinal) || !shown.StartsWith(text.ReplaceLineEndings("\n")[..40], StringComparison.Ordinal))
                wrong.Add("sem o limite: " + shown.Replace("\n", " "));
        }
        // A resposta que já cita o limite fica como está.
        const string cites = "O AMD-RAID Array 1 está sem alertas informados pelo Windows. Por ser um volume RAID, o estado dos discos-membro não aparece no Windows.";
        if (disks.WithSensorFigures(cites) != cites) wrong.Add("já citava: " + disks.WithSensorFigures(cites));
        const string driver = "O AMD-RAID Array 1 está Healthy; o estado vem do driver do controlador RAID.";
        if (disks.WithSensorFigures(driver) != driver) wrong.Add("RAID e driver: " + disks.WithSensorFigures(driver));
        // "Volume RAID" sozinho e o driver do RAID atualizado não dizem o limite: a nota entra (@qa).
        foreach (var loose in new[] { "O volume RAID AMD-RAID Array 1 está sem alertas informados pelo Windows.", "O driver do RAID está atualizado e os discos estão sem alertas informados pelo Windows." })
            if (Occurrences(disks.WithSensorFigures(loose), HealthReportPresentation.RaidScope) != 1) wrong.Add("frouxo: " + disks.WithSensorFigures(loose));
        // Sem RAID no relatório, o resumo genérico sem alerta continua bastando.
        var plain = Observed(PcReport(storage: [Usb, Nvme])); plain.AsksDiskHealth = true;
        if (plain.WithSensorFigures(Answer21) != Answer21) wrong.Add("sem RAID: " + plain.WithSensorFigures(Answer21));
        // Com disco em alerta, "sem alertas informados" sozinho não cita o estado de quem tem alerta: a linha do monitor entra.
        var warning = Raid with { Health = "Warning", OperationalStatus = ["Degraded"], State = "warning" };
        var alerted = Observed(PcReport(storage: [Usb, warning], storageStatus: "warning")); alerted.AsksDiskHealth = true;
        var alertShown = alerted.WithSensorFigures("Os discos estão sem alertas informados pelo Windows.");
        if (!alertShown.Contains("Pelo monitor de saúde: discos físicos", StringComparison.Ordinal) || Occurrences(alertShown, HealthReportPresentation.RaidScope) != 1)
            wrong.Add("alerta: " + alertShown.Replace("\n", " "));
        // Nomeando o disco com alerta, a linha do estado não entra; o limite do RAID, sim.
        var named = alerted.WithSensorFigures("O AMD-RAID Array 1 está com alerta Warning informado pelo Windows.");
        if (named.Contains("Pelo monitor de saúde: discos físicos", StringComparison.Ordinal) || Occurrences(named, HealthReportPresentation.RaidScope) != 1) wrong.Add("nomeado: " + named);
        // Pedido de temperatura: o limite só entra quando a resposta fala dos discos.
        var heat = Observed(PcReport()); heat.AsksTemperature = true;
        const string onlyHeat = "A temperatura da CPU está indisponível porque o driver PawnIO não está instalado.";
        if (heat.WithSensorFigures(onlyHeat) != onlyHeat) wrong.Add("temperatura: " + heat.WithSensorFigures(onlyHeat));
        if (Occurrences(heat.WithSensorFigures(onlyHeat + " Os discos estão sem alertas informados pelo Windows."), HealthReportPresentation.RaidScope) != 1) wrong.Add("temperatura com discos");
        // Pedido de outro assunto: nada muda.
        var other = Observed(PcReport());
        if (other.WithSensorFigures(Answer21) != Answer21) wrong.Add("outro pedido");
        assert(wrong.Count == 0, "#135 (PC afeefb7), " + culture + ": com RAID, a resposta sobre os discos que não cita o limite ganha a nota do RAID uma vez, sem repetir a linha do estado; sem RAID, nada muda; com alerta, o resumo genérico não basta"
            + (wrong.Count > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // "verificar o uso do disco… ou revisar o conteúdo dos volumes" sobre o T: encerrado passou na revalidação: é sugestão de
    // olhar o espaço de um disco cuja ocorrência acabou. Explicações genéricas continuam aceitas.
    private static void ClosedDiskUsageRetest(Action<bool, string> assert, string culture)
    {
        var closed = Observed(PcReport());
        var plain = new InvestigationEvidence();
        (string Text, string? Closed, string? Plain)[] cases =
        [
            (Answer21bStart + Answer21bSuggestion, InvestigationEvidence.ClosedDiskReason, null),
            (Answer21bStart, null, null),
            ("Vale verificar o uso do disco no T:.", InvestigationEvidence.ClosedDiskReason, null),
            ("Revise o conteúdo dos volumes para liberar espaço.", InvestigationEvidence.ClosedDiskReason, null),
            ("Revise o conteúdo dos volumes no T:.", InvestigationEvidence.ClosedDiskReason, null),
            ("O uso do disco aparece em Configurações > Sistema > Armazenamento.", null, null),
            ("Os arquivos temporários ocupam espaço no disco.", null, null),
            ("O alerta do T: foi resolvido; não é preciso verificar o uso do disco.", null, null),
            // A orientação do Armazenamento continua aceita; a vírgula depois da dispensa e o "não deixe de" mandam verificar (@qa).
            ("Para ver o uso do disco, abra Configurações > Sistema > Armazenamento.", null, null),
            ("Não é preciso, verifique o uso do disco no T:.", InvestigationEvidence.ClosedDiskReason, null),
            ("Não deixe de verificar o uso do disco no T:.", InvestigationEvidence.ClosedDiskReason, null),
        ];
        var wrong = cases.Where(c => closed.ValidateStatement(c.Text, false) != c.Closed || plain.ValidateStatement(c.Text, false) != c.Plain)
            .Select(c => c.Text + " → " + closed.ValidateStatement(c.Text, false) + " / " + plain.ValidateStatement(c.Text, false)).ToList();
        var repeated = closed.RepeatedGuidance(InvestigationEvidence.ClosedDiskReason, Answer21bStart + Answer21bSuggestion);
        if (!repeated.Contains("verificar o uso do disco", StringComparison.Ordinal) || repeated.Contains("já foi resolvido", StringComparison.Ordinal)) wrong.Add("2ª recusa: " + repeated);
        assert(wrong.Count == 0, "#135 (PC afeefb7), " + culture + ": sugerir verificar o uso do disco ou revisar o conteúdo dos volumes com o T: encerrado é recusado pelo motivo do disco encerrado; o estado resolvido e explicações genéricas passam"
            + (wrong.Count > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // Na revalidação, a resposta de temperatura ofereceu "posso ajudar a verificar se o driver está instalado ou buscar
    // informações adicionais sobre ele": nenhuma ferramenta confere o PawnIO.
    private static void SensorDriverOffer(Action<bool, string> assert, string culture)
    {
        var heat = Observed(PcReport()); heat.AsksTemperature = true;
        var plain = Observed(PcReport());
        var gpu = Observed(PcReport()); gpu.AsksTemperature = true; gpu.UserRequest = "minha placa de vídeo está esquentando, qual a temperatura?";
        const string real = "A temperatura da CPU está indisponível no momento, pois o driver PawnIO não está instalado. Para obter essa informação, seria necessário instalar ou verificar o status desse driver. "
            + "Se você deseja, posso ajudar a verificar se o driver está instalado ou buscar informações adicionais sobre ele.";
        (InvestigationEvidence Evidence, string Text, string? Expected)[] cases =
        [
            (heat, real, InvestigationEvidence.SensorDriverOfferReason),
            (heat, real.Replace(". Se", ".\r\nSe"), InvestigationEvidence.SensorDriverOfferReason),
            (heat, "Quer que eu instale o driver para você?", InvestigationEvidence.SensorDriverOfferReason),
            (plain, "Posso verificar se o PawnIO está instalado.", InvestigationEvidence.SensorDriverOfferReason),
            (plain, "Deseja que eu procure o instalador do PawnIO?", InvestigationEvidence.SensorDriverOfferReason),
            // A resposta real da 2.3 e a explicação passam.
            (heat, "A temperatura do processador não está disponível no momento, pois o driver PawnIO não está instalado. Para obter essa informação, seria necessário instalar o driver apropriado. Se você precisar de mais ajuda com isso, posso orientar você.", null),
            (heat, "Posso explicar como instalar o driver PawnIO, se você decidir instalá-lo.", null),
            (heat, "Posso consultar de novo o relatório de saúde para ver a temperatura.", null),
            (heat, "Não posso verificar se o driver está instalado.", null),
            // Drivers que o Nexo consulta (dispositivo, vídeo, impressora) não são o driver do sensor.
            (heat, "Posso verificar o driver da placa de vídeo, se quiser.", null),
            (plain, "Posso verificar se o driver da impressora está instalado.", null),
            (plain, "Posso verificar o driver do dispositivo com erro.", null),
            // Sem pedido de temperatura, "o driver" sozinho não é o do sensor.
            (plain, "Posso verificar se o driver está instalado.", null),
            // O "não" depois do driver não nega a oferta; o driver longe do verbo não é o objeto dela; a temperatura da placa de
            // vídeo faz "o driver" ser o dela (@qa).
            (heat, "Posso verificar por que o driver PawnIO não está instalado.", InvestigationEvidence.SensorDriverOfferReason),
            (heat, "Se quiser, posso verificar a temperatura novamente quando o driver PawnIO for instalado.", null),
            (gpu, "Posso verificar se o driver está atualizado.", null),
            (gpu, "Posso verificar se o PawnIO está instalado.", InvestigationEvidence.SensorDriverOfferReason),
            (plain, "Posso instalar o driver PawnIO para você.", InvestigationEvidence.SensorDriverOfferReason),
            (plain, "Posso verificar o driver da impressora, que não depende do PawnIO.", null),
        ];
        var wrong = cases.Where(c => c.Evidence.ValidateStatement(c.Text, false) != c.Expected).Select(c => c.Text + " → " + c.Evidence.ValidateStatement(c.Text, false)).ToList();
        var repeated = heat.RepeatedGuidance(InvestigationEvidence.SensorDriverOfferReason, real);
        if (!repeated.Contains("posso ajudar a verificar se o driver está instalado", StringComparison.Ordinal) || repeated.Contains("Para obter", StringComparison.Ordinal)) wrong.Add("2ª recusa: " + repeated);
        assert(wrong.Count == 0 && InvestigationEvidence.SensorDriverOfferReason.Contains("PawnIO") && InvestigationEvidence.SensorDriverOfferReason.Contains("decisão do usuário"),
            "#135 (PC afeefb7), " + culture + ": oferecer verificar, instalar ou procurar o driver do sensor (PawnIO) é recusado; explicação, nova consulta e drivers que o Nexo consulta passam"
            + (wrong.Count > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // Repetição no PC da 5b48461 (22/11, #135), 1b: a resposta aceita terminava com "Se quiser, posso ajudar a verificar o uso
    // do disco ou identificar quais processos estão utilizando mais espaço" e a nota do RAID entrou antes de "Deseja que eu faça
    // isso?". Na pergunta 2, o relatório da 3ª recusa idêntica trouxe as ocorrências e o espaço livre num pedido só de temperatura.
    private const string Answer1b = "O estado dos discos físicos está estável e sem alertas informados pelo sistema. O volume C: também tem **77,7% de espaço livre**. "
        + "Se quiser, posso ajudar a verificar o uso do disco ou identificar quais processos estão utilizando mais espaço. Deseja que eu faça isso?";
    private const string Answer2 = "A temperatura da CPU está indisponível no momento, pois o driver PawnIO não está instalado. Se você deseja, posso ajudar a verificar se há um driver disponível para instalar.";

    private static void DiskUsageOffer(Action<bool, string> assert, string culture)
    {
        var closed = Observed(PcReport());
        var plain = new InvestigationEvidence();
        (string Text, string? Expected)[] cases =
        [
            (Answer1b, InvestigationEvidence.FileSearchReason),
            (Answer1b.Replace(". Se", ".\r\nSe"), InvestigationEvidence.FileSearchReason),
            ("Se quiser, posso ajudar a identificar quais processos estão utilizando mais espaço.", InvestigationEvidence.FileSearchReason),
            ("Posso verificar o uso do disco.", InvestigationEvidence.FileSearchReason),
            ("Posso analisar o uso do espaço em disco no C:.", InvestigationEvidence.FileSearchReason),
            ("Deseja que eu verifique o uso do T:?", InvestigationEvidence.FileSearchReason),
            ("Quer que eu identifique quais programas estão ocupando mais espaço?", InvestigationEvidence.FileSearchReason),
            ("Posso verificar quais aplicativos consomem mais espaço.", InvestigationEvidence.FileSearchReason),
            ("Posso ver o uso de armazenamento do volume C:.", InvestigationEvidence.FileSearchReason),
            // @qa: outros verbos, o uso coordenado, a utilização, o consumo e "o que está consumindo espaço".
            ("Posso ajudar a revisar o uso do disco.", InvestigationEvidence.FileSearchReason),
            ("Posso checar o uso do disco.", InvestigationEvidence.FileSearchReason),
            ("Posso conferir o uso do disco C:.", InvestigationEvidence.FileSearchReason),
            ("Posso consultar o uso do disco.", InvestigationEvidence.FileSearchReason),
            ("Posso avaliar o uso do armazenamento.", InvestigationEvidence.FileSearchReason),
            ("Posso verificar o uso da CPU e do disco.", InvestigationEvidence.FileSearchReason),
            ("Posso verificar a utilização do disco.", InvestigationEvidence.FileSearchReason),
            ("Posso analisar o consumo de espaço no disco.", InvestigationEvidence.FileSearchReason),
            ("Posso ajudar a identificar o que está consumindo espaço.", InvestigationEvidence.FileSearchReason),
            ("Posso verificar o que está usando o espaço do disco.", InvestigationEvidence.FileSearchReason),
            // Negação, memória e CPU, estado e saúde do disco, orientação do Armazenamento e o ":" passam.
            ("Não posso verificar o uso do disco.", null),
            ("Posso verificar o uso de memória.", null),
            ("Posso verificar o uso da CPU.", null),
            ("Posso verificar o estado do disco.", null),
            ("Posso consultar a saúde do disco de novo.", null),
            ("Se quiser, posso orientar você a ver o uso do disco em Configurações > Sistema > Armazenamento.", null),
            ("Vou verificar o relatório: o uso do disco não aparece nele.", null),
            ("Posso verificar se algum processo está usando muito o processador.", null),
            ("O C: tem 77,7% livre e o uso do disco está normal.", null),
            ("Posso te mostrar como ver o uso do disco em Configurações > Sistema > Armazenamento.", null),
            ("Quer que eu explique como verificar o uso do disco em Configurações > Sistema > Armazenamento?", null),
            ("Posso consultar o relatório de saúde de novo para ver o estado dos discos.", null),
            ("Posso verificar o uso da CPU e da memória.", null),
            ("Quer que eu verifique a temperatura de novo?", null),
            ("Posso verificar o espaço livre do C:.", null),
        ];
        var wrong = cases.Where(c => plain.ValidateStatement(c.Text, false) != c.Expected || c.Expected != null && closed.ValidateStatement(c.Text, false) == null)
            .Select(c => c.Text + " → " + plain.ValidateStatement(c.Text, false) + " / " + closed.ValidateStatement(c.Text, false)).ToList();
        var repeated = plain.RepeatedGuidance(InvestigationEvidence.FileSearchReason, Answer1b);
        if (!repeated.Contains("verificar o uso do disco", StringComparison.Ordinal) || repeated.Contains("77,7%", StringComparison.Ordinal)) wrong.Add("2ª recusa: " + repeated);
        assert(wrong.Count == 0 && InvestigationEvidence.FileSearchReason.Contains("o uso do disco") && InvestigationEvidence.FileSearchReason.Contains("processos que usam espaço"),
            "#135 (PC 5b48461), " + culture + ": oferecer verificar o uso do disco ou os processos que usam espaço é recusado pelo motivo da busca; memória, CPU, estado do disco, negação e Armazenamento passam"
            + (wrong.Count > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // A nota entra antes da oferta que precede a pergunta final; sem oferta, como antes.
    private static void NoteBeforeOffer(Action<bool, string> assert, string culture)
    {
        var disks = Observed(PcReport()); disks.AsksDiskHealth = true;
        const string start = "Os discos estão sem alertas informados pelo Windows.";
        var note = "Pelo monitor de saúde: “AMD-RAID Array 1” — " + HealthReportPresentation.RaidScope + ".";
        (string Text, string Expected)[] cases =
        [
            (start + " Se quiser, posso consultar o relatório de novo. Deseja que eu faça isso?", start + "\n\n" + note + "\n\nSe quiser, posso consultar o relatório de novo. Deseja que eu faça isso?"),
            (start + "\n\nSe quiser, posso consultar o relatório de novo.\n\nDeseja que eu faça isso?", start + "\n\n" + note + "\n\nSe quiser, posso consultar o relatório de novo.\n\nDeseja que eu faça isso?"),
            (start + " Se precisar, posso consultar de novo.", start + "\n\n" + note + "\n\nSe precisar, posso consultar de novo."),
            (start + " Caso queira, posso repetir a consulta. Quer?", start + "\n\n" + note + "\n\nCaso queira, posso repetir a consulta. Quer?"),
            (start + " Se precisar de mais alguma coisa, estou à disposição.", start + "\n\n" + note + "\n\nSe precisar de mais alguma coisa, estou à disposição."),
            // Sem oferta: a pergunta final continua a última coisa, e a frase condicional que orienta não é oferta.
            (start + " Quer saber mais?", start + "\n\n" + note + "\n\nQuer saber mais?"),
            (start + " Se a lentidão continuar, reinicie o PC.", start + " Se a lentidão continuar, reinicie o PC.\n\n" + note),
            (start, start + "\n\n" + note),
            // @qa: frase declarativa com "posso … que" não é oferta, e a resposta toda de fechamento não leva a nota para o topo.
            ("Posso confirmar que os discos estão sem alertas informados pelo Windows.", "Posso confirmar que os discos estão sem alertas informados pelo Windows.\n\n" + note),
            (start + " Posso afirmar que não há falhas.", start + " Posso afirmar que não há falhas.\n\n" + note),
            ("Se quiser, posso consultar de novo: os discos estão sem alertas informados pelo Windows.", "Se quiser, posso consultar de novo: os discos estão sem alertas informados pelo Windows.\n\n" + note),
            ("Se quiser, posso consultar de novo: os discos estão sem alertas informados pelo Windows. Quer?", "Se quiser, posso consultar de novo: os discos estão sem alertas informados pelo Windows.\n\n" + note + "\n\nQuer?"),
        ];
        var wrong = cases.Select(c => (c, Shown: disks.WithSensorFigures(c.Text).ReplaceLineEndings("\n"))).Where(x => x.Shown != x.c.Expected)
            .Select(x => x.c.Text.Replace("\n", "⏎") + " → " + x.Shown.Replace("\n", "⏎")).ToList();
        assert(wrong.Count == 0, "#135 (PC 5b48461), " + culture + ": a nota do monitor entra antes da oferta ao usuário que precede a pergunta final, e a oferta e a pergunta ficam juntas"
            + (wrong.Count > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    private static void OnlyTemperatureRequest(Action<bool, string> assert, string culture)
    {
        (string Text, bool Only)[] cases =
        [
            ("qual a temperatura da CPU?", true),
            ("quantos graus está o processador?", true),
            ("o processador está muito quente?", true),
            ("meu pc ta esquentando muito", true),
            ("meu pc ta esquentando e desligando sozinho", false),
            ("o pc esquenta e dá tela azul", false),
            ("meu pc ta lento e esquentando", false),
            ("como está a saúde dos meus discos e a temperatura?", false),
            ("como está a saúde do PC e a temperatura?", false),
            ("meu disco ta cheio", false),
        ];
        var wrong = cases.Where(c => SupportIntake.AsksOnlyTemperature(c.Text) != c.Only).Select(c => c.Text).ToArray();
        assert(wrong.Length == 0, "#135 (PC 5b48461), " + culture + ": pedido só de temperatura é reconhecido; com outro sintoma, lentidão, discos ou saúde geral, não"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // Pergunta 2 do PC: a mesma oferta recusada 3 vezes encerra com o relatório; só a linha da temperatura do relatório de saúde.
    private static async Task TemperatureOnlyReport(Store store, Account actor, Action<bool, string> assert)
    {
        var tools = new FlowTools((name, _) => name == "get_health_report" ? WindowsTools.Ok(PcReport()) : throw new Exception("Ferramenta inesperada: " + name));
        var only = await Ask(store, actor, [User("qual a temperatura da CPU?")], tools, new CapturingModel([Text(Answer2), Text(Answer2), Text(Answer2)]));
        var general = await Ask(store, actor, [User("como está a saúde do PC e a temperatura?")], tools, new CapturingModel([Text(Answer2), Text(Answer2), Text(Answer2)]));
        // @qa: uma ocorrência ATIVA continua no relatório do pedido só de temperatura (aqui, o T: com pouco espaço ainda presente).
        var activeReport = PcReport().Replace("resolvida: o volume não está mais presente em 2 leituras seguidas", "persiste nas leituras atuais", StringComparison.Ordinal);
        var activeTools = new FlowTools((name, _) => name == "get_health_report" ? WindowsTools.Ok(activeReport) : throw new Exception("Ferramenta inesperada: " + name));
        var active = await Ask(store, actor, [User("qual a temperatura da CPU?")], activeTools, new CapturingModel([Text(Answer2), Text(Answer2), Text(Answer2)]));
        assert(only.Contains("Temperatura da CPU, pelo monitor de saúde: indisponível", StringComparison.Ordinal) && only.Contains("PawnIO", StringComparison.Ordinal)
            && !only.Contains("Espaço livre por volume", StringComparison.Ordinal) && !only.Contains("monitor de saúde não mostra ocorrência", StringComparison.Ordinal)
            && !only.Contains("Ocorrência", StringComparison.Ordinal) && !only.Contains("posso ajudar a verificar", StringComparison.Ordinal)
            && general.Contains("Espaço livre por volume", StringComparison.Ordinal) && general.Contains("Temperatura da CPU, pelo monitor de saúde", StringComparison.Ordinal)
            && activeReport.Contains("persiste nas leituras atuais", StringComparison.Ordinal) && active.Contains("Ocorrência ativa no monitor de saúde", StringComparison.Ordinal)
            && active.Contains("Pouco espaço no volume T:", StringComparison.Ordinal) && active.Contains("Temperatura da CPU, pelo monitor de saúde", StringComparison.Ordinal)
            && !active.Contains("Espaço livre por volume", StringComparison.Ordinal),
            "#135 (PC 5b48461): no pedido só de temperatura, o relatório da 3ª recusa idêntica traz só a linha da temperatura do relatório de saúde, mais a ocorrência ativa se houver; no pedido da saúde geral, o relatório inteiro"
            + " · " + only.Replace("\n", " ") + " ‖ " + general.Replace("\n", " ")[..Math.Min(general.Length, 200)] + " ‖ " + active.Replace("\n", " "));
    }

    private static int Occurrences(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

    // Relatório como o HealthReportPresentation monta no PC: AMD-RAID e pendrive, temperatura sem PawnIO e o T: encerrado.
    internal static string PcReport(TemperatureReading? temperature = null, PhysicalDiskReading[]? storage = null, string storageStatus = "healthy")
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-raid-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new OperationalStore(root);
            var now = DateTimeOffset.UtcNow;
            HealthSnapshot Sample(DateTimeOffset at, params DiskHealth[] disks) => new()
            {
                Time = at, CpuPercent = 12, MemoryUsedPercent = 40, CollectionStatus = "complete", Disks = [.. disks],
                CpuTemperature = temperature ?? new TemperatureReading("no_source", Time: at, Detail: PawnIo),
                StorageHealth = storageStatus, Storage = new StorageReading(storageStatus, HardwareSensors.StorageSource, at, (storage ?? [Usb, Raid]).Length, [.. storage ?? [Usb, Raid]])
            };
            var low = new[] { Sample(now.AddSeconds(-120), new("C:\\", 389_000, 500_000), new("T:\\", 140, 2_000)), Sample(now.AddSeconds(-90), new("C:\\", 389_000, 500_000), new("T:\\", 140, 2_000)) };
            var found = HealthRules.Evaluate(low, new HealthThresholds());
            foreach (var finding in found) finding.Id += ":s2";
            var later = new[] { Sample(now.AddSeconds(-30), new DiskHealth("C:\\", 389_000, 500_000)), Sample(now, new DiskHealth("C:\\", 389_000, 500_000)) };
            foreach (var sample in low.Concat(later)) db.SaveSnapshot(sample);
            db.SaveFindings(found);
            var report = HealthReportPresentation.FromStore(db, MachineSensors.Off);
            if (!report.Contains("resolvida")) throw new Exception("FAIL: o relatório do PC não trouxe o T: encerrado: " + report);
            return report;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal static InvestigationEvidence Observed(string report)
    {
        var evidence = new InvestigationEvidence();
        evidence.Observe("get_health_report", "h", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(report));
        return evidence;
    }

    private static Entry User(string text) => new() { Role = "user", Text = text };

    private static async Task<string> Ask(Store store, Account actor, Entry[] entries, IAgentTools tools, CapturingModel model)
    {
        using var http = new HttpClient(model);
        var conversation = new Conversation { UserId = actor.Id, Entries = [.. entries] };
        var answer = await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
        if (!model.Consumed) throw new Exception("FAIL: o roteiro do modelo não foi consumido em: " + entries[^1].Text);
        return answer.ReplaceLineEndings("\n");
    }

    private static string Text(string content) => JsonSerializer.Serialize(new { message = new { role = "assistant", content } });

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
