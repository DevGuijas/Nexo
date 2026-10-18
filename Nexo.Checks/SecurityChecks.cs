using System.Text.Json;
using Nexo.Support;

public static class SecurityChecks
{
    public static async Task Run(Store store, Account admin, Action<bool, string> assert)
    {
        static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
        async Task Reject(Func<Task> action, string title)
        {
            try { await action(); }
            catch (InvalidOperationException) { assert(true, title); return; }
            throw new Exception("FAIL: " + title);
        }
        void RejectSync(Action action, string title)
        {
            try { action(); }
            catch (InvalidOperationException) { assert(true, title); return; }
            throw new Exception("FAIL: " + title);
        }

        var confirmationCount = 0;
        var tools = new WindowsTools(store, admin, _ => { confirmationCount++; return Task.FromResult(false); }, new Progress<string>());
        RejectSync(() => WindowsTools.ValidateArguments("run_command", Args(new { command = "whoami" })), "Ferramenta de comando arbitrário não existe no executor");
        RejectSync(() => WindowsTools.ValidateArguments("flush_dns", Args(new { command = "whoami" })), "Propriedade inesperada rejeitada antes da confirmação");
        RejectSync(() => WindowsTools.ValidateArguments("restart_service", Args(new { service = "Spooler;whoami" })), "Serviço fora da lista rejeitado");
        RejectSync(() => WindowsTools.ValidateArguments("test_connection", Args(new { host = "example.com", port = "443" })), "Tipo incorreto de porta rejeitado");
        RejectSync(() => WindowsTools.ValidateArguments("set_dns", Args(new { interface_index = 1, mode = "manual", servers = new[] { "1.1.1.1", "2.2.2.2", "3.3.3.3", "4.4.4.4" } })), "Lista DNS acima do limite rejeitada");
        using (var duplicate = JsonDocument.Parse("{\"service\":\"Spooler\",\"service\":\"Audiosrv\"}"))
            RejectSync(() => WindowsTools.ValidateArguments("restart_service", duplicate.RootElement), "Chave JSON repetida rejeitada");
        assert(WindowsTools.Quote("x'; Invoke-Expression 'whoami") == "'x''; Invoke-Expression ''whoami'", "Texto em script PowerShell permanece literal");

        var denied = JsonDocument.Parse(await tools.Execute("flush_dns", Args(new { }), CancellationToken.None));
        using (denied) assert(denied.RootElement.GetProperty("denied").GetBoolean() && !denied.RootElement.GetProperty("success").GetBoolean() && confirmationCount == 1, "Recusa impede limpeza de DNS");
        var serviceDenied = JsonDocument.Parse(await tools.Execute("restart_service", Args(new { service = "WlanSvc" }), CancellationToken.None));
        using (serviceDenied) assert(serviceDenied.RootElement.GetProperty("denied").GetBoolean() && confirmationCount == 2, "Recusa impede reinício de serviço");
        assert(store.Data.Audit.Last().Action.Contains("Recusado: Reiniciar serviço") && !store.Data.Audit.Last().Action.Contains("WlanSvc"), "Auditoria registra decisão sem detalhe sensível");
        await Reject(() => tools.Execute("clear_print_queue", Args(new { printer_name = "Impressora inventada" }), CancellationToken.None), "Fila não consultada não pode ser limpa");
        await Reject(() => tools.Execute("install_driver_update", Args(new { update_id = Guid.NewGuid().ToString() }), CancellationToken.None), "Driver sem busca prévia não pode ser instalado");
        assert(confirmationCount == 2, "Alvos não observados são barrados antes da confirmação");

        var noDiagnosis = new Account { Login = "security-no-diagnosis", Diagnose = false, Repair = true };
        var noRepair = new Account { Login = "security-no-repair", Diagnose = true, Repair = false };
        var firstAccess = new Account { Login = "security-first-access", Diagnose = true, Repair = true, MustChangePassword = true };
        store.Data.Accounts.AddRange([noDiagnosis, noRepair, firstAccess]);
        try
        {
            var blockedConfirmations = 0;
            Func<ChangeRequest, Task<bool>> blockedConfirm = _ => { blockedConfirmations++; return Task.FromResult(true); };
            await Reject(() => new WindowsTools(store, noDiagnosis, blockedConfirm, new Progress<string>()).Execute("flush_dns", Args(new { }), CancellationToken.None), "Conta sem Diagnose não chega à ferramenta");
            await Reject(() => new WindowsTools(store, noRepair, blockedConfirm, new Progress<string>()).Execute("allow_camera", Args(new { }), CancellationToken.None), "Conta sem Repair não chega à autorização");
            await Reject(() => new WindowsTools(store, firstAccess, blockedConfirm, new Progress<string>()).Execute("flush_dns", Args(new { }), CancellationToken.None), "Primeiro acesso sem troca de senha bloqueia ferramenta");
            assert(blockedConfirmations == 0, "Permissões insuficientes não abrem confirmação");
        }
        finally
        {
            store.Data.Accounts.Remove(noDiagnosis);
            store.Data.Accounts.Remove(noRepair);
            store.Data.Accounts.Remove(firstAccess);
        }

        using var cancelledBefore = new CancellationTokenSource(); cancelledBefore.Cancel();
        var beforeCancelConfirmations = 0;
        var beforeCancelTools = new WindowsTools(store, admin, _ => { beforeCancelConfirmations++; return Task.FromResult(true); }, new Progress<string>());
        try { await beforeCancelTools.Execute("flush_dns", Args(new { }), cancelledBefore.Token); throw new Exception("FAIL: Cancelamento prévio ignorado"); }
        catch (OperationCanceledException) { assert(beforeCancelConfirmations == 0, "Cancelamento prévio impede confirmação e execução"); }

        using var cancelledDuring = new CancellationTokenSource();
        var afterCancelConfirmations = 0;
        var duringTools = new WindowsTools(store, admin, _ => { afterCancelConfirmations++; cancelledDuring.Cancel(); return Task.FromResult(true); }, new Progress<string>());
        try { await duringTools.Execute("flush_dns", Args(new { }), cancelledDuring.Token); throw new Exception("FAIL: Cancelamento durante confirmação ignorado"); }
        catch (OperationCanceledException) { assert(afterCancelConfirmations == 1, "Cancelamento durante confirmação impede execução"); }
        assert(store.Data.Audit.Last().Action == "Cancelado: Limpar cache DNS", "Cancelamento é auditado sem parâmetros");

        try { await SupportService.Powershell("Start-Sleep -Seconds 5", CancellationToken.None, 1); throw new Exception("FAIL: Consulta PowerShell ignorou o prazo"); }
        catch (TimeoutException) { assert(true, "Consulta PowerShell demorada expira sem ficar presa"); }
        using (var readCancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            try { await SupportService.Powershell("Start-Sleep -Seconds 5", readCancelled.Token, 15); throw new Exception("FAIL: Consulta PowerShell ignorou cancelamento"); }
            catch (OperationCanceledException) { assert(true, "Cancelamento interrompe consulta PowerShell em andamento"); }
        }

        RejectSync(() => WindowsTools.ValidateDeviceTarget("enable_camera", "audio", new Device("Alto-falante", "AUDIO\\1", 22, "Error")), "Câmera não aceita dispositivo de áudio");
        RejectSync(() => WindowsTools.ValidateDeviceTarget("enable_device", "camera", new Device("Câmera", "USB\\1", 0, "OK")), "Habilitação exige código PnP 22");
        WindowsTools.ValidateDeviceTarget("enable_camera", "camera", new Device("Câmera", "USB\\1", 22, "Error"));
        assert(true, "Câmera desabilitada diagnosticada é alvo válido");
        RejectSync(() => WindowsTools.MatchDriverTarget("", []), "Driver sem HardwareID não recebe instalação automática");
        RejectSync(() => WindowsTools.MatchDriverTarget("HID\\X", [new Device("A", "USB\\1", 0, "OK")]), "Driver sem dispositivo correspondente é bloqueado");
        var matched = WindowsTools.MatchDriverTarget("hid\\x", [new Device("A", "USB\\1", 0, "OK") { HardwareIds = ["HID\\X"] }]);
        assert(matched.Name == "A", "HardwareID exato encontra dispositivo diagnosticado");
        RejectSync(() => WindowsTools.MatchDriverTarget("HID\\X", [new Device("A", "USB\\1", 0, "OK") { HardwareIds = ["HID\\X"] }, new Device("B", "USB\\2", 0, "OK") { CompatibleIds = ["HID\\X"] }]), "Driver com múltiplos alvos exige avaliação manual");

        var cameraEvidence = new InvestigationEvidence();
        var cameraArgs = Args(new { topic = "camera" });
        cameraEvidence.Observe("diagnose_device", "camera", cameraArgs, WindowsTools.Ok(new { devices = new[] {
            new Device("Câmera interna", "PCI\\CAMERA_TEST", 22, "Error"),
            new Device("Webcam USB", "USB\\WEBCAM_TEST", 22, "Error") } }));
        assert(!cameraEvidence.DeviceTargetUnambiguous("PCI\\CAMERA_TEST", "Tenho câmera interna e webcam USB; não sei qual falhou.", true)
            && cameraEvidence.AmbiguityQuestion("camera", "Tenho câmera interna e webcam USB; não sei qual falhou.") is not null,
            "Citar duas câmeras com alvo incerto não autoriza escolha arbitrária");
        assert(cameraEvidence.DeviceTargetUnambiguous("USB\\WEBCAM_TEST", "A Webcam USB não aparece no Teams.", true),
            "Uma câmera nomeada explicitamente distingue dois alvos observados");

        var printerEvidence = new InvestigationEvidence();
        printerEvidence.Observe("get_printers", "printers", Args(new { }), WindowsTools.Ok(JsonSerializer.Serialize(new[] {
            new { Name = "Impressora USB", Status = "Error", Jobs = 1 },
            new { Name = "Impressora de rede", Status = "Error", Jobs = 1 } })));
        assert(!printerEvidence.PrinterTargetUnambiguous("Impressora USB", "Tenho Impressora USB e Impressora de rede; não sei qual recebeu o documento.")
            && printerEvidence.AmbiguityQuestion("printer", "Tenho Impressora USB e Impressora de rede; não sei qual recebeu o documento.") is not null,
            "Citar duas filas com documento incerto bloqueia limpeza");
    }
}
