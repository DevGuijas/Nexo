using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace Nexo.Support;

public sealed class WindowsHealthSampler : IHealthSampler
{
    internal const int MaxServicesCollected = 200;

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
            result.Processes = Process.GetProcesses().Select(p =>
            {
                try { return new ProcessHealth(p.ProcessName, p.Id, p.WorkingSet64, null); }
                catch { return null; }
                finally { p.Dispose(); }
            }).Where(p => p != null).Cast<ProcessHealth>().OrderByDescending(p => p.WorkingSetBytes).Take(12).ToList();
        }
        catch (Exception ex) { result.Warnings.Add("Processos: " + ex.Message); }
        try
        {
            result.Adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => new AdapterHealth(n.Id, n.Name, n.OperationalStatus.ToString(), n.OperationalStatus == OperationalStatus.Up)).ToList();
            result.Connectivity = result.Adapters.Any(a => a.Enabled) ? "local" : "none";
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
