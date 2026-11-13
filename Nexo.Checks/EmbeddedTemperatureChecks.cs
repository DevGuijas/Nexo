using System.Text.Json;
using Nexo.Support;
namespace Nexo.Checks;
internal static class EmbeddedTemperatureChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var session = new Session();
        var snapshot = Parse(EmbeddedCpuTemperature.Read(() => session, true, CancellationToken.None));
        check(snapshot.CpuTemperatureC == 57.4 && snapshot.CpuTemperature!.Sensor.StartsWith("CPU Package") && session.Opened && session.Disposed,
            "Temperatura embutida: lê pacote e fecha sessão sem aplicativo WMI");
        var created = false;
        var missing = Parse(EmbeddedCpuTemperature.Read(() => { created = true; return new Session(); }, false, CancellationToken.None));
        check(!created && missing.CpuTemperature is { State: "no_source", Celsius: null } && missing.CpuTemperature.Detail.Contains("PawnIO"),
            "Temperatura embutida: driver ausente não inventa leitura nem abre hardware");
        var broken = new Session { Fail = true };
        var failed = Parse(EmbeddedCpuTemperature.Read(() => broken, true, CancellationToken.None));
        check(broken.Disposed && failed.CpuTemperature is { State: "error", Celsius: null },
            "Temperatura embutida: falha no hardware fecha sessão e informa indisponibilidade");
        var readFailure = new Session { ReadFail = true };
        var failedUpdate = Parse(EmbeddedCpuTemperature.Read(() => readFailure, true, CancellationToken.None));
        check(readFailure.Disposed && failedUpdate.CpuTemperatureC == null && failedUpdate.CpuTemperature!.State == "error",
            "Temperatura embutida: falha ao atualizar não reaproveita valor anterior");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var propagated = false;
        try { EmbeddedCpuTemperature.Read(() => new Session(), true, cancelled.Token); }
        catch (OperationCanceledException) { propagated = true; }
        check(propagated, "Temperatura embutida: cancelamento é preservado");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = false;
        var pending = EmbeddedCpuTemperature.ReadAsync(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return "{}";
        }, CancellationToken.None, TimeSpan.FromMilliseconds(50));
        entered.Task.Wait(TimeSpan.FromSeconds(5));
        try { pending.GetAwaiter().GetResult(); }
        catch (TimeoutException) { timedOut = true; }
        var secondStarted = false;
        var overlapRejected = false;
        try { EmbeddedCpuTemperature.ReadAsync(() => { secondStarted = true; return "{}"; }, CancellationToken.None, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { overlapRejected = true; }
        finally { release.TrySetResult(); }
        check(timedOut && overlapRejected && !secondStarted,
            "Temperatura embutida: timeout não acumula sessões nativas bloqueadas");
        var recovered = SpinWait.SpinUntil(() =>
        {
            try { return EmbeddedCpuTemperature.ReadAsync(() => "{}", CancellationToken.None, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult() == "{}"; }
            catch (InvalidOperationException) { return false; }
        }, TimeSpan.FromSeconds(5));
        check(recovered, "Temperatura embutida: retoma coleta quando a sessão anterior termina");
        if (Environment.GetEnvironmentVariable("NEXO_LIVE_SAMPLE") == "1")
        {
            var live = Parse(EmbeddedCpuTemperature.ReadAsync(CancellationToken.None).GetAwaiter().GetResult());
            check(live.CpuTemperature != null && (live.CpuTemperature.State == "read" ? live.CpuTemperatureC > 0 : live.CpuTemperatureC == null),
                "Temperatura embutida real: retorna valor do sensor ou indisponibilidade explícita");
            Console.WriteLine("CPU EMBUTIDA: " + live.CpuTemperature!.State + "; " + live.CpuTemperature.Detail);
        }
    }
    private static HealthSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var snapshot = new HealthSnapshot();
        HardwareSensors.ReadTemperature(doc.RootElement, snapshot, DateTimeOffset.UtcNow);
        return snapshot;
    }
    private sealed class Session : ICpuSensorSession
    {
        public bool Opened, Disposed, Fail, ReadFail;
        public void Open() { Opened = true; if (Fail) throw new UnauthorizedAccessException("hardware bloqueado"); }
        public IEnumerable<CpuSensorValue> ReadSensors() => ReadFail ? throw new IOException("sensor desconectado") : [new("CPU Package", "/intelcpu/0/temperature/0", 57.4)];
        public void Dispose() => Disposed = true;
    }
}
