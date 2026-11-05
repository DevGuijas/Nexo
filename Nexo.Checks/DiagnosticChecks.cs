using Nexo.Support;

namespace Nexo.Checks;

internal static class DiagnosticChecks
{
    public static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var collector = new WindowsEventCollector((query, _) => Task.FromResult(query.Log switch
        {
            "System" => $$"""[{"RecordId":11,"TimeCreated":"{{now:O}}","ProviderName":"Disk","Id":7,"Level":2},{"RecordId":12,"TimeCreated":"{{now:O}}","ProviderName":"Disk","Id":7,"Level":2}]""",
            "Application" => $$"""[{"RecordId":21,"TimeCreated":"{{now:O}}","ProviderName":"Application Error","Id":1000,"Level":2}]""",
            _ => throw new InvalidOperationException("Access denied")
        }));
        var batch = collector.Collect(TimeSpan.FromHours(1), 20, CancellationToken.None).GetAwaiter().GetResult();
        check(batch.Events.Count == 3 && batch.Events.Select(e => e.Log).Distinct().Count() == 2, "Coleta preserva eventos de System e Application");
        check(batch.Events.All(e => e.Time == now), "Coleta preserva horário do evento");
        check(batch.Warnings.Count == 1 && batch.Warnings[0].Contains("Setup"), "Canal inacessível gera aviso sem descartar outros");
        var findings = EventAnalyzer.Analyze(batch.Events);
        check(findings.Count == 1 && findings[0].Evidence.Contains("2 ocorrências"), "Evento recorrente produz evidência quantificada");
        var snapshot = new HealthSnapshot { Time = now, CpuTemperatureC = null, StorageHealth = "unavailable", CollectionStatus = "partial" };
        var report = DiagnosticEngine.BuildReport(snapshot, [], batch, now);
        check(report.Limitations.Any(x => x.Contains("temperatura")) && report.Limitations.Any(x => x.Contains("Setup")), "Relatório explicita sensores e canais indisponíveis");
        check(report.Findings.All(f => !f.Confirmed && f.Confidence is >= 0 and <= 1), "Hipóteses não são declaradas confirmadas");
        var empty = DiagnosticEngine.BuildReport(new HealthSnapshot { CollectionStatus = "complete" }, [], new EventBatch(), now);
        check(empty.Findings.Count == 0, "Ausência de evidência não gera diagnóstico inventado");
        var malformed = new WindowsEventCollector((_, _) => Task.FromResult("not-json"));
        check(malformed.Collect(TimeSpan.FromHours(1), 2, CancellationToken.None).GetAwaiter().GetResult().Warnings.Count == 3, "JSON inválido em cada canal é limitação explícita");
        if (Environment.GetEnvironmentVariable("NEXO_LIVE_EVENTS") == "1")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            var live = new WindowsEventCollector().Collect(TimeSpan.FromDays(1), 5, timeout.Token).GetAwaiter().GetResult();
            check(live.Events.Count <= 15 && live.Events.Count + live.Warnings.Count > 0, "Consulta real de eventos é limitada e relata canais indisponíveis");
            Console.WriteLine($"EVENTOS: {live.Events.Count}, avisos: {string.Join(" | ", live.Warnings)}, lacunas: {string.Join(" | ", live.Gaps.Select(g => g.Message))}");
            var next = new WindowsEventCollector().Collect(TimeSpan.FromDays(1), 5, timeout.Token, live.Cursors).GetAwaiter().GetResult();
            check(next.Events.All(e => live.Cursors.TryGetValue(e.Log, out var cursor) && e.RecordId > cursor) && next.Gaps.All(g => g.Reason == "budget") && next.Warnings.Count <= live.Warnings.Count,
                "Consulta real continua do cursor sem repetir registros e confere os limites do log");
            Console.WriteLine($"CURSORES: {string.Join(", ", live.Cursors.Select(c => c.Key + "=" + c.Value))}; próxima coleta: {next.Events.Count} eventos, avisos: {string.Join(" | ", next.Warnings)}");
        }
    }
}
