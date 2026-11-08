using System.Text.Json;

namespace Nexo.Support;

public sealed record InstalledDriverPackage(string DeviceId, string? InfName, string? Version, string? Provider);

// DriverQuery: "skipped" (nenhum dispositivo com erro), "ok" ou "failed" (o Windows não respondeu à consulta de drivers a tempo).
public sealed record DeviceDiagnosis(List<Device> Devices, string DriverQuery);

public static class DriverStoreInventory
{
    // Limite só da consulta de drivers. Se estourar, os dispositivos continuam no resultado e o driver fica como não conferido.
    public const int DriverQueryTimeoutSeconds = 30;

    // Issue #62: diagnose_device lê os dispositivos da categoria, como antes, inclusive os não presentes.
    // Só para os presentes com erro (Code diferente de 0 e de 45) faz uma consulta a Win32_PnPSignedDriver
    // filtrada pelos IDs exatos, para trazer INF, versão e fornecedor.
    private const string DeviceDiagnosisTemplate = """
        $devices=@(Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPClass -in __CLASSES__ } | ForEach-Object { [pscustomobject]@{ Name=$_.Name; Id=$_.PNPDeviceID; Code=[int]$_.ConfigManagerErrorCode; Status=$_.Status; Present=($_.Present -eq $true); HardwareIds=@($_.HardwareID); CompatibleIds=@($_.CompatibleID) } })
        $faulty=@($devices | Where-Object { $_.Present -eq $true -and $_.Code -ne 0 -and $_.Code -ne 45 -and $_.Id } | ForEach-Object { [string]$_.Id })
        $query='skipped'; $drivers=@()
        if($faulty.Count -gt 0){ try { $drivers=@(Get-CimInstance Win32_PnPSignedDriver -OperationTimeoutSec __DRIVER_TIMEOUT__ -ErrorAction Stop | Where-Object { $_.DeviceID -and $faulty -contains [string]$_.DeviceID } | ForEach-Object { [pscustomobject]@{ DeviceId=[string]$_.DeviceID; InfName=[string]$_.InfName; Version=[string]$_.DriverVersion; Provider=[string]$_.DriverProviderName } }); $query='ok' } catch { $query='failed' } }
        ConvertTo-Json -Depth 5 -Compress -InputObject ([pscustomobject]@{ Devices=$devices; Drivers=$drivers; DriverQuery=$query })
        """;

    public static string DeviceDiagnosisScript(string classes) => DeviceDiagnosisTemplate.Replace("__CLASSES__", classes).Replace("__DRIVER_TIMEOUT__", DriverQueryTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // Mesmo critério do script: presente, com ID e com Code diferente de 0 e de 45 (45 = desconectado).
    public static bool HasError(Device device) => device.Present && device.Code is not (0 or 45) && !string.IsNullOrWhiteSpace(device.Id);

    // Junta o driver ao dispositivo pelo ID exato. Linha ausente, INF vazio ou mais de uma linha para o mesmo ID deixam o dispositivo sem driver.
    // Drivers fora do formato de lista (exceto um objeto único) contam como consulta não conferida, não como "sem driver".
    public static DeviceDiagnosis ParseDeviceDiagnosis(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var listed = root.GetProperty("Devices");
        var devices = listed.ValueKind == JsonValueKind.Object
            ? [JsonSerializer.Deserialize<Device>(listed.GetRawText())!]
            : JsonSerializer.Deserialize<List<Device>>(listed.GetRawText()) ?? [];
        var query = root.TryGetProperty("DriverQuery", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() is "ok" or "skipped" ? state.GetString()! : "failed";
        List<InstalledDriverPackage> packages = [];
        if (root.TryGetProperty("Drivers", out var signed) && signed.ValueKind == JsonValueKind.Array)
            packages = JsonSerializer.Deserialize<List<InstalledDriverPackage>>(signed.GetRawText()) ?? [];
        else if (signed.ValueKind == JsonValueKind.Object)
            packages = [JsonSerializer.Deserialize<InstalledDriverPackage>(signed.GetRawText())!];
        else query = "failed";
        var joined = devices.Select(device =>
        {
            if (!HasError(device)) return device;
            var rows = packages.Where(package => string.Equals(package.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            return rows.Length == 1 && MatchesObserved(device.Id, rows[0].DeviceId, rows[0].InfName)
                ? device with { InfName = rows[0].InfName!, DriverVersion = rows[0].Version ?? "", DriverProvider = rows[0].Provider ?? "" }
                : device;
        }).ToList();
        return new DeviceDiagnosis(joined, query);
    }

    public static string DriverSummary(Device device, string driverQuery)
    {
        if (device.InfName.Length > 0)
            return device.InfName + " · versão " + (device.DriverVersion.Length > 0 ? device.DriverVersion : "não informada") + " · " + (device.DriverProvider.Length > 0 ? device.DriverProvider : "fornecedor não informado");
        if (device.Code == 28) return "sem driver instalado";
        return driverQuery == "failed" ? "não foi possível conferir o driver no Windows" : "o Windows não informou driver para este ID";
    }

    public static object[] FaultyDrivers(DeviceDiagnosis diagnosis) => diagnosis.Devices
        .Where(HasError)
        .Select(device => (object)new { device.Id, device.Name, device.Code, Driver = DriverSummary(device, diagnosis.DriverQuery) })
        .ToArray();

    public static bool MatchesObserved(string observedId, string candidateId, string? infName)
        => observedId.Length > 0 && string.Equals(observedId, candidateId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(infName) && infName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase);

    public static async Task<InstalledDriverPackage?> Read(string observedId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(observedId)) throw new ArgumentException("ID do dispositivo não observado.", nameof(observedId));
        var script = "$driver=Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop | Where-Object { $_.DeviceID -eq " + WindowsTools.Quote(observedId) + " } | Select-Object -First 1; if($driver){[pscustomobject]@{DeviceId=[string]$driver.DeviceID;InfName=[string]$driver.InfName;Version=[string]$driver.DriverVersion;Provider=[string]$driver.DriverProviderName} | ConvertTo-Json -Compress}";
        var raw = await SupportService.Powershell(script, token, 30);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var package = JsonSerializer.Deserialize<InstalledDriverPackage>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return package != null && MatchesObserved(observedId, package.DeviceId, package.InfName) ? package : null;
    }
}
