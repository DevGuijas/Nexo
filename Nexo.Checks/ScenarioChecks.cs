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
        var wrong = NetworkAssessment.Enrich(ContinuousChecks.Inventory("Disabled", "192.168.99.5", ["192.168.1.1"]));
        check(wrong.GetProperty("Assessment")[0].GetProperty("DhcpCandidate").GetBoolean(), "Cenário 2: IP e gateway incompatíveis geram candidato à investigação");
        var evidence = new InvestigationEvidence();
        evidence.Observe("test_connection", "ip", System.Text.Json.JsonSerializer.SerializeToElement(new { host = "8.8.8.8", port = 53 }), WindowsTools.Ok(new { host = "8.8.8.8", dnsResolved = true, tcpConnected = true }));
        evidence.Observe("test_connection", "dns", System.Text.Json.JsonSerializer.SerializeToElement(new { host = "example.com", port = 443 }), WindowsTools.Ok(new { host = "example.com", dnsResolved = false, tcpConnected = false }));
        check(evidence.Summary().Contains("conectividade parcial"), "Cenário 3: DNS falho com TCP por IP preservado é falha parcial");
        var disk = new[] { At(-30), At(0) }; foreach (var sample in disk) sample.Disks.Add(new DiskHealth("C:", 3, 100));
        check(HealthRules.Evaluate(disk, new()).Any(x => x.Id.StartsWith("disk-low")), "Cenário 7: volume quase cheio detectado");
        var memory = new[] { At(-60), At(-30), At(0) }; memory[0].MemoryUsedPercent = 50; memory[1].MemoryUsedPercent = 67; memory[2].MemoryUsedPercent = 85;
        check(HealthRules.Evaluate(memory, new()).Any(x => x.Id == "memory-growing"), "Cenário 5: crescimento de memória detectado");
        var service = new[] { At(-30), At(0) }; foreach (var sample in service) sample.Services.Add(new ServiceHealth("Audiosrv", "Stopped", "Automatic"));
        check(HealthRules.Evaluate(service, new()).Any(x => x.Id.StartsWith("service-stopped")), "Cenário 4: serviço automático parado detectado");
        check(DriverStoreInventory.MatchesObserved("PCI\\VEN_1234", "PCI\\VEN_1234", "oem4.inf") && !DriverStoreInventory.MatchesObserved("PCI\\VEN_1234", "PCI\\VEN_9999", "oem4.inf"), "Cenário 6: pacote de driver só corresponde ao dispositivo observado");
        var root = Path.Combine(Path.GetTempPath(), "nexo-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new HealthSettings { IntervalSeconds = 45, CpuHighPercent = 88, DiskLowFreePercent = 12, RetentionDays = 14 };
            settings.Save(root);
            var loaded = HealthSettings.Load(root);
            check(loaded.IntervalSeconds == 45 && loaded.CpuHighPercent == 88 && loaded.RetentionDays == 14, "Preferências de saúde persistem localmente");
            loaded.IntervalSeconds = 1;
            try { loaded.Save(root); throw new Exception("FAIL: intervalo inválido aceito"); }
            catch (ArgumentOutOfRangeException) { check(true, "Intervalo de coleta inválido é bloqueado"); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
