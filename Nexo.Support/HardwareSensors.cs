using System.Globalization;
using System.Text.Json;

namespace Nexo.Support;

public sealed record TemperatureReading(string State, double? Celsius = null, string Sensor = "", string Source = "", DateTimeOffset? Time = null, string Detail = "");
public sealed record PhysicalDiskReading(string Device, string Name, string MediaType, string BusType, string Health, string[] OperationalStatus, string State);
public sealed record StorageReading(string State, string Source, DateTimeOffset Time, int Total, List<PhysicalDiskReading> Disks, string Detail = "");

public static class HardwareSensors
{
    public const int MaxPhysicalDisks = 16;
    public const string TemperatureSource = "Libre Hardware Monitor (WMI root/LibreHardwareMonitor)";
    public const string StorageSource = "Windows Storage (Get-PhysicalDisk)";
    public const double MaxPlausibleCelsius = 125;
    private static readonly string[] PackageSensors = ["CPU Package", "Package", "Core (Tdie)", "Core (Tctl/Tdie)"];
    private static readonly string[] AlertStatuses = ["Degraded", "Stressed", "Predictive Failure", "Error", "Non-Recoverable Error", "No Contact", "Lost Communication", "Aborted", "Supporting Entity in Error", "Failed Media", "Split", "Stale Metadata", "IO Error", "Unrecognized Metadata", "Transient Error", "Not Usable"];
    private static readonly string[] NormalStatuses = ["OK", "Online", "In Service"];

    internal const string Script = """
        $time=(Get-Date).ToUniversalTime().ToString('o')
        $temperature=[pscustomobject]@{ Available=$false; Error=''; Sensors=@() }
        try{
            $temperature.Sensors=@(Get-CimInstance -Namespace 'root/LibreHardwareMonitor' -ClassName 'Sensor' -Filter "SensorType='Temperature'" -ErrorAction Stop |
                Where-Object { [string]$_.Identifier -match '^/(intelcpu|amdcpu)/\d+/temperature/\d+$' } |
                Select-Object -First 32 @{N='Name';E={[string]$_.Name}},@{N='Identifier';E={[string]$_.Identifier}},@{N='Value';E={$_.Value}})
            $temperature.Available=$true
        }catch{ $temperature.Error=[string]$_.FullyQualifiedErrorId+' '+$_.Exception.Message }
        $storage=[pscustomobject]@{ Error=''; Total=0; Disks=@() }
        try{
            $all=@(Get-PhysicalDisk -ErrorAction Stop)
            $storage.Total=$all.Count
            $storage.Disks=@($all |
                Sort-Object @{E={ switch([string]$_.HealthStatus){ 'Unhealthy'{0} '2'{0} 'Warning'{1} '1'{1} 'Healthy'{3} '0'{3} default{2} } }},@{E={[string]$_.DeviceId}} |
                Select-Object -First 16 @{N='Device';E={[string]$_.DeviceId}},@{N='Name';E={[string]$_.FriendlyName}},@{N='MediaType';E={[string]$_.MediaType}},@{N='BusType';E={[string]$_.BusType}},@{N='Health';E={[string]$_.HealthStatus}},@{N='Operational';E={(@($_.OperationalStatus | ForEach-Object { [string]$_ }) -join ',')}})
        }catch{ $storage.Error=$_.Exception.Message }
        [pscustomobject]@{ Time=$time; Temperature=$temperature; Storage=$storage } | ConvertTo-Json -Compress -Depth 5
        """;

    public static void Read(JsonElement root, HealthSnapshot snapshot, DateTimeOffset fallbackTime)
    {
        var time = root.TryGetProperty("Time", out var t) && t.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : fallbackTime;
        snapshot.CpuTemperature = root.TryGetProperty("Temperature", out var temperature) && temperature.ValueKind == JsonValueKind.Object
            ? ReadTemperature(temperature, time)
            : new TemperatureReading("error", Source: TemperatureSource, Time: time, Detail: "Resposta sem dados de temperatura.");
        snapshot.CpuTemperatureC = snapshot.CpuTemperature.State == "read" ? snapshot.CpuTemperature.Celsius : null;
        if (snapshot.CpuTemperature.State is "error" or "invalid") snapshot.Warnings.Add("Temperatura da CPU: " + snapshot.CpuTemperature.Detail);
        snapshot.Storage = root.TryGetProperty("Storage", out var storage) && storage.ValueKind == JsonValueKind.Object
            ? ReadStorage(storage, time)
            : new StorageReading("unavailable", StorageSource, time, 0, [], "Resposta sem dados dos discos.");
        snapshot.StorageHealth = snapshot.Storage.State;
        if (snapshot.Storage.Detail.Length > 0 && snapshot.Storage.State is "unavailable" or "unknown") snapshot.Warnings.Add("Estado dos discos: " + snapshot.Storage.Detail);
    }

    public static void Unavailable(HealthSnapshot snapshot, DateTimeOffset time, string detail)
    {
        snapshot.CpuTemperature = new TemperatureReading("error", Source: TemperatureSource, Time: time, Detail: detail);
        snapshot.CpuTemperatureC = null;
        snapshot.Storage = new StorageReading("unavailable", StorageSource, time, 0, [], detail);
        snapshot.StorageHealth = "unavailable";
        snapshot.Warnings.Add("Temperatura da CPU e estado dos discos indisponíveis: " + detail);
    }

    private static TemperatureReading ReadTemperature(JsonElement element, DateTimeOffset time)
    {
        if (!(element.TryGetProperty("Available", out var available) && available.ValueKind == JsonValueKind.True))
        {
            var error = Text(element, "Error");
            return error.Contains("0x8004100e", StringComparison.OrdinalIgnoreCase) || error.Contains("0x80041010", StringComparison.OrdinalIgnoreCase) || error.Contains("InvalidNamespace", StringComparison.OrdinalIgnoreCase) || error.Contains("InvalidClass", StringComparison.OrdinalIgnoreCase)
                ? new TemperatureReading("no_source", Source: TemperatureSource, Time: time, Detail: "Libre Hardware Monitor não está em execução ou não publica sensores por WMI.")
                : new TemperatureReading("error", Source: TemperatureSource, Time: time, Detail: "O Libre Hardware Monitor não respondeu: " + Limit(error));
        }
        var sensors = Items(element, "Sensors")
            .Select(s => (Name: Text(s, "Name"), Identifier: Text(s, "Identifier"), Value: s.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null))
            .Where(s => PackageSensors.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => Array.FindIndex(PackageSensors, n => n.Equals(s.Name, StringComparison.OrdinalIgnoreCase))).ThenBy(s => s.Identifier, StringComparer.Ordinal)
            .ToArray();
        if (sensors.Length == 0) return new TemperatureReading("no_sensor", Source: TemperatureSource, Time: time, Detail: "O Libre Hardware Monitor não expôs sensor de pacote ou die da CPU.");
        foreach (var sensor in sensors)
            if (sensor.Value is { } value && double.IsFinite(value) && value > 0 && value <= MaxPlausibleCelsius)
                return new TemperatureReading("read", Math.Round(value, 1), sensor.Name + " (" + sensor.Identifier + ")", TemperatureSource, time);
        return new TemperatureReading("invalid", Source: TemperatureSource, Time: time, Detail: $"O sensor {sensors[0].Name} informou valor ausente ou fora de 0–{MaxPlausibleCelsius:F0} °C; a leitura foi descartada.");
    }

    private static StorageReading ReadStorage(JsonElement element, DateTimeOffset time)
    {
        var error = Text(element, "Error");
        if (error.Length > 0) return new StorageReading("unavailable", StorageSource, time, 0, [], "O Windows não informou os discos físicos: " + Limit(error));
        var disks = Items(element, "Disks").Select(d =>
        {
            var health = HealthName(d.TryGetProperty("Health", out var h) ? h : default);
            var operational = (d.TryGetProperty("Operational", out var status) ? status.ValueKind switch
            {
                JsonValueKind.Array => status.EnumerateArray().Select(OperationalName),
                JsonValueKind.String => (status.GetString() ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(OperationalName),
                JsonValueKind.Number => [OperationalName(status)],
                _ => []
            } : []).Where(s => s.Length > 0).ToArray();
            return new PhysicalDiskReading(Text(d, "Device"), Text(d, "Name"), Text(d, "MediaType"), Text(d, "BusType"), health, operational, DiskState(health, operational));
        }).OrderBy(d => Rank(d.State)).ThenBy(d => d.Device, StringComparer.Ordinal).Take(MaxPhysicalDisks).ToList();
        var total = element.TryGetProperty("Total", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n) ? Math.Max(n, disks.Count) : disks.Count;
        if (total == 0) return new StorageReading("unavailable", StorageSource, time, 0, [], "O Windows não listou discos físicos.");
        var truncated = disks.Count < total;
        var state = disks.Any(d => d.State == "unhealthy") ? "unhealthy" : disks.Any(d => d.State == "warning") ? "warning" : truncated || disks.Any(d => d.State == "unknown") ? "unknown" : "healthy";
        var detail = truncated ? $"{disks.Count} de {total} discos físicos avaliados; os demais não foram lidos e o resumo não os considera saudáveis." : "";
        return new StorageReading(state, StorageSource, time, total, disks, detail);
    }

    private static string DiskState(string health, string[] operational)
    {
        if (health == "Unhealthy") return "unhealthy";
        if (health == "Warning" || operational.Any(s => AlertStatuses.Contains(s, StringComparer.OrdinalIgnoreCase))) return "warning";
        if (health == "Healthy" && operational.Length > 0 && operational.All(s => NormalStatuses.Contains(s, StringComparer.OrdinalIgnoreCase))) return "healthy";
        return "unknown";
    }

    private static int Rank(string state) => state switch { "unhealthy" => 0, "warning" => 1, "unknown" => 2, _ => 3 };

    private static string HealthName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out var code) => code switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
        JsonValueKind.String => value.GetString() switch
        {
            "0" => "Healthy", "1" => "Warning", "2" => "Unhealthy",
            { } text when text.Equals("Healthy", StringComparison.OrdinalIgnoreCase) => "Healthy",
            { } text when text.Equals("Warning", StringComparison.OrdinalIgnoreCase) => "Warning",
            { } text when text.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase) => "Unhealthy",
            _ => "Unknown"
        },
        _ => "Unknown"
    };

    private static string OperationalName(JsonElement value) => OperationalName(value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Number => value.GetRawText(), _ => "" });

    private static string OperationalName(string text)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code switch
        {
            0 => "Unknown", 1 => "Other", 2 => "OK", 3 => "Degraded", 4 => "Stressed", 5 => "Predictive Failure", 6 => "Error", 7 => "Non-Recoverable Error",
            8 => "Starting", 9 => "Stopping", 10 => "Stopped", 11 => "In Service", 12 => "No Contact", 13 => "Lost Communication", 14 => "Aborted",
            15 => "Dormant", 16 => "Supporting Entity in Error", 17 => "Completed", 18 => "Power Mode", _ => "Código " + code
        } : Limit(text.Trim());
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().ToArray(),
        JsonValueKind.Object => [value],
        _ => []
    } : [];
    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Limit(value.GetString() ?? "") : "";
    private static string Limit(string value) => value.Length <= 200 ? value : value[..200];
}
