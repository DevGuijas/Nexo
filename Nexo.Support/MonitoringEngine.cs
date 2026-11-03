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
            var history = store.RecentSnapshots(snapshot.Time.AddMinutes(-10), 20).OrderBy(s => s.Time).ToArray();
            var current = HealthRules.Evaluate(history, thresholds);
            var keys = current.Select(f => f.Id).ToHashSet();
            active.IntersectWith(keys);
            var fresh = current.Where(f => !active.Contains(f.Id)).ToArray();
            foreach (var finding in fresh)
            {
                active.Add(finding.Id);
                finding.Id += ":" + snapshot.Id;
            }
            store.SaveFindings(fresh);
            LastError = null;
            Updated?.Invoke(snapshot, fresh);
        }
        finally { gate.Release(); }
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
