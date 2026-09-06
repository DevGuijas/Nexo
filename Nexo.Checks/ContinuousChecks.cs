using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

internal static class ContinuousChecks
{
    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        store.Data.AiEnabled = true;
        var fake = new FakeTools();
        using var handler = new ScriptedModel([
            Text("Vamos consultar as configurações da interface Ethernet."),
            Call("get_network", new { }),
            Call("finish_support", new { message = "Vamos testar a conexão com google.com na porta 443. Vamos fazer o teste." }),
            Call("test_connection", new { host = "google.com", port = 443 }),
            Call("finish_support", new { message = "A consulta e o teste terminaram; a conexão TCP funcionou." })
        ]);
        var conversation = Conversation(actor);
        var result = await RunModel(handler, fake, conversation);
        assert(handler.Calls == 5 && fake.Calls.SequenceEqual(new[] { "get_network", "test_connection" }) && result.Contains("terminaram"), "Promessas de consultas e testes continuam sem Ok, inclusive conclusão inválida");
        assert(conversation.AgentHistory.Count(m => m.role == "user") == 1 && conversation.Entries.Count == 1, "Continuação não fabrica consentimento ou mensagens do usuário");
        using var textControl = new ScriptedModel([Text("finish_support\n{\"message\":\"Resultado confirmado.\"}")]);
        assert((await RunModel(textControl, fake, Conversation(actor))) == "Resultado confirmado.", "Controle explícito em JSON aceito quando Qwen não emite tool_calls");
        assert(AgentProtocol.ParseControl("set_ipv4 {\"mode\":\"dhcp\"}") == null && AgentProtocol.ParseControl("finish_support {oops}") == null, "Parser de controle não converte texto em ação do Windows nem aceita JSON inválido");

        using var question = new ScriptedModel([Call("ask_user", new { reason = "missing_information", message = "O problema acontece em todos os sites ou apenas no aplicativo?" })]);
        assert((await RunModel(question, fake, Conversation(actor))).Contains("todos os sites") && question.Calls == 1, "Informação que depende do usuário pode pausar a investigação");
        assert(AgentProtocol.Validate("ask_user", JsonSerializer.SerializeToElement(new { reason = "ambiguous_target" }), "Qual interface você usa para internet: Ethernet ou Wi-Fi?") != null, "Modelo não pode transferir escolha técnica da interface ao usuário");
        using var approval = new ScriptedModel([
            Call("ask_user", new { reason = "missing_information", message = "Deseja que eu consulte o DNS?" }),
            Call("get_network", new { }),
            Call("finish_support", new { message = "Configuração consultada, nenhuma alteração aplicada." })]);
        assert((await RunModel(approval, fake, Conversation(actor))).Contains("consultada") && approval.Calls == 3, "Pedido de Ok para consulta é substituído por execução");
        using var loop = new ScriptedModel(Enumerable.Repeat(Text("Vou fazer o teste."), 4).ToArray());
        assert((await RunModel(loop, fake, Conversation(actor))).Contains("não conseguiu") && loop.Calls == 4, "Modelo sem chamadas termina com limitação explícita e limite de tentativas");
        using var readOnlyModel = new ScriptedModel([Call("restart_service", new { service = "bthserv" }), Call("finish_support", new { message = "Consulta encerrada sem alteração." })]);
        var readOnlyConversation = Conversation(actor); readOnlyConversation.Entries[0].Text = "Verifique meu Bluetooth. Apenas consulte; não faça alterações.";
        var beforeCalls = fake.Calls.Count;
        await RunModel(readOnlyModel, fake, readOnlyConversation);
        assert(fake.Calls.Count == beforeCalls && readOnlyConversation.AgentHistory.Any(m => m.role == "tool" && m.content.Contains("somente leitura")), "Pedido somente leitura bloqueia tentativa de alteração antes de chegar ao executor");
        assert(ToolCatalog.ForScope(true).All(d => ToolCatalog.IsReadOnly(JsonSerializer.SerializeToElement(d).GetProperty("function").GetProperty("name").GetString()!)), "Modelo em consulta recebe apenas ferramentas de leitura e controle");
        assert(AgentProtocol.Validate("finish_support", JsonSerializer.SerializeToElement(new { }), "Confirme se deseja obter IP automaticamente.", true) != null, "Conclusão em modo leitura não pede autorização para DHCP");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await new AgentService(store, fake).Run(actor, Conversation(actor), new Progress<string>(), (_, _) => { }, cancelled.Token); throw new Exception("Cancelamento ignorado"); }
        catch (OperationCanceledException) { assert(true, "Cancelamento interrompe ciclo contínuo"); }

        foreach (var scenario in new[] {
            ("fixo-sem-gateway", "Disabled", "192.168.99.10", Array.Empty<string>(), false, false, true),
            ("fixo-gateway-fora", "Disabled", "192.168.99.10", new[]{"192.168.1.1"}, false, false, true),
            ("fixo-coerente", "Disabled", "192.168.1.10", new[]{"192.168.1.1"}, false, false, false),
            ("consulta-parcial", "Disabled", "192.168.99.10", Array.Empty<string>(), true, false, false),
            ("rede-virtual", "Disabled", "192.168.99.10", Array.Empty<string>(), false, true, false),
            ("dhcp-ativo", "Enabled", "192.168.99.10", Array.Empty<string>(), false, false, false)
        })
        {
            var inventory = Inventory(scenario.Item2, scenario.Item3, scenario.Item4, scenario.Item5, scenario.Item6);
            var assessment = NetworkAssessment.Enrich(inventory).GetProperty("Assessment")[0];
            assert(assessment.GetProperty("DhcpCandidate").GetBoolean() == scenario.Item7, "Análise de endereço: " + scenario.Item1);
        }
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyperv-fixed-ip.json")));
        var reportAssessment = NetworkAssessment.Enrich(report.RootElement);
        assert(reportAssessment.GetProperty("InternetInterfaceCandidates").GetArrayLength() == 1 && reportAssessment.GetProperty("InternetInterfaceCandidates")[0].GetProperty("Index").GetInt32() == 3, "Topologia do log prioriza vEthernet com gateway, não Ethernet auxiliar");
        assert(!reportAssessment.GetProperty("Assessment")[1].GetProperty("DhcpCandidate").GetBoolean() && reportAssessment.GetProperty("Assessment")[2].GetProperty("CanConfigureIpDns").GetBoolean() && !reportAssessment.GetProperty("Assessment")[0].GetProperty("CanConfigureIpDns").GetBoolean(), "Correção permite vEthernet do host e preserva Default Switch e interface auxiliar");
        foreach (var restored in new[] { false, true })
        {
            using var failedTransaction = new ScriptedModel([Call("set_ipv4", new { interface_index = 3, mode = "dhcp", address = "", prefix = 0, gateway = "" })]);
            var failureAnswer = await RunModel(failedTransaction, new FailedTransaction(restored), Conversation(actor));
            assert(failedTransaction.Calls == 1 && failureAnswer.Contains(restored ? "restaurada e conferida" : "restauração automática não foi confirmada"), "Falha de transação encerra com estado de restauração claro, sem repetição · restored=" + restored);
        }
        store.Data.AiEnabled = false;

        async Task<string> RunModel(ScriptedModel model, IAgentTools tools, Conversation conv)
        {
            using var http = new HttpClient(model, false);
            return await new AgentService(store, tools, http).Run(actor, conv, new Progress<string>(), (_, _) => { }, CancellationToken.None);
        }
    }

    internal static Conversation Conversation(Account actor) => new() { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = "Quero investigar uma falha intermitente de rede." }] };
    internal static JsonElement Inventory(string dhcp, string ip, string[] gateways, bool partial = false, bool virtualAdapter = false) => JsonSerializer.SerializeToElement(new {
        Adapters = new[] { new { Index = 37, Guid = "00000000-0000-0000-0000-000000000037", Name = "Ethernet", Status = "Up", Disabled = false, Hardware = !virtualAdapter, Virtual = virtualAdapter, IpStackAvailable = true, Dhcp = dhcp,
            Addresses = new[] { new { IPAddress = ip, PrefixLength = 24, PrefixOrigin = dhcp == "Enabled" ? "Dhcp" : "Manual", AddressState = "Preferred" } }, Gateways = gateways, Dns = new[] { "8.8.8.8", "1.1.1.1" } } }, Partial = partial, Warnings = Array.Empty<string>()
    });
    private static string Text(string text) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = text } });
    private static string Call(string name, object args) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } } });
    private sealed class ScriptedModel(string[] responses) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Calls >= responses.Length) throw new Exception("Chamadas extras inesperadas ao modelo");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[Calls++], Encoding.UTF8, "application/json") });
        }
    }
    private sealed class FakeTools : IAgentTools
    {
        internal List<string> Calls = [];
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            Calls.Add(name);
            return Task.FromResult(name == "get_network" ? WindowsTools.Ok(NetworkAssessment.Enrich(Inventory("Enabled", "192.168.1.5", ["192.168.1.1"]))) : WindowsTools.Ok(new { dnsResolved = true, tcpConnected = true }));
        }
    }
    private sealed class FailedTransaction(bool restored) : IAgentTools
    {
        public Task<string> Execute(string name, JsonElement args, CancellationToken token) => Task.FromResult(JsonSerializer.Serialize(new { success = false, changed = true, error = "DHCP sem concessão.", reverted = restored, rollbackError = restored ? null : "MSFT_NetIPAddress não encontrado", backup = "before.json" }));
    }
}
