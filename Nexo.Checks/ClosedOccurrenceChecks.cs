using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// Validação da #135 no PC (b8213bd, 22/11/2026, 23:05–23:10), "como está a saúde dos meus discos?", #139. As respostas finais
// foram aceitas sem recusa, embora o relatório só trouxesse as ocorrências encerradas:
// 1a: "Foi identificado um volume (T:) com pouco espaço livre, mas essa situação já foi resolvida, e o espaço está normalizando"
//     (o T: já não existia) e "Alguns processos estão usando mais memória ao longo do tempo…";
// 1b: "Processos com crescimento de memória: … Recomendo verificar se esses processos estão funcionando corretamente…".
internal static class ClosedOccurrenceChecks
{
    private const string TrendSentence = "**Pouco espaço no volume T:** Foi identificado um volume (T:) com pouco espaço livre, mas essa situação já foi resolvida, e o espaço está normalizando.";
    private const string MemorySentence = "**Crescimento de memória em processos**: Alguns processos estão usando mais memória ao longo do tempo…";
    private const string Answer1bHeading = "**Processos com crescimento de memória:** Alguns processos estão aumentando o uso de memória.";
    private const string Answer1bAdvice = "Recomendo verificar se esses processos estão funcionando corretamente…";
    private const string DiskStart = "Os discos físicos estão sem alertas. **Disco 1**: \"AMD-RAID Array 1\" (SSD, RAID), também sem alertas, com status \"Healthy\" e \"OperationalStatus OK\". Além disso, o volume C: tem **77,7% de espaço livre**.";
    private const string Closing = "Se você notar lentidão, travamentos ou problemas específicos com o armazenamento, posso ajudar a investigar mais a fundo.";
    private static readonly string Answer1a = DiskStart + "\n- " + MemorySentence + "\n- " + TrendSentence + "\n" + Closing;

    private const string SystemClosed = "Achado memory (warning), 2026-11-22 22:40 _00:00, resolvida: 3 leituras seguidas sem a condição: Memória em crescimento persistente. Evidência: De 40% para 62% em três leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n";
    private const string ProcessClosed = "Achado memory (warning), 2026-11-22 22:41 _00:00, resolvida: 3 leituras seguidas sem a condição: Processo com memória em crescimento persistente. Evidência: NexoMemTeste-toca PID 1732 iniciado em 2026-11-22 21:52:08 _00:00: de 2072 MB para 7192 MB em 5 leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n";
    private const string ProcessActive = "Achado memory (warning), 2026-11-22 22:58 _00:00, persiste nas leituras atuais: Processo com memória em crescimento persistente. Evidência: Navegador PID 4410 iniciado em 2026-11-22 22:00:00 _00:00: de 1200 MB para 6200 MB em 4 leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n";
    private static readonly string ProcessStable = "Achado memory (info), 2026-11-22 22:58 _00:00, " + HealthOccurrences.StablePlateauLabel + ": Navegador PID 4410 segue com 6200 MB. Patamar de referência: 6100 MB: Processo mantém a memória num patamar estável. Evidência: Navegador PID 4410 iniciado em 2026-11-22 22:00:00 _00:00: de 1200 MB para 6200 MB em 4 leituras. Causa: hipótese, x. Confiança 70%; confirmado: não.\n";

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
                ClosedTrend(assert, culture);
                ClosedMemory(assert, culture);
                CurrentMemory(assert, culture);
                NormalizingExemption(assert, culture);
                Repeated(assert, culture);
            }
            await ClosedFlow(store, actor, assert);
        }
        finally { store.Data.AiEnabled = previousAi; System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
    }

    // 1a: a evolução inventada de uma ocorrência encerrada é recusada mesmo com "resolvida" na frase; o passado, a negação, a
    // pergunta e a condição passam. Sem relatório de saúde no turno, nada muda.
    private static void ClosedTrend(Action<bool, string> assert, string culture)
    {
        var closed = Observed(PcReport(SystemClosed + ProcessClosed));
        var plain = new InvestigationEvidence();
        (string Text, string? Closed)[] cases =
        [
            (TrendSentence, InvestigationEvidence.ClosedTrendReason),
            ("O espaço do T: está normalizando.", InvestigationEvidence.ClosedTrendReason),
            ("O volume T: vem melhorando aos poucos.", InvestigationEvidence.ClosedTrendReason),
            ("O espaço livre do T: continua diminuindo.", InvestigationEvidence.ClosedTrendReason),
            ("O disco T: está em recuperação.", InvestigationEvidence.ClosedTrendReason),
            ("O alerta do T: foi resolvido e o espaço está se estabilizando.", InvestigationEvidence.ClosedTrendReason),
            ("A memória está se normalizando.", InvestigationEvidence.ClosedTrendReason),
            ("O alerta de pouco espaço no T: já foi resolvido.", null),
            ("O T: voltou ao normal.", null),
            ("O espaço do T: não está mais diminuindo.", null),
            ("O espaço do T: não está diminuindo.", null),
            ("O T: já foi resolvido e o espaço livre não vem caindo.", null),
            ("Se o espaço do C: continua diminuindo, me avise.", null),
            ("O espaço está normalizando?", null),
            ("O C: tem 77,7% de espaço livre.", null),
            ("A CPU está estabilizando em 12%.", null),
            // @qa: o futuro inventado também é evolução; o verbo com objeto e o assunto depois da evolução não são.
            ("O espaço do T: deve voltar ao normal.", InvestigationEvidence.ClosedTrendReason),
            ("O espaço do T: vai se normalizar sozinho.", InvestigationEvidence.ClosedTrendReason),
            ("O espaço do T: está aumentando a cada leitura.", InvestigationEvidence.ClosedTrendReason),
            ("Manter espaço livre no disco tende a melhorar o desempenho.", null),
            ("A limpeza do disco está melhorando o desempenho do PC.", null),
            ("A conexão está caindo, mas o disco C: está sem alertas.", null),
            ("A CPU está diminuindo o uso e a memória está em 40%.", null),
        ];
        var wrong = cases.Where(c => closed.ValidateStatement(c.Text, false) != c.Closed || plain.ValidateStatement(c.Text, false) != null)
            .Select(c => c.Text + " → " + closed.ValidateStatement(c.Text, false) + " / " + plain.ValidateStatement(c.Text, false)).ToArray();
        assert(wrong.Length == 0 && InvestigationEvidence.ClosedTrendReason.Contains("encerrada") && InvestigationEvidence.ClosedTrendReason.Contains("normalizando"),
            "#139 (PC b8213bd), " + culture + ": a evolução de uma ocorrência encerrada (\"o espaço está normalizando\") é recusada mesmo com \"resolvida\"; passado, negação, pergunta e condição passam"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // 1a e 1b: o crescimento de memória encerrado apresentado como atual, com ou sem nome, e a recomendação de agir sobre "esses
    // processos" são recusados; o que diz que foi encerrado, o passado, a explicação genérica, a pergunta e a condição passam.
    private static void ClosedMemory(Action<bool, string> assert, string culture)
    {
        var closed = Observed(PcReport(SystemClosed + ProcessClosed));
        var systemOnly = Observed(PcReport(SystemClosed));
        var plain = new InvestigationEvidence();
        (string Text, string? Closed)[] cases =
        [
            (MemorySentence, InvestigationEvidence.ClosedMemoryReason),
            (Answer1bHeading + " " + Answer1bAdvice, InvestigationEvidence.ClosedMemoryReason),
            ("**Processos com crescimento de memória:** … " + Answer1bAdvice, InvestigationEvidence.ClosedMemoryReason),
            ("Houve crescimento de memória em alguns processos. " + Answer1bAdvice, InvestigationEvidence.ClosedMemoryReason),
            ("O crescimento de memória já foi resolvido. Se quiser, encerre esses processos.", null),
            ("O crescimento de memória já foi resolvido. Recomendo encerrá-los.", InvestigationEvidence.ClosedMemoryReason),
            ("Há um vazamento de memória no sistema.", InvestigationEvidence.ClosedMemoryReason),
            ("A memória está em crescimento.", InvestigationEvidence.ClosedMemoryReason),
            ("O NexoMemTeste-toca está crescendo em memória.", InvestigationEvidence.ClosedMemoryReason),
            ("Alguns processos estão usando cada vez mais memória.", InvestigationEvidence.ClosedMemoryReason),
            ("O crescimento de memória que o monitor registrou já foi resolvido.", null),
            ("Houve crescimento de memória, já encerrado.", null),
            ("A ocorrência de crescimento de memória não está mais ativa.", null),
            ("A memória está em 40% de uso.", null),
            ("A RAM é a memória de trabalho do computador e guarda os programas abertos.", null),
            ("Um programa com vazamento de memória pode usar cada vez mais memória.", null),
            ("Se algum processo voltar a usar cada vez mais memória, me avise.", null),
            ("Algum processo está usando cada vez mais memória?", null),
            ("Não há crescimento de memória nas leituras atuais.", null),
            ("Recomendo fechar os programas que você não estiver usando.", null),
            (Answer1bAdvice, null),
            ("O Chrome está usando mais memória que os outros programas.", null),
            // @qa: o aumento de memória como upgrade não é crescimento; "acumulando" e "memory leak" são.
            ("Recomendo um aumento de memória para 16 GB.", null),
            ("Um aumento de memória RAM para 16 GB ajudaria nos jogos.", null),
            ("Considere um aumento de memória.", null),
            ("O NexoMemTeste-toca apresenta aumento de memória.", InvestigationEvidence.ClosedMemoryReason),
            ("Um processo vem acumulando memória.", InvestigationEvidence.ClosedMemoryReason),
            ("Há um memory leak no sistema.", InvestigationEvidence.ClosedMemoryReason),
        ];
        var wrong = cases.Where(c => closed.ValidateStatement(c.Text, false) != c.Closed || systemOnly.ValidateStatement(c.Text, false) != c.Closed
                || plain.ValidateStatement(c.Text, false) != null)
            .Select(c => c.Text + " → " + closed.ValidateStatement(c.Text, false) + " / " + systemOnly.ValidateStatement(c.Text, false) + " / " + plain.ValidateStatement(c.Text, false)).ToArray();
        assert(wrong.Length == 0 && InvestigationEvidence.ClosedMemoryReason.Contains("crescimento de memória") && InvestigationEvidence.ClosedMemoryReason.Contains("encerrada"),
            "#139 (PC b8213bd), " + culture + ": o crescimento de memória encerrado apresentado como atual e a recomendação sobre \"esses processos\" são recusados, com ou sem processo; encerrado, passado, genérico, pergunta e condição passam"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // Com uma ocorrência de memória atual (persiste ou patamar estável informativo), o crescimento pode ser citado.
    private static void CurrentMemory(Action<bool, string> assert, string culture)
    {
        var active = Observed(PcReport(SystemClosed + ProcessClosed + ProcessActive));
        var stable = Observed(PcReport(SystemClosed + ProcessClosed + ProcessStable));
        string[] texts = [MemorySentence, Answer1bHeading + " " + Answer1bAdvice, "A memória está em crescimento.", "A memória está se normalizando."];
        var wrong = texts.Where(text => active.ValidateStatement(text, false) != null || stable.ValidateStatement(text, false) is InvestigationEvidence.ClosedMemoryReason or InvestigationEvidence.ClosedTrendReason)
            .Select(text => text + " → " + active.ValidateStatement(text, false) + " / " + stable.ValidateStatement(text, false)).ToList();
        // Um processo da consulta de processos do turno (get_processes) pode ser citado; sem a consulta, a mesma frase é recusada.
        var withProcesses = Observed(PcReport(SystemClosed + ProcessClosed));
        withProcesses.Observe("get_processes", "p", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(JsonSerializer.Serialize(new object[]
        {
            new { Name = "chrome", Id = 4120, MemoryMB = 912, Responding = true },
            new { Name = "Nexo.Support", Id = 7788, MemoryMB = 310, Responding = true }
        })));
        // Um relatório novo no turno substitui o anterior: sem ocorrência de memória nele, a trava deixa de valer.
        var renewed = Observed(PcReport(SystemClosed + ProcessClosed));
        renewed.Observe("get_health_report", "h2", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(PcReport("")));
        if (renewed.ValidateStatement(MemorySentence, false) != null) wrong.Add("relatório novo sem memória: " + renewed.ValidateStatement(MemorySentence, false));
        var empty = Observed(PcReport(SystemClosed + ProcessClosed));
        empty.Observe("get_health_report", "h3", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok("Nenhuma amostra de saúde nas últimas 24 horas."));
        if (empty.ValidateStatement(MemorySentence, false) != null) wrong.Add("relatório novo sem amostra: " + empty.ValidateStatement(MemorySentence, false));
        // @qa: depois de uma frase sobre os processos da consulta atual, "fechá-los" fala deles, não do crescimento encerrado.
        const string Referent = "O crescimento de memória já foi resolvido. O chrome e o Nexo.Support são os que mais usam memória agora. Recomendo fechá-los.";
        if (withProcesses.ValidateStatement(Referent, false) != null || Observed(PcReport(SystemClosed + ProcessClosed)).ValidateStatement(Referent, false) != InvestigationEvidence.ClosedMemoryReason)
            wrong.Add("referente atual: " + withProcesses.ValidateStatement(Referent, false));
        const string Chrome = "O chrome está usando cada vez mais memória.";
        if (withProcesses.ValidateStatement(Chrome, false) != null || Observed(PcReport(SystemClosed + ProcessClosed)).ValidateStatement(Chrome, false) != InvestigationEvidence.ClosedMemoryReason)
            wrong.Add("consulta atual: " + withProcesses.ValidateStatement(Chrome, false));
        assert(wrong.Count == 0, "#139, " + culture + ": com crescimento de memória atual (persiste ou patamar estável) ou citando um processo da consulta atual, a trava da memória encerrada não recusa"
            + (wrong.Count > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // "normalizando" não isenta mais como "normal": o disco encerrado com pouco espaço e o processo encerrado consumindo memória.
    private static void NormalizingExemption(Action<bool, string> assert, string culture)
    {
        var closed = Observed(PcReport(SystemClosed + ProcessClosed));
        (string Text, string? Closed)[] cases =
        [
            ("O T: está com pouco espaço e normalizando.", InvestigationEvidence.ClosedDiskReason),
            ("O NexoMemTeste-toca está consumindo memória, normalizando.", InvestigationEvidence.StaleProcessReason),
            ("O T: tinha pouco espaço, mas agora está normal.", null),
            ("O NexoMemTeste-toca consome memória, mas já está normal.", null),
            // @qa: o passado de "normalizar" continua isentando, como "resolvido".
            ("O pouco espaço no T: foi normalizado.", null),
            ("O NexoMemTeste-toca está consumindo memória, mas foi normalizado.", null),
        ];
        var wrong = cases.Where(c => closed.ValidateStatement(c.Text, false) != c.Closed).Select(c => c.Text + " → " + closed.ValidateStatement(c.Text, false)).ToArray();
        assert(wrong.Length == 0, "#139, " + culture + ": \"normalizando\" não isenta a falta de espaço nem o consumo do processo encerrado; \"está normal\" continua isentando"
            + (wrong.Length > 0 ? " · erradas: " + string.Join(" | ", wrong) : ""));
    }

    // Na 2ª recusa pelo mesmo motivo, o controle cita a frase recusada.
    private static void Repeated(Action<bool, string> assert, string culture)
    {
        var closed = Observed(PcReport(SystemClosed + ProcessClosed));
        var trend = closed.RepeatedGuidance(InvestigationEvidence.ClosedTrendReason, Answer1a);
        var memory = closed.RepeatedGuidance(InvestigationEvidence.ClosedMemoryReason, Answer1a);
        assert(trend.Contains("o espaço está normalizando", StringComparison.Ordinal) && !trend.Contains("Alguns processos", StringComparison.Ordinal)
            && memory.Contains("Alguns processos estão usando mais memória ao longo do tempo", StringComparison.Ordinal) && !memory.Contains("normalizando", StringComparison.Ordinal),
            "#139, " + culture + ": a 2ª recusa da evolução encerrada e do crescimento de memória encerrado cita a frase recusada · " + trend + " ‖ " + memory);
    }

    // O fluxo do PC: a resposta da 1a recusada pelo crescimento de memória e, sem ele, pela evolução do T:; a 3ª recusa idêntica
    // encerra com o relatório determinístico (#125), sem "normalizando" nem o crescimento de memória como atual.
    private static async Task ClosedFlow(Store store, Account actor, Action<bool, string> assert)
    {
        var report = PcReport(SystemClosed + ProcessClosed);
        var tools = new FlowTools((name, _) => name == "get_health_report" ? WindowsTools.Ok(report) : throw new Exception("Ferramenta inesperada: " + name));
        var repeatedModel = new CapturingModel([Text(Answer1a), Text(Answer1a), Text(Answer1a)]);
        var repeated = await Ask(store, actor, [User("como está a saúde dos meus discos?")], tools, repeatedModel);
        var trendOnly = DiskStart + " " + TrendSentence + " " + Closing;
        var trendModel = new CapturingModel([Text(trendOnly), Text(trendOnly), Text(trendOnly)]);
        var trend = await Ask(store, actor, [User("como está a saúde dos meus discos?")], tools, trendModel);
        const string Honest = DiskStart + " O alerta de pouco espaço no T: já foi resolvido e não está mais ativo. " + Closing;
        var fixedModel = new CapturingModel([Text(Answer1a), Text(Honest)]);
        var corrected = await Ask(store, actor, [User("como está a saúde dos meus discos?")], tools, fixedModel);
        var firstReason = repeatedModel.SystemMessages(1).LastOrDefault() ?? "";
        var trendReason = trendModel.SystemMessages(1).LastOrDefault() ?? "";
        assert(firstReason.Contains("Motivo: " + InvestigationEvidence.ClosedMemoryReason, StringComparison.Ordinal)
            && trendReason.Contains("Motivo: " + InvestigationEvidence.ClosedTrendReason, StringComparison.Ordinal)
            && new[] { repeated, trend }.All(answer => !answer.Contains("normalizando", StringComparison.Ordinal) && !answer.Contains("Alguns processos", StringComparison.Ordinal)
                && !answer.Contains("A IA não conseguiu", StringComparison.Ordinal) && answer.Contains("Discos físicos", StringComparison.Ordinal))
            && corrected.StartsWith(Honest, StringComparison.Ordinal) && (fixedModel.SystemMessages(1).LastOrDefault() ?? "").Contains(InvestigationEvidence.ClosedMemoryReason, StringComparison.Ordinal),
            "#139 (PC b8213bd): a resposta da 1a é recusada pelo crescimento de memória encerrado e, sem ele, pela evolução do T:; a 3ª recusa idêntica encerra com o relatório (#125), e a resposta corrigida é aceita"
            + " · " + repeated.Replace("\n", " ")[..Math.Min(repeated.Length, 220)] + " ‖ " + trend.Replace("\n", " ")[..Math.Min(trend.Length, 220)]);
    }

    // Relatório como o HealthReportPresentation monta no PC: pendrive e AMD-RAID, C: 77,8% livre, o T: encerrado depois de sair,
    // mais as linhas de memória dadas (no formato do relatório).
    private static string PcReport(string memoryLines)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-closed-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new OperationalStore(root);
            var now = DateTimeOffset.UtcNow;
            PhysicalDiskReading[] disks = [new("0", "USB DISK 2.0", "", "USB", "Healthy", ["OK"], "healthy"), new("1", "AMD-RAID Array 1", "SSD", "RAID", "Healthy", ["OK"], "healthy")];
            HealthSnapshot Sample(DateTimeOffset at, params DiskHealth[] volumes) => new()
            {
                Time = at, CpuPercent = 12, MemoryUsedPercent = 40, CollectionStatus = "complete", Disks = [.. volumes],
                CpuTemperature = new TemperatureReading("no_source", Time: at, Detail: "O driver PawnIO não está instalado. A temperatura da CPU está indisponível."),
                StorageHealth = "healthy", Storage = new StorageReading("healthy", HardwareSensors.StorageSource, at, disks.Length, [.. disks])
            };
            var low = new[] { Sample(now.AddSeconds(-120), new("C:\\", 389_000, 500_000), new("T:\\", 140, 2_000)), Sample(now.AddSeconds(-90), new("C:\\", 389_000, 500_000), new("T:\\", 140, 2_000)) };
            var found = HealthRules.Evaluate(low, new HealthThresholds());
            foreach (var finding in found) finding.Id += ":s2";
            var later = new[] { Sample(now.AddSeconds(-30), new DiskHealth("C:\\", 389_000, 500_000)), Sample(now, new DiskHealth("C:\\", 389_000, 500_000)) };
            foreach (var sample in low.Concat(later)) db.SaveSnapshot(sample);
            db.SaveFindings(found);
            var report = HealthReportPresentation.FromStore(db, MachineSensors.Off).ReplaceLineEndings("\n");
            if (!report.Contains("resolvida") || report.Contains("Achado memory")) throw new Exception("FAIL: o relatório do PC não trouxe só o T: encerrado: " + report);
            return report.TrimEnd('\n') + "\n" + memoryLines;
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
