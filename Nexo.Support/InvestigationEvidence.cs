using System.Net;
using System.Text.Json;

namespace Nexo.Support;

public sealed class InvestigationEvidence
{
    private readonly Dictionary<string, (string Result, int Count)> observations = [];
    private readonly Dictionary<string, (string Host, bool Dns, bool Tcp)> connections = [];
    private readonly List<string> changes = [];
    public static bool ExplicitlyUnchanged(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return false;
        foreach (var field in result.EnumerateObject())
        {
            if (field.Name.Equals("changed", StringComparison.OrdinalIgnoreCase) && field.Value.ValueKind == JsonValueKind.False) return true;
            if (field.Name is "data" or "Activation" or "Change" && ExplicitlyUnchanged(field.Value)) return true;
        }
        return false;
    }
    public bool Repeated(string signature) => observations.TryGetValue(signature, out var state) && state.Count >= 2;
    public void Observe(string name, string signature, JsonElement args, string result)
    {
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
        {
            if (ToolCatalog.IsMutation(name) && !ExplicitlyUnchanged(root)) { changes.Add(ToolCatalog.Label(name).TrimEnd('…')); observations.Clear(); connections.Clear(); }
            if (name == "wait_for_reconnect" && root.TryGetProperty("data", out var watch) && watch.TryGetProperty("Reconnected", out var reconnect) && reconnect.ValueKind == JsonValueKind.True) { observations.Clear(); connections.Clear(); }
            if (name == "test_connection" && root.TryGetProperty("data", out var data))
            {
                var host = data.TryGetProperty("host", out var h) ? h.GetString() ?? "destino" : args.TryGetProperty("host", out h) ? h.GetString() ?? "destino" : "destino";
                bool Flag(string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
                connections[signature] = (host, Flag("dnsResolved"), Flag("tcpConnected"));
            }
        }
        // Ignore durations: a new timestamp is not new diagnostic evidence.
        var fingerprint = name == "test_connection" && root.TryGetProperty("data", out var test)
            ? JsonSerializer.Serialize(new { dns = test.TryGetProperty("dnsResolved", out var d) ? d.ToString() : "", tcp = test.TryGetProperty("tcpConnected", out var t) ? t.ToString() : "" }) : result;
        observations[signature] = (fingerprint, observations.TryGetValue(signature, out var old) && old.Result == fingerprint ? old.Count + 1 : 1);
    }
    public string Summary(string reason = "")
    {
        var lines = new List<string>();
        var tests = connections.Values.ToArray();
        foreach (var test in tests.Distinct())
        {
            var numeric = IPAddress.TryParse(test.Host, out _);
            lines.Add(test.Tcp ? $"Conexão TCP com {test.Host}: funcionando." : !numeric && !test.Dns ? $"Resolução DNS de {test.Host}: não confirmada no teste." : $"Conexão TCP com {test.Host}: não confirmada no teste.");
        }
        if (tests.Any(t => t.Tcp) && tests.Any(t => !t.Tcp)) lines.Add("Há conectividade parcial: os testes não permitem afirmar que toda a internet está indisponível ou que todos os serviços funcionam.");
        if (tests.Length > 0 && tests.All(t => t.Tcp)) lines.Add("Os destinos testados responderam. Confirme também o aplicativo em que percebeu o problema.");
        lines.Add(changes.Count == 0 ? "Nenhuma alteração concluída pelo Nexo foi registrada neste turno." : "Operações concluídas neste turno: " + string.Join("; ", changes) + ".");
        if (reason.Length > 0) lines.Add(reason);
        return string.Join("\n", lines);
    }
}
