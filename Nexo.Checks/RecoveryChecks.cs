using Microsoft.Extensions.Time.Testing;
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
        var protection = RestorePointProtection.Prepare((_, _) => Task.FromResult("{\"success\":false,\"error\":\"disabled\"}"), CancellationToken.None).GetAwaiter().GetResult();
        check(!protection.Ready && protection.Reason.Contains("disabled"), "Driver não é instalado sem proteção de restauração confirmada");
        var prepared = RestorePointProtection.Prepare((_, _) => Task.FromResult("{\"success\":true,\"data\":{\"SequenceNumber\":42}}"), CancellationToken.None).GetAwaiter().GetResult();
        check(prepared.Ready && prepared.SequenceNumber == 42 && RestorePointProtection.Script.Contains("Checkpoint-Computer"), "Ponto de restauração exige sequência observada antes do driver");
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
        check(calls.SequenceEqual(["get_hardware_inventory", "get_driver_store"]) && driverAnswer.Contains("oem4.inf"), "Pedido somente de INF funciona sem busca online");
        var root = Path.Combine(Path.GetTempPath(), "nexo-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(root, directAccess: true);
            var tools = new WindowsTools(store, store.GetLocalSession(), _ => Task.FromResult(false), new Progress<string>());
            var refusal = tools.Execute("flush_dns", System.Text.Json.JsonSerializer.SerializeToElement(new { }), CancellationToken.None).GetAwaiter().GetResult();
            using var operations = new OperationalStore(root);
            var recorded = operations.RecentInterventions(5).Single();
            check(refusal.Contains("denied") && recorded.State == "denied", "Recusa é registrada sem executar alteração");
            check(recorded.Transitions.Select(t => t.State).SequenceEqual(["proposed", "denied"]) && recorded.Before.Contains("status"), "Intervenção persiste etapas e observação anterior estruturada");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        CheckNvidiaTimeouts(check);
        CheckPendingOperations(check);
    }

    private static void CheckNvidiaTimeouts(Action<bool, string> check)
    {
        var offer = new OfficialDriverSource.DriverOffer("282412", "621.01", new Uri("https://us.download.nvidia.com/Windows/621.01/driver.exe"), "NVIDIA GeForce RTX 5060 Ti", "32.0.16.1714");
        var script = WindowsTools.NvidiaDriverInstallationScript("C:\\nvidia.exe", offer, "PCI\\VEN_10DE", "32.0.16.1714");
        check(WindowsTools.NvidiaInstallerTimeout > WindowsTools.ElevatedTimeout
            && WindowsTools.NvidiaSupervisionTimeout > WindowsTools.NvidiaInstallerTimeout
            && script.Contains($"WaitForExit({(long)WindowsTools.NvidiaInstallerTimeout.TotalMilliseconds})"),
            "Supervisor da instalação NVIDIA espera mais que o prazo usado pelo próprio script");
        check(script.Contains("$process.WaitForExit()") && !script.Contains("throw 'O instalador NVIDIA excedeu"),
            "Ao superar 30 minutos, o script continua aguardando o instalador para registrar o resultado real");

        var time = new FakeTimeProvider();
        var installer = new TaskCompletionSource<int>();
        var supervised = OperationSupervisor.Wait(() => installer.Task, WindowsTools.NvidiaSupervisionTimeout, CancellationToken.None, time);
        time.Advance(TimeSpan.FromMinutes(16));
        installer.SetResult(0);
        check(supervised.GetAwaiter().GetResult() == OperationState.Completed,
            "Instalação NVIDIA simulada de 16 minutos termina dentro da supervisão");

        var genericLimit = OperationSupervisor.Wait(() => new TaskCompletionSource<int>().Task, WindowsTools.ElevatedTimeout, CancellationToken.None, time);
        time.Advance(TimeSpan.FromMinutes(16));
        check(genericLimit.GetAwaiter().GetResult() == OperationState.Unknown,
            "Com o prazo genérico de 15 minutos, a mesma instalação ficaria incerta");
    }

    private static void CheckPendingOperations(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var operations = new OperationalStore(root);
            var late = SavePending(operations, "late");
            var waiting = SavePending(operations, "waiting");
            var partial = SavePending(operations, "partial");
            WriteResult(root, "late", """{"success":true,"data":{"Driver":"NVIDIA 621.01","Verification":"version_changed"}}""");
            WriteResult(root, "partial", """{"success":tr""");

            var resolved = PendingOperations.Reconcile(root, operations, DateTimeOffset.UtcNow);
            var stored = operations.RecentInterventions(10).ToDictionary(item => item.Id);

            check(resolved == 1
                && stored[late.Id] is { State: "reported", PendingOperation: "" }
                && stored[late.Id].Transitions[^1].Details.Contains("reconciliar")
                && !Directory.Exists(PendingOperations.Folder(root, "late")),
                "Resultado que chega depois do prazo atualiza a intervenção incerta");
            check(stored[waiting.Id] is { State: "unknown", PendingOperation: "waiting" }
                && HealthReportPresentation.InterventionState(stored[waiting.Id]).Contains("em andamento"),
                "Operação sem resultado continua visível como em andamento ou incerta");
            check(stored[partial.Id] is { State: "unknown", PendingOperation: "partial" },
                "Resultado gravado pela metade não encerra a operação pendente");

            var old = new InterventionRecord { Action = "install_official_nvidia_driver", State = "unknown", PendingOperation = "old", Time = DateTimeOffset.UtcNow.AddDays(-1) };
            operations.SaveIntervention(old);
            for (var index = 0; index < 55; index++)
                operations.SaveIntervention(new InterventionRecord { Time = DateTimeOffset.UtcNow.AddMinutes(index) });
            WriteResult(root, "old", """{"success":true,"data":{"Driver":"NVIDIA 621.01","Verification":"version_changed"}}""");
            check(PendingOperations.Reconcile(root, operations, DateTimeOffset.UtcNow) == 1
                && operations.PendingInterventions().All(item => item.Id != old.Id),
                "Pendência antiga é reconciliada mesmo após mais de 50 intervenções novas");

            var prepared = new InterventionRecord { Action = "install_official_nvidia_driver", Authorized = true };
            PendingOperations.Register(operations, prepared, "prepared", DateTimeOffset.UtcNow);
            using var reopened = new OperationalStore(root);
            check(reopened.PendingInterventions().Any(item => item.Id == prepared.Id && item.PendingOperation == "prepared" && item.State == "executing")
                && HealthReportPresentation.InterventionState(prepared).Contains("em andamento"),
                "Vínculo da operação é persistido antes da espera e sobrevive à reabertura do banco");
            WriteResult(root, "prepared", """{"success":true,"data":{"Driver":"NVIDIA 621.01","Verification":"version_changed"}}""");
            check(PendingOperations.Reconcile(root, operations, DateTimeOffset.UtcNow) == 0,
                "Monitor não consome o resultado enquanto o executor ainda está ativo");
            PendingOperations.Release("prepared");
            check(PendingOperations.Reconcile(root, operations, DateTimeOffset.UtcNow) == 1,
                "Após encerrar o executor, o monitor pode reconciliar a pendência");

            var retained = new InterventionRecord { Time = DateTimeOffset.UtcNow.AddDays(-5), State = "unknown", PendingOperation = "retained" };
            var expired = new InterventionRecord { Time = DateTimeOffset.UtcNow.AddDays(-5), State = "reported" };
            operations.SaveIntervention(retained);
            operations.SaveIntervention(expired);
            WriteResult(root, "retained", """{"success":true,"data":{"Driver":"NVIDIA 621.01","Verification":"version_changed"}}""");
            operations.Prune(TimeSpan.FromDays(1));
            check(operations.PendingInterventions().Any(item => item.Id == retained.Id)
                && operations.RecentInterventions(100).All(item => item.Id != expired.Id)
                && PendingOperations.Reconcile(root, operations, DateTimeOffset.UtcNow) == 1,
                "Retenção preserva operação incerta até ler o resultado tardio");

            var rejectedPath = false;
            try { PendingOperations.Folder(root, "../outside"); }
            catch (ArgumentException) { rejectedPath = true; }
            check(rejectedPath, "Identificador de pendência não pode escapar da pasta de operações");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static InterventionRecord SavePending(OperationalStore operations, string operationId)
    {
        var intervention = new InterventionRecord { Action = "install_official_nvidia_driver", State = "unknown", PendingOperation = operationId };
        operations.SaveIntervention(intervention);
        return intervention;
    }

    private static void WriteResult(string root, string operationId, string json)
    {
        var folder = PendingOperations.Folder(root, operationId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "result.json"), json);
    }
}
