using System.Text.Json;
using Nexo.Support;

internal static class ObservationChecks
{
    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        var events = new List<string>();
        var sequence = new Queue<PhysicalState>([new(true, "usb1"), new(false, "usb1"), new(true, "usb1")]);
        var result = await PhysicalObservation.Wait(_ => Task.FromResult(sequence.Dequeue()), events.Add, CancellationToken.None, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
        assert(result.Reconnected && result.DisconnectedObserved && events.Count == 2, "Reconexão exige observar ausência e retorno da mesma identidade");
        var unchanged = await PhysicalObservation.Wait(_ => Task.FromResult(new PhysicalState(true, "usb1")), _ => { }, CancellationToken.None, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(1));
        assert(!unchanged.Reconnected && unchanged.TimedOut, "Dispositivo sempre presente não é reconexão confirmada");
        sequence = new([new(true, "usb1"), new(false, "usb1"), new(true, "usb2")]);
        var different = await PhysicalObservation.Wait(_ => Task.FromResult(sequence.Dequeue()), _ => { }, CancellationToken.None, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
        assert(!different.Reconnected && !different.TimedOut, "Outro dispositivo não é confundido com o alvo observado");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await PhysicalObservation.Wait(_ => Task.FromResult(new PhysicalState(true, "usb1")), _ => throw new Exception("Instrução após cancelar"), cancel.Token, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1)); throw new Exception("Cancelamento ignorado"); }
        catch (OperationCanceledException) { assert(true, "Observação física respeita cancelamento"); }

        var evidence = new InvestigationEvidence();
        var ip = JsonSerializer.SerializeToElement(new { host = "8.8.8.8", port = 53 });
        var dns = JsonSerializer.SerializeToElement(new { host = "google.com", port = 443 });
        evidence.Observe("test_connection", "ip", ip, WindowsTools.Ok(new { host = "8.8.8.8", dnsResolved = true, tcpConnected = true, elapsedMs = 37 }));
        evidence.Observe("test_connection", "dns", dns, WindowsTools.Ok(new { host = "google.com", dnsResolved = false, tcpConnected = false }));
        evidence.Observe("test_connection", "dns", dns, WindowsTools.Ok(new { host = "google.com", dnsResolved = false, tcpConnected = false, elapsedMs = 999 }));
        assert(evidence.Summary().Contains("conectividade parcial") && evidence.Summary().Contains("Nenhuma alteração concluída") && evidence.Repeated("dns"), "Cenário do log: TCP disponível + DNS falho é parcial, sem atribuir conserto ao Nexo");
        evidence.Observe("enable_network_adapter", "noop", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(new { Activation = new { success = true, data = new { Changed = false } } }));
        assert(evidence.Summary().Contains("Nenhuma alteração concluída") && evidence.Repeated("dns"), "Operação sem mudança não recebe crédito por recuperação nem apaga evidências");
        evidence.Observe("set_dns", "change", JsonSerializer.SerializeToElement(new { }), WindowsTools.Ok(new { Changed = true }));
        assert(!evidence.Repeated("dns") && !evidence.Summary().Contains("8.8.8.8"), "Mudança invalida testes anteriores e permite verificar novamente");
        var automatic = NetworkChange.Parse("set_dns", JsonSerializer.SerializeToElement(new { interface_index = 3, mode = "automatic" }));
        assert(automatic.Servers.Length == 0, "DNS automático aceita lista omitida, sem inventar servidores");
        try { NetworkChange.Parse("set_dns", JsonSerializer.SerializeToElement(new { interface_index = 3, mode = "manual" })); throw new Exception("DNS manual incompleto aceito"); }
        catch (InvalidOperationException ex) { assert(ex.Message.Contains("servers"), "DNS manual incompleto retorna erro claro antes da autorização"); }
        var asks = 0;
        var tools = new WindowsTools(store, actor, _ => { asks++; return Task.FromResult(false); }, new Progress<string>());
        try { await tools.Execute("wait_for_reconnect", JsonSerializer.SerializeToElement(new { kind = "device", device_id = "USB\\inventado", interface_index = 0 }), CancellationToken.None); throw new Exception("Dispositivo inventado aceito"); }
        catch (InvalidOperationException) { assert(asks == 0, "Observação exige identidade previamente consultada"); }

        foreach (var observeOnly in new[] { false, true })
        {
            var calls = new List<string>();
            var answer = await NetworkWorkflow.Run((name, args) =>
            {
                calls.Add(name);
                if (name == "probe_network_interfaces") return Task.FromResult(JsonSerializer.SerializeToElement(new { success = true, data = new { Interfaces = Array.Empty<object>() } }));
                if (name == "get_network") return Task.FromResult(JsonSerializer.SerializeToElement(new { success = true, data = NetworkAssessment.Enrich(ContinuousChecks.Inventory("Disabled", "182.0.0.2", ["182.0.0.1"])) }));
                if (name != "test_connection") throw new Exception("Alteração injustificada: " + name);
                var host = JsonSerializer.SerializeToElement(args).GetProperty("host").GetString();
                return Task.FromResult(JsonSerializer.SerializeToElement(new { success = true, data = new { host, dnsResolved = false, tcpConnected = !observeOnly && host == "8.8.8.8" } }));
            }, _ => { }, CancellationToken.None, allowChanges: !observeOnly);
            assert(calls.Count == 5 && (observeOnly ? answer.Contains("sem alterar configurações") : answer.Contains("conectividade parcial") && answer.Contains("Nenhuma alteração")), observeOnly ? "Pedido de observação não configura DHCP mesmo com testes falhos" : "Fluxo do último log encerra com evidência parcial, sem DHCP nem consultas repetidas");
        }
    }
}
