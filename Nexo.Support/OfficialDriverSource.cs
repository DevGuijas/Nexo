using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml.Linq;

namespace Nexo.Support;

public static class OfficialDriverSource
{
    public sealed record DriverOffer(string Id, string Version, Uri Url, string DeviceName, string InstalledVersion);
    public const string NvidiaLicense = "https://www.nvidia.com/en-us/drivers/geforce-license/";
    public const string NvidiaAppPage = "https://www.nvidia.com/en-us/software/nvidia-app/";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    public static bool IsNvidiaDisplay(Device device) => device.Id.StartsWith("PCI\\VEN_10DE&", StringComparison.OrdinalIgnoreCase)
        && (device.HardwareIds ?? []).Any(id => id.StartsWith("PCI\\VEN_10DE&", StringComparison.OrdinalIgnoreCase));

    public static bool AllowedNvidiaDriver(Uri url, string version) => url.Scheme == Uri.UriSchemeHttps
        && url.Host.Equals("us.download.nvidia.com", StringComparison.OrdinalIgnoreCase) && url.Port == 443
        && url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0
        && Regex.IsMatch(version, @"^\d{3}\.\d{2}$")
        && Regex.IsMatch(url.AbsolutePath, @"^/Windows/" + Regex.Escape(version) + "/" + Regex.Escape(version)
            + @"-(desktop|notebook)-win10-win11-64bit-international-dch-whql\.exe$", RegexOptions.IgnoreCase);

    public static Version? InstalledMarketingVersion(string windowsVersion)
    {
        var parts = windowsVersion.Split('.');
        if (parts.Length != 4 || !int.TryParse(parts[2], out var branch) || !int.TryParse(parts[3], out var build)
            || branch is < 10 or > 19 || build is < 0 or > 9999) return null;
        return new Version((branch % 10) * 100 + build / 100, build % 100);
    }

    public static DriverOffer? ParseNvidiaDriverDetails(string json, string id, string deviceName, string installedVersion, bool windows11)
    {
        using var document = JsonDocument.Parse(json);
        var info = document.RootElement.GetProperty("driverDetails").GetProperty("IDS")[0].GetProperty("downloadInfo");
        string Get(string key) => info.GetProperty(key).GetString() ?? "";
        if (Get("Success") != "1" || Get("ID") != id || Get("IsWHQL") != "1" || Get("Is64Bit") != "1") return null;
        var version = Get("Version");
        if (!Version.TryParse(version, out var offered) || !Regex.IsMatch(version, @"^\d{3}\.\d{2}$")) return null;
        var current = InstalledMarketingVersion(installedVersion);
        if (current == null || offered <= current) return null;
        var matchingOs = windows11 ? "Windows 11" : "Windows 10 64-bit";
        if (!info.GetProperty("OSList").EnumerateArray().Any(os => Uri.UnescapeDataString(os.GetProperty("OSName").GetString() ?? "") == matchingOs)) return null;
        var matchingProduct = info.GetProperty("series").EnumerateArray().Any(series =>
            series.GetProperty("products").EnumerateArray().Any(product =>
                Uri.UnescapeDataString(product.GetProperty("productName").GetString() ?? "")
                    .Equals(deviceName, StringComparison.OrdinalIgnoreCase)));
        if (!matchingProduct || !Uri.TryCreate(Get("DownloadURL"), UriKind.Absolute, out var url) || !AllowedNvidiaDriver(url, version)) return null;
        return new DriverOffer(id, version, url, deviceName, installedVersion);
    }

    public static async Task<DriverOffer?> FindNvidiaDriver(Device device, CancellationToken token)
    {
        if (!IsNvidiaDisplay(device) || !OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new InvalidOperationException("A consulta oficial exige uma placa NVIDIA PCI presente no Windows 10/11.");
        var windows11 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        var productXml = await FetchSmall("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3&ParentID=0&Lang=en-us", 200_000, token);
        var products = XDocument.Parse(productXml).Descendants("LookupValue")
            .Where(value => string.Equals(value.Element("Name")?.Value, device.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (products.Length != 1 || !int.TryParse(products[0].Element("Value")?.Value, out var productId)
            || !int.TryParse(products[0].Attribute("ParentID")?.Value, out var seriesId))
            throw new InvalidDataException("A NVIDIA não identificou de forma única o modelo observado. Consulte o fabricante manualmente.");
        var osXml = await FetchSmall("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=4&ParentID=0&Lang=en-us", 50_000, token);
        var osName = windows11 ? "Windows 11" : "Windows 10 64-bit";
        var osMatches = XDocument.Parse(osXml).Descendants("LookupValue").Where(value => value.Element("Name")?.Value == osName).ToArray();
        if (osMatches.Length != 1 || !int.TryParse(osMatches[0].Element("Value")?.Value, out var osId))
            throw new InvalidDataException("Não encontrei o Windows instalado na lista oficial da NVIDIA.");
        var searchUrl = $"https://www.nvidia.com/Download/processFind.aspx?psid={seriesId}&pfid={productId}&osid={osId}&lid=1&dtcid=1";
        var results = await FetchSmall(searchUrl, 300_000, token);
        var ids = Regex.Matches(results, @"driverResults\.aspx/(\d{4,8})", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value).Distinct().Take(12).ToArray();
        DriverOffer? best = null;
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            var details = await GetNvidiaDetails(id, token);
            DriverOffer? offer;
            try { offer = ParseNvidiaDriverDetails(details, id, device.Name, device.DriverVersion, windows11); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { continue; }
            if (offer != null && (best == null || Version.Parse(offer.Version) > Version.Parse(best.Version))) best = offer;
        }
        return best;
    }

    public static Task<string> GetNvidiaDetails(string id, CancellationToken token)
    {
        if (!Regex.IsMatch(id, @"^\d{4,8}$")) throw new InvalidDataException("Identificador NVIDIA inválido.");
        return FetchSmall("https://www.nvidia.com/services/com.nvidia.services/AEMDriversContent/getDownloadDetails?%7B%22ddID%22%3A%22" + id + "%22%7D", 100_000, token);
    }

    private static async Task<string> FetchSmall(string url, int maxBytes, CancellationToken token)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is { } length && length > maxBytes)
            throw new IOException("A fonte oficial da NVIDIA não respondeu com os dados esperados.");
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        if (bytes.Length > maxBytes) throw new InvalidDataException("Resposta da NVIDIA maior que o limite permitido.");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public static async Task<string> DownloadNvidiaDriver(DriverOffer offer, CancellationToken token)
    {
        if (!AllowedNvidiaDriver(offer.Url, offer.Version)) throw new InvalidDataException("Endereço do driver NVIDIA não permitido.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexo", "Downloads");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileName(offer.Url.LocalPath));
        if (File.Exists(destination) && await HasNvidiaSignature(destination, token)) return destination;
        var temporary = destination + ".download";
        try
        {
            using var response = await Client.GetAsync(offer.Url, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is not { } expected
                || expected is < 100_000_000 or > 2_000_000_000)
                throw new IOException("O pacote NVIDIA não está disponível ou tem tamanho inesperado.");
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(temporary))
            {
                var buffer = new byte[1024 * 1024];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += count;
                    if (total > expected) throw new InvalidDataException("Download NVIDIA maior que o tamanho informado.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (total != expected) throw new InvalidDataException("Download NVIDIA incompleto.");
            }
            if (!await HasNvidiaSignature(temporary, token)) throw new InvalidDataException("Assinatura NVIDIA inválida; pacote não será executado.");
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<bool> HasNvidiaSignature(string file, CancellationToken token)
    {
        var json = await SupportService.Powershell("$s=Get-AuthenticodeSignature -LiteralPath " + WindowsTools.Quote(file)
            + "; [pscustomobject]@{Status=[string]$s.Status;Subject=[string]$s.SignerCertificate.Subject} | ConvertTo-Json -Compress", token);
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.GetProperty("Status").GetString() == "Valid"
            && (parsed.RootElement.GetProperty("Subject").GetString() ?? "").Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
    }

    public static Uri? FindNvidiaAppDownload(string html)
    {
        var match = Regex.Match(html, @"https://us\.download\.nvidia\.com/nvapp/client/[0-9]+(?:\.[0-9]+){3}/NVIDIA_app_v[0-9]+(?:\.[0-9]+){3}\.exe", RegexOptions.IgnoreCase);
        if (!match.Success || !Uri.TryCreate(match.Value, UriKind.Absolute, out var url) || !AllowedNvidiaDownload(url)) return null;
        return url;
    }

    public static bool AllowedNvidiaDownload(Uri url) => url.Scheme == Uri.UriSchemeHttps
        && string.Equals(url.Host, "us.download.nvidia.com", StringComparison.OrdinalIgnoreCase)
        && url.Port == 443 && string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment)
        && Regex.IsMatch(url.AbsolutePath, @"^/nvapp/client/([0-9]+(?:\.[0-9]+){3})/NVIDIA_app_v\1\.exe$", RegexOptions.IgnoreCase);

    public static async Task<(string Path, Uri Url)> DownloadNvidiaApp(CancellationToken token)
    {
        using var page = await Client.GetAsync(NvidiaAppPage, HttpCompletionOption.ResponseHeadersRead, token);
        if (page.StatusCode != HttpStatusCode.OK) throw new IOException("A página oficial da NVIDIA não respondeu corretamente.");
        var url = FindNvidiaAppDownload(await page.Content.ReadAsStringAsync(token))
            ?? throw new IOException("Não foi possível identificar o download oficial da NVIDIA com segurança.");
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > 500_000_000)
            throw new IOException("O pacote oficial da NVIDIA não está disponível ou excede o limite de tamanho.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexo", "Downloads");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileName(url.LocalPath));
        var temporary = destination + ".download";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[1024 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > 500_000_000) throw new IOException("O pacote oficial excede o limite de tamanho.");
                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                }
                if (total < 1_000_000) throw new IOException("O download da NVIDIA parece incompleto.");
            }
            var signature = await SupportService.Powershell("$s=Get-AuthenticodeSignature -LiteralPath " + WindowsTools.Quote(temporary) + "; [pscustomobject]@{Status=[string]$s.Status;Subject=[string]$s.SignerCertificate.Subject} | ConvertTo-Json -Compress", token);
            using var parsed = System.Text.Json.JsonDocument.Parse(signature);
            var status = parsed.RootElement.GetProperty("Status").GetString();
            var subject = parsed.RootElement.GetProperty("Subject").GetString() ?? "";
            if (status != "Valid" || !subject.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                throw new IOException("A assinatura digital do pacote não foi validada como NVIDIA.");
            File.Move(temporary, destination, true);
            return (destination, url);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
