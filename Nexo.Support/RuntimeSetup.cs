using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Nexo.Support;

public enum RuntimeState { Installed, Missing, NotApplicable }

public sealed record RuntimeStatus(RuntimeItem Item, RuntimeState State, string Version, string Detail);

public sealed record RuntimeScan(IReadOnlyList<RuntimeStatus> Items, string DotNetFramework, IReadOnlyList<string> DesktopRuntimes, bool Is64BitWindows)
{
    public IEnumerable<RuntimeStatus> Missing => Items.Where(item => item.State == RuntimeState.Missing);
}

/// <summary>Leituras do Windows usadas pela verificação (#150): só registro e existência de arquivo, sem admin e sem escrita.</summary>
public interface IRuntimeProbe
{
    bool Is64BitWindows { get; }
    /// <summary>Valor em HKLM; <paramref name="wow64"/> escolhe a visão de 32 bits (WOW6432Node) em vez da de 64.</summary>
    object? Value(bool wow64, string path, string name);
    IReadOnlyList<string> ValueNames(bool wow64, string path);
    /// <summary>DisplayName das chaves de desinstalação de HKLM, nas duas visões.</summary>
    IReadOnlyList<string> UninstallNames();
    bool FileExists(string path);
    /// <summary>System32 nativo (x64 no Windows de 64 bits).</summary>
    string SystemFolder { get; }
    /// <summary>SysWOW64 (x86 no Windows de 64 bits).</summary>
    string Wow64Folder { get; }
}

/// <summary>
/// Verificação só de leitura (#150). VC++ v14: Installed=1 em HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{x64|x86}, nas
/// duas visões do registro (o instalador grava na de 32 bits), e, se faltar, o nome nas chaves de desinstalação. VC++ 2012 e 2013:
/// a chave Runtimes da versão (11.0, 12.0) ou o nome. VC++ 2005 a 2010: o nome nas chaves de desinstalação. A busca pelo nome é
/// aproximada de propósito para o lado do "instalado": qualquer build daquele ano e arquitetura conta, para não mostrar "falta"
/// num PC que tem o componente. DirectX: as DLLs de junho de 2010 em System32 e SysWOW64. .NET Framework e .NET Desktop: só
/// informativos.
/// </summary>
public static class RuntimeInspector
{
    private const string NetFramework = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full";

    public static RuntimeScan Scan(IRuntimeProbe probe)
    {
        var names = SafeNames(probe);
        var items = RuntimeCatalog.Items.Select(item => Detect(probe, item, names)).ToArray();
        return new(items, DotNetFramework(probe), DesktopRuntimes(probe), probe.Is64BitWindows);
    }

    public static RuntimeStatus Detect(IRuntimeProbe probe, RuntimeItem item) => Detect(probe, item, SafeNames(probe));

    private static IReadOnlyList<string> SafeNames(IRuntimeProbe probe)
    {
        try { return probe.UninstallNames(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
    }

    private static RuntimeStatus Detect(IRuntimeProbe probe, RuntimeItem item, IReadOnlyList<string> names)
    {
        if (item.Arch == RuntimeArch.X64 && !probe.Is64BitWindows)
            return new(item, RuntimeState.NotApplicable, "", "Este Windows é de 32 bits.");
        if (item.Kind == RuntimeKind.DirectX) return DirectX(probe, item);
        var arch = item.ArchLabel;
        var vsVersion = item.VcYear switch { 2015 => "14.0", 2013 => "12.0", 2012 => "11.0", _ => null };
        if (vsVersion != null)
            foreach (var wow64 in new[] { true, false })
            {
                var key = @"SOFTWARE\Microsoft\VisualStudio\" + vsVersion + @"\VC\Runtimes\" + arch;
                if (Read(probe, wow64, key, "Installed") is int installed && installed == 1)
                    return new(item, RuntimeState.Installed, Version(Read(probe, wow64, key, "Version")), "Registro do Visual C++ (" + vsVersion + ").");
            }
        var match = names.FirstOrDefault(name => NameMatches(name, item));
        return match != null
            ? new(item, RuntimeState.Installed, VersionIn(match), "Em Aplicativos: " + match + ".")
            : new(item, RuntimeState.Missing, "", "Não aparece no registro nem em Aplicativos.");
    }

    /// <summary>O nome da chave de desinstalação é deste ano e desta arquitetura do VC++ ("Microsoft Visual C++ 2010  x86
    /// Redistributable - 10.0.40219", "Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Visual C++ 2005
    /// Redistributable" sem arquitetura, que é o x86).</summary>
    internal static bool NameMatches(string displayName, RuntimeItem item)
    {
        var name = AgentProtocol.Normalize(displayName);
        var years = item.VcYear == 2015 ? @"(?:2015|2017|2019|2022|v14)" : item.VcYear.ToString();
        if (!Regex.IsMatch(name, @"\bvisual c\+\+ " + years + @"\b") || !Regex.IsMatch(name, @"\b(?:redistributable|runtime)\b")) return false;
        if (Regex.IsMatch(name, @"\b(?:ia64|arm64|arm)\b")) return false;
        var x64 = Regex.IsMatch(name, @"\b(?:x64|amd64)\b|64-bit");
        return item.Arch == RuntimeArch.X64 ? x64 : !x64;
    }

    private static RuntimeStatus DirectX(IRuntimeProbe probe, RuntimeItem item)
    {
        var folders = probe.Is64BitWindows ? new[] { ("System32", probe.SystemFolder), ("SysWOW64", probe.Wow64Folder) } : new[] { ("System32", probe.SystemFolder) };
        var missing = new List<string>();
        foreach (var (label, folder) in folders)
            foreach (var file in RuntimeCatalog.DirectXFiles)
            {
                bool exists;
                try { exists = probe.FileExists(Path.Combine(folder, file)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { exists = true; }
                if (!exists) missing.Add(label + @"\" + file);
            }
        return missing.Count == 0
            ? new(item, RuntimeState.Installed, "", "As DLLs " + string.Join(", ", RuntimeCatalog.DirectXFiles) + " existem em " + string.Join(" e ", folders.Select(f => f.Item1)) + ".")
            : new(item, RuntimeState.Missing, "", "Faltam: " + string.Join(", ", missing) + ".");
    }

    /// <summary>Versão do .NET Framework 4.x pelo Release de NDP\v4\Full (tabela da Microsoft).</summary>
    public static string DotNetFramework(IRuntimeProbe probe)
    {
        var release = Read(probe, false, NetFramework, "Release") as int? ?? Read(probe, true, NetFramework, "Release") as int?;
        return (release switch
        {
            null => "não encontrado",
            >= 533320 => "4.8.1",
            >= 528040 => "4.8",
            >= 461808 => "4.7.2",
            >= 461308 => "4.7.1",
            >= 460798 => "4.7",
            >= 394802 => "4.6.2",
            >= 394254 => "4.6.1",
            >= 393295 => "4.6",
            >= 379893 => "4.5.2",
            >= 378675 => "4.5.1",
            >= 378389 => "4.5",
            _ => "anterior ao 4.5",
        }) + (release is { } value ? " (Release " + value + ")" : "");
    }

    /// <summary>.NET Desktop Runtime instalados, pelas chaves que o instalador do .NET grava. Só informativo.</summary>
    public static IReadOnlyList<string> DesktopRuntimes(IRuntimeProbe probe)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var wow64 in new[] { true, false })
            foreach (var arch in new[] { "x64", "x86" })
            {
                IReadOnlyList<string> versions;
                try { versions = probe.ValueNames(wow64, @"SOFTWARE\dotnet\Setup\InstalledVersions\" + arch + @"\sharedfx\Microsoft.WindowsDesktop.App"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
                foreach (var version in versions.Where(v => System.Version.TryParse(v.Split('-')[0], out _))) found.Add(version + " (" + arch + ")");
            }
        return [.. found];
    }

    private static object? Read(IRuntimeProbe probe, bool wow64, string path, string name)
    {
        try { return probe.Value(wow64, path, name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }
    private static string Version(object? value) => value is string text ? text.Trim().TrimStart('v', 'V') : "";
    private static string VersionIn(string name) => Regex.Match(name, @"\d+\.\d+\.\d+(?:\.\d+)?") is { Success: true } m ? m.Value : "";
}

public enum RuntimeOutcome { Ok, AlreadyInstalled, RebootPending, Failed, UacCanceled, Integrity, Signature, Host, Network, Link, Untrusted, FolderChanged, NotStarted }

public sealed record RuntimeInstallResult(RuntimeItem Item, RuntimeOutcome Outcome, string Message, int? ExitCode = null, string Signer = "", string Sha256 = "")
{
    public string Label => RuntimeInstaller.Label(Outcome);
}

/// <summary>Linha de auditoria por item (runtimes-audit.ndjson). <see cref="Leftover"/> é a pasta temporária que não pôde ser apagada.</summary>
public sealed record RuntimeAudit(DateTimeOffset At, string Id, string Title, string Outcome, int? ExitCode, string Url, string FinalUrl, long Bytes, string Sha256, bool Pinned, string Signer, string Message, string Leftover = "");

/// <summary>Get-AuthenticodeSignature e a raiz da cadeia do SignerCertificate (impressão digital SHA-1), da mesma chamada.</summary>
/// <summary>Chain vazio quando a cadeia foi montada (Build verdadeiro) só com prazo vencido ou revogação desconhecida; senão, o motivo.</summary>
public sealed record RuntimeSignature(string Status, string Subject, string Root, string Chain = "");

/// <summary>O que a instalação precisa do Windows e da rede; as verificações trocam tudo por simulações.</summary>
public interface IRuntimeInstallSystem : IRuntimeProbe
{
    Task<HttpResponseMessage> Send(Uri url, CancellationToken token);
    Task<RuntimeSignature> Signature(string file, CancellationToken token);
    /// <summary>Código de saída do instalador, ou null quando o UAC é cancelado.</summary>
    Task<int?> RunElevated(string file, string arguments, CancellationToken token);
    string WorkDirectory();
    /// <summary>Primeiro link (reparse point) no arquivo ou em qualquer pasta acima dele, ou null.</summary>
    string? Linked(string file);
    /// <summary>DACL protegida, sem herança, na pasta do instalador: OWNER RIGHTS só lê, executa e apaga; SYSTEM e Administradores
    /// com controle total. O usuário atual deixa de poder criar arquivos e pastas ali e de trocar a DACL.</summary>
    void ProtectDirectory(string directory);
    /// <summary>Tudo o que há na pasta: arquivos e subpastas pelo nome e cada fluxo alternativo (ADS) da pasta ou de uma entrada,
    /// como "nome:fluxo".</summary>
    IReadOnlyList<string> DirectoryEntries(string directory);
}

/// <summary>
/// Instalação do que o usuário marcou (#150), um item por vez e um UAC por item. Cada instalador é baixado só de hosts da
/// Microsoft por HTTPS (redirecionamentos conferidos um a um), com limite de tamanho durante o download, gravado numa pasta nova
/// em %TEMP%\Nexo\Runtimes, mantido aberto sem compartilhar escrita até o instalador terminar, conferido pelo SHA-256 fixado
/// (quando houver), por link ou junção no caminho e pela assinatura Authenticode Valid do assinante Microsoft esperado, com a cadeia
/// montada e terminando na raiz esperada. A pasta ganha uma DACL protegida logo depois de o instalador ser criado vazio, antes do
/// download, e antes do UAC o Nexo confere que ela contém só o instalador.
/// Depois do instalador, a verificação confere o componente. Um UAC cancelado encerra a fila: os itens seguintes ficam "não iniciado".
/// </summary>
public static class RuntimeInstaller
{
    public static Task<IReadOnlyList<RuntimeInstallResult>> InstallAsync(IRuntimeInstallSystem system, IReadOnlyList<RuntimeItem> items,
        IProgress<string>? progress, Action<RuntimeAudit>? audit, CancellationToken token) => InstallAsync(system, items, progress, audit, token, null);

    // O hash esperado só é trocado pelas verificações, para simular um instalador fixado; o app usa sempre o SHA-256 do catálogo.
    internal static async Task<IReadOnlyList<RuntimeInstallResult>> InstallAsync(IRuntimeInstallSystem system, IReadOnlyList<RuntimeItem> items,
        IProgress<string>? progress, Action<RuntimeAudit>? audit, CancellationToken token, Func<RuntimeItem, string?>? expectedSha256)
    {
        var results = new List<RuntimeInstallResult>();
        string? stop = null;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            RuntimeInstallResult result;
            var trace = new Trace();
            if (stop == null && token.IsCancellationRequested) stop = "o cancelamento foi pedido antes de ele começar";
            if (stop != null) result = new(item, RuntimeOutcome.NotStarted, "Não iniciado: " + stop + ".");
            else
            {
                progress?.Report($"({i + 1}/{items.Count}) {item.Title}: verificando...");
                try { result = await InstallOne(system, item, progress, trace, token, expectedSha256?.Invoke(item) ?? item.Sha256); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { result = new(item, RuntimeOutcome.NotStarted, "Não iniciado: o cancelamento foi pedido durante o download. Nada foi executado."); }
                if (result.Outcome == RuntimeOutcome.UacCanceled) stop = "a autorização de administrador de um item anterior foi cancelada";
            }
            results.Add(result);
            try { audit?.Invoke(new(DateTimeOffset.UtcNow, item.Id, item.Title, result.Outcome.ToString(), result.ExitCode, item.Url.ToString(), trace.FinalUrl, trace.Bytes, result.Sha256, item.Pinned, result.Signer, result.Message, trace.Leftover)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return results;
    }

    private sealed class Trace { public string FinalUrl = ""; public long Bytes; public string Leftover = ""; }

    private static async Task<RuntimeInstallResult> InstallOne(IRuntimeInstallSystem system, RuntimeItem item, IProgress<string>? progress, Trace trace, CancellationToken token, string? expectedSha256)
    {
        if (RuntimeInspector.Detect(system, item) is { State: not RuntimeState.Missing } before)
            return new(item, before.State == RuntimeState.Installed ? RuntimeOutcome.AlreadyInstalled : RuntimeOutcome.NotStarted,
                before.State == RuntimeState.Installed ? "Já instalado. Nada foi baixado." : "Não se aplica a este Windows: " + before.Detail);
        var directory = system.WorkDirectory();
        var file = Path.Combine(directory, item.FileName);
        try
        {
            Directory.CreateDirectory(directory);
            // O instalador nasce vazio, já aberto para escrita sem compartilhamento, e a DACL vem logo em seguida, antes do download.
            // Um processo da mesma conta poderia gravar uma DLL na pasta, e os instaladores antigos (IExpress) carregam DLLs da própria
            // pasta já como administrador. A DACL protegida tira do dono a criação de arquivos e o WRITE_DAC; o acesso do handle já
            // aberto foi concedido na abertura, então o Nexo continua gravando nele.
            var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                try { system.ProtectDirectory(directory); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or IdentityNotMappedException or PlatformNotSupportedException)
                {
                    return new(item, RuntimeOutcome.Failed, "Não foi possível proteger a pasta do instalador (" + Short(ex.Message) + "). Nada foi baixado nem executado.");
                }
                progress?.Report(item.Title + ": baixando o instalador oficial da Microsoft...");
                if (await Download(system, item, output, trace, token) is { } failure) return failure;
            }
            finally { await output.DisposeAsync(); }
            // Reaberto só para leitura, sem permitir escrita nem exclusão, até o instalador terminar. O Get-AuthenticodeSignature e o
            // runas leem com compartilhamento de leitura, o que um handle de escrita impediria. Se outro handle de escrita existir
            // (aberto antes da DACL), a abertura falha; se algo mudou o arquivo entre o fechamento e esta abertura, o hash acusa.
            await using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var limit = item.Size ?? item.MaxSize;
            if (item.Size is { } size && held.Length != size) return Integrity(item, $"o arquivo tem {held.Length} bytes, e o esperado é {size}");
            if (held.Length > limit) return Integrity(item, "o arquivo é maior que o limite");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(held, token)).ToLowerInvariant();
            if (expectedSha256 != null && !string.Equals(hash, expectedSha256, StringComparison.Ordinal))
                return Integrity(item, "o SHA-256 não é o fixado no Nexo", hash);
            if (system.Linked(file) is { } link)
                return new(item, RuntimeOutcome.Link, $"O caminho do instalador passa por um link ou junção ({link}). O arquivo foi apagado e nada foi executado.", Sha256: hash);
            progress?.Report(item.Title + ": conferindo a assinatura digital...");
            string status, signer, root, chain;
            try { (status, signer, root, chain) = await system.Signature(file, token); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception)
            {
                return new(item, RuntimeOutcome.Signature, "Não foi possível conferir a assinatura digital (" + Short(ex.Message) + "). O arquivo foi apagado e nada foi executado.", Sha256: hash);
            }
            if (!string.Equals(status, "Valid", StringComparison.Ordinal))
                return new(item, RuntimeOutcome.Signature, $"A assinatura digital não é válida (estado: {(status.Length == 0 ? "desconhecido" : status)}). O arquivo foi apagado e nada foi executado.", Signer: signer, Sha256: hash);
            if (!string.Equals(signer, item.Signer, StringComparison.Ordinal))
                return new(item, RuntimeOutcome.Signature, $"O assinante não é o da Microsoft esperado ({(signer.Length == 0 ? "não informado" : signer)}). O arquivo foi apagado e nada foi executado.", Signer: signer, Sha256: hash);
            // Cadeia que não fecha (PartialChain), com assinatura inválida ou com qualquer outro problema além do prazo e da revogação.
            if (chain.Length > 0)
                return new(item, RuntimeOutcome.Untrusted, $"Assinatura não confiável: a cadeia do certificado não foi validada ({chain}). O arquivo foi apagado e nada foi executado.", Signer: signer, Sha256: hash);
            // Uma raiz extra no PC (adware, proxy TLS) emitiria um certificado com o mesmo Subject; a raiz da cadeia não seria a da Microsoft.
            if (!string.Equals(root, item.Root, StringComparison.OrdinalIgnoreCase))
                return new(item, RuntimeOutcome.Untrusted, $"Assinatura não confiável: a cadeia do certificado termina em outra raiz ({(root.Length == 0 ? "não informada" : root)}), e a esperada é a {item.Root}. O arquivo foi apagado e nada foi executado.", Signer: signer, Sha256: hash);
            // Com o instalador ainda aberto e a pasta já protegida: só ele pode estar ali.
            var entries = system.DirectoryEntries(directory);
            if (entries.Count != 1 || !string.Equals(entries[0], item.FileName, StringComparison.OrdinalIgnoreCase))
            {
                var extra = entries.Where(e => !string.Equals(e, item.FileName, StringComparison.OrdinalIgnoreCase)).Take(5).ToArray();
                return new(item, RuntimeOutcome.FolderChanged, "Pasta do instalador alterada: além do instalador, ela contém " + (extra.Length == 0 ? "outro conteúdo" : string.Join(", ", extra)) + ". Nada foi executado.", Signer: signer, Sha256: hash);
            }
            progress?.Report(item.Title + ": aguardando a autorização de administrador (UAC) e o instalador da Microsoft...");
            int? exit;
            try { exit = await system.RunElevated(file, item.Arguments, token); }
            catch (Win32Exception ex) { return new(item, RuntimeOutcome.Failed, "O Windows não abriu o instalador (" + Short(ex.Message) + "). Nada foi executado.", Signer: signer, Sha256: hash); }
            if (exit == null) return new(item, RuntimeOutcome.UacCanceled, "A autorização de administrador foi cancelada. Este item não foi instalado.", Signer: signer, Sha256: hash);
            var after = RuntimeInspector.Detect(system, item);
            return exit switch
            {
                RuntimeCatalog.ExitReboot => new(item, RuntimeOutcome.RebootPending,
                    $"Instalado, com reinício pendente (código {exit}). O Nexo não reinicia o PC: reinicie quando puder.", exit, signer, hash),
                RuntimeCatalog.ExitRebootStarted => new(item, RuntimeOutcome.RebootPending,
                    $"Instalado, e o instalador da Microsoft já iniciou o reinício do Windows (código {exit}). Salve o que estiver aberto.", exit, signer, hash),
                RuntimeCatalog.ExitNewerInstalled when after.State == RuntimeState.Installed => new(item, RuntimeOutcome.AlreadyInstalled,
                    "Uma versão igual ou mais nova já está instalada (código 1638), e a verificação confirma o componente.", exit, signer, hash),
                RuntimeCatalog.ExitNewerInstalled => new(item, RuntimeOutcome.Failed,
                    "Falha (1638): o instalador disse que já há uma versão igual ou mais nova, mas o componente não aparece como instalado. " + after.Detail, exit, signer, hash),
                RuntimeCatalog.ExitOk when after.State == RuntimeState.Installed => new(item, RuntimeOutcome.Ok,
                    "Instalado e conferido" + (after.Version.Length > 0 ? " (versão " + after.Version + ")" : "") + ". Assinatura válida de: " + signer + ".", exit, signer, hash),
                RuntimeCatalog.ExitOk => new(item, RuntimeOutcome.Failed, "O instalador terminou sem erro, mas o componente não aparece como instalado. " + after.Detail, exit, signer, hash),
                _ => new(item, RuntimeOutcome.Failed, $"O instalador terminou com o código {exit}" + (after.State == RuntimeState.Installed ? ", mas o componente aparece como instalado." : ", e o componente não aparece como instalado."), exit, signer, hash),
            };
        }
        catch (HttpRequestException ex) { return Offline(item, ex.Message); }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested) { return Offline(item, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(item, RuntimeOutcome.Failed, "Não foi possível gravar ou ler o instalador na pasta temporária do Nexo: " + Short(ex.Message)); }
        finally
        {
            // O Nexo apaga o arquivo e a pasta com a ACE de Delete do OWNER RIGHTS. Se não der (algo a mais na pasta), ela fica e vai à auditoria.
            try { if (File.Exists(file)) File.Delete(file); if (Directory.Exists(directory)) Directory.Delete(directory, false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { trace.Leftover = directory + " (" + Short(ex.Message) + ")"; }
        }
    }

    // Cada salto é conferido antes do pedido: HTTPS, porta 443, sem usuário na URL e host exato da lista da Microsoft.
    private static async Task<RuntimeInstallResult?> Download(IRuntimeInstallSystem system, RuntimeItem item, Stream output, Trace trace, CancellationToken token)
    {
        var url = item.Url;
        var limit = item.Size ?? item.MaxSize;
        for (var hop = 0; ; hop++)
        {
            trace.FinalUrl = url.ToString();
            if (!RuntimeCatalog.AllowedHost(url))
                return new(item, RuntimeOutcome.Host, $"O download foi levado para um endereço fora da Microsoft ({url.Scheme}://{url.Host}). Nada foi baixado nem executado.");
            using var response = await system.Send(url, token);
            var code = (int)response.StatusCode;
            if (code is >= 300 and < 400)
            {
                if (hop >= RuntimeCatalog.MaxRedirects || response.Headers.Location is not { } location)
                    return new(item, RuntimeOutcome.Network, "A Microsoft não entregou o instalador (redirecionamento sem destino ou repetido demais). Nada foi executado.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK) return new(item, RuntimeOutcome.Network, $"A Microsoft respondeu {code} ao pedido do instalador. Nada foi executado.");
            if (response.Content.Headers.ContentLength is { } length && (item.Size is { } size ? length != size : length > limit))
                return Integrity(item, $"o tamanho informado é {length} bytes, e o esperado é " + (item.Size is { } fixedSize ? fixedSize + " bytes" : "até " + limit + " bytes"));
            await using var source = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                total += read;
                trace.Bytes = total;
                if (total > limit) return Integrity(item, "o arquivo recebido é maior que o limite");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            return item.Size is { } expected && total != expected ? Integrity(item, $"o arquivo recebido tem {total} bytes, e o esperado é {expected}") : null;
        }
    }

    private static string Short(string detail) => detail.Length <= 200 ? detail : detail[..200];
    private static RuntimeInstallResult Integrity(RuntimeItem item, string reason, string hash = "") =>
        new(item, RuntimeOutcome.Integrity, "O instalador baixado não confere: " + reason + ". O arquivo foi apagado e nada foi executado.", Sha256: hash);
    private static RuntimeInstallResult Offline(RuntimeItem item, string detail) =>
        new(item, RuntimeOutcome.Network, "Sem conexão com a Microsoft para baixar o instalador. Nada foi instalado. Detalhe: " + Short(detail));

    public static string Label(RuntimeOutcome outcome) => outcome switch
    {
        RuntimeOutcome.Ok => "Ok",
        RuntimeOutcome.AlreadyInstalled => "Já instalado",
        RuntimeOutcome.RebootPending => "Reinício pendente",
        RuntimeOutcome.Failed => "Falha",
        RuntimeOutcome.UacCanceled => "UAC cancelado",
        RuntimeOutcome.Integrity => "Integridade",
        RuntimeOutcome.Signature => "Assinatura",
        RuntimeOutcome.Host => "Endereço recusado",
        RuntimeOutcome.Network => "Sem download",
        RuntimeOutcome.Link => "Link no caminho",
        RuntimeOutcome.Untrusted => "Assinatura não confiável",
        RuntimeOutcome.FolderChanged => "Pasta do instalador alterada",
        _ => "Não iniciado",
    };

    /// <summary>Estado geral para o histórico: concluída (todos ok, já instalados ou com reinício), parcial ou falhou.</summary>
    public static string State(IReadOnlyList<RuntimeInstallResult> results) =>
        results.All(Succeeded) ? "concluída" : results.Any(Succeeded) ? "parcial" : results.All(r => r.Outcome is RuntimeOutcome.UacCanceled or RuntimeOutcome.NotStarted) ? "cancelada" : "falhou";
    public static bool Succeeded(RuntimeInstallResult result) => result.Outcome is RuntimeOutcome.Ok or RuntimeOutcome.AlreadyInstalled or RuntimeOutcome.RebootPending;

    public static string Summary(IReadOnlyList<RuntimeInstallResult> results) =>
        string.Join(", ", results.GroupBy(r => r.Outcome).OrderBy(g => g.Key).Select(g => g.Count() + " " + Label(g.Key).ToLowerInvariant()));
}

/// <summary>Textos do cartão, sem depender do WPF.</summary>
public static class RuntimePresentation
{
    public const string CardTitle = "Componentes do Windows para programas";
    public const string CardNotice = "O Nexo verifica o Visual C++ Redistributable (2005 a 2015–2022, x86 e x64), o DirectX End-User Runtime de junho de 2010 e a versão do .NET Framework. Ele instala só o que você marcar, sempre pelo instalador oficial da Microsoft, com a assinatura conferida antes do UAC. O Nexo não mexe em antivírus, anticheat nem serviços do Windows.";

    public static string ConfirmQuestion(IEnumerable<RuntimeItem> items) =>
        "O Nexo vai baixar da Microsoft e instalar, um de cada vez:\n\n"
        + string.Join("\n", items.Select(item => "• " + item.Title + " — " + item.Url.Host + (item.Pinned ? ", SHA-256 fixado" : ", versão atual com assinatura Microsoft")))
        + "\n\nCada instalador tem a assinatura digital conferida antes de rodar, e o Windows pede autorização de administrador (UAC) para cada um. Se algum pedir reinício, o Nexo avisa e não reinicia o PC. Continuar?";

    public static string ScanText(RuntimeScan scan)
    {
        var missing = scan.Missing.Count();
        var desktop = scan.DesktopRuntimes.Count == 0 ? "nenhum encontrado" : string.Join(", ", scan.DesktopRuntimes);
        return (missing == 0 ? "Nenhum componente do catálogo falta neste PC." : $"{missing} componente(s) faltando. Marque o que quer instalar e clique em Instalar selecionados.")
            + $"\n.NET Framework: {scan.DotNetFramework}. .NET Desktop Runtime: {desktop}. (Só informativo.)";
    }

    public static string StatusLine(RuntimeStatus status) => status.Item.Title + ": " + status.State switch
    {
        RuntimeState.Installed => "instalado" + (status.Version.Length > 0 ? " (" + status.Version + ")" : ""),
        RuntimeState.NotApplicable => "não se aplica",
        _ => "falta",
    };

    public static string Result(IReadOnlyList<RuntimeInstallResult> results) =>
        "Instalação: " + RuntimeInstaller.Summary(results) + ".\n" + string.Join("\n", results.Select(r => r.Item.Title + ": " + r.Label + ". " + r.Message))
        + (results.Any(r => r.Outcome == RuntimeOutcome.RebootPending && r.ExitCode != RuntimeCatalog.ExitRebootStarted) ? "\nReinicie o PC quando puder; o Nexo não reinicia sozinho." : "")
        + (results.Any(r => r.ExitCode == RuntimeCatalog.ExitRebootStarted) ? "\nO instalador da Microsoft já iniciou o reinício do Windows; salve o que estiver aberto." : "")
        + "\nClique em Verificar este PC para ver o estado atual.";
}

/// <summary>O Windows de verdade: registro só de leitura, HTTP sem redirecionamento automático, Get-AuthenticodeSignature com a cadeia,
/// DACL da pasta do instalador e "runas".</summary>
public sealed class WindowsRuntimeSystem : IRuntimeInstallSystem
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public bool Is64BitWindows => Environment.Is64BitOperatingSystem;
    public string SystemFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem ? "System32" : "Sysnative");
    public string Wow64Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");

    private static RegistryKey Root(bool wow64) => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, wow64 ? RegistryView.Registry32 : RegistryView.Registry64);

    public object? Value(bool wow64, string path, string name)
    {
        using var root = Root(wow64);
        using var key = root.OpenSubKey(path, false);
        return key?.GetValue(name);
    }

    public IReadOnlyList<string> ValueNames(bool wow64, string path)
    {
        using var root = Root(wow64);
        using var key = root.OpenSubKey(path, false);
        return key?.GetValueNames() ?? [];
    }

    public IReadOnlyList<string> UninstallNames()
    {
        var names = new List<string>();
        foreach (var wow64 in new[] { false, true })
        {
            using var root = Root(wow64);
            using var key = root.OpenSubKey(Uninstall, false);
            if (key == null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                try
                {
                    using var entry = key.OpenSubKey(sub, false);
                    if (entry?.GetValue("DisplayName") is string name && name.Length > 0) names.Add(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        return names;
    }

    public bool FileExists(string path) => File.Exists(path);

    public Task<HttpResponseMessage> Send(Uri url, CancellationToken token) => Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);

    public async Task<RuntimeSignature> Signature(string file, CancellationToken token) => ParseSignature(await SupportService.Powershell(SignatureScript(file), token));

    // Na mesma chamada do Get-AuthenticodeSignature (o invólucro do SupportService põe o guard da #131), a cadeia do SignerCertificate
    // é montada com X509Chain, sem consulta de revogação (não depende de rede; o Authenticode Valid já veio do Windows) e ignorando
    // só o prazo: a raiz de 2001 e as folhas do 2012 e do 2013 estão vencidas. Os certificados embutidos no arquivo entram só como
    // auxiliares; a raiz é o último elemento da cadeia. O resultado do Build e cada ChainStatus vão na saída, e o ParseSignature decide.
    internal static string SignatureScript(string file) =>
        "$s=Get-AuthenticodeSignature -LiteralPath " + WindowsTools.Quote(file) + "; $root=''; $built=$false; $chain=@()"
        + "; if($s.SignerCertificate){$c=New-Object System.Security.Cryptography.X509Certificates.X509Chain"
        + "; $c.ChainPolicy.RevocationMode=[System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck"
        + "; $c.ChainPolicy.VerificationFlags=[System.Security.Cryptography.X509Certificates.X509VerificationFlags]::IgnoreNotTimeValid"
        + "; $x=New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection; try{$x.Import(" + WindowsTools.Quote(file) + ")}catch{}"
        + "; [void]$c.ChainPolicy.ExtraStore.AddRange($x); $built=[bool]$c.Build($s.SignerCertificate)"
        + "; $chain=@($c.ChainStatus | ForEach-Object { [string]$_.Status })"
        + "; if($c.ChainElements.Count -gt 0){$root=[string]$c.ChainElements[$c.ChainElements.Count-1].Certificate.Thumbprint}; $c.Reset()}"
        + "; [pscustomobject]@{Status=[string]$s.Status;Subject=[string]$s.SignerCertificate.Subject;Root=[string]$root;Built=$built;Chain=$chain} | ConvertTo-Json -Compress";

    // Com IgnoreNotTimeValid e NoCheck, o Build verdadeiro ainda lista o prazo vencido e, se aparecer, a revogação desconhecida.
    private static readonly string[] ChainTolerated = ["NotTimeValid", "RevocationStatusUnknown", "OfflineRevocation"];

    internal static RuntimeSignature ParseSignature(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            var built = root.TryGetProperty("Built", out var b) && b.ValueKind == JsonValueKind.True;
            var statuses = root.TryGetProperty("Chain", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(s => s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "?").ToArray() : null;
            var chain = statuses == null ? "estado da cadeia não informado"
                : !built ? "a cadeia não foi montada" + (statuses.Length > 0 ? ": " + string.Join(", ", statuses) : "")
                : string.Join(", ", statuses.Where(s => !ChainTolerated.Contains(s, StringComparer.Ordinal)));
            return new(Text("Status"), Text("Subject"), chain.Length == 0 ? Text("Root").Trim() : "", chain);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return new("", "", "", "saída do PowerShell ilegível"); }
    }

    // O instalador já iniciado não é interrompido pelo cancelamento: o Nexo espera o fim para informar o resultado.
    public async Task<int?> RunElevated(string file, string arguments, CancellationToken token)
    {
        Process? process;
        try { process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(file)! }); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return null; }
        if (process == null) throw new IOException("O Windows não iniciou o instalador.");
        using (process)
        {
            await process.WaitForExitAsync(CancellationToken.None);
            return process.ExitCode;
        }
    }

    public string WorkDirectory() => Path.Combine(Path.GetTempPath(), "Nexo", "Runtimes", Guid.NewGuid().ToString("N"));

    // Sem admin: o dono ainda tem o WRITE_DAC implícito e define a DACL; depois dela, o OWNER RIGHTS tira esse direito. As ACEs são
    // herdáveis, e o Windows as propaga ao instalador já criado (a herança antiga, do %TEMP%, sai). Nenhuma ACE para o usuário atual.
    public void ProtectDirectory(string directory)
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"),
            FileSystemRights.ReadAndExecute | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    // Arquivos ocultos e de sistema também contam; os fluxos alternativos vêm do FindFirstStreamW, na pasta e em cada entrada.
    public IReadOnlyList<string> DirectoryEntries(string directory)
    {
        var entries = new List<string>(Streams(directory, ""));
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false }))
        {
            var name = Path.GetFileName(path);
            entries.Add(name);
            entries.AddRange(Streams(path, name));
        }
        return entries;
    }

    private const int HandleEof = 38;
    private static IEnumerable<string> Streams(string path, string label)
    {
        var found = new List<string>();
        var handle = FindFirstStreamW(path, 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == HandleEof) return found;
            throw new IOException("Não foi possível listar os fluxos de " + path + " (erro " + error + ").");
        }
        try
        {
            do { if (!string.Equals(data.Name, "::$DATA", StringComparison.OrdinalIgnoreCase)) found.Add(label + data.Name); }
            while (FindNextStreamW(handle, out data));
            var error = Marshal.GetLastWin32Error();
            if (error != HandleEof) throw new IOException("Não foi possível listar os fluxos de " + path + " (erro " + error + ").");
        }
        finally { FindClose(handle); }
        return found;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstStreamW(string name, int level, out StreamData data, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStreamW(IntPtr find, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr find);

    // Do arquivo até a raiz do volume: %TEMP% pode apontar para qualquer pasta do usuário, então a cadeia inteira é conferida.
    public string? Linked(string file)
    {
        for (var path = Path.GetFullPath(file); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return path;
        return null;
    }
}
