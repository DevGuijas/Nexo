using System.Diagnostics;
using System.Text;
using Nexo.Support;

namespace Nexo.Checks;

// #131: todo script que o Nexo roda pelo PowerShell (elevado ou não) começa restringindo o PSModulePath à pasta de
// módulos do próprio Windows PowerShell ($PSHOME\Modules). Sem isso, um módulo plantado em Documentos\WindowsPowerShell\Modules
// com o nome de um módulo do Windows (DnsClient, NetAdapter…) seria autocarregado — com admin, depois do UAC do Nexo.
// A parte estrutural roda em qualquer sistema. A parte com PowerShell roda o script gerado de verdade, por arquivo
// temporário (-File), com um DnsClient falso no PSModulePath: no Windows usa o powershell.exe (como no CI); fora dele,
// só com NEXO_CHECKS_PWSH. O controle sem a fixação precisa carregar o módulo plantado, senão o teste não prova nada.
internal static class ModulePathGuardChecks
{
    private const string Expected = "$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules');";
    private const string Planted = "MODULO-PLANTADO";

    public static async Task Run(Action<bool, string> check)
    {
        Structure(check);
        Launch(check);
        StrictLaunch(check);
        SystemPaths(check);
        await Behavior(check);
    }

    private static string Decode(string base64) => Encoding.Unicode.GetString(Convert.FromBase64String(base64));

    private static void Structure(Action<bool, string> check)
    {
        check(PowerShellModulePath.Guard == Expected
            && !PowerShellModulePath.Guard.Contains("SystemRoot", StringComparison.OrdinalIgnoreCase) && !PowerShellModulePath.Guard.Contains("windir", StringComparison.OrdinalIgnoreCase)
            && !PowerShellModulePath.Guard.Contains("USERPROFILE", StringComparison.OrdinalIgnoreCase),
            "#131: a fixação aponta para $PSHOME\\Modules, que vem do powershell.exe do System32, e não de variável de ambiente que o usuário controla (SystemRoot, windir, USERPROFILE)");

        var sample = "Get-Service -Name Spooler";
        var direct = WindowsTools.ElevatedArguments(sample);
        check(direct is ["-NoProfile", "-NonInteractive", "-EncodedCommand", var encoded] && Decode(encoded).StartsWith(Expected, StringComparison.Ordinal)
            && Decode(encoded).EndsWith(sample, StringComparison.Ordinal),
            "#131: o script elevado direto (-EncodedCommand) começa fixando o PSModulePath, antes de qualquer comando do script");
        var wrapped = WindowsTools.ElevatedWrapper(sample, @"C:\ProgramData\NexoSupport\pending\x\result.json");
        check(WindowsTools.ElevatedArguments(wrapped) is [.., var wrappedEncoded] && Decode(wrappedEncoded).StartsWith(Expected, StringComparison.Ordinal),
            "#131: o invólucro elevado das ações (serviço, impressora, adaptador, dispositivo, driver) passa pela fixação central");

        var compact = NetworkRecovery.ElevatedArguments(sample);
        check(compact is ["-NoProfile", "-NonInteractive", "-Command", var loader] && loader.StartsWith(Expected, StringComparison.Ordinal)
            && loader.IndexOf(Expected, StringComparison.Ordinal) < loader.IndexOf("FromBase64String", StringComparison.Ordinal)
            && loader.IndexOf(Expected, StringComparison.Ordinal) < loader.IndexOf(NetworkRecovery.LoaderRun, StringComparison.Ordinal)
            && loader.EndsWith(NetworkRecovery.LoaderRun, StringComparison.Ordinal),
            "#131: o carregador compactado da rede fixa o PSModulePath antes de descompactar e executar o executor");

        check(SupportService.PowershellCommand(sample).StartsWith(Expected, StringComparison.Ordinal),
            "#131: as leituras sem elevação (inventário, serviços, rede, assinatura do driver baixado) também usam só os módulos do Windows");
        check(NetworkRecovery.CurrentStateScript("13").StartsWith(Expected, StringComparison.Ordinal),
            "#131: a leitura de rede da #128 usa a mesma fixação");
        check(OptimizationDiskExecutor.BuildScript("Optimize-Volume -DriveLetter C", @"C:\Program Files\Nexo-x", @"C:\Program Files\Nexo-x\result.json", "S-1-5-21-1-2-3-1001").StartsWith(Expected, StringComparison.Ordinal),
            "#131: o script elevado da otimização começa com a mesma fixação (antes só vinha na 2ª linha)");
        // O bootstrap elevado do Office é conferido no Nexo.Office.Checks, que compila o WindowsOfficeDeployment.

        // Tamanho: a fixação acrescenta poucos caracteres e os executores reais continuam no limite.
        var addedDirect = WindowsTools.CommandLength(WindowsTools.ElevatedArguments(sample)) - WindowsTools.CommandLength(["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(sample))]);
        var addedCompact = NetworkRecovery.Compact(sample).Length - NetworkRecovery.Compact(sample)[(PowerShellModulePath.Guard.Length + 1)..].Length;
        check(addedDirect > 0 && addedDirect <= 160 && addedCompact == PowerShellModulePath.Guard.Length + 1,
            $"#131: a fixação acrescenta {addedDirect} caracteres ao comando direto e {addedCompact} ao compactado");
        var nvidia = WindowsTools.CommandLength(WindowsTools.ElevatedArguments(WindowsTools.ElevatedWrapper(NvidiaAudioScriptChecks.Script(), @"C:\Users\x\AppData\Local\NexoSupport\pending\00000000000000000000000000000000\result.json")));
        var nvidiaFits = true;
        try { WindowsTools.EnsureElevatedFits(WindowsTools.ElevatedWrapper(NvidiaAudioScriptChecks.Script(), @"C:\Users\x\AppData\Local\NexoSupport\pending\00000000000000000000000000000000\result.json")); }
        catch (InvalidOperationException) { nvidiaFits = false; }
        check(nvidiaFits && nvidia < WindowsTools.CommandLineLimit,
            $"#131: com a fixação, o maior script elevado direto (NVIDIA) continua cabendo: {nvidia} de {WindowsTools.CommandLineLimit} caracteres");
    }

    private static void Launch(Action<bool, string> check)
    {
        bool Rejected(IReadOnlyList<string> arguments)
        {
            try { PowerShellModulePath.Require(arguments); return false; }
            catch (InvalidOperationException) { return true; }
        }
        var guardedEncoded = WindowsTools.ElevatedArguments("Get-Service");
        check(!Rejected(guardedEncoded) && !Rejected(NetworkRecovery.ElevatedArguments("Get-Service"))
            && !Rejected(["-NoProfile", "-NonInteractive", "-Command", OptimizationDiskExecutor.BuildScript("x", @"C:\Program Files\Nexo-x", @"C:\Program Files\Nexo-x\result.json", "S-1-5-21-1-2-3-1001")]),
            "#131: o lançamento elevado aceita os scripts gerados pelo Nexo, todos com a fixação");
        var unguarded = Convert.ToBase64String(Encoding.Unicode.GetBytes("Get-Service"));
        var late = Convert.ToBase64String(Encoding.Unicode.GetBytes("Import-Module DnsClient; " + Expected));
        check(Rejected(["-NoProfile", "-NonInteractive", "-EncodedCommand", unguarded])
            && Rejected(["-NoProfile", "-NonInteractive", "-Command", "Get-Service"])
            && Rejected(["-NoProfile", "-NonInteractive", "-EncodedCommand", late])
            && Rejected(["-NoProfile", "-NonInteractive", "-File", @"C:\Users\u\x.ps1"])
            && Rejected(["-NoProfile", "-NonInteractive", "-File", @"C:\Users\u\x.ps1", "-Command", Expected + "Get-Service"])
            && Rejected(["-NoProfile", "-NonInteractive", "-Command", Expected + "Get-Service", "-File", @"C:\Users\u\x.ps1"])
            && Rejected(["-NoProfile", "-NonInteractive", "-EncodedCommand", "isto-nao-e-base64"])
            && Rejected(["-NoProfile", "-NonInteractive", "-EncodedCommand"])
            && Rejected(["-NoProfile", "-NonInteractive"]),
            "#131: sem a fixação no começo (script direto, -Command, fixação depois de outro comando, -File mesmo junto de -Command, Base64 inválido ou sem script), o lançamento elevado recusa antes do UAC");
        check(ThrowsOnLaunch(["-NoProfile", "-NonInteractive", "-EncodedCommand", unguarded]) && !ThrowsOnLaunch(guardedEncoded)
            && ThrowsOnLaunch(WindowsTools.ElevatedArguments(new string('x', WindowsTools.CommandLineLimit))),
            "#131: o lançamento elevado confere a fixação no mesmo passo em que confere o tamanho, antes de pedir o UAC");
    }

    // @qa: o powershell.exe aceita abreviações de parâmetro (-c, -e, -ec, -enc, -f), parâmetros que carregam código
    // (-PSConsoleFile) e, sem -NoProfile, roda os perfis do usuário antes do script. O Require aceita só o formato que o
    // Nexo gera; tudo o mais é recusado, em vez de interpretado.
    private static void StrictLaunch(Action<bool, string> check)
    {
        bool Rejected(IReadOnlyList<string> arguments)
        {
            try { PowerShellModulePath.Require(arguments); return false; }
            catch (InvalidOperationException) { return true; }
        }
        var guarded = Expected + " Get-Service";
        var guardedB64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(guarded));
        check(Rejected(["-c", "Remove-Item C:\\x", "-Command", guarded])
            && Rejected(["-NoProfile", "-c", "evil", "-Command", guarded])
            && Rejected(["-NoProfile", "-NonInteractive", "-e", guardedB64])
            && Rejected(["-NoProfile", "-NonInteractive", "-ec", guardedB64])
            && Rejected(["-NoProfile", "-NonInteractive", "-enc", guardedB64])
            && Rejected(["-NoProfile", "-NonInteractive", "-encodedcommand", guardedB64])
            && Rejected(["-NoProfile", "-NonInteractive", "/Command", guarded])
            && Rejected(["-NoProfile", "-NonInteractive", "-f", @"C:\Users\u\x.ps1"]),
            "#131 (@qa): abreviações e outras grafias de -Command/-EncodedCommand/-File (-c, -e, -ec, -enc, minúsculas, /Command, -f) são recusadas, mesmo com um -Command com a fixação depois");
        check(Rejected(["-NonInteractive", "-Command", guarded]) && Rejected(["-NoProfile", "-Command", guarded])
            && Rejected(["-NonInteractive", "-NoProfile", "-Command", guarded])
            && Rejected(["-NoLogo", "-NonInteractive", "-Command", guarded]) && Rejected(["-NoProfile", "-NoLogo", "-Command", guarded])
            && Rejected(["-NoProfile", "-NonInteractive", "-EncodedCommand", guarded]),
            "#131 (@qa): sem -NoProfile e -NonInteractive nas posições do Nexo (ou com o script em texto puro no -EncodedCommand), o lançamento é recusado: os perfis do usuário rodariam com admin antes da fixação");
        check(Rejected(["-NoProfile", "-NonInteractive", "-Command", guarded, "; Remove-Item C:\\x"])
            && Rejected(["-NoProfile", "-NonInteractive", "-PSConsoleFile", @"C:\Users\u\x.psc1", "-Command", guarded])
            && Rejected(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", guarded])
            && Rejected(["-NoProfile", "-NonInteractive", "-EncodedCommand", guardedB64, "-Command", "Get-Service"]),
            "#131 (@qa): argumentos a mais depois do script (que o -Command juntaria), -PSConsoleFile, -ExecutionPolicy ou um 2º script são recusados");
        check(!Rejected(["-NoProfile", "-NonInteractive", "-Command", guarded]) && !Rejected(["-NoProfile", "-NonInteractive", "-EncodedCommand", guardedB64]),
            "#131 (@qa): o formato que o Nexo gera (-NoProfile -NonInteractive e o script com a fixação) continua aceito");
    }

    // @qa: $env:WINDIR e $env:SystemRoot vêm do ambiente do usuário (HKCU\Environment) também no processo elevado. Um
    // executável ou módulo chamado por esse caminho seria trocado por um do usuário, rodando com admin.
    private static void SystemPaths(Action<bool, string> check)
    {
        var envPath = new System.Text.RegularExpressions.Regex(@"\$\{?env:(windir|systemroot|systemdrive|programfiles|programdata|temp|tmp|localappdata|appdata|userprofile)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string transaction;
        using (var resource = typeof(NetworkChange).Assembly.GetManifestResourceStream("Nexo.NetworkTransaction.ps1")!)
        using (var reader = new StreamReader(resource)) transaction = reader.ReadToEnd();
        string Embedded(string name)
        {
            using var resource = typeof(NetworkChange).Assembly.GetManifestResourceStream(name);
            if (resource == null) return "";
            using var reader = new StreamReader(resource);
            return reader.ReadToEnd();
        }
        var scripts = new (string Name, string Text)[]
        {
            ("executor de rede (NetworkTransaction.ps1)", transaction),
            ("ativação de adaptador (EnableNetworkAdapter.ps1)", Embedded("Nexo.EnableNetworkAdapter.ps1")),
            ("restart_device", WindowsTools.DeviceScript("restart_device", "USB\\VID_1&PID_2\\3")),
            ("enable_device", WindowsTools.DeviceScript("enable_device", "USB\\VID_1&PID_2\\3")),
            ("enable_camera", WindowsTools.DeviceScript("enable_camera", "USB\\VID_1&PID_2\\3")),
            ("erros recentes do System", WindowsTools.SystemErrorEventsScript),
            ("coleta de eventos", WindowsEventCollector.ImportDiagnostics),
            ("otimização", OptimizationDiskExecutor.BuildScript("Optimize-Volume -DriveLetter C", @"C:\Program Files\Nexo-x", @"C:\Program Files\Nexo-x\result.json", "S-1-5-21-1-2-3-1001")),
            ("driver NVIDIA", WindowsTools.ElevatedWrapper(NvidiaAudioScriptChecks.Script(), @"C:\ProgramData\NexoSupport\pending\x\result.json")),
        };
        var offenders = scripts.Where(s => envPath.IsMatch(s.Text)).Select(s => s.Name).ToList();
        check(offenders.Count == 0,
            "#131 (@qa): nenhum script do PowerShell chama executável ou módulo por $env:WINDIR/$env:SystemRoot ou outra variável que o usuário define" + (offenders.Count == 0 ? "" : ": " + string.Join(", ", offenders)));
        check(transaction.Contains("& ([IO.Path]::Combine([Environment]::SystemDirectory,'netsh.exe')) interface", StringComparison.Ordinal)
            && WindowsTools.DeviceScript("restart_device", "X").Contains("& ([IO.Path]::Combine([Environment]::SystemDirectory,'pnputil.exe')) /restart-device", StringComparison.Ordinal)
            && WindowsTools.SystemErrorEventsScript.StartsWith("$log=& ([IO.Path]::Combine([Environment]::SystemDirectory,'wevtutil.exe')) qe System", StringComparison.Ordinal)
            && WindowsEventCollector.ImportDiagnostics.Contains("Combine($PSHOME,'Modules','Microsoft.PowerShell.Diagnostics'", StringComparison.Ordinal),
            "#131 (@qa): netsh.exe (elevado), pnputil.exe (elevado) e wevtutil.exe vêm do System32 por GetSystemDirectory, e o módulo de diagnóstico pelo $PSHOME");
        bool Invalid(string name) { try { PowerShellModulePath.SystemExecutable(name); return false; } catch (ArgumentException) { return true; } }
        check(Invalid("a'.exe") && Invalid("..\\x.exe") && Invalid("netsh") && Invalid("x.exe'); Remove-Item C:\\x; ('")
            && PowerShellModulePath.SystemExecutable("netsh.exe") == "([IO.Path]::Combine([Environment]::SystemDirectory,'netsh.exe'))",
            "#131 (@qa): o caminho de executável do System32 só aceita um nome simples terminado em .exe, sem aspas nem separador");
    }

    private static bool ThrowsOnLaunch(IReadOnlyList<string> arguments)
    {
        try { WindowsTools.CheckElevatedLaunch(arguments); return false; }
        catch (InvalidOperationException) { return true; }
    }

    private static string? Shell()
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var configured = Environment.GetEnvironmentVariable("NEXO_CHECKS_PWSH");
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private static async Task Behavior(Action<bool, string> check)
    {
        var shell = Shell();
        if (shell == null)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                check(false, "#131: o CI precisa de um PowerShell para provar que o módulo plantado não é carregado");
            Console.WriteLine("IGNORADO: #131: módulo plantado (sem powershell.exe e sem NEXO_CHECKS_PWSH)");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "NexoModulePath-" + Guid.NewGuid().ToString("N"));
        try
        {
            var modules = Path.Combine(root, "Documents", "WindowsPowerShell", "Modules");
            var fake = Path.Combine(modules, "DnsClient");
            Directory.CreateDirectory(fake);
            File.WriteAllText(Path.Combine(fake, "DnsClient.psm1"),
                "function Get-DnsClientServerAddress { '" + Planted + "' }\nExport-ModuleMember -Function Get-DnsClientServerAddress\n", new UTF8Encoding(true));
            // O cmdlet é chamado sem Import-Module: é o autocarregamento que um módulo plantado exploraria.
            var probe = "try { $r = Get-DnsClientServerAddress -ErrorAction Stop; if ($r -is [string]) { 'RESULTADO:' + $r } else { 'RESULTADO:WINDOWS' } } catch { 'RESULTADO:AUSENTE' }";

            var control = await RunFile(shell, root, modules, probe);
            check(control.Contains("RESULTADO:" + Planted, StringComparison.Ordinal),
                "#131: controle — sem a fixação, o PowerShell autocarrega o DnsClient plantado no PSModulePath (" + Last(control) + ")");

            var cases = new (string Name, string Script)[]
            {
                ("elevado direto (-EncodedCommand)", Decode(WindowsTools.ElevatedArguments(probe)[^1])),
                ("carregador compactado da rede (-Command)", NetworkRecovery.ElevatedArguments(probe)[^1]),
                ("leitura sem elevação", SupportService.PowershellCommand(probe)),
            };
            foreach (var (name, script) in cases)
            {
                var output = await RunFile(shell, root, modules, script);
                check(output.Contains("RESULTADO:", StringComparison.Ordinal) && !output.Contains(Planted, StringComparison.Ordinal),
                    "#131: " + name + " — com a fixação, o DnsClient plantado não é carregado (" + Last(output) + ")");
            }

            // @qa: os scripts que trocaram $env:WINDIR/$env:SystemRoot por [Environment]::SystemDirectory/$PSHOME continuam
            // sendo PowerShell válido (o parser do próprio PowerShell, sem executar nada).
            var parsed = new (string Name, string Text)[]
            {
                ("restart_device", WindowsTools.DeviceScript("restart_device", "USB\\VID_1&PID_2\\3")),
                ("enable_camera", WindowsTools.DeviceScript("enable_camera", "USB\\VID_1&PID_2\\3")),
                ("erros recentes do System", WindowsTools.SystemErrorEventsScript),
                ("coleta de eventos", WindowsEventCollector.ImportDiagnostics + "'ok'"),
            };
            foreach (var (name, text) in parsed)
            {
                var target = Path.Combine(root, "parse-" + Guid.NewGuid().ToString("N") + ".ps1");
                File.WriteAllText(target, text, new UTF8Encoding(true));
                var parse = "$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile('" + target.Replace("'", "''") + "',[ref]$null,[ref]$e); 'RESULTADO:ERROS=' + $e.Count";
                var output = await RunFile(shell, root, modules, parse);
                check(output.Contains("RESULTADO:ERROS=0", StringComparison.Ordinal), "#131 (@qa): " + name + " é PowerShell válido depois da troca do caminho (" + Last(output) + ")");
            }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Last(string output) => output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.Contains("RESULTADO:"))?.Trim() ?? "sem resultado";

    // Por arquivo temporário (-File), como o harness da #127: o comando elevado inteiro não cabe na linha de comando no CI.
    private static async Task<string> RunFile(string shell, string root, string modules, string script)
    {
        var file = Path.Combine(root, "probe-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            File.WriteAllText(file, script, new UTF8Encoding(true));
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }) start.ArgumentList.Add(argument);
            var current = Environment.GetEnvironmentVariable("PSModulePath");
            // O diretório plantado vem primeiro, como Documentos\WindowsPowerShell\Modules no Windows, e nunca por último:
            // uma fixação que só acrescente ao caminho existente não pode "acertar" por corromper a última entrada.
            var others = Path.Combine(root, "outros-modulos");
            start.Environment["PSModulePath"] = modules + Path.PathSeparator + others + (string.IsNullOrEmpty(current) ? "" : Path.PathSeparator + current);
            using var process = Process.Start(start) ?? throw new IOException("PowerShell não iniciou.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await process.WaitForExitAsync(timeout.Token);
            return await stdout + "\n" + await stderr;
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
        }
    }
}
