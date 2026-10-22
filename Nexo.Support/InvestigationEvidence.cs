using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexo.Support;

public sealed class InvestigationEvidence
{
    private readonly Dictionary<string, (string Result, int Count)> observations = [];
    private readonly Dictionary<string, (string Host, bool Dns, bool Tcp)> connections = [];
    private readonly List<string> changes = [];
    private readonly HashSet<string> successfulTools = [];
    private readonly HashSet<string> successfulChanges = [];
    private readonly HashSet<string> observedTopics = [];
    private readonly Dictionary<string, (string Topic, string Name, int Code, string[] HardwareIds, string[] CompatibleIds)> observedDevices = [];
    private readonly Dictionary<string, bool> observedPrinters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Title, string HardwareId)> observedUpdates = [];
    private readonly HashSet<string> failedTools = [];
    public string? DeviceTopic(string id) => observedDevices.TryGetValue(id, out var device) ? device.Topic : null;
    public bool DeviceTargetUnambiguous(string id, string request, bool enabling)
    {
        if (!observedDevices.TryGetValue(id, out var target)) return false;
        var candidates = observedDevices.Values.Where(device => device.Topic == target.Topic && (!enabling || device.Code == 22)).ToArray();
        if (UncertainTarget(request) && observedDevices.Values.Count(device => device.Topic == target.Topic) > 1) return false;
        if (candidates.Length == 1) return true;
        return UserNamedExactlyOneTarget(request, candidates.Select(device => device.Name), target.Name);
    }
    public bool PrinterTargetUnambiguous(string name, string request)
    {
        if (!observedPrinters.ContainsKey(name)) return false;
        var candidates = PrinterCandidates();
        if (UncertainTarget(request) && candidates.Length > 1) return false;
        if (candidates.Length == 1) return candidates[0].Equals(name, StringComparison.OrdinalIgnoreCase);
        return UserNamedExactlyOneTarget(request, candidates, name);
    }
    private static bool UncertainTarget(string request) => Regex.IsMatch(AgentProtocol.Normalize(request),
        @"\b(nao sei qual|nao sei se|nao tenho certeza|qual deles|qual delas)\b");

    private static bool UserNamedExactlyOneTarget(string request, IEnumerable<string> names, string target)
    {
        var normalizedTarget = AgentProtocol.Normalize(target);
        if (normalizedTarget.Length < 6) return false;
        var normalizedRequest = AgentProtocol.Normalize(request);
        var mentioned = names.Select(AgentProtocol.Normalize)
            .Where(name => name.Length >= 6 && normalizedRequest.Contains(name, StringComparison.Ordinal)).ToArray();
        return mentioned.Length == 1 && mentioned[0] == normalizedTarget;
    }
    private string[] PrinterCandidates()
    {
        var relevant = observedPrinters.Where(printer => printer.Value).Select(printer => printer.Key).ToArray();
        return relevant.Length > 0 ? relevant : observedPrinters.Keys.ToArray();
    }
    public string? AmbiguityQuestion(string topic, string request)
    {
        if (topic == "printer")
        {
            var printerCandidates = PrinterCandidates();
            if (printerCandidates.Length <= 1 || printerCandidates.Any(name => PrinterTargetUnambiguous(name, request))) return null;
            return "Encontrei mais de uma impressora com indício de falha: " + string.Join("; ", printerCandidates.Order(StringComparer.CurrentCultureIgnoreCase)) + ". Qual delas recebeu o documento que falhou?";
        }
        var candidates = observedDevices.Where(device => device.Value.Topic == topic && device.Value.Code == 22).ToArray();
        if (candidates.Length <= 1 || candidates.Any(device => DeviceTargetUnambiguous(device.Key, request, true))) return null;
        var category = topic switch { "camera" => "câmera desabilitada", "audio" => "dispositivo de áudio desabilitado", "bluetooth" => "dispositivo Bluetooth desabilitado", _ => "dispositivo desabilitado" };
        var prefix = topic == "camera" ? "Encontrei mais de uma " : "Encontrei mais de um ";
        var pronoun = topic == "camera" ? "delas" : "deles";
        return prefix + category + ": " + string.Join("; ", candidates.Select(device => device.Value.Name).Order(StringComparer.CurrentCultureIgnoreCase)) + ". Qual " + pronoun + " apresenta a falha no aplicativo?";
    }
    public bool UpdateMatchesIncident(string id, HashSet<string> incidentTopics)
    {
        if (!observedUpdates.TryGetValue(id, out var update)) return false;
        return update.HardwareId.Length > 0 && observedDevices.Values.Any(device => incidentTopics.Contains(device.Topic)
            && device.HardwareIds.Concat(device.CompatibleIds).Any(hardwareId => string.Equals(hardwareId, update.HardwareId, StringComparison.OrdinalIgnoreCase)));
    }
    public bool UpdateTargetUnambiguous(string id, HashSet<string> incidentTopics, string request)
    {
        if (!UpdateMatchesIncident(id, incidentTopics)) return false;
        var candidates = observedUpdates.Keys.Where(candidate => UpdateMatchesIncident(candidate, incidentTopics)).ToArray();
        if (candidates.Length == 1) return true;
        var title = AgentProtocol.Normalize(observedUpdates[id].Title);
        return title.Length >= 6 && AgentProtocol.Normalize(request).Contains(title, StringComparison.Ordinal)
            && candidates.Count(candidate => AgentProtocol.Normalize(observedUpdates[candidate].Title) == title) == 1;
    }
    public bool HasSuccessfulTool(string name) => successfulTools.Contains(name);
    public bool HasSuccessfulMutation => successfulChanges.Count > 0;
    public string? ValidateStatement(string message, bool userConfirmed)
    {
        var text = AgentProtocol.Normalize(message);
        if (AgentProtocol.ClaimsExecution(message) && successfulTools.Count == 0)
            return "Não há resultado bem sucedido de ferramenta para sustentar essa afirmação.";
        if (Regex.IsMatch(text, @"(?<!nao )\b(corrigi|resolvi)\b|\b(problema|falha)\s+(foi|esta|ficou)?\s*(resolvid[oa]|corrigid[oa]|solucionad[oa])\b|\bvoltou a funcionar\b") && !userConfirmed)
            return "A função afetada ainda não foi confirmada pelo usuário. Descreva somente a operação e o teste realizados, e peça o teste funcional.";
        var claims = new (string Pattern, string[] Tools)[]
        {
            (@"(?<!nao )\b(limpei)\b|\b(cache dns|fila de impressao)\s+(foi\s+)?limp[oa]\b", ["flush_dns", "clear_print_queue"]),
            (@"(?<!nao )\breiniciei\b|\bservico\s+(foi\s+)?reiniciado\b", ["restart_service", "restart_device"]),
            (@"(?<!nao )\b(instalei|atualizei)\b|\bdriver\s+(foi\s+)?instalado\b", ["install_driver_update"]),
            (@"(?<!nao )\bconfigurei\b", ["set_dns", "set_ipv4"]),
            (@"(?<!nao )\b(ativei|habilitei)\b", ["enable_network_adapter", "enable_camera", "enable_device", "allow_camera"]),
            (@"(?<!nao )\bdesativei\b", []),
        };
        foreach (var (pattern, tools) in claims)
            if (Regex.IsMatch(text, pattern) && !tools.Any(successfulChanges.Contains))
                return "A resposta atribui ao Nexo uma ação sem resultado success=true da ferramenta correspondente.";
        if (Regex.IsMatch(text, @"(?<!nao )\b(ajustei|alterei|mudei|apliquei|corrigi)\b") && !HasSuccessfulMutation)
            return "Nenhuma alteração concluída foi registrada neste turno.";
        if (AgentProtocol.ClaimsExecution(message))
        {
            var mentioned = SupportIntake.IncidentTopics(message);
            if (mentioned.Any(topic => !observedTopics.Contains(topic)))
                return "A resposta descreve uma verificação de componente sem resultado bem sucedido dessa categoria.";
        }
        return null;
    }
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
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Resultado da ferramenta deve ser um objeto JSON.");
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
        {
            failedTools.Remove(name);
            successfulTools.Add(name);
            switch (name)
            {
                case "get_network" or "test_connection" or "probe_network_interfaces" or "enable_network_adapter" or "set_dns" or "set_ipv4" or "flush_dns": observedTopics.Add("network"); break;
                case "get_printers" or "clear_print_queue": observedTopics.Add("printer"); break;
                case "get_processes" or "get_recent_errors": observedTopics.Add("system"); break;
                case "enable_camera" or "allow_camera": observedTopics.Add("camera"); break;
            }
            if ((name is "diagnose_device" or "get_hardware_inventory") && args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty(name == "diagnose_device" ? "topic" : "category", out var category) && category.ValueKind == JsonValueKind.String)
            {
                var topic = category.GetString() ?? "";
                if (name == "get_hardware_inventory" && (topic is "problem" or "display" or "storage" or "usb")) topic = "hardware";
                observedTopics.Add(topic);
                if (root.TryGetProperty("data", out var deviceData) && deviceData.ValueKind == JsonValueKind.Object
                    && (deviceData.TryGetProperty("devices", out var devices) || deviceData.TryGetProperty("Devices", out devices)) && devices.ValueKind == JsonValueKind.Array)
                    foreach (var device in devices.EnumerateArray())
                        if (device.ValueKind == JsonValueKind.Object && device.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } value)
                            observedDevices[value] = (topic,
                                device.TryGetProperty("Name", out var display) && display.ValueKind == JsonValueKind.String ? display.GetString() ?? "" : "",
                                device.TryGetProperty("Code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var numericCode) ? numericCode : -1,
                                TextArray(device, "HardwareIds"), TextArray(device, "CompatibleIds"));
            }
            if (name == "get_printers" && root.TryGetProperty("data", out var printerData) && printerData.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using var list = JsonDocument.Parse(printerData.GetString() ?? "");
                    if (list.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var printer in list.RootElement.EnumerateArray())
                            if (printer.ValueKind == JsonValueKind.Object && printer.TryGetProperty("Name", out var printerName) && printerName.ValueKind == JsonValueKind.String && printerName.GetString() is { Length: > 0 } exactName)
                            {
                                var jobs = printer.TryGetProperty("Jobs", out var queue) && (queue.ValueKind == JsonValueKind.Array && queue.GetArrayLength() > 0
                                    || queue.ValueKind == JsonValueKind.Number && queue.TryGetInt32(out var jobCount) && jobCount > 0);
                                var error = printer.TryGetProperty("Status", out var state) && state.ValueKind == JsonValueKind.String
                                    && state.GetString() is { } status && (status.Contains("error", StringComparison.OrdinalIgnoreCase) || status.Contains("offline", StringComparison.OrdinalIgnoreCase));
                                observedPrinters[exactName] = jobs || error;
                            }
                }
                catch (JsonException) { /* An unusable inventory cannot identify a queue. */ }
            }
            if (name == "scan_driver_updates" && root.TryGetProperty("data", out var updates) && updates.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using var list = JsonDocument.Parse(updates.GetString() ?? "");
                    if (list.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var update in list.RootElement.EnumerateArray())
                            if (update.ValueKind == JsonValueKind.Object && update.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } updateId)
                                observedUpdates[updateId] = (update.TryGetProperty("Title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() ?? "" : "",
                                    update.TryGetProperty("HardwareID", out var hardware) && hardware.ValueKind == JsonValueKind.String ? hardware.GetString() ?? "" : "");
                }
                catch (JsonException) { /* An unusable update list cannot authorize installation. */ }
            }
            if (name is "enable_device" or "restart_device" && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("device_id", out var selected) && selected.ValueKind == JsonValueKind.String && observedDevices.TryGetValue(selected.GetString() ?? "", out var selectedDevice))
                observedTopics.Add(selectedDevice.Topic);
            if (ToolCatalog.IsMutation(name) && !ExplicitlyUnchanged(root)) { successfulChanges.Add(name); changes.Add(ToolCatalog.Label(name).TrimEnd('…')); observations.Clear(); connections.Clear(); }
            if (name == "wait_for_reconnect" && root.TryGetProperty("data", out var watch) && watch.TryGetProperty("Reconnected", out var reconnect) && reconnect.ValueKind == JsonValueKind.True) { observations.Clear(); connections.Clear(); }
            if (name == "test_connection" && root.TryGetProperty("data", out var data))
            {
                var host = data.TryGetProperty("host", out var h) ? h.GetString() ?? "destino" : args.TryGetProperty("host", out h) ? h.GetString() ?? "destino" : "destino";
                bool Flag(string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
                connections[signature] = (host, Flag("dnsResolved"), Flag("tcpConnected"));
            }
        }
        else failedTools.Add(name);
        // Ignore durations: a new timestamp is not new diagnostic evidence.
        var fingerprint = name == "test_connection" && root.TryGetProperty("data", out var test)
            ? JsonSerializer.Serialize(new { dns = test.TryGetProperty("dnsResolved", out var d) ? d.ToString() : "", tcp = test.TryGetProperty("tcpConnected", out var t) ? t.ToString() : "" }) : result;
        observations[signature] = (fingerprint, observations.TryGetValue(signature, out var old) && old.Result == fingerprint ? old.Count + 1 : 1);
    }
    private static string[] TextArray(JsonElement source, string name)
        => source.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString() ?? "").Where(value => value.Length > 0).ToArray()
            : [];
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
        if (failedTools.Count > 0) lines.Add("Ferramentas sem conclusão bem sucedida: " + string.Join(", ", failedTools.Order()) + ". Confira os cartões de resultado para a causa.");
        if (reason.Length > 0) lines.Add(reason);
        return string.Join("\n", lines);
    }
}
