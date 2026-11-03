using System.Collections;
using Nexo.Support;

namespace Nexo.Checks;

internal static class MonitoringChecks
{
    public static void Run(Action<bool, string> check)
    {
        var assembly = typeof(Store).Assembly;
        var rules = assembly.GetType("Nexo.Support.HealthRules");
        check(rules != null, "Regras preventivas disponíveis");
        var settings = Activator.CreateInstance(assembly.GetType("Nexo.Support.HealthThresholds")!)!;
        var evaluate = rules!.GetMethod("Evaluate")!;
        List<HealthFinding> Eval(params HealthSnapshot[] samples) => ((IEnumerable)evaluate.Invoke(null, [samples, settings])!).Cast<HealthFinding>().ToList();
        var now = DateTimeOffset.UtcNow;
        HealthSnapshot At(int seconds, double? cpu = null, double? memory = null, string connectivity = "unknown")
            => new() { Time = now.AddSeconds(seconds), CpuPercent = cpu, MemoryUsedPercent = memory, Connectivity = connectivity, CollectionStatus = "complete" };

        check(Eval(At(-60, 99), At(-30, 20), At(0, 20)).All(f => f.Category != "cpu"), "Pico isolado de CPU não gera alerta");
        check(Eval(At(-60, 93), At(-30, 94), At(0, 95)).Count(f => f.Category == "cpu") == 1, "CPU alta persistente gera um alerta");
        check(Eval(At(-300, 95), At(-30, 95), At(0, 95)).All(f => f.Category != "cpu"), "Lacuna de coleta quebra persistência da CPU");
        var disks = new[] { At(-30), At(0) };
        foreach (var sample in disks) sample.Disks.Add(new DiskHealth("C:", 4, 100));
        check(Eval(disks).Any(f => f.Category == "disk" && f.Evidence.Contains("4")), "Disco com pouco espaço persistente gera evidência");
        var service = new[] { At(-30), At(0) };
        foreach (var sample in service) sample.Services.Add(new ServiceHealth("Audiosrv", "Stopped", "Automatic"));
        check(Eval(service).Any(f => f.Category == "service"), "Serviço automático parado em duas leituras gera alerta");
        var demand = new[] { At(-30), At(0) };
        foreach (var sample in demand) sample.Services.Add(new ServiceHealth("Spooler", "Stopped", "Manual"));
        check(Eval(demand).All(f => f.Category != "service"), "Serviço sob demanda parado não é falha");
        var network = Eval(At(-60, connectivity: "none"), At(-30, connectivity: "none"), At(0, connectivity: "none"));
        check(network.Any(f => f.Category == "network"), "Falha de conectividade recorrente gera alerta");
        check(Eval(At(-60, 10, 55), At(-30, 10, 70), At(0, 10, 85)).Any(f => f.Category == "memory"), "Crescimento sustentado de memória gera alerta preventivo");

        var root = Path.Combine(Path.GetTempPath(), "nexo-monitor-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var operational = new OperationalStore(root);
            var current = At(0, 95);
            var sampler = new FakeSampler(() => current);
            using var engine = new MonitoringEngine(sampler, operational, TimeSpan.FromMinutes(1));
            engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            current = At(30, 95); engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            current = At(60, 95); engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            check(operational.RecentFindings(10).Count(f => f.Category == "cpu") == 1, "Monitor grava primeiro alerta persistente");
            current = At(90, 95); engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            check(operational.RecentFindings(10).Count(f => f.Category == "cpu") == 1, "Monitor suprime alerta duplicado enquanto persiste");
            current = At(120, 20); engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            current = At(150, 95); engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            current = At(180, 95); engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            current = At(210, 95); engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            check(operational.RecentFindings(10).Count(f => f.Category == "cpu") == 2, "Recorrência após resolução cria nova ocorrência");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var count = sampler.Count;
            try { engine.RunOnceAsync(cancelled.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            check(sampler.Count == count, "Cancelamento impede nova coleta");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        if (Environment.GetEnvironmentVariable("NEXO_LIVE_SAMPLE") == "1")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var live = new WindowsHealthSampler().Sample(timeout.Token).GetAwaiter().GetResult();
            check(live.Disks.Count > 0 || live.Warnings.Count > 0, "Coleta real de leitura retorna discos ou limitação explícita");
            Console.WriteLine($"AMOSTRA: CPU={live.CpuPercent?.ToString("F0") ?? "indisponível"}, memória={live.MemoryUsedPercent?.ToString("F0") ?? "indisponível"}, discos={live.Disks.Count}, serviços={live.Services.Count}, estado={live.CollectionStatus}");
        }
    }

    private sealed class FakeSampler(Func<HealthSnapshot> next) : IHealthSampler
    {
        public int Count { get; private set; }
        public Task<HealthSnapshot> Sample(CancellationToken token) { token.ThrowIfCancellationRequested(); Count++; return Task.FromResult(next()); }
    }
}
