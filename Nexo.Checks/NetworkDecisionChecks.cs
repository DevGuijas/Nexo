using System.Diagnostics;
using System.IO.Pipes;
using Nexo.Support;

namespace Nexo.Checks;

internal static class NetworkDecisionChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var name = NetworkDecisionChannel.PipeName(Guid.NewGuid().ToString("N"));
        var sending = NetworkDecisionChannel.SendAsync(name, Environment.ProcessId, "keep", TimeSpan.FromSeconds(3), CancellationToken.None);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        using var reader = new StreamReader(client);
        var decision = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        check(await sending && decision == "keep", "Rede: somente o executor identificado recebe a confirmação pelo pipe");

        var foreignName = NetworkDecisionChannel.PipeName(Guid.NewGuid().ToString("N"));
        var rejecting = NetworkDecisionChannel.SendAsync(foreignName, Environment.ProcessId + 1, "keep", TimeSpan.FromMilliseconds(400), CancellationToken.None);
        using var foreign = new NamedPipeClientStream(".", foreignName, PipeDirection.In, PipeOptions.Asynchronous);
        await foreign.ConnectAsync(1000);
        using var foreignReader = new StreamReader(foreign);
        var leaked = await foreignReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1));
        check(!await rejecting && leaked == null, "Rede: conexão de outro PID não recebe keep");

        var absent = await NetworkDecisionChannel.SendAsync(NetworkDecisionChannel.PipeName(Guid.NewGuid().ToString("N")), 0, "keep", TimeSpan.FromSeconds(1), CancellationToken.None);
        check(!absent, "Rede: PID ausente impede confirmar a mudança");

        var secondClient = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        var repeated = false;
        try { await secondClient.ConnectAsync(150); repeated = true; }
        catch (TimeoutException) { }
        finally { secondClient.Dispose(); }
        check(!repeated, "Rede: confirmação autenticada é consumida uma vez");

        await CheckPowerShellPeer(check);
    }

    private static async Task CheckPowerShellPeer(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "NexoPipeChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "NetworkTransaction.ps1");
            using (var resource = typeof(NetworkChange).Assembly.GetManifestResourceStream("Nexo.NetworkTransaction.ps1")!)
            using (var file = File.Create(source)) await resource.CopyToAsync(file);
            var wrapper = Path.Combine(root, "read-decision.ps1");
            await File.WriteAllTextAsync(wrapper, """
                param([string]$source, [string]$pipeName, [int]$expectedPid, [string]$operationDir)
                $ErrorActionPreference='Stop'
                $text=Get-Content -LiteralPath $source -Raw
                $tokens=$null; $errors=$null
                $ast=[System.Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
                if($errors.Count){throw 'O script de rede não passou pelo parser.'}
                $function=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Receive-Decision'},$true)
                if($null -eq $function){throw 'Função de confirmação ausente.'}
                . ([scriptblock]::Create($function.Extent.Text.Replace('.AddSeconds(60)', '.AddSeconds(2)')))
                $decisionPipeName=$pipeName; $appProcessId=$expectedPid
                Receive-Decision
                """);

            var validName = NetworkDecisionChannel.PipeName(Guid.NewGuid().ToString("N"));
            using (var valid = StartReader(wrapper, source, validName, Environment.ProcessId, root))
            {
                await Task.Delay(600); // O executor costuma chegar à confirmação antes da resposta do usuário.
                var sent = NetworkDecisionChannel.SendAsync(validName, valid.Id, "keep", TimeSpan.FromSeconds(5), CancellationToken.None);
                var result = await ReadResult(valid);
                check(await sent && result == "keep", "Rede: PowerShell 5.1 aceita keep só do processo do Nexo");
            }

            var forgedName = NetworkDecisionChannel.PipeName(Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(Path.Combine(root, "decision.txt"), "keep");
            using (var forged = StartReader(wrapper, source, forgedName, Environment.ProcessId, root))
                check(await ReadResult(forged) == "rollback", "Rede: PowerShell 5.1 ignora keep forjado no arquivo do usuário");

            var wrongServerName = NetworkDecisionChannel.PipeName(Guid.NewGuid().ToString("N"));
            using (var wrongServer = StartReader(wrapper, source, wrongServerName, Environment.ProcessId + 1, root))
            {
                var send = NetworkDecisionChannel.SendAsync(wrongServerName, wrongServer.Id, "keep", TimeSpan.FromSeconds(4), CancellationToken.None);
                var result = await ReadResult(wrongServer);
                await send;
                check(result == "rollback", "Rede: PowerShell 5.1 rejeita servidor do pipe com PID diferente");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Process StartReader(string wrapper, string source, string name, int appPid, string root)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", wrapper, source, name, appPid.ToString(), root })
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task<string> ReadResult(Process process)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        var error = (await process.StandardError.ReadToEndAsync()).Trim();
        if (process.ExitCode != 0) throw new Exception("PowerShell de confirmação falhou: " + error);
        return output;
    }
}
