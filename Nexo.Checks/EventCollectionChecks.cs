using System.Text.Json;
using Nexo.Support;

namespace Nexo.Checks;

internal static class EventCollectionChecks
{
    private sealed record Record(long Id, DateTimeOffset Time, int Level);

    public static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;

        var burst = Log(101, 145, now.AddMinutes(-4));
        var burstBatch = Collect(Source(new() { ["System"] = burst }), new() { ["System"] = 100 }, 200);
        var burstIds = burstBatch.Events.Where(e => e.Log == "System").Select(e => e.RecordId).ToList();
        check(burstIds.SequenceEqual(Enumerable.Range(101, 45).Select(i => (long)i)) && burstBatch.Gaps.Count == 0 && burstBatch.Cursors["System"] == 145,
            "Eventos: rajada de 45 eventos entre consultas é coletada por completo a partir do RecordId");
        check(burstBatch.Events.Where(e => e.Log == "System").Select(e => e.Time).SequenceEqual(burst.Select(r => r.Time)),
            "Eventos: rajada preserva o horário de cada evento");

        var paged = Collect(Source(new() { ["System"] = Log(1, 130, now.AddHours(-2)) }), null, 200);
        check(paged.Events.Count == 130 && paged.Events.Select(e => e.RecordId).Distinct().Count() == 130 && paged.Gaps.Count == 0,
            "Eventos: consulta pagina além de uma página sem repetir nem perder registros");

        var heavy = new Dictionary<string, List<Record>> { ["System"] = Log(1, 260, now.AddHours(-3)) };
        var first = Collect(Source(heavy), null, 200);
        var budgetGap = first.Gaps.SingleOrDefault();
        check(first.Events.Count == 200 && budgetGap?.Reason == "budget" && budgetGap.Log == "System" && budgetGap.FromRecordId == 201 && budgetGap.Message.Contains("200"),
            "Eventos: orçamento atingido é registrado como lacuna com o RecordId pendente");
        var second = Collect(Source(heavy), first.Cursors, 200);
        var all = first.Events.Concat(second.Events).Select(e => e.RecordId).ToList();
        check(all.Count == 260 && all.Distinct().Count() == 260 && second.Gaps.Count == 0,
            "Eventos: próxima consulta continua do cursor e recupera os registros pendentes");

        var mixed = Log(101, 160, now.AddMinutes(-3)).Select(r => r.Id % 3 == 0 ? r with { Level = 4 } : r).ToList();
        var mixedBatch = Collect(Source(new() { ["System"] = mixed }), new() { ["System"] = 100 }, 200);
        check(mixedBatch.Events.Count == mixed.Count(r => r.Level <= 3) && mixedBatch.Events.All(e => e.Level <= 3) && mixedBatch.Gaps.Count == 0,
            "Eventos: registros informativos intercalados não são confundidos com lacuna");

        var overwritten = Collect(Source(new() { ["System"] = Log(150, 170, now.AddMinutes(-2)) }), new() { ["System"] = 100 }, 200);
        var lost = overwritten.Gaps.SingleOrDefault();
        check(lost?.Reason == "overwritten" && lost.FromRecordId == 101 && lost.ToRecordId == 149 && overwritten.Events.Count == 21,
            "Eventos: registros sobrescritos pelo Windows antes da coleta viram lacuna explícita");

        var reset = Collect(Source(new() { ["System"] = Log(1, 40, now.AddMinutes(-30)) }), new() { ["System"] = 500 }, 200);
        var cleared = reset.Gaps.SingleOrDefault();
        check(cleared?.Reason == "reset" && reset.Events.Count == 40 && reset.Cursors["System"] == 40,
            "Eventos: log limpo ou reiniciado é registrado e a coleta recomeça");

        var aged = Collect(Source(new() { ["System"] = [.. Log(1, 10, now.AddDays(-3)), .. Log(11, 15, now.AddHours(-1))] }), null, 200);
        check(aged.Events.Select(e => e.RecordId).SequenceEqual([11L, 12L, 13L, 14L, 15L]),
            "Eventos: primeira coleta sem cursor respeita a janela de tempo");

        var pages = 0;
        var failing = Collect(Source(new() { ["System"] = Log(1, 120, now.AddHours(-1)) }, () => { if (++pages == 2) throw new InvalidOperationException("RPC indisponível"); }), null, 200);
        check(failing.Cursors["System"] == 50 && failing.Events.Count == 50 && failing.Warnings.Any(w => w.Contains("System") && w.Contains("RPC")),
            "Eventos: falha no meio da paginação mantém o cursor no último registro guardado");

        var stuck = new WindowsEventCollector((query, _) => Task.FromResult(Rows(query.Log == "System" ? Log(1, 3, now) : [])));
        var stuckBatch = stuck.Collect(TimeSpan.FromDays(1), 200, CancellationToken.None, new Dictionary<string, long> { ["System"] = 3 }).GetAwaiter().GetResult();
        check(stuckBatch.Warnings.Any(w => w.Contains("já coletados")) && stuckBatch.Events.Count == 0,
            "Eventos: fonte que ignora o cursor gera aviso em vez de repetir indefinidamente");

        var root = Path.Combine(Path.GetTempPath(), "NexoEvents-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = new OperationalStore(root))
            {
                store.SaveEvents(first.Events);
                store.SaveEventGaps(first.Gaps);
                store.SaveEventCursors(first.Cursors);
            }
            using var reopened = new OperationalStore(root);
            var cursors = reopened.EventCursors();
            var gaps = reopened.RecentEventGaps(10);
            check(cursors.Count == 1 && cursors["System"] == 200 && gaps.Count == 1 && gaps[0].Reason == "budget" && gaps[0].FromRecordId == 201,
                "Eventos: cursor e lacuna sobrevivem ao reinício do aplicativo");
            reopened.SaveEventCursors(new Dictionary<string, long>());
            check(reopened.EventCursors().Count == 0, "Eventos: cursor removido após reinício do log não é reaproveitado");
            var report = DiagnosticEngine.BuildReport(new HealthSnapshot { CollectionStatus = "complete" }, [], new EventBatch { Gaps = gaps }, now);
            check(report.Limitations.Any(l => l.Contains("limite de 200 eventos")), "Eventos: lacuna aparece como limitação no relatório de saúde");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static EventBatch Collect(WindowsEventCollector collector, Dictionary<string, long>? cursors, int budget)
        => collector.Collect(TimeSpan.FromDays(1), budget, CancellationToken.None, cursors).GetAwaiter().GetResult();

    private static List<Record> Log(long from, long to, DateTimeOffset start)
        => Enumerable.Range(0, (int)(to - from + 1)).Select(i => new Record(from + i, start.AddSeconds(i), 2)).ToList();

    private static WindowsEventCollector Source(Dictionary<string, List<Record>> logs, Action? onPage = null)
        => new(
            (query, _) =>
            {
                onPage?.Invoke();
                var records = logs.GetValueOrDefault(query.Log) ?? [];
                return Task.FromResult(Rows(records.Where(r => r.Level <= 3 && r.Id > query.AfterRecordId && (query.Since == null || r.Time >= query.Since)).OrderBy(r => r.Id).Take(query.Limit)));
            },
            (log, _) =>
            {
                var records = logs.GetValueOrDefault(log) ?? [];
                return Task.FromResult(JsonSerializer.Serialize(new { Oldest = records.Count == 0 ? (long?)null : records.Min(r => r.Id), Newest = records.Count == 0 ? (long?)null : records.Max(r => r.Id) }));
            });

    private static string Rows(IEnumerable<Record> records)
        => JsonSerializer.Serialize(records.Select(r => new { RecordId = r.Id, TimeCreated = r.Time.ToString("O"), ProviderName = "Disk", Id = 7, Level = r.Level }));
}
