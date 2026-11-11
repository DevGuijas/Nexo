using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace Nexo.Support;

public sealed class WindowsHealthSampler : IHealthSampler
{
    /// <summary>Serviços Auto fora de execução guardados por leitura: são os que levam início atrasado, códigos e gatilho.</summary>
    internal const int MaxServicesCollected = 200;
    /// <summary>
    /// Serviços Auto em execução guardados por leitura, só com nome e estado (#57). A regra de parada normal precisa
    /// deles para provar a execução antes da parada; um Windows comum tem bem menos que isso (59 no PC do ensaio de
    /// 08/11/2026), e o teto só protege o banco de uma lista anormal.
    /// </summary>
    internal const int MaxRunningServicesCollected = 1000;
    private readonly HealthSettings settings;
    private readonly ConnectivityProbe connectivityProbe;

    public WindowsHealthSampler(HealthSettings? settings = null, ConnectivityProbe? probe = null)
    {
        this.settings = settings ?? new HealthSettings();
        connectivityProbe = probe ?? new ConnectivityProbe();
    }

    // Para serviços Auto fora de execução, a coleta também lê o início atrasado e os códigos de saída (Win32_Service)
    // e se há gatilho de início (Action=1 em alguma subchave de TriggerInfo, no registro, sem abrir processo).
    // Serviços em execução não precisam disso para a regra, e o registro é lido só para os poucos que não estão.
    // Erro ao ler o gatilho vira nulo, que não isenta o serviço do alerta, e não derruba o resto da coleta.
    internal const string MetricsScript = """
        $os=Get-CimInstance Win32_OperatingSystem
        $cpu=Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average
        function Get-NexoTriggerStart([string]$Name){
            $path='HKLM:\SYSTEM\CurrentControlSet\Services\'+$Name+'\TriggerInfo'
            try{
                if(-not (Test-Path -LiteralPath $path)){return $false}
                foreach($trigger in @(Get-ChildItem -LiteralPath $path -ErrorAction Stop)){
                    if((Get-ItemProperty -LiteralPath $trigger.PSPath -ErrorAction Stop).Action -eq 1){return $true}
                }
                return $false
            }catch{return $null}
        }
        $services=@(Get-CimInstance Win32_Service | Where-Object { $_.StartMode -eq 'Auto' } | ForEach-Object {
            if($_.State -eq 'Running'){[pscustomobject]@{ Name=$_.Name; State=$_.State }}
            else{[pscustomobject]@{ Name=$_.Name; State=$_.State; DelayedAutoStart=$_.DelayedAutoStart; ExitCode=$_.ExitCode; ServiceSpecificExitCode=$_.ServiceSpecificExitCode; TriggerStart=(Get-NexoTriggerStart $_.Name) }}
        })
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
                // Um volume fixo que não está pronto (BitLocker bloqueado, por exemplo) fica registrado à parte: sem isso, ele
                // pareceria removido e encerraria o alerta dele; como aviso, deixaria toda amostra parcial para sempre.
                try { if (drive.IsReady) result.Disks.Add(new DiskHealth(drive.Name, drive.AvailableFreeSpace, drive.TotalSize)); else (result.UnreadyVolumes ??= []).Add(drive.Name); }
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
        await HardwareSensors.Collect(result, (script, timeout, cancel) => SupportService.Powershell(script, cancel, timeout), token);
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
                "Automatic",
                OptionalBool(service, "DelayedAutoStart"),
                OptionalBool(service, "TriggerStart"),
                OptionalCode(service, "ExitCode"),
                OptionalCode(service, "ServiceSpecificExitCode")))
            .ToList();

        // Os limites são separados: muitos serviços parados não podem tirar da leitura um serviço em execução (#57).
        bool IsRunning(ServiceHealth service) => service.Status.Equals("Running", StringComparison.OrdinalIgnoreCase);
        var notRunning = automatic.Where(service => !IsRunning(service)).OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var running = automatic.Where(IsRunning).OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase).ToList();
        snapshot.Services = [.. notRunning.Take(MaxServicesCollected), .. running.Take(MaxRunningServicesCollected)];

        if (notRunning.Count > MaxServicesCollected)
            snapshot.Warnings.Add($"Serviços automáticos fora de execução: {MaxServicesCollected} de {notRunning.Count} coletados; os demais não foram avaliados.");
        if (running.Count > MaxRunningServicesCollected)
            snapshot.Warnings.Add($"Serviços automáticos em execução: {MaxRunningServicesCollected} de {running.Count} coletados; os demais não foram avaliados.");
    }

    private static long? OptionalCode(JsonElement service, string name)
        => service.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var code) ? code : null;

    private static bool? OptionalBool(JsonElement service, string name)
        => service.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
}
