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
        CheckNvidiaHdmiAudio(check);
        CheckPendingOperations(check);
    }

    // Ensaio de 07/11/2026 (RTX 3060 Ti): com só Display.Driver, o setup removeu o nvhda.inf
    // e o HDAUDIO\FUNC_01&VEN_10DE&DEV_009E ficou com Code 28; o endpoint de áudio do monitor sumiu.
    private static void CheckNvidiaHdmiAudio(Action<bool, string> check)
    {
        var withAudio = WindowsTools.NvidiaInstallerArguments(hdmiAudio: true);
        var withoutAudio = WindowsTools.NvidiaInstallerArguments(hdmiAudio: false);
        check(withAudio.SequenceEqual(["-s", "-n", "Display.Driver", "HDAudio.Driver"]),
            "Com áudio NVIDIA presente, o instalador recebe Display.Driver e HDAudio.Driver");
        check(withoutAudio.SequenceEqual(["-s", "-n", "Display.Driver"]),
            "Sem áudio NVIDIA presente, o instalador recebe só Display.Driver");
        check(withAudio.Concat(withoutAudio).All(argument => !argument.Contains(' ')),
            "Cada componente do instalador NVIDIA vai como argumento separado");

        // Sem .gitattributes, o checkout no Windows grava o .cs com CRLF e a string raw do script herda essa quebra.
        var script = NvidiaAudioScriptChecks.Script().ReplaceLineEndings("\n");
        int At(string text) => script.IndexOf(text, StringComparison.Ordinal);
        var enumeration = At("$audioBefore=@(Get-CimInstance Win32_PnPEntity -ErrorAction Stop | Where-Object {[string]$_.PNPDeviceID -like 'HDAUDIO\\FUNC_01&VEN_10DE&*' -and $_.Present -eq $true})");
        var failure = At("}catch{throw ('Não foi possível consultar o áudio HDMI da placa antes da instalação. O instalador NVIDIA não foi iniciado e nenhum driver foi alterado. Detalhe: '+$_.Exception.Message)}");
        var launch = At("Start-Process");
        check(At("try{") is >= 0 and var opening && opening < enumeration && enumeration < failure && failure < launch,
            "Script lê o áudio NVIDIA presente com -ErrorAction Stop antes do instalador e, se falhar, diz que nada começou");
        check(script.Contains("$audioBeforeCodes[[string]$device.PNPDeviceID]=$(if($null -eq $device.ConfigManagerErrorCode){$null}else{[int]$device.ConfigManagerErrorCode})"),
            "Script guarda o código de antes por PNPDeviceID, sem transformar código nulo em 0");
        check(script.Contains("$arguments=if($audioIds.Count -gt 0){ @('-s','-n','Display.Driver','HDAudio.Driver') }else{ @('-s','-n','Display.Driver') }")
            && script.Contains("-ArgumentList $arguments ") && !script.Contains("-ArgumentList @("),
            "Script só inclui HDAudio.Driver quando o áudio NVIDIA existe, com IDs separados");
        check(script.Contains("$audioAfterCodes[$id]=if($devicesNow.Count -eq 0 -or ($match.Count -eq 1 -and $null -eq $match[0].ConfigManagerErrorCode)){$null}elseif($match.Count -ne 1){-1}else{[int]$match[0].ConfigManagerErrorCode}"),
            "Depois da instalação, dispositivo sumido vira -1 e código ausente fica nulo, nunca 0");
        check(script.Contains("$audioWorking=@($audioIds | Where-Object {$audioBeforeCodes[$_] -eq 0})")
            && script.Contains("$audioVanished=@($audioWorking | Where-Object {$audioAfterCodes[$_] -eq -1})")
            && script.Contains("$audioUnknown=@($audioWorking | Where-Object {$null -eq $audioAfterCodes[$_]})")
            && script.Contains("$audioFailed=@($audioWorking | Where-Object {$null -ne $audioAfterCodes[$_] -and $audioAfterCodes[$_] -notin @(0,-1)})"),
            "Só conta como piora o áudio que estava em 0 e agora tem erro, sumiu ou ficou sem código");
        check(WindowsTools.NvidiaAudioReadings == 5 && WindowsTools.NvidiaAudioReadingIntervalSeconds == 2
            && script.Contains("for($attempt=1; $attempt -le 5; $attempt++){")
            && script.Contains("-or $attempt -ge 5){break}\n        Start-Sleep -Seconds 2\n    }"),
            "Áudio é lido até 5 vezes, com 2 s entre leituras e sem espera depois da última");
        check(script.Contains("$audioStatus=if($audioIds.Count -eq 0){'absent'}elseif($audioVanished.Count){'vanished'}elseif($audioFailed.Count){'failed'}elseif($audioUnknown.Count){'unknown'}elseif($audioPreexisting.Count){'preexisting'}else{'preserved'}")
            && script.Contains("$audioPreexisting=@($audioIds | Where-Object {$audioBeforeCodes[$_] -ne 0})")
            && script.Contains("$audioPreserved=if($audioIds.Count -eq 0){$null}else{$audioStatus -eq 'preserved'}"),
            "Estado do áudio separa piora, código desconhecido e áudio que já estava desativado antes");
        check(script.Contains("$audioDriver=@(Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue | Where-Object {[string]$_.DeviceID -eq $audioKey})")
            && script.Contains("HdmiAudioBefore=($audioIds.Count -gt 0);HdmiAudioStatus=$audioStatus;HdmiAudioBeforeCode=$audioBeforeCode;HdmiAudioAfterCode=$audioAfterCode;HdmiAudioPreserved=$audioPreserved;HdmiAudioDriverVersion=$audioDriverVersion}"),
            "Resultado da instalação NVIDIA informa estado, códigos e versão do driver de áudio");

        var lost = NvidiaAudioScriptChecks.Result("version_changed", "failed", 0, 28, false);
        using (var json = System.Text.Json.JsonDocument.Parse(lost))
        {
            var root = json.RootElement;
            var data = root.GetProperty("data");
            var warning = root.TryGetProperty("warning", out var found) ? found.GetString() ?? "" : "";
            check(root.GetProperty("success").GetBoolean() && warning.Contains("código 28") && warning.Contains("O som pela placa parou")
                && !warning.Contains("pode ter parado") && !warning.Contains("reinicialização")
                && data.GetProperty("HdmiAudioBefore").GetBoolean() && data.GetProperty("HdmiAudioAfterCode").GetInt32() == 28
                && !data.GetProperty("HdmiAudioPreserved").GetBoolean(),
                "Áudio que funcionava e ficou com Code 28 gera aviso de que o som parou, sem falar em reinício sem pedido do Windows");
        }
        var lostRestart = NvidiaAudioScriptChecks.Result("pending_restart", "failed", 0, 28, false, reboot: true);
        var vanished = NvidiaAudioScriptChecks.Result("version_changed", "vanished", 0, -1, false);
        check(Field(lostRestart, "warning").Contains("O Windows pediu reinicialização: reinicie") && Field(vanished, "warning").Contains("não o encontra mais"),
            "Aviso fala em reiniciar só quando o Windows pediu, e trata o dispositivo sumido");
        var disabled = NvidiaAudioScriptChecks.Result("version_changed", "preexisting", 22, 22, false);
        var enabledNow = NvidiaAudioScriptChecks.Result("version_changed", "preexisting", 22, 0, false, version: "1.4.6.6");
        check(Field(disabled, "warning").Length == 0 && Field(disabled, "audioInfo").Contains("já estava desativado ou com erro antes da instalação (código 22) e continua assim (código 22)")
            && Field(disabled, "audioInfo").Contains("não foi causado por esta instalação"),
            "Áudio já desativado antes (22) não acusa a instalação e informa de forma neutra");
        check(Field(enabledNow, "warning").Length == 0 && Field(enabledNow, "audioTest").Contains("agora está sem erro (driver 1.4.6.6)"),
            "Áudio que estava com erro antes e voltou a 0 pede teste de som");
        var kept = NvidiaAudioScriptChecks.Result("version_changed", "preserved", 0, 0, true, version: "1.4.6.6");
        var keptTest = Field(kept, "audioTest");
        check(Field(kept, "warning").Length == 0 && keptTest.Contains("está sem erro no Windows (driver 1.4.6.6)") && keptTest.Contains("O Windows só informa o estado do dispositivo; quem confirma se o som sai é você")
            && !keptTest.Contains("reinstalado") && !keptTest.Contains("confirmado"),
            "Áudio em 0 informa a versão do driver e não diz que foi reinstalado nem confirmado");
        var unknown = NvidiaAudioScriptChecks.Result("version_changed", "unknown", 0, null, false);
        var preservedWithoutCode = NvidiaAudioScriptChecks.Result("version_changed", "preserved", 0, null, true);
        check(Field(unknown, "audioTest").Contains("não informou o estado") && !Field(unknown, "audioTest").Contains("está sem erro")
            && Field(preservedWithoutCode, "audioTest").Contains("não informou o estado"),
            "Código de áudio nulo é tratado como desconhecido, nunca como preservado");
        var absent = NvidiaAudioScriptChecks.Result("version_changed", "absent", null, null, null);
        var legacy = WindowsTools.VerifyDriverInstallationResult(WindowsTools.Ok(new { Verification = "version_changed", AfterVersion = "2.0" }));
        check(new[] { absent, legacy }.All(result => !result.Contains("\"warning\"") && !result.Contains("\"audioTest\"") && !result.Contains("\"audioInfo\"")),
            "PC sem áudio NVIDIA ou resultado sem os campos de áudio não recebem mensagem de áudio");
        using (var json = System.Text.Json.JsonDocument.Parse(NvidiaAudioScriptChecks.Result("unconfirmed", "vanished", 0, -1, false)))
            check(!json.RootElement.GetProperty("success").GetBoolean()
                && json.RootElement.GetProperty("error").GetString()!.Contains("não o encontra mais"),
                "Vídeo não confirmado também avisa que o áudio HDMI sumiu");

        const string label = "Instalando e verificando o driver NVIDIA…";
        var shownLost = ToolResultPresentation.Describe(label, lost);
        var shownKept = ToolResultPresentation.Describe(label, kept);
        var shownDisabled = ToolResultPresentation.Describe(label, disabled);
        check(shownLost.Warning && shownLost.State == "Driver instalado; som pela placa parou" && shownLost.Detail.Contains("nova versão 32.0.16.2114")
            && shownLost.Detail.Contains("código 28"),
            "Apresentação avisa que o som pela placa parou, mesmo com o vídeo instalado");
        int Step(string text) => shownKept.Detail.IndexOf(text, StringComparison.Ordinal);
        check(!shownKept.Warning && shownKept.State == "Driver instalado; teste o vídeo e, se usar, o som pelo monitor ou TV"
            && Step("Se você usa o som pelo monitor ou TV (HDMI/DisplayPort), toque um áudio e confira se ele sai") >= 0
            && Step("1) confira se a saída escolhida no Windows é a do monitor ou TV") > Step("Se você usa o som pelo monitor ou TV (HDMI/DisplayPort), toque um áudio e confira se ele sai")
            && Step("2) feche e abra de novo o aplicativo que toca o som") > Step("1) confira se a saída")
            && Step("3) desligue e ligue o monitor ou TV, ou reconecte o cabo HDMI/DisplayPort") > Step("2) feche")
            && Step("4) reinicie o computador") > Step("3) desligue")
            && Step("Se ainda assim não sair, encaminhe à TI.") > Step("4) reinicie") && !shownKept.Detail.Contains("confirmado"),
            "Com áudio em 0, o teste de som vale para quem usa monitor ou TV, começa pela saída escolhida e não declara o áudio confirmado");
        check(!shownDisabled.Warning && shownDisabled.State == "Driver instalado; teste funcional pendente" && shownDisabled.Detail.Contains("já estava desativado"),
            "Apresentação mostra o áudio já desativado antes sem alarme");
        var shownRestartLost = ToolResultPresentation.Describe(label, lostRestart);
        var shownRestartKept = ToolResultPresentation.Describe(label, NvidiaAudioScriptChecks.Result("pending_restart", "preserved", 0, 0, true, reboot: true, version: "1.4.6.6"));
        check(shownRestartLost.Warning && shownRestartLost.State == "Reinicialização necessária; som pela placa parou"
            && shownRestartKept.State == "Reinicialização necessária; se usar, teste o som pelo monitor ou TV",
            "Com reinício pendente, o título mantém a reinicialização e acrescenta o estado do áudio");

        string Flow(string installed) => DriverWorkflow.Run("Atualize o driver da placa de vídeo", false, (name, _) =>
        {
            var response = name switch
            {
                "get_hardware_inventory" => WindowsTools.Ok(new { Total = 1, Truncated = false, Devices = new[] {
                    new Device("NVIDIA GeForce RTX 3060 Ti", "PCI\\VEN_10DE&DEV_2486", 0, "OK") { HardwareIds = ["PCI\\VEN_10DE&DEV_2486"], DriverVersion = "32.0.15.9186", DriverProvider = "NVIDIA" }
                } }),
                "scan_driver_updates" => WindowsTools.Ok("[]"),
                "find_official_nvidia_driver" => WindowsTools.Ok(new { Available = true, DriverId = "282412", Version = "621.01", Target = "NVIDIA GeForce RTX 3060 Ti" }),
                "install_official_nvidia_driver" => installed,
                _ => throw new Exception("Ferramenta inesperada")
            };
            using var json = System.Text.Json.JsonDocument.Parse(response);
            return Task.FromResult(json.RootElement.Clone());
        }, CancellationToken.None).GetAwaiter().GetResult();
        var answerLost = Flow(lost);
        var answerKept = Flow(kept);
        var answerAbsent = Flow(absent);
        var answerDisabled = Flow(disabled);
        var answerUnconfirmed = Flow(NvidiaAudioScriptChecks.Result("unconfirmed", "preserved", 0, 0, true, version: "1.4.6.6"));
        check(answerLost.Contains("Instalei o driver NVIDIA 621.01") && answerLost.Contains("áudio HDMI/DisplayPort") && answerLost.Contains("código 28"),
            "Resposta do fluxo de driver avisa a perda do áudio HDMI junto com o vídeo instalado");
        check(answerKept.Contains("Instalei o driver NVIDIA 621.01") && answerKept.Contains("Teste a placa de vídeo")
            && answerKept.Contains("Se você usa o som pelo monitor ou TV (HDMI/DisplayPort), toque um áudio e confira se ele sai") && !answerKept.Contains("confirmado") && !answerKept.Contains("reinstalado"),
            "Com áudio em 0, a resposta pede teste de vídeo e, a quem usa, de som pelo monitor ou TV, sem dizer confirmado");
        check(answerAbsent.Contains("Instalei o driver NVIDIA 621.01") && !answerAbsent.Contains("áudio HDMI"),
            "Resposta do fluxo de driver não cita áudio quando o PC não tinha áudio NVIDIA");
        check(answerDisabled.Contains("já estava desativado") && !answerDisabled.Contains("Atenção"),
            "Resposta do fluxo de driver não acusa a instalação pelo áudio já desativado");
        var answerUnconfirmedNoAudio = Flow(NvidiaAudioScriptChecks.Result("unconfirmed", "absent", null, null, null));
        check(answerUnconfirmedNoAudio.Contains("A instalação do driver NVIDIA 621.01 não foi confirmada")
            && answerUnconfirmedNoAudio.Split("Gerenciador de Dispositivos").Length == 2 && answerUnconfirmedNoAudio.Split("encaminhe à TI").Length == 2
            && !answerUnconfirmedNoAudio.Contains("áudio"),
            "Instalação NVIDIA não confirmada sem áudio cita a versão oferecida e encaminha à TI, sem repetir o Gerenciador de Dispositivos");
        check(answerUnconfirmed.Split("Gerenciador de Dispositivos").Length == 2 && answerUnconfirmed.Split("encaminhe à TI").Length == 2
            && answerUnconfirmed.Contains("621.01") && answerUnconfirmed.Contains("toque um áudio"),
            "Instalação não confirmada orienta o Gerenciador de Dispositivos uma vez só e mantém o teste de som");

        var url = new Uri("https://us.download.nvidia.com/Windows/621.01/driver.exe");
        var original = "Placa: NVIDIA GeForce RTX 3060 Ti\nVersão atual: 32.0.15.9186\nNova versão: 621.01\nFonte: " + url
            + "\nO download tem cerca de 1 GB. A tela pode piscar; talvez seja necessário reiniciar."
            + "\nO Nexo não reiniciará o computador. Leia e aceite a licença da NVIDIA para autorizar o download e a instalação. Antes de instalar, o Nexo tentará criar e confirmar um ponto de restauração; se indisponível, a instalação será interrompida.";
        string Approval(bool? audio) => WindowsTools.NvidiaApprovalDetails("NVIDIA GeForce RTX 3060 Ti", "32.0.15.9186", "621.01", url, audio);
        check(Approval(true).Contains("\nO áudio HDMI/DisplayPort da placa (som pelo monitor ou TV) também será reinstalado. Se você usa o som pelo monitor ou TV, talvez seja preciso testá-lo depois.")
            && Approval(true).Replace("\nO áudio HDMI/DisplayPort da placa (som pelo monitor ou TV) também será reinstalado. Se você usa o som pelo monitor ou TV, talvez seja preciso testá-lo depois.", "") == original,
            "Pedido de autorização avisa que o áudio HDMI da placa também será reinstalado quando ele existe");
        check(Approval(false) == original && Approval(null).Contains("Se o computador tiver áudio HDMI/DisplayPort da placa"),
            "Sem áudio NVIDIA, o pedido de autorização fica como o original; sem leitura, o aviso é condicional");
        check(WindowsTools.ParseNvidiaHdmiAudioQuery("[]") == false && WindowsTools.ParseNvidiaHdmiAudioQuery("[\"HDAUDIO\\\\FUNC_01&VEN_10DE&DEV_009E\"]") == true
            && WindowsTools.ParseNvidiaHdmiAudioQuery("") == null && WindowsTools.ParseNvidiaHdmiAudioQuery("{}") == null
            && WindowsTools.NvidiaHdmiAudioQuery.Contains("-like 'HDAUDIO\\FUNC_01&VEN_10DE&*' -and $_.Present -eq $true")
            && !WindowsTools.NvidiaHdmiAudioQuery.Contains("Start-Process") && !WindowsTools.NvidiaHdmiAudioQuery.Contains("RunAs"),
            "Consulta de áudio antes da autorização só lê dispositivos presentes e trata saída inválida como desconhecida");

        string? asked = null;
        bool? Detect(Func<string> output) => WindowsTools.DetectNvidiaHdmiAudio((script, _) => { asked = script; return Task.FromResult(output()); }, CancellationToken.None).GetAwaiter().GetResult();
        check(Detect(() => throw new InvalidOperationException("O Windows não concluiu a operação.")) == null,
            "Falha da consulta de áudio antes da autorização fica desconhecida, sem virar ausência de áudio");
        check(Detect(() => "[]") == false && Detect(() => "[\"HDAUDIO\\\\FUNC_01&VEN_10DE&DEV_009E\"]") == true && asked == WindowsTools.NvidiaHdmiAudioQuery,
            "Consulta de áudio antes da autorização: lista vazia é sem áudio e um ID é áudio presente");
        var cancelledLookup = false;
        try { Detect(() => throw new OperationCanceledException()); } catch (OperationCanceledException) { cancelledLookup = true; }
        check(cancelledLookup, "Cancelamento durante a consulta de áudio não é engolido");

        using (var tools = System.Text.Json.JsonSerializer.SerializeToDocument(ToolCatalog.Definitions))
        {
            var description = tools.RootElement.EnumerateArray().Select(tool => tool.GetProperty("function"))
                .Single(function => function.GetProperty("name").GetString() == "install_official_nvidia_driver").GetProperty("description").GetString()!;
            check(description.Contains("áudio HDMI/DisplayPort NVIDIA") && description.Contains("ficou com erro ou sumiu")
                && description.Contains("teste de som a quem usa o som pelo monitor ou TV") && !description.Contains("apenas o driver de vídeo"),
                "Descrição da ferramenta NVIDIA diz o que é verificado no áudio: erro ou ausência e pedido de teste de som");
        }

        NvidiaAudioScriptChecks.Run(check);
    }

    private static string Field(string result, string key)
    {
        using var json = System.Text.Json.JsonDocument.Parse(result);
        return json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() ?? "" : "";
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
