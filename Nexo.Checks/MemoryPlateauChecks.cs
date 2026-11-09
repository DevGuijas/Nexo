using Nexo.Support;

namespace Nexo.Checks;

// Issue #71: um processo que segura a memória num patamar estável deixa de "persistir" e fica "estável em patamar alto",
// informativo. A referência é o 1º patamar depois do alerta (30 min com amplitude < 5% e inclinação < 2% em 30 min) e
// fica fixa: um acumulado de 10% sobre ela volta a "Persiste" até um novo alerta. Vazamento lento, degraus, lacuna antes
// do patamar ou falta de leitura desde o alerta mantêm "Persiste". O nome do processo não entra no critério.
internal static class MemoryPlateauChecks
{
    private const long Mb = 1024 * 1024;
    private static readonly long Ram = 32L * 1024 * Mb;
    private static readonly TimeSpan Brasilia = TimeSpan.FromHours(-3);
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 3, 0, 0, Brasilia);
    private static readonly DateTimeOffset ProcessStart = Now.AddDays(-5);
    private static readonly HealthThresholds Limits = new() { ProcessMemoryGrowthPoints = 7, ProcessMemorySamples = 5 };
    private const int Pid = 20248;
    private const string Name = "Navegador";

    public static void Run(Action<bool, string> check)
    {
        Window(check);
        Amplitude(check);
        Slope(check);
        Accumulated(check);
        Rise(check);
        Breaks(check);
        TwoProcesses(check);
        Recorder(check);
        Recorded(check);
    }

    // Leituras a cada 30 s terminando em "end": a 0 tem o alerta do sistema e a 1 o do processo, que subiu de 2000 para 16000 MB
    // (segura a memória acima de 9000 MB). mb(i) dá o working set na leitura i; nulo tira o processo da lista.
    private static HealthSnapshot[] Series(int readings, Func<int, double?> mb, DateTimeOffset? end = null, Func<int, ProcessHealth[]>? extra = null)
        => Enumerable.Range(0, readings).Select(i =>
    {
        var processes = new List<ProcessHealth> { new("explorer", 6400, 310 * Mb, null, ProcessStart), new("dwm", 1200, 180 * Mb, null, ProcessStart) };
        if (mb(i) is { } value) processes.Add(new(Name, Pid, (long)(value * Mb), null, ProcessStart));
        if (extra != null) processes.AddRange(extra(i));
        return new HealthSnapshot
        {
            Time = (end ?? Now).AddSeconds(-30 * (readings - 1 - i)), CollectionStatus = "complete", MemoryUsedPercent = 60, MemoryTotalBytes = Ram,
            MemoryUsedBytes = Ram * 6 / 10, Processes = [.. processes.OrderByDescending(p => p.WorkingSetBytes)]
        };
    }).ToArray();

    private static HealthFinding SystemAlert(HealthSnapshot[] series)
        => new() { Id = "memory-growing:s0", Time = series[0].Time, Category = "memory", Severity = "warning", Problem = "Memória em crescimento persistente",
            Cause = "Processo responsável ainda não identificado", Recommendation = "Compare o uso por processo antes de intervir" };

    private static HealthFinding ProcessAlert(HealthSnapshot[] series, int at = 1, string sample = "s1", int pid = Pid, string name = Name, long fromMb = 2000, long toMb = 16000)
        => new() { Id = $"process-memory-growing:{pid}:{ProcessStart.UtcTicks}:{sample}", Time = series[at].Time, Category = "memory", Severity = "warning",
            Problem = "Processo com memória em crescimento persistente", Recommendation = "Verifique se o aplicativo está em uso e salve o trabalho antes de qualquer intervenção",
            MemoryGrowth = new(name, fromMb * Mb, toMb * Mb) };

    private static (OccurrenceStatus System, OccurrenceStatus Process) States(HealthSnapshot[] series)
    {
        var shown = HealthOccurrences.Classify([SystemAlert(series), ProcessAlert(series)], series, Limits, series[^1].Time);
        return (shown.Single(o => o.Finding.Id.StartsWith("memory-growing:")).Status, shown.Single(o => o.Finding.Id.StartsWith("process-memory-growing:")).Status);
    }

    private static OccurrenceState State(HealthSnapshot[] series) => States(series).Process.State;

    // Crescimento composto de "percent" a cada 30 min (60 leituras) a partir do alerta do processo: a taxa relativa não diminui com o tempo.
    private static Func<int, double?> Ramp(double percent, double from = 14000) => i => from * Math.Pow(1 + percent, Math.Max(0, i - 1) / 60.0);

    // 64 leituras: 31 min depois do alerta do processo. 60 leituras: 29 min.
    private const int ThirtyOne = 64, TwentyNine = 60;

    private static void Window(Action<bool, string> check)
    {
        var flat = States(Series(ThirtyOne, _ => 14000));
        check(flat.Process.State == OccurrenceState.Stable
            && flat.Process.Label == "Estável em patamar alto (informativo, não é falha ativa): Navegador PID 20248 segue com 14000 MB. "
                + "Patamar de referência: cerca de 14000 MB, o primeiro medido depois do alerta, em 30 min seguidos com variação abaixo de 5% e sem tendência de alta. Volta a persistir se chegar a 15400 MB",
            "#71: processo parado em 14000 MB por 31 min fica estável, com o processo, os MB, a referência e o limite de volta");
        check(flat.System.State == OccurrenceState.Stable && flat.System.Label.Contains("Navegador PID 20248 segue com 14000 MB"),
            "#71: a ocorrência de memória do sistema ligada ao processo acompanha o patamar estável");
        var series = Series(ThirtyOne, _ => 14000);
        var shown = HealthOccurrences.Classify([SystemAlert(series), ProcessAlert(series)], series, Limits, Now);
        check(shown.All(o => HealthOccurrences.ShownSeverity(o.Finding, o.Status) == "info") && shown.All(o => o.Finding.Severity == "warning"),
            "#71: o patamar estável é exibido como informativo, sem alterar a severidade gravada");
        var process = shown.Single(o => o.Finding.Id.StartsWith("process-memory-growing:")).Finding;
        var system = shown.Single(o => o.Finding.Id.StartsWith("memory-growing:")).Finding;
        check(process.Problem == "Processo mantém a memória num patamar estável, sem passar de 10% sobre o patamar de referência" && system.Problem == "Uso de memória ligado a processo em patamar estável"
            && !process.Recommendation.Contains("Verifique") && !system.Recommendation.Contains("Compare") && process.Recommendation.StartsWith("Nenhuma ação recomendada"),
            "#71: no patamar estável, o problema exibido acompanha o estado e não há recomendação de ação");
        check(shown.All(o => o.Finding.Problem.Contains("patamar")) && series.Length == ThirtyOne
            && HealthOccurrences.Classify([ProcessAlert(series)], series, Limits, Now).Single().Finding.Problem == "Processo mantém a memória num patamar estável, sem passar de 10% sobre o patamar de referência",
            "#71: o texto do patamar vale também para a ocorrência do processo sozinha");
        var stored = ProcessAlert(series);
        HealthOccurrences.Classify([stored], series, Limits, Now);
        check(stored.Problem == "Processo com memória em crescimento persistente" && stored.Recommendation.StartsWith("Verifique"), "#71: o registro gravado não é alterado, só a cópia exibida");

        var short29 = States(Series(TwentyNine, _ => 14000));
        check(short29.Process.State == OccurrenceState.Persistent && short29.System.State == OccurrenceState.Persistent && short29.Process.Label.StartsWith("Persiste: Navegador PID 20248 segue com 14000 MB"),
            "#71: com 29 min de patamar depois do alerta, as duas ocorrências ainda persistem");
        // A janela é medida em tempo: com coleta a cada 60 s, 31 min também bastam e 29 min não.
        HealthSnapshot[] Sparse(int minutes) => [.. Series(minutes + 2, _ => 14000).Select((s, i) => { s.Time = Now.AddMinutes(-(minutes + 1 - i)); return s; })];
        OccurrenceState Slow(int minutes) { var s = Sparse(minutes); return HealthOccurrences.Classify([ProcessAlert(s)], s, Limits, Now).Single().Status.State; }
        check(Slow(31) == OccurrenceState.Stable && Slow(29) == OccurrenceState.Persistent, "#71: a janela de 30 min vale em tempo, também com coleta a cada 60 s");
    }

    private static void Amplitude(Action<bool, string> check)
    {
        // Oscilação entre dois níveis, sem inclinação: amplitude/média de 4,8% e de 5,2%.
        double High(double ratio) => 14000 * (2 + ratio) / (2 - ratio);
        check(State(Series(ThirtyOne, i => i % 2 == 0 ? 14000 : High(0.048))) == OccurrenceState.Stable, "#71: oscilação de 4,8% em torno do patamar continua estável");
        var outside = States(Series(ThirtyOne, i => i % 2 == 0 ? 14000 : High(0.052)));
        check(outside.Process.State == OccurrenceState.Persistent && outside.System.State == OccurrenceState.Persistent, "#71: oscilação de 5,2% não é patamar estável");
    }

    private static void Slope(Action<bool, string> check)
    {
        // Inclinação da regressão linear projetada para 30 min: 1,9% passa, 2,1% não.
        check(State(Series(ThirtyOne, Ramp(0.019))) == OccurrenceState.Stable, "#71: rampa de 1,9% em 30 min ainda é patamar");
        check(State(Series(ThirtyOne, Ramp(0.021))) == OccurrenceState.Persistent, "#71: rampa de 2,1% em 30 min não é patamar");
        check(State(Series(240, Ramp(0.021))) == OccurrenceState.Persistent, "#71: rampa de 2,1% em 30 min por 2 h nunca forma patamar");
        // O degrau no meio da janela conta pela inclinação, sem a folga das médias dos quartos.
        check(State(Series(ThirtyOne, i => i < 32 ? 14000 : 14000 * 1.019)) == OccurrenceState.Persistent, "#71: degrau de 1,9% no meio da janela tem inclinação acima de 2% e não é patamar");
        var leak = States(Series(ThirtyOne, Ramp(0.03)));
        check(leak.Process.State == OccurrenceState.Persistent && leak.System.State == OccurrenceState.Persistent && leak.Process.Label.StartsWith("Persiste:"),
            "#71: vazamento lento de ~3% em 30 min continua persistindo");
        check(State(Series(362, Ramp(0.025))) == OccurrenceState.Persistent, "#71: sonda do @qa: 2,5% em 30 min por 3 h (+15%) continua persistindo");
    }

    private static void Accumulated(Action<bool, string> check)
    {
        // Sonda do @qa: degraus de +9% a cada 35 min (70 leituras). O 1º patamar fica em 14000 MB e é a referência fixa.
        Func<int, double?> Steps(int count) => i => 14000 * Math.Pow(1.09, Math.Min(count, Math.Max(0, i - 1) / 70));
        check(State(Series(1 + 70 + 40, Steps(3))) == OccurrenceState.Stable, "#71: depois do 1º degrau de +9% sobre a referência, ainda estável");
        check(State(Series(1 + 140 + 40, Steps(3))) == OccurrenceState.Persistent, "#71: no 2º degrau, o acumulado passa de 10% sobre a referência e volta a persistir");
        check(State(Series(1 + 70 * 4 + 10, Steps(3))) == OccurrenceState.Persistent, "#71: sonda do @qa: degraus de +9% a cada 35 min (+30%) continuam persistindo");
        check(State(Series(1 + 140 + 240, Steps(2))) == OccurrenceState.Persistent,
            "#71: depois de passar de 10% sobre a referência, 2 h paradas no nível novo não voltam a ser estáveis");
        // Sonda do @qa: 1,9% em 30 min. Por 2 h, o acumulado sobre a referência fica abaixo de 10%; por 3,5 h, passa.
        var twoHours = States(Series(242, Ramp(0.019, 10000)));
        check(twoHours.Process.State == OccurrenceState.Stable && twoHours.Process.Label.Contains("Volta a persistir se chegar a 11104 MB"),
            "#71: sonda do @qa: 1,9% em 30 min por 2 h (+7,6%) fica estável, com o limite de volta calculado da referência fixa");
        check(State(Series(422, Ramp(0.019, 10000))) == OccurrenceState.Persistent, "#71: 1,9% em 30 min por 3,5 h passa de 10% sobre a referência e volta a persistir");
    }

    private static void Rise(Action<bool, string> check)
    {
        // Patamar de 31 min seguido de 4 leituras mais altas.
        OccurrenceStatus After(double ratio, int extra = 4) => States(Series(ThirtyOne + extra, i => i < ThirtyOne ? 14000 : 14000 * (1 + ratio))).Process;
        check(After(0.09).State == OccurrenceState.Stable, "#71: subida de 9% sobre a referência mantém o estado estável");
        var back = After(0.11);
        check(back.State == OccurrenceState.Persistent && back.Label.Contains("segue com 15540 MB"), "#71: subida de 11% sobre a referência volta a ocorrência a persistir");
        check(States(Series(ThirtyOne + 4, i => i < ThirtyOne ? 14000 : 15540)).System.State == OccurrenceState.Persistent, "#71: a do sistema também volta a persistir com a nova subida");
        check(After(0.11, 70).State == OccurrenceState.Persistent, "#71: um novo patamar depois de passar de 10% não volta a ser estável sem novo alerta");
        check(States(Series(ThirtyOne + 70, i => i is >= ThirtyOne and < ThirtyOne + 2 ? 15540 : 14000)).Process.State == OccurrenceState.Persistent,
            "#71: um pico de 11% que depois volta ao patamar não apaga a volta a persistir");

        // Novo alerta do mesmo processo: a referência recomeça nele.
        var series = Series(ThirtyOne + 70, i => i < ThirtyOne ? 14000 : 15540);
        var newer = ProcessAlert(series, ThirtyOne + 2, "s9");
        var shown = HealthOccurrences.Classify([ProcessAlert(series), newer], series, Limits, Now);
        check(shown.Single(o => o.Finding.Id.EndsWith(":s9")).Status.State == OccurrenceState.Stable
            && shown.Single(o => o.Finding.Id.EndsWith(":s1")).Status.State == OccurrenceState.Unknown,
            "#71: novo alerta do processo recomeça a referência: 30 min parados depois dele voltam a ser estáveis");
        var fresh = Series(ThirtyOne, _ => 14000);
        var recent = HealthOccurrences.Classify([ProcessAlert(fresh), ProcessAlert(fresh, ThirtyOne - 3, "s9")], fresh, Limits, Now);
        check(recent.Single(o => o.Finding.Id.EndsWith(":s9")).Status.State == OccurrenceState.Persistent, "#71: logo depois de um novo alerta, a ocorrência persiste");
    }

    private static void Breaks(Action<bool, string> check)
    {
        // Processo encerrado: três leituras sem ele resolvem, como antes da #71.
        var exited = States(Series(ThirtyOne + 3, i => i < ThirtyOne ? 14000 : null));
        check(exited.Process.State == OccurrenceState.Resolved && exited.System.State == OccurrenceState.Resolved, "#71: processo encerrado depois do patamar segue a regra de fim");
        // Lacuna maior que o intervalo máximo antes do 1º patamar: não se sabe qual foi ele.
        check(State(Series(ThirtyOne + 40, _ => 14000).Where((_, i) => i is < 30 or > 32).ToArray()) == OccurrenceState.Persistent,
            "#71: lacuna de 2 min antes do 1º patamar impede o patamar");
        // Depois da referência, a lacuna não apaga o patamar: o que conta é não passar de 10% sobre ela.
        check(State(Series(ThirtyOne + 40, _ => 14000).Where((_, i) => i is < 80 or > 82).ToArray()) == OccurrenceState.Stable,
            "#71: lacuna depois do 1º patamar mantém o estado estável");
        var failed = Series(ThirtyOne, _ => 14000);
        failed[40].Processes = [];
        check(State(failed) == OccurrenceState.Persistent, "#71: falha na lista de processos antes do 1º patamar impede o patamar");
        check(State(Series(ThirtyOne, i => i == 20 ? 13000 : 14000)) == OccurrenceState.Persistent, "#71: uma queda de 7% dentro da janela também conta na amplitude");
        // Sem as leituras do início do alerta, não se sabe qual foi o 1º patamar.
        var full = Series(ThirtyOne + 40, _ => 14000);
        var missing = HealthOccurrences.Classify([ProcessAlert(full)], [.. full[..1], .. full[12..]], Limits, Now).Single().Status.State;
        var complete = HealthOccurrences.Classify([ProcessAlert(full)], full, Limits, Now).Single().Status.State;
        check(missing == OccurrenceState.Persistent && complete == OccurrenceState.Stable, "#71: sem as leituras do início do alerta, não se sabe qual foi o 1º patamar e a ocorrência persiste");
        // Abaixo da marca (metade da subida) o processo liberou a memória: o patamar não se aplica.
        check(States(Series(ThirtyOne, _ => 8000)).Process.State == OccurrenceState.Resolved, "#71: patamar abaixo da metade da subida resolve, como antes");
        var other = Series(ThirtyOne, _ => 14000);
        foreach (var s in other) s.Processes = [.. s.Processes.Select(p => p.Name == Name ? p with { StartTime = ProcessStart.AddMinutes(1) } : p)];
        check(State(other) != OccurrenceState.Stable, "#71: o patamar exige o mesmo PID e o mesmo início");
        var unreadable = Series(ThirtyOne, _ => 14000);
        unreadable[40].Processes = [.. unreadable[40].Processes.Select(p => p.Name == Name ? p with { StartTime = null } : p)];
        check(State(unreadable) == OccurrenceState.Persistent, "#71: leitura sem início legível antes do 1º patamar impede o patamar");
    }

    private static void TwoProcesses(Action<bool, string> check)
    {
        // Dois processos ligados ao alerta do sistema: o Navegador para em 14000 MB e o "Vaza" (alerta na leitura 2) segue crescendo 3% em 30 min.
        ProcessHealth[] Leaking(int i) => [new("Vaza", 7777, (long)(4500 * (1 + 0.03 * i / 60.0) * Mb), null, ProcessStart)];
        var series = Series(ThirtyOne + 2, _ => 14000, extra: Leaking);
        var leaking = ProcessAlert(series, 2, "s2", 7777, "Vaza", 1000, 5000);
        foreach (var order in new[] { new[] { SystemAlert(series), ProcessAlert(series), leaking }, new[] { SystemAlert(series), leaking, ProcessAlert(series) } })
        {
            var shown = HealthOccurrences.Classify(order, series, Limits, Now);
            var system = shown.Single(o => o.Finding.Id.StartsWith("memory-growing:"));
            check(system.Finding.Cause.Contains("Navegador PID 20248") && system.Finding.Cause.Contains("Vaza PID 7777") && system.Status.State == OccurrenceState.Persistent
                && system.Status.Label.Contains("Vaza PID 7777") && system.Finding.Problem == "Memória em crescimento persistente",
                "#71: com um processo em patamar e outro crescendo, a ocorrência do sistema persiste pelo que cresce");
            check(shown.Single(o => o.Finding.Id.EndsWith(":s1")).Status.State == OccurrenceState.Stable && shown.Single(o => o.Finding.Id.EndsWith(":s2")).Status.State == OccurrenceState.Persistent,
                "#71: cada processo tem o próprio estado: o parado fica estável, o que cresce persiste");
        }
    }

    // Grava as leituras no banco uma a uma, como o monitor, e grava o patamar a cada leitura.
    private static void Monitor(OperationalStore db, IEnumerable<HealthSnapshot> series)
    {
        foreach (var sample in series) { db.SaveSnapshot(sample); HealthOccurrences.RecordPlateaus(db, sample, Limits); }
    }

    private static void WithStore(Action<OperationalStore> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-memory-plateau-" + Guid.NewGuid().ToString("N"));
        try { using var db = new OperationalStore(root); body(db); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static HealthFinding Stored(OperationalStore db) => db.RecentFindingsWithPrefix("process-memory-growing:", 5).Single();

    private static void Recorder(Action<bool, string> check)
    {
        // O monitor grava a referência quando o 1º patamar é comprovado: média de 14000 MB, na leitura que fecha os 30 min.
        WithStore(db =>
        {
            var series = Series(ThirtyOne + 10, _ => 14000, DateTimeOffset.UtcNow.AddSeconds(-5));
            db.SaveFindings([ProcessAlert(series)]);
            Monitor(db, series[..61]);
            check(Stored(db).MemoryPlateau == null, "#71: antes de 30 min depois do alerta, o monitor não grava patamar");
            Monitor(db, series[61..]);
            var plateau = Stored(db).MemoryPlateau;
            check(plateau == new MemoryPlateauEvidence(Pid, ProcessStart.UtcTicks, 14000 * Mb, series[61].Time),
                "#71: o monitor grava na ocorrência o PID, o início, a média de referência e o instante do 1º patamar");
            check(Stored(db).Problem == "Processo com memória em crescimento persistente" && Stored(db).Severity == "warning",
                "#71: gravar o patamar não muda o resto da ocorrência");
            // Subida de 11% depois: a passagem fica gravada e não se apaga quando o processo volta ao patamar.
            var later = Series(6, i => i < 2 ? 15540 : 14000, series[^1].Time.AddSeconds(180));
            Monitor(db, later);
            var exceeded = Stored(db).MemoryPlateau;
            check(exceeded?.ExceededAt == later[0].Time && exceeded.ExceededBytes == 15540 * Mb && exceeded.ReferenceBytes == 14000 * Mb,
                "#71: a 1ª leitura 10% acima da referência fica gravada na ocorrência");
            check(HealthOccurrences.Classify([Stored(db)], db.RecentSnapshots(DateTimeOffset.UtcNow.AddHours(-1), 20), Limits, later[^1].Time).Single().Status.State == OccurrenceState.Persistent,
                "#71: com a passagem gravada, a ocorrência persiste mesmo com as leituras recentes de volta ao patamar");
        });
        // Lacuna antes do 1º patamar: o monitor grava que o patamar não pode ser comprovado e não tenta de novo.
        WithStore(db =>
        {
            var series = Series(ThirtyOne + 40, _ => 14000, DateTimeOffset.UtcNow.AddSeconds(-5)).Where((_, i) => i is < 30 or > 32).ToArray();
            db.SaveFindings([ProcessAlert(series)]);
            Monitor(db, series);
            var plateau = Stored(db).MemoryPlateau;
            check(plateau is { ReferenceBytes: null, Unavailable: "lacuna ou falha de leitura antes do 1º patamar" },
                "#71: lacuna antes do 1º patamar fica gravada como patamar não comprovável");
            check(HealthOccurrences.Classify([Stored(db)], db.RecentSnapshots(DateTimeOffset.UtcNow.AddHours(-1), 20), Limits, series[^1].Time).Single().Status.State == OccurrenceState.Persistent,
                "#71: sem patamar comprovável, a ocorrência persiste");
        });
        // Sem as leituras do início do alerta no banco (o Nexo abriu depois dele), o patamar fica gravado como não comprovável.
        WithStore(db =>
        {
            var series = Series(ThirtyOne + 40, _ => 14000, DateTimeOffset.UtcNow.AddSeconds(-5));
            db.SaveFindings([ProcessAlert(series)]);
            Monitor(db, series[20..]);
            check(Stored(db).MemoryPlateau is { ReferenceBytes: null, Unavailable: "sem as leituras do início do alerta" },
                "#71: sem as leituras do início do alerta no banco, o patamar fica gravado como não comprovável");
        });
        // Vazamento: a janela atual nunca é patamar, então a série desde o alerta não é lida e nada é gravado.
        WithStore(db =>
        {
            var series = Series(180, Ramp(0.03), DateTimeOffset.UtcNow.AddSeconds(-5));
            db.SaveFindings([ProcessAlert(series)]);
            Monitor(db, series);
            check(Stored(db).MemoryPlateau == null, "#71: vazamento de 3% em 30 min não grava patamar");
        });
        // Sonda do @qa: 1,9% em 30 min por 3,5 h: a referência é gravada na 1ª janela e a passagem de 10% depois.
        WithStore(db =>
        {
            var series = Series(422, Ramp(0.019, 10000), DateTimeOffset.UtcNow.AddSeconds(-5));
            db.SaveFindings([ProcessAlert(series)]);
            Monitor(db, series);
            var plateau = Stored(db).MemoryPlateau;
            check(plateau?.ProvenAt == series[61].Time && plateau.ExceededAt != null && plateau.ExceededBytes >= plateau.ReferenceBytes * 1.1,
                "#71: vazamento de 1,9% em 30 min grava a referência e depois a passagem de 10%");
            var line = HealthReportPresentation.FromStore(db, Limits).ReplaceLineEndings("\n").Split('\n').SingleOrDefault(l => l.Contains("Processo com memória em crescimento persistente"));
            check(line != null && line.StartsWith("Achado memory (warning)") && line.Contains("persiste: Navegador PID 20248"),
                "#71: relatório do assistente mostra o vazamento de 1,9% em 30 min por 3,5 h persistindo");
        });
        // O monitor real chama a gravação a cada leitura.
        WithStore(db =>
        {
            var series = Series(ThirtyOne, _ => 14000, DateTimeOffset.UtcNow.AddSeconds(-5));
            db.SaveFindings([ProcessAlert(series)]);
            foreach (var sample in series[..^1]) db.SaveSnapshot(sample);
            using var engine = new MonitoringEngine(new QueueSampler(new Queue<HealthSnapshot>([series[^1]])), db, TimeSpan.FromSeconds(30), Limits);
            engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            check(Stored(db).MemoryPlateau?.ReferenceBytes == 14000 * Mb && engine.LastError == null, "#71: o monitor grava o patamar na leitura que o comprova");
        });
    }

    private static void Recorded(Action<bool, string> check)
    {
        // Alerta de 3 dias atrás com a referência gravada: a classificação só usa as 20 leituras recentes.
        var recent = Series(20, _ => 14000);
        HealthFinding Old(MemoryPlateauEvidence? plateau)
        {
            var alert = ProcessAlert(recent);
            alert.Time = Now.AddDays(-3);
            alert.MemoryPlateau = plateau;
            return alert;
        }
        var proven = new MemoryPlateauEvidence(Pid, ProcessStart.UtcTicks, 14000 * Mb, Now.AddDays(-3).AddMinutes(31));
        var stable = HealthOccurrences.Classify([Old(proven)], recent, Limits, Now).Single();
        check(stable.Status.State == OccurrenceState.Stable && stable.Status.Label.Contains("cerca de 14000 MB") && stable.Finding.Problem.StartsWith("Processo mantém a memória"),
            "#71: alerta de 3 dias com a referência gravada e o processo estável fica estável, só com as leituras recentes");
        var near = Series(20, i => i < 19 ? 14000 : 15260);
        check(HealthOccurrences.Classify([Old(proven)], near, Limits, Now).Single().Status.State == OccurrenceState.Stable, "#71: 3 dias depois, 9% sobre a referência gravada continua estável");
        var up = Series(20, i => i < 19 ? 14000 : 15410);
        var back = HealthOccurrences.Classify([Old(proven)], up, Limits, Now).Single().Status;
        check(back.State == OccurrenceState.Persistent && back.Label.StartsWith("Persiste:"), "#71: 3 dias depois, 10% sobre a referência gravada volta a persistir");
        var earlier = Series(20, i => i == 5 ? 15410 : 14000);
        check(HealthOccurrences.Classify([Old(proven)], earlier, Limits, Now).Single().Status.State == OccurrenceState.Persistent,
            "#71: uma leitura recente 10% acima, mesmo que já tenha voltado, mantém a ocorrência persistindo");
        check(HealthOccurrences.Classify([Old(proven with { ExceededAt = Now.AddDays(-1), ExceededBytes = 15500 * Mb })], recent, Limits, Now).Single().Status.State == OccurrenceState.Persistent,
            "#71: com a passagem de 10% gravada, a ocorrência de 3 dias persiste até um novo alerta");
        check(HealthOccurrences.Classify([Old(null)], recent, Limits, Now).Single().Status.State == OccurrenceState.Persistent,
            "#71: ocorrência antiga sem referência gravada e sem leituras desde o alerta persiste");
        check(HealthOccurrences.Classify([Old(proven with { Pid = 4242 })], recent, Limits, Now).Single().Status.State == OccurrenceState.Persistent
            && HealthOccurrences.Classify([Old(proven with { StartTicks = proven.StartTicks + 1 })], recent, Limits, Now).Single().Status.State == OccurrenceState.Persistent,
            "#71: referência gravada para outro PID ou outro início não vale");
        check(HealthOccurrences.Classify([Old(new(Pid, ProcessStart.UtcTicks, null, null, Unavailable: "lacuna ou falha de leitura antes do 1º patamar"))], recent, Limits, Now).Single().Status.State == OccurrenceState.Persistent,
            "#71: patamar gravado como não comprovável persiste");
        var released = Series(20, _ => 8000);
        check(HealthOccurrences.Classify([Old(proven)], released, Limits, Now).Single().Status.State == OccurrenceState.Resolved,
            "#71: com a referência gravada, o processo que libera a memória resolve, como antes");
        // A ocorrência gravada lê o formato novo e o antigo.
        var json = System.Text.Json.JsonSerializer.Serialize(Old(proven));
        var read = System.Text.Json.JsonSerializer.Deserialize<HealthFinding>(json)!;
        check(read.MemoryPlateau == proven && !System.Text.Json.JsonSerializer.Serialize(ProcessAlert(recent)).Contains("MemoryPlateau"),
            "#71: a referência gravada vai e volta do banco, e ocorrência sem ela fica no formato antigo");

        // Relatório do assistente com a referência gravada pelo monitor: informativo, sem recomendação de ação.
        WithStore(db =>
        {
            var series = Series(242, _ => 14000, DateTimeOffset.UtcNow.AddSeconds(-5));
            db.SaveFindings([SystemAlert(series), ProcessAlert(series)]);
            Monitor(db, series);
            var lines = HealthReportPresentation.FromStore(db, Limits).ReplaceLineEndings("\n").Split('\n');
            var process = lines.SingleOrDefault(line => line.Contains("Processo mantém a memória num patamar estável"));
            check(process != null && process.StartsWith("Achado memory (info)") && process.Contains("estável em patamar alto (informativo, não é falha ativa): Navegador PID 20248 segue com 14000 MB")
                && process.Contains("Recomendação: Nenhuma ação recomendada") && !lines.Any(line => line.Contains("Verifique se o aplicativo")),
                "#71: relatório do assistente mostra o patamar como informativo, com o processo e os MB e sem recomendação de ação");
            check(!lines.Any(line => line.Contains("persiste:") || line.Contains("crescimento persistente")), "#71: relatório do assistente não apresenta o patamar como problema atual");
            var readings = db.ProcessReadings(Pid, ProcessStart.UtcTicks, series[1].Time);
            check(readings.Count == 241 && readings.All(r => r.Bytes == 14000 * Mb) && readings[0].Time == series[1].Time,
                "#71: o banco devolve o working set do processo em cada leitura desde o alerta");
            check(db.ProcessReadings(Pid, ProcessStart.AddMinutes(1).UtcTicks, series[1].Time).All(r => r.Bytes == null)
                && db.ProcessReadings(4242, ProcessStart.UtcTicks, series[1].Time).All(r => r.Bytes == null),
                "#71: outro início ou outro PID não contam como o processo apontado nas leituras do banco");
        });
    }

    private sealed class QueueSampler(Queue<HealthSnapshot> samples) : IHealthSampler
    {
        public Task<HealthSnapshot> Sample(CancellationToken token) => Task.FromResult(samples.Dequeue());
    }
}
