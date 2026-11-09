using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Nexo.Support;

public sealed class HealthSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    public double? CpuPercent { get; set; }
    public double? CpuTemperatureC { get; set; }
    public double? MemoryUsedPercent { get; set; }
    public long? MemoryUsedBytes { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public string StorageHealth { get; set; } = "unavailable";
    public string Connectivity { get; set; } = "unknown";
    public NetworkProbeReading GatewayProbe { get; set; } = new();
    public NetworkProbeReading DnsProbe { get; set; } = new();
    public NetworkProbeReading ExternalProbe { get; set; } = new();
    public string CollectionStatus { get; set; } = "partial";
    public List<DiskHealth> Disks { get; set; } = [];
    public List<ProcessHealth> Processes { get; set; } = [];
    public List<ServiceHealth> Services { get; set; } = [];
    public List<AdapterHealth> Adapters { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed record DiskHealth(string Name, long FreeBytes, long TotalBytes, string Health = "unavailable");
public sealed record ProcessHealth(string Name, int Id, long WorkingSetBytes, double? CpuPercent, DateTimeOffset? StartTime = null);
/// <summary>
/// Serviço automático observado. Os campos opcionais só são lidos para serviços que não estão em execução
/// (Win32_Service.DelayedAutoStart, Win32_Service.ExitCode, Win32_Service.ServiceSpecificExitCode e a chave
/// TriggerInfo do serviço no registro). Nulo quer dizer "não lido"; leituras gravadas antes da #57 não têm esses
/// campos e continuam nulas.
/// </summary>
public sealed record ServiceHealth(string Name, string Status, string StartType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? DelayedAutoStart = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? TriggerStart = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ExitCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ServiceSpecificExitCode = null);
public sealed record AdapterHealth(string Id, string Name, string Status, bool Enabled);
public sealed record NetworkProbeReading(string State = "disabled", string Target = "", string Detail = "", string ConfigurationId = "");

public sealed class HealthFinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    public string Category { get; set; } = "system";
    public string Problem { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Cause { get; set; } = "";
    public string Severity { get; set; } = "info";
    public string Impact { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public double Confidence { get; set; }
    public bool Confirmed { get; set; }
    public string Source { get; set; } = "rules";
    public string ProbeTarget { get; set; } = "";
    public string ProbeConfigurationId { get; set; } = "";
    /// <summary>Só em process-memory-growing gravado a partir da #60; nos demais e nos registros anteriores fica nulo e não é gravado.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ProcessMemoryGrowth? MemoryGrowth { get; set; }
    /// <summary>
    /// Só em service-stopped de serviço que parou com saída normal, sem início atrasado e sem gatilho (#57): guarda
    /// a execução estável observada antes da parada, que é a prova do alerta. Nos demais fica nulo e não é gravado.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ServiceStopEvidence? ServiceStop { get; set; }

    /// <summary>Cópia para exibição: o registro gravado não muda.</summary>
    public HealthFinding WithCause(string cause)
    {
        var copy = (HealthFinding)MemberwiseClone();
        copy.Cause = cause;
        return copy;
    }
}

/// <summary>
/// Subida que disparou o alerta de um processo: nome já filtrado e working set no início e no fim da janela.
/// O PID e o horário de início continuam no identificador da ocorrência.
/// </summary>
public sealed record ProcessMemoryGrowth(string Name, long FromBytes, long ToBytes);

/// <summary>Serviço visto em execução em todas as leituras desde RunningSince até a parada observada em StoppedAt (#57).</summary>
public sealed record ServiceStopEvidence(DateTimeOffset RunningSince, DateTimeOffset StoppedAt);

public sealed class SystemEvent
{
    public string Log { get; set; } = "";
    public long RecordId { get; set; }
    public DateTimeOffset Time { get; set; }
    public string Provider { get; set; } = "";
    public int EventId { get; set; }
    public long Level { get; set; }
}

public sealed class EventGap
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; set; }
    public string Log { get; set; } = "";
    public string Reason { get; set; } = "";
    public long FromRecordId { get; set; }
    public long? ToRecordId { get; set; }
    public string Message { get; set; } = "";
}

public sealed class InterventionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string State { get; set; } = "proposed";
    public string Risk { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string Summary { get; set; } = "";
    public string CommandTemplate { get; set; } = "";
    public bool Authorized { get; set; }
    public bool RebootRequired { get; set; }
    public int? RecoveryPointSequence { get; set; }
    public string PendingOperation { get; set; } = "";
    public List<InterventionTransition> Transitions { get; set; } = [];
}

public sealed record InterventionTransition(DateTimeOffset Time, string State, string Details);

public sealed class DiagnosticReport
{
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public List<HealthFinding> Findings { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}

internal static class ProcessReader
{
    /// <summary>Descreve os 12 processos com maior working set e libera todos os identificadores recebidos.</summary>
    public static List<ProcessHealth> Top(Process[] processes)
    {
        try
        {
            return Largest(processes.Select(Describe).OfType<ProcessHealth>())
                .Select(found => found with { StartTime = StartOf(() => processes.First(p => p.Id == found.Id).StartTime) }).ToList();
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static IEnumerable<ProcessHealth> Largest(IEnumerable<ProcessHealth> processes)
        => processes.OrderByDescending(p => p.WorkingSetBytes).Take(12);

    private static ProcessHealth? Describe(Process process)
    {
        try { return new ProcessHealth(process.ProcessName, process.Id, process.WorkingSet64, null); }
        catch { return null; }
    }

    // Sem privilégio, o Windows pode negar esta leitura: o processo fica na lista, sem identidade confirmada.
    public static DateTimeOffset? StartOf(Func<DateTime> read)
    {
        try { return new DateTimeOffset(read()); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or ArgumentOutOfRangeException) { return null; }
    }
}
