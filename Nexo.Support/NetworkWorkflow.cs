using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexo.Support;

// A bounded runbook for a common incident; model-generated text cannot skip its checks.
public static class NetworkWorkflow
{
    public static bool Matches(string message) => Regex.IsMatch(message, @"(?i)\b(sem internet|sem conex[aã]o|cabo desconectado)\b");
    public static async Task<string> Run(Func<string, object, Task<JsonElement>> execute, Action<string> inform, CancellationToken token, bool allowChanges = true)
    {
        var changed = false;
        async Task<JsonElement> Read() => await execute("get_network", new { });
        static bool Flag(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
        static string Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        static JsonElement Data(JsonElement value) => value.TryGetProperty("data", out var data) ? data : value;
        static string Failure(JsonElement value) => Text(value, "error") is { Length: > 0 } error ? error : Text(value, "message");
        string ChangeNote() => changed ? "A alteração autorizada foi concluída; isso, por si só, não comprova todos os serviços." : "Nenhuma alteração de configuração foi concluída pelo Nexo neste atendimento.";
        var network = await Read();
        if (!Flag(network, "success")) return "Não consegui consultar os adaptadores: " + Failure(network);
        var data = Data(network);
        var adapters = data.GetProperty("Adapters").EnumerateArray().ToArray();
        var hasActiveRoute = adapters.Any(a => Text(a, "Status") == "Up" && a.TryGetProperty("Gateways", out var gateways) && gateways.ValueKind == JsonValueKind.Array && gateways.GetArrayLength() > 0);
        var disabled = hasActiveRoute ? [] : adapters.Where(a => Flag(a, "Disabled")).ToArray();
        if (disabled.Length > 1) return "Encontrei mais de uma interface desativada: " + string.Join(", ", disabled.Select(a => Text(a, "Name"))) + ". Qual você usa para conectar?";
        if (disabled.Length == 1)
        {
            if (!allowChanges) return "A interface " + Text(disabled[0], "Name") + " está desativada. Acompanhar o cabo não a habilita. Você quer que eu apresente a proposta de ativação?";
            var enabled = await execute("enable_network_adapter", new { interface_index = disabled[0].GetProperty("Index").GetInt32() });
            if (!Flag(enabled, "success")) return "A ativação não foi concluída. " + Failure(enabled);
            changed = !InvestigationEvidence.ExplicitlyUnchanged(enabled); network = await Read();
            if (!Flag(network, "success")) return "A ativação terminou, mas a consulta posterior falhou. " + Failure(network);
            data = Data(network); adapters = data.GetProperty("Adapters").EnumerateArray().ToArray();
        }
        var physicalDown = adapters.Where(a => Flag(a, "Hardware") && !Flag(a, "Virtual") && a.TryGetProperty("InterfaceType", out var type) && type.GetInt32() == 6 && Text(a, "Status") == "Disconnected").ToArray();
        if (physicalDown.Length == 1 && !adapters.Any(a => Text(a, "Status") == "Up" && a.TryGetProperty("Gateways", out var g) && g.GetArrayLength() > 0))
        {
            var observation = await execute("wait_for_reconnect", new { kind = "network", interface_index = physicalDown[0].GetProperty("Index").GetInt32(), device_id = "" });
            if (!Flag(observation, "success") || !Flag(Data(observation), "Reconnected")) return "Não confirmei a reconexão. " + (Flag(observation, "success") ? Text(Data(observation), "Note") : Failure(observation));
            inform("O Windows indicou reconexão. Agora vou conferir a configuração e testar a conexão.");
            network = await Read();
            if (!Flag(network, "success")) return "A reconexão foi observada, mas a consulta de rede falhou. " + Failure(network);
            data = Data(network); adapters = data.GetProperty("Adapters").EnumerateArray().ToArray();
        }
        async Task<(bool Named, bool Numeric, string Description)> Test()
        {
            var named = await execute("test_connection", new { host = "google.com", port = 443 });
            if (Flag(named, "success") && Flag(Data(named), "tcpConnected")) return (true, false, "A resolução e a conexão TCP com google.com:443 funcionaram.");
            var numeric = await execute("test_connection", new { host = "8.8.8.8", port = 53 });
            var numericOk = Flag(numeric, "success") && Flag(Data(numeric), "tcpConnected");
            return (false, numericOk, numericOk ? "A conexão TCP por IP funcionou, mas o acesso por nome não foi confirmado. Há conectividade parcial; isso não significa que toda a internet está fora." : "Os dois destinos de teste não responderam como esperado. Isso sozinho não comprova a causa nem indisponibilidade de todos os serviços.");
        }
        var first = await Test();
        if (first.Named) return first.Description + "\n" + ChangeNote() + "\nEstá funcionando também no aplicativo em que percebeu o problema?";
        if (first.Numeric) return first.Description + "\n" + ChangeNote() + "\nO problema ocorre em qual aplicativo ou site? Ele funciona no navegador? Essa diferença ajuda a investigar a resolução de nomes sem alterar seu IP por tentativa.";
        if (!allowChanges) return first.Description + "\nAcompanhei a conexão sem alterar configurações. Em qual aplicativo você percebe a falha?";
        var routed = adapters.Where(a => Text(a, "Status") == "Up" && a.TryGetProperty("Gateways", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0).ToArray();
        JsonElement? target = routed.Length == 1 ? routed[0] : null;
        if (routed.Length > 1) return first.Description + "\nHá mais de uma interface com gateway: " + string.Join(", ", routed.Select(a => Text(a, "Name"))) + ". Qual conexão está com problema?";
        if (target == null && data.TryGetProperty("Assessment", out var assessments))
        {
            var candidates = assessments.EnumerateArray().Where(a => Flag(a, "DhcpCandidate")).ToArray();
            if (candidates.Length == 1) target = adapters.Single(a => a.GetProperty("Index").GetInt32() == candidates[0].GetProperty("Index").GetInt32());
        }
        if (target is { } selected && Text(selected, "Dhcp") == "Disabled" && NetworkAssessment.CanConfigure(selected))
        {
            inform("A interface " + Text(selected, "Name") + " usa IP fixo e os testes falharam. Vou apresentar a alternativa de obter IP automaticamente, com possibilidade de recusar e de restaurar. Ainda não sabemos se a rede fornece DHCP.");
            var applied = await execute("set_ipv4", new { interface_index = selected.GetProperty("Index").GetInt32(), mode = "dhcp", address = "", prefix = 0, gateway = "" });
            if (!Flag(applied, "success")) return "A alteração não foi concluída. " + Failure(applied) + (Flag(applied, "reverted") ? "\nA configuração anterior foi restaurada." : Flag(applied, "denied") ? "\nVocê recusou a proposta; ela não foi executada." : applied.TryGetProperty("backup", out _) ? "\nConfira o estado de restauração e o backup no cartão da operação antes de outra alteração." : "");
            changed = true; network = await Read();
            if (!Flag(network, "success")) return "A alteração terminou, mas a consulta posterior falhou. " + Failure(network);
            var after = await Test();
            // Allow one brief convergence retry after an actual configuration change.
            if (!after.Named) { await Task.Delay(1500, token); after = await Test(); }
            return after.Description + "\n" + ChangeNote() + "\nEstá funcionando no aplicativo que você utiliza?";
        }
        return first.Description + "\n" + ChangeNote() + "\nA configuração observada não justifica uma mudança automática de IP. Outros aparelhos nessa mesma rede conseguem acessar a internet?";
    }
}
