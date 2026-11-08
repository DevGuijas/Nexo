using System.Globalization;
using System.Text;

namespace Nexo.Support;

public enum OccurrenceState { Persistent, Resolved, Unknown }
public sealed record OccurrenceStatus(OccurrenceState State, string Label);

/// <summary>Estado atual de uma ocorrência registrada. Resolver exige prova: sem ela, o estado é desconhecido.</summary>
public static class HealthOccurrences
{
    private sealed record Criterion(int Samples, TimeSpan MaxGap, Func<HealthSnapshot, bool> Clear);
    private enum Hold { Holding, Released, Unknown }

    /// <summary>Processo apontado por process-memory-growing: PID e início vêm do identificador; a subida, do registro (#60).</summary>
    private sealed record Pointed(HealthFinding Finding, int Pid, long StartTicks, string Label, ProcessMemoryGrowth? Growth)
    {
        // Segura a memória enquanto mantém mais da metade da subida registrada no alerta. Registro anterior à #60 não tem limite.
        public long? Limit => Growth is { } growth && growth.ToBytes > growth.FromBytes ? growth.FromBytes + (growth.ToBytes - growth.FromBytes) / 2 : null;
    }

    // A regra de memória do sistema alerta com três leituras; o fim das ocorrências de memória exige o mesmo.
    private const int MemorySamples = 3;

    public static IReadOnlyList<(HealthFinding Finding, OccurrenceStatus Status)> Classify(IReadOnlyList<HealthFinding> findings, IReadOnlyList<HealthSnapshot> history, HealthThresholds thresholds, DateTimeOffset now)
    {
        var ordered = history.OrderBy(s => s.Time).ToArray();
        var active = HealthRules.Evaluate(ordered, thresholds);
        var current = IsRecent(ordered.LastOrDefault(), thresholds, now);
        var newest = findings.GroupBy(BaseId).Select(group => group.MaxBy(f => f.Time)!).ToHashSet();
        var pointed = findings.Select(f => PointedBy(f, ordered)).OfType<Pointed>().ToArray();
        var cadence = Cadence(ordered, thresholds);
        return findings.Select(finding =>
        {
            var related = Related(finding);
            // A ocorrência do processo pode chegar leituras depois da do sistema: a ligação é feita aqui, sem regravar nada.
            var shown = related.Length == 0 ? finding : finding.WithCause(HealthRules.RelatedProcessCause(related.Select(p => p.Label)));
            return (shown, Status(finding, related));
        }).ToArray();

        Pointed[] Related(HealthFinding system) => system.Source != "rules" || BaseId(system) != "memory-growing" ? [] : pointed
            .Where(p => Overlaps(system.Time, p.Finding.Time))
            .GroupBy(p => (p.Pid, p.StartTicks)).Select(group => group.MaxBy(p => p.Finding.Time)!).OrderBy(p => p.Finding.Time).ToArray();

        // A janela do sistema tem as três leituras até o alerta dele. A do processo tem as ProcessMemorySamples leituras com
        // lista de processos até o alerta dele, como na regra: leitura com a lista vazia (falha de coleta) não entra nela.
        // A ligação exige que as duas janelas dividam ao menos uma leitura.
        bool Overlaps(DateTimeOffset system, DateTimeOffset process)
        {
            var (before, after) = (MemorySamples - 1, thresholds.ProcessMemorySamples - 1);
            // Cada alerta tem o horário da leitura que o disparou.
            var (s, p) = (Array.FindIndex(ordered, r => r.Time == system), Array.FindIndex(ordered, r => r.Time == process));
            if (s >= 0 && p >= 0)
                return Enumerable.Range(0, p + 1).Where(i => ordered[i].Processes.Count > 0).TakeLast(thresholds.ProcessMemorySamples)
                    .Any(i => i >= s - before && i <= s);
            // Leitura fora do histórico consultado: conta as leituras pelo intervalo típico entre elas, com meia leitura de folga.
            // Aqui não se sabe quais leituras vieram sem lista, nem se o intervalo de coleta mudou depois dos alertas.
            if (cadence is not { } step) return false;
            var readings = (process - system) / step;
            return readings >= -before - 0.5 && readings <= after + 0.5;
        }

        OccurrenceStatus Status(HealthFinding finding, Pointed[] related)
        {
            if (finding.Source != "rules") return Unknown("sem regra para reavaliar");
            if (!newest.Contains(finding)) return Unknown("há ocorrência mais recente do mesmo alerta");
            if (!current) return Unknown("sem leitura recente");
            var id = BaseId(finding);
            if (active.Any(currentFinding => currentFinding.Id == id && (id != "network-external-unconfirmed"
                || SameProbe(finding, currentFinding.ProbeTarget, currentFinding.ProbeConfigurationId))))
                return new(OccurrenceState.Persistent, "Persiste nas leituras atuais");
            // Memória do sistema depende dos processos apontados; a do processo, dele mesmo.
            Pointed[] holders = related.Length > 0 ? related : PointedBy(finding, ordered) is { } self ? [self] : [];
            foreach (var holder in holders)
                if (HoldAt(holder, ordered[^1]) is (Hold.Holding, var bytes))
                    return new(OccurrenceState.Persistent, $"Persiste: {holder.Label} segue com {Megabytes(bytes)} MB, mais da metade da subida de {Megabytes(holder.Growth!.FromBytes)} para {Megabytes(holder.Growth.ToBytes)} MB registrada no alerta do processo");
            var criterion = CriterionFor(id, finding, thresholds, holders);
            if (criterion == null) return Unknown("sem critério para confirmar o fim");
            var recent = ordered.Where(s => s.Time > finding.Time).TakeLast(criterion.Samples).ToArray();
            var proven = recent.Length == criterion.Samples && recent.All(criterion.Clear)
                && recent.Zip(recent.Skip(1)).All(pair => pair.Second.Time - pair.First.Time <= criterion.MaxGap);
            if (proven) return new(OccurrenceState.Resolved, $"Resolvida: {recent.Length} leituras seguidas sem a condição");
            var unconfirmed = holders.FirstOrDefault(holder => HoldAt(holder, ordered[^1]).State == Hold.Unknown);
            return Unknown(unconfirmed != null ? $"sem leitura que confirme que {unconfirmed.Label} liberou a memória" : "leituras insuficientes para confirmar");
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

    private static Pointed? PointedBy(HealthFinding finding, HealthSnapshot[] history)
    {
        var parts = BaseId(finding).Split(':');
        if (finding.Source != "rules" || parts.Length != 3 || parts[0] != "process-memory-growing"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return null;
        // Registro anterior à #60 não guarda o nome: usa o da leitura, se o processo ainda aparece no histórico.
        var name = finding.MemoryGrowth?.Name ?? history.SelectMany(s => s.Processes).LastOrDefault(p => Same(p, pid, ticks))?.Name;
        return new(finding, pid, ticks, $"{(name == null ? "processo" : HealthRules.SafeName(name))} PID {pid}", finding.MemoryGrowth);
    }

    private static bool Same(ProcessHealth process, int pid, long startTicks) => process.Id == pid && process.StartTime?.UtcTicks == startTicks;

    private static (Hold State, long Bytes) HoldAt(Pointed process, HealthSnapshot sample)
    {
        // Falha de coleta deixa a lista vazia: essa leitura não é evidência sobre o processo.
        if (sample.Processes.Count == 0) return (Hold.Unknown, 0);
        if (sample.Processes.FirstOrDefault(p => Same(p, process.Pid, process.StartTicks)) is { } same)
            return (process.Limit is not { } limit ? Hold.Unknown : same.WorkingSetBytes > limit ? Hold.Holding : Hold.Released, same.WorkingSetBytes);
        // O mesmo PID sem início legível pode ser o mesmo processo.
        if (sample.Processes.Any(p => p.Id == process.Pid && p.StartTime == null)) return (Hold.Unknown, 0);
        // Com outro início, é outro processo: o Windows só reutiliza o PID depois que o apontado encerra.
        if (sample.Processes.Any(p => p.Id == process.Pid)) return (Hold.Released, 0);
        // Fora da lista dos maiores, o processo encerrou ou ocupa no máximo o menor listado.
        return (process.Limit is not { } bound || sample.Processes.Min(p => p.WorkingSetBytes) <= bound ? Hold.Released : Hold.Unknown, 0);
    }

    // Mediana dos intervalos entre as leituras, limitada ao intervalo máximo: leituras espaçadas (o Nexo fechado) não alargam a ligação.
    private static TimeSpan? Cadence(HealthSnapshot[] ordered, HealthThresholds thresholds)
    {
        var gaps = ordered.Zip(ordered.Skip(1), (first, second) => second.Time - first.Time).Order().ToArray();
        if (gaps.Length == 0) return null;
        var median = gaps[(gaps.Length - 1) / 2];
        return median < thresholds.MaxSampleGap ? median : thresholds.MaxSampleGap;
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024)).ToString("F0", CultureInfo.InvariantCulture);

    private static Criterion? CriterionFor(string id, HealthFinding finding, HealthThresholds limits, Pointed[] holders)
    {
        var separator = id.IndexOf(':');
        var (rule, subject) = separator < 0 ? (id, "") : (id[..separator], id[(separator + 1)..]);
        return rule switch
        {
            "cpu-high" => new(limits.CpuSamples, limits.MaxSampleGap, s => s.CpuPercent < limits.CpuHighPercent),
            // A compressão do Windows reduz o uso total sem que o processo apontado libere a memória.
            "memory-growing" => new(MemorySamples, limits.MaxSampleGap, s => s.MemoryUsedPercent < limits.MemoryGrowthFinalPercent
                && holders.All(holder => HoldAt(holder, s).State == Hold.Released)),
            "process-memory-growing" when holders is [{ Limit: not null } self] => new(MemorySamples, limits.MaxSampleGap, s => HoldAt(self, s).State == Hold.Released),
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
