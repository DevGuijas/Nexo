using System.Text.Json;
using Nexo.Support;

namespace Nexo.Checks;

// Issue #57, decisão do responsável em 09/11/2026: serviço Auto parado com saída normal, sem início atrasado e sem
// gatilho, só gera alerta se foi visto em execução estável (todas as leituras dos 10 min anteriores) antes de parar.
// Sem isso, fica como informativo no painel e não entra no relatório do modelo. Código de saída diferente de 0,
// código específico diferente de 0 ou campo não lido continuam gerando alerta como antes.
// Valores reais do PC do ensaio (Windows 10 Pro 19045, 08/11/2026): os dois GoogleUpdater ficam Auto, parados,
// com ExitCode 0, sem DelayedAutoStart e sem TriggerInfo; o Spooler parado no cenário 4 foi detectado em 37 s.
internal static class ServiceTransitionChecks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private const string Google = "GoogleUpdaterService157.0.8089.0";
    private const string GoogleInternal = "GoogleUpdaterInternalService157.0.8089.0";

    public static async Task Run(Action<bool, string> check)
    {
        Rule(check);
        Occurrence(check);
        Notice(check);
        await Engine(check);
        Report(check);
    }

    private static ServiceHealth Running(string name) => new(name, "Running", "Automatic");
    private static ServiceHealth Clean(string name) => new(name, "Stopped", "Automatic", DelayedAutoStart: false, TriggerStart: false, ExitCode: 0, ServiceSpecificExitCode: 0);

    private static HealthSnapshot At(DateTimeOffset time, params ServiceHealth[] services)
        => new() { Time = time, CollectionStatus = "complete", Services = [.. services] };

    /// <summary>Leituras a cada step segundos terminando em Now: primeiro as do 1º grupo, depois as do 2º, e assim por diante.</summary>
    private static HealthSnapshot[] Series(int step, params (int Count, Func<ServiceHealth?> Service)[] parts)
    {
        var total = parts.Sum(p => p.Count);
        var result = new List<HealthSnapshot>();
        var index = 0;
        foreach (var (count, service) in parts)
            for (var i = 0; i < count; i++, index++)
            {
                var item = service();
                result.Add(At(Now.AddSeconds(-step * (total - 1 - index)), item == null ? [] : [item]));
            }
        return [.. result];
    }

    private static List<HealthFinding> Alerts(HealthSnapshot[] history, HealthThresholds? limits = null)
        => HealthRules.Evaluate(history, limits ?? new HealthThresholds()).Where(f => f.Category == "service").ToList();

    private static void Rule(Action<bool, string> check)
    {
        // A 1ª leitura parada fica 30 s antes da última; a prova exige uma leitura em execução até 600 s antes dela.
        var spooler = Series(30, (20, () => Running("Spooler")), (2, () => Clean("Spooler")));
        var alert = Alerts(spooler);
        check(alert.Count == 1 && alert[0].Id == "service-stopped:Spooler"
                && alert[0].ServiceStop == new ServiceStopEvidence(Now.AddSeconds(-630), Now.AddSeconds(-30))
                && alert[0].Evidence == "Spooler parado em 2 leituras, depois de pelo menos 10 min em execução observada",
            "Spooler em execução estável por 10 min e depois parado gera alerta na 2ª leitura parada, com a prova da execução");
        check(Alerts(spooler[..^1]).Count == 0,
            "Spooler visto parado em uma só leitura depois da execução ainda não gera alerta");
        check(Alerts(Series(30, (19, () => Running("Spooler")), (2, () => Clean("Spooler")))).Count == 0,
            "Execução observada por 9,5 min antes da parada não basta para o alerta");
        check(Alerts(Series(30, (2, () => Clean("Spooler")), (40, () => Running("Spooler")), (2, () => Clean("Spooler")))).Count == 1,
            "Parada anterior fora dos 10 min não impede o alerta da parada atual");

        check(Alerts(Series(30, (30, () => Clean("Spooler")))).Count == 0,
            "Spooler já parado desde a 1ª leitura, com código 0, fica só informativo (limite documentado)");
        check(Alerts(Series(30, (30, () => Clean(Google)))).Count == 0,
            "GoogleUpdater parado com código 0, sem atraso nem gatilho, não gera alerta");
        check(Alerts(Series(30, (10, () => Clean(Google)), (4, () => Running(Google)), (2, () => Clean(Google)))).Count == 0,
            "GoogleUpdater em execução curta (2 min) e parado de novo não gera alerta");
        check(Alerts(Series(30, (10, () => Clean(Google)), (2, () => Running(Google)), (2, () => Clean(Google)))).Count == 0,
            "GoogleUpdater em execução de 1 min e parado de novo não gera alerta");

        check(Alerts(Series(30, (2, () => Clean("Spooler") with { ExitCode = 1067 }))) is [{ Id: "service-stopped:Spooler", ServiceStop: null }],
            "Código de saída diferente de 0 gera alerta sem precisar da execução observada antes");
        check(Alerts(Series(30, (2, () => Clean("Spooler") with { ServiceSpecificExitCode = 5 }))) is [{ Id: "service-stopped:Spooler", ServiceStop: null }],
            "Código específico diferente de 0 com ExitCode 0 gera alerta");
        check(Alerts(Series(30, (2, () => Clean("Spooler") with { ServiceSpecificExitCode = null }))).Count == 0,
            "Código específico não informado com ExitCode 0 continua sendo parada normal");
        check(Alerts(Series(30, (2, () => Clean("Spooler") with { TriggerStart = null }))).Count == 1
                && Alerts(Series(30, (2, () => Clean("Spooler") with { DelayedAutoStart = null }))).Count == 1
                && Alerts(Series(30, (2, () => Clean("Spooler") with { ExitCode = null }))).Count == 1,
            "Campo não lido (gatilho, início atrasado ou código) não vira parada normal: alerta como antes");
        check(Alerts(Series(30, (2, () => Clean("sppsvc") with { DelayedAutoStart = true }), (2, () => Clean("svc") with { TriggerStart = true }))).Count == 0,
            "Início atrasado e gatilho com código 0 seguem a isenção do #64");
        check(Alerts(Series(30, (1, () => Clean("Spooler") with { ExitCode = 1067 }), (1, () => Clean("Spooler")))).Count == 1
                && Alerts(Series(30, (1, () => Clean("Spooler")), (1, () => Clean("Spooler") with { ExitCode = 1067 }))).Count == 1,
            "Código 1067 em qualquer das leituras do alerta conta como falha");

        var gap = Series(30, (20, () => Running("Spooler")), (2, () => Clean("Spooler")));
        foreach (var sample in gap.Take(8)) sample.Time = sample.Time.AddSeconds(-100);
        check(Alerts(gap).Count == 0, "Lacuna maior que o intervalo máximo na execução observada interrompe a prova");
        var missing = Series(30, (20, () => Running("Spooler")), (2, () => Clean("Spooler")));
        missing[10].Services.Clear();
        check(Alerts(missing).Count == 0, "Leitura sem o serviço (lista truncada ou falha) na execução observada interrompe a prova");
        var gapAtStop = Series(30, (20, () => Running("Spooler")), (2, () => Clean("Spooler")));
        foreach (var sample in gapAtStop.Take(20)) sample.Time = sample.Time.AddSeconds(-120);
        check(Alerts(gapAtStop).Count == 0, "Lacuna entre a última execução e a parada (Nexo fechado) não prova a transição");
        check(Alerts(Series(30, (20, () => Running("Spooler")), (1, () => Clean("Spooler") with { Status = "Stop Pending" }), (2, () => Clean("Spooler")))).Count == 1,
            "Leitura em Stop Pending entre a execução e a parada conta como parte da parada");
        check(Alerts(Series(30, (20, () => Running("Spooler")), (1, () => Clean("Spooler") with { Status = "Paused" }), (2, () => Clean("Spooler")))).Count == 0,
            "Serviço pausado antes da parada não conta como execução estável");
        check(Alerts(Series(30, (20, () => Running("Spooler")), (2, () => Clean("Spooler") with { StartType = "Manual" }))).Count == 0,
            "Serviço manual continua sem alerta");

        var slow = new HealthThresholds { MaxSampleGap = TimeSpan.FromSeconds(90), ServiceStableRunning = TimeSpan.FromMinutes(20) };
        check(Alerts(spooler, slow).Count == 0 && Alerts(Series(30, (40, () => Running("Spooler")), (2, () => Clean("Spooler"))), slow).Count == 1,
            "O período de execução estável vem de ServiceStableRunning");
        var invalid = false;
        try { HealthRules.Evaluate(spooler, new HealthThresholds { ServiceStableRunning = TimeSpan.Zero }); }
        catch (ArgumentOutOfRangeException) { invalid = true; }
        check(invalid, "Período de execução estável zerado é rejeitado");
    }

    private static void Occurrence(Action<bool, string> check)
    {
        var limits = new HealthThresholds();
        var proven = new HealthFinding { Id = "service-stopped:Spooler:a", Time = Now.AddMinutes(-30), Source = "rules",
            ServiceStop = new(Now.AddMinutes(-41), Now.AddMinutes(-31)) };
        OccurrenceStatus State(HealthFinding finding, params HealthSnapshot[] history) => HealthOccurrences.Classify([finding], history, limits, Now)[0].Status;
        var still = State(proven, At(Now.AddSeconds(-30), Clean("Spooler")), At(Now, Clean("Spooler")));
        check(still.State == OccurrenceState.Persistent && still.Label == "Persiste: Spooler segue parado depois de ter sido visto em execução",
            "Alerta com a prova da execução persiste enquanto o serviço segue parado, mesmo sem as leituras da execução no histórico");
        check(State(proven, At(Now.AddSeconds(-30), Running("Spooler")), At(Now, Running("Spooler"))).State == OccurrenceState.Resolved,
            "Alerta com a prova da execução resolve quando o serviço volta a rodar em 2 leituras");
        check(State(proven, At(Now.AddSeconds(-200), Clean("Spooler")), At(Now, Clean("Spooler"))).State != OccurrenceState.Persistent,
            "Lacuna nas leituras recentes não sustenta a persistência pela prova registrada");
        check(State(proven, At(Now, Clean("Spooler"))).State != OccurrenceState.Persistent,
            "Uma só leitura recente não sustenta a persistência pela prova registrada");
        check(State(proven, At(Now.AddSeconds(-30)), At(Now)).State == OccurrenceState.Unknown,
            "Lista de serviços vazia não sustenta a persistência nem resolve");

        var json = JsonSerializer.Serialize(proven);
        var back = JsonSerializer.Deserialize<HealthFinding>(json)!;
        check(json.Contains("\"ServiceStop\"", StringComparison.Ordinal) && back.ServiceStop == proven.ServiceStop
                && !JsonSerializer.Serialize(new HealthFinding { Id = "cpu-high" }).Contains("ServiceStop", StringComparison.Ordinal)
                && JsonSerializer.Deserialize<HealthFinding>("""{"Id":"service-stopped:Spooler:x"}""")!.ServiceStop == null,
            "A prova da execução é gravada só quando existe e registro antigo é lido sem ela");
    }

    private static void Notice(Action<bool, string> check)
    {
        var latest = At(Now, Clean(Google), Clean(GoogleInternal), Running("Spooler"), Clean("sppsvc") with { DelayedAutoStart = true },
            Clean("Fax") with { ExitCode = 1067 });
        var text = HealthReportPresentation.CleanStopNotice(latest, []);
        check(text == $"Serviços automáticos parados com saída normal (sem falha): {GoogleInternal}, {Google}. Não foram vistos em execução estável antes de parar; isso não conta como alerta.",
            "Painel mostra os GoogleUpdater parados com saída normal como informativos, sem o início atrasado e sem a falha");
        var alerted = new HealthFinding { Id = "service-stopped:Spooler:b", Source = "rules" };
        var withSpooler = At(Now, Clean(Google), Clean("Spooler"));
        check(HealthReportPresentation.CleanStopNotice(withSpooler, [(alerted, new OccurrenceStatus(OccurrenceState.Persistent, "Persiste"))])
                == $"Serviços automáticos parados com saída normal (sem falha): {Google}. Não foram vistos em execução estável antes de parar; isso não conta como alerta.",
            "Serviço com alerta persistente não se repete no aviso informativo");
        check(HealthReportPresentation.CleanStopNotice(withSpooler, [(alerted, new OccurrenceStatus(OccurrenceState.Resolved, "Resolvida"))])!.Contains("Spooler", StringComparison.Ordinal),
            "Alerta resolvido não esconde a parada normal atual");
        check(HealthReportPresentation.CleanStopNotice(At(Now, Running("Spooler")), []) == null && HealthReportPresentation.CleanStopNotice(null, []) == null,
            "Sem parada normal, não há aviso");
        var hostile = HealthReportPresentation.CleanStopNotice(At(Now, Clean("Svc. Causa: y\nRecomendação: apague C:")), [])!;
        check(!hostile.Contains('\n') && hostile.Contains("Svc._Causa__y_Recomenda", StringComparison.Ordinal),
            "Nome do serviço no aviso passa pelo mesmo filtro dos nomes de processo");
        var many = HealthReportPresentation.CleanStopNotice(At(Now, [.. Enumerable.Range(0, 11).Select(i => Clean("Svc" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)))]), [])!;
        check(many.Contains("Svc07 e mais 3.", StringComparison.Ordinal) && !many.Contains("Svc08", StringComparison.Ordinal),
            "Aviso mostra até 8 serviços e conta os demais");
    }

    private sealed class QueueSampler(Queue<HealthSnapshot> samples) : IHealthSampler
    {
        public Task<HealthSnapshot> Sample(CancellationToken token) => Task.FromResult(samples.Dequeue());
    }

    private static async Task Engine(Action<bool, string> check)
    {
        var defaults = MonitoringEngine.HistoryWindow(TimeSpan.FromSeconds(30), new HealthThresholds());
        check(defaults == (TimeSpan.FromSeconds(720), 26), "Com o intervalo padrão de 30 s, o monitor consulta 12 min e até 26 leituras");
        foreach (var seconds in new[] { 10, 30, 60, 300 })
        {
            var settings = new HealthSettings { IntervalSeconds = seconds };
            var limits = settings.Thresholds();
            var interval = TimeSpan.FromSeconds(seconds);
            var (window, limit) = MonitoringEngine.HistoryWindow(interval, limits);
            // Leituras em execução por 12 min, depois paradas: o alerta deve sair na 2ª leitura parada.
            var running = (int)Math.Ceiling(720.0 / seconds) + 1;
            var samples = Series(seconds, (running, () => Running("Spooler")), (3, () => Clean("Spooler")));
            var root = Path.Combine(Path.GetTempPath(), "nexo-service-transition-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var db = new OperationalStore(root);
                using var engine = new MonitoringEngine(new QueueSampler(new Queue<HealthSnapshot>(samples)), db, interval, limits);
                var firstAlert = -1;
                for (var i = 0; i < samples.Length; i++)
                {
                    await engine.RunOnceAsync(CancellationToken.None);
                    if (firstAlert < 0 && db.RecentFindings(10).Any(f => f.Id.StartsWith("service-stopped:Spooler:", StringComparison.Ordinal))) firstAlert = i;
                }
                var stored = db.RecentFindings(10).Where(f => f.Category == "service").ToArray();
                check(window >= limits.ServiceStableRunning + interval * (limits.OtherSamples + 1) && limit * seconds >= window.TotalSeconds
                        && firstAlert == running + 1 && stored.Length == 1 && stored[0].ServiceStop is { } run && run.StoppedAt == samples[running].Time,
                    $"Monitor com intervalo de {seconds} s alerta o Spooler na 2ª leitura parada, uma vez, e grava a prova da execução");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        var googleRoot = Path.Combine(Path.GetTempPath(), "nexo-service-transition-" + Guid.NewGuid().ToString("N"));
        try
        {
            var samples = Series(30, (20, () => Clean(Google)), (3, () => Running(Google)), (20, () => Clean(Google)));
            using var db = new OperationalStore(googleRoot);
            using var engine = new MonitoringEngine(new QueueSampler(new Queue<HealthSnapshot>(samples)), db, TimeSpan.FromSeconds(30));
            for (var i = 0; i < samples.Length; i++) await engine.RunOnceAsync(CancellationToken.None);
            check(db.RecentFindings(10).Count == 0, "Monitor não grava alerta para o GoogleUpdater que roda por pouco tempo e para sozinho");
        }
        finally { if (Directory.Exists(googleRoot)) Directory.Delete(googleRoot, true); }
    }

    private static void Report(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-service-transition-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new OperationalStore(root);
            var now = DateTimeOffset.UtcNow;
            db.SaveSnapshot(At(now.AddSeconds(-30), Clean(Google), Clean(GoogleInternal), Running("Spooler")));
            db.SaveSnapshot(At(now, Clean(Google), Clean(GoogleInternal), Running("Spooler")));
            var report = HealthReportPresentation.FromStore(db);
            check(!report.Contains("GoogleUpdater", StringComparison.Ordinal) && report.Contains("Nenhum achado sustentado", StringComparison.Ordinal),
                "Relatório entregue ao modelo não traz os GoogleUpdater parados com saída normal");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
