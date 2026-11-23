using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Nexo.Support;

// Métricas ao vivo (#141): leitura separada do monitor de saúde, a cada segundo e só com a tela Saúde do PC visível. Nada daqui
// vai para o operations.db nem muda as ocorrências: o MonitoringEngine continua com o próprio intervalo (30 s por padrão) e o
// próprio caminho (WindowsHealthSampler, por CIM). As fontes são APIs do Windows que não pedem driver nem administrador.

public enum LiveStatus { Waiting, Ok, Unavailable }

/// <summary>Tempos acumulados do GetSystemTimes, em unidades de 100 ns. O tempo de kernel já inclui o ocioso.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
public readonly record struct MemoryStatus(ulong TotalBytes, ulong AvailableBytes);
/// <summary>Contadores do disco físico (_Total) já em taxa: o PDH calcula a taxa entre duas coletas, então a primeira é Waiting.</summary>
public sealed record DiskCounters(LiveStatus Status, double IdlePercent = 0, double ReadBytesPerSecond = 0, double WriteBytesPerSecond = 0, string? Error = null);
/// <summary>Bytes acumulados de um adaptador desde que ele subiu.</summary>
public sealed record AdapterBytes(string Id, string Name, long Received, long Sent);

public interface ILiveMetricsSource : IDisposable
{
    int ProcessorCount { get; }
    CpuTimes ReadCpuTimes();
    MemoryStatus ReadMemory();
    DiskCounters ReadDisk();
    IReadOnlyList<AdapterBytes> ReadAdapters();
    TimeSpan ReadProcessCpuTime();
}

public sealed record LiveMetric(LiveStatus Status, double? Value = null, string? Detail = null)
{
    public static readonly LiveMetric Waiting = new(LiveStatus.Waiting);
    public static LiveMetric Unavailable(string? detail) => new(LiveStatus.Unavailable, null, detail);
}

public sealed record LiveReading(DateTimeOffset Time, LiveMetric Cpu, LiveMetric Memory, ulong? MemoryUsedBytes, ulong? MemoryTotalBytes,
    LiveMetric DiskActive, double? DiskReadBytesPerSecond, double? DiskWriteBytesPerSecond,
    LiveMetric Network, double? ReceiveBitsPerSecond, double? SendBitsPerSecond, string? Adapter, LiveMetric NexoCpu);

/// <summary>
/// Calcula as métricas a partir das leituras acumuladas da fonte. Cada métrica falha sozinha: uma exceção ou um contador
/// ausente vira Unavailable só para ela. A CPU, a rede e o uso de CPU do Nexo precisam de duas amostras (Waiting na primeira).
/// Uma pausa curta (a tela recriada pelo monitor, que remove e devolve o painel) mantém a linha de base e o histórico; uma pausa
/// longa (janela minimizada, escondida na bandeja ou outra tela) descarta os dois, para a 1ª leitura depois da volta não ser a
/// média do tempo parado e o gráfico não ligar pontos separados por minutos.
/// </summary>
public sealed class LiveMetricsSampler
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ResetAfterPause = TimeSpan.FromSeconds(3);
    public const int HistoryCapacity = 60;

    private readonly ILiveMetricsSource source;
    private CpuTimes? lastCpu;
    private Dictionary<string, AdapterBytes>? lastAdapters;
    private TimeSpan? lastProcessCpu;
    private DateTimeOffset? lastTime;
    private DateTimeOffset? pausedAt;
    private readonly Queue<double?>[] history = [new(), new(), new(), new()];
    // A amostra roda fora da thread da interface; pausa, retomada e histórico vêm da interface.
    private readonly object sync = new();

    public LiveMetricsSampler(ILiveMetricsSource source) => this.source = source;

    public bool Paused { get { lock (sync) return pausedAt != null; } }
    public IReadOnlyList<double?> CpuHistory => History(0);
    public IReadOnlyList<double?> MemoryHistory => History(1);
    public IReadOnlyList<double?> DiskHistory => History(2);
    /// <summary>Recebido + enviado, em bits por segundo.</summary>
    public IReadOnlyList<double?> NetworkHistory => History(3);
    private double?[] History(int index) { lock (sync) return history[index].ToArray(); }

    public void Pause(DateTimeOffset now) { lock (sync) pausedAt ??= now; }

    public void Resume(DateTimeOffset now)
    {
        lock (sync)
        {
            if (pausedAt is not { } since) return;
            pausedAt = null;
            if (now - since >= ResetAfterPause) ResetLocked();
        }
    }

    public void Reset() { lock (sync) ResetLocked(); }

    private void ResetLocked()
    {
        lastCpu = null; lastAdapters = null; lastProcessCpu = null; lastTime = null;
        foreach (var queue in history) queue.Clear();
    }

    public LiveReading Sample(DateTimeOffset now) { lock (sync) return SampleLocked(now); }

    private LiveReading SampleLocked(DateTimeOffset now)
    {
        var elapsed = lastTime is { } previous ? (now - previous).TotalSeconds : 0;
        var cpu = Cpu();
        var (memory, used, total) = Memory();
        var (disk, read, write) = Disk();
        var (network, receive, send, adapter) = Network(elapsed);
        var nexo = NexoCpu(elapsed);
        lastTime = now;
        Push(0, cpu.Value); Push(1, memory.Value); Push(2, disk.Value);
        Push(3, network.Status == LiveStatus.Ok ? receive + send : null);
        return new LiveReading(now, cpu, memory, used, total, disk, read, write, network, receive, send, adapter, nexo);
    }

    private void Push(int index, double? value)
    {
        var queue = history[index];
        queue.Enqueue(value);
        while (queue.Count > HistoryCapacity) queue.Dequeue();
    }

    private LiveMetric Cpu()
    {
        CpuTimes current;
        try { current = source.ReadCpuTimes(); }
        catch (Exception ex) { lastCpu = null; return LiveMetric.Unavailable(ex.Message); }
        var previous = lastCpu; lastCpu = current;
        if (previous is not { } before) return LiveMetric.Waiting;
        // Contador que voltou (não deveria) ou intervalo sem tempo: espera a próxima amostra em vez de inventar um valor.
        if (current.Idle < before.Idle || current.Kernel < before.Kernel || current.User < before.User) return LiveMetric.Waiting;
        var idle = current.Idle - before.Idle;
        var total = (current.Kernel - before.Kernel) + (current.User - before.User);
        if (total == 0 || idle > total) return LiveMetric.Waiting;
        return new LiveMetric(LiveStatus.Ok, Clamp(100.0 * (total - idle) / total));
    }

    private (LiveMetric, ulong?, ulong?) Memory()
    {
        MemoryStatus status;
        try { status = source.ReadMemory(); }
        catch (Exception ex) { return (LiveMetric.Unavailable(ex.Message), null, null); }
        if (status.TotalBytes == 0 || status.AvailableBytes > status.TotalBytes) return (LiveMetric.Unavailable("leitura de memória inválida"), null, null);
        var used = status.TotalBytes - status.AvailableBytes;
        return (new LiveMetric(LiveStatus.Ok, Clamp(100.0 * used / status.TotalBytes)), used, status.TotalBytes);
    }

    private (LiveMetric, double?, double?) Disk()
    {
        DiskCounters counters;
        try { counters = source.ReadDisk(); }
        catch (Exception ex) { return (LiveMetric.Unavailable(ex.Message), null, null); }
        return counters.Status switch
        {
            LiveStatus.Ok when double.IsFinite(counters.IdlePercent) && double.IsFinite(counters.ReadBytesPerSecond) && double.IsFinite(counters.WriteBytesPerSecond) =>
                // O PDH pode passar de 100% de ocioso por arredondamento entre coletas; o tempo ativo fica entre 0 e 100.
                (new LiveMetric(LiveStatus.Ok, Clamp(100 - counters.IdlePercent)), Math.Max(0, counters.ReadBytesPerSecond), Math.Max(0, counters.WriteBytesPerSecond)),
            LiveStatus.Ok => (LiveMetric.Unavailable("contador de disco com valor inválido"), null, null),
            LiveStatus.Waiting => (LiveMetric.Waiting, null, null),
            _ => (LiveMetric.Unavailable(counters.Error), null, null),
        };
    }

    private (LiveMetric, double?, double?, string?) Network(double elapsed)
    {
        IReadOnlyList<AdapterBytes> adapters;
        try { adapters = source.ReadAdapters(); }
        catch (Exception ex) { lastAdapters = null; return (LiveMetric.Unavailable(ex.Message), null, null, null); }
        var current = adapters.GroupBy(adapter => adapter.Id).ToDictionary(group => group.Key, group => group.First());
        var previous = lastAdapters; lastAdapters = current;
        if (current.Count == 0) return (LiveMetric.Unavailable("nenhum adaptador de rede ativo"), null, null, null);
        if (previous == null || elapsed <= 0) return (LiveMetric.Waiting, null, null, null);
        double received = 0, sent = 0, busiest = -1; string? name = null; var compared = 0;
        foreach (var (id, now) in current)
        {
            // Adaptador novo nesta amostra, ou contador zerado (o adaptador caiu e voltou): fica fora até a próxima.
            if (!previous.TryGetValue(id, out var before) || now.Received < before.Received || now.Sent < before.Sent) continue;
            compared++;
            double down = now.Received - before.Received, up = now.Sent - before.Sent;
            received += down; sent += up;
            if (down + up > busiest) { busiest = down + up; name = now.Name; }
        }
        if (compared == 0) return (LiveMetric.Waiting, null, null, null);
        var receiveBits = received * 8 / elapsed; var sendBits = sent * 8 / elapsed;
        return (new LiveMetric(LiveStatus.Ok, receiveBits + sendBits), receiveBits, sendBits, name);
    }

    private LiveMetric NexoCpu(double elapsed)
    {
        TimeSpan current;
        try { current = source.ReadProcessCpuTime(); }
        catch (Exception ex) { lastProcessCpu = null; return LiveMetric.Unavailable(ex.Message); }
        var previous = lastProcessCpu; lastProcessCpu = current;
        if (previous is not { } before || elapsed <= 0 || current < before) return LiveMetric.Waiting;
        var cores = Math.Max(1, source.ProcessorCount);
        return new LiveMetric(LiveStatus.Ok, Clamp(100.0 * (current - before).TotalSeconds / (elapsed * cores)));
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 100);

    // Unidades como no Gerenciador de Tarefas: disco em bytes (base 1024), rede em bits (base 1000).
    public static string FormatBytesPerSecond(double? value)
    {
        if (value is not { } bytes) return "—";
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var index = 0;
        while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
        return (index == 0 ? bytes.ToString("F0", CultureInfo.CurrentCulture) : bytes.ToString("F1", CultureInfo.CurrentCulture)) + " " + units[index];
    }

    public static string FormatBitsPerSecond(double? value)
    {
        if (value is not { } bits) return "—";
        string[] units = ["bps", "Kbps", "Mbps", "Gbps"];
        var index = 0;
        while (bits >= 1000 && index < units.Length - 1) { bits /= 1000; index++; }
        return (index == 0 ? bits.ToString("F0", CultureInfo.CurrentCulture) : bits.ToString("F1", CultureInfo.CurrentCulture)) + " " + units[index];
    }

    public static string FormatPercent(LiveMetric metric) => metric.Status switch
    {
        LiveStatus.Ok => metric.Value!.Value.ToString("F0", CultureInfo.CurrentCulture) + "%",
        LiveStatus.Waiting => "medindo…",
        _ => "indisponível",
    };
}

/// <summary>
/// Fonte real. CPU por GetSystemTimes e memória por GlobalMemoryStatusEx (kernel32); disco pelo PDH com nomes em inglês
/// (PdhAddEnglishCounterW), que funciona em Windows de qualquer idioma, ao contrário dos nomes localizados ("Disco Físico") que o
/// PerformanceCounter exige; rede pelos bytes acumulados de cada adaptador ativo (NetworkInterface.GetIPStatistics, IPv4 + IPv6),
/// sem contador de desempenho. Os contadores de desempenho podem estar desligados ou corrompidos no PC: o disco fica indisponível
/// e o resto segue.
/// </summary>
public sealed class WindowsLiveMetricsSource : ILiveMetricsSource
{
    private const uint PdhFmtDouble = 0x00000200, PdhFmtNoCap100 = 0x00008000;
    private IntPtr query, idleCounter, readCounter, writeCounter;
    private string? diskError;
    private bool diskOpened, disposed;

    public int ProcessorCount => Environment.ProcessorCount;

    public CpuTimes ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new InvalidOperationException("GetSystemTimes falhou (" + Marshal.GetLastWin32Error() + ")");
        return new CpuTimes(idle, kernel, user);
    }

    public MemoryStatus ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("GlobalMemoryStatusEx falhou (" + Marshal.GetLastWin32Error() + ")");
        return new MemoryStatus(status.TotalPhys, status.AvailPhys);
    }

    public DiskCounters ReadDisk()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!diskOpened) OpenDisk();
        if (diskError != null) return new DiskCounters(LiveStatus.Unavailable, Error: diskError);
        var collected = PdhCollectQueryData(query);
        if (collected != 0) return new DiskCounters(LiveStatus.Unavailable, Error: "PdhCollectQueryData 0x" + collected.ToString("X8"));
        var idle = Value(idleCounter); var read = Value(readCounter); var write = Value(writeCounter);
        // Depois de uma só coleta, contadores de taxa ainda não têm valor válido.
        if (idle == null || read == null || write == null) return new DiskCounters(LiveStatus.Waiting);
        return new DiskCounters(LiveStatus.Ok, idle.Value, read.Value, write.Value);
    }

    private void OpenDisk()
    {
        diskOpened = true;
        var status = PdhOpenQueryW(null, IntPtr.Zero, out query);
        if (status != 0) { diskError = "PdhOpenQuery 0x" + status.ToString("X8"); return; }
        foreach (var (path, assign) in new (string, Action<IntPtr>)[]
        {
            (@"\PhysicalDisk(_Total)\% Idle Time", handle => idleCounter = handle),
            (@"\PhysicalDisk(_Total)\Disk Read Bytes/sec", handle => readCounter = handle),
            (@"\PhysicalDisk(_Total)\Disk Write Bytes/sec", handle => writeCounter = handle),
        })
        {
            status = PdhAddEnglishCounterW(query, path, IntPtr.Zero, out var counter);
            if (status != 0) { diskError = "contador de disco ausente (" + path + ", 0x" + status.ToString("X8") + ")"; return; }
            assign(counter);
        }
        PdhCollectQueryData(query);
    }

    private static double? Value(IntPtr counter)
    {
        var status = PdhGetFormattedCounterValue(counter, PdhFmtDouble | PdhFmtNoCap100, out _, out var value);
        return status == 0 && value.CStatus <= 1 ? value.DoubleValue : null;
    }

    public IReadOnlyList<AdapterBytes> ReadAdapters()
    {
        var list = new List<AdapterBytes>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var statistics = adapter.GetIPStatistics();
                list.Add(new AdapterBytes(adapter.Id, adapter.Name, statistics.BytesReceived, statistics.BytesSent));
            }
            catch (NetworkInformationException) { }
        }
        return list;
    }

    public TimeSpan ReadProcessCpuTime()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
