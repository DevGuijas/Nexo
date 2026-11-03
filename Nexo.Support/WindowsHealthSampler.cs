using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace Nexo.Support;

public sealed class WindowsHealthSampler : IHealthSampler
{
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
                .Select(n => new AdapterHealth(n.Id, n.Name, n.OperationalStatus.ToString(), n.OperationalStatus == OperationalStatus.Up)).ToList();
            result.Connectivity = result.Adapters.Any(a => a.Enabled) ? "local" : "none";
        }
        catch (Exception ex) { result.Warnings.Add("Adaptadores: " + ex.Message); }
        try
        {
            const string script = "$os=Get-CimInstance Win32_OperatingSystem; $cpu=Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average; $svc=@(Get-CimInstance Win32_Service | Where-Object { $_.StartMode -eq 'Auto' } | Select-Object -First 80 Name,State,StartMode); [pscustomobject]@{Cpu=$cpu.Average; TotalKb=$os.TotalVisibleMemorySize; FreeKb=$os.FreePhysicalMemory; Services=$svc} | ConvertTo-Json -Compress -Depth 4";
            using var doc = JsonDocument.Parse(await SupportService.Powershell(script, token, 20));
            var root = doc.RootElement;
            if (root.TryGetProperty("Cpu", out var cpu) && cpu.ValueKind == JsonValueKind.Number) result.CpuPercent = cpu.GetDouble();
            if (root.TryGetProperty("TotalKb", out var total) && total.ValueKind == JsonValueKind.Number && total.GetInt64() > 0
                && root.TryGetProperty("FreeKb", out var free) && free.ValueKind == JsonValueKind.Number)
            {
                result.MemoryTotalBytes = total.GetInt64() * 1024;
                result.MemoryUsedBytes = (total.GetInt64() - free.GetInt64()) * 1024;
                result.MemoryUsedPercent = 100.0 * result.MemoryUsedBytes / result.MemoryTotalBytes;
            }
            if (root.TryGetProperty("Services", out var services) && services.ValueKind == JsonValueKind.Array)
                result.Services = services.EnumerateArray().Select(s => new ServiceHealth(s.GetProperty("Name").GetString() ?? "", s.GetProperty("State").GetString() ?? "", "Automatic")).ToList();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { result.Warnings.Add("CPU, memória ou serviços indisponíveis: " + ex.Message); }
        result.CollectionStatus = result.Warnings.Count == 0 ? "complete" : "partial";
        return result;
    }
}
