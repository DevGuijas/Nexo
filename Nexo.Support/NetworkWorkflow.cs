using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexo.Support;

public static class NetworkWorkflow
{
    public static bool Matches(string message) => Regex.IsMatch(message, @"(?i)\b(sem internet|sem conex[aã]o|cabo desconectado)\b");
    private static bool Flag(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    private static int Number(JsonElement o, string k, int fallback = 0) => o.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) && n >= 0 && n <= int.MaxValue ? (int)n : fallback;
    private static JsonElement Data(JsonElement v) => v.TryGetProperty("data", out var d) ? d : v;
    private static string Failure(JsonElement v) => Text(v, "error") is { Length: > 0 } e ? e : Text(v, "message");

    public static async Task<string> Run(Func<string, object, Task<JsonElement>> execute, Action<string> inform, CancellationToken token,
        bool allowChanges = true, Func<int, CancellationToken, Task>? delay = null, bool afterChange = false)
    {
        delay ??= Task.Delay;
        var changed = afterChange;
        JsonElement data = default;
        JsonElement[] adapters = [];
        async Task<bool> Read()
        {
            token.ThrowIfCancellationRequested();
            var network = await execute("get_network", new { });
            if (!Flag(network, "success")) return false;
            data = Data(network); adapters = data.GetProperty("Adapters").EnumerateArray().ToArray(); return true;
        }
        string ChangeNote() => changed ? "A alteração autorizada foi concluída." : "Nenhuma alteração de configuração foi concluída pelo Nexo neste atendimento.";
        async Task<(bool Named, bool Numeric)> Test()
        {
            var named = await execute("test_connection", new { host = "google.com", port = 443 });
            if (Flag(named, "success") && Flag(Data(named), "dnsResolved") && Flag(Data(named), "tcpConnected")) return (true, true);
            var numeric = await execute("test_connection", new { host = "8.8.8.8", port = 53 });
            return (false, Flag(numeric, "success") && Flag(Data(numeric), "tcpConnected"));
        }
        async Task<(bool Named, bool Numeric)> Stabilize()
        {
            inform("A configuração ou o link mudou. Vou aguardar a conexão estabilizar e repetir os testes antes de concluir.");
            var watch = Stopwatch.StartNew(); var consecutive = 0;
            (bool Named, bool Numeric) latest = (false, false);
            for (var round = 0; round < 6 && watch.Elapsed < TimeSpan.FromSeconds(60); round++)
            {
                await delay(5000, token);
                if (!await Read()) { consecutive = 0; continue; }
                latest = await Test(); consecutive = latest.Named ? consecutive + 1 : 0;
                if (consecutive >= 2) return latest;
            }
            return (false, latest.Numeric);
        }
        string Success() => "A resolução DNS e a conexão TCP com google.com:443 responderam nas verificações" + (changed ? " após a alteração e a espera de estabilização." : ".") + "\n" + ChangeNote() + "\nEstá funcionando também no aplicativo em que percebeu o problema?";
        string Partial() => "Há conectividade parcial: o teste por IP respondeu, mas o acesso por nome não ficou estável. " + ChangeNote() + "\nEm qual aplicativo ou site você ainda percebe a falha?";
        if (!await Read()) return "Não consegui consultar as interfaces. Não alterei a configuração; confira o erro no cartão da consulta.";
        var state = afterChange ? await Stabilize() : await Test();
        if (state.Named) return Success();

        // Compare the whole inventory; no adapter names or indices are requested from users.
        foreach (var candidate in adapters.Where(a => Flag(a, "Disabled")).OrderByDescending(a => Flag(a, "Hardware")).ThenBy(a => Number(a, "Index")).ToArray())
        {
            if (!allowChanges) break;
            inform("Encontrei uma interface desativada: " + Text(candidate, "Name") + ". Vou apresentar a proposta de ativação e verificar o resultado antes de tentar outro ajuste.");
            var enabled = await execute("enable_network_adapter", new { interface_index = Number(candidate, "Index") });
            if (!Flag(enabled, "success")) return "A ativação não foi concluída. " + Failure(enabled);
            changed |= !InvestigationEvidence.ExplicitlyUnchanged(enabled);
            state = await Stabilize(); if (state.Named) return Success();
        }
        var physicalDown = adapters.Where(a => Flag(a, "Hardware") && !Flag(a, "Virtual") && Number(a, "InterfaceType") == 6 && Text(a, "Status") == "Disconnected").ToArray();
        if (physicalDown.Length == 1 && !adapters.Any(a => Text(a, "Status") == "Up" && a.TryGetProperty("Gateways", out var g) && g.GetArrayLength() > 0))
        {
            var observation = await execute("wait_for_reconnect", new { kind = "network", interface_index = Number(physicalDown[0], "Index"), device_id = "" });
            if (!Flag(observation, "success") || !Flag(Data(observation), "Reconnected")) return "Não confirmei a reconexão. " + (Flag(observation, "success") ? Text(Data(observation), "Note") : Failure(observation));
            state = await Stabilize(); if (state.Named) return Success();
        }
        var probes = await execute("probe_network_interfaces", new { });
        if (!Flag(probes, "success")) return "Não consegui concluir a comparação das interfaces. Não vou escolher uma por tentativa. " + Failure(probes);
        if (!await Read()) return "Os testes terminaram, mas não consegui atualizar a configuração. Não fiz nova alteração.";
        var reports = Data(probes).GetProperty("Interfaces").EnumerateArray().ToArray();
        inform("Comparei as interfaces: " + string.Join("; ", reports.Select(r => Text(r, "Name") + (Flag(r, "TcpConnected") ? " respondeu" : Flag(r, "ProbeCompleted") ? " não respondeu aos testes" : " sem teste externo disponível"))) + ".");
        if (state.Numeric) return Partial();
        if (!allowChanges) return "Acompanhei a conexão sem alterar configurações. Os testes ainda não confirmaram acesso. Outros aparelhos nessa rede funcionam?";
        if (Flag(data, "Partial")) return "Algumas consultas do Windows falharam. Não há evidência suficiente para reconfigurar IP; confira os avisos do diagnóstico.";
        var targets = adapters.Where(a => Text(a, "Status") == "Up" && Text(a, "Dhcp") == "Disabled" && NetworkAssessment.CanConfigure(a)
            && reports.Any(r => Number(r, "Index") == Number(a, "Index") && Text(r, "Guid") == Text(a, "Guid") && Flag(r, "ProbeCompleted") && !Flag(r, "TcpConnected")))
            .OrderBy(a => (long)Number(a, "InterfaceMetric", 9999) + Number(a, "DefaultRouteMetric", 9999)).ThenByDescending(a => Flag(a, "Hardware")).ToArray();
        if (targets.Length > 0)
        {
            var selected = targets[0];
            inform("Os testes pela interface " + Text(selected, "Name") + " falharam e ela usa IP fixo. Vou apresentar a opção de obter IP automaticamente, preservando o DNS e guardando a configuração anterior. A rede precisa fornecer DHCP.");
            var applied = await execute("set_ipv4", new { interface_index = Number(selected, "Index"), mode = "dhcp", address = "", prefix = 0, gateway = "" });
            if (!Flag(applied, "success")) return "A alteração não foi concluída. " + Failure(applied) + (Flag(applied, "reverted") ? "\nA configuração anterior foi restaurada. Não vou repetir a mesma alteração. Pode ser necessário verificar o cabo, a porta ou o serviço DHCP da rede." : Flag(applied, "denied") ? "\nVocê recusou a proposta; ela não foi executada." : "\nConfira o resultado e o estado da restauração antes de outra alteração.");
            changed = true; state = await Stabilize();
            if (state.Named) return Success();
            if (state.Numeric) return Partial();
            return "A configuração autorizada foi aplicada, mas os testes após a espera não confirmaram acesso estável. Não considerei o problema resolvido. Outros aparelhos nessa rede estão funcionando?";
        }
        return "Verifiquei as interfaces e seus testes sem pedir nomes técnicos. Ainda não há evidência suficiente para mudar IP. Outros aparelhos nessa mesma rede conseguem acessar a internet?";
    }
}
