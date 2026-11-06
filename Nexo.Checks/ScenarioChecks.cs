using Nexo.Support;

namespace Nexo.Checks;

internal static class ScenarioChecks
{
    public static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        HealthSnapshot At(int offset) => new() { Time = now.AddSeconds(offset), CollectionStatus = "complete" };
        var disabled = new[] { At(-30), At(0) }; foreach (var sample in disabled) { sample.Connectivity = "none"; sample.Adapters.Add(new AdapterHealth("adapter-1", "Wi-Fi", "Disabled", false)); }
        check(HealthRules.Evaluate(disabled, new()).Any(x => x.Id.StartsWith("adapter-disabled")), "Cenário 1: adaptador desativado sem rede detectado");
        disabled[1].Connectivity = "local";
        check(HealthRules.Evaluate(disabled, new()).All(x => !x.Id.StartsWith("adapter-disabled")), "Cenário 1: outra rede local evita falso alerta e exige verificação funcional");
        var wrong = NetworkAssessment.Enrich(ContinuousChecks.Inventory("Disabled", "192.168.99.5", ["192.168.1.1"]));
        check(wrong.GetProperty("Assessment")[0].GetProperty("DhcpCandidate").GetBoolean(), "Cenário 2: IP e gateway incompatíveis geram candidato à investigação");
        var coherent = NetworkAssessment.Enrich(ContinuousChecks.Inventory("Disabled", "192.168.1.5", ["192.168.1.1"]));
        check(!coherent.GetProperty("Assessment")[0].GetProperty("DhcpCandidate").GetBoolean(), "Cenário 2: IP fixo coerente não recebe correção automática");
        var evidence = new InvestigationEvidence();
        evidence.Observe("test_connection", "ip", System.Text.Json.JsonSerializer.SerializeToElement(new { host = "8.8.8.8", port = 53 }), WindowsTools.Ok(new { host = "8.8.8.8", dnsResolved = true, tcpConnected = true }));
        evidence.Observe("test_connection", "dns", System.Text.Json.JsonSerializer.SerializeToElement(new { host = "example.com", port = 443 }), WindowsTools.Ok(new { host = "example.com", dnsResolved = false, tcpConnected = false }));
        check(evidence.Summary().Contains("conectividade parcial"), "Cenário 3: DNS falho com TCP por IP preservado é falha parcial");
        var noRoute = new InvestigationEvidence();
        noRoute.Observe("test_connection", "ip", System.Text.Json.JsonSerializer.SerializeToElement(new { host = "8.8.8.8", port = 53 }), WindowsTools.Ok(new { host = "8.8.8.8", dnsResolved = true, tcpConnected = false }));
        noRoute.Observe("test_connection", "dns", System.Text.Json.JsonSerializer.SerializeToElement(new { host = "example.com", port = 443 }), WindowsTools.Ok(new { host = "example.com", dnsResolved = false, tcpConnected = false }));
        check(!noRoute.Summary().Contains("conectividade parcial"), "Cenário 3: falha TCP e DNS não é atribuída apenas a DNS");
        check(AgentProtocol.Validate("finish_support", System.Text.Json.JsonSerializer.SerializeToElement(new { message = "" }), "Vamos reiniciá-lo para verificar se resolve.") != null, "Agente não encerra prometendo reinício sem ferramenta");
        var disk = new[] { At(-30), At(0) }; foreach (var sample in disk) sample.Disks.Add(new DiskHealth("C:", 3, 100));
        check(HealthRules.Evaluate(disk, new()).Any(x => x.Id.StartsWith("disk-low")), "Cenário 7: volume quase cheio detectado");
        disk[1].Disks.Clear(); disk[1].Disks.Add(new DiskHealth("C:", 30, 100));
        check(HealthRules.Evaluate(disk, new()).All(x => !x.Id.StartsWith("disk-low")), "Cenário 7: espaço recuperado encerra condição de alerta");
        var memory = new[] { At(-60), At(-30), At(0) }; memory[0].MemoryUsedPercent = 50; memory[1].MemoryUsedPercent = 67; memory[2].MemoryUsedPercent = 85;
        check(HealthRules.Evaluate(memory, new()).Any(x => x.Id == "memory-growing"), "Cenário 5: crescimento de memória detectado");
        memory[2].MemoryUsedPercent = 60;
        check(HealthRules.Evaluate(memory, new()).All(x => x.Id != "memory-growing"), "Cenário 5: pico revertido não é crescimento persistente");
        var service = new[] { At(-30), At(0) }; foreach (var sample in service) sample.Services.Add(new ServiceHealth("Audiosrv", "Stopped", "Automatic"));
        check(HealthRules.Evaluate(service, new()).Any(x => x.Id.StartsWith("service-stopped")), "Cenário 4: serviço automático parado detectado");
        service[1].Services.Clear(); service[1].Services.Add(new ServiceHealth("Audiosrv", "Running", "Automatic"));
        check(HealthRules.Evaluate(service, new()).All(x => !x.Id.StartsWith("service-stopped")), "Cenário 4: serviço em execução encerra condição, sem provar som funcional");
        check(DriverStoreInventory.MatchesObserved("PCI\\VEN_1234", "PCI\\VEN_1234", "oem4.inf") && !DriverStoreInventory.MatchesObserved("PCI\\VEN_1234", "PCI\\VEN_9999", "oem4.inf"), "Cenário 6: pacote de driver só corresponde ao dispositivo observado");
        using (var validation = System.Text.Json.JsonDocument.Parse(WindowsTools.VerifyDriverInstallationResult(WindowsTools.Ok(new { Verification = "unconfirmed", BeforeVersion = "1.0", AfterVersion = "1.0" }))))
            check(!validation.RootElement.GetProperty("success").GetBoolean(), "Cenário 6: instalação relatada sem nova versão continua não confirmada");
        var root = Path.Combine(Path.GetTempPath(), "nexo-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new HealthSettings { IntervalSeconds = 45, CpuHighPercent = 88, DiskLowFreePercent = 12, RetentionDays = 14 };
            settings.Save(root);
            var loaded = HealthSettings.Load(root);
            check(loaded.IntervalSeconds == 45 && loaded.CpuHighPercent == 88 && loaded.RetentionDays == 14, "Preferências de saúde persistem localmente");
            using (var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "health-settings.json"))))
                check(saved.RootElement.TryGetProperty("CheckGateway", out var gatewayCheck) && gatewayCheck.ValueKind == System.Text.Json.JsonValueKind.False
                    && saved.RootElement.TryGetProperty("CheckDns", out var dnsCheck) && dnsCheck.ValueKind == System.Text.Json.JsonValueKind.False
                    && saved.RootElement.TryGetProperty("CheckExternal", out var externalCheck) && externalCheck.ValueKind == System.Text.Json.JsonValueKind.False,
                    "Sondagens de rede ficam desativadas por padrão no arquivo local");
            var configured = System.Text.Json.JsonSerializer.Deserialize<HealthSettings>("""{"CheckGateway":true,"CheckDns":true,"CheckExternal":true,"DnsProbeHost":"example.com","HttpProbeUrl":"https://example.com/check","HttpExpectedText":"OK"}""")!;
            configured.Save(root);
            using (var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "health-settings.json"))))
                check(saved.RootElement.GetProperty("CheckGateway").GetBoolean()
                    && saved.RootElement.GetProperty("DnsProbeHost").GetString() == "example.com"
                    && saved.RootElement.GetProperty("HttpExpectedText").GetString() == "OK",
                    "Alvos e opções de sondagem persistem no perfil");
            var invalid = System.Text.Json.JsonSerializer.Deserialize<HealthSettings>("""{"CheckExternal":true,"HttpProbeUrl":"file:///C:/secret","HttpExpectedText":"OK"}""")!;
            var invalidRejected = false;
            try { invalid.Save(root); } catch (ArgumentException) { invalidRejected = true; }
            check(invalidRejected, "Sondagem externa rejeita endereço fora de HTTP(S)");
            var ipAsDns = System.Text.Json.JsonSerializer.Deserialize<HealthSettings>("""{"CheckDns":true,"DnsProbeHost":"1.2.3.4"}""")!;
            var ipRejected = false;
            try { ipAsDns.Save(root); } catch (ArgumentException) { ipRejected = true; }
            check(ipRejected, "Sondagem DNS rejeita IP literal que não consultaria um nome");
            var emptyExpectation = System.Text.Json.JsonSerializer.Deserialize<HealthSettings>("""{"CheckExternal":true,"HttpExpectedText":""}""")!;
            var emptyRejected = false;
            try { emptyExpectation.Save(root); } catch (ArgumentException) { emptyRejected = true; }
            check(emptyRejected, "Sondagem HTTP exige resposta esperada para não aceitar qualquer página");
            var disabledWithDraft = new HealthSettings { CheckDns = false, CheckExternal = false, DnsProbeHost = "1.2.3.4", HttpProbeUrl = "incompleto", HttpExpectedText = "" };
            disabledWithDraft.Save(root);
            check(HealthSettings.Load(root).IntervalSeconds == 30,
                "Campos de sondagem desligada não impedem salvar outras preferências");
            using (var snapshot = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new HealthSnapshot())))
                check(snapshot.RootElement.TryGetProperty("GatewayProbe", out var gatewayProbe) && gatewayProbe.GetProperty("State").GetString() == "disabled",
                    "Nova amostra diferencia sondagem desligada de falha de rede");
            File.WriteAllText(Path.Combine(root, "health-settings.json"), """{"IntervalSeconds":45,"RetentionDays":14}""");
            var legacySettings = HealthSettings.Load(root);
            check(!legacySettings.CheckGateway && !legacySettings.CheckDns && !legacySettings.CheckExternal,
                "Preferência antiga carrega sem ativar consultas de rede");
            loaded.IntervalSeconds = 1;
            try { loaded.Save(root); throw new Exception("FAIL: intervalo inválido aceito"); }
            catch (ArgumentOutOfRangeException) { check(true, "Intervalo de coleta inválido é bloqueado"); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
