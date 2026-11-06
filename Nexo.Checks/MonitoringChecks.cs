using System.Collections;
using System.Text.Json;
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
        HealthSnapshot ExternalAt(int seconds, string local, string external)
        {
            var sample = At(seconds, connectivity: local);
            sample.ExternalProbe = new NetworkProbeReading(external, "example.com", "resultado do destino configurado");
            return sample;
        }
        check(Eval(ExternalAt(-60, "local", "ok"), ExternalAt(-30, "local", "ok"), ExternalAt(0, "local", "no_response"))
            .All(f => f.Id != "network-external-unconfirmed"), "Falha isolada do destino externo não gera alerta");
        check(Eval(ExternalAt(-60, "local", "no_response"), ExternalAt(-30, "local", "unexpected"), ExternalAt(0, "local", "no_response"))
            .Any(f => f.Id == "network-external-unconfirmed" && f.Evidence.Contains("example.com")),
            "Três falhas consecutivas do destino externo geram alerta com alvo identificado");
        check(Eval(ExternalAt(-60, "local", "no_response"), ExternalAt(-30, "local", "no_response"), ExternalAt(0, "local", "ok"))
            .All(f => f.Id != "network-external-unconfirmed"), "Resposta externa bem-sucedida encerra o alerta");
        check(Eval(ExternalAt(-60, "none", "unavailable"), ExternalAt(-30, "none", "unavailable"), ExternalAt(0, "none", "unavailable"))
            .All(f => f.Id != "network-external-unconfirmed"), "Rede isolada não vira diagnóstico de falha do destino externo");
        check(Eval(ExternalAt(-300, "local", "no_response"), ExternalAt(-30, "local", "no_response"), ExternalAt(0, "local", "no_response"))
            .All(f => f.Id != "network-external-unconfirmed"), "Lacuna entre amostras impede alerta externo persistente");
        check(Eval(ExternalAt(-60, "local", "no_response"), ExternalAt(-30, "local", "unavailable"), ExternalAt(0, "local", "no_response"))
            .All(f => f.Id != "network-external-unconfirmed"), "Sondagem indisponível interrompe a sequência de falhas externas");
        var otherTarget = ExternalAt(0, "local", "no_response");
        otherTarget.ExternalProbe = new NetworkProbeReading("no_response", "other.example", "destino diferente");
        check(Eval(ExternalAt(-60, "local", "no_response"), ExternalAt(-30, "local", "no_response"), otherTarget)
            .All(f => f.Id != "network-external-unconfirmed"), "Troca do destino externo interrompe a sequência de falhas");
        check(Eval(At(-60, 10, 55), At(-30, 10, 70), At(0, 10, 85)).Any(f => f.Category == "memory"), "Crescimento sustentado de memória gera alerta preventivo");

        var reportRoot = Path.Combine(Path.GetTempPath(), "nexo-connectivity-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var reportStore = new OperationalStore(reportRoot);
            var observed = ExternalAt(0, "local", "unexpected");
            observed.GatewayProbe = new NetworkProbeReading("no_response", "192.0.2.1", "sem resposta ICMP");
            observed.DnsProbe = new NetworkProbeReading("ok", "example.com", "resolvido");
            reportStore.SaveSnapshot(observed);
            var text = HealthReportPresentation.FromStore(reportStore);
            check(text.Contains("Gateway:") && text.Contains("DNS:") && text.Contains("Destino externo:")
                && text.Contains("inesperada"), "Relatório separa gateway, DNS e destino externo sem alegar acesso geral");
        }
        finally { if (Directory.Exists(reportRoot)) Directory.Delete(reportRoot, true); }

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
        var slowRoot = Path.Combine(Path.GetTempPath(), "nexo-monitor-slow-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var slowStore = new OperationalStore(slowRoot);
            var current = At(-7200, 95);
            var slowSettings = new HealthSettings { IntervalSeconds = 3600 };
            using var engine = new MonitoringEngine(new FakeSampler(() => current), slowStore, TimeSpan.FromSeconds(slowSettings.IntervalSeconds), slowSettings.Thresholds());
            engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            current = At(-3600, 95); engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            current = At(0, 95); engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            check(slowStore.RecentFindings(10).Any(f => f.Category == "cpu"), "Intervalo configurável de uma hora ainda permite alerta persistente");
        }
        finally { if (Directory.Exists(slowRoot)) Directory.Delete(slowRoot, true); }
        CheckServiceCollection(check);
        if (Environment.GetEnvironmentVariable("NEXO_LIVE_SAMPLE") == "1")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var live = new WindowsHealthSampler().Sample(timeout.Token).GetAwaiter().GetResult();
            check(live.Disks.Count > 0 || live.Warnings.Count > 0, "Coleta real de leitura retorna discos ou limitação explícita");
            Console.WriteLine($"AMOSTRA: CPU={live.CpuPercent?.ToString("F0") ?? "indisponível"}, memória={live.MemoryUsedPercent?.ToString("F0") ?? "indisponível"}, discos={live.Disks.Count}, serviços={live.Services.Count}, estado={live.CollectionStatus}");
        }
    }

    private static void CheckServiceCollection(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var stoppedAtPosition230 = AutomaticServices(250, stoppedPosition: 230);
        var previous = ReadServices(stoppedAtPosition230, now.AddSeconds(-30));
        var latest = ReadServices(stoppedAtPosition230, now);

        check(latest.Services.Count == WindowsHealthSampler.MaxServicesCollected
            && latest.Services[0] is { Name: "Servico230", Status: "Stopped" }
            && latest.Warnings.Single().Contains("200 de 250")
            && latest.CollectionStatus == "partial",
            "Coleta acima do limite mantém o serviço parado da posição 230, informa o corte e fica parcial");

        var findings = HealthRules.Evaluate([previous, latest], new HealthThresholds());
        check(findings.Any(f => f.Id == "service-stopped:Servico230"),
            "Serviço automático parado depois da posição 80 gera alerta preventivo");

        var report = DiagnosticEngine.BuildReport(latest, findings, new EventBatch(), now);
        check(report.Limitations.Contains("A coleta de métricas foi parcial.")
            && report.Limitations.Any(limitation => limitation.Contains("200 de 250")),
            "Relatório de saúde não conclui ausência de falhas quando a coleta de serviços é parcial");

        var complete = ReadServices(AutomaticServices(83, stoppedPosition: null), now);
        check(complete.Services.Count == 83 && complete.Warnings.Count == 0 && complete.CollectionStatus == "complete",
            "Coleta dentro do limite inclui todos os serviços automáticos e fica completa");
    }

    private static object[] AutomaticServices(int count, int? stoppedPosition)
    {
        return Enumerable.Range(1, count)
            .Select(position => (object)new { Name = $"Servico{position:D3}", State = position == stoppedPosition ? "Stopped" : "Running" })
            .ToArray();
    }

    private static HealthSnapshot ReadServices(object[] services, DateTimeOffset time)
    {
        var snapshot = new HealthSnapshot { Time = time };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { Services = services }));
        WindowsHealthSampler.ReadServices(document.RootElement, snapshot);
        WindowsHealthSampler.ApplyCollectionStatus(snapshot);
        return snapshot;
    }

    private sealed class FakeSampler(Func<HealthSnapshot> next) : IHealthSampler
    {
        public int Count { get; private set; }
        public Task<HealthSnapshot> Sample(CancellationToken token) { token.ThrowIfCancellationRequested(); Count++; return Task.FromResult(next()); }
    }
}
