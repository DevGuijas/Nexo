using System.Text.Json;

namespace Nexo.Support;

public sealed record InstalledDriverPackage(string DeviceId, string? InfName, string? Version, string? Provider);

public static class DriverStoreInventory
{
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
