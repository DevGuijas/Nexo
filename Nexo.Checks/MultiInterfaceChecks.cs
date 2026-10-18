using System.Text.Json;
using Nexo.Support;

internal static class MultiInterfaceChecks
{
    internal static async Task Run(Action<bool, string> assert)
    {
        await AmbiguousDisabledAdapters(assert);
        await UnchangedActivation(assert);
        foreach (var scenario in new[] { "delayed-activation", "two-gateways", "rollback", "stale-identity", "transient-success", "cancel-wait" })
        {
            var changed = false; var testsAfter = 0; var waits = 0; var probes = 0; var alterations = new List<int>();
            var activate = scenario is "delayed-activation" or "transient-success" or "cancel-wait";
            JsonElement Inventory() => JsonSerializer.SerializeToElement(new { Adapters = new[] {
                new { Index = 4, Guid = "physical-4", Name = "Ethernet", Hardware = true, Virtual = false, Description = "Realtek", Disabled = activate && !changed, Status = activate && !changed ? "Disabled" : "Up", Dhcp = changed || activate ? "Enabled" : "Disabled", InterfaceMetric = 25, DefaultRouteMetric = 0, Gateways = new[] { "199.168.0.1" } },
                new { Index = 3, Guid = "virtual-3", Name = "vEthernet laboratório", Hardware = false, Virtual = true, Description = "Hyper-V Virtual Ethernet Adapter #2", Disabled = false, Status = "Up", Dhcp = "Disabled", InterfaceMetric = 25, DefaultRouteMetric = 256, Gateways = new[] { "182.0.0.1" } },
                new { Index = 12, Guid = "virtual-12", Name = "vEthernet (Default Switch)", Hardware = false, Virtual = true, Description = "Hyper-V Virtual Ethernet Adapter", Disabled = false, Status = "Up", Dhcp = "Disabled", InterfaceMetric = 5000, DefaultRouteMetric = 0, Gateways = Array.Empty<string>() }
            }, Partial = false });
            Task<JsonElement> Execute(string name, object args)
            {
                object Ok(object value) => new { success = true, data = value };
                object result;
                if (name == "get_network") result = Ok(Inventory());
                else if (name == "probe_network_interfaces")
                {
                    probes++;
                    result = Ok(new { Interfaces = new[] {
                        new { Index = 4, Guid = scenario == "stale-identity" ? "new-identity" : "physical-4", Name = "Ethernet", ProbeCompleted = true, TcpConnected = false },
                        new { Index = 3, Guid = "virtual-3", Name = "vEthernet laboratório", ProbeCompleted = true, TcpConnected = true },
                        new { Index = 12, Guid = "virtual-12", Name = "Default Switch", ProbeCompleted = false, TcpConnected = false }
                    } });
                }
                else if (name == "test_connection")
                {
                    var named = JsonSerializer.SerializeToElement(args).GetProperty("host").GetString() == "google.com";
                    if (changed && named) testsAfter++;
                    var ready = changed && (scenario == "transient-success" ? testsAfter % 2 == 0 : testsAfter >= (activate ? 3 : 1));
                    result = Ok(new { dnsResolved = ready, tcpConnected = ready });
                }
                else if (name is "enable_network_adapter" or "set_ipv4")
                {
                    var index = JsonSerializer.SerializeToElement(args).GetProperty("interface_index").GetInt32(); alterations.Add(index);
                    if (index != 4 || alterations.Count > 1) throw new Exception("Alterou interface válida ou repetiu alteração");
                    changed = scenario != "rollback";
                    result = changed ? Ok(new { Changed = true }) : new { success = false, changed = true, reverted = true, error = "DHCP sem concessão, restaurado." };
                }
                else throw new Exception("Ferramenta inesperada: " + name);
                return Task.FromResult(JsonSerializer.SerializeToElement(result));
            }
            Task Delay(int ms, CancellationToken token) { waits++; if (scenario == "cancel-wait") throw new OperationCanceledException(); return Task.CompletedTask; }
            try
            {
                var answer = await NetworkWorkflow.Run(Execute, _ => { }, CancellationToken.None, delay: Delay);
                assert(!answer.Contains("Qual interface") && !answer.Contains("Qual conexão"), "Sem pergunta técnica sobre interface: " + scenario);
                if (scenario == "delayed-activation") assert(waits == 4 && testsAfter == 4 && alterations.SequenceEqual([4]) && answer.Contains("responderam"), "Ativação aguarda duas falhas e duas confirmações separadas antes de concluir");
                if (scenario == "two-gateways") assert(probes == 1 && alterations.SequenceEqual([4]) && waits == 2 && answer.Contains("responderam"), "Dois gateways: testa todas, corrige Ethernet falha e preserva vEthernet que responde");
                if (scenario == "rollback") assert(probes == 1 && alterations.Count == 1 && answer.Contains("restaurada") && !answer.Contains("responderam"), "DHCP revertido é informado sem repetir nem declarar resolução");
                if (scenario == "stale-identity") assert(alterations.Count == 0, "Resultado de teste com identidade antiga não autoriza selecionar interface reutilizada");
                if (scenario == "transient-success") assert(waits == 6 && !answer.Contains("responderam"), "Sucesso intermitente não é recuperação estável");
            }
            catch (OperationCanceledException) when (scenario == "cancel-wait") { assert(waits == 1 && alterations.Count == 1, "Espera de estabilização respeita cancelamento"); }
        }
    }

    private static async Task AmbiguousDisabledAdapters(Action<bool, string> assert)
    {
        var activations = new List<int>();
        var reads = 0;
        var probes = 0;
        Task<JsonElement> Execute(string name, object args)
        {
            object result;
            switch (name)
            {
                case "get_network":
                    reads++;
                    result = new { success = true, data = new { Adapters = new[] {
                        new { Index = 4, Guid = "physical-4", Name = "Ethernet", Hardware = true, Virtual = false, InterfaceType = 6, Disabled = true, Status = "Disabled", Dhcp = "Enabled", Gateways = Array.Empty<string>() },
                        new { Index = 6, Guid = "physical-6", Name = "Wi-Fi", Hardware = true, Virtual = false, InterfaceType = 71, Disabled = true, Status = "Disabled", Dhcp = "Enabled", Gateways = Array.Empty<string>() }
                    }, Partial = false } };
                    break;
                case "test_connection":
                    result = new { success = true, data = new { dnsResolved = false, tcpConnected = false } };
                    break;
                case "probe_network_interfaces":
                    probes++;
                    result = new { success = true, data = new { Interfaces = new[] {
                        new { Index = 4, Guid = "physical-4", Name = "Ethernet", ProbeCompleted = false, TcpConnected = false },
                        new { Index = 6, Guid = "physical-6", Name = "Wi-Fi", ProbeCompleted = false, TcpConnected = false }
                    } } };
                    break;
                case "enable_network_adapter":
                    activations.Add(JsonSerializer.SerializeToElement(args).GetProperty("interface_index").GetInt32());
                    result = new { success = true, data = new { changed = true } };
                    break;
                default:
                    throw new Exception("Ferramenta inesperada no caso ambíguo: " + name);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }

        var answer = await NetworkWorkflow.Run(Execute, _ => { }, CancellationToken.None, delay: (_, _) => Task.CompletedTask);
        Console.WriteLine("  Múltiplas interfaces desativadas: leituras=" + reads + ", sondagens=" + probes + ", ativações=" + string.Join(",", activations) + "; resposta=" + answer);
        assert(reads > 0 && activations.Count == 0 && answer.Contains("cabo") && answer.Contains("Wi-Fi") && !answer.Contains("responderam"),
            "Múltiplas interfaces Disabled sem evidência discriminante: nenhuma ativação por tentativa e pergunta sobre o tipo de conexão");
    }

    private static async Task UnchangedActivation(Action<bool, string> assert)
    {
        var enableCalls = 0;
        var networkReads = 0;
        var waits = 0;
        Task<JsonElement> Execute(string name, object args)
        {
            object result;
            switch (name)
            {
                case "get_network":
                    networkReads++;
                    result = new { success = true, data = new { Adapters = new[] {
                        new { Index = 17, Guid = "physical-17", Name = "Ethernet de teste", Hardware = true, Virtual = false, InterfaceType = 6,
                            Disabled = enableCalls == 0, Status = enableCalls == 0 ? "Disabled" : "Up", Dhcp = "Enabled", Gateways = Array.Empty<string>() }
                    }, Partial = false } };
                    break;
                case "test_connection":
                    result = new { success = true, data = new { dnsResolved = false, tcpConnected = false } };
                    break;
                case "enable_network_adapter":
                    if (JsonSerializer.SerializeToElement(args).GetProperty("interface_index").GetInt32() != 17) throw new Exception("Índice diferente do observado.");
                    enableCalls++;
                    result = new { success = true, data = new { changed = false, note = "A interface já estava habilitada no momento da operação." } };
                    break;
                default:
                    throw new Exception("Ação extra indevida após ativação sem mudança: " + name);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
        var answer = await NetworkWorkflow.Run(Execute, _ => { }, CancellationToken.None,
            delay: (_, _) => { waits++; return Task.CompletedTask; });
        Console.WriteLine($"  Ativação obsoleta: leituras={networkReads}, tentativas={enableCalls}, esperas={waits}; resposta={answer}");
        assert(enableCalls == 1 && networkReads > 1 && answer.Contains("já não estava desativada") && !answer.Contains("foi ativada") && !answer.Contains("responderam"),
            "Ativação sem mudança: interface obsoleta não recebe crédito por correção nem dispara outra alteração");
    }
}
