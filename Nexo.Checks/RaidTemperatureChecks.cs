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
            }
            await DiskHealthFlow(store, actor, assert);
            await TemperatureFlow(store, actor, assert);
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

    // Relatório como o HealthReportPresentation monta no PC: AMD-RAID e pendrive, temperatura sem PawnIO e o T: encerrado.
    private static string PcReport(TemperatureReading? temperature = null)
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
                StorageHealth = "healthy", Storage = new StorageReading("healthy", HardwareSensors.StorageSource, at, 2, [Usb, Raid])
            };
            var low = new[] { Sample(now.AddSeconds(-120), new("C:\\", 389_000, 500_000), new("T:\\", 140, 2_000)), Sample(now.AddSeconds(-90), new("C:\\", 389_000, 500_000), new("T:\\", 140, 2_000)) };
            var found = HealthRules.Evaluate(low, new HealthThresholds());
            foreach (var finding in found) finding.Id += ":s2";
            var later = new[] { Sample(now.AddSeconds(-30), new DiskHealth("C:\\", 389_000, 500_000)), Sample(now, new DiskHealth("C:\\", 389_000, 500_000)) };
            foreach (var sample in low.Concat(later)) db.SaveSnapshot(sample);
            db.SaveFindings(found);
            var report = HealthReportPresentation.FromStore(db);
            if (!report.Contains("resolvida")) throw new Exception("FAIL: o relatório do PC não trouxe o T: encerrado: " + report);
            return report;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static InvestigationEvidence Observed(string report)
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
