using System.Text;

namespace Nexo.Support;

public static class HealthReportPresentation
{
    public static string FromStore(OperationalStore store, HealthThresholds? thresholds = null)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        var latest = store.RecentSnapshots(since, 1).FirstOrDefault();
        if (latest == null) return "Nenhuma amostra de saúde foi coletada nas últimas 24 horas. O monitoramento funciona enquanto o Nexo está aberto. Nenhum problema ou reparo pode ser confirmado com esses dados.";
        var known = store.RecentFindings(40).Where(f => f.Time >= since).ToArray();
        var active = HealthRules.Evaluate(store.RecentSnapshots(latest.Time.AddHours(-6), 20).OrderBy(s => s.Time).ToArray(), thresholds ?? new())
            .Select(f => f.Id).ToHashSet();
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
        foreach (var finding in report.Findings.OrderByDescending(f => f.Time).Take(10))
        {
            var baseId = finding.Id.Contains(':') ? finding.Id[..finding.Id.LastIndexOf(':')] : finding.Id;
            var current = finding.Source == "rules" && active.Contains(baseId);
            text.AppendLine($"Achado {Safe(finding.Category)} ({Safe(finding.Severity)}), {finding.Time:yyyy-MM-dd HH:mm zzz}, {(current ? "persiste nas leituras atuais" : "ocorrência histórica; estado atual não confirmado")}: {Safe(finding.Problem)}. Evidência: {Safe(finding.Evidence)}. Causa: hipótese, {Safe(finding.Cause)}. Recomendação: {Safe(finding.Recommendation)}. Confiança {Math.Clamp(finding.Confidence, 0, 1):P0}; confirmado: não.");
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
