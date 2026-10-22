using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Nexo.Support;

public static class OfficialDriverSource
{
    public const string NvidiaAppPage = "https://www.nvidia.com/en-us/software/nvidia-app/";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static bool IsNvidiaDisplay(Device device) => device.Id.StartsWith("PCI\\VEN_10DE&", StringComparison.OrdinalIgnoreCase)
        && (device.HardwareIds ?? []).Any(id => id.StartsWith("PCI\\VEN_10DE&", StringComparison.OrdinalIgnoreCase));

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
