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
    private static readonly string[] Scenarios = ["none", "kept", "lost28", "lost28restart", "vanished", "disabled22", "nullcode", "late", "two", "lowercase", "enumfail"];

    public static string Script() => WindowsTools.NvidiaDriverInstallationScript("C:\\nvidia.exe",
        new OfficialDriverSource.DriverOffer("282412", "621.01", new Uri("https://us.download.nvidia.com/Windows/621.01/driver.exe"), "NVIDIA GeForce RTX 3060 Ti", "32.0.15.9186"),
        GpuId, "32.0.15.9186");

    public static string Result(string verification, string status, int? beforeCode, int? afterCode, bool? preserved, bool reboot = false, string version = "") =>
        WindowsTools.VerifyDriverInstallationResult(WindowsTools.Ok(new
        {
            Driver = "NVIDIA 621.01", Target = "NVIDIA GeForce RTX 3060 Ti", BeforeVersion = "32.0.15.9186", AfterVersion = "32.0.16.2114",
            RebootRequired = reboot, Verification = verification, HdmiAudioBefore = status != "absent", HdmiAudioStatus = status,
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
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private sealed record Scenario(JsonElement Root, string[] Args, bool Started, int Sleeps)
    {
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
        $global:SecondAudioId='HDAUDIO\FUNC_01&VEN_10DE&DEV_0083&SUBSYS_00000000&REV_1001\5&AAA&0&0001'
        function global:Audio($code, $id=$global:AudioId) { [pscustomobject]@{PNPDeviceID=$id; Present=$true; PNPClass='MEDIA'; Name='NVIDIA High Definition Audio'; ConfigManagerErrorCode=$code} }
        function global:Get-CimInstance {
            [CmdletBinding()] param([Parameter(Position=0)][string]$ClassName)
            if($ClassName -eq 'Win32_PnPSignedDriver'){
                [pscustomobject]@{DeviceID=$global:GpuId; DriverVersion=$(if($global:Started){'32.0.16.2114'}else{'32.0.15.9186'})}
                if($global:Scenario -ne 'none'){[pscustomobject]@{DeviceID=$global:AudioId; DriverVersion='1.4.6.6'}}
                return
            }
            if($global:Started){$global:AfterReads++}else{$global:BeforeReads++}
            if($global:Scenario -eq 'enumfail' -and $global:BeforeReads -eq 2){throw 'Falha WMI simulada'}
            $after=$global:Started
            [pscustomobject]@{PNPDeviceID=$global:GpuId; Present=$true; PNPClass='Display'; Name='NVIDIA GeForce RTX 3060 Ti'; ConfigManagerErrorCode=0}
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
            }
        }
        function global:Get-AuthenticodeSignature { param([string]$LiteralPath) [pscustomobject]@{Status='Valid'; SignerCertificate=[pscustomobject]@{Subject='CN=NVIDIA Corporation'}} }
        function global:Start-Process {
            [CmdletBinding()] param([string]$FilePath, [object[]]$ArgumentList, [switch]$PassThru)
            $global:Started=$true; $global:LaunchArgs=@($ArgumentList)
            $process=[pscustomobject]@{ExitCode=$(if($global:Scenario -eq 'lost28restart'){1}else{0})}
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
