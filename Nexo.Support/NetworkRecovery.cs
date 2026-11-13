using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexo.Support;

public sealed record NetworkTransactionRecord(string Action, int Index, string ExpectedGuid, DateTimeOffset Created, int? ProcessId = null, int? RecoveryProcessId = null, string? Phase = null);

public sealed record InterruptedNetworkTransaction(string Directory, NetworkTransactionRecord Record, string Phase, string? Before, string? Resolution, string? ProtectedDirectory = null);

/// <summary>
/// Onde fica a cópia protegida de cada transação (#10) e como conferir que ela só pode ter sido gravada com elevação.
/// O executor elevado grava backup e registro em %ProgramData%\NexoSupport\operations\&lt;id&gt;, com dono Administradores,
/// herança desligada, SYSTEM e Administradores com controle total e Usuários só com leitura.
/// </summary>
public sealed record NetworkRecoveryTrust(string ProtectedRoot, Func<string, bool> Trusted)
{
    public static NetworkRecoveryTrust Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NexoSupport", "operations"),
        directory => OperatingSystem.IsWindows() && NetworkRecovery.TrustedOnWindows(directory))
    {
        TrustedFile = (directory, file) => OperatingSystem.IsWindows() && NetworkRecovery.TrustedFileOnWindows(directory, file),
        ChainProblem = root => OperatingSystem.IsWindows() ? NetworkRecovery.ProtectedChainProblemOnWindows(root) : null
    };

    public Func<string, string, bool>? TrustedFile { get; init; }

    /// <summary>Motivo pelo qual o executor elevado recusaria a cadeia NexoSupport\operations que já existe (#97), ou null.</summary>
    public Func<string, string?>? ChainProblem { get; init; }

    public bool TrustsFile(string directory, string file) => TrustedFile?.Invoke(directory, file) ?? Trusted(directory);
}

public interface IElevatedProcess : IDisposable
{
    int? Id { get; }
    bool HasExited { get; }
    Task WaitForExitAsync(CancellationToken token);
}

internal sealed class ElevatedProcess(Process process) : IElevatedProcess
{
    public int? Id { get { try { return process.Id; } catch (InvalidOperationException) { return null; } } }
    public bool HasExited => process.HasExited;
    public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
    public void Dispose() => process.Dispose();
}

public static class NetworkRecovery
{
    public const string RecordFile = "transaction.json";
    public const string ResultFile = "recovery-result.json";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(10);
    /// <summary>Uma transação interrompida há mais tempo que isso não tem a restauração oferecida (#10).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public const string NotStartedMessage = "O executor de rede não foi iniciado ({0}). Nenhuma alteração de rede foi feita.";

    /// <summary>Limite da linha de comando do Windows (CreateProcess), que inclui o caminho do powershell.exe.</summary>
    public const int CommandLineLimit = WindowsTools.CommandLineLimit;

    public static IElevatedProcess StartElevated(string script) => new ElevatedProcess(WindowsTools.StartElevated(ElevatedArguments(script)));

    /// <summary>Argumentos que o executor de rede usa na elevação: sempre o carregador compactado.</summary>
    public static string[] ElevatedArguments(string script) => WindowsTools.ElevatedArguments(Compact(script));

    /// <summary>Tamanho da linha de comando elevada do executor de rede, como o Windows a recebe.</summary>
    public static int CommandLength(string script) => WindowsTools.CommandLength(ElevatedArguments(script));

    /// <summary>
    /// O executor de rede inteiro em -EncodedCommand (UTF-16 em Base64) passa do limite de 32 767 caracteres da linha de
    /// comando do Windows. Ele vai compactado (GZip) dentro de um carregador curto, que o descompacta na memória do
    /// processo elevado e o executa no mesmo escopo. Nada é gravado em disco no caminho até a elevação.
    /// </summary>
    public static string Compact(string script)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, true)) gzip.Write(Encoding.UTF8.GetBytes(script));
        return "$nexoScript=(New-Object System.IO.StreamReader((New-Object System.IO.Compression.GZipStream((New-Object System.IO.MemoryStream(,[Convert]::FromBase64String('"
            + Convert.ToBase64String(buffer.ToArray()) + "'))),[System.IO.Compression.CompressionMode]::Decompress)),[System.Text.Encoding]::UTF8)).ReadToEnd(); " + LoaderRun;
    }

    /// <summary>Tamanho da linha de comando se o script fosse sem compactar.</summary>
    public static int UncompactedLength(string script) => WindowsTools.CommandLength(WindowsTools.ElevatedArguments(script));

    /// <summary>Final do carregador, trocado no teste de ida e volta para conferir o script sem executá-lo.</summary>
    public const string LoaderRun = ". ([scriptblock]::Create($nexoScript))";

    public static void WriteRecord(string directory, NetworkTransactionRecord record) => WriteJson(Path.Combine(directory, RecordFile), record);

    /// <summary>Id de operação aceito: GUID no formato "N" (32 dígitos hexadecimais minúsculos), como o NetworkChange cria.</summary>
    public static bool IsOperationId(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Registra que o executor não chegou a iniciar (UAC recusado, por exemplo): a abertura seguinte não oferece nada.</summary>
    public static void MarkNotStarted(string directory, string reason)
        => WriteJson(Path.Combine(directory, ResultFile), new { success = false, restored = false, started = false, unchanged = true, message = string.Format(System.Globalization.CultureInfo.InvariantCulture, NotStartedMessage, reason), recordedAt = DateTimeOffset.UtcNow });

    public static List<InterruptedNetworkTransaction> Scan(string root, DateTimeOffset now, Func<int, bool>? isRunning = null, NetworkRecoveryTrust? trust = null)
    {
        var found = new List<InterruptedNetworkTransaction>();
        var operations = Path.Combine(root, "operations");
        if (!System.IO.Directory.Exists(operations)) return found;
        foreach (var directory in System.IO.Directory.EnumerateDirectories(operations).Where(d => IsOperationId(Path.GetFileName(d))).OrderBy(d => System.IO.Directory.GetCreationTimeUtc(d)))
            if (Inspect(directory, now, isRunning, trust: trust) is { } item) found.Add(item);
        return found;
    }

    public static InterruptedNetworkTransaction? Inspect(string directory, DateTimeOffset now, Func<int, bool>? isRunning = null, bool exited = false, NetworkRecoveryTrust? trust = null)
    {
        isRunning ??= Running;
        trust ??= NetworkRecoveryTrust.Default;
        // Só uma pasta de operação de verdade: GUID "N" e nenhum "..". Outra pasta é ignorada, sem gravar nada nela.
        var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        if (!IsOperationId(id) || directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..")) return null;
        var protectedDirectory = Path.Combine(trust.ProtectedRoot, id);
        // O resultado da restauração elevada fica na pasta protegida (#10); se o aplicativo fechou antes de copiá-lo, a
        // cópia é feita aqui, sem elevação, e a transação não é oferecida de novo.
        // Só de pasta protegida confiável: um resultado plantado numa pasta qualquer não encerra a transação.
        // Vale para o resultado da transação (result.json) e o da restauração (recovery-result.json), conferindo o
        // próprio arquivo copiado.
        foreach (var name in new[] { ResultFile, "result.json" })
        {
            if (File.Exists(Path.Combine(directory, name)) || !File.Exists(Path.Combine(protectedDirectory, name)) || !trust.TrustsFile(protectedDirectory, name)) continue;
            try { WriteTextAtomic(Path.Combine(directory, name), File.ReadAllText(Path.Combine(protectedDirectory, name))).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return null;
        }
        var recordPath = Path.Combine(directory, RecordFile);
        if (!File.Exists(recordPath) || File.Exists(Path.Combine(directory, "result.json")) || File.Exists(Path.Combine(directory, ResultFile))) return null;
        NetworkTransactionRecord record;
        try { record = JsonSerializer.Deserialize<NetworkTransactionRecord>(File.ReadAllText(recordPath)) ?? throw new JsonException("Registro vazio."); }
        catch (JsonException ex)
        {
            var unreadable = new NetworkTransactionRecord("", 0, "", now);
            return Resolved(directory, unreadable, "unknown", new { success = false, restored = false, uncertain = true, message = "O registro da transação de rede está ilegível (" + ex.Message + "). A restauração automática não foi oferecida; confira a rede manualmente." });
        }
        if (!exited && (record.ProcessId is { } pid && isRunning(pid) || record.RecoveryProcessId is { } recovery && isRunning(recovery))) return null;
        if (!exited && record.ProcessId == null && now - record.Created < StartGrace) return null;
        var decision = ReadDecision(directory);
        var applied = File.Exists(Path.Combine(protectedDirectory, "applied.json"));
        var phase = !applied ? "applying" : decision == "rollback" ? "restoring" : "confirming";
        var beforePath = Path.Combine(protectedDirectory, "before.json");
        // Sem a cópia protegida, uma transação antiga pode ter tido a pasta apagada pela limpeza de 7 dias depois de
        // alterar a rede: não se afirma que nada mudou.
        if (!File.Exists(beforePath) && now - record.Created > MaxAge)
            return Resolved(directory, record, phase, new { success = false, restored = false, uncertain = true, message = TooOldMessage(record.Created), backup = beforePath });
        if (!File.Exists(beforePath))
            return Resolved(directory, record, "before-backup", new { success = true, restored = false, unchanged = true, message = "A transação de rede foi interrompida antes de salvar o estado anterior; o executor não chegou a alterar a rede. Nenhuma restauração é necessária." });
        // A cópia em %LOCALAPPDATA% pode ser forjada por qualquer processo do usuário (#10): o que é mostrado e restaurado
        // vem só da cópia protegida, gravada pelo executor elevado.
        var protectedBefore = Path.Combine(protectedDirectory, "before.json");
        var protectedRecord = Path.Combine(protectedDirectory, RecordFile);
        if (!File.Exists(protectedBefore) || !File.Exists(protectedRecord))
            return Resolved(directory, record, phase, new { success = false, restored = false, uncertain = true, message = "Não há cópia protegida do estado anterior desta alteração de rede (ela é gravada pelo executor com permissão de administrador). A restauração automática não foi oferecida; confira a rede manualmente.", backup = beforePath });
        if (!trust.Trusted(protectedDirectory))
            return Resolved(directory, record, phase, new { success = false, restored = false, uncertain = true, message = "A cópia protegida do estado anterior tem dono ou permissões inesperados e pode ter sido alterada. A restauração automática não foi oferecida; confira a rede manualmente.", backup = beforePath });
        NetworkTransactionRecord secured;
        try { secured = JsonSerializer.Deserialize<NetworkTransactionRecord>(File.ReadAllText(protectedRecord)) ?? throw new JsonException("Registro vazio."); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Resolved(directory, record, phase, new { success = false, restored = false, uncertain = true, message = "O registro protegido da transação de rede está ilegível (" + ex.Message + "). A restauração automática não foi oferecida; confira a rede manualmente.", backup = beforePath });
        }
        if (secured.Action != record.Action || secured.Index != record.Index || !SameGuid(secured.ExpectedGuid, record.ExpectedGuid))
            return Resolved(directory, record, phase, new { success = false, restored = false, uncertain = true, message = "O registro da transação de rede não corresponde à cópia protegida. A restauração automática não foi oferecida; confira a rede manualmente.", backup = beforePath });
        record = secured with { ProcessId = record.ProcessId, RecoveryProcessId = record.RecoveryProcessId };
        if (secured.Phase is "applying" or "confirming" or "restoring") phase = secured.Phase;
        if (now - secured.Created > MaxAge)
            return Resolved(directory, record, phase, new { success = false, restored = false, uncertain = true, message = TooOldMessage(secured.Created), backup = beforePath });
        if (decision == "keep")
            return Resolved(directory, record, "keeping", new { success = false, restored = false, uncertain = true, message = "Você confirmou manter a nova configuração de rede, mas o executor terminou antes de registrar o resultado. A configuração atual não foi conferida; consulte a rede antes de outra alteração.", backup = beforePath });
        try { return new InterruptedNetworkTransaction(directory, record, phase, Validate(File.ReadAllText(protectedBefore), record), null, protectedDirectory); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
        {
            return Resolved(directory, record, phase, new { success = false, restored = false, uncertain = true, message = "O backup da configuração anterior não pôde ser validado (" + ex.Message + "). A restauração automática não foi oferecida; confira a rede manualmente.", backup = beforePath });
        }
    }

    private static string TooOldMessage(DateTimeOffset created)
        => "A alteração de rede interrompida foi iniciada há mais de " + MaxAge.TotalDays.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " dias (" + created.ToLocalTime().ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "). A restauração automática não é oferecida para uma configuração tão antiga; confira a rede antes de outra alteração.";

    public static string Validate(string beforeJson, NetworkTransactionRecord record)
    {
        if (record.Action is not ("set_ipv4" or "set_dns")) throw new InvalidDataException("ação de rede desconhecida");
        var before = JsonNode.Parse(beforeJson) as JsonObject ?? throw new InvalidDataException("formato inesperado");
        var guid = Text(before["Guid"]);
        if (!Guid.TryParse(guid, out var parsed) || !Guid.TryParse(record.ExpectedGuid, out var expected) || parsed != expected) throw new InvalidDataException("GUID da interface diferente do registrado");
        if (Number(before["Index"]) != record.Index) throw new InvalidDataException("índice da interface diferente do registrado");
        var dhcp = Text(before["Dhcp"]);
        if (dhcp is not ("Enabled" or "Disabled")) throw new InvalidDataException("modo DHCP inválido");
        var staticDns = Text(before["StaticDns"]);
        foreach (var server in staticDns.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)) Ipv4(server);
        var addresses = new JsonArray();
        foreach (var item in Items(before["Addresses"]))
        {
            var prefix = Number(item?["PrefixLength"]);
            if (prefix is < 0 or > 32) throw new InvalidDataException("prefixo IPv4 inválido");
            addresses.Add(new JsonObject { ["IPAddress"] = Ipv4(Text(item?["IPAddress"])), ["PrefixLength"] = prefix, ["PrefixOrigin"] = Text(item?["PrefixOrigin"]), ["AddressState"] = Text(item?["AddressState"]) });
        }
        var routes = new JsonArray();
        foreach (var item in Items(before["Routes"]))
        {
            var metric = Number(item?["RouteMetric"]);
            if (metric is < 0 or > 9999) throw new InvalidDataException("métrica de rota inválida");
            routes.Add(new JsonObject { ["NextHop"] = Ipv4(Text(item?["NextHop"])), ["RouteMetric"] = metric });
        }
        var dns = new JsonArray();
        foreach (var item in Items(before["Dns"])) dns.Add(Ipv4(Text(item)));
        return new JsonObject
        {
            ["Index"] = record.Index, ["Name"] = Text(before["Name"]), ["Guid"] = guid, ["Dhcp"] = dhcp, ["StaticDns"] = staticDns,
            ["Dns"] = dns, ["Addresses"] = addresses, ["Routes"] = routes
        }.ToJsonString();
    }

    public static string Describe(InterruptedNetworkTransaction item)
    {
        using var before = JsonDocument.Parse(item.Before ?? "{}");
        var root = before.RootElement;
        var name = SafeName(root.TryGetProperty("Name", out var n) ? n.GetString() : "");
        var dhcp = root.GetProperty("Dhcp").GetString() == "Enabled";
        var addresses = string.Join(", ", root.GetProperty("Addresses").EnumerateArray().Where(a => a.GetProperty("PrefixOrigin").GetString() is "Manual" or "1").Select(a => a.GetProperty("IPAddress").GetString() + "/" + a.GetProperty("PrefixLength").GetInt32()));
        var gateways = string.Join(", ", root.GetProperty("Routes").EnumerateArray().Select(r => r.GetProperty("NextHop").GetString()));
        var dns = string.IsNullOrWhiteSpace(root.GetProperty("StaticDns").GetString()) ? "automático" : string.Join(", ", root.GetProperty("Dns").EnumerateArray().Select(d => d.GetString()));
        var phase = item.Phase switch
        {
            "applying" => "durante a aplicação da mudança",
            "restoring" => "durante a restauração",
            _ => "enquanto aguardava sua confirmação"
        };
        var ipv4 = item.Record.Action == "set_dns" ? "" : dhcp ? "\nIPv4: automático (DHCP)" : $"\nIPv4: {(addresses.Length == 0 ? "sem endereço manual" : addresses)}; gateway: {(gateways.Length == 0 ? "nenhum" : gateways)}";
        return $"Uma alteração de rede iniciada em {item.Record.Created.ToLocalTime():g} foi interrompida {phase}, sem resultado registrado. A configuração atual pode estar diferente da anterior.\n\nConfiguração anterior salva para a interface #{item.Record.Index} {name}:{ipv4}\nDNS: {dns}\n\nAntes de restaurar, o Nexo confere se a interface ainda é a mesma e lê o estado atual. Se ele já corresponder ao anterior, nada é alterado. A restauração pede permissão de administrador.";
    }

    public static async Task<string> Recover(InterruptedNetworkTransaction item, Func<ChangeRequest, Task<bool>> confirm, Func<string, IElevatedProcess> start, TimeSpan timeout, CancellationToken token)
    {
        if (item.Resolution != null) return item.Resolution;
        if (item.Before == null) throw new InvalidOperationException("Transação sem backup validado.");
        var resultPath = Path.Combine(item.Directory, ResultFile);
        var backup = Path.Combine(item.ProtectedDirectory ?? item.Directory, "before.json");
        bool accepted;
        try { accepted = await confirm(new ChangeRequest("Restaurar configuração de rede anterior?", Describe(item))); }
        catch (Exception ex) { return Serialize(new { success = false, restored = false, uncertain = true, error = "Confirmação indisponível: " + ex.Message + ". Nada foi alterado; a restauração será oferecida novamente.", backup }); }
        if (!accepted)
        {
            var declined = Serialize(new { success = false, restored = false, declined = true, message = "Você optou por não restaurar a configuração anterior. A configuração atual não foi alterada nem conferida; o backup continua salvo.", backup, recordedAt = DateTimeOffset.UtcNow });
            await WriteTextAtomic(resultPath, declined);
            return declined;
        }
        using var source = typeof(NetworkRecovery).Assembly.GetManifestResourceStream("Nexo.NetworkTransaction.ps1") ?? throw new IOException("Executor de rede ausente.");
        using var reader = new StreamReader(source);
        var request = new NetworkRequest(item.Record.Action, item.Record.Index, "", [], "", 0, "") { ExpectedGuid = item.Record.ExpectedGuid };
        // O estado anterior não vai no script: o processo elevado lê a cópia protegida e confere dono e permissões (#10).
        var script = "$requestJson=" + Decode(JsonSerializer.Serialize(request)) + "; $recoveryMode=$true; $operationId=" + WindowsTools.Quote(Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Directory))) + "; $operationDir=" + WindowsTools.Quote(item.Directory) + ";\n" + await reader.ReadToEndAsync(token);
        IElevatedProcess process;
        try { process = start(script); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { return Serialize(new { success = false, restored = false, uncertain = true, error = "A restauração não foi iniciada: " + ex.Message + ". Nada foi alterado; a restauração será oferecida novamente.", backup }); }
        using (process)
        {
            WriteRecord(item.Directory, item.Record with { RecoveryProcessId = process.Id });
            // O processo elevado grava o resultado só na pasta protegida; a cópia para %LOCALAPPDATA% é feita aqui, sem
            // elevação (#10).
            var elevatedResult = Path.Combine(item.ProtectedDirectory ?? item.Directory, ResultFile);
            var clock = Stopwatch.StartNew();
            while (!File.Exists(elevatedResult) && !process.HasExited && clock.Elapsed < timeout) await Task.Delay(250, CancellationToken.None);
            if (!File.Exists(elevatedResult) && process.HasExited) await Task.Delay(250, CancellationToken.None);
            if (File.Exists(elevatedResult))
            {
                var text = await File.ReadAllTextAsync(elevatedResult, CancellationToken.None);
                if (elevatedResult != resultPath) await WriteTextAtomic(resultPath, text);
                return text;
            }
            var running = !process.HasExited;
            var unconfirmed = Serialize(new { success = false, restored = false, uncertain = true, state = "unknown", processRunning = running, error = (running ? "A restauração não respondeu no prazo e ainda está em execução. " : "O processo de restauração terminou sem registrar resultado. ") + "O estado da rede não pôde ser confirmado; a restauração será oferecida novamente na próxima abertura do Nexo.", backup, recordedAt = DateTimeOffset.UtcNow });
            await WriteTextAtomic(Path.Combine(item.Directory, "recovery-unconfirmed.json"), unconfirmed);
            return unconfirmed;
        }
    }

    public static string Explain(string outcome)
    {
        try
        {
            using var json = JsonDocument.Parse(outcome);
            var root = json.RootElement;
            var text = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? "A restauração da configuração de rede não foi concluída. " + e.GetString() : "Resultado da restauração não reconhecido.";
            return root.TryGetProperty("backup", out var b) && b.ValueKind == JsonValueKind.String && !text.Contains("backup continua") ? text + "\nBackup: " + b.GetString() : text;
        }
        catch (JsonException) { return "Resultado da restauração ilegível; o estado da rede não foi confirmado."; }
    }

    public static bool Restored(string outcome)
    {
        try
        {
            using var json = JsonDocument.Parse(outcome);
            var root = json.RootElement;
            return root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True
                && (root.TryGetProperty("restored", out var r) && r.ValueKind == JsonValueKind.True || root.TryGetProperty("alreadyRestored", out var a) && a.ValueKind == JsonValueKind.True);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// A pasta protegida e os dois arquivos só valem se nenhum deles é link, todos têm dono SYSTEM, Administradores ou
    /// TrustedInstaller, as pastas não herdam permissões e nenhuma outra conta tem direito de escrita, exclusão, troca de
    /// permissão ou de dono. A pasta "operations" e a "NexoSupport" acima dela também são conferidas.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool TrustedOnWindows(string directory) => UntrustedReasonOnWindows(directory) == null;

    /// <summary>Motivo da recusa da cópia protegida (caminho, regra, dono e ACE), ou null quando ela é confiável.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? UntrustedReasonOnWindows(string directory) => UntrustedReasonOnWindows(directory, ["before.json", RecordFile]);

    [SupportedOSPlatform("windows")]
    internal static bool TrustedFileOnWindows(string directory, string file) => UntrustedReasonOnWindows(directory, [file]) == null;

    [SupportedOSPlatform("windows")]
    internal static string? UntrustedReasonOnWindows(string directory, string[] files)
    {
        try
        {
            var operations = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory))!;
            var folders = new FileSystemInfo[] { new DirectoryInfo(Path.GetDirectoryName(operations)!), new DirectoryInfo(operations), new DirectoryInfo(directory) };
            var items = folders.Concat(files.Select(file => new FileInfo(Path.Combine(directory, file))));
            foreach (var item in items)
            {
                if (!item.Exists) return item.FullName + ": não existe";
                if (UntrustedItem(item) is { } reason) return reason;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException) { return directory + ": não foi possível ler a ACL (" + ex.GetType().Name + ": " + ex.Message + ")"; }
    }

    /// <summary>
    /// #97: confere só as pastas da cadeia (NexoSupport e operations) que já existem, com as mesmas regras da cópia
    /// protegida. Pasta que não existe é criada pelo executor e não é problema; a primeira ausente encerra a conferência.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static string? ProtectedChainProblemOnWindows(string protectedRoot)
    {
        var operations = Path.TrimEndingDirectorySeparator(protectedRoot);
        try
        {
            foreach (var folder in new[] { Path.GetDirectoryName(operations)!, operations })
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(folder); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
                if (!attributes.HasFlag(FileAttributes.Directory)) return folder + ": não é pasta";
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return folder + ": é link (reparse point)";
                var item = new DirectoryInfo(folder);
                if (UntrustedItem(item) is { } reason) return reason;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException) { return operations + ": não foi possível ler a ACL (" + ex.GetType().Name + ": " + ex.Message + ")"; }
    }

    [SupportedOSPlatform("windows")]
    private static string? UntrustedItem(FileSystemInfo item)
    {
        if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) return item.FullName + ": é link (reparse point)";
        FileSystemSecurity security = item is DirectoryInfo folder ? folder.GetAccessControl() : ((FileInfo)item).GetAccessControl();
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || !TrustedSid(owner)) return item.FullName + ": dono não confiável (" + Account(owner) + ")";
        // As pastas precisam estar com a herança desligada; os arquivos são criados já com o descritor explícito.
        if (item is DirectoryInfo && !security.AreAccessRulesProtected) return item.FullName + ": pasta herda permissões (dono " + Account(owner) + ")";
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                && ((long)rule.FileSystemRights & WriteRights) != 0 && !(rule.IdentityReference is SecurityIdentifier sid && TrustedSid(sid)))
                return item.FullName + ": ACE de escrita para " + Account(rule.IdentityReference as SecurityIdentifier) + " (" + rule.FileSystemRights + (rule.IsInherited ? ", herdada" : "") + "; dono " + Account(owner) + ")";
        return null;
    }

    /// <summary>Informa a condição atual da pasta sem atribuir a ela uma saída sem resultado do executor.</summary>
    internal static string HostileChainMessage(string reason) =>
        "O executor terminou sem resultado e a cadeia %ProgramData%\\NexoSupport, onde o Nexo guarda o backup protegido, tem uma condição que bloqueia esta operação (" + reason + "). "
        + "Ela pode ter impedido a troca; não há confirmação do estado atual da rede. Confira a configuração antes de repetir. "
        + (reason.Contains(": não é pasta", StringComparison.Ordinal)
            ? "Há um arquivo no lugar de uma das pastas. Peça a um administrador para conferir e remover apenas o arquivo indicado; os comandos rmdir para pastas do README, seção \"Rede e recuperação\", não se aplicam a esse arquivo."
            : "Para liberar, siga o README do Nexo, seção \"Rede e recuperação\": num prompt como administrador, rode takeown /F \"%ProgramData%\\NexoSupport\" /A /R /D S (em Windows em inglês, /D Y), "
            + "icacls \"%ProgramData%\\NexoSupport\" /reset /T e rmdir /S /Q \"%ProgramData%\\NexoSupport\". Se ela for um link (junção), use só rmdir \"%ProgramData%\\NexoSupport\", sem /S, para não apagar o destino.");

    [SupportedOSPlatform("windows")]
    private static string Account(SecurityIdentifier? sid)
    {
        if (sid == null) return "desconhecido";
        try { return sid.Translate(typeof(NTAccount)).Value + " " + sid.Value; }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException) { return sid.Value; }
    }

    // WriteData/CreateFiles, AppendData/CreateDirectories, WriteExtendedAttributes, DeleteSubdirectoriesAndFiles,
    // WriteAttributes, Delete, ChangePermissions, TakeOwnership, GENERIC_ALL e GENERIC_WRITE.
    private const long WriteRights = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    [SupportedOSPlatform("windows")]
    private static bool TrustedSid(SecurityIdentifier sid)
        => sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.Value == TrustedInstaller;

    private static bool SameGuid(string first, string second) => Guid.TryParse(first, out var a) && Guid.TryParse(second, out var b) && a == b;

    // O nome do adaptador vai para a janela de aprovação: só letras, dígitos e pontuação comum de nomes de interface.
    internal static string SafeName(string? value)
        => new string((value ?? "").Take(80).Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')' or '#' ? c : '_').ToArray()).Trim();

    private static bool Running(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase)) return false;
            try { return !process.HasExited; } catch { return true; }
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static InterruptedNetworkTransaction Resolved(string directory, NetworkTransactionRecord record, string phase, object resolution)
    {
        var text = Serialize(resolution);
        WriteTextAtomic(Path.Combine(directory, ResultFile), text).GetAwaiter().GetResult();
        return new InterruptedNetworkTransaction(directory, record, phase, null, text);
    }

    private static string ReadDecision(string directory)
    {
        var path = Path.Combine(directory, "decision.txt");
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch (IOException) { return ""; }
    }

    private static string Decode(string json) => "[System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "'))";
    private static string Serialize(object value) => JsonSerializer.Serialize(value);
    private static void WriteJson(string path, object value) => WriteTextAtomic(path, Serialize(value)).GetAwaiter().GetResult();
    internal static async Task WriteTextAtomic(string path, string text)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, text, CancellationToken.None).ConfigureAwait(false);
        File.Move(temporary, path, true);
    }

    private static IEnumerable<JsonNode?> Items(JsonNode? node) => node switch
    {
        null => [],
        JsonArray array => array,
        _ => [node]
    };
    private static string Text(JsonNode? node) => node is JsonValue value ? value.TryGetValue<string>(out var s) ? s : value.ToJsonString().Trim('"') : "";
    private static int Number(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var number)) return number;
            if (value.TryGetValue<long>(out var wide) && wide is >= int.MinValue and <= int.MaxValue) return (int)wide;
            if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed)) return parsed;
        }
        throw new InvalidDataException("número ausente ou inválido");
    }
    private static string Ipv4(string value)
    {
        if (value.Count(c => c == '.') != 3 || !IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork) throw new InvalidDataException("endereço IPv4 inválido: " + value);
        return address.ToString();
    }
}
