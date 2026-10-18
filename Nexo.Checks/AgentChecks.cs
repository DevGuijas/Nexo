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
        await FlowRegression(store, actor, assert);
        store.Data.AiEnabled = false;
    }
    private static async Task FlowRegression(Store store, Account actor, Action<bool, string> assert)
    {
        static Conversation ConversationFor(Account user, string request) => new() { UserId = user.Id, Entries = [new Entry { Role = "user", Text = request }] };
        static string Call(string name, object args, bool includeContent = true) => includeContent
            ? JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } } })
            : JsonSerializer.Serialize(new { message = new { role = "assistant", tool_calls = new[] { new { function = new { name, arguments = args } } } } });
        async Task<string> Run(string request, IAgentTools tools, params string[] responses)
        {
            using var model = new ModelSequence(responses);
            using var http = new HttpClient(model);
            return await new AgentService(store, tools, http).Run(actor, ConversationFor(actor, request), new Progress<string>(), (_, _) => { }, CancellationToken.None);
        }

        var services = new FlowTools((name, _) => name == "get_services" ? WindowsTools.Ok("bthserv: Stopped") : throw new Exception("Ferramenta inesperada"));
        var falseClaim = await Run("Verifique o serviço Bluetooth", services,
            Call("get_services", new { }),
            Call("finish_support", new { message = "Reiniciei o Bluetooth e resolvi o problema." }),
            Call("finish_support", new { message = "A consulta terminou. Nenhuma alteração foi concluída." }));
        assert(services.Calls.SequenceEqual(["get_services"]) && falseClaim.Contains("Nenhuma alteração") && !falseClaim.Contains("Reiniciei"), "Consulta não sustenta alegação de correção ou resolução");
        assert(!AgentProtocol.UserConfirmedFunction("Está funcionando?") && AgentProtocol.UserConfirmedFunction("Funcionou, obrigado."), "Pergunta não equivale a confirmação funcional");

        var cameraResult = WindowsTools.Ok(new { count = 1, devices = new[] { new { Id = "USB\\CAM1", Name = "Webcam de teste", Code = 22, Status = "Error" } } });
        var cameraTools = new FlowTools((name, _) => name == "diagnose_device" ? cameraResult : throw new Exception("Ação fora do incidente chegou ao executor"));
        var cameraAnswer = await Run("Minha câmera não funciona", cameraTools,
            Call("restart_service", new { service = "Spooler" }),
            Call("finish_support", new { message = "A câmera foi identificada, mas nenhuma alteração foi concluída." }));
        assert(cameraTools.Calls.SequenceEqual(["diagnose_device"]) && cameraAnswer.Contains("nenhuma alteração"), "Ferramenta de impressão não altera Windows em incidente de câmera");

        var twoCameras = WindowsTools.Ok(new { count = 2, devices = new[] {
            new { Id = "USB\\CAM1", Name = "Câmera interna", Code = 22, Status = "Error" },
            new { Id = "USB\\CAM2", Name = "Webcam USB", Code = 22, Status = "Error" } } });
        var ambiguousCameras = new FlowTools((name, _) => name == "diagnose_device" ? twoCameras : throw new Exception("Câmera ambígua chegou ao executor"));
        var cameraQuestion = await Run("Minha câmera não funciona", ambiguousCameras,
            Call("enable_camera", new { device_id = "USB\\CAM1" }),
            Call("ask_user", new { reason = "ambiguous_target", message = "Encontrei duas câmeras desabilitadas. Qual delas falha no aplicativo?" }));
        assert(ambiguousCameras.Calls.SequenceEqual(["diagnose_device"]) && cameraQuestion.Contains('?') && cameraQuestion.Contains("Câmera interna") && cameraQuestion.Contains("Webcam USB"), "Duas câmeras possíveis bloqueiam escolha arbitrária do modelo");

        var twoPrinters = WindowsTools.Ok("""[{"Name":"Impressora USB","Jobs":[{"ID":1}]},{"Name":"Impressora de rede","Jobs":[{"ID":2}]}]""");
        var ambiguousPrinters = new FlowTools((name, _) => name == "get_printers" ? twoPrinters : throw new Exception("Fila ambígua chegou ao executor"));
        var printerQuestion = await Run("Minha impressora não imprime", ambiguousPrinters,
            Call("clear_print_queue", new { printer_name = "Impressora USB" }),
            Call("ask_user", new { reason = "ambiguous_target", message = "Qual impressora recebeu o documento que falhou?" }));
        assert(ambiguousPrinters.Calls.SequenceEqual(["get_printers"]) && printerQuestion.Contains('?') && printerQuestion.Contains("Impressora USB") && printerQuestion.Contains("Impressora de rede"), "Duas filas possíveis bloqueiam limpeza arbitrária do modelo");

        var malformedTools = new FlowTools((_, _) => throw new Exception("Resposta malformada não pode acionar ferramenta"));
        var malformed = await Run("Verifique o serviço", malformedTools, "{broken");
        assert(malformed.Contains("resposta inválida") && malformedTools.Calls.Count == 0, "JSON inválido do modelo encerra sem executar ações");

        var noContentTools = new FlowTools((name, _) => name == "get_services" ? WindowsTools.Ok("serviço consultado") : throw new Exception("Ferramenta inesperada"));
        var noContent = await Run("Verifique serviços", noContentTools,
            Call("get_services", new { }, includeContent: false),
            Call("finish_support", new { message = "Consulta concluída." }));
        assert(noContentTools.Calls.SequenceEqual(["get_services"]) && noContent == "Consulta concluída.", "Resposta só com tool_calls é aceita sem content");

        var cancelledTools = new FlowTools((_, _) => throw new OperationCanceledException("Cancelado durante a ferramenta"));
        using (var model = new ModelSequence([Call("get_services", new { }), Call("finish_support", new { message = "Concluído." })]))
        using (var http = new HttpClient(model))
        {
            try
            {
                await new AgentService(store, cancelledTools, http).Run(actor, ConversationFor(actor, "Verifique serviços"), new Progress<string>(), (_, _) => { }, CancellationToken.None);
                throw new Exception("Cancelamento foi ignorado");
            }
            catch (OperationCanceledException)
            {
                assert(model.Calls == 1 && cancelledTools.Calls.Count == 1, "Cancelamento durante ferramenta impede próximas chamadas do modelo");
            }
        }
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
    private sealed class FlowTools(Func<string, JsonElement, string> execute) : IAgentTools
    {
        internal List<string> Calls { get; } = [];
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            Calls.Add(name);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(execute(name, args));
        }
    }
    private sealed class ModelSequence(string[] responses) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Calls >= responses.Length) throw new Exception("Modelo recebeu mais chamadas que o roteiro.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[Calls++], Encoding.UTF8, "application/json") });
        }
    }
}
