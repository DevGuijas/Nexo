using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexo.Support;

public sealed record NetworkTransactionRecord(string Action, int Index, string ExpectedGuid, DateTimeOffset Created, int? ProcessId = null, int? RecoveryProcessId = null);

public sealed record InterruptedNetworkTransaction(string Directory, NetworkTransactionRecord Record, string Phase, string? Before, string? Resolution);

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

    public static IElevatedProcess StartElevated(string script) => new ElevatedProcess(WindowsTools.StartElevated(script));

    public static void WriteRecord(string directory, NetworkTransactionRecord record) => WriteJson(Path.Combine(directory, RecordFile), record);

    public static List<InterruptedNetworkTransaction> Scan(string root, DateTimeOffset now, Func<int, bool>? isRunning = null)
    {
        var found = new List<InterruptedNetworkTransaction>();
        var operations = Path.Combine(root, "operations");
        if (!System.IO.Directory.Exists(operations)) return found;
        foreach (var directory in System.IO.Directory.EnumerateDirectories(operations).OrderBy(d => System.IO.Directory.GetCreationTimeUtc(d)))
            if (Inspect(directory, now, isRunning) is { } item) found.Add(item);
        return found;
    }

    public static InterruptedNetworkTransaction? Inspect(string directory, DateTimeOffset now, Func<int, bool>? isRunning = null, bool exited = false)
    {
        isRunning ??= Running;
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
        var applied = File.Exists(Path.Combine(directory, "applied.json"));
        var phase = !applied ? "applying" : decision == "rollback" ? "restoring" : "confirming";
        var beforePath = Path.Combine(directory, "before.json");
        if (!File.Exists(beforePath))
            return Resolved(directory, record, "before-backup", new { success = true, restored = false, unchanged = true, message = "A transação de rede foi interrompida antes de salvar o estado anterior; o executor não chegou a alterar a rede. Nenhuma restauração é necessária." });
        if (decision == "keep")
            return Resolved(directory, record, "keeping", new { success = false, restored = false, uncertain = true, message = "Você confirmou manter a nova configuração de rede, mas o executor terminou antes de registrar o resultado. A configuração atual não foi conferida; consulte a rede antes de outra alteração.", backup = beforePath });
        try { return new InterruptedNetworkTransaction(directory, record, phase, Validate(File.ReadAllText(beforePath), record), null); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or IOException)
        {
            return Resolved(directory, record, phase, new { success = false, restored = false, uncertain = true, message = "O backup da configuração anterior não pôde ser validado (" + ex.Message + "). A restauração automática não foi oferecida; confira a rede manualmente.", backup = beforePath });
        }
    }

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
        var name = root.TryGetProperty("Name", out var n) ? n.GetString() : "";
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
        var backup = Path.Combine(item.Directory, "before.json");
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
        var script = "$requestJson=" + Decode(JsonSerializer.Serialize(request)) + "; $recoveryBeforeJson=" + Decode(item.Before) + "; $operationDir=" + WindowsTools.Quote(item.Directory) + ";\n" + await reader.ReadToEndAsync(token);
        IElevatedProcess process;
        try { process = start(script); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { return Serialize(new { success = false, restored = false, uncertain = true, error = "A restauração não foi iniciada: " + ex.Message + ". Nada foi alterado; a restauração será oferecida novamente.", backup }); }
        using (process)
        {
            WriteRecord(item.Directory, item.Record with { RecoveryProcessId = process.Id });
            var clock = Stopwatch.StartNew();
            while (!File.Exists(resultPath) && !process.HasExited && clock.Elapsed < timeout) await Task.Delay(250, CancellationToken.None);
            if (!File.Exists(resultPath) && process.HasExited) await Task.Delay(250, CancellationToken.None);
            if (File.Exists(resultPath)) return await File.ReadAllTextAsync(resultPath, CancellationToken.None);
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
    private static async Task WriteTextAtomic(string path, string text)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, text, CancellationToken.None);
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
