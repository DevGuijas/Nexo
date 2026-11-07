using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Nexo.Support;

public static class PendingOperations
{
    private static readonly ConcurrentDictionary<string, byte> Active = new(StringComparer.Ordinal);

    public static string Folder(string root, string operationId)
    {
        ValidateId(operationId);
        return Path.Combine(root, "operations", operationId);
    }

    private static void ValidateId(string operationId)
    {
        if (string.IsNullOrEmpty(operationId) || operationId.Length > 64 || operationId.Any(c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("Identificador de operação inválido.", nameof(operationId));
    }

    public static bool IsWaiting(InterventionRecord intervention)
        => intervention.State is "unknown" or "executing" && !string.IsNullOrEmpty(intervention.PendingOperation);

    public static void Register(OperationalStore operations, InterventionRecord intervention, string operationId, DateTimeOffset now)
    {
        ValidateId(operationId);
        intervention.PendingOperation = operationId;
        intervention.State = "executing";
        intervention.Summary = "Operação elevada preparada; aguardando resultado verificável.";
        if (intervention.Transitions.LastOrDefault()?.State != "executing")
            intervention.Transitions.Add(new InterventionTransition(now, "executing", intervention.Summary));
        operations.SaveIntervention(intervention);
        Active.TryAdd(operationId, 0);
    }

    public static void Release(string operationId) => Active.TryRemove(operationId, out _);

    public static int Reconcile(string root, OperationalStore operations, DateTimeOffset now)
    {
        var resolved = 0;
        foreach (var intervention in operations.PendingInterventions().Where(IsWaiting))
        {
            if (Active.ContainsKey(intervention.PendingOperation)) continue;
            string folder;
            try { folder = Folder(root, intervention.PendingOperation); }
            catch (ArgumentException) { continue; }
            var output = ReadLateResult(folder);
            if (output == null) continue;

            var state = WindowsTools.ResultState(output);
            if (state == "unknown") continue;

            intervention.State = state;
            intervention.After = output;
            intervention.Summary = WindowsTools.InterventionSummary(state, pending: false);
            intervention.PendingOperation = "";
            intervention.Transitions.Add(new InterventionTransition(now, state, "Resultado recebido ao reconciliar a operação pendente."));
            operations.SaveIntervention(intervention);
            TryDelete(folder);
            resolved++;
        }
        return resolved;
    }

    private static string? ReadLateResult(string folder)
    {
        var resultFile = Path.Combine(folder, "result.json");
        if (!File.Exists(resultFile)) return null;
        try
        {
            return WindowsTools.VerifyDriverInstallationResult(File.ReadAllText(resultFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void DeleteResult(string root, string operationId) => TryDelete(Folder(root, operationId));
}
