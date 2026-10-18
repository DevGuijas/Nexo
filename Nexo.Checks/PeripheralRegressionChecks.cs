using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// All Windows operations in this suite are simulated. The HTTP handler scripts the
// model's tool calls, so these cases are repeatable even when Ollama is unavailable.
internal static class PeripheralRegressionChecks
{
    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        var previousAi = store.Data.AiEnabled;
        store.Data.AiEnabled = true;
        try
        {
            var firstSamples = 0;
            var firstWatch = await PhysicalObservation.Wait(_ => { firstSamples++; return Task.FromResult(new PhysicalState(true, "USB\\HEADSET_TEST")); },
                _ => { }, CancellationToken.None, TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(1));
            var delayedStates = new Queue<PhysicalState>([
                new(true, "USB\\HEADSET_TEST"), new(true, "USB\\HEADSET_TEST"), new(true, "USB\\HEADSET_TEST"),
                new(false, "USB\\HEADSET_TEST"), new(false, "USB\\HEADSET_TEST"), new(true, "USB\\HEADSET_TEST")]);
            var secondSamples = 0;
            var secondWatch = await PhysicalObservation.Wait(_ => { secondSamples++; return Task.FromResult(delayedStates.Dequeue()); },
                _ => { }, CancellationToken.None, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
            Console.WriteLine($"REGRESSÃO reconexão-atrasada | esperado: primeira tentativa inconclusiva; retorno do mesmo USB apenas na segunda observação | obtido: primeiro timeout={firstWatch.TimedOut}, reconectou={firstWatch.Reconnected}, amostras={firstSamples}; segundo reconectou={secondWatch.Reconnected}, amostras={secondSamples} | evidência: {secondWatch.Note}");
            assert(firstWatch.TimedOut && !firstWatch.Reconnected && secondWatch.DisconnectedObserved && secondWatch.Reconnected && secondSamples >= 6,
                "Reconexão simulada: primeira espera inconclusiva e recuperação atrasada do mesmo periférico");

            await Check("camera-absent", "Minha câmera não aparece no aplicativo.", [],
                "Nenhum dispositivo identificado; nenhuma correção",
                (s, answer, model) => s.Reads == 1 && s.Attempts == 0 && model.Calls == 0 && answer.Contains("não listou nenhum dispositivo"), assert);

            await Check("camera-ambiguous", "Minha câmera não aparece no aplicativo.",
                [Call("ask_user", new { reason = "ambiguous_target", message = "Encontrei Câmera interna e Webcam USB. Em qual delas o aplicativo falha?" })],
                "Duas câmeras: pedir sintoma que identifique o alvo antes de alterar",
                (s, answer, model) => s.Reads == 1 && s.Attempts == 0 && model.Calls == 1 && answer.Contains("Câmera interna") && answer.Contains("Webcam USB"), assert);

            await Check("camera-success", "Minha câmera não aparece no aplicativo.",
                [Call("enable_camera", new { device_id = CameraId })],
                "Câmera Code 22 habilitada e teste de imagem pedido",
                (s, answer, model) => s.Reads == 1 && s.Attempts == 1 && s.Applied == 1 && s.Approvals == 1 && model.Calls == 1
                    && answer.Contains("imagem") && answer.Contains("Funcionou"), assert);

            await Check("audio-first-failure", "Estou sem som em todos os aplicativos.",
                [Call("get_services", new { }), Call("restart_service", new { service = "Audiosrv" }),
                 Call("finish_support", new { message = "Reiniciei o áudio e resolvi o problema." }),
                 Call("finish_support", new { message = "A tentativa de reiniciar o serviço de áudio falhou. O som ainda não foi confirmado; encaminhe à equipe de TI se persistir." })],
                "Falha da primeira correção: parar imediatamente ou rejeitar alegação falsa, sem resolução declarada",
                (s, answer, model) => s.Reads == 2 && s.Attempts == 1 && s.Applied == 0
                    && (model.Calls == 2 || model.Calls == 4 && model.SawRejectedControl)
                    && (answer.Contains("falhou") || answer.Contains("não foi"))
                    && !answer.Contains("Reiniciei") && !answer.Contains("resolvi o problema"), assert);

            foreach (var failure in new[] { "audio-exception", "audio-timeout", "audio-access-denied" })
                await Check(failure, "Estou sem som em todos os aplicativos.",
                    [Call("get_services", new { }), Call("restart_service", new { service = "Audiosrv" })],
                    "Falha da ferramenta, prazo excedido ou acesso negado: parar sem declarar reparo",
                    (s, answer, model) => s.Reads == 2 && s.Attempts == 1 && s.Applied == 0 && model.Calls == 2
                        && answer.Contains("não foi concluída") && !answer.Contains("Funcionou?"), assert);

            await Check("audio-success", "Estou sem som em todos os aplicativos.",
                [Call("get_services", new { }), Call("restart_service", new { service = "Audiosrv" })],
                "Serviço de áudio voltou a Running; solicitar teste de som no aplicativo",
                (s, answer, model) => s.Reads == 2 && s.Attempts == 1 && s.Applied == 1 && s.Approvals == 1 && model.Calls == 2
                    && answer.Contains("reproduza um áudio") && answer.Contains("Funcionou"), assert);

            await Check("bluetooth-denied", "Meu Bluetooth desapareceu do Windows.",
                [Call("enable_device", new { device_id = BluetoothId })],
                "Recusa interrompe o atendimento sem habilitar Bluetooth",
                (s, answer, model) => s.Reads == 1 && s.Attempts == 1 && s.Applied == 0 && s.Approvals == 1 && model.Calls == 1
                    && answer.Contains("recusou") && answer.Contains("não foi executada"), assert);

            await Check("bluetooth-absent", "Meu Bluetooth desapareceu do Windows.", [],
                "Rádio ausente do inventário: nenhuma ação ou ID inventado",
                (s, answer, model) => s.Reads == 1 && s.Attempts == 0 && model.Calls == 0 && answer.Contains("não listou nenhum dispositivo"), assert);

            await Check("bluetooth-success", "Meu Bluetooth desapareceu do Windows.",
                [Call("enable_device", new { device_id = BluetoothId })],
                "Dispositivo Bluetooth voltou a Code 0; solicitar teste de uso",
                (s, answer, model) => s.Reads == 1 && s.Attempts == 1 && s.Applied == 1 && s.Approvals == 1 && model.Calls == 1
                    && answer.Contains("teste o periférico") && answer.Contains("Funcionou"), assert);

            await Check("printer-ambiguous", "Documentos presos na fila da impressora.",
                [Call("ask_user", new { reason = "ambiguous_target", message = "Encontrei Impressora USB e Impressora de rede com trabalhos pendentes. Qual delas recebeu o documento que falhou?" })],
                "Duas filas: pedir escolha baseada no documento antes de limpar",
                (s, answer, model) => s.Reads == 1 && s.Attempts == 0 && model.Calls <= 1
                    && answer.Contains("Impressora USB") && answer.Contains("Impressora de rede"), assert);

            await Check("printer-absent", "A impressora não imprime meus documentos.",
                [Call("finish_support", new { message = "Nenhuma impressora instalada foi encontrada. Informe se é uma impressora USB ou de rede para direcionar a verificação." })],
                "Inventário vazio: não inventar fila nem tentar limpeza",
                (s, answer, model) => s.Reads == 1 && s.Attempts == 0 && model.Calls == 1 && answer.Contains("Nenhuma impressora instalada"), assert);

            await Check("printer-success", "Documentos presos na fila da impressora.",
                [Call("clear_print_queue", new { printer_name = "Impressora USB" })],
                "Fila específica limpa; solicitar impressão de teste",
                (s, answer, model) => s.Reads == 1 && s.Attempts == 1 && s.Applied == 1 && s.Approvals == 1 && model.Calls == 1
                    && answer.Contains("envie um documento pequeno") && answer.Contains("Funcionou"), assert);
        }
        finally { store.Data.AiEnabled = previousAi; }

        async Task Check(string id, string prompt, string[] replies, string expected,
            Func<Scenario, string, ScriptedModel, bool> passes, Action<bool, string> check)
        {
            var scenario = new Scenario(id);
            using var model = new ScriptedModel(replies);
            using var http = new HttpClient(model, disposeHandler: false);
            var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = prompt }] };
            var watch = Stopwatch.StartNew();
            var answer = await new AgentService(store, scenario, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
            watch.Stop();
            var questions = conversation.AgentHistory.Count(m => m.role == "assistant" && m.tool_calls?.ToString().Contains("ask_user") == true);
            var observed = $"leituras={scenario.Reads}, tentativas={scenario.Attempts}, aplicadas={scenario.Applied}, decisões_de_autorização_simuladas={scenario.Approvals}, perguntas={questions}, modelo={model.Calls}, duração={watch.ElapsedMilliseconds}ms";
            Console.WriteLine($"REGRESSÃO {id} | esperado: {expected} | obtido: {observed} | evidência: {answer}");
            check(!scenario.Unexpected && passes(scenario, answer, model), "Periférico simulado: " + id + " · " + expected);
        }
    }

    private const string CameraId = "USB\\CAMERA_TEST";
    private const string BluetoothId = "BTH\\RADIO_TEST";
    private static string Call(string name, object args) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } } });

    private sealed class ScriptedModel(string[] replies) : HttpMessageHandler
    {
        internal int Calls;
        internal bool SawRejectedControl;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var payload = await request.Content!.ReadAsStringAsync(token);
            using (var doc = JsonDocument.Parse(payload))
                SawRejectedControl |= doc.RootElement.GetProperty("messages").EnumerateArray().Any(message =>
                    message.TryGetProperty("role", out var role) && role.GetString() == "tool"
                    && message.TryGetProperty("tool_name", out var name) && name.GetString() == "finish_support"
                    && message.TryGetProperty("content", out var content) && content.GetString()?.Contains("\"success\":false") == true);
            if (Calls >= replies.Length) throw new InvalidOperationException("O modelo recebeu uma chamada adicional inesperada.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(replies[Calls++], Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Scenario(string id) : IAgentTools
    {
        internal int Reads, Attempts, Applied, Approvals;
        internal bool Unexpected;
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            if (name == "diagnose_device")
            {
                Reads++;
                var topic = args.GetProperty("topic").GetString();
                var expected = id.StartsWith("camera") ? "camera" : id.StartsWith("audio") ? "audio" : "bluetooth";
                if (topic != expected) return Wrong(name);
                var devices = id switch
                {
                    "camera-absent" or "bluetooth-absent" => Array.Empty<Device>(),
                    "camera-ambiguous" => new[] { new Device("Câmera interna", "PCI\\CAMERA_TEST", 0, "OK"), new Device("Webcam USB", CameraId, 0, "OK") },
                    "camera-success" => new[] { new Device("Webcam USB", CameraId, 22, "Error") },
                    "bluetooth-denied" or "bluetooth-success" => new[] { new Device("Rádio Bluetooth", BluetoothId, 22, "Error") },
                    _ => new[] { new Device("Alto-falante", "USB\\AUDIO_TEST", 0, "OK") }
                };
                return Task.FromResult(WindowsTools.Ok(new { count = devices.Length, devices, privacy = "Allow", note = "Code 0 não comprova funcionamento no aplicativo." }));
            }
            if (name == "get_services" && id.StartsWith("audio"))
            {
                Reads++;
                return Task.FromResult(WindowsTools.Ok(JsonSerializer.Serialize(new[] { new { Name = "Audiosrv", Status = "Stopped" } })));
            }
            if (name == "get_printers" && id.StartsWith("printer"))
            {
                Reads++;
                object[] printers = id == "printer-absent" ? Array.Empty<object>() : id == "printer-ambiguous"
                    ? new[] { new { Name = "Impressora USB", Jobs = 1 }, new { Name = "Impressora de rede", Jobs = 1 } }
                    : new[] { new { Name = "Impressora USB", Jobs = 1 } };
                return Task.FromResult(WindowsTools.Ok(JsonSerializer.Serialize(printers)));
            }
            if (name is "enable_camera" or "enable_device" or "restart_service" or "clear_print_queue")
            {
                Attempts++; Approvals++;
                var appropriate = id switch
                {
                    "camera-success" => name == "enable_camera" && args.GetProperty("device_id").GetString() == CameraId,
                    "audio-first-failure" or "audio-exception" or "audio-timeout" or "audio-access-denied" or "audio-success" => name == "restart_service" && args.GetProperty("service").GetString() == "Audiosrv",
                    "bluetooth-denied" or "bluetooth-success" => name == "enable_device" && args.GetProperty("device_id").GetString() == BluetoothId,
                    "printer-success" => name == "clear_print_queue" && args.GetProperty("printer_name").GetString() == "Impressora USB",
                    _ => false
                };
                if (!appropriate || Reads == 0 || Attempts > 1) return Wrong(name);
                if (id == "bluetooth-denied") return Task.FromResult(JsonSerializer.Serialize(new { success = false, denied = true, message = "Usuário recusou na confirmação simulada." }));
                if (id == "audio-first-failure") return Task.FromResult(JsonSerializer.Serialize(new { success = false, error = "O serviço Audiosrv permaneceu Stopped após a tentativa." }));
                if (id == "audio-access-denied") return Task.FromResult(JsonSerializer.Serialize(new { success = false, error = "O Windows negou acesso ao serviço Audiosrv." }));
                if (id == "audio-exception") throw new IOException("Falha simulada do executor após autorização.");
                if (id == "audio-timeout") throw new TimeoutException("Prazo simulado excedido durante a operação.");
                Applied++;
                return Task.FromResult(WindowsTools.Ok(new { Changed = true, verified = true, Status = "Running", Code = 0, RemainingJobs = 0,
                    note = "Estado posterior consultado; o teste funcional no aplicativo depende do usuário." }));
            }
            return Wrong(name);
        }
        private Task<string> Wrong(string name)
        {
            Unexpected = true;
            throw new InvalidOperationException("Ferramenta ou alvo indevido no cenário " + id + ": " + name);
        }
    }
}
