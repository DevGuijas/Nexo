using System.Text.Json;
using System.Text.RegularExpressions;
using Nexo.Support;

internal static class ComponentEvals
{
    public static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        store.Data.AiEnabled = true; store.Data.Model = AgentService.DefaultModel;
        await LocalModel.EnsureRunning(CancellationToken.None);
        foreach (var prompt in new[] { "Bom dia", "Bom dia, nexo!", "Está funcionando. Obrigado!", "Você também ajuda com impressoras?" })
        {
            var tools = new Scenario("social");
            var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = prompt }] };
            var answer = await new AgentService(store, tools).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
            Console.WriteLine(prompt + " → " + answer);
            assert(!tools.Unexpected && tools.Reads == 0 && !answer.Contains("não conseguiu") && !answer.Contains("Nenhuma alteração") && answer.Length > 10, "Qwen conversa sem acionar Windows: " + prompt);
        }
        var failures = new List<string>();
        void Check(bool condition, string label)
        {
            if (condition) assert(true, label);
            else { failures.Add(label); Console.WriteLine("FAIL: " + label); }
        }
        foreach (var topic in new[] { "camera", "audio", "bluetooth", "printer", "camera-absent", "bluetooth-denied", "camera-ambiguous", "printer-ambiguous" })
        {
            var fake = new Scenario(topic);
            var prompt = topic switch {
                "audio" => "Estou sem som em todos os aplicativos, pode investigar e corrigir?",
                "printer" => "Os documentos estão presos na fila da impressora, pode investigar e corrigir?",
                "printer-ambiguous" => "Tenho duas impressoras instaladas e as duas têm trabalho preso na fila. Não sei qual recebeu meu documento. Pode investigar?",
                "bluetooth" or "bluetooth-denied" => "Meu Bluetooth desapareceu do Windows, pode investigar e corrigir?",
                "camera-ambiguous" => "Tenho câmera interna e webcam USB. A imagem não aparece no Teams e não sei qual câmera ele usa. Pode investigar?",
                _ => "Bom dia! Minha câmera não aparece no Teams, pode investigar e corrigir?"
            };
            var conversation = new Conversation { UserId = actor.Id, Entries = [new Entry { Role = "user", Text = prompt }] };
            var answer = await new AgentService(store, fake).Run(actor, conversation, new Progress<string>(), (name, result) => Console.WriteLine(name + " " + result[..Math.Min(result.Length, 120)]), CancellationToken.None);
            Console.WriteLine(topic + " → " + answer);
            var noTarget = topic == "camera-absent" || topic.EndsWith("ambiguous");
            Check(fake.Reads > 0 && !fake.Unexpected && fake.Changes == (noTarget ? 0 : 1), "Agente observa o componente e respeita as ações esperadas: " + topic);
            Check(!answer.Contains("não conseguiu selecionar") && !answer.Contains("limite de etapas"), "Conclui o cenário sem esgotar o ciclo: " + topic);
            if (topic == "bluetooth-denied") Check(answer.Contains("recusou"), "Recusa da correção Bluetooth é respeitada");
            else if (topic.EndsWith("ambiguous")) Check(answer.Contains('?') && Regex.IsMatch(answer, "(?i)qual|câmera|webcam|impressora"), "Alvos ambíguos geram pergunta discriminante sem mudança: " + topic);
            else if (topic != "camera-absent") Check(Regex.IsMatch(answer, "(?i)test|abra|funcionou|tente"), "Solicita teste funcional após estado do Windows: " + topic);
            if (topic == "audio")
            {
                var previousReads = fake.Reads;
                conversation.Entries.Add(new Entry { Role = "assistant", Text = answer });
                conversation.Entries.Add(new Entry { Role = "user", Text = "Agora está funcionando, saiu som. Obrigado!" });
                var thanks = await new AgentService(store, fake).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
                Console.WriteLine("audio-confirmado → " + thanks);
                Check(!fake.Unexpected && fake.Changes == 1 && fake.Reads == previousReads && !thanks.Contains("não conseguiu"), "Confirmação após reparo mantém contexto sem repetir investigação");
            }
        }
        if (failures.Count > 0) throw new Exception("Falhas de avaliação com Qwen: " + string.Join(" | ", failures));
    }
    private sealed class Scenario(string scenario) : IAgentTools
    {
        public int Reads, Changes;
        public bool Unexpected;
        private string Topic => scenario.Split('-')[0];
        private readonly string id = "USB\\COMPONENT_TEST";
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            if (scenario == "social") { Unexpected = true; throw new Exception("Uma conversa acionou ferramenta: " + name); }
            if (name == "diagnose_device" && args.GetProperty("topic").GetString() == Topic)
            {
                Reads++;
                var devices = scenario switch
                {
                    "camera-absent" => Array.Empty<Device>(),
                    "camera-ambiguous" => new[] { new Device("Câmera interna", "PCI\\INTERNAL_CAMERA_TEST", 22, "Error"), new Device("Webcam USB", "USB\\EXTERNAL_CAMERA_TEST", 22, "Error") },
                    "printer-ambiguous" => new[] { new Device("Impressora USB", "USB\\PRINTER_ONE_TEST", 0, "OK"), new Device("Impressora de rede", "SWD\\PRINTER_TWO_TEST", 0, "OK") },
                    _ => new[] { new Device("Periférico de teste", id, Changes > 0 || Topic is "audio" or "printer" ? 0 : 22, Changes > 0 || Topic is "audio" or "printer" ? "OK" : "Error") }
                };
                return Task.FromResult(WindowsTools.Ok(new { count = devices.Length, devices, privacy = "Allow", note = "Code 22 = desabilitado; Code 0 = sem erro de PnP, não comprova funcionamento no aplicativo." }));
            }
            if (name == "get_services")
            {
                Reads++;
                return Task.FromResult(WindowsTools.Ok(JsonSerializer.Serialize(new[] { new { Name = "Audiosrv", Status = Topic == "audio" && Changes == 0 ? "Stopped" : "Running" }, new { Name = "Spooler", Status = "Running" }, new { Name = "bthserv", Status = "Running" } })));
            }
            if (name == "get_printers" && Topic == "printer")
            {
                Reads++;
                if (scenario == "printer-ambiguous")
                    return Task.FromResult(WindowsTools.Ok(JsonSerializer.Serialize(new[] {
                        new { Name = "Impressora USB", Status = "Error", Driver = "Test", Port = "USB001", Jobs = new[] { new { ID = 1, JobStatus = "Error" } } },
                        new { Name = "Impressora de rede", Status = "Error", Driver = "Test", Port = "IP_192.0.2.20", Jobs = new[] { new { ID = 2, JobStatus = "Error" } } }
                    })));
                return Task.FromResult(WindowsTools.Ok(JsonSerializer.Serialize(new[] { new { Name = "Impressora de teste", Status = "Normal", Driver = "Test", Port = "USB001", Jobs = Changes > 0 ? Array.Empty<object>() : new object[] { new { ID = 1, JobStatus = "Error" } } } })));
            }
            if (scenario.EndsWith("ambiguous") && ToolCatalog.IsMutation(name))
            {
                Unexpected = true;
                return Task.FromResult(JsonSerializer.Serialize(new { success = false, error = "Inventário ambíguo: alteração sem alvo confirmado." }));
            }
            var appropriate = Reads > 0 && Changes == 0 && scenario != "camera-absent" &&
                ((Topic is "camera" or "bluetooth" && name is "enable_device" or "enable_camera" && args.GetProperty("device_id").GetString() == id)
                 || (Topic == "audio" && name == "restart_service" && args.GetProperty("service").GetString() == "Audiosrv")
                 || (Topic == "printer" && (name == "clear_print_queue" && args.GetProperty("printer_name").GetString() == "Impressora de teste" || name == "restart_service" && args.GetProperty("service").GetString() == "Spooler")));
            if (appropriate)
            {
                Changes++;
                if (scenario.EndsWith("denied")) return Task.FromResult(JsonSerializer.Serialize(new { success = false, denied = true, message = "Usuário recusou na confirmação simulada." }));
                return Task.FromResult(WindowsTools.Ok(new { Name = "Periférico de teste", Id = id, ConfigManagerErrorCode = 0, Status = "Running", RemainingJobs = 0, note = "Operação autorizada em simulação, estado posterior consultado; ainda precisa de teste funcional no aplicativo." }));
            }
            Unexpected = true; throw new Exception("Ferramenta sem relação ou ação sem identificação: " + name);
        }
    }
}
