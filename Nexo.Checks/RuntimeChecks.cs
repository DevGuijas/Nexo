using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexo.Support;
namespace Nexo.Checks;

/// <summary>
/// #150: cartão "Componentes do Windows para programas". Registro, arquivos, download, assinatura e processo são simulados;
/// nenhum teste toca o registro, a rede ou o UAC de verdade.
/// </summary>
internal static class RuntimeChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        Catalog(check);
        Detection(check);
        DirectX(check);
        Informative(check);
        ReadOnly(check);
        await Install(check);
        await Signature(check);
        WindowsFolder(check);
        Guard(check);
        Card(check);
    }

    private const string V14 = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\";

    private class FakeProbe : IRuntimeProbe
    {
        public bool Is64BitWindows { get; set; } = true;
        public Dictionary<string, object> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Names { get; } = [];
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string SystemFolder => @"C:\Windows\System32";
        public string Wow64Folder => @"C:\Windows\SysWOW64";
        public object? Value(bool wow64, string path, string name) => Registry.TryGetValue(wow64 + "|" + path + "|" + name, out var value) ? value : null;
        public IReadOnlyList<string> ValueNames(bool wow64, string path) =>
            Registry.Keys.Where(k => k.StartsWith(wow64 + "|" + path + "|", StringComparison.OrdinalIgnoreCase)).Select(k => k.Split('|')[2]).ToArray();
        public IReadOnlyList<string> UninstallNames() => Names.ToArray();
        public bool FileExists(string path) => Files.Contains(path);
        public void SetV14(string arch, bool wow64 = true, string version = "v14.50.35710.00")
        {
            Registry[wow64 + "|" + V14 + arch + "|Installed"] = 1;
            Registry[wow64 + "|" + V14 + arch + "|Version"] = version;
        }
        public void AllDirectX()
        {
            foreach (var folder in new[] { SystemFolder, Wow64Folder })
                foreach (var file in RuntimeCatalog.DirectXFiles) Files.Add(Path.Combine(folder, file));
        }
    }

    private sealed class FakeSystem : FakeProbe, IRuntimeInstallSystem
    {
        public required string Root { get; init; }
        public Func<Uri, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public (string, string) Sign { get; set; } = ("Valid", RuntimeCatalog.MicrosoftSigner);
        public string SignRoot { get; set; } = RuntimeCatalog.MicrosoftRoot2011;
        public string SignChain { get; set; } = "";
        // Ordem das etapas que importam para a pasta: "DACL", "download", "assinatura", "listar", "runas".
        public List<string> Events { get; } = [];
        // Simulam o que outro processo da mesma conta gravou na pasta antes da DACL: arquivos, subpastas e fluxos alternativos.
        public List<string> PlantFiles { get; } = [];
        public List<string> PlantFolders { get; } = [];
        public List<string> Streams { get; } = [];
        public bool ProtectFails { get; set; }
        // O que havia na pasta quando a DACL foi aplicada: deve ser só o instalador, vazio e já criado.
        public List<(string Name, long Length)> AtDacl { get; } = [];
        public bool HeldWhenListed { get; private set; } = true;
        public Func<string, int?> Exit { get; set; } = _ => 0;
        public Action<string>? AfterRun { get; set; }
        public List<Uri> Requests { get; } = [];
        public List<string> Ran { get; } = [];
        public List<string> Arguments { get; } = [];
        public List<string> Signed { get; } = [];
        public List<string> Dirs { get; } = [];
        public bool ExistedWhenRun { get; private set; } = true;
        public bool WriteBlockedWhenRun { get; private set; } = true;
        public string? Link { get; set; }
        public Task<HttpResponseMessage> Send(Uri url, CancellationToken token)
        {
            Requests.Add(url);
            if (Events.LastOrDefault() != "download") Events.Add("download");
            return Task.FromResult(Respond(url));
        }
        public Task<RuntimeSignature> Signature(string file, CancellationToken token) { Signed.Add(file); Events.Add("assinatura"); return Task.FromResult(new RuntimeSignature(Sign.Item1, Sign.Item2, SignRoot, SignChain)); }
        public void ProtectDirectory(string directory)
        {
            AtDacl.AddRange(Directory.EnumerateFileSystemEntries(directory).Select(e => (Path.GetFileName(e), File.Exists(e) ? new FileInfo(e).Length : -1L)));
            foreach (var name in PlantFiles) File.WriteAllText(Path.Combine(directory, name), "planted");
            foreach (var name in PlantFolders) Directory.CreateDirectory(Path.Combine(directory, name));
            if (ProtectFails) throw new UnauthorizedAccessException("Acesso negado.");
            Events.Add("DACL");
        }
        public IReadOnlyList<string> DirectoryEntries(string directory)
        {
            Events.Add("listar");
            // O instalador segue aberto sem compartilhar escrita enquanto a pasta é listada.
            foreach (var file in Directory.EnumerateFiles(directory))
                try { using var _ = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); if (Path.GetExtension(file) == ".exe") HeldWhenListed = false; }
                catch (IOException) { }
            return [.. Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Select(n => n!), .. Streams];
        }
        public Task<int?> RunElevated(string file, string arguments, CancellationToken token)
        {
            var name = Path.GetFileName(file);
            Events.Add("runas");
            Ran.Add(name); Arguments.Add(arguments);
            ExistedWhenRun &= File.Exists(file);
            // O arquivo segue aberto pelo Nexo sem compartilhar escrita: outro processo não consegue gravar nele.
            try { using var _ = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); WriteBlockedWhenRun = false; }
            catch (IOException) { }
            var exit = Exit(name);
            if (exit != null) AfterRun?.Invoke(name);
            return Task.FromResult(exit);
        }
        public string WorkDirectory() { var dir = Path.Combine(Root, Guid.NewGuid().ToString("N")); Dirs.Add(dir); return dir; }
        public string? Linked(string file) => Link;
    }

    private static HttpResponseMessage Ok(byte[] body, long? length = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentLength = length ?? body.Length;
        return response;
    }
    private static HttpResponseMessage Redirect(string location) { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute); return r; }

    private sealed class Endless(long limit) : Stream
    {
        public long Served { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) { var n = (int)Math.Min(count, limit - Served); Served += n; return n; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static RuntimeItem Item(string id) => RuntimeCatalog.Find(id)!;

    private static void Catalog(Action<bool, string> check)
    {
        var items = RuntimeCatalog.Items;
        var ids = new[] { "vc14-x64", "vc14-x86", "vc2013-x64", "vc2013-x86", "vc2012-x64", "vc2012-x86", "vc2010-x64", "vc2010-x86", "vc2008-x64", "vc2008-x86", "vc2005-x64", "vc2005-x86", "directx" };
        check(items.Select(i => i.Id).SequenceEqual(ids) && items.All(i => RuntimeCatalog.AllowedHost(i.Url)),
            "#150 catálogo: VC++ 2005 a v14 em x86 e x64 e o DirectX, todos em host da Microsoft por HTTPS");
        var pinned = items.Where(i => i.Pinned).ToArray();
        check(pinned.Length == 11 && pinned.All(i => Regex.IsMatch(i.Sha256!, "^[0-9a-f]{64}$") && i.Size > 100_000 && i.MaxSize == i.Size)
            && !Item("vc14-x64").Pinned && !Item("vc14-x86").Pinned && Item("vc14-x64").MaxSize == RuntimeCatalog.LatestMaxSize
            && Item("vc14-x64").Url.ToString() == "https://aka.ms/vc14/vc_redist.x64.exe" && Item("vc14-x86").Url.ToString() == "https://aka.ms/vc14/vc_redist.x86.exe",
            "#150 catálogo: URL, tamanho e SHA-256 fixados nos instaladores imutáveis; o v14 usa o link permanente da Microsoft com teto de tamanho");
        check(Item("directx").Sha256 == "2cf71d098c608c56e07f4655855a886c3102553f648df88458df616b26fd612f" && Item("directx").Size == 295320 && Item("directx").FileName == "dxwebsetup.exe"
            && Item("vc2005-x86").Sha256 == "8648c5fc29c44b9112fe52f9a33f80e7fc42d10f3b5b42b2121542a13e44adfd" && Item("vc2013-x64").Size == 7200744
            && items.All(i => !i.Url.ToString().Contains("DXSDK", StringComparison.OrdinalIgnoreCase)),
            "#150 catálogo: hashes conferidos em 23/11/2026 (docs/runtimes.md) e o DirectX pelo instalador web, sem o DirectX SDK");
        check(Item("vc14-x64").Arguments == "/install /passive /norestart" && Item("vc2013-x86").Arguments == "/install /passive /norestart" && Item("vc2012-x64").Arguments == "/install /passive /norestart"
            && Item("vc2010-x64").Arguments == "/passive /norestart" && Item("vc2008-x86").Arguments == "/q" && Item("vc2005-x64").Arguments == "/q" && Item("directx").Arguments == "/Q",
            "#150 catálogo: argumentos silenciosos documentados de cada instalador");
        check(Item("vc14-x64").DefaultWhenMissing && Item("vc14-x86").DefaultWhenMissing && items.Where(i => i.VcYear is > 0 and < 2015).All(i => !i.DefaultWhenMissing) && !Item("directx").DefaultWhenMissing
            && items.Where(i => i.VcYear is 2012 or 2013).All(i => i.Signer == RuntimeCatalog.MicrosoftSignerMopr) && items.Where(i => i.VcYear is not (2012 or 2013)).All(i => i.Signer == RuntimeCatalog.MicrosoftSigner),
            "#150 catálogo: só o v14 que falta vem marcado; os antigos e o DirectX vêm desmarcados; assinante Microsoft por item");
        check(items.Where(i => i.VcYear is 2012 or 2013).All(i => i.Root == RuntimeCatalog.MicrosoftRoot2001) && items.Where(i => i.VcYear is not (2012 or 2013)).All(i => i.Root == RuntimeCatalog.MicrosoftRoot2011)
            && RuntimeCatalog.MicrosoftRoot2011 == "8F43288AD272F3103B6FB1428485EA3014C0BCFE" && RuntimeCatalog.MicrosoftRoot2001 == "CDD4EEAE6000AC7F40C3802C171E30148030C072",
            "#150 catálogo: raiz esperada por item (Microsoft Root Certificate Authority 2011 no v14, 2005, 2008, 2010 e DirectX; a de 2001 no 2012 e 2013)");
        string[] refusedHosts = ["http://download.microsoft.com/a.exe", "https://download.microsoft.com.evil.example/a.exe", "https://evil.example/a.exe", "https://user@aka.ms/a",
            "https://aka.ms:8443/a", "https://microsoft.com/a.exe", "https://evilaka.ms/a", "https://download.visualstudio.microsoft.com.evil/a", "ftp://download.microsoft.com/a",
            "http://download.microsoft.com:443/a.exe", "https://download.microsoft.com./a.exe"];
        check(refusedHosts.All(u => !RuntimeCatalog.AllowedHost(new Uri(u))) && RuntimeCatalog.AllowedHost(new Uri("https://download.visualstudio.microsoft.com/download/pr/x/VC_redist.x64.exe")),
            "#150 hosts: só HTTPS na porta 443, sem usuário, com host exato da lista da Microsoft");
    }

    private static void Detection(Action<bool, string> check)
    {
        var present = new FakeProbe(); present.SetV14("x64"); present.SetV14("x86");
        var scan = RuntimeInspector.Scan(present);
        var x64 = scan.Items.First(s => s.Item.Id == "vc14-x64"); var x86 = scan.Items.First(s => s.Item.Id == "vc14-x86");
        check(x64.State == RuntimeState.Installed && x86.State == RuntimeState.Installed && x64.Version == "14.50.35710.00", "#150 detecção: v14 com Installed=1 na chave Runtimes aparece instalado, com a versão");
        var view64 = new FakeProbe(); view64.SetV14("x64", wow64: false);
        check(RuntimeInspector.Detect(view64, Item("vc14-x64")).State == RuntimeState.Installed, "#150 detecção: a chave do v14 vale também na visão de 64 bits do registro");
        var absent = RuntimeInspector.Scan(new FakeProbe());
        check(absent.Items.Where(s => s.Item.Kind == RuntimeKind.VisualCpp).All(s => s.State == RuntimeState.Missing) && absent.Missing.Count() == 13,
            "#150 detecção: sem chave nem nome em Aplicativos, todos os VC++ faltam");
        var onlyX86 = new FakeProbe(); onlyX86.SetV14("x86");
        check(RuntimeInspector.Detect(onlyX86, Item("vc14-x86")).State == RuntimeState.Installed && RuntimeInspector.Detect(onlyX86, Item("vc14-x64")).State == RuntimeState.Missing,
            "#150 detecção: só o x86 instalado → o x64 falta");
        var zero = new FakeProbe(); zero.Registry["True|" + V14 + "x64|Installed"] = 0;
        check(RuntimeInspector.Detect(zero, Item("vc14-x64")).State == RuntimeState.Missing, "#150 detecção: Installed=0 não conta como instalado");
        var names = new FakeProbe();
        names.Names.AddRange(["Microsoft Visual C++ 2010  x86 Redistributable - 10.0.40219", "Microsoft Visual C++ 2005 Redistributable", "Microsoft Visual C++ 2008 Redistributable - x64 9.0.30729.6161",
            "Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.40664", "Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211", "Microsoft Visual C++ 2012 Redistributable (ARM64)"]);
        var byName = RuntimeInspector.Scan(names).Items.ToDictionary(s => s.Item.Id, s => s.State);
        check(byName["vc2010-x86"] == RuntimeState.Installed && byName["vc2010-x64"] == RuntimeState.Missing && byName["vc2005-x86"] == RuntimeState.Installed && byName["vc2005-x64"] == RuntimeState.Missing
            && byName["vc2008-x64"] == RuntimeState.Installed && byName["vc2008-x86"] == RuntimeState.Missing && byName["vc2013-x64"] == RuntimeState.Installed && byName["vc14-x86"] == RuntimeState.Installed
            && byName["vc14-x64"] == RuntimeState.Missing && byName["vc2012-x86"] == RuntimeState.Missing && byName["vc2012-x64"] == RuntimeState.Missing,
            "#150 detecção: VC++ antigos e o v14 pelo nome em Aplicativos, por ano e arquitetura (2005 sem arquitetura é o x86; ARM64 não conta)");
        var minimum = new FakeProbe(); minimum.Names.Add("Microsoft Visual C++ 2012 x64 Minimum Runtime - 11.0.61030"); minimum.Names.Add("Microsoft Visual C++ v14 Redistributable (x64) - 14.50.35710");
        minimum.Registry[@"True|SOFTWARE\Microsoft\VisualStudio\12.0\VC\Runtimes\x86|Installed"] = 1;
        check(RuntimeInspector.Detect(minimum, Item("vc2012-x64")).State == RuntimeState.Installed && RuntimeInspector.Detect(minimum, Item("vc14-x64")).State == RuntimeState.Installed
            && RuntimeInspector.Detect(minimum, Item("vc2013-x86")).State == RuntimeState.Installed,
            "#150 detecção: a busca é aproximada para o lado do instalado (Minimum Runtime, nome v14 e a chave Runtimes do 2012/2013), sem falso \"falta\"");
        var x32 = new FakeProbe { Is64BitWindows = false };
        var scan32 = RuntimeInspector.Scan(x32);
        check(scan32.Items.Where(s => s.Item.Arch == RuntimeArch.X64).All(s => s.State == RuntimeState.NotApplicable) && scan32.Missing.All(s => s.Item.Arch != RuntimeArch.X64),
            "#150 detecção: no Windows de 32 bits os itens x64 não se aplicam e não são oferecidos");
        var defaults = absent.Missing.Where(RuntimeCatalog.DefaultSelected).Select(s => s.Item.Id).ToArray();
        check(defaults.SequenceEqual(["vc14-x64", "vc14-x86"]) && !RuntimeCatalog.DefaultSelected(x64),
            "#150 seleção: os itens antigos e o DirectX que faltam vêm desmarcados; só o v14 que falta vem marcado; o instalado não tem caixa marcada");
    }

    private static void DirectX(Action<bool, string> check)
    {
        var full = new FakeProbe(); full.AllDirectX();
        check(RuntimeInspector.Detect(full, Item("directx")).State == RuntimeState.Installed, "#150 DirectX: todas as DLLs de junho de 2010 em System32 e SysWOW64 → instalado");
        var gap = new FakeProbe(); gap.AllDirectX(); gap.Files.Remove(Path.Combine(gap.Wow64Folder, "d3dx9_43.dll")); gap.Files.Remove(Path.Combine(gap.SystemFolder, "xinput1_3.dll"));
        var status = RuntimeInspector.Detect(gap, Item("directx"));
        check(status.State == RuntimeState.Missing && status.Detail.Contains(@"SysWOW64\d3dx9_43.dll") && status.Detail.Contains(@"System32\xinput1_3.dll") && !status.Detail.Contains("d3dx11"),
            "#150 DirectX: DLL faltando em System32 ou SysWOW64 → falta, com os arquivos no detalhe");
        var x32 = new FakeProbe { Is64BitWindows = false };
        foreach (var file in RuntimeCatalog.DirectXFiles) x32.Files.Add(Path.Combine(x32.SystemFolder, file));
        check(RuntimeInspector.Detect(x32, Item("directx")).State == RuntimeState.Installed && RuntimeCatalog.DirectXFiles.Contains("XAudio2_7.dll") && RuntimeCatalog.DirectXFiles.Contains("d3dx10_43.dll"),
            "#150 DirectX: no Windows de 32 bits só System32 conta; D3DX9/10/11, XInput 1.3, XAudio 2.7 e XACT conferidos");
    }

    private static void Informative(Action<bool, string> check)
    {
        var probe = new FakeProbe();
        probe.Registry[@"False|SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full|Release"] = 533320;
        probe.Registry[@"True|SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App|8.0.11"] = 1;
        probe.Registry[@"True|SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App|6.0.36"] = 1;
        var scan = RuntimeInspector.Scan(probe);
        var old = new FakeProbe(); old.Registry[@"False|SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full|Release"] = 528040;
        var win10 = new FakeProbe(); win10.Registry[@"False|SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full|Release"] = 528372;
        check(scan.DotNetFramework.StartsWith("4.8.1") && RuntimeInspector.DotNetFramework(old).StartsWith("4.8 ") && RuntimeInspector.DotNetFramework(win10).StartsWith("4.8 (") && RuntimeInspector.DotNetFramework(new FakeProbe()) == "não encontrado"
            && scan.DesktopRuntimes.SequenceEqual(["6.0.36 (x64)", "8.0.11 (x64)"]) && scan.Items.All(s => s.Item.Id != "netfx"),
            "#150 .NET: Framework pelo Release de NDP\\v4\\Full e Desktop Runtime pelas chaves do instalador, só informativos (não entram na instalação)");
        var text = RuntimePresentation.ScanText(scan);
        check(text.Contains("4.8.1") && text.Contains("8.0.11") && text.Contains("informativo"), "#150 .NET: a verificação mostra as versões como informação");
    }

    // Verificar não altera nada: a interface da leitura só tem leituras, e o leitor do Windows abre o registro só para leitura.
    private static void ReadOnly(Action<bool, string> check)
    {
        var methods = typeof(IRuntimeProbe).GetMethods().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ThemeSources", "Code", "RuntimeSetup.cs"));
        var reader = Regex.Match(source, @"public sealed class WindowsRuntimeSystem.*?public Task<HttpResponseMessage> Send", RegexOptions.Singleline).Value;
        check(methods.SequenceEqual(["FileExists", "UninstallNames", "Value", "ValueNames", "get_Is64BitWindows", "get_SystemFolder", "get_Wow64Folder"])
            && reader.Length > 0 && Regex.Matches(reader, @"OpenSubKey\([^)]*, false\)").Count == 4 && Regex.IsMatch(reader, @"UninstallNames\(\)\s*\{[^}]*new\[\] \{ false, true \}") && !Regex.IsMatch(source, @"CreateSubKey|SetValue|DeleteValue|DeleteSubKey|File\.(?:Write|Delete|Move|Copy)[^;]*SystemFolder"),
            "#150 verificação só de leitura: a interface tem só leituras e o registro é aberto sem escrita");
        var probe = new FakeProbe(); probe.SetV14("x64"); probe.AllDirectX(); probe.Names.Add("Microsoft Visual C++ 2010  x86 Redistributable - 10.0.40219");
        var before = JsonSerializer.Serialize(new { probe.Registry, probe.Names, probe.Files });
        RuntimeInspector.Scan(probe); RuntimeInspector.Scan(probe);
        check(JsonSerializer.Serialize(new { probe.Registry, probe.Names, probe.Files }) == before, "#150 verificação só de leitura: registro e arquivos iguais antes e depois de verificar");
    }

    private static async Task Install(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "NexoRuntimes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        bool Clean() => !Directory.EnumerateFileSystemEntries(temp).Any();
        var dx = Item("directx"); var v14 = Item("vc14-x64"); var v14x86 = Item("vc14-x86"); var vc2013 = Item("vc2013-x64");
        var dxBody = new byte[dx.Size!.Value]; new Random(150).NextBytes(dxBody);
        var dxHash = Convert.ToHexString(SHA256.HashData(dxBody)).ToLowerInvariant();
        var v14Body = new byte[2_000_000]; new Random(14).NextBytes(v14Body);
        var audits = new List<RuntimeAudit>();
        Func<RuntimeItem, string?> fake = item => item.Id == "directx" ? dxHash : item.Sha256;

        // Hash fixado diferente: o SHA-256 do catálogo não é o dos bytes simulados.
        var wrongHash = new FakeSystem { Root = temp, Respond = _ => Ok(dxBody) };
        var results = await RuntimeInstaller.InstallAsync(wrongHash, [dx], null, audits.Add, CancellationToken.None);
        check(dxHash != dx.Sha256 && results[0].Outcome == RuntimeOutcome.Integrity && results[0].Message.Contains("SHA-256") && wrongHash.Signed.Count == 0 && wrongHash.Ran.Count == 0 && Clean()
            && audits.Count == 1 && audits[0].Outcome == "Integrity" && audits[0].Sha256 == dxHash,
            "#150 instalação: SHA-256 diferente do fixado para antes da assinatura e do UAC, apaga o arquivo e registra na auditoria");
        var shorter = new FakeSystem { Root = temp, Respond = _ => Ok(dxBody[..^1]) };
        results = await RuntimeInstaller.InstallAsync(shorter, [dx], null, null, CancellationToken.None);
        check(results[0].Outcome == RuntimeOutcome.Integrity && shorter.Ran.Count == 0 && Clean(), "#150 instalação: tamanho informado diferente do fixado para sem executar");
        var endless = new Endless(RuntimeCatalog.LatestMaxSize * 2);
        var flood = new FakeSystem { Root = temp, Respond = _ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(endless) }; r.Content.Headers.ContentLength = null; return r; } };
        results = await RuntimeInstaller.InstallAsync(flood, [v14], null, null, CancellationToken.None);
        check(results[0].Outcome == RuntimeOutcome.Integrity && endless.Served <= RuntimeCatalog.LatestMaxSize + 81920 && flood.Ran.Count == 0 && Clean(),
            "#150 instalação: o v14 sem tamanho fixado para no teto durante o download (" + endless.Served + " bytes lidos)");
        var big = new FakeSystem { Root = temp, Respond = _ => Ok([1, 2], RuntimeCatalog.LatestMaxSize + 1) };
        results = await RuntimeInstaller.InstallAsync(big, [v14], null, null, CancellationToken.None);
        check(results[0].Outcome == RuntimeOutcome.Integrity && big.Ran.Count == 0, "#150 instalação: Content-Length acima do teto é recusado antes de gravar");

        // Host fora da lista, em qualquer salto.
        foreach (var target in new[] { "https://evil.example/vc_redist.x64.exe", "http://download.visualstudio.microsoft.com/a.exe", "https://download.microsoft.com.evil.example/a", "https://aka.ms:444/a", "https://u@download.microsoft.com/a" })
        {
            var hop = new FakeSystem { Root = temp, Respond = url => url == v14.Url ? Redirect(target) : Ok(v14Body) };
            results = await RuntimeInstaller.InstallAsync(hop, [v14], null, null, CancellationToken.None);
            check(results[0].Outcome == RuntimeOutcome.Host && hop.Requests.Count == 1 && hop.Ran.Count == 0 && hop.Signed.Count == 0, "#150 instalação: redirecionamento recusado antes de pedir " + target);
        }
        var loop = new FakeSystem { Root = temp, Respond = _ => Redirect("https://aka.ms/loop") };
        results = await RuntimeInstaller.InstallAsync(loop, [v14], null, null, CancellationToken.None);
        check(results[0].Outcome == RuntimeOutcome.Network && loop.Requests.Count == RuntimeCatalog.MaxRedirects + 1 && loop.Ran.Count == 0, "#150 instalação: redirecionamentos em laço param no limite");

        // Assinatura: assinante diferente da Microsoft, estado inválido, MOPR fora do 2012/2013.
        HttpResponseMessage Chain(Uri url) => url.Host == "aka.ms" ? Redirect("https://download.visualstudio.microsoft.com/download/pr/x/VC_redist.x64.exe") : Ok(v14Body);
        var contoso = new FakeSystem { Root = temp, Respond = Chain, Sign = ("Valid", "CN=Contoso Ltd, O=Contoso Ltd, L=Redmond, S=Washington, C=US") };
        results = await RuntimeInstaller.InstallAsync(contoso, [v14], null, null, CancellationToken.None);
        check(results[0].Outcome == RuntimeOutcome.Signature && results[0].Message.Contains("Contoso") && contoso.Signed.Count == 1 && contoso.Ran.Count == 0 && Clean(),
            "#150 instalação: assinatura Valid de outro assinante é recusada antes do UAC e o arquivo é apagado");
        var lookalike = new FakeSystem { Root = temp, Respond = Chain, Sign = ("Valid", "CN=Microsoft Corporation, O=Microsoft Corporation Fake, L=Redmond, S=Washington, C=US") };
        results = await RuntimeInstaller.InstallAsync(lookalike, [v14], null, null, CancellationToken.None);
        var mopr = new FakeSystem { Root = temp, Respond = Chain, Sign = ("Valid", RuntimeCatalog.MicrosoftSignerMopr) };
        var moprResult = await RuntimeInstaller.InstallAsync(mopr, [v14], null, null, CancellationToken.None);
        var unsigned = new FakeSystem { Root = temp, Respond = Chain, Sign = ("NotSigned", "") };
        var unsignedResult = await RuntimeInstaller.InstallAsync(unsigned, [v14], null, null, CancellationToken.None);
        var hashMismatch = new FakeSystem { Root = temp, Respond = Chain, Sign = ("HashMismatch", RuntimeCatalog.MicrosoftSigner) };
        var mismatchResult = await RuntimeInstaller.InstallAsync(hashMismatch, [v14], null, null, CancellationToken.None);
        check(results[0].Outcome == RuntimeOutcome.Signature && moprResult[0].Outcome == RuntimeOutcome.Signature && unsignedResult[0].Outcome == RuntimeOutcome.Signature
            && mismatchResult[0].Outcome == RuntimeOutcome.Signature && mismatchResult[0].Message.Contains("HashMismatch")
            && lookalike.Ran.Count + mopr.Ran.Count + unsigned.Ran.Count + hashMismatch.Ran.Count == 0 && Clean(),
            "#150 instalação: assinante parecido, assinatura sem estado Valid e o certificado MOPR no v14 são recusados");
        var junction = new FakeSystem { Root = temp, Respond = Chain, Link = @"C:\Users\u\AppData\Local\Temp\Nexo" };
        results = await RuntimeInstaller.InstallAsync(junction, [v14], null, null, CancellationToken.None);
        check(results[0].Outcome == RuntimeOutcome.Link && junction.Signed.Count == 0 && junction.Ran.Count == 0 && Clean(), "#150 instalação: link ou junção no caminho para antes da assinatura e do UAC");

        // Sucesso, reinício pendente, versão mais nova, falha com código e sem confirmação depois do instalador.
        var ok = new FakeSystem { Root = temp, Respond = Chain, AfterRun = _ => { } };
        ok.AfterRun = _ => ok.SetV14("x64");
        var progress = new List<string>();
        results = await RuntimeInstaller.InstallAsync(ok, [v14], new SyncProgress(progress.Add), null, CancellationToken.None);
        check(results[0].Outcome == RuntimeOutcome.Ok && results[0].Signer == RuntimeCatalog.MicrosoftSigner && results[0].Message.Contains("14.50") && ok.Arguments.SequenceEqual(["/install /passive /norestart"])
            && ok.ExistedWhenRun && (!OperatingSystem.IsWindows() || ok.WriteBlockedWhenRun) && ok.Requests.Select(u => u.Host).SequenceEqual(["aka.ms", "download.visualstudio.microsoft.com"]) && Clean()
            && progress.Any(p => p.Contains("UAC")),
            "#150 instalação: v14 pelo link permanente, assinatura Microsoft, UAC com /install /passive /norestart, arquivo sem escrita de terceiros e conferido depois");
        var reboot = new FakeSystem { Root = temp, Respond = Chain, Exit = _ => 3010 };
        var newer = new FakeSystem { Root = temp, Respond = Chain, Exit = _ => 1638 };
        newer.AfterRun = _ => newer.SetV14("x64");
        var newerAbsent = new FakeSystem { Root = temp, Respond = Chain, Exit = _ => 1638 };
        var failed = new FakeSystem { Root = temp, Respond = Chain, Exit = _ => 1603 };
        var silent = new FakeSystem { Root = temp, Respond = Chain, Exit = _ => 0 };
        var started = new FakeSystem { Root = temp, Respond = Chain, Exit = _ => 1641 };
        var errorButPresent = new FakeSystem { Root = temp, Respond = Chain, Exit = _ => 1603 };
        errorButPresent.AfterRun = _ => errorButPresent.SetV14("x64");
        var rebootResult = (await RuntimeInstaller.InstallAsync(reboot, [v14], null, null, CancellationToken.None))[0];
        var newerResult = (await RuntimeInstaller.InstallAsync(newer, [v14], null, null, CancellationToken.None))[0];
        var newerAbsentResult = (await RuntimeInstaller.InstallAsync(newerAbsent, [v14], null, null, CancellationToken.None))[0];
        var failedResult = (await RuntimeInstaller.InstallAsync(failed, [v14], null, null, CancellationToken.None))[0];
        var silentResult = (await RuntimeInstaller.InstallAsync(silent, [v14], null, null, CancellationToken.None))[0];
        var startedResult = (await RuntimeInstaller.InstallAsync(started, [v14], null, null, CancellationToken.None))[0];
        var presentResult = (await RuntimeInstaller.InstallAsync(errorButPresent, [v14], null, null, CancellationToken.None))[0];
        check(rebootResult.Outcome == RuntimeOutcome.RebootPending && rebootResult.ExitCode == 3010 && rebootResult.Message.Contains("não reinicia")
            && RuntimePresentation.Result([rebootResult]).Contains("Reinicie o PC quando puder") && startedResult.Outcome == RuntimeOutcome.RebootPending && startedResult.ExitCode == 1641,
            "#150 instalação: códigos 3010 e 1641 → reinício pendente, sem reinício automático");
        // @qa achado baixo 6: no 1641 o instalador já iniciou o reinício; o texto não manda "reiniciar quando puder".
        var startedText = RuntimePresentation.Result([startedResult]);
        check(startedResult.Message.Contains("já iniciou o reinício do Windows") && !startedResult.Message.Contains("quando puder")
            && startedText.Contains("já iniciou o reinício do Windows") && !startedText.Contains("Reinicie o PC quando puder")
            && RuntimePresentation.Result([rebootResult, startedResult]).Contains("Reinicie o PC quando puder"),
            "#150 instalação: 1641 diz que o instalador já iniciou o reinício do Windows; 3010 segue com \"reinicie quando puder\"");
        check(newerResult.Outcome == RuntimeOutcome.AlreadyInstalled && newerResult.ExitCode == 1638 && newerResult.Label == "Já instalado", "#150 instalação: código 1638 com o componente conferido depois → já instalado");
        check(newerAbsentResult.Outcome == RuntimeOutcome.Failed && newerAbsentResult.ExitCode == 1638 && newerAbsentResult.Label == "Falha" && newerAbsentResult.Message.StartsWith("Falha (1638)")
            && newerAbsent.Ran.Count == 1 && Clean(),
            "#150 instalação: código 1638 sem o componente na verificação depois → Falha (1638), não \"Já instalado\"");
        check(failedResult.Outcome == RuntimeOutcome.Failed && failedResult.ExitCode == 1603 && failedResult.Message.Contains("1603")
            && silentResult.Outcome == RuntimeOutcome.Failed && silentResult.Message.Contains("não aparece como instalado")
            && presentResult.Outcome == RuntimeOutcome.Failed && presentResult.ExitCode == 1603 && presentResult.Message.Contains("mas o componente aparece como instalado"),
            "#150 instalação: código de erro mostra o código, mesmo com o componente presente depois; saída 0 sem o componente na verificação não é dada como instalada");

        // DirectX fixado (hash dos bytes simulados pela sobrecarga interna), com /Q e as DLLs conferidas depois.
        var dxOk = new FakeSystem { Root = temp, Respond = _ => Ok(dxBody) };
        dxOk.AfterRun = _ => dxOk.AllDirectX();
        results = await RuntimeInstaller.InstallAsync(dxOk, [dx], null, null, CancellationToken.None, fake);
        check(results[0].Outcome == RuntimeOutcome.Ok && dxOk.Arguments.SequenceEqual(["/Q"]) && dxOk.Ran.SequenceEqual(["dxwebsetup.exe"]) && results[0].Sha256 == dxHash && Clean(),
            "#150 instalação: DirectX pelo dxwebsetup.exe fixado, com /Q, e as DLLs conferidas depois");
        var vc2013Body = new byte[vc2013.Size!.Value]; new Random(2013).NextBytes(vc2013Body);
        var vc2013Hash = Convert.ToHexString(SHA256.HashData(vc2013Body)).ToLowerInvariant();
        var old = new FakeSystem { Root = temp, Respond = _ => Ok(vc2013Body), Sign = ("Valid", RuntimeCatalog.MicrosoftSignerMopr), SignRoot = RuntimeCatalog.MicrosoftRoot2001 };
        old.AfterRun = _ => old.Names.Add("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.40664");
        results = await RuntimeInstaller.InstallAsync(old, [vc2013], null, null, CancellationToken.None, item => item.Id == "vc2013-x64" ? vc2013Hash : item.Sha256);
        check(results[0].Outcome == RuntimeOutcome.Ok && old.Requests.Count == 1 && old.Requests[0].Host == "download.visualstudio.microsoft.com",
            "#150 instalação: VC++ 2013 fixado pela URL direta, com o assinante MOPR da Microsoft e a raiz de 2001");
        var oldOtherRoot = new FakeSystem { Root = temp, Respond = _ => Ok(vc2013Body), Sign = ("Valid", RuntimeCatalog.MicrosoftSignerMopr) };
        results = await RuntimeInstaller.InstallAsync(oldOtherRoot, [vc2013], null, null, CancellationToken.None, item => item.Id == "vc2013-x64" ? vc2013Hash : item.Sha256);
        check(results[0].Outcome == RuntimeOutcome.Untrusted && oldOtherRoot.Ran.Count == 0 && Clean(),
            "#150 raiz: cada item tem a sua; o 2013 com a raiz de 2011 é recusado");

        await Folder(check, temp, Chain, v14, Clean);
        await Roots(check, temp, Chain, v14, Clean);

        // UAC cancelado: estado claro e os itens seguintes não começam.
        var cancel = new FakeSystem { Root = temp, Respond = url => url.Host == "aka.ms" ? Redirect("https://download.visualstudio.microsoft.com/x/" + url.Segments[^1]) : Ok(v14Body), Exit = name => name == "vc_redist.x64.exe" ? null : 0 };
        audits.Clear();
        results = await RuntimeInstaller.InstallAsync(cancel, [v14, v14x86, dx], null, audits.Add, CancellationToken.None);
        check(results.Select(r => r.Outcome).SequenceEqual([RuntimeOutcome.UacCanceled, RuntimeOutcome.NotStarted, RuntimeOutcome.NotStarted]) && results[0].ExitCode == null
            && results[0].Message.Contains("autorização de administrador foi cancelada") && results[1].Message.Contains("cancelada") && cancel.Requests.Count == 2 && cancel.Ran.Count == 1
            && RuntimeInstaller.State(results) == "cancelada" && audits.Select(a => a.Outcome).SequenceEqual(["UacCanceled", "NotStarted", "NotStarted"]) && Clean(),
            "#150 instalação: UAC cancelado vira estado claro, os itens seguintes ficam não iniciados e tudo vai à auditoria");

        // Um item por vez, só os marcados; o que já está instalado não baixa nada; resultado parcial.
        var mixed = new FakeSystem { Root = temp, Respond = url => url.Host == "aka.ms" ? Redirect("https://download.visualstudio.microsoft.com/x/" + url.Segments[^1]) : Ok(v14Body), Exit = name => name == "vc_redist.x86.exe" ? 1603 : 0 };
        mixed.AfterRun = name => { if (name == "vc_redist.x64.exe") mixed.SetV14("x64"); };
        mixed.Names.Add("Microsoft Visual C++ 2010  x86 Redistributable - 10.0.40219");
        audits.Clear();
        results = await RuntimeInstaller.InstallAsync(mixed, [v14, Item("vc2010-x86"), v14x86], null, audits.Add, CancellationToken.None);
        check(results.Select(r => r.Outcome).SequenceEqual([RuntimeOutcome.Ok, RuntimeOutcome.AlreadyInstalled, RuntimeOutcome.Failed]) && mixed.Ran.SequenceEqual(["vc_redist.x64.exe", "vc_redist.x86.exe"])
            && mixed.Requests.All(u => !u.ToString().Contains("165255E7")) && RuntimeInstaller.State(results) == "parcial" && audits.Count == 3 && audits[0].FinalUrl.StartsWith("https://download.visualstudio.microsoft.com/")
            && audits[0].Signer == RuntimeCatalog.MicrosoftSigner && !audits[0].Pinned && audits[1].Bytes == 0 && RuntimeInstaller.Summary(results) == "1 ok, 1 já instalado, 1 falha",
            "#150 instalação: um item de cada vez, só os escolhidos; já instalado não baixa; resultado parcial com auditoria por item");
        var offline = new FakeSystem { Root = temp, Respond = _ => throw new HttpRequestException("Nenhuma conexão") };
        results = await RuntimeInstaller.InstallAsync(offline, [v14], null, null, CancellationToken.None);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var stopped = new FakeSystem { Root = temp, Respond = Chain };
        var stoppedResult = await RuntimeInstaller.InstallAsync(stopped, [v14, v14x86], null, null, canceled.Token);
        check(results[0].Outcome == RuntimeOutcome.Network && results[0].Message.Contains("Sem conexão") && stoppedResult.All(r => r.Outcome == RuntimeOutcome.NotStarted) && stopped.Requests.Count == 0,
            "#150 instalação: sem rede e cancelamento antes do início viram estado claro, sem executar");
        var question = RuntimePresentation.ConfirmQuestion([v14, dx]);
        check(question.Contains("• " + v14.Title) && question.Contains("• " + dx.Title) && question.Contains("aka.ms") && question.Contains("SHA-256 fixado") && question.Contains("UAC") && question.Contains("não reinicia"),
            "#150 confirmação: lista item a item o que vai ser instalado, a origem, a assinatura e o UAC");
        try { Directory.Delete(temp, true); } catch (IOException) { }
    }

    // @qa risco médio 1: DLL plantada ao lado do instalador antes do UAC.
    private static async Task Folder(Action<bool, string> check, string temp, Func<Uri, HttpResponseMessage> chain, RuntimeItem v14, Func<bool> clean)
    {
        var ok = new FakeSystem { Root = temp, Respond = chain };
        ok.AfterRun = _ => ok.SetV14("x64");
        var results = await RuntimeInstaller.InstallAsync(ok, [v14], null, null, CancellationToken.None);
        check(results[0].Outcome == RuntimeOutcome.Ok && ok.Events.SequenceEqual(["DACL", "download", "assinatura", "listar", "runas"]) && ok.AtDacl.SequenceEqual([(v14.FileName, 0L)])
            && results[0].Sha256.Length == 64 && (!OperatingSystem.IsWindows() || ok.HeldWhenListed) && clean(),
            "#150 pasta: instalador criado vazio → DACL protegida → download → hash → assinatura → listar com o instalador aberto → runas, e a pasta é apagada no fim");
        // O hash vem depois do download e antes da assinatura: um SHA-256 fixado diferente para sem chamar a assinatura.
        var wrongHash = new FakeSystem { Root = temp, Respond = chain };
        results = await RuntimeInstaller.InstallAsync(wrongHash, [v14], null, null, CancellationToken.None, _ => new string('0', 64));
        check(results[0].Outcome == RuntimeOutcome.Integrity && results[0].Sha256.Length == 64 && wrongHash.Events.SequenceEqual(["DACL", "download"]) && wrongHash.Signed.Count == 0 && wrongHash.Ran.Count == 0 && clean(),
            "#150 pasta: com a DACL já aplicada, o hash do arquivo baixado é conferido antes da assinatura e do UAC");
        foreach (var (label, plant) in new (string, Action<FakeSystem>)[]
        {
            ("DLL ao lado do instalador", f => f.PlantFiles.Add("version.dll")),
            ("arquivo oculto qualquer", f => f.PlantFiles.Add(".a")),
            ("subpasta", f => f.PlantFolders.Add("sub")),
            ("fluxo alternativo no instalador", f => f.Streams.Add("vc_redist.x64.exe:payload:$DATA")),
            ("fluxo alternativo na pasta", f => f.Streams.Add(":payload:$DATA")),
        })
        {
            var planted = new FakeSystem { Root = temp, Respond = chain };
            plant(planted);
            var audits = new List<RuntimeAudit>();
            results = await RuntimeInstaller.InstallAsync(planted, [v14], null, audits.Add, CancellationToken.None);
            var dir = planted.Dirs.Single();
            var leftover = planted.PlantFiles.Count + planted.PlantFolders.Count > 0;
            check(results[0].Outcome == RuntimeOutcome.FolderChanged && results[0].Label == "Pasta do instalador alterada" && results[0].Message.StartsWith("Pasta do instalador alterada")
                && planted.Ran.Count == 0 && planted.Events.SequenceEqual(["DACL", "download", "assinatura", "listar"]) && !File.Exists(Path.Combine(dir, v14.FileName))
                && (leftover ? Directory.Exists(dir) && audits.Single().Leftover.StartsWith(dir) && !results[0].Message.Contains(dir) : !Directory.Exists(dir) && audits.Single().Leftover == ""),
                "#150 pasta: " + label + " → \"pasta do instalador alterada\" antes do UAC; o instalador é apagado e a pasta que sobra vai só à auditoria");
            try { Directory.Delete(dir, true); } catch (DirectoryNotFoundException) { }
        }
        var refused = new FakeSystem { Root = temp, Respond = chain, ProtectFails = true };
        results = await RuntimeInstaller.InstallAsync(refused, [v14], null, null, CancellationToken.None);
        check(results[0].Outcome == RuntimeOutcome.Failed && results[0].Message.Contains("proteger a pasta") && results[0].Message.Contains("Nada foi baixado")
            && refused.Requests.Count == 0 && refused.Signed.Count == 0 && refused.Ran.Count == 0 && clean(),
            "#150 pasta: se a DACL não puder ser aplicada, nada é baixado nem executado");
    }

    // @qa risco médio 2: o v14 sem hash dependia só do Subject; agora a raiz da cadeia também precisa ser a esperada.
    private static async Task Roots(Action<bool, string> check, string temp, Func<Uri, HttpResponseMessage> chain, RuntimeItem v14, Func<bool> clean)
    {
        foreach (var (label, root) in new[] { ("raiz de um proxy TLS", "0123456789ABCDEF0123456789ABCDEF01234567"), ("raiz vazia", ""), ("raiz de 2001 no v14", RuntimeCatalog.MicrosoftRoot2001),
            ("raiz com um caractere a mais", RuntimeCatalog.MicrosoftRoot2011 + "0"), ("raiz parcial", RuntimeCatalog.MicrosoftRoot2011[..39]) })
        {
            var rogue = new FakeSystem { Root = temp, Respond = chain, SignRoot = root };
            var result = (await RuntimeInstaller.InstallAsync(rogue, [v14], null, null, CancellationToken.None))[0];
            check(result.Outcome == RuntimeOutcome.Untrusted && result.Label == "Assinatura não confiável" && result.Message.StartsWith("Assinatura não confiável")
                && result.Signer == RuntimeCatalog.MicrosoftSigner && rogue.Ran.Count == 0 && !rogue.Events.Contains("listar") && clean(),
                "#150 raiz: Subject da Microsoft com " + label + " → assinatura não confiável, antes do UAC");
        }
        foreach (var (label, status) in new[] { ("PartialChain", "PartialChain"), ("NotSignatureValid", "NotSignatureValid"), ("Build falso", "a cadeia não foi montada") })
        {
            var broken = new FakeSystem { Root = temp, Respond = chain, SignChain = status };
            var result = (await RuntimeInstaller.InstallAsync(broken, [v14], null, null, CancellationToken.None))[0];
            check(result.Outcome == RuntimeOutcome.Untrusted && result.Message.Contains("cadeia do certificado não foi validada (" + status + ")")
                && broken.Ran.Count == 0 && !broken.Events.Contains("listar") && clean(),
                "#150 raiz: cadeia com " + label + " → assinatura não confiável, mesmo com a raiz da Microsoft, antes do UAC");
        }
        var lower = new FakeSystem { Root = temp, Respond = chain, SignRoot = RuntimeCatalog.MicrosoftRoot2011.ToLowerInvariant() };
        lower.AfterRun = _ => lower.SetV14("x64");
        var ok = (await RuntimeInstaller.InstallAsync(lower, [v14], null, null, CancellationToken.None))[0];
        check(ok.Outcome == RuntimeOutcome.Ok, "#150 raiz: comparação exata sem diferenciar maiúsculas de minúsculas");
    }

    // A saída simulada do PowerShell e o próprio script, com Get-AuthenticodeSignature falso, no pwsh (Windows ou NEXO_CHECKS_PWSH).
    private static async Task Signature(Action<bool, string> check)
    {
        RuntimeSignature Out(string built, string chain) => WindowsRuntimeSystem.ParseSignature("{\"Status\":\"Valid\",\"Subject\":\"" + RuntimeCatalog.MicrosoftSigner
            + "\",\"Root\":\"8F43288AD272F3103B6FB1428485EA3014C0BCFE\"" + (built.Length > 0 ? ",\"Built\":" + built : "") + (chain.Length > 0 ? ",\"Chain\":" + chain : "") + "}");
        var valid = Out("true", "[]");
        var noRoot = WindowsRuntimeSystem.ParseSignature("{\"Status\":\"Valid\",\"Subject\":\"" + RuntimeCatalog.MicrosoftSigner + "\",\"Built\":true,\"Chain\":[]}");
        var nullRoot = WindowsRuntimeSystem.ParseSignature("{\"Status\":\"NotSigned\",\"Subject\":null,\"Root\":null,\"Built\":false,\"Chain\":[]}");
        check(valid == new RuntimeSignature("Valid", RuntimeCatalog.MicrosoftSigner, RuntimeCatalog.MicrosoftRoot2011, "") && noRoot.Root == "" && noRoot.Chain == ""
            && nullRoot == new RuntimeSignature("NotSigned", "", "", "a cadeia não foi montada") && WindowsRuntimeSystem.ParseSignature("aviso\n").Root == "" && WindowsRuntimeSystem.ParseSignature("aviso\n").Chain.Length > 0,
            "#150 assinatura: a saída do PowerShell traz estado, Subject, a raiz, o Build e o ChainStatus; sem raiz, ela fica vazia e o item é recusado");
        // A raiz de 2001 e as folhas do 2012 e do 2013 estão vencidas: com IgnoreNotTimeValid o Build é verdadeiro e o prazo segue listado.
        foreach (var (label, chain) in new[] { ("prazo vencido", "[\"NotTimeValid\"]"), ("revogação desconhecida", "[\"RevocationStatusUnknown\",\"OfflineRevocation\"]"),
            ("prazo e revogação", "[\"NotTimeValid\",\"RevocationStatusUnknown\"]") })
        {
            var accepted = Out("true", chain);
            check(accepted.Root == RuntimeCatalog.MicrosoftRoot2011 && accepted.Chain == "", "#150 cadeia: Build verdadeiro com " + label + " é aceito, e a raiz segue para a comparação");
        }
        foreach (var (label, built, chain, reason) in new[]
        {
            ("PartialChain", "true", "[\"PartialChain\"]", "PartialChain"), ("NotSignatureValid", "true", "[\"NotSignatureValid\"]", "NotSignatureValid"),
            ("PartialChain com o prazo vencido", "true", "[\"NotTimeValid\",\"PartialChain\"]", "PartialChain"), ("UntrustedRoot", "true", "[\"UntrustedRoot\"]", "UntrustedRoot"),
            ("flags combinadas num status", "true", "[\"NotTimeValid, PartialChain\"]", "NotTimeValid, PartialChain"),
            ("Build falso sem status", "false", "[]", "a cadeia não foi montada"), ("Build falso com PartialChain", "false", "[\"PartialChain\"]", "a cadeia não foi montada: PartialChain"),
            ("Build ausente", "", "[]", "a cadeia não foi montada"), ("Build como texto", "\"true\"", "[]", "a cadeia não foi montada"),
            ("ChainStatus ausente", "true", "", "estado da cadeia não informado"), ("ChainStatus não é lista", "true", "\"PartialChain\"", "estado da cadeia não informado"),
        })
        {
            var refusedChain = Out(built, chain);
            check(refusedChain.Root == "" && refusedChain.Chain == reason, "#150 cadeia: " + label + " é recusado, com a raiz vazia (" + reason + ")");
        }
        var script = WindowsRuntimeSystem.SignatureScript(@"C:\Temp\x'y.exe");
        var wrapped = SupportService.PowershellCommand(script);
        check(script.Contains("Get-AuthenticodeSignature -LiteralPath 'C:\\Temp\\x''y.exe'") && script.Contains("X509Chain") && script.Contains("X509RevocationMode]::NoCheck")
            && script.Contains("$built=[bool]$c.Build($s.SignerCertificate)") && !script.Contains("[void]$c.Build") && script.Contains("X509VerificationFlags]::IgnoreNotTimeValid")
            && script.Contains("$chain=@($c.ChainStatus") && script.Contains("Built=$built;Chain=$chain")
            && script.Contains("ChainElements[$c.ChainElements.Count-1].Certificate.Thumbprint") && wrapped.StartsWith(PowerShellModulePath.Guard),
            "#150 assinatura: a cadeia sai da mesma chamada do Get-AuthenticodeSignature, com o Build e o ChainStatus na saída, ignorando só o prazo, sem revogação pela rede e com o guard da #131");
        var shell = OperatingSystem.IsWindows() ? "powershell.exe" : Environment.GetEnvironmentVariable("NEXO_CHECKS_PWSH");
        if (string.IsNullOrWhiteSpace(shell)) return;
        const string fake = "$k=[System.Security.Cryptography.RSA]::Create(2048); $r=New-Object System.Security.Cryptography.X509Certificates.CertificateRequest('CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US',$k,[System.Security.Cryptography.HashAlgorithmName]::SHA256,[System.Security.Cryptography.RSASignaturePadding]::Pkcs1)"
            + "; $cert=$r.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1),[DateTimeOffset]::Now.AddDays(1)); [Console]::Error.WriteLine('THUMB='+$cert.Thumbprint)"
            + "; function global:Get-AuthenticodeSignature { param([string]$LiteralPath) if($LiteralPath -like '*unsigned*'){[pscustomobject]@{Status='NotSigned';SignerCertificate=$null}}else{[pscustomobject]@{Status='Valid';SignerCertificate=$cert}} }; ";
        async Task<(string Out, string Err)> Run(string file)
        {
            var start = new System.Diagnostics.ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(SupportService.PowershellCommand(fake + WindowsRuntimeSystem.SignatureScript(file)))) }) start.ArgumentList.Add(a);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (await output, await error);
        }
        var self = await Run(Path.Combine(Path.GetTempPath(), "nexo-sem-arquivo.exe"));
        var parsed = WindowsRuntimeSystem.ParseSignature(self.Out);
        var thumb = Regex.Match(self.Err, "THUMB=([0-9A-F]{40})").Groups[1].Value;
        string rawRoot = "", rawBuilt = "";
        string[] rawChain = [];
        try
        {
            using var raw = JsonDocument.Parse(self.Out);
            rawRoot = raw.RootElement.GetProperty("Root").GetString() ?? "";
            rawBuilt = raw.RootElement.GetProperty("Built").ValueKind.ToString();
            rawChain = raw.RootElement.GetProperty("Chain").EnumerateArray().Select(s => s.GetString() ?? "").ToArray();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        // Raiz própria fora do repositório: o Build é falso (UntrustedRoot). A raiz informada é a dele, e o ParseSignature a descarta.
        check(parsed.Status == "Valid" && parsed.Subject == RuntimeCatalog.MicrosoftSigner && thumb.Length == 40 && rawRoot == thumb && rawBuilt == "False" && rawChain.Contains("UntrustedRoot")
            && parsed.Root == "" && parsed.Chain.StartsWith("a cadeia não foi montada", StringComparison.Ordinal),
            "#150 assinatura (pwsh): certificado com o Subject da Microsoft e raiz própria → Build falso com UntrustedRoot, raiz dele (" + (rawRoot.Length > 0 ? rawRoot[..8] : "vazia") + "...) descartada");
        var unsigned = WindowsRuntimeSystem.ParseSignature((await Run(Path.Combine(Path.GetTempPath(), "unsigned.exe"))).Out);
        check(unsigned == new RuntimeSignature("NotSigned", "", "", "a cadeia não foi montada"), "#150 assinatura (pwsh): sem SignerCertificate a raiz fica vazia, sem erro no script");
    }

    // Só no Windows: a DACL real da pasta, aplicada como no Nexo, com o instalador vazio já aberto para escrita. O dono não cria
    // arquivo, pasta nem fluxo e não troca a DACL, o handle aberto antes dela continua gravando, e o Nexo apaga tudo no fim.
    private static void WindowsFolder(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var system = new WindowsRuntimeSystem();
        var dir = Path.Combine(Path.GetTempPath(), "NexoRuntimeAcl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "vc_redist.x64.exe");
        bool Denied(Action action) { try { action(); return false; } catch (UnauthorizedAccessException) { return true; } catch (IOException) { return true; } }
        // Só o acesso negado pela DACL conta; uma violação de compartilhamento (IOException) não prova nada sobre ela.
        bool AclDenied(Action action) { try { action(); return false; } catch (UnauthorizedAccessException) { return true; } catch (IOException) { return false; } }
        // Elevado (o runner do GitHub), o token tem os Administradores habilitados, e a DACL dá a eles controle total.
        var elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        const string skip = " (processo elevado: os Administradores têm controle total na DACL; roda sem elevação)";
        var wrote = false;
        using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            system.ProtectDirectory(dir);
            try { output.Write([1, 2, 3]); output.Flush(true); wrote = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var security = new DirectoryInfo(dir).GetAccessControl();
        var sids = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var fileRules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
        check(security.AreAccessRulesProtected && sids.SequenceEqual(["S-1-3-4", "S-1-5-18", "S-1-5-32-544"]) && fileRules.SequenceEqual(["S-1-3-4", "S-1-5-18", "S-1-5-32-544"]),
            "#150 pasta (Windows): DACL protegida só com OWNER RIGHTS, SYSTEM e Administradores, propagada ao instalador");
        check(wrote && File.ReadAllBytes(file).SequenceEqual(new byte[] { 1, 2, 3 }),
            "#150 pasta (Windows): o instalador aberto para escrita antes da DACL continua gravando depois dela (o acesso vem da abertura)");
        using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            const string blocked = "#150 pasta (Windows): o usuário atual não cria arquivo, pasta nem fluxo na pasta ou no instalador e não troca a DACL";
            if (elevated) Console.WriteLine("IGNORADO: " + blocked + skip);
            else check(Denied(() => File.WriteAllText(Path.Combine(dir, "version.dll"), "x")) && Denied(() => Directory.CreateDirectory(Path.Combine(dir, "sub")))
                && AclDenied(() => File.WriteAllText(dir + ":payload", "x")) && Denied(() => File.WriteAllText(file + ":payload", "x"))
                && Denied(() => new DirectoryInfo(dir).SetAccessControl(OpenToEveryone())), blocked);
            check(system.DirectoryEntries(dir).SequenceEqual(["vc_redist.x64.exe"]), "#150 pasta (Windows): a pasta lista só o instalador aberto");
        }
        // Sem o handle do Nexo não há compartilhamento em jogo: a recusa vem só da DACL herdada pelo instalador.
        const string acl = "#150 pasta (Windows): com o instalador fechado, a DACL sozinha recusa gravar nele e no fluxo dele";
        if (elevated) Console.WriteLine("IGNORADO: " + acl + skip);
        else check(AclDenied(() => { using var _ = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }) && AclDenied(() => File.WriteAllText(file + ":payload", "x")), acl);
        var deleted = !Denied(() => { File.Delete(file); Directory.Delete(dir, false); });
        check(deleted && !Directory.Exists(dir), "#150 pasta (Windows): o Nexo apaga o instalador e a pasta com a ACE de Delete do OWNER RIGHTS");
    }

    // Uma DACL que abriria a pasta: trocá-la exige WRITE_DAC, que o OWNER RIGHTS tirou do dono.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static System.Security.AccessControl.DirectorySecurity OpenToEveryone()
    {
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
            System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        return security;
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }

    private static void Guard(Action<bool, string> check)
    {
        string? Validate(string text) => AgentProtocol.Validate("finish_support", JsonSerializer.SerializeToElement(new { }), text);
        string[] refused =
        [
            "Posso instalar o Visual C++ Redistributable para você.",
            "Quer que eu baixe o DirectX?",
            "Vou instalar o DirectX End-User Runtime agora.",
            "Deseja que eu atualize o .NET Framework?",
            "Posso reinstalar o VC++ 2015-2022 se quiser.",
            "Se preferir, posso baixar e instalar os redistribuíveis do Visual C++.",
            "Posso instalar o d3dx9_43.dll que está faltando.",
            "Eu te ajudo a instalar o vcredist agora mesmo.",
            "O jogo pede o xinput1_3.dll. Posso baixar o xinput para você.",
            "Gostaria que eu reparasse o C++ deste PC?",
            // @qa (f7bcb21): a negação ou a orientação no começo não esconde a oferta seguinte; "eu instalo", download e instalador.
            "Não consigo abrir o jogo por aqui, mas posso instalar o Visual C++ para você.",
            "Não vou mexer no antivírus, mas vou instalar o DirectX agora.",
            "Posso explicar o erro e já instalar o Visual C++ x64 para você.",
            "Posso mostrar o que falta e depois baixar o DirectX para você.",
            "Posso instalá-lo para você, o Visual C++ 2015-2022.",
            "Se quiser, eu instalo o Visual C++ agora.",
            "Posso fazer o download do DirectX para você.",
            "Posso executar o instalador do DirectX.",
            "Posso rodar o vc_redist.x64.exe para você.",
        ];
        string[] accepted =
        [
            "Não posso instalar o DirectX pelo chat.",
            "Posso explicar como instalar o Visual C++ pelo cartão Componentes do Windows para programas, em Ferramentas.",
            "Posso indicar onde instalar o DirectX: no cartão Componentes do Windows para programas, no menu Ferramentas.",
            "Você pode instalar o VC++ pelo cartão Componentes do Windows para programas, em Ferramentas.",
            "Posso instalar o driver da placa de vídeo com a sua autorização.",
            "Vou reinstalar o driver de vídeo.",
            "Posso verificar a versão do DirectX no relatório.",
            "O .NET Framework 4.8 já vem com o Windows.",
            "Posso consultar os eventos do jogo que não abre.",
            "Posso desinstalar a versão anterior do driver NVIDIA.",
            // @qa (f7bcb21): substantivo e adjetivo não são oferta; a orientação que rege a instalação passa.
            "Posso verificar a versão instalada do .NET no relatório.",
            "Posso consultar o log de instalação do Visual C++ no Visualizador de Eventos.",
            "Posso analisar o erro de instalação do DirectX se você me mandar o código.",
            "Vou ser breve, mas repare que o DirectX 12 já vem com o Windows.",
            "Posso ajudar a entender por que o FPS fica baixo com o DirectX 12.",
            "Posso conferir se há atualização do .NET no Windows Update.",
            "Vou listar os pacotes instalados do Visual C++ que aparecem no relatório.",
            "Posso explicar o que é e como instalar o Visual C++ pelo cartão Componentes do Windows para programas.",
            "Posso indicar o cartão onde você pode instalar o DirectX, em Ferramentas.",
            "Posso analisar o erro do instalador do DirectX que apareceu na tela.",
        ];
        var wrongRefused = refused.Where(text => Validate(text) != AgentProtocol.RuntimeInstallReason).Select(text => text + " → " + Validate(text)).ToArray();
        var wrongAccepted = accepted.Where(text => Validate(text) == AgentProtocol.RuntimeInstallReason || AgentProtocol.OffersRuntimeInstall(AgentProtocol.Normalize(text))).ToArray();
        check(wrongRefused.Length == 0 && wrongAccepted.Length == 0,
            "#150 trava do modelo: a oferta de instalar o VC++, o DirectX ou o .NET pelo chat é recusada; negação, orientação ao cartão e drivers passam"
            + (wrongRefused.Length > 0 ? " · deviam ser recusadas: " + string.Join(" | ", wrongRefused) : "") + (wrongAccepted.Length > 0 ? " · deviam passar: " + string.Join(" | ", wrongAccepted) : ""));
        var prompt = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ThemeSources", "Code", "AgentService.cs"));
        check(AgentProtocol.RuntimeInstallReason.Contains("“" + RuntimePresentation.CardTitle + "”") && AgentProtocol.RuntimeInstallReason.Contains("Ferramentas")
            && AgentProtocol.RuntimeCard == RuntimePresentation.CardTitle && prompt.Contains("O Nexo não instala o Visual C++ Redistributable, o DirectX nem o .NET pelo chat")
            && prompt.Contains("“" + RuntimePresentation.CardTitle + "”, no menu Ferramentas"),
            "#150 trava do modelo: o motivo e o prompt indicam o cartão de Ferramentas pelo nome exato");
    }

    private static void Card(Action<bool, string> check)
    {
        var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ThemeSources", "MainWindow.xaml.cs"));
        var install = Regex.Match(code, @"private async Task InstallRuntimes\(\).*?\n    \}", RegexOptions.Singleline).Value;
        var render = Regex.Match(code, @"private void RenderRuntimeList\(\).*?\n    \}", RegexOptions.Singleline).Value;
        check(code.Contains("ToolCard(4, \"Programas · bibliotecas\", RuntimePresentation.CardTitle") && code.Contains("Action(\"Verificar este PC\", async (_, _) => await GuardAsync(ScanRuntimes), icon: \"search\")")
            && code.Contains("Action(\"Instalar selecionados\", async (_, _) => await GuardAsync(InstallRuntimes), true)") && code.Contains("officeRunning || optimizationRunning || debloatRunning || runtimeRunning"),
            "#150 cartão: 04 em Ferramentas, Verificar (secundário) e Instalar selecionados (primário), e o Nexo não fecha no meio da instalação");
        check(install.IndexOf("ConfirmQuestion", StringComparison.Ordinal) is > 0 and var confirm && install.IndexOf("RuntimeInstaller.InstallAsync", StringComparison.Ordinal) > confirm
            && install.Contains("check.IsChecked == true") && install.Contains("runtimes-audit.ndjson") && code.Contains("healthStore.SaveIntervention(new InterventionRecord")
            && render.Contains("RuntimeCatalog.DefaultSelected(status)") && render.Contains("status.State == RuntimeState.Missing"),
            "#150 cartão: caixa só nos itens que faltam, marcada pela regra do catálogo; confirmação com a lista antes de instalar; auditoria e operations.db");
        check(RuntimePresentation.CardNotice.Contains("não mexe em antivírus, anticheat nem serviços") && RuntimePresentation.CardNotice.Contains("instalador oficial da Microsoft"),
            "#150 cartão: aviso de que o Nexo não mexe em antivírus, anticheat nem serviços");
    }
}
