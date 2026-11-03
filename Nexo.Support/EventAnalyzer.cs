namespace Nexo.Support;

public static class EventAnalyzer
{
    public static List<HealthFinding> Analyze(IEnumerable<SystemEvent> events)
    {
        return events.Where(e => e.Level is 1 or 2 or 3)
            .GroupBy(e => (e.Log, e.Provider, e.EventId))
            .Where(group => group.Count() >= 2)
            .Select(group =>
            {
                var last = group.MaxBy(e => e.Time)!;
                return new HealthFinding
                {
                    Id = "event:" + group.Key.Log + ":" + group.Key.Provider + ":" + group.Key.EventId + ":" + last.RecordId,
                    Time = last.Time,
                    Category = "event",
                    Problem = "Evento recorrente do Windows",
                    Evidence = $"{group.Count()} ocorrências em {group.Key.Log}, provedor {group.Key.Provider}, ID {group.Key.EventId}; última em {last.Time:O}",
                    Cause = "O evento indica uma falha ou aviso recorrente; causa específica ainda não confirmada",
                    Severity = group.Any(e => e.Level <= 2) ? "warning" : "info",
                    Impact = "A função associada pode estar degradada",
                    Recommendation = "Correlacione com sintomas e outros sinais antes de qualquer intervenção",
                    Confidence = 0.65,
                    Source = "events"
                };
            }).ToList();
    }
}
