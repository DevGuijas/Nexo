using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexo.Support;

// Travas do controlador que impedem uma chamada de chegar ao executor.
public enum ControlBlock { RepeatedEvidence, RepeatedFailure, Topic, UninformedDnsServers }

public sealed class InvestigationEvidence
{
    private const int MaxFindings = 8;
    private const int MaxFindingLength = 320;
    private const int MaxNameLength = 60;
    private const int MaxEvidenceLength = 120;
    private const int MaxListed = 5;
    private readonly Dictionary<string, (string Result, int Count)> observations = [];
    // O que as consultas observaram, já em texto curto para o usuário; a chave substitui a leitura anterior do mesmo assunto.
    private readonly List<(string Key, string Text, string Guidance)> findings = [];
    private readonly List<(string Name, long Megabytes, bool Responding)> topProcesses = [];
    // Processos das ocorrências de memória do relatório de saúde (#88): os que persistem, com os MB do alerta e da leitura
    // atual, e os nomes que só aparecem em ocorrência que não é atual (resolvida, histórica ou informativa).
    private readonly List<(string Name, int Pid, long From, long To, long? Now)> memoryHolders = [];
    private readonly HashSet<string> staleProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, ControlBlock Kind, string? Topic)> blocks = [];
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
        var candidates = observedDevices.Where(device => device.Value.Topic == target.Topic && (!enabling || device.Value.Code == 22)).ToArray();
        if (UncertainTarget(request) && observedDevices.Values.Count(device => device.Topic == target.Topic) > 1) return false;
        // O alvo da ferramenta precisa ser o próprio candidato, pelo ID: outro dispositivo com o mesmo nome não conta.
        if (candidates.Length == 1) return candidates[0].Key == id;
        return UserNamedExactlyOneTarget(request, candidates.Select(device => (device.Key, device.Value.Name)), id, StringComparer.Ordinal);
    }
    public bool PrinterTargetUnambiguous(string name, string request)
    {
        if (!observedPrinters.ContainsKey(name)) return false;
        var candidates = PrinterCandidates();
        if (UncertainTarget(request) && candidates.Length > 1) return false;
        if (candidates.Length == 1) return candidates[0].Equals(name, StringComparison.OrdinalIgnoreCase);
        return UserNamedExactlyOneTarget(request, candidates.Select(candidate => (candidate, candidate)), name, StringComparer.OrdinalIgnoreCase);
    }
    private static bool UncertainTarget(string request) => Regex.IsMatch(AgentProtocol.Normalize(request),
        @"\b(nao sei qual|nao sei se|nao tenho certeza|qual deles|qual delas)\b");

    // Com mais de um alvo possível, só escolhe a mensagem que é apenas o nome de um candidato (#70): o nome inteiro, ou cortado
    // como a pergunta o mostra, entre aspas ou não, com no máximo um prefixo curto ("é a", "foi na", "sim,") e ponto final.
    // Qualquer outra palavra faz o Nexo perguntar de novo. Quatro revisões mostraram que uma lista de recusas ("não é a X",
    // "esquece a X", "a X tá ok"...) nunca fecha, e cada frase que escapa autoriza o alvo errado; perguntar de novo custa pouco.
    // A forma normalizada só encontra o candidato; a autorização compara a identidade dele (ID do dispositivo ou nome exato da
    // impressora) com o alvo da ferramenta, para que outro observado com nome parecido não passe pelo escolhido.
    private static bool UserNamedExactlyOneTarget(string request, IEnumerable<(string Key, string Name)> candidates, string target, StringComparer identity)
        => NamedCandidate(request, candidates) is { } chosen && identity.Equals(chosen, target);
    // A identidade (Key) do único candidato que a mensagem nomeia pela regra acima; null quando nenhum ou mais de um.
    // Também usada pela pergunta de dispositivo do fluxo de driver (#74).
    internal static string? NamedCandidate(string request, IEnumerable<(string Key, string Name)> candidates)
    {
        if (ReplyForms(request) is not { Count: > 0 } replies) return null;
        // Sem mínimo de tamanho (validação no PC, #70: "Fax" nunca era escolhida): a regra é a resposta inteira ser o nome, e o
        // comprimento não protege nada. O que protege é que um nome vazio ou igual a um prefixo da resposta ("sim", "é a") nunca
        // é escolhido, porque aí a resposta curta de confirmação casaria com ele.
        static bool Usable(string? form) => form is { Length: > 0 } && !ReservedForms.Contains(form);
        var chosen = candidates.Select(candidate => (candidate.Key, Full: NameForm(candidate.Name), Cut: CutNameForm(candidate.Name)))
            .Where(candidate => Usable(candidate.Full) && (replies.Contains(candidate.Full) || Usable(candidate.Cut) && replies.Contains(candidate.Cut!))).ToArray();
        return chosen.Length == 1 ? chosen[0].Key : null;
    }
    private static readonly string[] ReplyPrefixes = (from yes in new[] { "", "sim ", "sim, " } from verb in new[] { "", "e ", "foi " }
        from article in new[] { "", "a ", "o ", "na ", "no " } select yes + verb + article).Distinct().ToArray();
    // Os prefixos sozinhos, como a resposta normalizada os mostra ("sim", "sim,", "e a", "foi no"…), e as respostas que recusam
    // ou não escolhem ("não", "ok", "nenhuma", "qualquer uma"…): nunca são um nome, nem de uma impressora chamada assim.
    private static readonly HashSet<string> ReservedForms = ReplyPrefixes.Select(prefix => TrimReplyEnd(prefix.Trim()))
        .Concat(["nao", "ok", "nenhuma", "nenhum", "todas", "todos", "qualquer", "qualquer uma", "qualquer um"])
        .SelectMany(form => new[] { form, form.TrimEnd(',') }).Where(form => form.Length > 0).ToHashSet(StringComparer.Ordinal);
    // As formas possíveis do nome na resposta, já sem o prefixo, as aspas em volta e o ponto final. Caractere de controle ou
    // de formatação (largura zero, direção de texto) no meio da resposta não forma nome: a resposta não conta como escolha.
    private static HashSet<string>? ReplyForms(string request)
    {
        var trimmed = request.Trim();
        if (trimmed.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)) return null;
        var form = AgentProtocol.Normalize(Clean(trimmed, quotes: true));
        var forms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefix in ReplyPrefixes.Where(prefix => form.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var rest = TrimReplyEnd(form[prefix.Length..]);
            forms.Add(rest);
            if (rest.Length > 2 && rest[0] == '\'' && rest[^1] == '\'') forms.Add(TrimReplyEnd(rest[1..^1].Trim()));
        }
        forms.Remove("");
        return forms;
    }
    private static string TrimReplyEnd(string text) => Regex.Replace(text, @"[\s.!…]+$", "");
    private static string NameForm(string name) => TrimReplyEnd(AgentProtocol.Normalize(Clean(name, quotes: true)));
    // O nome como a pergunta o mostrou, quando ela o cortou em 60 caracteres; sem o corte, só o nome inteiro vale.
    private static string? CutNameForm(string name)
    {
        var clean = Clean(name, quotes: true);
        return clean.Length > MaxNameLength ? TrimReplyEnd(AgentProtocol.Normalize(Shorten(clean, MaxNameLength))) : null;
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
            // "Com indício de falha" só quando as candidatas têm fila com trabalhos ou estado de erro; sem nenhuma, são todas (#70).
            var flagged = observedPrinters.Values.Any(relevant => relevant);
            return "Encontrei mais de uma impressora" + (flagged ? " com indício de falha" : "") + ": " + string.Join("; ", printerCandidates.Order(StringComparer.CurrentCultureIgnoreCase).Select(name => Quoted(name))) + ". Qual delas recebeu o documento que falhou? " + ReplyWithName;
        }
        var candidates = observedDevices.Where(device => device.Value.Topic == topic && device.Value.Code == 22).ToArray();
        if (candidates.Length <= 1 || candidates.Any(device => DeviceTargetUnambiguous(device.Key, request, true))) return null;
        var category = topic switch { "camera" => "câmera desabilitada", "audio" => "dispositivo de áudio desabilitado", "bluetooth" => "dispositivo Bluetooth desabilitado", _ => "dispositivo desabilitado" };
        var prefix = topic == "camera" ? "Encontrei mais de uma " : "Encontrei mais de um ";
        var pronoun = topic == "camera" ? "delas" : "deles";
        return prefix + category + ": " + string.Join("; ", candidates.Select(device => device.Value.Name).Order(StringComparer.CurrentCultureIgnoreCase).Select(name => Quoted(name))) + ". Qual " + pronoun + " apresenta a falha no aplicativo? " + ReplyWithName;
    }
    internal const string ReplyWithName ="Responda só com o nome, como está escrito acima.";
    public bool UpdateMatchesIncident(string id, HashSet<string> incidentTopics)
    {
        if (!observedUpdates.TryGetValue(id, out var update)) return false;
        return update.HardwareId.Length > 0 && observedDevices.Values.Any(device => incidentTopics.Contains(device.Topic)
            && device.HardwareIds.Concat(device.CompatibleIds).Any(hardwareId => string.Equals(hardwareId, update.HardwareId, StringComparison.OrdinalIgnoreCase)));
    }
    // Ofertas de driver seguem a regra das impressoras e dispositivos (#72): com mais de uma oferta para o hardware do incidente,
    // só a resposta que é apenas o título de uma delas escolhe, e a autorização compara o update_id escolhido com o da chamada.
    public bool UpdateTargetUnambiguous(string id, HashSet<string> incidentTopics, string request)
    {
        if (!UpdateMatchesIncident(id, incidentTopics)) return false;
        var candidates = observedUpdates.Keys.Where(candidate => UpdateMatchesIncident(candidate, incidentTopics)).ToArray();
        if (candidates.Length == 1) return true;
        // Guardados para a explicação da trava durante o turno (a evidência é nova a cada turno): uma chamada posterior barrada
        // por outro motivo não apaga a pergunta das ofertas.
        ambiguousUpdateTitles = candidates.Select(candidate => observedUpdates[candidate].Title).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        return UserNamedExactlyOneTarget(request, candidates.Select(candidate => (candidate, observedUpdates[candidate].Title)), id, StringComparer.Ordinal);
    }
    private string[] ambiguousUpdateTitles = [];
    // Por que a trava barrou uma alteração de impressora ou dispositivo cujo componente o pedido cita (#70, validação no PC).
    // Sem isso, a explicação pedia "responda só com o nome" mesmo quando o usuário já tinha respondido só com o nome.
    private readonly Dictionary<string, string> targetBlockDetails = [];
    public void NoteTargetBlock(string name, JsonElement args, string request)
    {
        string? Argument(string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        string Listed(IEnumerable<string> names) => string.Join("; ", names.Order(StringComparer.CurrentCultureIgnoreCase).Take(MaxListed).Select(item => Quoted(item)));
        string? detail = null;
        if (name == "clear_print_queue" && Argument("printer_name") is { } printer)
        {
            var named = NamedCandidate(request, observedPrinters.Keys.Select(key => (key, key)));
            if (!observedPrinters.ContainsKey(printer))
                detail = Quoted(printer) + " não aparece na consulta de impressoras deste turno" + (observedPrinters.Count > 0 ? " (a consulta mostrou " + Listed(observedPrinters.Keys) + ")" : "");
            else if (named != null && !named.Equals(printer, StringComparison.OrdinalIgnoreCase))
                detail = "você indicou " + Quoted(named) + ", mas a alteração pedida era para " + Quoted(printer);
            else if (named != null)
                detail = "a consulta deste turno não confirma " + Quoted(printer) + " como a impressora com a falha: as filas com indício de falha são " + Listed(PrinterCandidates());
        }
        else if (name is "enable_camera" or "enable_device" or "restart_device" && Argument("device_id") is { } id)
        {
            if (!observedDevices.TryGetValue(id, out var target))
                detail = "o dispositivo indicado não aparece na consulta de dispositivos deste turno";
            else
            {
                var named = NamedCandidate(request, observedDevices.Where(device => device.Value.Topic == target.Topic).Select(device => (device.Key, device.Value.Name)));
                if (named != null && named != id)
                    detail = "você indicou " + Quoted(observedDevices[named].Name) + ", mas a alteração pedida era para " + Quoted(target.Name);
                else if (named != null && name != "restart_device" && target.Code != 22)
                    detail = Quoted(target.Name) + " não está desabilitado nesta consulta, então não há o que habilitar";
                else if (named != null)
                    detail = "o nome " + Quoted(target.Name) + " não identifica um único dispositivo nesta consulta";
            }
        }
        if (detail != null) targetBlockDetails[name] = detail; else targetBlockDetails.Remove(name);
    }
    // Se o alvo de uma alteração já apareceu numa consulta deste turno. Sem isso, a trava barra por falta de evidência, e não
    // por alvo errado: o controlador não conta essa primeira tentativa como falha repetida.
    public bool TargetObserved(string name, JsonElement args)
    {
        string? Argument(string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return name switch
        {
            "enable_camera" or "enable_device" or "restart_device" => Argument("device_id") is { } device && observedDevices.ContainsKey(device),
            "clear_print_queue" => Argument("printer_name") is { } printer && observedPrinters.ContainsKey(printer),
            "install_driver_update" => Argument("update_id") is { } id && observedUpdates.TryGetValue(id, out var update) && observedDevices.Values.Any(device
                => device.HardwareIds.Concat(device.CompatibleIds).Any(hardwareId => string.Equals(hardwareId, update.HardwareId, StringComparison.OrdinalIgnoreCase))),
            _ => true
        };
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
            (@"(?<!nao )\b(instalei|atualizei)\b|\bdriver\s+(foi\s+)?instalado\b", ["install_driver_update", "install_official_nvidia_driver"]),
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
            var mentioned = SupportIntake.ComponentTopics(message);
            if (mentioned.Any(topic => !observedTopics.Contains(topic)))
                return "A resposta descreve uma verificação de componente sem resultado bem sucedido dessa categoria.";
        }
        if (PresentsStaleProcess(text)) return StaleProcessReason;
        return null;
    }

    internal const string StaleProcessReason = "A resposta apresenta como atual o consumo de memória de um processo que o relatório de saúde só mostra em ocorrência que não é atual (resolvida, histórica ou informativa). Cite apenas o que persiste nas leituras atuais, ou diga que essa ocorrência não está ativa.";

    // "O llama-server está consumindo memória" quando a única ocorrência dele está resolvida (#88). Vale a frase que cita o
    // processo com um verbo de consumo no presente, sem negação; um processo que aparece numa consulta atual (get_processes
    // ou ocorrência que persiste) pode ser citado.
    private bool PresentsStaleProcess(string normalized)
    {
        var current = topProcesses.Select(p => p.Name).Concat(memoryHolders.Select(h => h.Name)).Select(AgentProtocol.Normalize).ToHashSet();
        var stale = staleProcesses.Select(AgentProtocol.Normalize).Where(name => !current.Contains(name)).ToArray();
        if (stale.Length == 0) return false;
        return Regex.Split(normalized, @"(?<=[.!?;\n])").Any(sentence =>
            stale.Any(name => Regex.IsMatch(sentence, @"(?<![\w.-])" + Regex.Escape(name) + @"(?![\w-])"))
            && Regex.IsMatch(sentence, @"\b(esta|estao|continua|continuam|segue|seguem)\s+(consumindo|usando|ocupando|segurando)\b|\b(consome|consomem|ocupa|ocupam)\b")
            && !Regex.IsMatch(sentence, @"\b(nao|nem|deixou de|deixaram de|parou de|pararam de)\b"));
    }

    // Os MB do processo que segura memória entram na resposta (#88): no PC, a resposta citou "NexoMemTeste PID 23132" e só
    // disse "memória em crescimento persistente", com "de 4375 MB para 13591 MB" no relatório. Quando a resposta cita o processo
    // (pelo nome ou pelo PID) ou fala de memória sem nenhum valor em MB ou GB, o Nexo acrescenta a leitura do monitor.
    public string WithMemoryFigures(string answer)
    {
        if (memoryHolders.Count == 0 || string.IsNullOrWhiteSpace(answer)) return answer;
        var text = AgentProtocol.Normalize(answer);
        if (Regex.IsMatch(text, @"\d\s*(mb|gb|megabytes?|gigabytes?)\b")) return answer;
        var cited = memoryHolders.Where(holder => Regex.IsMatch(text, @"(?<![\w.-])" + Regex.Escape(AgentProtocol.Normalize(holder.Name)) + @"(?![\w-])")
            || Regex.IsMatch(text, @"(?<!\d)" + holder.Pid.ToString(CultureInfo.InvariantCulture) + @"(?!\d)")).ToArray();
        if (cited.Length == 0 && !text.Contains("memoria", StringComparison.Ordinal)) return answer;
        var figures = (cited.Length > 0 ? cited : memoryHolders.ToArray()).Take(2).Select(holder =>
            Quoted(holder.Name) + " (PID " + holder.Pid.ToString(CultureInfo.InvariantCulture) + ")"
            + (holder.Now is { } now ? " segue com " + now.ToString(CultureInfo.InvariantCulture) + " MB; no alerta, subiu" : " subiu")
            + " de " + holder.From.ToString(CultureInfo.InvariantCulture) + " MB para " + holder.To.ToString(CultureInfo.InvariantCulture) + " MB");
        return answer.TrimEnd() + "\n\nPelo monitor de saúde: " + string.Join("; ", figures) + ".";
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
    public bool HasFindings => findings.Count > 0;
    public bool HasNumericConnectionTest => connections.Values.Any(test => IsAddress(test.Host));
    public void Observe(string name, string signature, JsonElement args, string result, (ControlBlock Kind, string? Topic)? block = null)
    {
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Resultado da ferramenta deve ser um objeto JSON.");
        if (block is { } blocked && !blocks.Any(b => b.Name == name && b.Kind == blocked.Kind)) blocks.Add((name, blocked.Kind, blocked.Topic));
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
        {
            failedTools.Remove(name);
            successfulTools.Add(name);
            Remember(name, args, root);
            switch (name)
            {
                case "get_network" or "test_connection" or "probe_network_interfaces" or "enable_network_adapter" or "set_dns" or "set_ipv4" or "flush_dns": observedTopics.Add("network"); break;
                case "get_printers" or "clear_print_queue": observedTopics.Add("printer"); break;
                case "get_processes" or "get_recent_errors" or "get_health_report": observedTopics.Add("system"); break;
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
            if (ToolCatalog.IsMutation(name) && !ExplicitlyUnchanged(root)) { successfulChanges.Add(name); changes.Add(ToolCatalog.Label(name).TrimEnd('…')); observations.Clear(); connections.Clear(); ForgetFindings(); }
            if (name == "wait_for_reconnect" && root.TryGetProperty("data", out var watch) && watch.TryGetProperty("Reconnected", out var reconnect) && reconnect.ValueKind == JsonValueKind.True) { observations.Clear(); connections.Clear(); ForgetFindings(); }
            if (name == "test_connection" && root.TryGetProperty("data", out var data))
            {
                var host = data.TryGetProperty("host", out var h) ? h.GetString() ?? "destino" : args.TryGetProperty("host", out h) ? h.GetString() ?? "destino" : "destino";
                bool Flag(string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
                connections[signature] = (host, Flag("dnsResolved"), Flag("tcpConnected"));
            }
        }
        // Uma repetição barrada pelo controlador não chegou ao executor: a ferramenta não falhou se já concluiu neste turno.
        else if (!((block?.Kind is ControlBlock.RepeatedEvidence or ControlBlock.RepeatedFailure) && successfulTools.Contains(name))) failedTools.Add(name);
        // Ignore durations: a new timestamp is not new diagnostic evidence.
        var fingerprint = name == "test_connection" && root.TryGetProperty("data", out var test)
            ? JsonSerializer.Serialize(new { dns = test.TryGetProperty("dnsResolved", out var d) ? d.ToString() : "", tcp = test.TryGetProperty("tcpConnected", out var t) ? t.ToString() : "" }) : result;
        observations[signature] = (fingerprint, observations.TryGetValue(signature, out var old) && old.Result == fingerprint ? old.Count + 1 : 1);
    }
    private static string[] TextArray(JsonElement source, string name)
        => source.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString() ?? "").Where(value => value.Length > 0).ToArray()
            : [];
    // Resumo para o modelo, a cada etapa: os resultados completos já estão no histórico da conversa.
    public string Summary(string reason = "") => Compose(reason, forUser: false);

    // Resposta final escrita pelo controlador, sem o modelo: inclui o que as consultas mostraram e por que o Nexo parou.
    public string Report(string reason) => Compose(reason, forUser: true);

    private string Compose(string reason, bool forUser)
    {
        var lines = new List<string>();
        if (forUser && findings.Count > 0)
        {
            lines.Add("O que as consultas deste turno mostraram:");
            // As linhas do relatório de saúde contam como um achado só; o que passar do limite é avisado, não cortado em silêncio.
            static string Group(string key) => key.StartsWith("health", StringComparison.Ordinal) ? "health" : key;
            var groups = findings.Select(f => Group(f.Key)).Distinct().ToArray();
            var shown = groups.Take(MaxFindings).ToHashSet();
            foreach (var (key, text, guidance) in findings.Where(f => shown.Contains(Group(f.Key))))
                lines.Add("• " + text + (key == "storage" && !findings.Any(f => f.Key is "system" or "health:volumes")
                    ? " O espaço livre por disco aparece na verificação de memória e disco, que não foi feita neste turno." : "") + guidance);
            if (groups.Length > MaxFindings)
                lines.Add($"• E mais {groups.Length - MaxFindings} {(groups.Length - MaxFindings == 1 ? "achado" : "achados")} nos cartões de resultado deste turno.");
        }
        var tests = connections.Values.ToArray();
        foreach (var test in tests.Distinct())
        {
            var numeric = IPAddress.TryParse(test.Host, out _);
            lines.Add(test.Tcp ? $"Conexão TCP com {test.Host}: funcionando." : !numeric && !test.Dns ? $"Resolução DNS de {test.Host}: não confirmada no teste." : $"Conexão TCP com {test.Host}: não confirmada no teste.");
        }
        if (tests.Any(t => t.Tcp) && tests.Any(t => !t.Tcp)) lines.Add("Há conectividade parcial: os testes não permitem afirmar que toda a internet está indisponível ou que todos os serviços funcionam.");
        if (tests.Length > 0 && tests.All(t => t.Tcp)) lines.Add("Os destinos testados responderam. Confirme também o aplicativo em que percebeu o problema.");
        // Para o usuário, as alterações estão na linha de fatos do fim da resposta (#73), que é a fonte única.
        if (!forUser) lines.Add(changes.Count == 0 ? "Nenhuma alteração concluída pelo Nexo foi registrada neste turno." : "Operações concluídas neste turno: " + string.Join("; ", changes) + ".");
        if (!forUser)
        {
            if (failedTools.Count > 0) lines.Add("Ferramentas sem conclusão bem sucedida: " + string.Join(", ", failedTools.Order()) + ". Confira os cartões de resultado para a causa.");
        }
        else
        {
            // Para o usuário, em português; a etapa barrada ou já falha é explicada pela trava logo abaixo.
            // As alterações não concluídas também já estão na linha de fatos; aqui ficam as consultas.
            var unexplained = failedTools.Where(tool => !ToolCatalog.IsMutation(tool) && !blocks.Any(b => b.Name == tool && b.Kind is ControlBlock.Topic or ControlBlock.RepeatedFailure or ControlBlock.UninformedDnsServers)).Order().ToArray();
            if (unexplained.Length > 0) lines.Add("Não terminaram com sucesso: " + string.Join("; ", unexplained.Select(Describe)) + ". Confira os cartões de resultado para a causa.");
            lines.AddRange(blocks.Take(3).Select(BlockExplanation));
        }
        if (reason.Length > 0) lines.Add(reason);
        return string.Join("\n", lines);
    }

    private string BlockExplanation((string Name, ControlBlock Kind, string? Topic) block) => block.Kind switch
    {
        ControlBlock.Topic when block.Topic == null && block.Name == "install_driver_update" && ambiguousUpdateTitles.Length > 1 => OfferQuestion(),
        ControlBlock.RepeatedEvidence => "Parei a investigação porque a IA pediu de novo " + Describe(block.Name) + ", que já tinha trazido o mesmo resultado duas vezes; repetir não traria informação nova.",
        ControlBlock.RepeatedFailure => "Parei a investigação porque a IA tentou repetir " + Describe(block.Name) + ", que já tinha falhado ou sido recusada neste turno; o motivo está no cartão desse resultado.",
        ControlBlock.UninformedDnsServers => "O Nexo não executou " + Describe(block.Name) + " porque os servidores DNS escolhidos pela IA não foram informados por você, e o Nexo não usa endereços de DNS inventados. Se o computador obtinha o DNS automaticamente, a correção indicada é voltar ao DNS automático, que continua dependendo da sua aprovação; para usar outro DNS, diga quais servidores a sua rede usa.",
        _ => block.Topic is { } topic
            ? "O Nexo não executou " + Describe(block.Name) + " porque o seu pedido não cita " + Component(topic) + ", a parte do Windows que essa alteração muda. Essa trava evita mexer em algo diferente do que você relatou. Se o problema for com " + Component(topic) + ", diga isso com suas palavras; a alteração continua dependendo da sua aprovação."
            : targetBlockDetails.TryGetValue(block.Name, out var detail)
                ? "O Nexo não executou " + Describe(block.Name) + " porque " + detail + ". Nenhuma alteração foi feita nessa etapa; se o problema continuar, diga o que acontece para investigarmos a próxima hipótese."
                : "O Nexo não executou " + Describe(block.Name) + " porque o alvo não foi identificado sem ambiguidade nas consultas deste turno. Diga qual dispositivo ou impressora apresenta a falha respondendo só com o nome dele, como aparece no cartão da consulta."
    };

    // Ofertas com o mesmo nome (depois de ignorar caixa, acento e pontuação final) não se distinguem pela resposta: a pergunta
    // diz isso em vez de pedir um nome que não resolve.
    private string OfferQuestion()
    {
        var listed = "há mais de uma oferta para o dispositivo relatado: " + string.Join("; ", ambiguousUpdateTitles.Select(title => Quoted(title)));
        var forms = ambiguousUpdateTitles.GroupBy(NameForm).ToArray();
        if (forms.Length == 1)
            return "O Nexo não instalou nenhum driver porque " + listed + ". As ofertas têm o mesmo nome, então a resposta com o nome não distingue uma da outra, e o Nexo não escolhe entre elas. Encaminhe à TI para avaliar qual instalar.";
        return "O Nexo não instalou nenhum driver porque " + listed + ". Qual delas você quer instalar? " + ReplyWithName + " A instalação continua dependendo da sua aprovação."
            + (forms.Any(form => form.Count() > 1) ? " Ofertas com o mesmo nome não se distinguem pela resposta; para essas, encaminhe à TI." : "");
    }

    // Identidade de uma alteração na linha de fatos (#73): a ferramenta e o alvo canônico, não o texto da descrição, que muda
    // quando uma consulta traz o nome no lugar do ID.
    public static string ChangeKey(string name, JsonElement args)
    {
        // Ferramentas inventadas pelo modelo contam como uma só na linha (#84): o nome delas não identifica nada no Windows.
        if (!ToolCatalog.IsKnown(name)) return UnknownToolKey;
        string Target(string key) => Text(args, key) is { } value ? value.ToUpperInvariant() : "";
        var target = name switch
        {
            "clear_print_queue" => Target("printer_name"),
            "restart_service" => Target("service"),
            "enable_device" or "enable_camera" or "restart_device" => Target("device_id"),
            "install_driver_update" => Target("update_id"),
            "install_official_nvidia_driver" => Target("driver_id"),
            "set_dns" or "set_ipv4" or "enable_network_adapter" => args.ValueKind == JsonValueKind.Object && args.TryGetProperty("interface_index", out var index) ? index.GetRawText() : "",
            _ => ""
        };
        return name + "|" + target;
    }

    internal const string UnknownToolKey = "?ferramenta-inexistente|";
    // Na linha de fatos e nas explicações, uma ferramenta que não existe no Nexo (#84). O nome que o modelo inventou não
    // entra: a linha é escrita só pelo Nexo.
    internal const string UnknownToolDescription = "ferramenta inexistente pedida pelo modelo";

    // Como uma alteração aparece na linha de fatos (#73): o que muda e em quê, com o nome vindo de ferramenta entre aspas.
    public string ChangeDescription(string name, JsonElement args)
    {
        string? Argument(string key) => Text(args, key);
        string Interface() => args.ValueKind == JsonValueKind.Object && args.TryGetProperty("interface_index", out var index) && index.ValueKind == JsonValueKind.Number
            && index.TryGetInt32(out var number) ? " da interface " + number : "";
        string Device(string key) => Argument(key) is { } id ? " " + Quoted(observedDevices.TryGetValue(id, out var device) && device.Name.Length > 0 ? device.Name : id) : "";
        return name switch
        {
            "clear_print_queue" => "limpeza da fila" + (Argument("printer_name") is { } printer ? " de " + Quoted(printer) : " de impressão"),
            "restart_service" => "reinício do serviço" + (Argument("service") is { } service ? " " + Quoted(service) : ""),
            "enable_device" => "habilitação do dispositivo" + Device("device_id"),
            "enable_camera" => "habilitação da câmera" + Device("device_id"),
            "restart_device" => "reinício do dispositivo" + Device("device_id"),
            "allow_camera" => "permissão de acesso à câmera",
            "flush_dns" => "limpeza do cache DNS",
            "set_dns" => (Argument("mode") == "automatic" ? "DNS automático" : "DNS manual" + (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("servers", out var servers)
                && servers.ValueKind == JsonValueKind.Array ? " " + string.Join(", ", servers.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => Quoted(v.GetString() ?? "", 45)).Take(MaxListed)) : "")) + Interface(),
            "set_ipv4" => "configuração do IPv4" + Interface(),
            "enable_network_adapter" => "ativação" + (Interface() is { Length: > 0 } adapter ? adapter : " da interface de rede"),
            "install_driver_update" => "instalação do driver" + (Argument("update_id") is { } update ? " " + Quoted(observedUpdates.TryGetValue(update, out var offer) && offer.Title.Length > 0 ? offer.Title : update) : ""),
            "install_official_nvidia_driver" => "instalação do driver NVIDIA" + (Argument("driver_id") is { } driver ? " " + Quoted(driver) : ""),
            "download_nvidia_app" => "download do aplicativo da NVIDIA",
            _ when !ToolCatalog.IsKnown(name) => UnknownToolDescription,
            _ => ToolCatalog.Label(name).TrimEnd('…').ToLowerInvariant()
        };
    }

    internal static string Describe(string name) => name switch
    {
        "get_processes" => "a consulta de processos",
        "diagnose_device" => "a verificação de dispositivos e sistema",
        "get_hardware_inventory" => "o inventário de hardware",
        "get_network" => "a consulta de rede",
        "test_connection" => "o teste de conexão",
        "get_health_report" => "o relatório de saúde",
        "get_services" => "a consulta de serviços",
        "get_printers" => "a consulta de impressoras",
        _ when !ToolCatalog.IsKnown(name) => "a " + UnknownToolDescription,
        _ => "a etapa “" + ToolCatalog.Label(name).TrimEnd('…') + "”"
    };

    internal static string Component(string topic) => topic switch
    {
        "network" => "a rede ou a internet",
        "camera" => "a câmera",
        "audio" => "o som ou o microfone",
        "printer" => "a impressora",
        "bluetooth" => "o Bluetooth",
        "display" => "a placa de vídeo",
        _ => "esse componente"
    };

    // A recomendação é a da REGRA que gerou o achado, com o texto do HealthRules. O relatório não traz o código da
    // regra, então ela é reconhecida pela categoria (que abre a linha) e pelo começo fixo do problema, que vem do código;
    // um nome de volume ou processo só aparece depois desse começo. Ficam de fora as regras cujo passo é do agente
    // ("antes de propor reinício", serviço e rede) e as de memória sem identidade confirmada (#59, revisões do @qa).
    private static (string Rule, string Text) Advice(string category, string problem) => category switch
    {
        "disk" when problem.StartsWith("Pouco espaço no volume ", StringComparison.Ordinal) => ("disk-low", "Revise o uso do disco antes de remover arquivos"),
        "cpu" when problem == "Uso persistente de CPU" => ("cpu-high", "Identifique os processos ativos antes de fechar qualquer um"),
        "memory" when problem == "Memória em crescimento persistente" => ("memory-growing", "Compare o uso por processo antes de intervir"),
        "memory" when problem == "Processo com memória em crescimento persistente" => ("process-memory-growing", "Verifique se o aplicativo está em uso e salve o trabalho antes de qualquer intervenção"),
        _ => ("", "")
    };

    // Assunto do pedido do usuário ("space" ou "slowness"): a recomendação ligada a ele vem primeiro no limite de duas.
    public string? Focus { get; set; }

    private void ForgetFindings() { findings.Clear(); topProcesses.Clear(); memoryHolders.Clear(); staleProcesses.Clear(); }

    // Só a evidência é encurtada; a orientação de segurança que acompanha a linha nunca é cortada.
    private void Finding(string key, string text, string guidance = "")
    {
        var line = (key, Shorten(text, MaxFindingLength), guidance);
        var index = findings.FindIndex(f => f.Key == key);
        if (index >= 0) findings[index] = line; else findings.Add(line);
    }

    // Guarda, em português curto, o que uma consulta bem sucedida mostrou. Formato inesperado não vira evidência.
    private void Remember(string name, JsonElement args, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var raw)) return;
        try
        {
            using var parsed = raw.ValueKind == JsonValueKind.String && raw.GetString() is { } embedded && embedded.TrimStart().StartsWith('[') ? JsonDocument.Parse(embedded) : null;
            var data = parsed?.RootElement ?? raw;
            switch (name)
            {
                case "get_processes":
                    RememberProcesses(data);
                    break;
                case "diagnose_device" when Argument(args, "topic") == "system":
                    if (data.ValueKind != JsonValueKind.String) return;
                    var readings = Regex.Matches(data.GetString() ?? "", @"^(Memória livre: .+|Disco .+ livres de .+)$", RegexOptions.Multiline)
                        .Select(match => Clean(match.Value).Replace("::", ":")).Take(5).ToArray();
                    if (readings.Length > 0) Finding("system", string.Join("; ", readings) + ".");
                    break;
                case "diagnose_device" or "get_hardware_inventory":
                    RememberDevices(name, Argument(args, name == "diagnose_device" ? "topic" : "category") ?? "", data);
                    break;
                case "get_network":
                    RememberNetwork(data);
                    break;
                case "get_health_report":
                    if (data.ValueKind == JsonValueKind.String) RememberHealth(data.GetString() ?? "");
                    break;
                // Só o resultado em que o executor confirmou o estado do serviço (#84); a lista de impressoras não vira achado aqui.
                case "get_printers" when data.ValueKind == JsonValueKind.Object && data.TryGetProperty("SpoolerRunning", out var running) && running.ValueKind == JsonValueKind.False:
                    var state = Text(data, "SpoolerStatus") ?? "";
                    var disabled = string.Equals(Text(data, "SpoolerStartType"), "Disabled", StringComparison.OrdinalIgnoreCase);
                    Finding("printer", (string.Equals(state, "Stopped", StringComparison.OrdinalIgnoreCase) ? "O serviço de impressão (Spooler) está parado"
                        : "O serviço de impressão (Spooler) não está em execução (estado " + Quoted(state, 20) + ")") + (disabled ? " e desabilitado" : "")
                        + ": sem ele o Windows não lista impressoras nem filas, e nada imprime.",
                        disabled ? " Verifique a política da organização antes de mudar o modo de inicialização do serviço." : " Reiniciar o serviço de impressão depende da sua aprovação.");
                    break;
            }
        }
        catch (JsonException) { /* Um resultado sem o formato esperado continua só no cartão. */ }
    }

    private void RememberProcesses(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return;
        var top = list.EnumerateArray()
            .Where(process => process.ValueKind == JsonValueKind.Object && process.TryGetProperty("MemoryMB", out var memory) && memory.ValueKind == JsonValueKind.Number && memory.TryGetInt64(out _))
            .Select(process => (Name: ProcessName(Text(process, "Name") ?? ""), Megabytes: process.GetProperty("MemoryMB").GetInt64(),
                Responding: !(process.TryGetProperty("Responding", out var responding) && responding.ValueKind == JsonValueKind.False)))
            .OrderByDescending(process => process.Megabytes).Take(1).ToArray();
        if (top.Length == 0) return;
        topProcesses.Add(top[0]);
        var (first, last) = (topProcesses[0], topProcesses[^1]);
        var text = topProcesses.Count == 1 || first == last ? $"Processo que mais usa memória: “{last.Name}”, com {last.Megabytes} MB."
            : first.Name == last.Name ? $"Processo que mais usa memória: “{last.Name}”, com {first.Megabytes} MB na primeira consulta e {last.Megabytes} MB na última."
            : $"Processo que mais usa memória na última consulta: “{last.Name}”, com {last.Megabytes} MB (na primeira consulta era “{first.Name}”, com {first.Megabytes} MB).";
        if (!last.Responding) text += " Ele não está respondendo.";
        Finding("processes", text, " Se for um programa seu que não está em uso agora, salve o trabalho nele e feche-o; o Nexo não encerra processos.");
    }

    private void RememberDevices(string name, string topic, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !(data.TryGetProperty("devices", out var list) || data.TryGetProperty("Devices", out list)) || list.ValueKind != JsonValueKind.Array) return;
        var devices = list.EnumerateArray().Where(device => device.ValueKind == JsonValueKind.Object).ToArray();
        var subject = topic switch
        {
            "camera" => "câmera", "audio" => "áudio", "bluetooth" => "Bluetooth", "printer" => "impressora", "storage" => "armazenamento",
            "display" => "vídeo", "usb" => "USB", "network" => "rede", "problem" => "dispositivos com problema", _ => "dispositivos"
        };
        var faulty = devices.Select(device => (Name: Quoted(Text(device, "Name") ?? ""), Code: device.TryGetProperty("Code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) ? value : 0))
            .Where(device => device.Code > 0).ToArray();
        var source = name == "diagnose_device" ? "Na verificação de " + subject : "No inventário de " + subject;
        Finding("devices:" + topic, devices.Length == 0 ? source + ", o Windows não listou nenhum dispositivo."
            : faulty.Length == 0 ? $"{source}, {(devices.Length == 1 ? "1 dispositivo apareceu" : devices.Length + " dispositivos apareceram")} sem código de erro do Windows."
            : $"{source}, com código de erro do Windows: " + Within(faulty.Take(MaxListed).Select(device => $"{device.Name} (código {device.Code}{(device.Code == 22 ? ", desabilitado" : "")})"), faulty.Length) + ".");
        if (name == "get_hardware_inventory" && topic == "storage")
            Finding("storage", "O inventário de armazenamento lista discos e controladoras, mas não informa espaço livre.");
    }

    private void RememberNetwork(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("Adapters", out var adapters) || adapters.ValueKind != JsonValueKind.Array) return;
        var connected = adapters.EnumerateArray().Where(adapter => adapter.ValueKind == JsonValueKind.Object && Text(adapter, "Status") == "Up").ToArray();
        Finding("network", connected.Length == 0 ? "Nenhum adaptador de rede aparece conectado na consulta."
            : "Rede: " + Within(connected.Take(3).Select(adapter => $"{Quoted(Text(adapter, "Name") ?? "")} conectada, gateway {Addresses(adapter, "Gateways")}, DNS {Addresses(adapter, "Dns")}"
                + (Text(adapter, "DnsSource") == "manual" && Text(adapter, "Dhcp") == "Enabled" ? " (fixo, com DHCP ativo)" : "")), connected.Length) + ".");
    }

    private void RememberHealth(string report)
    {
        // Cada relatório substitui as três linhas do anterior; uma parte ausente agora não pode sobrar da leitura velha.
        findings.RemoveAll(f => f.Key is "health" or "health:unknown" or "health:informative" or "health:volumes");
        memoryHolders.Clear(); staleProcesses.Clear();
        if (report.StartsWith("Nenhuma amostra", StringComparison.Ordinal)) { Finding("health", "O relatório de saúde não tem leituras das últimas 24 horas."); return; }
        // O relatório marca a amostra velha na linha de coleta, pelo mesmo critério de HealthOccurrences.CollectionNotice.
        var stale = Regex.IsMatch(report, @"^Última coleta: [^\n]*Sem leitura recente: o estado atual das ocorrências é desconhecido\.", RegexOptions.Multiline);
        var sample = Regex.Match(report, @"^Amostra: (?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} [+-]\d{2}:\d{2});", RegexOptions.Multiline) is { Success: true } found
            && DateTimeOffset.TryParseExact(found.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToLocalTime().ToString("dd/MM 'às' HH:mm", CultureInfo.InvariantCulture) : null;
        // O relatório troca a vírgula decimal por "_"; aqui ela volta para o usuário ler o número.
        static string Readable(string text) => Regex.Replace(text.Replace(":_", ":"), @"(?<=\d)_(?=\d)", ",");
        // Toda linha de achado tem o mesmo formato; o estado vem depois da data e antes do problema. Só um estado que começa
        // por "persiste" é atual (#88): "persiste nas leituras atuais" e "persiste: <processo> segue com N MB…", que antes não
        // era reconhecido e tirava do resumo a ocorrência do processo que segura memória. Qualquer outro estado não é atual.
        // Os estados conhecidos vêm do código; um estado novo, sem dois-pontos, também é lido. O problema e a evidência podem
        // trazer nome de volume que imita ". Evidência:", então o estado nunca avança sobre eles.
        var lines = Regex.Matches(report, @"^Achado (?<category>[a-z]+) \([^)\n]*\), [^,\n]*, (?<status>persiste nas leituras atuais|persiste: [\p{L}\p{N}._-]+ PID \d+ segue com [^:\n]*|ocorrência histórica; estado atual desconhecido \([^)\n]*\)|resolvida: [^:\n]*|[^:\n]*): (?<problem>[^\n]+?)\. Evidência: (?<evidence>[^\n]+?)\. Causa:", RegexOptions.Multiline)
            .Select(match => (Status: match.Groups["status"].Value, Category: match.Groups["category"].Value, Problem: match.Groups["problem"].Value, Evidence: match.Groups["evidence"].Value,
                Active: match.Groups["status"].Value.StartsWith("persiste", StringComparison.Ordinal)))
            .ToArray();
        foreach (var line in lines.Where(line => line.Category == "memory"))
        {
            if (Regex.Match(line.Evidence, @"^(?<name>[\p{L}\p{N}._-]+) PID (?<pid>\d{1,9})\b") is not { Success: true } who) continue;
            var name = who.Groups["name"].Value;
            if (!line.Active) { staleProcesses.Add(name); continue; }
            if (Regex.Match(line.Evidence, @": de (?<from>\d{1,9}) MB para (?<to>\d{1,9}) MB") is { Success: true } growth
                && int.TryParse(who.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                memoryHolders.Add((name, pid, long.Parse(growth.Groups["from"].Value, CultureInfo.InvariantCulture), long.Parse(growth.Groups["to"].Value, CultureInfo.InvariantCulture),
                    Regex.Match(line.Status, @" segue com (?<now>\d{1,9}) MB\b") is { Success: true } now ? long.Parse(now.Groups["now"].Value, CultureInfo.InvariantCulture) : null));
        }
        // Um processo citado também numa ocorrência atual não é só de ocorrência antiga.
        foreach (var holder in lines.Where(line => line.Active && line.Category == "memory")
            .Select(line => Regex.Match(line.Evidence, @"^(?<name>[\p{L}\p{N}._-]+) PID ")).Where(match => match.Success)) staleProcesses.Remove(holder.Groups["name"].Value);
        var occurrences = lines
            .Where(line => !line.Status.StartsWith("resolvida", StringComparison.Ordinal) && !line.Status.Contains("(há ocorrência mais recente do mesmo alerta)", StringComparison.Ordinal))
            // A evidência pode trazer nome de adaptador, serviço ou processo: vai entre aspas, como todo nome vindo de ferramenta.
            .Select(line => (line.Active, Unknown: line.Status.StartsWith("ocorrência histórica", StringComparison.Ordinal),
                Text: Clean(Readable(line.Problem)) + " (evidência: " + Quoted(Readable(line.Evidence), MaxEvidenceLength) + ")",
                Advice: Advice(line.Category, line.Problem)))
            .ToArray();
        var active = occurrences.Where(o => o.Active).Select(o => o.Text).ToArray();
        // A recomendação das ocorrências ativas acompanha a linha (#59: "Revise o uso do disco antes de remover arquivos").
        // Ela vem da regra, não do texto do relatório: um nome de adaptador, serviço ou volume pode imitar ". Recomendação: ..."
        // na linha (@qa). No limite de duas, a do assunto do pedido vem antes ("sem espaço" -> disco; "lento" -> memória e CPU).
        bool Related(string rule) => Focus == "space" ? rule == "disk-low" : Focus == "slowness" && rule is "cpu-high" or "memory-growing" or "process-memory-growing";
        var advice = occurrences.Where(o => o.Active && o.Advice.Text.Length > 0).OrderBy(o => Related(o.Advice.Rule) ? 0 : 1)
            .Select(o => o.Advice.Text).Distinct().Take(2).ToArray();
        var unknown = occurrences.Where(o => !o.Active && o.Unknown).Select(o => o.Text).ToArray();
        // Estado que não é atual nem desconhecido (um estado informativo, por exemplo): fica visível, marcado como não atual.
        var informative = occurrences.Where(o => !o.Active && !o.Unknown).Select(o => o.Text).ToArray();
        var volumes = Regex.Matches(report, @"^Volume (?<name>[^\n]+?): (?<free>[\d.,]+% livre)\.", RegexOptions.Multiline)
            // Letra de unidade sai como está; outro nome de volume é dado da ferramenta e vai entre aspas (@qa: nome que imita o relatório).
            .Select(match => (Regex.IsMatch(match.Groups["name"].Value, @"^[A-Za-z]:_?$") ? match.Groups["name"].Value.TrimEnd('_') : Quoted(match.Groups["name"].Value)) + " " + match.Groups["free"].Value).ToArray();
        var when = sample == null ? "" : " (amostra de " + sample + ")";
        // Uma linha por parte, cada uma com o próprio orçamento por itens inteiros: nenhuma parte some porque a anterior é longa.
        Finding("health", stale
            ? "O relatório de saúde não tem leitura recente" + when + ": o estado atual é desconhecido."
            : active.Length == 0 ? "O monitor de saúde não mostra ocorrência ativa nas leituras atuais" + when + "."
            : "Ocorrência ativa no monitor de saúde" + when + ": " + Within(active.Take(3), active.Length) + ".",
            advice.Length == 0 ? "" : " Recomendação do monitor: " + string.Join("; ", advice) + ". O Nexo não apaga arquivos nem encerra programas."
                // Caminho real para ver o uso do disco (#88): o Nexo não lista arquivos nem pastas.
                + (advice.Contains(Advice("disk", "Pouco espaço no volume ").Text) ? " Para ver o que ocupa espaço, abra Configurações > Sistema > Armazenamento no Windows." : ""));
        if (unknown.Length > 0) Finding("health:unknown", "Ocorrências do monitor com estado atual desconhecido: " + Within(unknown.Take(3), unknown.Length) + ".");
        if (informative.Length > 0) Finding("health:informative", "Ocorrências do monitor que não são problemas atuais: " + Within(informative.Take(3), informative.Length) + ".");
        if (volumes.Length > 0) Finding("health:volumes", (stale ? "Espaço livre por volume naquela amostra: " : "Espaço livre por volume: ") + Within(volumes.Take(4), volumes.Length) + ".");
    }

    // Descreve a alteração concluída com o que a própria ferramenta devolveu e o estado consultado antes dela.
    public string? ChangeOutcome(string name, JsonElement args, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)) return null;
        try
        {
            using var parsed = data.ValueKind == JsonValueKind.String && data.GetString() is { } embedded && embedded.TrimStart().StartsWith('{') ? JsonDocument.Parse(embedded) : null;
            var result = parsed?.RootElement ?? data;
            switch (name)
            {
                case "enable_device" or "enable_camera" or "restart_device":
                    var known = observedDevices.TryGetValue(Argument(args, "device_id") ?? "", out var before);
                    var reported = Text(result, "Name") ?? (known ? before.Name : null);
                    if (reported == null) return null;
                    var device = Quoted(reported);
                    var text = name == "restart_device"
                        ? $"O dispositivo {device} foi reiniciado" + (known && before.Code >= 0 ? $"; antes, o Windows informava código {before.Code}." : ".")
                        : known && before.Code == 22 ? $"O dispositivo {device} estava desabilitado (código 22) e foi habilitado." : $"O dispositivo {device} foi habilitado.";
                    if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("ConfigManagerErrorCode", out var after) && after.ValueKind == JsonValueKind.Number && after.TryGetInt32(out var code))
                        text += $" Depois da alteração, o Windows informa código {code}" + (Text(result, "Status") is { } status ? " e status " + DeviceStatus(status) + "." : ".");
                    return text;
                case "restart_service":
                    var service = Text(result, "Service") ?? Argument(args, "service");
                    var state = Text(result, "Status");
                    return service == null ? null : $"O serviço {Quoted(service)} foi reiniciado" + (state != null ? "; o Windows informa que ele está " + ServiceState(state) + "." : ".");
                case "clear_print_queue":
                    var printer = Text(result, "Printer") ?? Argument(args, "printer_name");
                    if (printer == null) return null;
                    var remaining = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("RemainingJobs", out var jobs) && jobs.ValueKind == JsonValueKind.Number && jobs.TryGetInt32(out var count) ? count : (int?)null;
                    // Fila com trabalhos restantes não foi limpa, mesmo com a ferramenta concluída.
                    return remaining switch
                    {
                        0 => $"A fila da impressora {Quoted(printer)} foi limpa; o Windows informa 0 trabalhos na fila.",
                        > 0 => $"O Nexo tentou limpar a fila da impressora {Quoted(printer)}, mas o Windows ainda informa {remaining} {(remaining == 1 ? "trabalho" : "trabalhos")} na fila.",
                        _ => $"O Nexo pediu a limpeza da fila da impressora {Quoted(printer)}; o Windows não informou quantos trabalhos restam."
                    };
                case "allow_camera":
                    return Text(result, "After") is { } allowed ? $"A permissão de câmera deste perfil passou de {Permission(Text(result, "Before"))} para {Permission(allowed)}." : null;
                case "flush_dns":
                    return "O Windows limpou o cache DNS.";
                default:
                    return null;
            }
        }
        catch (JsonException) { return null; }
    }

    private static string ServiceState(string state) => state switch
    {
        "Running" => "em execução", "Stopped" => "parado", "StartPending" => "iniciando", "StopPending" => "parando", "Paused" => "pausado", _ => "no estado " + Quoted(state)
    };
    private static string DeviceStatus(string status) => status switch
    {
        "OK" => "OK", "Error" => "com erro", "Degraded" => "degradado", "Unknown" => "desconhecido", _ => Quoted(status)
    };
    private static string Permission(string? value) => value switch
    {
        null => "não definida", "Allow" => "permitido", "Deny" => "negado", _ => Quoted(value)
    };

    private static string? Argument(JsonElement args, string key) => Text(args, key);
    private static string? Text(JsonElement source, string key) => source.ValueKind == JsonValueKind.Object && source.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    private static string Addresses(JsonElement adapter, string key)
    {
        var values = adapter.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString() ?? "").Where(IsAddress).Take(3).ToArray()
            : [];
        return values.Length == 0 ? "nenhum" : string.Join(", ", values);
    }
    // IPAddress.TryParse aceita "1" como 0.0.0.1; aqui só passa IPv4 com quatro partes ou IPv6 com escopo numérico.
    private static bool IsAddress(string value) => Regex.IsMatch(value, @"^(\d{1,3}(\.\d{1,3}){3}|[0-9A-Fa-f]*:[0-9A-Fa-f:.]*(%\d+)?)$") && IPAddress.TryParse(value, out _);
    // Junta itens até o limite da linha; o que não couber vira "e mais N", sem cortar um item no meio.
    private static string Within(IEnumerable<string> items, int total)
    {
        var kept = new List<string>();
        foreach (var item in items)
        {
            if (kept.Count > 0 && string.Join("; ", kept.Append(item)).Length > MaxFindingLength - 80) break;
            kept.Add(item);
        }
        return string.Join("; ", kept) + (total > kept.Count ? $" e mais {total - kept.Count}" : "");
    }
    // Nome de executável é escolhido por quem o criou: mesmo filtro de WindowsTools.ProcessesForModel.
    private static string ProcessName(string name)
    {
        var safe = new string(name.Take(40).Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return safe.Length == 0 ? "processo" : safe;
    }
    // Nome vindo de ferramenta (dispositivo, adaptador, impressora, serviço) é dado, não texto do Nexo: vai entre aspas,
    // sem caracteres de controle, de formatação (como U+202E) ou separadores de linha e parágrafo, e com tamanho limitado.
    internal static string Quoted(string name, int limit = MaxNameLength) => "“" + Shorten(Clean(name, quotes: true), limit) + "”";
    // Por rune, não por unidade UTF-16: tags como U+E0000-U+E007F (formatação fora do BMP) também saem.
    internal static string Clean(string text, bool quotes = false)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) clean.Append(' ');
            else if (quotes && (category is UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation || QuoteLike.Contains(rune.Value))) clean.Append('\'');
            else clean.Append(rune.ToString());
        }
        return Regex.Replace(clean.ToString(), @"\s+", " ").Trim();
    }
    // Aspas que não são Pi/Pf no Unicode, mas que um leitor toma por aspas: " „ ‚ ″ ‴ ‶ ‷ ❛ ❜ ❝ ❞ 〝 〞 〟 ＂ ＇.
    private static readonly HashSet<int> QuoteLike = ['"', 0x201E, 0x201A, 0x2033, 0x2034, 0x2036, 0x2037, 0x275B, 0x275C, 0x275D, 0x275E, 0x301D, 0x301E, 0x301F, 0xFF02, 0xFF07];
    private static string Shorten(string text, int limit)
    {
        if (text.Length <= limit) return text;
        var cut = limit - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd() + "…";
    }
}
