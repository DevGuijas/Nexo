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
    }
}
