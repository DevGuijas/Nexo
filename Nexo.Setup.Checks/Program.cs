using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexo.Setup;

if (args.Contains("--remote"))
{
    Environment.ExitCode = await Installer.RunAsync(["--check"], Console.WriteLine, false);
    return;
}

var root = Path.Combine(Path.GetTempPath(), "NexoSetupChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var count = 0;
void Check(bool condition, string title) { if (!condition) throw new Exception("FAIL: " + title); Console.WriteLine("PASS: " + title); count++; }
void Reject(Action action, string title)
{
    try { action(); } catch (InvalidDataException) { Check(true, title); return; }
    throw new Exception("FAIL: " + title);
}
try
{
    Installer.ValidateManifestUri(new Uri("https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/ns/b/nexo/o/latest.json"));
    Check(true, "Manifesto Oracle por HTTPS aceito");
    var releaseRoot = "https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/ns/b/nexo/o/";
    Check(Installer.PackageBaseUrl(new Uri(releaseRoot + "latest.json")).AbsoluteUri == releaseRoot,
        "Canal estável resolve pacotes na raiz do bucket");
    Check(Installer.PackageBaseUrl(new Uri(releaseRoot + "preview/latest.json")).AbsoluteUri == releaseRoot,
        "Canal de prévia reutiliza pacotes na raiz do bucket");
    var signatureProcess = Installer.CreateSignatureCheckProcess(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
    Check(signatureProcess.Environment["PSModulePath"] == Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules"),
        "Checagem Authenticode usa somente módulos do Windows, mesmo quando iniciada pelo PowerShell 7");
    var shortcutFolder = Path.Combine(root, "Desktop");
    Directory.CreateDirectory(shortcutFolder);
    Installer.CreateShortcut(Path.Combine(root, "installed"), shortcutFolder);
    var shortcutPath = Path.Combine(shortcutFolder, "Nexo.lnk");
    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
    dynamic shortcut = shell.CreateShortcut(shortcutPath);
    Check(File.Exists(shortcutPath) && (string)shortcut.TargetPath == Path.Combine(root, "installed", "Nexo.exe"),
        "Atalho da Área de Trabalho aponta para o executável instalado");
    var stagedApp = Path.Combine(root, "staged-app");
    Directory.CreateDirectory(stagedApp);
    Installer.StageUninstaller(Environment.ProcessPath!, stagedApp);
    Check(File.Exists(Path.Combine(stagedApp, "NexoUninstall.exe"))
        && new FileInfo(Path.Combine(stagedApp, "NexoUninstall.exe")).Length == new FileInfo(Environment.ProcessPath!).Length,
        "Instalação inclui o desinstalador executável ao lado do Nexo");
    var uninstallRoot = Path.Combine(root, "uninstall-case");
    var uninstallApp = Path.Combine(uninstallRoot, "Programs", "Nexo");
    var uninstallCache = Path.Combine(uninstallRoot, "Nexo", "InstallerCache");
    var uninstallLogs = Path.Combine(uninstallRoot, "Nexo", "InstallerLogs");
    var uninstallDesktop = Path.Combine(uninstallRoot, "Desktop");
    var uninstallMenu = Path.Combine(uninstallRoot, "StartMenu");
    Directory.CreateDirectory(uninstallApp);
    Directory.CreateDirectory(uninstallCache);
    Directory.CreateDirectory(uninstallLogs);
    File.WriteAllText(Path.Combine(uninstallApp, "Nexo.exe"), "app");
    File.WriteAllText(Path.Combine(uninstallApp, "NexoUninstall.exe"), "uninstaller");
    File.WriteAllText(Path.Combine(uninstallCache, "package.zip"), "cache");
    File.WriteAllText(Path.Combine(uninstallLogs, "history.jsonl"), "history");
    var sharedOllama = Path.Combine(uninstallRoot, "Ollama", "ollama.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(sharedOllama)!);
    File.WriteAllText(sharedOllama, "shared");
    Installer.CreateShortcut(uninstallApp, uninstallDesktop);
    Installer.CreateShortcut(uninstallApp, uninstallMenu);
    using (var uninstallAudit = new InstallAudit(uninstallLogs, "uninstall"))
    {
        Uninstaller.RemoveInstallation(new UninstallTargets(uninstallApp, uninstallCache,
            Path.Combine(uninstallDesktop, "Nexo.lnk"), Path.Combine(uninstallMenu, "Nexo.lnk")), uninstallAudit);
        uninstallAudit.Complete("success");
    }
    Check(!Directory.Exists(uninstallApp) && !Directory.Exists(uninstallCache)
        && !File.Exists(Path.Combine(uninstallDesktop, "Nexo.lnk")) && !File.Exists(Path.Combine(uninstallMenu, "Nexo.lnk"))
        && File.Exists(sharedOllama) && File.Exists(Path.Combine(uninstallLogs, "history.jsonl"))
        && File.ReadAllText(Path.Combine(uninstallLogs, "history.jsonl")) == "history",
        "Desinstalação remove Nexo, cache e atalhos próprios; preserva Ollama e logs");
    var otherRoot = Path.Combine(root, "other-shortcut-case");
    var otherApp = Path.Combine(otherRoot, "Nexo");
    var otherLink = Path.Combine(otherRoot, "Desktop", "Nexo.lnk");
    Directory.CreateDirectory(otherApp);
    Directory.CreateDirectory(Path.GetDirectoryName(otherLink)!);
    File.WriteAllText(Path.Combine(otherApp, "Nexo.exe"), "app");
    File.WriteAllText(Path.Combine(otherApp, "NexoUninstall.exe"), "uninstaller");
    File.WriteAllText(otherLink, "atalho não pertencente ao Nexo");
    using (var otherAudit = new InstallAudit(Path.Combine(otherRoot, "Logs"), "uninstall"))
    {
        Uninstaller.RemoveInstallation(new UninstallTargets(otherApp, Path.Combine(otherRoot, "InstallerCache"),
            otherLink, Path.Combine(otherRoot, "StartMenu", "Nexo.lnk")), otherAudit);
        otherAudit.Complete("success");
    }
    Check(File.Exists(otherLink) && !Directory.Exists(otherApp), "Desinstalador preserva atalho que não reconhece como próprio");
    var auditFolder = Path.Combine(root, "logs");
    string firstLogPath;
    string firstDeviceId;
    using (var first = new InstallAudit(auditFolder, "install"))
    {
        first.Record("package", "cached", new { package = "nexo-app.zip", bytes = 71351297 });
        first.Complete("success");
        firstLogPath = first.Path;
        firstDeviceId = first.DeviceId;
    }
    var lines = File.ReadAllLines(firstLogPath);
    using (var start = JsonDocument.Parse(lines[0]))
    using (var package = JsonDocument.Parse(lines[1]))
    using (var finish = JsonDocument.Parse(lines[2]))
        Check(lines.Length == 3 && start.RootElement.GetProperty("event").GetString() == "run_started"
            && start.RootElement.GetProperty("schemaVersion").GetInt32() == 1
            && start.RootElement.GetProperty("data").GetProperty("physicalMemoryBytes").GetUInt64() > 0
            && package.RootElement.GetProperty("status").GetString() == "cached"
            && finish.RootElement.GetProperty("status").GetString() == "success",
            "Log JSONL mantém contexto, cache e resultado em eventos distintos");
    using (var second = new InstallAudit(auditFolder, "install"))
        Check(Directory.GetFiles(auditFolder, "*.jsonl").Length == 2 && second.DeviceId == firstDeviceId
            && second.Path != firstLogPath,
            "Cada execução tem um log próprio e a identificação do computador é estável");
    var geo = GeoIpLookup.Parse("{\"success\":true,\"ip\":\"203.0.113.10\",\"latitude\":-23.5,\"longitude\":-46.6,\"postal\":\"01000\",\"country_code\":\"BR\",\"region\":\"São Paulo\",\"city\":\"São Paulo\"}");
    var geoJson = JsonSerializer.Serialize(geo);
    Check(geo.CountryCode == "BR" && geo.Region == "São Paulo" && geo.City == "São Paulo"
        && !geoJson.Contains("203.0.113.10") && !geoJson.Contains("01000") && !geoJson.Contains("-23.5"),
        "GeoIP guarda somente localização aproximada, sem IP ou coordenadas");
    Reject(() => GeoIpLookup.Parse("{\"success\":false,\"message\":\"Rate limit\"}"), "Erro da API GeoIP não vira localização válida");
    var failedLogFolder = Path.Combine(root, "failed-install-logs");
    var failureCode = await Installer.RunAsync(["--manifest", "https://127.0.0.1:1/latest.json"], _ => { }, false,
        allowGeoIp: false, logDirectory: failedLogFolder);
    var failureLog = Directory.GetFiles(failedLogFolder, "*.jsonl").Single();
    var failedEvents = File.ReadAllLines(failureLog).Select(line => JsonDocument.Parse(line)).ToArray();
    try
    {
        Check(failureCode == 1 && failedEvents.Any(e => e.RootElement.GetProperty("event").GetString() == "run_finished"
                && e.RootElement.GetProperty("status").GetString() == "failure")
            && failedEvents.Any(e => e.RootElement.GetProperty("event").GetString() == "geoip"
                && e.RootElement.GetProperty("status").GetString() == "skipped"),
            "Falha de instalação termina no log e não consulta GeoIP sem consentimento");
    }
    finally { foreach (var entry in failedEvents) entry.Dispose(); }
    Reject(() => Installer.ValidateManifestUri(new Uri("http://example.com/latest.json")), "Manifesto sem HTTPS rejeitado");
    Reject(() => Installer.ValidateManifestUri(new Uri("https://example.com/other.json")), "Manifesto fora do nome esperado rejeitado");
    var hash = new string('a', 64);
    var valid = new ReleaseManifest(1, "1.6.2", "qwen3:8b", new Package("releases/1.6.2/nexo-app.zip", hash, 1000),
        new Package("releases/1.6.2/qwen3-8b-model.zip", hash, 5_000_000_000), new Package("releases/1.6.2/OllamaSetup.exe", hash, 1000));
    Installer.ValidateManifest(valid);
    Check(true, "Manifesto completo e modelo esperado aceitos");
    Installer.ValidateManifest(valid with { Version = "1.6.3", App = valid.App with { Path = "releases/1.6.3/nexo-app.zip" } });
    Check(true, "Nova versão pode reutilizar pacotes de modelo e runtime com hash assinado");
    Reject(() => Installer.ValidateManifest(valid with { Version = "1.6.3" }), "Aplicativo deve pertencer à versão publicada");
    Reject(() => Installer.ValidateManifest(valid with { App = valid.App with { Path = "../evil.exe" } }), "Caminho fora da versão rejeitado");
    Reject(() => Installer.ValidateManifest(valid with { Model = "outro-modelo" }), "Modelo inesperado rejeitado");
    Reject(() => Installer.ValidateManifest(valid with { App = valid.App with { Sha256 = "invalido" } }), "Hash inválido rejeitado");
    var signedBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "signed-manifest.json"));
    var signedProof = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "signed-manifest.sig"));
    Check(Installer.VerifyReleaseSignature(signedBytes, signedProof), "Manifesto assinado pela chave de publicação aceito");
    signedBytes[0] ^= 1;
    Check(!Installer.VerifyReleaseSignature(signedBytes, signedProof) && !Installer.VerifyReleaseSignature(signedBytes, "assinatura-invalida"), "Manifesto adulterado ou assinatura inválida rejeitados");
    var archive = Path.Combine(root, "traversal.zip");
    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("../outside.txt");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("não escrever fora");
    }
    Reject(() => Installer.ExtractChecked(archive, Path.Combine(root, "target"), 1000), "Entrada ZIP que escapa da pasta rejeitada");
    Check(!File.Exists(Path.Combine(root, "outside.txt")), "Nenhum arquivo foi criado fora da pasta de instalação");
    var blobData = Encoding.UTF8.GetBytes("model-data");
    var digest = Convert.ToHexString(SHA256.HashData(blobData)).ToLowerInvariant();
    var modelArchive = Path.Combine(root, "model.zip");
    using (var zip = ZipFile.Open(modelArchive, ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(zip.CreateEntry("manifests/registry.ollama.ai/library/qwen3/8b").Open()))
            writer.Write("{\"config\":{\"digest\":\"sha256:" + digest + "\"},\"layers\":[]}");
        using var blob = zip.CreateEntry("blobs/sha256-" + digest).Open();
        blob.Write(blobData);
    }
    var modelRoot = Path.Combine(root, "valid-model");
    await Installer.InstallModelArchive(modelArchive, modelRoot);
    Check(File.Exists(Path.Combine(modelRoot, "blobs", "sha256-" + digest))
        && File.Exists(Path.Combine(modelRoot, "manifests", "registry.ollama.ai", "library", "qwen3", "8b")), "Modelo validado por digest e manifesto publicado por último");
    var installedBlob = Path.Combine(modelRoot, "blobs", "sha256-" + digest);
    File.Delete(installedBlob);
    using (var zip = ZipFile.OpenRead(modelArchive))
    using (var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(modelRoot, "manifests", "registry.ollama.ai", "library", "qwen3", "8b"))))
        await Installer.EnsureModelManifestBlobs(zip, document.RootElement, modelRoot);
    Check(File.Exists(installedBlob) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(installedBlob))).Equals(digest, StringComparison.OrdinalIgnoreCase),
        "Blob removido durante a importação é restaurado antes de ativar o manifesto");
    var corruptArchive = Path.Combine(root, "corrupt-model.zip");
    using (var zip = ZipFile.Open(corruptArchive, ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(zip.CreateEntry("manifests/registry.ollama.ai/library/qwen3/8b").Open()))
            writer.Write("{\"config\":{\"digest\":\"sha256:" + digest + "\"},\"layers\":[]}");
        using var blob = zip.CreateEntry("blobs/sha256-" + digest).Open();
        blob.Write(Encoding.UTF8.GetBytes("wrong"));
    }
    var corruptRoot = Path.Combine(root, "corrupt-model");
    try { await Installer.InstallModelArchive(corruptArchive, corruptRoot); throw new Exception("FAIL: blob inválido aceito"); }
    catch (InvalidDataException) { Check(!File.Exists(Path.Combine(corruptRoot, "manifests", "registry.ollama.ai", "library", "qwen3", "8b")), "Blob inválido bloqueia publicação do manifesto do modelo"); }
    ReleaseChecks.Run(Check);
    // Sem pwsh no PATH (e fora do CI), a checagem de assinatura avisa e pula, sem exceção e sem contar como aprovada.
    var savedPath = Environment.GetEnvironmentVariable("PATH");
    var savedCi = Environment.GetEnvironmentVariable("CI");
    var before = count;
    var skipped = new StringWriter();
    var savedOut = Console.Out;
    try
    {
        Environment.SetEnvironmentVariable("PATH", Path.Combine(root, "sem-pwsh"));
        Environment.SetEnvironmentVariable("CI", null);
        Console.SetOut(skipped);
        CodeSigningChecks.Run(Check);
    }
    finally { Console.SetOut(savedOut); Environment.SetEnvironmentVariable("PATH", savedPath); Environment.SetEnvironmentVariable("CI", savedCi); }
    Check(count == before && skipped.ToString().Contains("PULADO: Assinatura: pwsh (PowerShell 7) não encontrado no PATH")
        && CodeSigningChecks.FindPwsh(null) == null,
        "Assinatura: sem pwsh no PATH, as verificações do script são puladas com aviso e não contam como aprovadas");
    CodeSigningChecks.Run(Check);
    Console.WriteLine($"{count} verificações do instalador aprovadas.");
}
finally { Directory.Delete(root, true); }
