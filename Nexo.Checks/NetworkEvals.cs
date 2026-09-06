using System.Text.Json;
using System.Text.Json.Nodes;
using Nexo.Support;

internal static class NetworkEvals
{
    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        store.Data.AiEnabled = true; store.Data.Model = AgentService.DefaultModel;
        foreach (var scenario in new[] { new Scenario("log-real-vEthernet-indice-3", false, false, true), new Scenario("fixo-gateway-incompatível", false, false), new Scenario("fixo-válido", true, false), new Scenario("dhcp-recusado", false, true) })
        {
            var conversation = ContinuousChecks.Conversation(actor);
            conversation.Entries[0].Text = "Estou sem internet.";
            var answer = await new AgentService(store, scenario, workflowDelay: (_, _) => Task.CompletedTask).Run(actor, conversation, new Progress<string>(), (name, _) => Console.WriteLine("  " + name), CancellationToken.None);
            Console.WriteLine("  " + scenario.Id + ": " + answer);
            if (answer.Contains("não conseguiu")) foreach (var entry in conversation.AgentHistory.TakeLast(5)) Console.WriteLine("    TRACE " + entry.role + ": " + entry.content + " " + entry.tool_calls);
            assert(scenario.Reads > 0 && scenario.Changes == (scenario.Valid ? 0 : 1) && !scenario.Unexpected,
                "Fluxo de rede: " + scenario.Id + " seleciona ação por evidência no primeiro turno");
            if (!scenario.Deny) assert(scenario.Tests > 0 && (scenario.Valid || scenario.ReadsAfterChange > 0), "Fluxo de rede: consulta e verifica conexão sem exigir Ok · " + scenario.Id);
            assert(conversation.AgentHistory.Count(m => m.role == "user") == 1 && !answer.Contains("limite de etapas") && !answer.Contains("não conseguiu selecionar"), "Fluxo de rede: conclui dentro do limite · " + scenario.Id);
        }
    }
    private sealed class Scenario(string id, bool valid, bool deny, bool fromReport = false) : IAgentTools
    {
        internal string Id => id;
        internal bool Valid => valid;
        internal bool Deny => deny;
        internal int Reads, Changes, Tests, ReadsAfterChange;
        internal bool Unexpected;
        private bool changed;
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            if (name == "probe_network_interfaces")
            {
                using var snapshot = JsonDocument.Parse(Execute("get_network", args, token).GetAwaiter().GetResult());
                return Task.FromResult(WindowsTools.Ok(new { Interfaces = snapshot.RootElement.GetProperty("data").GetProperty("Adapters").EnumerateArray().Select(a => new { Index = a.GetProperty("Index").GetInt32(), Guid = a.GetProperty("Guid").GetString(), Name = a.GetProperty("Name").GetString(), ProbeCompleted = a.GetProperty("Gateways").GetArrayLength() > 0, TcpConnected = changed || valid }).ToArray() }));
            }
            if (name == "get_network")
            {
                Reads++; if (changed) ReadsAfterChange++;
                if (fromReport)
                {
                    var inventory = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyperv-fixed-ip.json")))!;
                    if (changed)
                    {
                        var target = inventory["Adapters"]!.AsArray().Single(a => a!["Index"]!.GetValue<int>() == 3)!;
                        target["Dhcp"] = "Enabled";
                        target["Addresses"] = JsonSerializer.SerializeToNode(new[] { new { IPAddress = "192.168.15.4", PrefixLength = 24, PrefixOrigin = "Dhcp", AddressState = "Preferred" } });
                        target["Gateways"] = JsonSerializer.SerializeToNode(new[] { "192.168.15.1" });
                    }
                    return Task.FromResult(WindowsTools.Ok(NetworkAssessment.Enrich(JsonSerializer.SerializeToElement(inventory))));
                }
                return Task.FromResult(WindowsTools.Ok(NetworkAssessment.Enrich(ContinuousChecks.Inventory(changed ? "Enabled" : "Disabled", changed || valid ? "192.168.1.5" : "192.168.99.5", ["192.168.1.1"]))));
            }
            if (name == "test_connection")
            { Tests++; return Task.FromResult(WindowsTools.Ok(new { host = args.GetProperty("host").GetString(), dnsResolved = changed || valid, tcpConnected = changed || valid })); }
            if (name == "set_ipv4")
            {
                Changes++;
                _ = NetworkChange.Parse(name, args);
                if (Reads == 0 || args.GetProperty("interface_index").GetInt32() != (fromReport ? 3 : 37) || args.GetProperty("mode").GetString() != "dhcp" || valid || Changes > 1 || (fromReport && Tests == 0))
                { Unexpected = true; throw new InvalidOperationException("Mudança incorreta ou repetida"); }
                if (deny) return Task.FromResult(JsonSerializer.Serialize(new { success = false, denied = true, message = "Usuário recusou na confirmação. Não repetir nem alterar configurações." }));
                changed = true;
                using var after = JsonDocument.Parse(Execute("get_network", JsonSerializer.SerializeToElement(new { }), token).GetAwaiter().GetResult());
                return Task.FromResult(WindowsTools.Ok(new { Change = new { success = true, message = "Usuário aceitou DHCP e confirmou manter a configuração.", before = new { Dhcp = "Disabled" }, after = new { Dhcp = "Enabled", IPAddress = "192.168.1.5", Gateway = "192.168.1.1" } }, NetworkAfter = after.RootElement.GetProperty("data").Clone(), InternetVerified = false }));
            }
            Unexpected = true; throw new InvalidOperationException("Ferramenta indevida: " + name);
        }
    }
}
