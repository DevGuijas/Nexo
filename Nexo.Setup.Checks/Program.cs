using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Nexo.Setup;

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
    Reject(() => Installer.ValidateManifestUri(new Uri("http://example.com/latest.json")), "Manifesto sem HTTPS rejeitado");
    Reject(() => Installer.ValidateManifestUri(new Uri("https://example.com/other.json")), "Manifesto fora do nome esperado rejeitado");
    var hash = new string('a', 64);
    var valid = new ReleaseManifest(1, "1.6.2", "qwen3:8b", new Package("releases/1.6.2/nexo-app.zip", hash, 1000),
        new Package("releases/1.6.2/qwen3-8b-model.zip", hash, 5_000_000_000), new Package("releases/1.6.2/OllamaSetup.exe", hash, 1000));
    Installer.ValidateManifest(valid);
    Check(true, "Manifesto completo e modelo esperado aceitos");
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
    Console.WriteLine($"{count} verificações do instalador aprovadas.");
}
finally { Directory.Delete(root, true); }
