using Nexo.Support;

namespace Nexo.Checks;

// Issue #71: um processo que segura a memória num patamar estável por 30 min deixa de "persistir" e fica
// "estável em patamar alto", informativo. Um vazamento lento, uma nova subida ou uma lacuna mantêm "Persiste".
// O nome do processo não entra no critério: os testes usam um nome qualquer.
internal static class MemoryPlateauChecks
{
    private const long Mb = 1024 * 1024;
    private static readonly long Ram = 32L * 1024 * Mb;
    private static readonly TimeSpan Brasilia = TimeSpan.FromHours(-3);
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 3, 0, 0, Brasilia);
    private static readonly DateTimeOffset ProcessStart = Now.AddHours(-5);
    private static readonly HealthThresholds Limits = new() { ProcessMemoryGrowthPoints = 7, ProcessMemorySamples = 5 };
    private const int Pid = 20248;
    private const string Name = "Navegador";

    public static void Run(Action<bool, string> check)
    {
        Window(check);
        Amplitude(check);
        Drift(check);
        Rise(check);
        Breaks(check);
        Report(check);
    }

    // Leituras a cada 30 s terminando em "end": a 0 tem o alerta do sistema e a 1 o do processo, que subiu de 2000 para 16000 MB
    // (segura a memória acima de 9000 MB). mb(i) dá o working set na leitura i; nulo tira o processo da lista.
    private static HealthSnapshot[] Series(int readings, Func<int, double?> mb, DateTimeOffset? end = null) => Enumerable.Range(0, readings).Select(i =>
    {
        var processes = new List<ProcessHealth> { new("explorer", 6400, 310 * Mb, null, ProcessStart), new("dwm", 1200, 180 * Mb, null, ProcessStart) };
        if (mb(i) is { } value) processes.Add(new(Name, Pid, (long)(value * Mb), null, ProcessStart));
        return new HealthSnapshot
        {
            Time = (end ?? Now).AddSeconds(-30 * (readings - 1 - i)), CollectionStatus = "complete", MemoryUsedPercent = 60, MemoryTotalBytes = Ram,
            MemoryUsedBytes = Ram * 6 / 10, Processes = [.. processes.OrderByDescending(p => p.WorkingSetBytes)]
        };
    }).ToArray();

    private static HealthFinding SystemAlert(HealthSnapshot[] series)
        => new() { Id = "memory-growing:s0", Time = series[0].Time, Category = "memory", Severity = "warning", Problem = "Memória em crescimento persistente", Cause = "Processo responsável ainda não identificado" };

    private static HealthFinding ProcessAlert(HealthSnapshot[] series, int at = 1, string sample = "s1")
        => new() { Id = $"process-memory-growing:{Pid}:{ProcessStart.UtcTicks}:{sample}", Time = series[at].Time, Category = "memory", Severity = "warning",
            Problem = "Processo com memória em crescimento persistente", MemoryGrowth = new(Name, 2000 * Mb, 16000 * Mb) };

    private static (OccurrenceStatus System, OccurrenceStatus Process) States(HealthSnapshot[] series)
    {
        var shown = HealthOccurrences.Classify([SystemAlert(series), ProcessAlert(series)], series, Limits, series[^1].Time);
        return (shown.Single(o => o.Finding.Id.StartsWith("memory-growing:")).Status, shown.Single(o => o.Finding.Id.StartsWith("process-memory-growing:")).Status);
    }

    // 64 leituras: 31 min depois do alerta do processo. 60 leituras: 29 min.
    private const int ThirtyOne = 64, TwentyNine = 60;

    private static void Window(Action<bool, string> check)
    {
        var flat = States(Series(ThirtyOne, _ => 14000));
        check(flat.Process.State == OccurrenceState.Stable && flat.Process.Label.StartsWith("Estável em patamar alto (informativo, não é falha ativa): Navegador PID 20248 segue com 14000 MB")
            && flat.Process.Label.Contains("patamar de cerca de 14000 MB") && flat.Process.Label.Contains("30 min"),
            "#71: processo parado em 14000 MB por 31 min fica estável em patamar alto, com o processo e os MB");
        check(flat.System.State == OccurrenceState.Stable && flat.System.Label.Contains("Navegador PID 20248 segue com 14000 MB"),
            "#71: a ocorrência de memória do sistema ligada ao processo acompanha o patamar estável");
        var shown = HealthOccurrences.Classify([SystemAlert(Series(ThirtyOne, _ => 14000)), ProcessAlert(Series(ThirtyOne, _ => 14000))], Series(ThirtyOne, _ => 14000), Limits, Now);
        check(shown.All(o => HealthOccurrences.ShownSeverity(o.Finding, o.Status) == "info") && shown.All(o => o.Finding.Severity == "warning"),
            "#71: o patamar estável é exibido como informativo, sem alterar a severidade gravada");
        var short29 = States(Series(TwentyNine, _ => 14000));
        check(short29.Process.State == OccurrenceState.Persistent && short29.System.State == OccurrenceState.Persistent && short29.Process.Label.StartsWith("Persiste: Navegador PID 20248 segue com 14000 MB"),
            "#71: com 29 min de patamar depois do alerta, as duas ocorrências ainda persistem");
        // A janela é medida em tempo: com coleta a cada 60 s, 31 min também bastam e 29 min não.
        HealthSnapshot[] Sparse(int minutes) => [.. Series(minutes + 2, _ => 14000).Select((s, i) => { s.Time = Now.AddMinutes(-(minutes + 1 - i)); return s; })];
        var limits60 = new HealthThresholds { ProcessMemoryGrowthPoints = 7, ProcessMemorySamples = 5, MaxSampleGap = TimeSpan.FromSeconds(90) };
        OccurrenceState Slow(int minutes) { var s = Sparse(minutes); return HealthOccurrences.Classify([ProcessAlert(s)], s, limits60, Now).Single().Status.State; }
        check(Slow(31) == OccurrenceState.Stable && Slow(29) == OccurrenceState.Persistent, "#71: a janela de 30 min vale em tempo, também com coleta a cada 60 s");
    }

    private static void Amplitude(Action<bool, string> check)
    {
        // Oscilação entre dois níveis, sem crescimento líquido: amplitude/média de 4,8% e de 5,2%.
        double High(double ratio) => 14000 * (2 + ratio) / (2 - ratio);
        var inside = States(Series(ThirtyOne, i => i % 2 == 0 ? 14000 : High(0.048)));
        check(inside.Process.State == OccurrenceState.Stable, "#71: oscilação de 4,8% em torno do patamar continua estável");
        var outside = States(Series(ThirtyOne, i => i % 2 == 0 ? 14000 : High(0.052)));
        check(outside.Process.State == OccurrenceState.Persistent && outside.System.State == OccurrenceState.Persistent, "#71: oscilação de 5,2% não é patamar estável");
    }

    private static void Drift(Action<bool, string> check)
    {
        // Degrau no meio da série: o 1º quarto da janela fica em 14000 MB e o último, d acima.
        OccurrenceState Step(double d) => States(Series(ThirtyOne, i => i < 32 ? 14000 : 14000 * (1 + d))).Process.State;
        check(Step(0.019) == OccurrenceState.Stable, "#71: crescimento líquido de 1,9% na janela ainda é patamar");
        check(Step(0.021) == OccurrenceState.Persistent, "#71: crescimento líquido de 2,1% na janela não é patamar");
        // Vazamento lento: 3% em 31 min, com amplitude abaixo de 5%. Não pode ser rebaixado.
        var leak = States(Series(ThirtyOne, i => 14000 * (1 + 0.03 * Math.Max(0, i - 1) / 62.0)));
        check(leak.Process.State == OccurrenceState.Persistent && leak.System.State == OccurrenceState.Persistent && leak.Process.Label.StartsWith("Persiste:"),
            "#71: vazamento lento de ~3% em 30 min continua persistindo");
        var longer = States(Series(120, i => 14000 * (1 + 0.03 * Math.Max(0, i - 1) / 60.0)));
        check(longer.Process.State == OccurrenceState.Persistent, "#71: vazamento lento que continua por 1 h nunca forma patamar");
    }

    private static void Rise(Action<bool, string> check)
    {
        // Patamar de 31 min seguido de 4 leituras mais altas.
        OccurrenceStatus After(double ratio) => States(Series(ThirtyOne + 4, i => i < ThirtyOne ? 14000 : 14000 * (1 + ratio))).Process;
        check(After(0.09).State == OccurrenceState.Stable, "#71: subida de 9% sobre o patamar mantém o estado estável");
        var back = After(0.11);
        check(back.State == OccurrenceState.Persistent && back.Label.Contains("segue com 15540 MB"), "#71: subida de 11% sobre o patamar volta a ocorrência a persistir");
        var system = States(Series(ThirtyOne + 4, i => i < ThirtyOne ? 14000 : 15540)).System;
        check(system.State == OccurrenceState.Persistent, "#71: a do sistema também volta a persistir com a nova subida");
        // Depois da volta, um novo patamar de 30 min rebaixa de novo.
        var again = States(Series(ThirtyOne + 70, i => i < ThirtyOne ? 14000 : 15540)).Process;
        check(again.State == OccurrenceState.Stable && again.Label.Contains("patamar de cerca de 15540 MB"), "#71: novo patamar estável depois da subida é rebaixado de novo");

        // Novo alerta do mesmo processo: a janela recomeça nele.
        var series = Series(ThirtyOne, _ => 14000);
        var newer = ProcessAlert(series, ThirtyOne - 3, "s9");
        var shown = HealthOccurrences.Classify([SystemAlert(series), ProcessAlert(series), newer], series, Limits, Now);
        check(shown.Single(o => o.Finding.Id.EndsWith(":s9")).Status.State == OccurrenceState.Persistent
            && shown.Single(o => o.Finding.Id.EndsWith(":s1")).Status.State == OccurrenceState.Unknown,
            "#71: novo alerta do processo volta a persistir e o anterior fica histórico");
    }

    private static void Breaks(Action<bool, string> check)
    {
        // Processo encerrado: três leituras sem ele resolvem, como antes da #71.
        var exited = States(Series(ThirtyOne + 3, i => i < ThirtyOne ? 14000 : null));
        check(exited.Process.State == OccurrenceState.Resolved && exited.System.State == OccurrenceState.Resolved, "#71: processo encerrado depois do patamar segue a regra de fim");
        // Lacuna maior que o intervalo máximo: o Nexo ficou fechado e não há prova do patamar.
        var gap = Series(ThirtyOne, _ => 14000).Where((_, i) => i is < 30 or > 32).ToArray();
        check(States(gap).Process.State == OccurrenceState.Persistent, "#71: lacuna de 2 min dentro da janela impede o patamar");
        var failed = Series(ThirtyOne, _ => 14000);
        failed[40].Processes = [];
        check(States(failed).Process.State == OccurrenceState.Persistent, "#71: falha na lista de processos dentro da janela impede o patamar");
        var abrupt = Series(ThirtyOne, i => i == 20 ? 13000 : 14000);
        check(States(abrupt).Process.State == OccurrenceState.Persistent, "#71: uma queda de 7% dentro da janela também conta na amplitude");
        // Abaixo da marca (metade da subida) o processo liberou a memória: o patamar não se aplica.
        var released = States(Series(ThirtyOne, _ => 8000));
        check(released.Process.State == OccurrenceState.Resolved, "#71: patamar abaixo da metade da subida resolve, como antes");
        // Outro PID com o mesmo nome não conta como o processo apontado.
        var other = Series(ThirtyOne, _ => 14000);
        foreach (var s in other) s.Processes = [.. s.Processes.Select(p => p.Name == Name ? p with { StartTime = ProcessStart.AddMinutes(1) } : p)];
        check(States(other).Process.State != OccurrenceState.Stable, "#71: o patamar exige o mesmo PID e o mesmo início");
        // Leitura em que o mesmo PID aparece sem início legível: não se sabe se é o mesmo processo.
        var unreadable = Series(ThirtyOne, _ => 14000);
        unreadable[40].Processes = [.. unreadable[40].Processes.Select(p => p.Name == Name ? p with { StartTime = null } : p)];
        check(States(unreadable).Process.State == OccurrenceState.Persistent, "#71: leitura sem início legível dentro da janela impede o patamar");
    }

    private static void Report(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-memory-plateau-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new OperationalStore(root);
            var series = Series(ThirtyOne, _ => 14000, DateTimeOffset.UtcNow.AddSeconds(-5));
            foreach (var sample in series) db.SaveSnapshot(sample);
            db.SaveFindings([SystemAlert(series), ProcessAlert(series)]);
            var lines = HealthReportPresentation.FromStore(db, Limits).ReplaceLineEndings("\n").Split('\n');
            var process = lines.SingleOrDefault(line => line.Contains("Processo com memória em crescimento persistente"));
            check(process != null && process.StartsWith("Achado memory (info)") && process.Contains("estável em patamar alto (informativo, não é falha ativa): Navegador PID 20248 segue com 14000 MB"),
                "#71: relatório do assistente mostra o patamar como informativo, com o processo e os MB");
            check(!lines.Any(line => line.Contains("persiste:")), "#71: relatório do assistente não apresenta o patamar como problema atual");
            var history = HealthOccurrences.History(db, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
            check(history.Count == ThirtyOne && history.Select(s => s.Id).Distinct().Count() == ThirtyOne,
                "#71: o histórico consultado cobre a janela do patamar, sem leituras repetidas");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
