namespace Nexo.Support;

public static class DiagnosticEngine
{
    public static DiagnosticReport BuildReport(HealthSnapshot snapshot, IEnumerable<HealthFinding> ruleFindings, EventBatch events, DateTimeOffset now)
    {
        var report = new DiagnosticReport { Created = now };
        report.Findings.AddRange(ruleFindings);
        report.Findings.AddRange(EventAnalyzer.Analyze(events.Events));
        if (snapshot.CpuTemperatureC == null) report.Limitations.Add("Sensor de temperatura da CPU indisponível.");
        if (snapshot.StorageHealth == "unavailable") report.Limitations.Add("Saúde SMART do armazenamento indisponível.");
        if (snapshot.CollectionStatus != "complete") report.Limitations.Add("A coleta de métricas foi parcial.");
        report.Limitations.AddRange(snapshot.Warnings);
        report.Limitations.AddRange(events.Warnings);
        report.Limitations.AddRange(events.Gaps.Select(g => g.Message));
        return report;
    }
}
