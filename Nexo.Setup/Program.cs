using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.IO;
using System.Windows;

namespace Nexo.Setup;

public sealed record Package(string Path, string Sha256, long Bytes);
public sealed record ReleaseManifest(int Schema, string Version, string Model, Package App, Package ModelArchive, Package? Ollama = null);

public static class Installer
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
#if NEXO_PREVIEW
    public const string DefaultManifestUrl = "https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/preview/latest.json";
    public const bool IsPreview = true;
#else
    public const string DefaultManifestUrl = "https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/latest.json";
    public const bool IsPreview = false;
#endif
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromHours(2) };
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--uninstall-worker")
            return Uninstaller.RunWorkerAsync(args).GetAwaiter().GetResult();
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Equals("NexoUninstall", StringComparison.OrdinalIgnoreCase))
        {
            var uninstallWindow = new UninstallWindow();
            new Application().Run(uninstallWindow);
            return uninstallWindow.ExitCode;
        }
        if (args.Contains("--check")) return RunAsync(args, Console.WriteLine, false).GetAwaiter().GetResult();
        var window = new SetupWindow(args);
        new Application().Run(window);
        return window.ExitCode;
    }

    public static async Task<int> RunAsync(string[] args, Action<string> report, bool showMessage,
        bool allowGeoIp = false, string? logDirectory = null, string? uninstallerSourcePath = null)
    {
        var checkOnly = args.Length == 1 && args[0] == "--check" || args.Length == 3 && args[0] == "--check" && args[1] == "--manifest";
        var manifestText = args.Length == 0 || args.Length == 1 && checkOnly ? DefaultManifestUrl
            : args.Length == 2 && args[0] == "--manifest" ? args[1]
            : args.Length == 3 && checkOnly ? args[2] : "";
        InstallAudit audit;
        try { audit = new InstallAudit(logDirectory, checkOnly ? "check" : "install"); }
        catch (Exception ex)
        {
            report("Não foi possível criar o log da instalação: " + ex.Message);
            return 1;
        }
        using (audit)
        {
        void Report(string message)
        {
            report(message);
            audit.Record("message", "info", new { text = message });
        }
        if (checkOnly || !allowGeoIp)
            audit.Record("geoip", "skipped", new { reason = checkOnly ? "check_mode" : "not_selected" });
        if (!Uri.TryCreate(manifestText, UriKind.Absolute, out var manifestUrl))
        {
            Report("Uso: NexoSetup.exe [--manifest https://servidor-oracle/.../latest.json]");
            audit.Complete("failure", new { reason = "invalid_arguments" });
            return 2;
        }
        try
        {
            ValidateManifestUri(manifestUrl);
            if (allowGeoIp && !checkOnly)
            {
                Report("Consultando localização aproximada por GeoIP…");
                try { audit.Record("geoip", "success", await GeoIpLookup.LookupAsync()); }
                catch (Exception ex) { audit.Record("geoip", "unavailable", new { reason = ex.GetType().Name }); }
            }
            Report("Consultando a versão publicada do Nexo…");
            audit.Record("manifest", "fetch_started", new { source = manifestUrl.AbsoluteUri });
            using var response = await Client.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 64_000) throw new InvalidDataException("Manifesto muito grande.");
            var manifestBytes = await response.Content.ReadAsByteArrayAsync();
            if (manifestBytes.Length > 64_000) throw new InvalidDataException("Manifesto muito grande.");
            using var signatureResponse = await Client.GetAsync(new Uri(manifestUrl, "latest.sig"));
            signatureResponse.EnsureSuccessStatusCode();
            var signatureText = await signatureResponse.Content.ReadAsStringAsync();
            if (signatureText.Length > 2048 || !VerifyReleaseSignature(manifestBytes, signatureText.Trim()))
                throw new InvalidDataException("Assinatura do manifesto inválida. Nenhum pacote será executado.");
            var json = Encoding.UTF8.GetString(manifestBytes);
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Manifesto vazio.");
            ValidateManifest(manifest);
            audit.Record("manifest", "verified", new { version = manifest.Version, model = manifest.Model, bytes = manifestBytes.Length });
            var baseUrl = PackageBaseUrl(manifestUrl);
            if (checkOnly)
            {
                foreach (var package in new[] { manifest.App, manifest.ModelArchive }.Concat(manifest.Ollama == null ? [] : [manifest.Ollama]))
                {
                    var uri = PackageUrl(baseUrl, package);
                    using var head = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri));
                    head.EnsureSuccessStatusCode();
                    if (head.Content.Headers.ContentLength != package.Bytes) throw new InvalidDataException("Tamanho remoto inesperado: " + package.Path);
                    audit.Record("package", "available", new { package = package.Path, bytes = package.Bytes, sha256 = package.Sha256 });
                    Report("Disponível: " + package.Path);
                }
                Report("Manifesto assinado e arquivos publicados para a versão " + manifest.Version + ".");
                audit.Complete("success", new { version = manifest.Version, mode = "check" });
                return 0;
            }
            var cache = Path.Combine(LocalAppData, "Nexo", "InstallerCache", manifest.Version);
            Directory.CreateDirectory(cache);
            var appZip = await Download(baseUrl, manifest.App, Path.Combine(cache, "nexo-app.zip"), Report, audit);
            var modelZip = await Download(baseUrl, manifest.ModelArchive, Path.Combine(cache, "qwen3-model.zip"), Report, audit);
            var ollamaSetup = manifest.Ollama == null ? null : await Download(baseUrl, manifest.Ollama, Path.Combine(cache, "OllamaSetup.exe"), Report, audit);

            var appRoot = Path.Combine(LocalAppData, "Programs", "Nexo");
            var stage = appRoot + ".new";
            var backup = appRoot + ".previous";
            var stageTime = Stopwatch.StartNew();
            audit.Record("application", "staging_started", new { version = manifest.Version });
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            ExtractChecked(appZip, stage, 2_000_000_000);
            if (!File.Exists(Path.Combine(stage, "Nexo.exe"))) throw new InvalidDataException("O pacote não contém Nexo.exe.");
            StageUninstaller(uninstallerSourcePath ?? Environment.ProcessPath
                ?? throw new IOException("Executável do instalador indisponível."), stage);
            audit.Record("uninstaller", "staged", new { file = "NexoUninstall.exe" });
            audit.Record("application", "staged", new { version = manifest.Version, durationMs = stageTime.ElapsedMilliseconds });

            if (!OllamaInstalled())
            {
                var ollamaTime = Stopwatch.StartNew();
                Report("Instalando o Ollama…");
                audit.Record("ollama", "install_started", new { source = ollamaSetup == null ? "winget" : "verified_package" });
                if (ollamaSetup != null)
                {
                    if (!await VerifyOllamaSignature(ollamaSetup))
                        throw new InvalidDataException("Pacote Ollama sem assinatura Authenticode válida.");
                    audit.Record("ollama", "signature_verified", new { signer = "Ollama Inc." });
                    await Run(ollamaSetup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", TimeSpan.FromMinutes(15));
                }
                else await Run("winget.exe", "install --id Ollama.Ollama --exact --source winget --silent --accept-package-agreements --accept-source-agreements", TimeSpan.FromMinutes(40));
                if (!OllamaInstalled()) throw new IOException("O Ollama não foi encontrado após a instalação.");
                audit.Record("ollama", "installed", new { durationMs = ollamaTime.ElapsedMilliseconds });
            }
            else audit.Record("ollama", "already_installed");

            Report("Instalando o modelo local qwen3:8b…");
            var modelTime = Stopwatch.StartNew();
            audit.Record("model", "import_started", new { model = manifest.Model });
            var modelsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
            Directory.CreateDirectory(modelsRoot);
            await InstallModelArchive(modelZip, modelsRoot);
            var modelManifest = Path.Combine(modelsRoot, "manifests", "registry.ollama.ai", "library", "qwen3", "8b");
            if (!File.Exists(modelManifest)) throw new InvalidDataException("O modelo qwen3:8b não foi encontrado no pacote.");
            audit.Record("model", "installed", new { model = manifest.Model, durationMs = modelTime.ElapsedMilliseconds });

            Report("Ativando o Nexo " + manifest.Version + "…");
            var activationTime = Stopwatch.StartNew();
            audit.Record("application", "activation_started", new { version = manifest.Version, priorInstallation = Directory.Exists(appRoot) });
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(appRoot)) Directory.Move(appRoot, backup);
            try { Directory.Move(stage, appRoot); }
            catch { if (Directory.Exists(backup) && !Directory.Exists(appRoot)) Directory.Move(backup, appRoot); throw; }
            audit.Record("application", "activated", new { version = manifest.Version, durationMs = activationTime.ElapsedMilliseconds });
            try { CreateStartMenuShortcut(appRoot); audit.Record("shortcut_start_menu", "created"); }
            catch (Exception ex) { audit.Record("shortcut_start_menu", "failed", new { error = ex.Message }); Report("Atalho do menu Iniciar não criado: " + ex.Message); }
            var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            audit.Record("shortcut_desktop", "creation_started", new { path = Path.Combine(desktopDirectory, "Nexo.lnk") });
            try { CreateShortcut(appRoot, desktopDirectory); }
            catch (Exception ex)
            {
                audit.Record("shortcut_desktop", "failed", new { type = ex.GetType().Name, message = ex.Message });
                throw;
            }
            audit.Record("shortcut_desktop", "created", new { path = Path.Combine(desktopDirectory, "Nexo.lnk") });
            Report("Instalação concluída: " + Path.Combine(appRoot, "Nexo.exe"));
            Report("Log da instalação: " + audit.Path);
            audit.Complete("success", new { version = manifest.Version, desktopShortcut = true });
            if (showMessage) MessageBoxW(IntPtr.Zero, "Nexo " + manifest.Version + " instalado com o modelo local. Abra pela Área de Trabalho ou pelo menu Iniciar.", "Nexo instalado", 0x40);
            return 0;
        }
        catch (Exception ex)
        {
            audit.Record("error", "failure", new { type = ex.GetType().Name, message = ex.Message });
            Report("Instalação interrompida: " + ex.Message);
            Report("Log da instalação: " + audit.Path);
            audit.Complete("failure", new { errorType = ex.GetType().Name });
            if (!checkOnly && showMessage) MessageBoxW(IntPtr.Zero, "A instalação foi interrompida: " + ex.Message, "Nexo", 0x10);
            return 1;
        }
        }
    }

    public static void ValidateManifestUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !uri.AbsolutePath.EndsWith("/latest.json", StringComparison.Ordinal))
            throw new InvalidDataException("Use um manifesto latest.json publicado por HTTPS.");
    }

    public static Uri PackageBaseUrl(Uri manifestUrl) =>
        new(manifestUrl, manifestUrl.AbsolutePath.EndsWith("/preview/latest.json", StringComparison.Ordinal) ? "../" : ".");

    public static bool VerifyReleaseSignature(byte[] manifest, string signatureText)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureText); }
        catch (FormatException) { return false; }
        using var stream = typeof(Installer).Assembly.GetManifestResourceStream("Nexo.Setup.release-public.pem")
            ?? throw new InvalidOperationException("Chave pública do instalador ausente.");
        using var reader = new StreamReader(stream);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(reader.ReadToEnd());
        return rsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public static void ValidateManifest(ReleaseManifest manifest)
    {
        if (manifest.Schema != 1 || !Regex.IsMatch(manifest.Version ?? "", @"^\d+\.\d+\.\d+$") || manifest.Model != "qwen3:8b")
            throw new InvalidDataException("Versão ou modelo não suportado.");
        foreach (var package in new[] { manifest.App, manifest.ModelArchive }.Concat(manifest.Ollama == null ? [] : [manifest.Ollama]))
        {
            if (package == null) throw new InvalidDataException("Arquivo ausente no manifesto.");
            if (package.Path is not { } path || !Regex.IsMatch(path, @"^releases/\d+\.\d+\.\d+/[A-Za-z0-9_.-]+$")
                || package == manifest.App && !path.StartsWith("releases/" + manifest.Version + "/", StringComparison.Ordinal)
                || !Regex.IsMatch(package.Sha256 ?? "", "^[0-9a-fA-F]{64}$") || package.Bytes is < 1000 or > 8_000_000_000)
                throw new InvalidDataException("Arquivo do manifesto inválido.");
        }
    }

    private static async Task<string> Download(Uri baseUrl, Package package, string destination, Action<string> report, InstallAudit? audit = null)
    {
        var source = PackageUrl(baseUrl, package);
        var elapsed = Stopwatch.StartNew();
        if (File.Exists(destination) && new FileInfo(destination).Length == package.Bytes && await HashMatches(destination, package.Sha256))
        {
            report("Arquivo já verificado: " + Path.GetFileName(destination));
            audit?.Record("package", "cache_verified", new { package = package.Path, bytes = package.Bytes, sha256 = package.Sha256, durationMs = elapsed.ElapsedMilliseconds });
            return destination;
        }
        report("Baixando " + Path.GetFileName(destination) + "…");
        audit?.Record("package", "download_started", new { package = package.Path, expectedBytes = package.Bytes, expectedSha256 = package.Sha256 });
        var temp = destination + ".download";
        try
        {
            using var response = await Client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != package.Bytes) throw new InvalidDataException("Tamanho remoto diferente do manifesto.");
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[1024 * 1024];
                long total = 0;
                var reported = 0;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > package.Bytes) throw new InvalidDataException("Download maior que o tamanho anunciado.");
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    var percent = (int)(total * 100 / package.Bytes);
                    if (percent >= reported + 10) { reported = percent / 10 * 10; report($"  {reported}% recebido"); }
                }
                if (total != package.Bytes) throw new InvalidDataException("Download incompleto.");
            }
            if (!await HashMatches(temp, package.Sha256)) throw new InvalidDataException("SHA-256 diferente do manifesto para " + package.Path);
            File.Move(temp, destination, true);
            audit?.Record("package", "download_verified", new { package = package.Path, bytes = package.Bytes, sha256 = package.Sha256, durationMs = elapsed.ElapsedMilliseconds });
            return destination;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static Uri PackageUrl(Uri baseUrl, Package package)
    {
        var source = new Uri(baseUrl, package.Path);
        if (source.Scheme != Uri.UriSchemeHttps || source.Host != baseUrl.Host || source.Port != baseUrl.Port
            || !source.AbsolutePath.StartsWith(baseUrl.AbsolutePath, StringComparison.Ordinal))
            throw new InvalidDataException("Arquivo fora da origem do manifesto.");
        return source;
    }

    private static async Task<bool> HashMatches(string path, string expected)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    public static void ExtractChecked(string archive, string destination, long maxExpandedBytes)
    {
        using var zip = ZipFile.OpenRead(archive);
        long expanded = 0;
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > maxExpandedBytes) throw new InvalidDataException("Pacote expandido excede o limite.");
            var path = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':'))
                throw new InvalidDataException("O pacote contém um caminho inválido.");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, true);
        }
    }

    public static async Task InstallModelArchive(string archive, string modelsRoot)
    {
        const string manifestName = "manifests/registry.ollama.ai/library/qwen3/8b";
        using var zip = ZipFile.OpenRead(archive);
        long expanded = 0;
        var manifest = zip.GetEntry(manifestName) ?? throw new InvalidDataException("Manifesto qwen3:8b ausente no pacote.");
        var blobs = new List<ZipArchiveEntry>();
        foreach (var entry in zip.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > 8_000_000_000) throw new InvalidDataException("Modelo expandido excede o limite.");
            if (entry.FullName == manifestName) continue;
            if (!Regex.IsMatch(entry.FullName, "^blobs/sha256-[0-9a-f]{64}$")) throw new InvalidDataException("Arquivo inesperado no pacote do modelo.");
            blobs.Add(entry);
        }
        foreach (var entry in blobs)
        {
            var name = Path.GetFileName(entry.FullName);
            var destination = Path.Combine(modelsRoot, "blobs", name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var expected = name[7..];
            if (File.Exists(destination) && new FileInfo(destination).Length == entry.Length && await HashMatches(destination, expected)) continue;
            var temporary = destination + ".nexo-new";
            try
            {
                entry.ExtractToFile(temporary, true);
                if (!await HashMatches(temporary, expected)) throw new InvalidDataException("Blob do modelo não corresponde ao digest: " + name);
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var targetManifest = Path.Combine(modelsRoot, manifestName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(targetManifest)!);
        var tempManifest = targetManifest + ".nexo-new";
        try
        {
            manifest.ExtractToFile(tempManifest, true);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(tempManifest));
            await EnsureModelManifestBlobs(zip, document.RootElement, modelsRoot);
            File.Move(tempManifest, targetManifest, true);
        }
        finally { if (File.Exists(tempManifest)) File.Delete(tempManifest); }
    }

    public static async Task EnsureModelManifestBlobs(ZipArchive zip, JsonElement modelManifest, string modelsRoot)
    {
        var digests = new[] { modelManifest.GetProperty("config").GetProperty("digest").GetString() }
            .Concat(modelManifest.GetProperty("layers").EnumerateArray().Select(layer => layer.GetProperty("digest").GetString()));
        foreach (var digest in digests)
        {
            if (digest == null || !Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$"))
                throw new InvalidDataException("Digest inválido no manifesto do modelo.");
            var name = digest.Replace(':', '-');
            var destination = Path.Combine(modelsRoot, "blobs", name);
            if (File.Exists(destination)) continue;
            var entry = zip.GetEntry("blobs/" + name)
                ?? throw new InvalidDataException("O manifesto do modelo referencia um blob ausente.");
            var temporary = destination + ".nexo-new";
            try
            {
                entry.ExtractToFile(temporary, true);
                if (!await HashMatches(temporary, digest[7..]))
                    throw new InvalidDataException("Blob do modelo não corresponde ao digest: " + name);
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static async Task Run(string program, string arguments, TimeSpan timeout)
    {
        using var process = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true })
            ?? throw new IOException("Não foi possível iniciar o instalador do Ollama.");
        using var cancel = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancel.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("O instalador do Ollama excedeu o tempo limite."); }
        if (process.ExitCode is not (0 or 3010)) throw new IOException("O instalador do Ollama terminou com código " + process.ExitCode);
    }

    private static bool OllamaInstalled() => File.Exists(Path.Combine(LocalAppData, "Programs", "Ollama", "ollama.exe"))
        || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"));

    public static void CreateShortcut(string appRoot, string shortcutDirectory)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Atalhos do Windows indisponíveis.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        Directory.CreateDirectory(shortcutDirectory);
        dynamic shortcut = shell.CreateShortcut(Path.Combine(shortcutDirectory, "Nexo.lnk"));
        shortcut.TargetPath = Path.Combine(appRoot, "Nexo.exe");
        shortcut.WorkingDirectory = appRoot;
        shortcut.IconLocation = Path.Combine(appRoot, "Nexo.exe");
        shortcut.Save();
    }

    private static void CreateStartMenuShortcut(string appRoot) => CreateShortcut(appRoot,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"));

    public static void StageUninstaller(string installerExecutable, string appStage)
    {
        if (!File.Exists(installerExecutable) || !Path.GetExtension(installerExecutable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Executável de desinstalação ausente.");
        File.Copy(installerExecutable, Path.Combine(appStage, "NexoUninstall.exe"), true);
    }

    // Mesmo escape do WindowsTools.Quote: o PowerShell também fecha a string simples com ‘ ’ ‚ ‛.
    internal static string Quote(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var character in value)
        {
            quoted.Append(character);
            if (character is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B') quoted.Append(character);
        }
        return quoted.Append('\'').ToString();
    }

    public static ProcessStartInfo CreateSignatureCheckProcess(string path)
    {
        var script = "$s=Get-AuthenticodeSignature -LiteralPath " + Quote(path) + "; [pscustomobject]@{Status=[string]$s.Status;Subject=[string]$s.SignerCertificate.Subject} | ConvertTo-Json -Compress";
        var command = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var windowsPowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0");
        var startInfo = new ProcessStartInfo(Path.Combine(windowsPowerShell, "powershell.exe"))
        {
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + command,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        startInfo.Environment["PSModulePath"] = Path.Combine(windowsPowerShell, "Modules");
        return startInfo;
    }

    private static async Task<bool> VerifyOllamaSignature(string path)
    {
        using var process = Process.Start(CreateSignatureCheckProcess(path))
            ?? throw new IOException("Não foi possível validar a assinatura do Ollama.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Falha ao validar a assinatura do Ollama: " + errors);
        using var result = JsonDocument.Parse(output);
        var status = result.RootElement.GetProperty("Status").GetString();
        var subject = result.RootElement.GetProperty("Subject").GetString() ?? "";
        if (status == "NotSigned") return false;
        if (status != "Valid" || !subject.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"A assinatura do Ollama é inválida ou pertence a outro fornecedor (status: {status}; titular: {subject}; erro: {errors}).");
        return true;
    }
}
