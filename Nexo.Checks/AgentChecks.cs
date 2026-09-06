using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

internal static class AgentChecks
{
    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        store.Data.AiEnabled = true;
        var handler = new ScriptedModel();
        using var http = new HttpClient(handler);
        var executor = new FakeTools();
        var agent = new AgentService(store, executor, http);
        var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = "Minha conexão falhou" }] };
        var result = await agent.Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
        assert(result.Contains("Verifiquei") && executor.Calls == 1 && handler.Calls == 2, "Agente chama ferramenta e continua após o resultado");
        assert(conversation.AgentHistory.Any(m => m.role == "tool"), "Resultado de ferramenta preservado no histórico estruturado");
        var approvals = 0;
        var denied = new WindowsTools(store, actor, _ => { approvals++; return Task.FromResult(false); }, new Progress<string>());
        var rejection = await denied.Execute("flush_dns", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        assert(approvals == 1 && rejection.Contains("denied"), "Correção recusada não executa o comando");
        try { await denied.Execute("set_dns", JsonSerializer.SerializeToElement(new { interface_index = 1, mode = "manual", servers = new[] { "1.1.1.1" } }), CancellationToken.None); throw new Exception("Mudança sem consulta passou"); }
        catch (InvalidOperationException) { assert(true, "Mudança de rede exige observação prévia da interface"); }
        try { await denied.Execute("run_shell", JsonSerializer.SerializeToElement(new { command = "whoami" }), CancellationToken.None); throw new Exception("Shell livre aceito"); }
        catch (InvalidOperationException) { assert(true, "Ferramenta desconhecida recusada"); }
        store.Data.AiEnabled = false;
    }
    internal static async Task Live(Store store, Account actor, Action<bool, string> assert)
    {
        store.Data.AiEnabled = true; store.Data.Model = AgentService.DefaultModel;
        await LocalModel.EnsureRunning(CancellationToken.None);
        foreach (var prompt in new[] { "Verifique meus dispositivos Bluetooth e diga quais estão disponíveis. Apenas consulte; não faça alterações.", "Veja as impressoras e o serviço de impressão deste PC. Apenas consulte, sem alterar nada.", "Confira meus adaptadores, IP, DNS e gateway. Não mude a rede." })
        {
            var events = new Progress<string>(s => Console.WriteLine("  " + s));
            var requestedChanges = 0;
            var executor = new WindowsTools(store, actor, _ => { requestedChanges++; return Task.FromResult(false); }, events);
            var agent = new AgentService(store, executor);
            var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = prompt }] };
            var calls = new List<string>();
            var successful = 0;
            var result = await agent.Run(actor, conversation, events, (name, output) => { calls.Add(name); using var doc=JsonDocument.Parse(output); if(doc.RootElement.GetProperty("success").GetBoolean()) successful++; Console.WriteLine("  Ferramenta: " + name); }, CancellationToken.None);
            Console.WriteLine("  Resposta: " + result);
            assert(successful > 0 && result.Length > 0, "IA instalada acionou ferramentas reais: " + prompt);
            assert(requestedChanges == 0 && !result.Contains("não conseguiu selecionar") && !result.Contains("limite de etapas") && !result.Contains("Confirme se deseja"), "Consulta real conclui sem acionar alterações nem pedir autorização");
        }
        var deniedCount = 0;
        var restricted = new WindowsTools(store, actor, _ => { deniedCount++; return Task.FromResult(false); }, new Progress<string>());
        var change = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = "Limpe o cache DNS deste computador agora, usando sua ferramenta." }] };
        var outputText = await new AgentService(store, restricted).Run(actor, change, new Progress<string>(), (_, _) => { }, CancellationToken.None);
        Console.WriteLine("  Resposta à ação negada: " + outputText);
        assert(deniedCount == 1 && change.AgentHistory.Any(m => m.role == "tool" && m.content.Contains("denied")), "IA solicita uma correção real e respeita a recusa do executor");
    }
    private sealed class FakeTools : IAgentTools
    {
        internal int Calls;
        public Task<string> Execute(string name, JsonElement args, CancellationToken token) { Calls++; if (name != "get_network") throw new Exception("Ferramenta inesperada"); return Task.FromResult("{\"success\":true,\"data\":\"adaptador de teste\"}"); }
    }
    private sealed class ScriptedModel : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Calls == 2 && !payload.Contains("adaptador de teste")) throw new Exception("O modelo não recebeu o resultado da ferramenta");
            var json = Calls == 1 ? """{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"get_network","arguments":{}}}]}}""" : """{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"finish_support","arguments":{"message":"Verifiquei o adaptador de teste."}}}]}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
