using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace Nexo.Support;

public sealed class WindowsHealthSampler : IHealthSampler
{
    internal const int MaxServicesCollected = 200;
    private readonly HealthSettings settings;
    private readonly ConnectivityProbe connectivityProbe;

    public WindowsHealthSampler(HealthSettings? settings = null, ConnectivityProbe? probe = null)
    {
        this.settings = settings ?? new HealthSettings();
        connectivityProbe = probe ?? new ConnectivityProbe();
    }

    private const string MetricsScript = """
        $os=Get-CimInstance Win32_OperatingSystem
        $cpu=Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average
        $services=@(Get-CimInstance Win32_Service | Where-Object { $_.StartMode -eq 'Auto' } | Select-Object Name,State)
        [pscustomobject]@{ Cpu=$cpu.Average; TotalKb=$os.TotalVisibleMemorySize; FreeKb=$os.FreePhysicalMemory; Services=$services } | ConvertTo-Json -Compress -Depth 4
        """;

    public async Task<HealthSnapshot> Sample(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = new HealthSnapshot();
        IReadOnlyList<IPAddress> gateways = [];
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            {
                try { if (drive.IsReady) result.Disks.Add(new DiskHealth(drive.Name, drive.AvailableFreeSpace, drive.TotalSize)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Warnings.Add("Volume " + drive.Name + ": " + ex.Message); }
            }
        }
        catch (Exception ex) { result.Warnings.Add("Discos: " + ex.Message); }
        try
        {
            result.Processes = ProcessReader.Top(Process.GetProcesses());
        }
        catch (Exception ex) { result.Warnings.Add("Processos: " + ex.Message); }
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToArray();
            result.Adapters = interfaces
                .Select(n => new AdapterHealth(n.Id, n.Name, n.OperationalStatus.ToString(), n.OperationalStatus == OperationalStatus.Up)).ToList();
            result.Connectivity = result.Adapters.Any(a => a.Enabled) ? "local" : "none";
            gateways = interfaces.Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n =>
                {
                    try { return n.GetIPProperties().GatewayAddresses.Select(g => g.Address).ToArray(); }
                    catch (NetworkInformationException) { return []; }
                })
                .Distinct().Take(4).ToArray();
        }
        catch (Exception ex) { result.Warnings.Add("Adaptadores: " + ex.Message); }
        try
        {
            var raw = await SupportService.Powershell("ConvertTo-Json -Compress -InputObject @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Select-Object -First 80 @{N='Id';E={[string]$_.InterfaceGuid}},Name,@{N='Status';E={[string]$_.Status}})", token, 20);
            using var adapters = JsonDocument.Parse(raw);
            foreach (var item in adapters.RootElement.EnumerateArray())
            {
                var id = item.GetProperty("Id").GetString() ?? "";
                var status = item.GetProperty("Status").GetString() ?? "unknown";
                var name = item.GetProperty("Name").GetString() ?? "";
                var existing = result.Adapters.FindIndex(a => a.Id.Trim('{', '}').Equals(id.Trim('{', '}'), StringComparison.OrdinalIgnoreCase));
                var observed = new AdapterHealth(id, name, status, status != "Disabled");
                if (existing >= 0) result.Adapters[existing] = observed; else result.Adapters.Add(observed);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { result.Warnings.Add("Estado administrativo dos adaptadores indisponível: " + ex.Message); }
        try
        {
            using var doc = JsonDocument.Parse(await SupportService.Powershell(MetricsScript, token, 20));
            var root = doc.RootElement;
            if (root.TryGetProperty("Cpu", out var cpu) && cpu.ValueKind == JsonValueKind.Number) result.CpuPercent = cpu.GetDouble();
            if (root.TryGetProperty("TotalKb", out var total) && total.ValueKind == JsonValueKind.Number && total.GetInt64() > 0
                && root.TryGetProperty("FreeKb", out var free) && free.ValueKind == JsonValueKind.Number)
            {
                result.MemoryTotalBytes = total.GetInt64() * 1024;
                result.MemoryUsedBytes = (total.GetInt64() - free.GetInt64()) * 1024;
                result.MemoryUsedPercent = 100.0 * result.MemoryUsedBytes / result.MemoryTotalBytes;
            }
            ReadServices(root, result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { result.Warnings.Add("CPU, memória ou serviços indisponíveis: " + ex.Message); }
        try
        {
            var readings = await connectivityProbe.Run(settings, result.Connectivity == "local", gateways, token);
            result.GatewayProbe = readings.Gateway;
            result.DnsProbe = readings.Dns;
            result.ExternalProbe = readings.External;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { result.Warnings.Add("Sondagens de rede indisponíveis: " + ex.Message); }
        ApplyCollectionStatus(result);
        return result;
    }

    internal static void ApplyCollectionStatus(HealthSnapshot snapshot)
    {
        snapshot.CollectionStatus = snapshot.Warnings.Count == 0 ? "complete" : "partial";
    }

    internal static void ReadServices(JsonElement root, HealthSnapshot snapshot)
    {
        if (!root.TryGetProperty("Services", out var services) || services.ValueKind != JsonValueKind.Array)
            return;

        var automatic = services.EnumerateArray()
            .Select(service => new ServiceHealth(
                service.GetProperty("Name").GetString() ?? "",
                service.GetProperty("State").GetString() ?? "",
                "Automatic"))
            .ToList();

        snapshot.Services = automatic
            .OrderBy(service => service.Status.Equals("Running", StringComparison.OrdinalIgnoreCase))
            .ThenBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxServicesCollected)
            .ToList();

        if (automatic.Count > snapshot.Services.Count)
            snapshot.Warnings.Add($"Serviços automáticos: {snapshot.Services.Count} de {automatic.Count} coletados; os demais não foram avaliados.");
    }
}
