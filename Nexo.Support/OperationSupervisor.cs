namespace Nexo.Support;

public enum OperationState { Completed, Failed, Unknown }

public static class OperationSupervisor
{
    /// <summary>The operation keeps running after timeout or UI cancellation. Unknown never means success.</summary>
    public static async Task<OperationState> Wait(Func<Task<int>> exitCode, TimeSpan timeout, CancellationToken token)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var work = exitCode();
        var ended = await Task.WhenAny(work, Task.Delay(timeout, token));
        if (ended != work) return OperationState.Unknown;
        try { return await work == 0 ? OperationState.Completed : OperationState.Failed; }
        catch { return OperationState.Unknown; }
    }
}
