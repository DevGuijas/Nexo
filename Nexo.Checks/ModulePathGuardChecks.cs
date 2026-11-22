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
