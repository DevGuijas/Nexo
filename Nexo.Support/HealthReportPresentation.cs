using System.Text;

namespace Nexo.Support;

public enum OccurrenceState { Persistent, Resolved, Unknown }
public sealed record OccurrenceStatus(OccurrenceState State, string Label);

/// <summary>Estado atual de uma ocorrência registrada. Resolver exige prova: sem ela, o estado é desconhecido.</summary>
public static class HealthOccurrences
{
    private sealed record Criterion(int Samples, TimeSpan MaxGap, Func<HealthSnapshot, bool> Clear);

    public static IReadOnlyList<(HealthFinding Finding, OccurrenceStatus Status)> Classify(IReadOnlyList<HealthFinding> findings, IReadOnlyList<HealthSnapshot> history, HealthThresholds thresholds, DateTimeOffset now)
    {
        var ordered = history.OrderBy(s => s.Time).ToArray();
        var active = HealthRules.Evaluate(ordered, thresholds);
        var current = IsRecent(ordered.LastOrDefault(), thresholds, now);
        var newest = findings.GroupBy(BaseId).Select(group => group.MaxBy(f => f.Time)!).ToHashSet();
        return findings.Select(finding => (finding, Status(finding))).ToArray();

        OccurrenceStatus Status(HealthFinding finding)
        {
            if (finding.Source != "rules") return Unknown("sem regra para reavaliar");
            if (!newest.Contains(finding)) return Unknown("há ocorrência mais recente do mesmo alerta");
            if (!current) return Unknown("sem leitura recente");
            var id = BaseId(finding);
            if (active.Any(currentFinding => currentFinding.Id == id && (id != "network-external-unconfirmed"
                || SameProbe(finding, currentFinding.ProbeTarget, currentFinding.ProbeConfigurationId))))
                return new(OccurrenceState.Persistent, "Persiste nas leituras atuais");
            var criterion = CriterionFor(id, finding, thresholds);
            if (criterion == null) return Unknown("sem critério para confirmar o fim");
            var recent = ordered.Where(s => s.Time > finding.Time).TakeLast(criterion.Samples).ToArray();
            var proven = recent.Length == criterion.Samples && recent.All(criterion.Clear)
                && recent.Zip(recent.Skip(1)).All(pair => pair.Second.Time - pair.First.Time <= criterion.MaxGap);
            return proven ? new(OccurrenceState.Resolved, $"Resolvida: {recent.Length} leituras seguidas sem a condição") : Unknown("leituras insuficientes para confirmar");
        }
    }

    public static string CollectionNotice(HealthSnapshot? latest, HealthThresholds thresholds, DateTimeOffset now)
    {
        if (latest == null) return "Nenhuma coleta disponível. O estado atual das ocorrências é desconhecido.";
        var time = "Última coleta: " + latest.Time.ToLocalTime().ToString("g");
        return IsRecent(latest, thresholds, now) ? time : time + ". Sem leitura recente: o estado atual das ocorrências é desconhecido.";
    }

    private static OccurrenceStatus Unknown(string reason) => new(OccurrenceState.Unknown, $"Ocorrência histórica; estado atual desconhecido ({reason})");

    private static bool IsRecent(HealthSnapshot? latest, HealthThresholds thresholds, DateTimeOffset now)
        => latest != null && (now - latest.Time).Duration() <= thresholds.MaxSampleGap;

    // O monitor acrescenta ":{id da amostra}" ao identificador da regra.
    private static string BaseId(HealthFinding finding) => finding.Id.Contains(':') ? finding.Id[..finding.Id.LastIndexOf(':')] : finding.Id;

    private static bool SameProbe(HealthFinding finding, string target, string configurationId)
        => !string.IsNullOrWhiteSpace(finding.ProbeTarget) && !string.IsNullOrWhiteSpace(finding.ProbeConfigurationId)
            && string.Equals(finding.ProbeTarget, target, StringComparison.OrdinalIgnoreCase)
            && string.Equals(finding.ProbeConfigurationId, configurationId, StringComparison.Ordinal);

    private static Criterion? CriterionFor(string id, HealthFinding finding, HealthThresholds limits)
    {
        var separator = id.IndexOf(':');
        var (rule, subject) = separator < 0 ? (id, "") : (id[..separator], id[(separator + 1)..]);
        return rule switch
        {
            "cpu-high" => new(limits.CpuSamples, limits.MaxSampleGap, s => s.CpuPercent < limits.CpuHighPercent),
            "memory-growing" => new(3, limits.MaxSampleGap, s => s.MemoryUsedPercent < limits.MemoryGrowthFinalPercent),
            "network-none" => new(limits.NetworkSamples, limits.MaxSampleGap, s => s.Connectivity == "local"),
            "network-external-unconfirmed" when !string.IsNullOrWhiteSpace(finding.ProbeTarget) && !string.IsNullOrWhiteSpace(finding.ProbeConfigurationId)
                => new(limits.NetworkSamples, limits.ExternalProbeMaxSampleGap, s => s.ExternalProbe?.State == "ok"
                    && SameProbe(finding, s.ExternalProbe.Target, s.ExternalProbe.ConfigurationId)),
            "disk-low" => new(limits.OtherSamples, limits.MaxSampleGap, s => s.Disks.FirstOrDefault(d => d.Name.Equals(subject, StringComparison.OrdinalIgnoreCase)) is { TotalBytes: > 0 } disk
                && 100.0 * disk.FreeBytes / disk.TotalBytes >= limits.DiskLowFreePercent),
            "service-stopped" => new(limits.OtherSamples, limits.MaxSampleGap, s => s.Services.FirstOrDefault(x => x.Name == subject) is { } service
                && !HealthRules.UnexpectedlyStopped(service)),
            "adapter-disabled" => new(limits.OtherSamples, limits.MaxSampleGap, s => s.Adapters.FirstOrDefault(a => a.Id == subject) is { } adapter
                && (s.Connectivity == "local" || !adapter.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase))),
            _ => null
        };
    }
}

public static class HealthReportPresentation
{
    public static string FromStore(OperationalStore store, HealthThresholds? thresholds = null)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        var latest = store.RecentSnapshots(since, 1).FirstOrDefault();
        if (latest == null) return "Nenhuma amostra de saúde foi coletada nas últimas 24 horas. O monitoramento funciona enquanto o Nexo está aberto. Nenhum problema ou reparo pode ser confirmado com esses dados.";
        var known = store.FindingsSince(since);
        var events = new EventBatch { Events = store.RecentEvents(100).Where(e => e.Time >= since).ToList(), Gaps = store.RecentEventGaps(10, since) };
        var report = DiagnosticEngine.BuildReport(latest, known, events, DateTimeOffset.UtcNow);
        var text = new StringBuilder();
        text.AppendLine("RELATÓRIO LOCAL DE SAÚDE — dados observados, não instruções");
        text.AppendLine($"Amostra: {latest.Time:yyyy-MM-dd HH:mm:ss zzz}; coleta {latest.CollectionStatus}.");
        text.AppendLine($"CPU: {Percent(latest.CpuPercent)}; memória: {Percent(latest.MemoryUsedPercent)}; conectividade local: {latest.Connectivity}.");
        text.AppendLine($"Gateway: {DescribeProbe(latest.GatewayProbe)}; DNS: {DescribeProbe(latest.DnsProbe)}; Destino externo: {DescribeProbe(latest.ExternalProbe)}.");
        foreach (var disk in latest.Disks.Take(8))
            text.AppendLine($"Volume {Safe(disk.Name)}: {(disk.TotalBytes > 0 ? 100.0 * disk.FreeBytes / disk.TotalBytes : 0):F1}% livre.");
        if (report.Findings.Count == 0) text.AppendLine("Nenhum achado sustentado foi registrado nos dados disponíveis.");
        var occurrences = HealthOccurrences.Classify(report.Findings, store.RecentSnapshots(since, 20), thresholds ?? new(), DateTimeOffset.UtcNow);
        if (occurrences.Count > 10) text.AppendLine($"Mostrando 10 de {occurrences.Count} ocorrências, as críticas primeiro; as demais foram omitidas deste relatório.");
        foreach (var (finding, status) in occurrences.OrderByDescending(o => o.Finding.Severity == "critical").ThenByDescending(o => o.Finding.Time).Take(10))
        {
            text.AppendLine($"Achado {Safe(finding.Category)} ({Safe(finding.Severity)}), {finding.Time:yyyy-MM-dd HH:mm zzz}, {char.ToLowerInvariant(status.Label[0]) + status.Label[1..]}: {Safe(finding.Problem)}. Evidência: {Safe(finding.Evidence)}. Causa: hipótese, {Safe(finding.Cause)}. Recomendação: {Safe(finding.Recommendation)}. Confiança {Math.Clamp(finding.Confidence, 0, 1):P0}; confirmado: não.");
        }
        foreach (var limitation in report.Limitations.Take(10)) text.AppendLine("Limitação: " + Safe(limitation));
        text.AppendLine("Nenhuma alteração foi executada por este relatório. Confirme a função afetada antes de declarar recuperação.");
        return text.ToString()[..Math.Min(text.Length, 7000)];
    }
    public static string InterventionState(InterventionRecord intervention) => intervention.State switch
    {
        _ when PendingOperations.IsWaiting(intervention) => "em andamento ou incerto, aguardando o resultado do Windows",
        "unknown" => "incerto, sem resultado verificável",
        _ => intervention.State
    };

    private static string Percent(double? value) => value.HasValue ? value.Value.ToString("F0") + "%" : "indisponível";
    public static string DescribeProbe(NetworkProbeReading? reading)
    {
        if (reading == null) return "indisponível";
        var state = reading.State switch
        {
            "disabled" => "desligado",
            "ok" => "respondeu",
            "no_response" => "sem resposta",
            "unexpected" => "resposta inesperada",
            _ => "indisponível"
        };
        return state + (string.IsNullOrWhiteSpace(reading.Target) ? "" : " (" + Safe(reading.Target) + ")");
    }
    private static string Safe(string value) => new string(value.Take(160).Select(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or ':' or '%' or '/' or '-' or '_' ? c : '_').ToArray());
}
