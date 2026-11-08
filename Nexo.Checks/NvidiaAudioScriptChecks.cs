using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nexo.Support;

namespace Nexo.Checks;

// Executa o script real de instalação NVIDIA, dentro do mesmo invólucro do processo elevado, trocando
// Get-CimInstance, Start-Process, Get-AuthenticodeSignature e Start-Sleep por funções falsas.
// Roda no Windows (powershell.exe, como no CI). Fora dele, só roda se NEXO_CHECKS_PWSH apontar para um pwsh.
internal static class NvidiaAudioScriptChecks
{
    private const string GpuId = "PCI\\VEN_10DE&DEV_2486&SUBSYS_163019DA&REV_A1\\4&2283F625&0&0019";
    private static readonly string[] Scenarios = ["none", "kept", "lost28", "lost28restart", "vanished", "disabled22", "nullcode", "late", "two", "lowercase", "enumfail",
        "exit5kept", "exit5lost", "exit5nochange", "otherbuild", "gpugone", "gpunullcode",
        "secondgpu", "exitnull", "twosigned", "basicfallback", "exit5code14", "exit2kept"];
    // Código real do ensaio de 07/11/2026: o setup 621.14 saiu com 0x80070005 e o Windows ficou em 32.0.16.2114 (= 621.14).
    public const int AccessDenied = unchecked((int)0x80070005);

    public static string Script() => WindowsTools.NvidiaDriverInstallationScript("C:\\nvidia.exe",
        new OfficialDriverSource.DriverOffer("283968", "621.14", new Uri("https://us.download.nvidia.com/Windows/621.14/621.14-desktop-win10-win11-64bit-international-dch-whql.exe"), "NVIDIA GeForce RTX 3060 Ti", "32.0.15.9186"),
        GpuId, "32.0.15.9186");

    // Monta a saída do script para o estado pedido e passa pela classificação em C#, como o executor faz.
    public static string Result(string verification, string status, int? beforeCode, int? afterCode, bool? preserved, bool reboot = false, string version = "", int exitCode = 0) =>
        WindowsTools.VerifyDriverInstallationResult(WindowsTools.Ok(new
        {
            Driver = "NVIDIA 621.14", OfferedVersion = "621.14", Target = "NVIDIA GeForce RTX 3060 Ti", BeforeVersion = "32.0.15.9186",
            AfterVersion = verification switch { "unexpected_version" => "32.0.16.2101", "unchanged" => "32.0.15.9186", _ => "32.0.16.2114" },
            DeviceCodeAfter = verification == "unconfirmed" ? -1 : 0, Source = "NVIDIA", InstallerExitCode = reboot ? 1 : exitCode,
            RebootRequired = reboot, HdmiAudioBefore = status != "absent", HdmiAudioStatus = status,
            HdmiAudioBeforeCode = beforeCode, HdmiAudioAfterCode = afterCode, HdmiAudioPreserved = preserved, HdmiAudioDriverVersion = version
        }));

    public static void Run(Action<bool, string> check)
    {
        var shell = OperatingSystem.IsWindows() ? "powershell.exe" : Environment.GetEnvironmentVariable("NEXO_CHECKS_PWSH");
        if (string.IsNullOrWhiteSpace(shell)) return;
        var dir = Path.Combine(Path.GetTempPath(), "nexo-nvidia-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            var harness = Path.Combine(dir, "harness.ps1");
            File.WriteAllText(harness, Harness, bom);
            foreach (var scenario in Scenarios)
                File.WriteAllText(Path.Combine(dir, "run-" + scenario + ".ps1"), WindowsTools.ElevatedWrapper(Script(), Path.Combine(dir, "out-" + scenario + ".json")), bom);
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harness, "-Dir", dir, "-Scenarios", string.Join(",", Scenarios) })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(120_000);
            if (!exited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            check(exited && process.ExitCode == 0 && stderr.Result.Trim().Length == 0,
                "Script NVIDIA executado com cmdlets falsos termina sem erro do PowerShell " + stderr.Result.Trim() + stdout.Result.Trim());

            Scenario Read(string name)
            {
                var output = WindowsTools.VerifyDriverInstallationResult(File.ReadAllText(Path.Combine(dir, "out-" + name + ".json")));
                using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta-" + name + ".json")));
                var args = meta.RootElement.GetProperty("Args");
                return new Scenario(JsonDocument.Parse(output).RootElement.Clone(),
                    args.ValueKind == JsonValueKind.Array ? args.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : [],
                    meta.RootElement.GetProperty("Started").GetBoolean(), meta.RootElement.GetProperty("Sleeps").GetInt32());
            }
            var withAudio = new[] { "-s", "-n", "Display.Driver", "HDAudio.Driver" };

            var none = Read("none");
            check(none.Args.SequenceEqual(["-s", "-n", "Display.Driver"]) && !none.Bool("HdmiAudioBefore") && none.Text("HdmiAudioStatus") == "absent"
                && none.Kind("HdmiAudioPreserved") == JsonValueKind.Null && none.Kind("HdmiAudioAfterCode") == JsonValueKind.Null && none.Sleeps == 3 && none.Note() == "",
                "Script executado: sem áudio NVIDIA, instala só Display.Driver e não fala de áudio");
            var kept = Read("kept");
            check(kept.Args.SequenceEqual(withAudio) && kept.Text("HdmiAudioStatus") == "preserved" && kept.Bool("HdmiAudioPreserved")
                && kept.Int("HdmiAudioBeforeCode") == 0 && kept.Int("HdmiAudioAfterCode") == 0 && kept.Text("HdmiAudioDriverVersion") == "1.4.6.6"
                && kept.Sleeps == 3 && kept.Root.TryGetProperty("audioTest", out var keptTest) && keptTest.GetString()!.Contains("driver 1.4.6.6"),
                "Script executado: áudio preservado passa HDAudio.Driver separado, lê a versão do áudio e pede teste de som");
            var lost = Read("lost28");
            check(lost.Text("HdmiAudioStatus") == "failed" && !lost.Bool("HdmiAudioPreserved") && lost.Int("HdmiAudioAfterCode") == 28
                && lost.Sleeps == 3 + 4 * 2 && lost.Field("warning").Contains("código 28") && !lost.Field("warning").Contains("reinicialização"),
                "Script executado: Code 28 depois avisa a perda após 5 leituras, sem espera depois da última");
            var restart = Read("lost28restart");
            check(restart.Text("Verification") == "pending_restart" && restart.Field("warning").Contains("reinicie"),
                "Script executado: com reinício pedido pelo Windows, o aviso manda reiniciar");
            var vanished = Read("vanished");
            check(vanished.Text("HdmiAudioStatus") == "vanished" && vanished.Int("HdmiAudioAfterCode") == -1 && vanished.Field("warning").Contains("não o encontra mais"),
                "Script executado: dispositivo de áudio sumido vira -1 e gera aviso");
            var disabled = Read("disabled22");
            check(disabled.Args.SequenceEqual(withAudio) && disabled.Text("HdmiAudioStatus") == "preexisting" && disabled.Int("HdmiAudioBeforeCode") == 22
                && disabled.Int("HdmiAudioAfterCode") == 22 && disabled.Sleeps == 3 && disabled.Field("warning") == "" && disabled.Field("audioInfo").Contains("código 22"),
                "Script executado: áudio já desativado antes (22) não gera aviso contra a instalação");
            var nullCode = Read("nullcode");
            check(nullCode.Text("HdmiAudioStatus") == "unknown" && nullCode.Kind("HdmiAudioAfterCode") == JsonValueKind.Null && !nullCode.Bool("HdmiAudioPreserved")
                && nullCode.Field("audioTest").Contains("não informou o estado"),
                "Script executado: código de áudio nulo fica desconhecido e não vira preservado");
            var late = Read("late");
            check(late.Text("HdmiAudioStatus") == "preserved" && late.Sleeps == 3 + 2 + 2,
                "Script executado: áudio que volta na terceira leitura é preservado e para de esperar");
            var two = Read("two");
            check(two.Text("HdmiAudioStatus") == "failed" && two.Int("HdmiAudioAfterCode") == 28,
                "Script executado: com dois áudios NVIDIA, o que ficou com erro é o informado");
            var lowercase = Read("lowercase");
            check(lowercase.Text("HdmiAudioStatus") == "preserved",
                "Script executado: ID do áudio com outra caixa depois continua sendo o mesmo dispositivo");
            var failed = Read("enumfail");
            check(!failed.Started && !failed.Root.GetProperty("success").GetBoolean()
                && failed.Root.GetProperty("error").GetString()!.Contains("O instalador NVIDIA não foi iniciado e nenhum driver foi alterado"),
                "Script executado: falha do WMI antes da instalação não inicia o instalador e diz que nada foi alterado");

            // Issue #13: o código de saída não interrompe a medição, e a versão é comparada com a oferta.
            check(kept.Text("Verification") == "expected_version_confirmed" && kept.Text("OfferedVersion") == "621.14" && kept.Int("InstallerExitCode") == 0
                && kept.Field("installerWarning") == "" && kept.Root.GetProperty("success").GetBoolean(),
                "Script executado: código 0 com 32.0.16.2114 confirma a oferta 621.14 sem aviso do instalador");
            var exit5 = Read("exit5kept");
            check(exit5.Root.GetProperty("success").GetBoolean() && exit5.Text("Verification") == "expected_version_confirmed"
                && exit5.Int("InstallerExitCode") == AccessDenied && exit5.Text("AfterVersion") == "32.0.16.2114" && exit5.Int("DeviceCodeAfter") == 0
                && exit5.Field("installerWarning").Contains("código -2147024891 (0x80070005)") && exit5.Field("installerWarning").Contains("mas o Windows confirma a versão oferecida, com a placa sem erro")
                && exit5.Text("HdmiAudioStatus") == "preserved" && exit5.Field("audioTest").Contains("driver 1.4.6.6") && exit5.Sleeps == 3
                && WindowsTools.ResultState(exit5.Raw) == "reported",
                "Script executado: 0x80070005 com a versão oferecida confirmada vira sucesso com aviso e ainda lê o áudio");
            var exit5Lost = Read("exit5lost");
            check(exit5Lost.Root.GetProperty("success").GetBoolean() && exit5Lost.Text("HdmiAudioStatus") == "failed" && exit5Lost.Int("HdmiAudioAfterCode") == 28
                && exit5Lost.Sleeps == 3 + 4 * 2 && exit5Lost.Field("warning").Contains("código 28") && exit5Lost.Field("installerWarning").Contains("0x80070005"),
                "Script executado: com 0x80070005, o áudio é lido nas 5 tentativas e a perda do som é avisada");
            var exit5Same = Read("exit5nochange");
            check(!exit5Same.Root.GetProperty("success").GetBoolean() && exit5Same.Root.GetProperty("installationReported").GetBoolean()
                && exit5Same.Text("Verification") == "unchanged" && exit5Same.Field("error").Contains("código -2147024891 (0x80070005)")
                && exit5Same.Field("error").Contains("versão anterior 32.0.15.9186") && exit5Same.Text("HdmiAudioStatus") == "preserved"
                && exit5Same.Field("error").Contains("driver 1.4.6.6") && WindowsTools.ResultState(exit5Same.Raw) == "failed",
                "Script executado: 0x80070005 sem mudança de versão é falha com o código no erro e o áudio lido");
            var other = Read("otherbuild");
            check(!other.Root.GetProperty("success").GetBoolean() && other.Text("Verification") == "unexpected_version"
                && other.Field("error").Contains("32.0.16.2101 (driver 621.01), que não corresponde ao driver 621.14 oferecido") && !other.Field("error").Contains("código 0"),
                "Script executado: versão diferente da oferta não é atribuída à oferta");
            var gone = Read("gpugone");
            check(!gone.Root.GetProperty("success").GetBoolean() && gone.Text("Verification") == "unconfirmed" && gone.Int("DeviceCodeAfter") == -1
                && gone.Field("error").Contains("não encontrou a placa NVIDIA de forma única"),
                "Script executado: placa que continua listada, mas não presente, depois da instalação fica não confirmada");
            var nullGpu = Read("gpunullcode");
            check(!nullGpu.Root.GetProperty("success").GetBoolean() && nullGpu.Text("Verification") == "unconfirmed" && nullGpu.Int("DeviceCodeAfter") == -1
                && nullGpu.Text("AfterVersion") == "32.0.16.2114",
                "Script executado: placa sem código informado não vira Code 0, mesmo na versão oferecida");
            var secondGpu = Read("secondgpu");
            check(secondGpu.Text("Verification") == "unchanged" && secondGpu.Text("AfterVersion") == "32.0.15.9186" && !secondGpu.Root.GetProperty("success").GetBoolean(),
                "Script executado: outra placa NVIDIA já na versão oferecida não confirma a oferta no alvo, que continua na versão antiga");
            var exitNull = Read("exitnull");
            check(exitNull.Kind("InstallerExitCode") == JsonValueKind.Null && exitNull.Text("Verification") == "unconfirmed" && !exitNull.Bool("RebootRequired")
                && exitNull.Field("error").Contains("o instalador não informou o código de saída") && exitNull.Text("HdmiAudioStatus") == "preserved",
                "Script executado: código de saída nulo não vira 0, fica não confirmado e o áudio ainda é lido");
            var twoSigned = Read("twosigned");
            check(twoSigned.Text("Verification") == "unconfirmed" && twoSigned.Int("AfterDriverCount") == 2 && twoSigned.Text("AfterVersion") == ""
                && twoSigned.Field("error").Contains("o Windows informou mais de um driver para a placa"),
                "Script executado: dois drivers assinados para a placa não escolhem um deles e dizem que vieram vários");
            var basic = Read("basicfallback");
            check(basic.Text("Verification") == "unexpected_version" && basic.Field("error").Contains("(0xE4000020)")
                && basic.Field("error").Contains("10.0.19041.5794, que não corresponde ao driver 621.14 oferecido"),
                "Script executado: placa que cai no driver básico do Windows não é atribuída à oferta");
            var code14 = Read("exit5code14");
            check(code14.Root.GetProperty("success").GetBoolean() && code14.Text("Verification") == "pending_restart" && code14.Text("RestartRequestedBy") == "windows"
                && code14.Int("DeviceCodeAfter") == 14 && code14.Field("installerWarning").Contains("0x80070005") && !code14.Field("error").Contains("com erro"),
                "Script executado: placa em Code 14 com a versão oferecida fica como reinício pendente, não como erro");
            var exit2 = Read("exit2kept");
            check(exit2.Root.GetProperty("success").GetBoolean() && exit2.Text("Verification") == "expected_version_confirmed"
                && exit2.Field("installerWarning").Contains("código 2 (0x00000002)") && exit2.Field("audioTest").Contains("driver 1.4.6.6"),
                "Script executado: código positivo fora de 0 e 1 com a versão oferecida também gera aviso");
            check(restart.Int("InstallerExitCode") == 1 && restart.Bool("RebootRequired") && restart.Root.GetProperty("success").GetBoolean() && restart.Field("installerWarning") == "",
                "Script executado: código 1 fica como reinício pendente, sem aviso de código inesperado");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private sealed record Scenario(JsonElement Root, string[] Args, bool Started, int Sleeps)
    {
        public string Raw => Root.GetRawText();
        private JsonElement Data => Root.TryGetProperty("data", out var data) ? data : default;
        public JsonValueKind Kind(string key) => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(key, out var value) ? value.ValueKind : JsonValueKind.Undefined;
        public bool Bool(string key) => Kind(key) == JsonValueKind.True;
        public int? Int(string key) => Kind(key) == JsonValueKind.Number ? Data.GetProperty(key).GetInt32() : null;
        public string Text(string key) => Kind(key) == JsonValueKind.String ? Data.GetProperty(key).GetString() ?? "" : "";
        public string Field(string key) => Root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        public string Note() => Field("warning") + Field("audioTest") + Field("audioInfo");
    }

    private const string Harness = """
        param([string]$Dir, [string]$Scenarios)
        $ErrorActionPreference='Stop'
        $global:GpuId='PCI\VEN_10DE&DEV_2486&SUBSYS_163019DA&REV_A1\4&2283F625&0&0019'
        $global:AudioId='HDAUDIO\FUNC_01&VEN_10DE&DEV_009E&SUBSYS_19DA1630&REV_1001\5&C53C01E&0&0001'
        $global:OtherGpuId='PCI\VEN_10DE&DEV_2489&SUBSYS_00000000&REV_A1\4&OUTRA&0&0008'
        $global:SecondAudioId='HDAUDIO\FUNC_01&VEN_10DE&DEV_0083&SUBSYS_00000000&REV_1001\5&AAA&0&0001'
        function global:Audio($code, $id=$global:AudioId) { [pscustomobject]@{PNPDeviceID=$id; Present=$true; PNPClass='MEDIA'; Name='NVIDIA High Definition Audio'; ConfigManagerErrorCode=$code} }
        function global:Get-CimInstance {
            [CmdletBinding()] param([Parameter(Position=0)][string]$ClassName)
            if($ClassName -eq 'Win32_PnPSignedDriver'){
                if($global:Scenario -eq 'secondgpu'){[pscustomobject]@{DeviceID=$global:OtherGpuId; DriverVersion='32.0.16.2114'}; [pscustomobject]@{DeviceID=$global:GpuId; DriverVersion='32.0.15.9186'}; return}
                if($global:Scenario -eq 'twosigned' -and $global:Started){[pscustomobject]@{DeviceID=$global:GpuId; DriverVersion='32.0.15.9186'}; [pscustomobject]@{DeviceID=$global:GpuId; DriverVersion='32.0.16.2114'}; return}
                if($global:Scenario -eq 'basicfallback' -and $global:Started){[pscustomobject]@{DeviceID=$global:GpuId; DriverVersion='10.0.19041.5794'}; return}
                [pscustomobject]@{DeviceID=$global:GpuId; DriverVersion=$(if(-not $global:Started -or $global:Scenario -eq 'exit5nochange'){'32.0.15.9186'}elseif($global:Scenario -eq 'otherbuild'){'32.0.16.2101'}else{'32.0.16.2114'})}
                if($global:Scenario -ne 'none'){[pscustomobject]@{DeviceID=$global:AudioId; DriverVersion='1.4.6.6'}}
                return
            }
            if($global:Started){$global:AfterReads++}else{$global:BeforeReads++}
            if($global:Scenario -eq 'enumfail' -and $global:BeforeReads -eq 2){throw 'Falha WMI simulada'}
            $after=$global:Started
            [pscustomobject]@{PNPDeviceID=$global:GpuId; Present=$(-not ($after -and $global:Scenario -eq 'gpugone')); PNPClass='Display'; Name='NVIDIA GeForce RTX 3060 Ti'; ConfigManagerErrorCode=$(if($after -and $global:Scenario -eq 'gpunullcode'){$null}elseif($after -and $global:Scenario -eq 'exit5code14'){14}else{0})}
            if($global:Scenario -eq 'secondgpu'){[pscustomobject]@{PNPDeviceID=$global:OtherGpuId; Present=$true; PNPClass='Display'; Name='NVIDIA GeForce RTX 3060 Ti'; ConfigManagerErrorCode=0}}
            [pscustomobject]@{PNPDeviceID='HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_1458A0C3&REV_1003\4&1&0001'; Present=$true; PNPClass='MEDIA'; Name='Realtek'; ConfigManagerErrorCode=0}
            [pscustomobject]@{PNPDeviceID=$global:SecondAudioId.Replace('AAA','FANTASMA'); Present=$false; PNPClass='MEDIA'; Name='Áudio NVIDIA ausente'; ConfigManagerErrorCode=0}
            switch($global:Scenario){
                'kept' {Audio 0}
                'lost28' {Audio $(if($after){28}else{0})}
                'lost28restart' {Audio $(if($after){28}else{0})}
                'vanished' {if(-not $after){Audio 0}}
                'disabled22' {Audio 22}
                'nullcode' {Audio $(if($after){$null}else{0})}
                'late' {Audio $(if(-not $after){0}elseif($global:AfterReads -ge 4){0}else{28})}
                'two' {Audio 0; Audio $(if($after){28}else{0}) $global:SecondAudioId}
                'lowercase' {Audio 0 $(if($after){$global:AudioId.ToLowerInvariant()}else{$global:AudioId})}
                'enumfail' {Audio 0}
                'exit5kept' {Audio 0}
                'exit5lost' {Audio $(if($after){28}else{0})}
                'exit5nochange' {Audio 0}
                'otherbuild' {Audio 0}
                'gpugone' {Audio 0}
                'gpunullcode' {Audio 0}
                'exitnull' {Audio 0}
                'exit2kept' {Audio 0}
                default {}
            }
        }
        function global:Get-AuthenticodeSignature { param([string]$LiteralPath) [pscustomobject]@{Status='Valid'; SignerCertificate=[pscustomobject]@{Subject='CN=NVIDIA Corporation'}} }
        function global:Start-Process {
            [CmdletBinding()] param([string]$FilePath, [object[]]$ArgumentList, [switch]$PassThru)
            $global:Started=$true; $global:LaunchArgs=@($ArgumentList)
            $process=[pscustomobject]@{ExitCode=$(if($global:Scenario -eq 'lost28restart'){1}elseif($global:Scenario -like 'exit5*'){-2147024891}elseif($global:Scenario -eq 'exitnull'){$null}elseif($global:Scenario -eq 'basicfallback'){-469762016}elseif($global:Scenario -eq 'exit2kept'){2}else{0})}
            $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($milliseconds) $true }
            $process
        }
        function global:Start-Sleep { [CmdletBinding()] param([int]$Seconds) $global:Sleeps+=$Seconds }
        foreach($scenario in $Scenarios.Split(',')){
            $global:Scenario=$scenario; $global:Started=$false; $global:BeforeReads=0; $global:AfterReads=0; $global:Sleeps=0; $global:LaunchArgs=@()
            & (Join-Path $Dir ('run-'+$scenario+'.ps1'))
            [pscustomobject]@{Args=@($global:LaunchArgs | ForEach-Object {[string]$_}); Started=[bool]$global:Started; Sleeps=[int]$global:Sleeps} | ConvertTo-Json -Depth 4 -Compress | Set-Content -LiteralPath (Join-Path $Dir ('meta-'+$scenario+'.json')) -Encoding UTF8
        }
        """;
}
