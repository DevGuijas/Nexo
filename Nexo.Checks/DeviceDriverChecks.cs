using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nexo.Support;

namespace Nexo.Checks;

// Issue #62: diagnose_device associa INF, versão e fornecedor ao ID exato de cada dispositivo com erro.
// A parte com PowerShell roda o script real com Get-CimInstance falso: no Windows usa powershell.exe; fora dele, só com NEXO_CHECKS_PWSH.
internal static class DeviceDriverChecks
{
    // IDs no formato do cenário 6 da #8: o Fifine expõe MI_00 (áudio) e MI_02 no mesmo USB.
    private const string Mic = "USB\\VID_3142&PID_5060&MI_00\\7&2B6E3F1&0&0000";
    private const string Sibling = "USB\\VID_3142&PID_5060&MI_02\\7&2B6E3F1&0&0002";
    private const string Unknown = "USB\\VID_1234&PID_0001\\5&ABCDEF&0&3";
    private const string AudioClasses = "'AudioEndpoint','MEDIA'";
    private const string Ghost = "BTHENUM\\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001005D_PID&B013\\8&2A7C1F&0&0C9B3A";
    private static readonly string[] Scenarios = ["fifine", "healthy", "single", "twins", "driverfail", "ghost", "drivertimeout", "entityfail"];

    public static void Run(Action<bool, string> check)
    {
        var lines = DriverStoreInventory.DeviceDiagnosisScript(AudioClasses).ReplaceLineEndings("\n").Split('\n');
        var signed = lines.Where(line => line.Contains("Win32_PnPSignedDriver")).ToArray();
        check(signed.Length == 1 && signed[0].StartsWith("if($faulty.Count -gt 0){ try { $drivers=@(Get-CimInstance Win32_PnPSignedDriver -OperationTimeoutSec 30 -ErrorAction Stop | Where-Object { $_.DeviceID -and $faulty -contains [string]$_.DeviceID }")
            && signed[0].EndsWith("$query='ok' } catch { $query='failed' } }")
            && lines.Contains("$faulty=@($devices | Where-Object { $_.Present -eq $true -and $_.Code -ne 0 -and $_.Code -ne 45 -and $_.Id } | ForEach-Object { [string]$_.Id })")
            && lines[0].Contains("Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPClass -in 'AudioEndpoint','MEDIA' }") && lines[0].Contains("Present=($_.Present -eq $true);")
            && !lines[0].Contains("Win32_PnPSignedDriver"),
            "diagnose_device consulta Win32_PnPSignedDriver uma vez, com limite próprio, só para os IDs presentes com erro");

        // MI_02 vem antes na lista de drivers e tem outro INF: cada interface fica com o seu.
        var twins = DriverStoreInventory.ParseDeviceDiagnosis(Json(
            [Entry("Fifine Microphone", Mic, 22), Entry("Fifine Microphone (controle)", Sibling, 22), Entry("Dispositivo de áudio USB", Unknown, 28), Entry("Alto-falantes", "HDAUDIO\\FUNC_01&VEN_10EC\\1", 0)],
            [Row(Sibling, "oem42.inf", "2.0.0.1", "Fifine"), Row(Mic, "wdma_usb.inf", "10.0.19041.3636", "Microsoft"), Row("HDAUDIO\\FUNC_01&VEN_10EC\\1", "hdaudio.inf", "6.0", "Realtek")]));
        var mic = twins.Devices.Single(device => device.Id == Mic);
        var sibling = twins.Devices.Single(device => device.Id == Sibling);
        check(mic is { InfName: "wdma_usb.inf", DriverVersion: "10.0.19041.3636", DriverProvider: "Microsoft" }
            && sibling is { InfName: "oem42.inf", DriverVersion: "2.0.0.1", DriverProvider: "Fifine" },
            "Driver é ligado ao ID exato: MI_00 e MI_02 do mesmo USB não trocam INF nem versão");
        var healthy = twins.Devices.Single(device => device.Code == 0);
        check(healthy is { InfName: "", DriverVersion: "", DriverProvider: "" } && twins.Devices.Count == 4,
            "Dispositivo sem erro não recebe driver no diagnóstico");
        var unknown = twins.Devices.Single(device => device.Id == Unknown);
        check(unknown is { InfName: "", DriverVersion: "", DriverProvider: "" } && DriverStoreInventory.DriverSummary(unknown, twins.DriverQuery) == "sem driver instalado",
            "Code 28 sem linha em Win32_PnPSignedDriver fica sem driver instalado");
        var emptyInf = DriverStoreInventory.ParseDeviceDiagnosis(Json([Entry("Dispositivo de áudio USB", Unknown, 28)], [Row(Unknown, "", "10.0.19041.1", "Microsoft")])).Devices.Single();
        check(emptyInf is { InfName: "", DriverVersion: "", DriverProvider: "" } && DriverStoreInventory.DriverSummary(emptyInf, "ok") == "sem driver instalado",
            "Code 28 com linha sem INF continua sem driver instalado, sem inventar fornecedor ou versão");
        var duplicated = DriverStoreInventory.ParseDeviceDiagnosis(Json([Entry("Fifine Microphone", Mic, 22)], [Row(Mic, "wdma_usb.inf", "10.0.19041.3636", "Microsoft"), Row(Mic, "oem42.inf", "2.0.0.1", "Fifine")])).Devices.Single();
        check(duplicated.InfName == "" && DriverStoreInventory.DriverSummary(duplicated, "ok") == "o Windows não informou driver para este ID",
            "Dois drivers para o mesmo ID não escolhem um deles");
        var casing = DriverStoreInventory.ParseDeviceDiagnosis(Json([Entry("Fifine Microphone", Mic, 22)], [Row(Mic.ToLowerInvariant(), "wdma_usb.inf", "10.0.19041.3636", "Microsoft")])).Devices.Single();
        check(casing.InfName == "wdma_usb.inf", "ID do driver com outra caixa continua sendo o mesmo dispositivo");
        var failed = DriverStoreInventory.ParseDeviceDiagnosis(Json([Entry("Fifine Microphone", Mic, 22)], [], "failed"));
        var invalid = DriverStoreInventory.ParseDeviceDiagnosis(Json([Entry("Fifine Microphone", Mic, 22)], [], "talvez"));
        check(DriverStoreInventory.DriverSummary(failed.Devices.Single(), failed.DriverQuery) == "não foi possível conferir o driver no Windows" && invalid.DriverQuery == "failed",
            "Falha na consulta de drivers é dita como falha, não como dispositivo sem driver");
        check(DriverStoreInventory.DriverSummary(failed.Devices.Single() with { Code = 28 }, "failed") == "sem driver instalado"
            && DriverStoreInventory.ParseDeviceDiagnosis(Json([Entry("Alto-falantes", "HDAUDIO\\1", 0)], [], "skipped")).DriverQuery == "skipped",
            "Code 28 continua sem driver instalado mesmo com a consulta falha, e consulta não feita não vira falha");
        var driverObject = DriverStoreInventory.ParseDeviceDiagnosis("""{"Devices":{"Name":"Fifine Microphone","Id":"USB\\X","Code":22,"Status":"Error"},"Drivers":{"DeviceId":"USB\\X","InfName":"wdma_usb.inf","Version":"10.0.19041.3636","Provider":"Microsoft"},"DriverQuery":"ok"}""");
        check(driverObject.DriverQuery == "ok" && driverObject.Devices.Single() is { InfName: "wdma_usb.inf", DriverVersion: "10.0.19041.3636" },
            "Devices e Drivers como objeto único são aceitos como lista de um item");
        var driversText = DriverStoreInventory.ParseDeviceDiagnosis("""{"Devices":[{"Name":"Fifine Microphone","Id":"USB\\X","Code":22,"Status":"Error"}],"Drivers":"?","DriverQuery":"ok"}""");
        var driversMissing = DriverStoreInventory.ParseDeviceDiagnosis("""{"Devices":[{"Name":"Fifine Microphone","Id":"USB\\X","Code":22,"Status":"Error"}],"DriverQuery":"ok"}""");
        check(driversText.DriverQuery == "failed" && driversMissing.DriverQuery == "failed"
            && DriverStoreInventory.DriverSummary(driversText.Devices.Single(), driversText.DriverQuery) == "não foi possível conferir o driver no Windows",
            "Drivers fora do formato esperado viram driver não conferido, não \"o Windows não informou driver\"");
        check(DriverStoreInventory.DriverSummary(mic, twins.DriverQuery) == "wdma_usb.inf · versão 10.0.19041.3636 · Microsoft"
            && DriverStoreInventory.DriverSummary(mic with { DriverVersion = "", DriverProvider = "" }, "ok") == "wdma_usb.inf · versão não informada · fornecedor não informado",
            "Resumo do driver traz INF, versão e fornecedor e não preenche o que o Windows não informou");
        var faulty = DriverStoreInventory.FaultyDrivers(twins);
        using (var listed = JsonDocument.Parse(JsonSerializer.Serialize(faulty)))
            check(listed.RootElement.GetArrayLength() == 3 && listed.RootElement.EnumerateArray().All(entry => entry.GetProperty("Code").GetInt32() != 0)
                && listed.RootElement[0].GetProperty("Id").GetString() == Mic && listed.RootElement[0].GetProperty("Driver").GetString() == "wdma_usb.inf · versão 10.0.19041.3636 · Microsoft",
                "drivers do diagnose_device lista só os dispositivos com erro, cada um com o seu ID");
        var legacy = JsonSerializer.Deserialize<Device>("""{"Name":"GPU","Id":"PCI\\GPU1","Code":0,"Status":"OK","DriverVersion":"1.0"}""")!;
        check(legacy.InfName == "" && legacy.Present && legacy.DriverVersion == "1.0" && JsonSerializer.Serialize(legacy).Contains("\"InfName\":\"\""),
            "Device sem InfName e Present no JSON continua desserializando, com INF vazio e presente");

        var label = ToolCatalog.Label("diagnose_device");
        var result = WindowsTools.DeviceDiagnosisResult(twins, "");
        using (var payload = JsonDocument.Parse(result))
            check(payload.RootElement.GetProperty("data").TryGetProperty("drivers", out var listedDrivers) && listedDrivers.GetArrayLength() == 3
                && payload.RootElement.GetProperty("data").GetProperty("devices").EnumerateArray().Any(device => device.GetProperty("InfName").GetString() == "wdma_usb.inf")
                && payload.RootElement.GetProperty("data").GetProperty("note").GetString()!.Contains("sem driver instalado"),
                "Resultado do diagnose_device leva drivers, o INF no dispositivo e a explicação para o modelo");
        var shown = ToolResultPresentation.Describe(label, result);
        check(shown.State == "3 dispositivos com erro" && !shown.Warning
            && shown.Detail.Contains("• Fifine Microphone: desativado (código 22)\n  Driver: wdma_usb.inf · versão 10.0.19041.3636 · Microsoft")
            && shown.Detail.Contains("• Fifine Microphone (controle): desativado (código 22)\n  Driver: oem42.inf · versão 2.0.0.1 · Fifine")
            && shown.Detail.Contains("• Dispositivo de áudio USB: código 28\n  Driver: sem driver instalado") && !shown.Detail.Contains("Alto-falantes"),
            "Apresentação do diagnóstico mostra INF, versão e fornecedor do dispositivo com erro, em português");
        var calm = DriverStoreInventory.ParseDeviceDiagnosis(Json([Entry("Alto-falantes", "HDAUDIO\\1", 0)], [], "skipped"));
        check(ToolResultPresentation.Describe(label, WindowsTools.DeviceDiagnosisResult(calm, "")).State == "Executado",
            "Diagnóstico sem dispositivo com erro mantém a apresentação anterior");
        check(!ToolResultPresentation.Describe(ToolCatalog.Label("get_services"), result).State.Contains("com erro"),
            "Apresentação de dispositivos com erro vale só para o diagnose_device");

        // Revisão do @qa (C1): dispositivo desconectado (Code 45 ou não presente) continua na lista, mas não conta como "com erro".
        var ghost = DriverStoreInventory.ParseDeviceDiagnosis(Json([Entry("Fone Bluetooth antigo", Ghost, 45), Entry("Alto-falantes", "HDAUDIO\\1", 0)], [], "skipped"));
        check(ghost.Devices.Count == 2 && DriverStoreInventory.FaultyDrivers(ghost).Length == 0
            && ToolResultPresentation.Describe(label, WindowsTools.DeviceDiagnosisResult(ghost, "")).State == "Executado",
            "Code 45 mais Code 0 fica como Executado, sem dispositivo com erro, e a lista mantém os dois");
        var unplugged = DriverStoreInventory.ParseDeviceDiagnosis(Json([Entry("Fifine Microphone", Mic, 22, present: false)], [Row(Mic, "wdma_usb.inf", "10.0.19041.3636", "Microsoft")]));
        check(unplugged.Devices.Single() is { Present: false, InfName: "" } && DriverStoreInventory.FaultyDrivers(unplugged).Length == 0,
            "Dispositivo não presente não conta como com erro nem recebe driver");
        var crowd = DriverStoreInventory.ParseDeviceDiagnosis(Json(
            [.. Enumerable.Range(1, 8).Select(index => Entry("Fone Bluetooth antigo " + index, Ghost + index, 45, present: false)), Entry("Fifine Microphone", Mic, 22)],
            [Row(Mic, "wdma_usb.inf", "10.0.19041.3636", "Microsoft")]));
        var crowded = ToolResultPresentation.Describe(label, WindowsTools.DeviceDiagnosisResult(crowd, ""));
        check(crowded.State == "1 dispositivo com erro" && crowded.Detail == "• Fifine Microphone: desativado (código 22)\n  Driver: wdma_usb.inf · versão 10.0.19041.3636 · Microsoft",
            "Fifine em Code 22 depois de 8 dispositivos em Code 45 ainda aparece, sozinho");
        // Revisão do @qa (Q17): um único dispositivo passando pelo resultado real.
        var lone = DriverStoreInventory.ParseDeviceDiagnosis(Json([Entry("Fifine Microphone", Mic, 22)], [Row(Mic, "wdma_usb.inf", "10.0.19041.3636", "Microsoft")]));
        var loneResult = WindowsTools.DeviceDiagnosisResult(lone, "");
        using (var payload = JsonDocument.Parse(loneResult))
            check(payload.RootElement.GetProperty("data").GetProperty("count").GetInt32() == 1
                && payload.RootElement.GetProperty("data").TryGetProperty("drivers", out var loneDrivers) && loneDrivers.ValueKind == JsonValueKind.Array && loneDrivers.GetArrayLength() == 1
                && loneDrivers[0].GetProperty("Driver").GetString() == "wdma_usb.inf · versão 10.0.19041.3636 · Microsoft"
                && ToolResultPresentation.Describe(label, loneResult).State == "1 dispositivo com erro",
                "Diagnóstico com um único dispositivo com erro traz drivers com um item e mostra 1 dispositivo com erro");

        RunScript(check);
    }

    private static string Json(object[] devices, object[] drivers, string query = "ok")
        => JsonSerializer.Serialize(new { Devices = devices, Drivers = drivers, DriverQuery = query });
    private static object Entry(string name, string id, int code, bool present = true) => new { Name = name, Id = id, Code = code, Status = code == 0 ? "OK" : "Error", Present = present, HardwareIds = new[] { id }, CompatibleIds = Array.Empty<string>() };
    private static object Row(string id, string inf, string version, string provider) => new { DeviceId = id, InfName = inf, Version = version, Provider = provider };

    private static void RunScript(Action<bool, string> check)
    {
        var shell = OperatingSystem.IsWindows() ? "powershell.exe" : Environment.GetEnvironmentVariable("NEXO_CHECKS_PWSH");
        if (string.IsNullOrWhiteSpace(shell)) return;
        var dir = Path.Combine(Path.GetTempPath(), "nexo-device-driver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            var harness = Path.Combine(dir, "harness.ps1");
            File.WriteAllText(harness, Harness, bom);
            foreach (var scenario in Scenarios)
                File.WriteAllText(Path.Combine(dir, "run-" + scenario + ".ps1"), SupportService.PowershellCommand(DriverStoreInventory.DeviceDiagnosisScript(AudioClasses)), bom);
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harness, "-Dir", dir, "-Scenarios", string.Join(",", Scenarios) })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(60_000);
            if (!exited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            check(exited && process.ExitCode == 0 && stderr.Result.Trim() == "Falha WMI simulada na lista de dispositivos",
                "Script do diagnose_device executado com Get-CimInstance falso termina sem erro do PowerShell " + stderr.Result.Trim() + stdout.Result.Trim());

            (int Signed, int Exit, int Timeout) Meta(string name)
            {
                using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta-" + name + ".json")));
                return (meta.RootElement.GetProperty("Signed").GetInt32(), meta.RootElement.GetProperty("Exit").GetInt32(), meta.RootElement.GetProperty("Timeout").GetInt32());
            }
            (DeviceDiagnosis Parsed, JsonElement Raw, int Signed, int Exit) Read(string name)
            {
                var raw = File.ReadAllText(Path.Combine(dir, "out-" + name + ".json"));
                using var output = JsonDocument.Parse(raw);
                var meta = Meta(name);
                return (DriverStoreInventory.ParseDeviceDiagnosis(raw), output.RootElement.Clone(), meta.Signed, meta.Exit);
            }
            string[] Ids(JsonElement raw) => raw.GetProperty("Drivers").EnumerateArray().Select(row => row.GetProperty("DeviceId").GetString() ?? "").ToArray();

            var fifine = Read("fifine");
            var fifineMic = fifine.Parsed.Devices.Single(device => device.Id == Mic);
            check(fifine.Exit == 0 && fifine.Signed == 1 && fifine.Parsed.DriverQuery == "ok" && fifine.Parsed.Devices.Count == 4
                && fifine.Parsed.Devices.Single(device => device.Id == Ghost) is { Present: false, Code: 45, InfName: "" }
                && Ids(fifine.Raw).OrderBy(id => id).SequenceEqual(new[] { Mic, Unknown }.OrderBy(id => id)),
                "Script executado: uma consulta de drivers, que devolve só os IDs presentes com erro da categoria, e o desconectado continua na lista");
            check(fifineMic is { Code: 22, InfName: "wdma_usb.inf", DriverVersion: "10.0.19041.3636", DriverProvider: "Microsoft" }
                && fifine.Parsed.Devices.Single(device => device.Id == Sibling) is { Code: 0, InfName: "" },
                "Script executado: Fifine em Code 22 recebe wdma_usb.inf 10.0.19041.3636 da Microsoft, e o MI_02 sem erro fica sem driver");
            var fifineUnknown = fifine.Parsed.Devices.Single(device => device.Id == Unknown);
            check(fifineUnknown is { Code: 28, InfName: "", DriverVersion: "", DriverProvider: "" } && DriverStoreInventory.DriverSummary(fifineUnknown, fifine.Parsed.DriverQuery) == "sem driver instalado",
                "Script executado: Code 28 com linha sem INF fica sem driver instalado");
            var calm = Read("healthy");
            check(calm.Exit == 0 && calm.Signed == 0 && calm.Parsed.DriverQuery == "skipped" && Ids(calm.Raw).Length == 0 && calm.Parsed.Devices.Count == 2 && DriverStoreInventory.FaultyDrivers(calm.Parsed).Length == 0,
                "Script executado: sem dispositivo com erro, Win32_PnPSignedDriver não é consultado");
            var single = Read("single");
            check(single.Exit == 0 && single.Raw.GetProperty("Devices").ValueKind == JsonValueKind.Array && single.Raw.GetProperty("Drivers").ValueKind == JsonValueKind.Array
                && single.Parsed.Devices.Single().InfName == "wdma_usb.inf",
                "Script executado: com um só dispositivo e um só driver, as listas continuam listas");
            var pair = Read("twins");
            check(pair.Exit == 0 && pair.Signed == 1 && pair.Parsed.Devices.Single(device => device.Id == Mic).InfName == "wdma_usb.inf"
                && pair.Parsed.Devices.Single(device => device.Id == Sibling).InfName == "oem42.inf",
                "Script executado: MI_00 e MI_02 com erro recebem cada um o seu driver, mesmo com o ID do driver em outra caixa");
            var broken = Read("driverfail");
            check(broken.Exit == 0 && broken.Signed == 1 && broken.Parsed.DriverQuery == "failed" && broken.Parsed.Devices.Count == 4
                && DriverStoreInventory.DriverSummary(broken.Parsed.Devices.Single(device => device.Id == Mic), broken.Parsed.DriverQuery) == "não foi possível conferir o driver no Windows"
                && DriverStoreInventory.DriverSummary(broken.Parsed.Devices.Single(device => device.Id == Unknown), broken.Parsed.DriverQuery) == "sem driver instalado",
                "Script executado: falha na consulta de drivers mantém os dispositivos e diz que o driver não foi consultado");
            var phantom = Read("ghost");
            check(phantom.Exit == 0 && phantom.Signed == 0 && phantom.Parsed.DriverQuery == "skipped" && phantom.Parsed.Devices.Count == 2
                && ToolResultPresentation.Describe(ToolCatalog.Label("diagnose_device"), WindowsTools.DeviceDiagnosisResult(phantom.Parsed, "")).State == "Executado",
                "Script executado: Code 45 desconectado mais Code 0 não consulta drivers e fica como Executado");
            var slow = Read("drivertimeout");
            check(slow.Exit == 0 && slow.Signed == 1 && Meta("drivertimeout").Timeout == DriverStoreInventory.DriverQueryTimeoutSeconds && slow.Parsed.DriverQuery == "failed"
                && slow.Parsed.Devices.Count == 4 && DriverStoreInventory.DriverSummary(slow.Parsed.Devices.Single(device => device.Id == Mic), slow.Parsed.DriverQuery) == "não foi possível conferir o driver no Windows",
                "Script executado: tempo esgotado só na consulta de drivers mantém os dispositivos e deixa o driver como não conferido");
            var listFailed = Meta("entityfail");
            check(listFailed.Exit == 1 && listFailed.Signed == 0 && File.ReadAllText(Path.Combine(dir, "out-entityfail.json")).Trim().Length == 0,
                "Script executado: erro do Windows na lista de dispositivos encerra o script com código 1, como antes");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private const string Harness = """
        param([string]$Dir, [string]$Scenarios)
        $ErrorActionPreference='Stop'
        $global:Mic='USB\VID_3142&PID_5060&MI_00\7&2B6E3F1&0&0000'
        $global:Sibling='USB\VID_3142&PID_5060&MI_02\7&2B6E3F1&0&0002'
        $global:Unknown='USB\VID_1234&PID_0001\5&ABCDEF&0&3'
        $global:Gpu='PCI\VEN_10DE&DEV_2486&SUBSYS_163019DA&REV_A1\4&2283F625&0&0019'
        $global:Ghost='BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001005D_PID&B013\8&2A7C1F&0&0C9B3A'
        function global:Entity($id, $class, $name, $code, $present=$true) { [pscustomobject]@{PNPDeviceID=$id; PNPClass=$class; Name=$name; ConfigManagerErrorCode=$code; Present=$present; Status=$(if($code -eq 0){'OK'}else{'Error'}); HardwareID=@($id); CompatibleID=@('USB\Class_01')} }
        function global:Get-CimInstance {
            [CmdletBinding()] param([Parameter(Position=0)][string]$ClassName, [int]$OperationTimeoutSec)
            if($ClassName -eq 'Win32_PnPSignedDriver'){
                $global:Signed++; $global:Timeout=$OperationTimeoutSec
                if($global:Scenario -eq 'driverfail'){throw 'Falha WMI simulada'}
                if($global:Scenario -eq 'drivertimeout'){throw 'A operação CIM excedeu o tempo limite'}
                [pscustomobject]@{DeviceID=$global:Sibling; InfName='oem42.inf'; DriverVersion='2.0.0.1'; DriverProviderName='Fifine'}
                [pscustomobject]@{DeviceID=$global:Gpu; InfName='nv_dispi.inf'; DriverVersion='32.0.15.9186'; DriverProviderName='NVIDIA'}
                [pscustomobject]@{DeviceID=$global:Unknown; InfName=$null; DriverVersion=$null; DriverProviderName=$null}
                [pscustomobject]@{DeviceID=$null; InfName='machine.inf'; DriverVersion='10.0.19041.1'; DriverProviderName='Microsoft'}
                [pscustomobject]@{DeviceID=$(if($global:Scenario -eq 'twins'){$global:Mic.ToLowerInvariant()}else{$global:Mic}); InfName='wdma_usb.inf'; DriverVersion='10.0.19041.3636'; DriverProviderName='Microsoft'}
                return
            }
            if($ClassName -ne 'Win32_PnPEntity'){throw ('Classe inesperada: '+$ClassName)}
            if($global:Scenario -eq 'entityfail'){Write-Error 'Falha WMI simulada na lista de dispositivos'; return}
            switch($global:Scenario){
                'ghost' {Entity $global:Ghost 'MEDIA' 'Fone Bluetooth antigo' 45 $false; Entity $global:Mic 'MEDIA' 'Fifine Microphone' 0}
                'healthy' {Entity $global:Mic 'MEDIA' 'Fifine Microphone' 0; Entity $global:Sibling 'MEDIA' 'Fifine Microphone (controle)' 0}
                'single' {Entity $global:Mic 'MEDIA' 'Fifine Microphone' 22}
                'twins' {Entity $global:Mic 'MEDIA' 'Fifine Microphone' 22; Entity $global:Sibling 'MEDIA' 'Fifine Microphone (controle)' 22}
                default {Entity $global:Mic 'MEDIA' 'Fifine Microphone' 22; Entity $global:Sibling 'MEDIA' 'Fifine Microphone (controle)' 0; Entity $global:Unknown 'MEDIA' 'Dispositivo de áudio USB' 28; Entity $global:Gpu 'Display' 'NVIDIA GeForce RTX 3060 Ti' 43; Entity $global:Ghost 'MEDIA' 'Fone Bluetooth antigo' 45 $false}
            }
        }
        # O script real define a própria preferência de erro; o harness não a impõe.
        $ErrorActionPreference='Continue'
        foreach($scenario in $Scenarios.Split(',')){
            $global:Scenario=$scenario; $global:Signed=0; $global:Timeout=0; $global:LASTEXITCODE=0
            $out=& (Join-Path $Dir ('run-'+$scenario+'.ps1'))
            $exit=[int]$global:LASTEXITCODE
            (@($out) -join "`n") | Set-Content -LiteralPath (Join-Path $Dir ('out-'+$scenario+'.json')) -Encoding UTF8
            [pscustomobject]@{Signed=[int]$global:Signed; Exit=$exit; Timeout=[int]$global:Timeout} | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $Dir ('meta-'+$scenario+'.json')) -Encoding UTF8
        }
        """;
}
