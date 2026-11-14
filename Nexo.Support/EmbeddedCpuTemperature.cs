using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;

namespace Nexo.Support;
internal sealed record CpuSensorValue(string Name, string Identifier, double? Value);
internal interface ICpuSensorSession : IDisposable
{
    void Open();
    IEnumerable<CpuSensorValue> ReadSensors();
}
internal sealed class CpuInitializationCircuit
{
    private string? failure;
    internal string? Failure => Volatile.Read(ref failure);
    internal void Fail(string detail) => Interlocked.CompareExchange(ref failure,
        "A inicialização do sensor embutido falhou. Reinicie o Nexo para tentar novamente. Detalhe: " + detail, null);
}

/// <summary>CPU-only access; never installs drivers or starts an external monitor.</summary>
internal static class EmbeddedCpuTemperature
{
    // A native hardware call cannot safely be killed. Keep its gate until the worker ends, even after timeout.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly CpuInitializationCircuit Initialization = new();
    internal static Task<string> ReadAsync(CancellationToken token) =>
        ReadAsync(() => Read(() => new LibreCpuSession(), PawnIo.IsInstalled, token, Initialization), token, TimeSpan.FromSeconds(HardwareSensors.TemperatureTimeoutSeconds));

    internal static async Task<string> ReadAsync(Func<string> read, CancellationToken token, TimeSpan timeout)
    {
        token.ThrowIfCancellationRequested();
        if (!Gate.Wait(0)) throw new InvalidOperationException("A leitura anterior do sensor ainda está terminando.");
        var work = Task.Run(() =>
        {
            try { return read(); }
            finally { Gate.Release(); }
        });
        return await work.WaitAsync(timeout, token);
    }
    internal static string Read(Func<ICpuSensorSession> create, bool driverInstalled, CancellationToken token, CpuInitializationCircuit initialization)
    {
        token.ThrowIfCancellationRequested();
        if (initialization.Failure is { } previousFailure) return Error(previousFailure);
        if (!driverInstalled)
            return JsonSerializer.Serialize(new { Temperature = new { Available = false, DriverMissing = true, Error = "O driver PawnIO não está instalado. A temperatura da CPU está indisponível." } });
        try
        {
            using var session = create();
            try { session.Open(); }
            catch (Exception ex)
            {
                // LHM 0.9.6 can throw before setting its _open flag, making Close a no-op despite partial setup.
                // Attempt disposal, but never repeat Open in this process after an initialization failure.
                initialization.Fail(ex.Message);
                throw;
            }
            token.ThrowIfCancellationRequested();
            var sensors = session.ReadSensors().ToArray();
            token.ThrowIfCancellationRequested();
            return JsonSerializer.Serialize(new { Temperature = new { Available = true, Sensors = sensors } });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Error(initialization.Failure ?? "Não foi possível acessar o sensor embutido: " + ex.Message);
        }
    }
    private static string Error(string detail) => JsonSerializer.Serialize(new { Temperature = new { Available = false, Error = detail } });
    private sealed class LibreCpuSession : ICpuSensorSession
    {
        private readonly Computer computer = new() { IsCpuEnabled = true };
        public void Open() => computer.Open();
        public IEnumerable<CpuSensorValue> ReadSensors()
        {
            foreach (var cpu in computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
                foreach (var sensor in ReadCpu(cpu)) yield return sensor;
        }
        private static IEnumerable<CpuSensorValue> ReadCpu(IHardware hardware)
        {
            hardware.Update();
            foreach (var sensor in hardware.Sensors.Where(s => s.SensorType == SensorType.Temperature))
                yield return new(sensor.Name, sensor.Identifier.ToString(), sensor.Value is { } value && float.IsFinite(value) ? value : null);
            foreach (var child in hardware.SubHardware)
                foreach (var sensor in ReadCpu(child)) yield return sensor;
        }
        public void Dispose() => computer.Close();
    }
}
