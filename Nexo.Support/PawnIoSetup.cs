using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Nexo.Support;

/// <summary>
/// Presença do driver PawnIO (#142). A regra é a do PawnIo.IsInstalled da LibreHardwareMonitorLib 0.9.6: DisplayVersion
/// válido em HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO, na visão padrão e, se faltar, na de 64 bits.
/// A biblioteca lê o registro uma vez por processo (construtor estático); aqui ele é relido a cada chamada, para a
/// conferência depois da instalação ver o driver novo sem reiniciar o Nexo.
/// </summary>
public static class PawnIoDriver
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    public static bool Installed()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(Key))
                if (ValidVersion(key?.GetValue("DisplayVersion"))) return true;
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var wide = root.OpenSubKey(Key);
            return ValidVersion(wide?.GetValue("DisplayVersion"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    internal static bool ValidVersion(object? value) => Version.TryParse(value as string, out _);
}

public sealed record PawnIoInstallResult(string State, string Message, int? ExitCode = null, string Signer = "");

/// <summary>O que a instalação precisa do Windows e da rede; as verificações trocam tudo por simulações.</summary>
public interface IPawnIoInstallSystem
{
    Task<HttpResponseMessage> Send(Uri url, CancellationToken token);
    Task<(string Status, string Subject)> Signature(string file, CancellationToken token);
    /// <summary>Código de saída do instalador, ou null quando o UAC é cancelado.</summary>
    Task<int?> RunElevated(string file, CancellationToken token);
    bool DriverInstalled();
    string WorkDirectory();
    /// <summary>Primeiro link (reparse point) no arquivo ou em qualquer pasta acima dele, ou null.</summary>
    string? Linked(string file);
}

/// <summary>
/// Instalação opcional do PawnIO (#142), só por clique e depois da confirmação. O Nexo não embute o binário (GPL-2.0): baixa a
/// release oficial 2.2.0 fixada, confere tamanho, SHA-256 e assinatura Authenticode, e abre o próprio instalador com UAC. Quem
/// instala é o assistente do PawnIO. Nenhuma flag silenciosa; nenhuma consulta à "latest"; nunca a edição "Unrestricted".
/// </summary>
public static class PawnIoSetup
{
    public const string Version = "2.2.0";
    public static readonly Uri Url = new("https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe");
    public const long Size = 3410960;
    public const string Sha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";
    public const int MaxRedirects = 5;
    public const string Uninstall = "Configurações > Aplicativos > PawnIO";
    private static readonly string[] Hosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    public const string ConfirmationTitle = "Instalar o PawnIO (driver oficial)";
    public static readonly string ConfirmationDetails = string.Join('\n',
        "O PawnIO é um driver de kernel de terceiros, de namazso, distribuído sob a licença GPL-2.0. A LibreHardwareMonitor, embutida no Nexo, usa esse driver para ler a temperatura da CPU.",
        "",
        "O que acontece ao autorizar:",
        "1. O Nexo baixa o PawnIO_setup.exe da release oficial " + Version + " no GitHub (namazso/PawnIO.Setup).",
        "2. Confere o tamanho (" + Size + " bytes), o SHA-256 fixado e a assinatura digital. Se algo não bater, o arquivo é apagado e nada é executado.",
        "3. O Windows vai pedir autorização de administrador (UAC).",
        "4. O instalador do PawnIO abre e é ele quem instala: siga o assistente dele.",
        "",
        "Para desinstalar depois, basta ir em " + Uninstall + ".",
        "O Nexo não embute o driver e não o instala sem este pedido.");

    public static bool AllowedHost(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.Port == 443 && url.UserInfo.Length == 0
        && Hosts.Contains(url.IdnHost, StringComparer.OrdinalIgnoreCase);

    public static Task<PawnIoInstallResult> InstallAsync(IPawnIoInstallSystem system, IProgress<string>? progress, CancellationToken token) =>
        InstallAsync(system, progress, token, Sha256);

    // O hash esperado é parâmetro só para as verificações simularem um instalador; o app usa sempre o SHA-256 fixado acima.
    internal static async Task<PawnIoInstallResult> InstallAsync(IPawnIoInstallSystem system, IProgress<string>? progress, CancellationToken token, string expectedSha256)
    {
        if (system.DriverInstalled()) return new("installed", "O PawnIO já está instalado neste computador. Nada foi baixado.");
        var directory = system.WorkDirectory();
        var file = Path.Combine(directory, "PawnIO_setup.exe");
        try
        {
            Directory.CreateDirectory(directory);
            progress?.Report("Baixando o instalador oficial do PawnIO " + Version + " do GitHub...");
            if (await Download(system, file, token) is { } failure) return failure;
            string subject;
            // O arquivo fica aberto sem permitir escrita nem exclusão do conferir até o instalador terminar.
            await using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (held.Length != Size) return Integrity($"o arquivo tem {held.Length} bytes, e o esperado é {Size}");
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(held, token)).ToLowerInvariant();
                if (!string.Equals(hash, expectedSha256, StringComparison.Ordinal)) return Integrity("o SHA-256 não é o da release oficial");
                // Com o arquivo aberto, as pastas acima dele não podem ser renomeadas; uma junção ou link no caminho ainda poderia
                // ser reapontada antes do UAC, e o processo elevado abriria outro arquivo pelo mesmo caminho. Link no caminho: nada roda.
                if (system.Linked(file) is { } link)
                    return new("link", $"O caminho do instalador passa por um link ou junção ({link}). Por segurança, o arquivo foi apagado e nada foi executado.");
                progress?.Report("Conferindo a assinatura digital do instalador...");
                string status, signer;
                try { (status, signer) = await system.Signature(file, token); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception)
                {
                    return new("signature", "Não foi possível conferir a assinatura digital do instalador (" + Short(ex.Message) + "). O arquivo foi apagado e nada foi executado.");
                }
                subject = signer;
                if (!string.Equals(status, "Valid", StringComparison.Ordinal))
                    return new("signature", $"A assinatura digital do instalador não é válida (estado: {(status.Length == 0 ? "desconhecido" : status)}). O arquivo foi apagado e nada foi executado.", Signer: signer);
                progress?.Report("Aguardando a autorização de administrador (UAC) e o assistente do PawnIO...");
                int? exit;
                try { exit = await system.RunElevated(file, token); }
                catch (Win32Exception ex) { return new("failed", "O Windows não abriu o instalador do PawnIO (" + Short(ex.Message) + "). Nada foi executado.", Signer: subject); }
                if (exit == null) return new("canceled", "A autorização de administrador foi cancelada. O PawnIO não foi instalado.", Signer: subject);
                if (system.DriverInstalled())
                    return new("installed", $"PawnIO {Version} instalado. Assinatura válida de: {Signer(subject)}. Para desinstalar: {Uninstall}.", exit, subject);
                if (exit != 0) return new("failed", $"O instalador do PawnIO terminou com o código {exit}, e o driver não foi encontrado. Nada mais foi alterado pelo Nexo.", exit, subject);
                return new("not_installed", "O instalador terminou sem erro, mas o driver PawnIO não foi encontrado. O assistente pode ter sido fechado antes do fim.", exit, subject);
            }
        }
        catch (HttpRequestException ex) { return Offline(ex.Message); }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested) { return Offline(ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new("failed", "Não foi possível gravar ou ler o instalador na pasta temporária do Nexo: " + Short(ex.Message)); }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); if (Directory.Exists(directory)) Directory.Delete(directory, false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Short(string detail) => detail.Length <= 200 ? detail : detail[..200];
    private static string Signer(string subject) => subject.Length == 0 ? "assinante não informado" : subject;
    private static PawnIoInstallResult Integrity(string reason) =>
        new("integrity", "O instalador baixado não confere com a release oficial " + Version + ": " + reason + ". O arquivo foi apagado e nada foi executado.");
    private static PawnIoInstallResult Offline(string detail) =>
        new("network", "Sem conexão com o GitHub para baixar o PawnIO. Nada foi instalado. Detalhe: " + Short(detail));

    // Redirecionamento só para github.com e os hosts de arquivos do GitHub, por HTTPS; o resto é recusado antes de qualquer pedido.
    private static async Task<PawnIoInstallResult?> Download(IPawnIoInstallSystem system, string file, CancellationToken token)
    {
        var url = Url;
        for (var hop = 0; ; hop++)
        {
            if (!AllowedHost(url)) return new("host", $"O download foi redirecionado para um endereço fora do GitHub ({url.Scheme}://{url.Host}). Nada foi baixado nem executado.");
            using var response = await system.Send(url, token);
            var code = (int)response.StatusCode;
            if (code is >= 300 and < 400)
            {
                if (hop >= MaxRedirects || response.Headers.Location is not { } location)
                    return new("network", "O GitHub não entregou o instalador (redirecionamento sem destino ou repetido demais). Nada foi executado.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK) return new("network", $"O GitHub respondeu {code} ao pedido do instalador. Nada foi executado.");
            if (response.Content.Headers.ContentLength is { } length && length != Size) return Integrity($"o tamanho informado é {length} bytes, e o esperado é {Size}");
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                total += read;
                if (total > Size) return Integrity("o arquivo recebido é maior que o esperado");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            return total == Size ? null : Integrity($"o arquivo recebido tem {total} bytes, e o esperado é {Size}");
        }
    }
}

/// <summary>O Windows de verdade: HTTP sem redirecionamento automático, Get-AuthenticodeSignature e ShellExecute "runas".</summary>
public sealed class WindowsPawnIoInstallSystem : IPawnIoInstallSystem
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    public Task<HttpResponseMessage> Send(Uri url, CancellationToken token) => Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);

    // Mesma leitura da assinatura do pacote NVIDIA baixado (OfficialDriverSource.HasNvidiaSignature).
    public async Task<(string Status, string Subject)> Signature(string file, CancellationToken token)
    {
        var json = await SupportService.Powershell("$s=Get-AuthenticodeSignature -LiteralPath " + WindowsTools.Quote(file)
            + "; [pscustomobject]@{Status=[string]$s.Status;Subject=[string]$s.SignerCertificate.Subject} | ConvertTo-Json -Compress", token);
        try
        {
            using var parsed = JsonDocument.Parse(json);
            return (parsed.RootElement.GetProperty("Status").GetString() ?? "", parsed.RootElement.GetProperty("Subject").GetString() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return ("", ""); }
    }

    public async Task<int?> RunElevated(string file, CancellationToken token)
    {
        Process? process;
        try { process = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(file)! }); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return null; }
        if (process == null) throw new IOException("O Windows não iniciou o instalador do PawnIO.");
        using (process)
        {
            await process.WaitForExitAsync(token);
            return process.ExitCode;
        }
    }

    public bool DriverInstalled() => PawnIoDriver.Installed();
    public string WorkDirectory() => Path.Combine(Path.GetTempPath(), "Nexo", "PawnIO", Guid.NewGuid().ToString("N"));

    // Do arquivo até a raiz do volume: %TEMP% pode apontar para qualquer pasta do usuário, então a cadeia inteira é conferida.
    public string? Linked(string file)
    {
        for (var path = Path.GetFullPath(file); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return path;
        return null;
    }
}
