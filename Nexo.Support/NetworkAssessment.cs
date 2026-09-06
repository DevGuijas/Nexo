using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexo.Support;

// Evidence derived from the Windows inventory, independent of the language model.
public static class NetworkAssessment
{
    public static bool CanConfigure(JsonElement adapter)
    {
        string Text(string key) => adapter.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        bool Flag(string key) => adapter.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
        var name = Text("Name");
        if (new[] { "Default Switch", "Comutador Padrão", "Switch Padrão" }.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) return false;
        return (Flag("Hardware") && !Flag("Virtual")) || System.Text.RegularExpressions.Regex.IsMatch(Text("Description"), @"^Hyper-V Virtual Ethernet Adapter( #\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public static JsonElement Enrich(JsonElement inventory)
    {
        var root = JsonNode.Parse(inventory.GetRawText())!.AsObject();
        var assessment = new JsonArray();
        var partial = root["Partial"]?.GetValue<bool>() == true;
        var routed = (root["Adapters"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(a => a["Status"]?.GetValue<string>() == "Up" && (a["Gateways"] as JsonArray)?.Count > 0).ToArray();
        root["InternetInterfaceCandidates"] = JsonSerializer.SerializeToNode(routed.Select(a => new { Index = a["Index"]?.GetValue<int>(), Name = a["Name"]?.GetValue<string>() }));
        foreach (var node in root["Adapters"]?.AsArray() ?? [])
        {
            if (node is not JsonObject adapter) continue;
            var dhcp = adapter["Dhcp"]?.GetValue<string>();
            var addresses = (adapter["Addresses"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
            var gateways = (adapter["Gateways"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).Where(s => s != null).ToArray();
            var fixedIp = dhcp == "Disabled" || addresses.Any(a => a["PrefixOrigin"]?.GetValue<string>() == "Manual");
            var findings = new List<string>();
            var inconsistent = false;
            if (fixedIp) findings.Add("IPv4 fixo detectado. Ter um endereço IP não comprova que ele pertence à rede atual. IP fixo sozinho não é defeito.");
            if (partial) findings.Add("Inventário parcial: campos ausentes não comprovam defeito de configuração.");
            if (gateways.Length == 0 && !partial && addresses.Length > 0)
            {
                findings.Add("Sem gateway padrão IPv4. Isso pode impedir acesso externo por esta interface; redes locais isoladas e rotas específicas podem funcionar sem gateway padrão.");
                // Split default routes can intentionally replace 0.0.0.0/0 (e.g. VPN).
                inconsistent = adapter["HasSplitDefaultRoute"]?.GetValue<bool>() != true;
            }
            foreach (var gateway in gateways)
            {
                if (addresses.Length > 0 && !partial && addresses.All(a => OutsideSubnet(a, gateway!)))
                {
                    findings.Add("Gateway " + gateway + " fora das sub-redes IPv4 observadas. Verifique IP e prefixo antes de atribuir a falha ao DNS.");
                    inconsistent = true;
                }
            }
            foreach (var address in addresses)
            {
                if (address["IPAddress"]?.GetValue<string>()?.StartsWith("169.254.", StringComparison.Ordinal) == true)
                { findings.Add("Endereço local 169.254/16 (APIPA): não comprova obtenção de uma concessão DHCP utilizável para internet."); inconsistent = true; }
                if (address["AddressState"]?.GetValue<string>() == "Duplicate")
                { findings.Add("Windows identificou endereço IPv4 duplicado."); inconsistent = true; }
            }
            var configurable = CanConfigure(JsonSerializer.SerializeToElement(adapter));
            var candidate = fixedIp && inconsistent && adapter["Status"]?.GetValue<string>() == "Up" && configurable
                && (routed.Length == 0 || gateways.Length > 0);
            if (gateways.Length == 0 && routed.Length > 0) findings.Add("Outra interface tem gateway padrão. Não selecionar esta interface como causa da falta de internet apenas por não ter gateway: ela pode ser auxiliar ou sustentar um switch Hyper-V.");
            assessment.Add(new JsonObject
            {
                ["Index"] = adapter["Index"]?.DeepClone(), ["Name"] = adapter["Name"]?.DeepClone(),
                ["AddressMode"] = fixedIp ? "static" : dhcp == "Enabled" ? "dhcp" : "unknown",
                ["Findings"] = JsonSerializer.SerializeToNode(findings), ["DhcpCandidate"] = candidate,
                ["CanConfigureIpDns"] = configurable, ["HasDefaultGateway"] = gateways.Length > 0,
                ["NextStep"] = candidate
                    ? "Possível correção, se esta interface for usada para internet: Obter um endereço IP automaticamente (DHCP). Depende da finalidade da interface, disponibilidade de DHCP na rede e autorização para mudar. Disponibilidade de DHCP ainda não foi comprovada; DNS é preservado."
                    : fixedIp && gateways.Length > 0 && configurable ? "Interface com gateway padrão e IP fixo. Priorize-a na investigação da falta de internet, mesmo sendo vEthernet. Se os testes falharem, DHCP é uma alternativa a apresentar com confirmação e restauração. IP e gateway na mesma sub-rede não provam que são os valores corretos da rede."
                    : fixedIp ? "Se houver falha de conexão, avalie rota e testes; pergunte se a rede usava IP automático quando não houver evidência suficiente para propor uma alteração. Não substitua IP fixo válido só por ser fixo."
                    : "Investigue os achados e teste a conectividade conforme o problema."
            });
        }
        root["Assessment"] = assessment;
        root["ConnectivityScope"] = "Inventário não comprova internet. test_connection usa a rota escolhida pelo Windows, não uma interface específica.";
        return JsonSerializer.SerializeToElement(root);
    }

    private static bool OutsideSubnet(JsonObject address, string gateway)
    {
        // Unknown/point-to-point prefixes cannot establish an inconsistency.
        if (!IPAddress.TryParse(address["IPAddress"]?.GetValue<string>(), out var ip)
            || !IPAddress.TryParse(gateway, out var gw) || ip.AddressFamily != AddressFamily.InterNetwork
            || gw.AddressFamily != AddressFamily.InterNetwork || address["PrefixLength"]?.GetValue<int>() is not int prefix || prefix is < 1 or > 30) return false;
        var left = ip.GetAddressBytes(); var right = gw.GetAddressBytes();
        for (var bit = 0; bit < prefix; bit++)
            if ((left[bit / 8] & (1 << (7 - bit % 8))) != (right[bit / 8] & (1 << (7 - bit % 8)))) return true;
        return false;
    }
}
