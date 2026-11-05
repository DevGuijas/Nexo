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
    public string CollectionStatus { get; set; } = "partial";
    public List<DiskHealth> Disks { get; set; } = [];
    public List<ProcessHealth> Processes { get; set; } = [];
    public List<ServiceHealth> Services { get; set; } = [];
    public List<AdapterHealth> Adapters { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed record DiskHealth(string Name, long FreeBytes, long TotalBytes, string Health = "unavailable");
public sealed record ProcessHealth(string Name, int Id, long WorkingSetBytes, double? CpuPercent);
public sealed record ServiceHealth(string Name, string Status, string StartType);
public sealed record AdapterHealth(string Id, string Name, string Status, bool Enabled);

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
}

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
    public List<InterventionTransition> Transitions { get; set; } = [];
}

public sealed record InterventionTransition(DateTimeOffset Time, string State, string Details);

public sealed class DiagnosticReport
{
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public List<HealthFinding> Findings { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}
