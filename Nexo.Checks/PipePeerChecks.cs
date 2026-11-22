using System.Diagnostics;
using System.Text;
using Nexo.Support;

namespace Nexo.Checks;

// #133: o executor de rede elevado confere o PID do servidor do pipe da decisão (#11) sem compilar código. O Add-Type do
// Windows PowerShell 5.1 compila pelo csc.exe e grava o .cs e a .dll no TEMP do processo elevado, que vem de
// HKCU\Environment e o usuário grava sem elevação: dava para trocar a .dll antes de o PowerShell carregá-la com admin.
// O P/Invoke agora é definido por System.Reflection.Emit num assembly dinâmico só em memória (AssemblyBuilderAccess.Run).
// A parte estrutural roda em qualquer sistema. A parte com PowerShell cria o tipo de verdade e confere a assinatura: no
// Windows usa o powershell.exe (como no CI); fora dele, só com NEXO_CHECKS_PWSH. A chamada real ao kernel32 e o pipe
// do Nexo ficam no NetworkDecisionChecks, que só roda no Windows.
internal static class PipePeerChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var transaction = Transaction();
        Structure(transaction, check);
        await Behavior(transaction, check);
    }

    internal static string Transaction()
    {
        using var resource = typeof(NetworkChange).Assembly.GetManifestResourceStream("Nexo.NetworkTransaction.ps1")!;
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }

    private static void Structure(string transaction, Action<bool, string> check)
    {
        string[] compilers = ["Add-Type", "TypeDefinition", "CompilerParameters", "CSharpCodeProvider", "CodeDom", "csc.exe", "CompileAssemblyFrom"];
        var found = compilers.Where(word => transaction.Contains(word, StringComparison.OrdinalIgnoreCase)).ToList();
        check(found.Count == 0,
            "#133: o executor de rede elevado não compila código (sem Add-Type nem CodeDom/csc.exe, que gravam .cs e .dll no TEMP do usuário)" + (found.Count == 0 ? "" : ": " + string.Join(", ", found)));

        check(transaction.Contains("[System.Reflection.Emit.AssemblyBuilderAccess]::Run)", StringComparison.Ordinal)
            && !transaction.Contains("RunAndSave", StringComparison.OrdinalIgnoreCase) && !transaction.Contains("AssemblyBuilderAccess]::Save", StringComparison.OrdinalIgnoreCase)
            && !transaction.Contains(".Save(", StringComparison.Ordinal),
            "#133: o P/Invoke vem de um assembly dinâmico só em memória (AssemblyBuilderAccess.Run, sem RunAndSave/Save)");

        check(transaction.Contains("DefinePInvokeMethod('GetNamedPipeServerProcessId', 'kernel32.dll'", StringComparison.Ordinal),
            "#133: a conferência continua pelo GetNamedPipeServerProcessId do kernel32 (o PID do servidor vem do kernel, #11)");

        // O NetworkDecisionChecks extrai só a função Receive-Decision do script: a criação do tipo precisa estar dentro dela.
        var receive = transaction.IndexOf("function Receive-Decision {", StringComparison.Ordinal);
        var peer = transaction.IndexOf("function New-NexoPipePeer {", StringComparison.Ordinal);
        var loop = transaction.IndexOf("$decisionPath = Join-Path $operationDir 'decision.txt'", StringComparison.Ordinal);
        check(receive >= 0 && peer > receive && peer < loop,
            "#133: a criação do tipo fica dentro da Receive-Decision, antes da leitura do pipe");

        var guard = transaction.IndexOf("if ([int]$appProcessId -le 0 -or [string]$decisionPipeName -cnotmatch '^nexo-network-[0-9a-f]{32}$') { return 'rollback' }", StringComparison.Ordinal);
        var create = transaction.IndexOf("try { $peer = New-NexoPipePeer } catch { return 'rollback' }", StringComparison.Ordinal);
        check(guard > receive && create > guard && create < loop,
            "#133: sem PID do Nexo ou com nome de pipe fora do padrão a decisão é rollback, e uma falha ao criar o tipo também vira rollback (falha fechada)");

        check(transaction.Contains("$peer::GetNamedPipeServerProcessId($pipe.SafePipeHandle.DangerousGetHandle(), [ref]$serverPid)", StringComparison.Ordinal)
            && transaction.Contains("$serverPid -ne [uint32]$appProcessId) { continue }", StringComparison.Ordinal),
            "#133: o PID do servidor do pipe ainda precisa ser o do Nexo; outro processo não recebe a leitura");

        check(NetworkRecovery.CommandLength(transaction) <= WindowsTools.CommandLineLimit,
            $"#133: o executor de rede compactado continua dentro do limite da linha de comando ({NetworkRecovery.CommandLength(transaction)} de {WindowsTools.CommandLineLimit})");
    }

    private static string? Shell()
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var configured = Environment.GetEnvironmentVariable("NEXO_CHECKS_PWSH");
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private static async Task Behavior(string transaction, Action<bool, string> check)
    {
        var shell = Shell();
        if (shell == null)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                check(false, "#133: o CI precisa de um PowerShell para criar o tipo do pipe");
            Console.WriteLine("IGNORADO: #133: tipo do pipe por Reflection.Emit (sem powershell.exe e sem NEXO_CHECKS_PWSH)");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "NexoPipePeer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "NetworkTransaction.ps1");
            await File.WriteAllTextAsync(source, transaction, new UTF8Encoding(true));
            // TEMP e TMP apontam para uma pasta vazia: se algo compilasse ou extraísse arquivo, apareceria nela.
            var temp = Path.Combine(root, "temp");
            Directory.CreateDirectory(temp);
            var wrapper = Path.Combine(root, "peer.ps1");
            await File.WriteAllTextAsync(wrapper, """
                param([string]$source, [string]$temp)
                $ErrorActionPreference='Stop'
                $env:TEMP=$temp; $env:TMP=$temp
                $tokens=$null; $errors=$null
                $ast=[System.Management.Automation.Language.Parser]::ParseInput((Get-Content -LiteralPath $source -Raw),[ref]$tokens,[ref]$errors)
                if($errors.Count){throw 'O script de rede não passou pelo parser.'}
                $function=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-NexoPipePeer'},$true)
                if($null -eq $function){throw 'Criação do tipo do pipe ausente.'}
                . ([scriptblock]::Create($function.Extent.Text))
                $type=New-NexoPipePeer
                $again=New-NexoPipePeer
                $method=$type.GetMethod('GetNamedPipeServerProcessId')
                $parameters=@($method.GetParameters())
                $import=$method.Attributes -band [System.Reflection.MethodAttributes]::PinvokeImpl
                $files=@(Get-ChildItem -LiteralPath $temp -Force -Recurse -ErrorAction SilentlyContinue).Count
                'DINAMICO:' + $type.Assembly.IsDynamic
                'LOCAL:' + ([string]::IsNullOrEmpty($type.Assembly.Location))
                'ESTATICO:' + $method.IsStatic
                'PINVOKE:' + ($import -ne 0)
                'RETORNO:' + $method.ReturnType.FullName
                'PARAMETROS:' + (($parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',')
                'SAIDA:' + $parameters[1].IsOut
                'REUSO:' + [object]::ReferenceEquals($type, $again)
                'ARQUIVOS:' + $files
                """, new UTF8Encoding(true));

            var output = await Run(shell, wrapper, source, temp);
            bool Has(string line) => output.Split('\n').Any(l => l.Trim() == line);
            check(Has("DINAMICO:True") && Has("LOCAL:True"),
                "#133: o tipo do pipe nasce num assembly dinâmico sem arquivo em disco (" + Summary(output) + ")");
            check(Has("ESTATICO:True") && Has("PINVOKE:True") && Has("RETORNO:System.Boolean")
                && Has("PARAMETROS:System.IntPtr,System.UInt32&") && Has("SAIDA:True"),
                "#133: GetNamedPipeServerProcessId tem a mesma assinatura do DllImport antigo: bool (IntPtr, out uint)");
            check(Has("REUSO:True"),
                "#133: uma segunda chamada reaproveita o tipo, sem criar outro assembly a cada tentativa de leitura do pipe");
            check(Has("ARQUIVOS:0"),
                "#133: criar o tipo não grava nada no TEMP do processo (nem .cs, nem .dll)");

            // A Receive-Decision real (extraída como no NetworkDecisionChecks), sem servidor do outro lado do pipe.
            var decide = Path.Combine(root, "decide.ps1");
            await File.WriteAllTextAsync(decide, """
                param([string]$source, [string]$temp, [string]$pipeName, [int]$expectedPid, [string]$breakType)
                $ErrorActionPreference='Stop'
                $env:TEMP=$temp; $env:TMP=$temp
                $tokens=$null; $errors=$null
                $ast=[System.Management.Automation.Language.Parser]::ParseInput((Get-Content -LiteralPath $source -Raw),[ref]$tokens,[ref]$errors)
                $function=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Receive-Decision'},$true)
                $text=$function.Extent.Text.Replace('.AddSeconds(60)', '.AddSeconds(1)')
                if($breakType -eq '1'){ $text=$text.Replace('[System.Reflection.Emit.AssemblyBuilderAccess]::Run)', '[System.Reflection.Emit.AssemblyBuilderAccess]::Inexistente)') }
                . ([scriptblock]::Create($text))
                $decisionPipeName=$pipeName; $appProcessId=$expectedPid; $operationDir=$temp
                'DECISAO:' + (Receive-Decision)
                """, new UTF8Encoding(true));
            var validName = "nexo-network-" + Guid.NewGuid().ToString("N");
            var noServer = await Run(shell, decide, source, temp, validName, Environment.ProcessId.ToString(), "0");
            var noPid = await Run(shell, decide, source, temp, validName, "0", "0");
            var badName = await Run(shell, decide, source, temp, "outro-pipe", Environment.ProcessId.ToString(), "0");
            var broken = await Run(shell, decide, source, temp, validName, Environment.ProcessId.ToString(), "1");
            check(noServer.Contains("DECISAO:rollback", StringComparison.Ordinal) && noPid.Contains("DECISAO:rollback", StringComparison.Ordinal)
                && badName.Contains("DECISAO:rollback", StringComparison.Ordinal),
                "#133: a Receive-Decision real devolve rollback sem servidor no pipe, sem PID do Nexo e com nome de pipe fora do padrão");
            check(broken.Contains("DECISAO:rollback", StringComparison.Ordinal),
                "#133: se o tipo do pipe não puder ser criado, a Receive-Decision devolve rollback em vez de lançar ou manter a mudança");

            // (@qa) Com um servidor de pipe conectado que nunca confirma, a decisão é rollback. No Windows, a chamada ao
            // kernel32 confere o PID (este processo) e a leitura expira sem linha; fora dele, a chamada ao kernel32 lança
            // e cai no catch genérico. Os dois caminhos de falha depois da conexão precisam devolver rollback, nunca keep.
            var connectedName = "nexo-network-" + Guid.NewGuid().ToString("N");
            using (var server = new System.IO.Pipes.NamedPipeServerStream(connectedName, System.IO.Pipes.PipeDirection.Out, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous))
            {
                var waiting = server.WaitForConnectionAsync();
                var silent = await Run(shell, decide, source, temp, connectedName, Environment.ProcessId.ToString(), "0");
                check(waiting.IsCompleted && silent.Contains("DECISAO:rollback", StringComparison.Ordinal),
                    "#133 (@qa): conectado ao servidor do pipe sem confirmação (leitura expirada ou falha na conferência do PID), a decisão é rollback");
            }

            // (@qa) Só a linha exata "keep" mantém a mudança. No Windows, o servidor (este processo, PID conferido) manda
            // "KEEP": a leitura acontece e precisa devolver rollback; fora dele, a conferência do PID lança e também é rollback.
            var otherLineName = "nexo-network-" + Guid.NewGuid().ToString("N");
            using (var server = new System.IO.Pipes.NamedPipeServerStream(otherLineName, System.IO.Pipes.PipeDirection.Out, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous))
            {
                var writing = Task.Run(async () =>
                {
                    try
                    {
                        await server.WaitForConnectionAsync();
                        var line = new UTF8Encoding(false).GetBytes("KEEP\n");
                        await server.WriteAsync(line);
                        await server.FlushAsync();
                    }
                    catch (IOException) { } // o cliente pode fechar antes (fora do Windows a conferência do PID falha)
                    catch (ObjectDisposedException) { }
                });
                var other = await Run(shell, decide, source, temp, otherLineName, Environment.ProcessId.ToString(), "0");
                await Task.WhenAny(writing, Task.Delay(2000));
                check(other.Contains("DECISAO:rollback", StringComparison.Ordinal),
                    "#133 (@qa): uma linha diferente de \"keep\" exato (\"KEEP\") vinda do servidor do Nexo não mantém a mudança");
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static string Summary(string output) => string.Join(" ", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3));

    private static async Task<string> Run(string shell, string wrapper, params string[] arguments)
    {
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", wrapper }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        var output = await stdout;
        if (process.ExitCode != 0) throw new Exception("#133: PowerShell do tipo do pipe falhou: " + (await stderr).Trim());
        return output;
    }
}
