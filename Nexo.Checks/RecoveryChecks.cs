using Nexo.Support;

namespace Nexo.Checks;

internal static class RecoveryChecks
{
    public static void Run(Action<bool, string> check)
    {
        var unknown = OperationSupervisor.Wait(async () => { await Task.Delay(100); return 0; }, TimeSpan.FromMilliseconds(5), CancellationToken.None).GetAwaiter().GetResult();
        check(unknown == OperationState.Unknown, "Processo elevado que excede prazo fica desconhecido");
        var failed = OperationSupervisor.Wait(() => Task.FromResult(5), TimeSpan.FromSeconds(1), CancellationToken.None).GetAwaiter().GetResult();
        check(failed == OperationState.Failed, "Código de saída não zero não indica sucesso");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var interrupted = OperationSupervisor.Wait(async () => { await Task.Delay(50); return 0; }, TimeSpan.FromSeconds(1), cancelled.Token).GetAwaiter().GetResult();
        check(interrupted == OperationState.Unknown, "Cancelamento após lançamento não simula reversão");
        check(DriverStoreInventory.MatchesObserved("PCI\\VEN_1234", "PCI\\VEN_1234", "oem4.inf") && !DriverStoreInventory.MatchesObserved("PCI\\VEN_1234", "PCI\\VEN_5678", "oem4.inf"), "Inventário de driver exige dispositivo exato");
        check(!DriverStoreInventory.MatchesObserved("PCI\\VEN_1234", "PCI\\VEN_1234", ""), "INF ausente é limitação explícita");
        var calls = new List<string>();
        var driverAnswer = DriverWorkflow.Run("Apenas verifique o INF do driver da placa de vídeo", true, (name, _) =>
        {
            calls.Add(name);
            var response = name switch
            {
                "get_hardware_inventory" => WindowsTools.Ok(new { Truncated = false, Devices = new[] { new Device("GPU", "PCI\\GPU1", 0, "OK") { HardwareIds = ["PCI\\VEN_1234"], DriverVersion = "1.0" } } }),
                "get_driver_store" => WindowsTools.Ok(new { available = true, package = new { DeviceId = "PCI\\GPU1", InfName = "oem4.inf", Version = "1.0", Provider = "Vendor" } }),
                "scan_driver_updates" => WindowsTools.Ok("[]"),
                _ => throw new Exception("Ferramenta inesperada")
            };
            using var json = System.Text.Json.JsonDocument.Parse(response);
            return Task.FromResult(json.RootElement.Clone());
        }, CancellationToken.None).GetAwaiter().GetResult();
        check(calls.SequenceEqual(["get_hardware_inventory", "get_driver_store", "scan_driver_updates"]) && driverAnswer.Contains("oem4.inf"), "Pedido de INF expõe pacote instalado antes da busca online");
        var root = Path.Combine(Path.GetTempPath(), "nexo-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(root, directAccess: true);
            var tools = new WindowsTools(store, store.GetLocalSession(), _ => Task.FromResult(false), new Progress<string>());
            var refusal = tools.Execute("flush_dns", System.Text.Json.JsonSerializer.SerializeToElement(new { }), CancellationToken.None).GetAwaiter().GetResult();
            using var operations = new OperationalStore(root);
            check(refusal.Contains("denied") && operations.RecentInterventions(5).Single().State == "denied", "Recusa é registrada sem executar alteração");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
