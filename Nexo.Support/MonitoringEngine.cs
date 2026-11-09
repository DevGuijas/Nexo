namespace Nexo.Support;

public interface IHealthSampler
{
    Task<HealthSnapshot> Sample(CancellationToken token);
}

/// <summary>Serial collector, active only while the host application is running.</summary>
public sealed class MonitoringEngine : IDisposable
{
    private readonly IHealthSampler sampler;
    private readonly OperationalStore store;
    private readonly TimeSpan interval;
    private readonly HealthThresholds thresholds;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<string> active = [];
    private readonly Dictionary<string, DateTimeOffset> processAlerts = [];
    private CancellationTokenSource? running;
    private Task? loop;
    public event Action<HealthSnapshot, IReadOnlyList<HealthFinding>>? Updated;
    public string? LastError { get; private set; }

    public MonitoringEngine(IHealthSampler sampler, OperationalStore store, TimeSpan interval, HealthThresholds? thresholds = null)
    {
        if (interval < TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(interval));
        this.sampler = sampler; this.store = store; this.interval = interval; this.thresholds = thresholds ?? new();
    }

    public async Task RunOnceAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var snapshot = await sampler.Sample(token);
            store.SaveSnapshot(snapshot);
            var (window, limit) = HistoryWindow(interval, thresholds);
            var history = store.RecentSnapshots(snapshot.Time - window, limit).OrderBy(s => s.Time).ToArray();
            var current = HealthRules.Evaluate(history, thresholds);
            var keys = current.Select(f => f.Id).ToHashSet();
            active.IntersectWith(keys);
            var fresh = current.Where(f => !active.Contains(f.Id) && !(processAlerts.TryGetValue(f.Id, out var seen) && snapshot.Time - seen < thresholds.ProcessMemoryAlertCooldown)).ToArray();
            var freshKeys = fresh.Select(f => f.Id).ToArray();
            foreach (var finding in fresh) finding.Id += ":" + snapshot.Id;
            store.SaveFindings(fresh);
            // Só depois de gravar: uma falha não pode consumir o alerta.
            active.UnionWith(freshKeys);
            // A memória de um processo oscila: uma janela abaixo do limiar não encerra a ocorrência.
            foreach (var expired in processAlerts.Where(alert => snapshot.Time - alert.Value >= thresholds.ProcessMemoryAlertCooldown).Select(alert => alert.Key).ToArray()) processAlerts.Remove(expired);
            foreach (var key in keys.Where(key => key.StartsWith("process-memory-", StringComparison.Ordinal))) processAlerts[key] = snapshot.Time;
            LastError = null;
            Updated?.Invoke(snapshot, fresh);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Histórico consultado a cada leitura. Além das regras de memória, precisa alcançar uma leitura feita
    /// ServiceStableRunning antes da 1ª leitura de um serviço parado, quando ele completa as leituras do alerta (#57).
    /// </summary>
    internal static (TimeSpan Window, int Limit) HistoryWindow(TimeSpan interval, HealthThresholds thresholds)
    {
        var seconds = Math.Max(Math.Max(600, interval.TotalSeconds * Math.Max(5, thresholds.ProcessMemorySamples + 1)),
            thresholds.ServiceStableRunning.TotalSeconds + interval.TotalSeconds * (thresholds.OtherSamples + 2));
        return (TimeSpan.FromSeconds(seconds), Math.Clamp((int)Math.Ceiling(seconds / interval.TotalSeconds) + 2, 20, 1000));
    }

    public void Start()
    {
        if (running != null) return;
        running = new CancellationTokenSource();
        var token = running.Token;
        loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            do
            {
                try { await RunOnceAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { LastError = ex.Message; }
            } while (await timer.WaitForNextTickAsync(token));
        }, token);
    }

    public async Task StopAsync()
    {
        if (running == null) return;
        running.Cancel();
        try { if (loop != null) await loop; }
        catch (OperationCanceledException) { }
        running.Dispose(); running = null; loop = null;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        gate.Dispose();
    }
}
