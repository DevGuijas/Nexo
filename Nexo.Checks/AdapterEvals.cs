using System.Text.Json;
using Nexo.Support;

internal static class AdapterEvals
{
    public static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        store.Data.AiEnabled = true; store.Data.Model = AgentService.DefaultModel;
        await LocalModel.EnsureRunning(CancellationToken.None);
        foreach (var scenario in new[] {
            new Scenario("virtual-sem-ip", 3, "vEthernet (Rede de teste)", "Boa tarde. Estou sem conexão.", false),
            new Scenario("indice-diferente", 77, "Wi-Fi laboratório", "Desativei a interface Wi-Fi laboratório. Consegue ativar?", false),
            new Scenario("recusa", 19, "Ethernet Escritório", "Ative a interface Ethernet Escritório que desativei.", true)
        })
        {
            var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = scenario.Prompt }] };
            var agent = new AgentService(store, scenario, workflowDelay: (_, _) => Task.CompletedTask);
            var answer = await agent.Run(actor, conversation, new Progress<string>(), (name, _) => Console.WriteLine("  " + name), CancellationToken.None);
            Console.WriteLine("  Resposta: " + answer);
            var controller = NetworkWorkflow.Matches(scenario.Prompt) ? "Fluxo de rede" : "Modelo real";
            assert(scenario.EnableCalls == 1 && scenario.NetworkCalls >= (scenario.Deny ? 1 : 2) && !scenario.UnexpectedAction, controller + ": " + scenario.Id + " · observar, selecionar, agir e verificar");
            assert(!answer.Contains("não conseguiu selecionar") && !answer.Contains("limite de etapas"), controller + " conclui o atendimento: " + scenario.Id);
            if (!scenario.Deny) assert(scenario.Tests >= 2, "Ativação exige verificação estável também fora do fluxo comum: " + scenario.Id);
        }
    }
    private sealed class Scenario(string id, int index, string adapterName, string prompt, bool deny) : IAgentTools
    {
        public string Id => id;
        public string Prompt => prompt;
        public bool Deny => deny;
        public int EnableCalls; public int NetworkCalls; public int Tests; public bool UnexpectedAction;
        private bool enabled;
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            if (name == "get_network")
            {
                NetworkCalls++;
                return Task.FromResult(WindowsTools.Ok(new { Adapters = new object[] {
                    new { Index = 5, Name = "Ethernet auxiliar", Status = "Up", Disabled = false, Virtual = false, IpStackAvailable = true, Addresses = new[]{"192.0.2.10/24"}, Gateways = Array.Empty<string>(), Dns = Array.Empty<string>() },
                    new { Index = index, Guid = "00000000-0000-0000-0000-000000000001", Name = adapterName, Status = enabled ? "Up" : "Disabled", Disabled = !enabled, CanEnable = !enabled, Virtual = true, IpStackAvailable = enabled, Addresses = enabled ? new[]{"192.0.2.50/24"} : [], Gateways = enabled ? new[]{"192.0.2.1"} : [], Dns = enabled ? new[]{"192.0.2.53"} : [] }
                }, Warnings = Array.Empty<string>(), Partial = false, Note = "A interface desativada pode não ter registro na pilha IP. Ativar não comprova conectividade à internet." }));
            }
            if (name == "enable_network_adapter")
            {
                EnableCalls++;
                if (NetworkCalls == 0 || args.GetProperty("interface_index").GetInt32() != index) { UnexpectedAction = true; throw new InvalidOperationException("Índice incorreto ou ausência de consulta."); }
                if (deny) return Task.FromResult(JsonSerializer.Serialize(new { success = false, denied = true, message = "Usuário recusou. Não repetir." }));
                enabled = true;
                var after = JsonSerializer.Deserialize<JsonElement>(awaitResult());
                return Task.FromResult(WindowsTools.Ok(new { Activation = new { success = true, Changed = true, Before = "Disabled", After = "Up", Name = adapterName }, NetworkAfter = after, InternetVerified = false }));

                string awaitResult() => Execute("get_network", JsonSerializer.SerializeToElement(new { }), token).GetAwaiter().GetResult();
            }
            if (name == "test_connection") { Tests++; return Task.FromResult(WindowsTools.Ok(new { dnsResolved = enabled, tcpConnected = enabled })); }
            UnexpectedAction = true; throw new InvalidOperationException("Ferramenta não esperada neste cenário: " + name);
        }
    }
}
