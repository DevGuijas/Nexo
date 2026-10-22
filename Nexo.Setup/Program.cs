using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace Nexo.Setup;

public sealed record Package(string Path, string Sha256, long Bytes);
public sealed record ReleaseManifest(int Schema, string Version, string Model, Package App, Package ModelArchive, Package? Ollama = null);

public static class Installer
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    public const string DefaultManifestUrl = "https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/latest.json";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromHours(2) };
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static async Task<int> Main(string[] args)
    {
        var checkOnly = args.Length == 1 && args[0] == "--check" || args.Length == 3 && args[0] == "--check" && args[1] == "--manifest";
        var manifestText = args.Length == 0 || args.Length == 1 && checkOnly ? DefaultManifestUrl
            : args.Length == 2 && args[0] == "--manifest" ? args[1]
            : args.Length == 3 && checkOnly ? args[2] : "";
        if (!Uri.TryCreate(manifestText, UriKind.Absolute, out var manifestUrl))
        {
            Console.Error.WriteLine("Uso: NexoSetup.exe [--manifest https://servidor-oracle/.../latest.json]");
            return 2;
        }
        try
        {
            ValidateManifestUri(manifestUrl);
            Console.WriteLine("Consultando a versão publicada do Nexo…");
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
            var baseUrl = new Uri(manifestUrl, ".");
            if (checkOnly)
            {
                foreach (var package in new[] { manifest.App, manifest.ModelArchive }.Concat(manifest.Ollama == null ? [] : [manifest.Ollama]))
                {
                    var uri = PackageUrl(baseUrl, package);
                    using var head = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri));
                    head.EnsureSuccessStatusCode();
                    if (head.Content.Headers.ContentLength != package.Bytes) throw new InvalidDataException("Tamanho remoto inesperado: " + package.Path);
                    Console.WriteLine("Disponível: " + package.Path);
                }
                Console.WriteLine("Manifesto assinado e arquivos publicados para a versão " + manifest.Version + ".");
                return 0;
            }
            var cache = Path.Combine(LocalAppData, "Nexo", "InstallerCache", manifest.Version);
            Directory.CreateDirectory(cache);
            var appZip = await Download(baseUrl, manifest.App, Path.Combine(cache, "nexo-app.zip"));
            var modelZip = await Download(baseUrl, manifest.ModelArchive, Path.Combine(cache, "qwen3-model.zip"));
            var ollamaSetup = manifest.Ollama == null ? null : await Download(baseUrl, manifest.Ollama, Path.Combine(cache, "OllamaSetup.exe"));

            var appRoot = Path.Combine(LocalAppData, "Programs", "Nexo");
            var stage = appRoot + ".new";
            var backup = appRoot + ".previous";
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            ExtractChecked(appZip, stage, 2_000_000_000);
            if (!File.Exists(Path.Combine(stage, "Nexo.exe"))) throw new InvalidDataException("O pacote não contém Nexo.exe.");

            if (!OllamaInstalled())
            {
                Console.WriteLine("Instalando o Ollama…");
                if (ollamaSetup != null)
                {
                    if (!await VerifyOllamaSignature(ollamaSetup))
                        throw new InvalidDataException("Pacote Ollama sem assinatura Authenticode válida.");
                    await Run(ollamaSetup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", TimeSpan.FromMinutes(15));
                }
                else await Run("winget.exe", "install --id Ollama.Ollama --exact --source winget --silent --accept-package-agreements --accept-source-agreements", TimeSpan.FromMinutes(40));
                if (!OllamaInstalled()) throw new IOException("O Ollama não foi encontrado após a instalação.");
            }

            Console.WriteLine("Instalando o modelo local qwen3:8b…");
            var modelsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
            Directory.CreateDirectory(modelsRoot);
            await InstallModelArchive(modelZip, modelsRoot);
            var modelManifest = Path.Combine(modelsRoot, "manifests", "registry.ollama.ai", "library", "qwen3", "8b");
            if (!File.Exists(modelManifest)) throw new InvalidDataException("O modelo qwen3:8b não foi encontrado no pacote.");

            Console.WriteLine("Ativando o Nexo " + manifest.Version + "…");
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(appRoot)) Directory.Move(appRoot, backup);
            try { Directory.Move(stage, appRoot); }
            catch { if (Directory.Exists(backup) && !Directory.Exists(appRoot)) Directory.Move(backup, appRoot); throw; }
            try { CreateStartMenuShortcut(appRoot); }
            catch (Exception ex) { Console.WriteLine("Atalho não criado: " + ex.Message); }
            Console.WriteLine("Instalação concluída: " + Path.Combine(appRoot, "Nexo.exe"));
            MessageBoxW(IntPtr.Zero, "Nexo " + manifest.Version + " instalado com o modelo local. Abra pelo menu Iniciar.", "Nexo instalado", 0x40);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Instalação interrompida: " + ex.Message);
            if (!checkOnly) MessageBoxW(IntPtr.Zero, "A instalação foi interrompida: " + ex.Message, "Nexo", 0x10);
            return 1;
        }
    }

    public static void ValidateManifestUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !uri.AbsolutePath.EndsWith("/latest.json", StringComparison.Ordinal))
            throw new InvalidDataException("Use um manifesto latest.json publicado por HTTPS.");
    }

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
                || !path.StartsWith("releases/" + manifest.Version + "/", StringComparison.Ordinal)
                || !Regex.IsMatch(package.Sha256 ?? "", "^[0-9a-fA-F]{64}$") || package.Bytes is < 1000 or > 8_000_000_000)
                throw new InvalidDataException("Arquivo do manifesto inválido.");
        }
    }

    private static async Task<string> Download(Uri baseUrl, Package package, string destination)
    {
        var source = PackageUrl(baseUrl, package);
        if (File.Exists(destination) && new FileInfo(destination).Length == package.Bytes && await HashMatches(destination, package.Sha256))
        {
            Console.WriteLine("Arquivo já verificado: " + Path.GetFileName(destination));
            return destination;
        }
        Console.WriteLine("Baixando " + Path.GetFileName(destination) + "…");
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
                    if (percent >= reported + 10) { reported = percent / 10 * 10; Console.WriteLine($"  {reported}% recebido"); }
                }
                if (total != package.Bytes) throw new InvalidDataException("Download incompleto.");
            }
            if (!await HashMatches(temp, package.Sha256)) throw new InvalidDataException("SHA-256 diferente do manifesto para " + package.Path);
            File.Move(temp, destination, true);
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
            var digests = new[] { document.RootElement.GetProperty("config").GetProperty("digest").GetString() }
                .Concat(document.RootElement.GetProperty("layers").EnumerateArray().Select(layer => layer.GetProperty("digest").GetString()));
            foreach (var digest in digests)
                if (digest == null || !Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$")
                    || !File.Exists(Path.Combine(modelsRoot, "blobs", digest.Replace(':', '-'))))
                    throw new InvalidDataException("O manifesto do modelo referencia um blob ausente.");
            File.Move(tempManifest, targetManifest, true);
        }
        finally { if (File.Exists(tempManifest)) File.Delete(tempManifest); }
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

    private static void CreateStartMenuShortcut(string appRoot)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Atalhos do Windows indisponíveis.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs");
        Directory.CreateDirectory(programs);
        dynamic shortcut = shell.CreateShortcut(Path.Combine(programs, "Nexo.lnk"));
        shortcut.TargetPath = Path.Combine(appRoot, "Nexo.exe");
        shortcut.WorkingDirectory = appRoot;
        shortcut.IconLocation = Path.Combine(appRoot, "Nexo.exe");
        shortcut.Save();
    }

    private static async Task<bool> VerifyOllamaSignature(string path)
    {
        var script = "$s=Get-AuthenticodeSignature -LiteralPath '" + path.Replace("'", "''") + "'; [pscustomobject]@{Status=[string]$s.Status;Subject=[string]$s.SignerCertificate.Subject} | ConvertTo-Json -Compress";
        var command = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + command,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new IOException("Não foi possível validar a assinatura do Ollama.");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Falha ao validar a assinatura do Ollama.");
        using var result = JsonDocument.Parse(output);
        var status = result.RootElement.GetProperty("Status").GetString();
        if (status == "NotSigned") return false;
        if (status != "Valid" || !(result.RootElement.GetProperty("Subject").GetString() ?? "").Contains("Ollama", StringComparison.OrdinalIgnoreCase))
            throw new IOException("A assinatura do Ollama é inválida ou pertence a outro fornecedor.");
        return true;
    }
}
