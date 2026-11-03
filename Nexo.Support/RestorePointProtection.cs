using System.Text.Json;

namespace Nexo.Support;

public sealed record RecoveryPointResult(bool Ready, int? SequenceNumber, string Reason);

public static class RestorePointProtection
{
    // Executed only after the user approves a specific driver installation and UAC approves elevation.
    public const string Script = """
        $before=@(Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Sort-Object SequenceNumber -Descending | Select-Object -First 1)
        $previous=if($before.Count -gt 0){[int]$before[0].SequenceNumber}else{0}
        Checkpoint-Computer -Description 'Nexo before driver installation' -RestorePointType APPLICATION_INSTALL -ErrorAction Stop
        $after=@(Get-ComputerRestorePoint -ErrorAction Stop | Sort-Object SequenceNumber -Descending | Select-Object -First 1)
        if($after.Count -eq 0 -or [int]$after[0].SequenceNumber -le $previous){throw 'O novo ponto de restauração não foi confirmado.'}
        [pscustomobject]@{SequenceNumber=[int]$after[0].SequenceNumber}
        """;

    public static async Task<RecoveryPointResult> Prepare(Func<string, CancellationToken, Task<string>> elevated, CancellationToken token)
    {
        try
        {
            using var result = JsonDocument.Parse(await elevated(Script, token));
            var root = result.RootElement;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("SequenceNumber", out var number) && number.TryGetInt32(out var sequence) && sequence > 0)
                return new RecoveryPointResult(true, sequence, "Ponto de restauração confirmado.");
            var reason = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() ?? "Erro não detalhado" : "Ponto de restauração não confirmado";
            return new RecoveryPointResult(false, null, reason);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new RecoveryPointResult(false, null, ex.Message); }
    }
}
