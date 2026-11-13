using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Nexo.Support;

internal static class NetworkDecisionChannel
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);

    internal static string PipeName(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new ArgumentException("Identificador de operação inválido.", nameof(operationId));
        return "nexo-network-" + operationId;
    }

    internal static async Task<bool> SendAsync(string pipeName, int expectedClientPid, string decision, TimeSpan timeout, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || expectedClientPid <= 0 || decision is not ("keep" or "rollback") || timeout <= TimeSpan.Zero) return false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        while (!limit.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(limit.Token);
                if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var clientPid)
                    || clientPid != (uint)expectedClientPid) continue;
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(decision.AsMemory(), limit.Token);
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        return false;
    }
}
