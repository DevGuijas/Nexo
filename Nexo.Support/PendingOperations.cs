using System.IO;
using System.Text.Json;

namespace Nexo.Support;

public static class PendingOperations
{
    private const int InterventionsToCheck = 50;

    public static string Folder(string root, string operationId) => Path.Combine(root, "operations", operationId);

    public static bool IsWaiting(InterventionRecord intervention)
        => intervention.State == "unknown" && intervention.PendingOperation != "";

    public static int Reconcile(string root, OperationalStore operations, DateTimeOffset now)
    {
        var resolved = 0;
        foreach (var intervention in operations.RecentInterventions(InterventionsToCheck).Where(IsWaiting))
        {
            var folder = Folder(root, intervention.PendingOperation);
            var output = ReadLateResult(folder);
            if (output == null) continue;

            var state = WindowsTools.ResultState(output);
            if (state == "unknown") continue;

            intervention.State = state;
            intervention.After = output;
            intervention.Summary = WindowsTools.InterventionSummary(state, pending: false);
            intervention.PendingOperation = "";
            intervention.Transitions.Add(new InterventionTransition(now, state, "Resultado recebido depois do prazo de espera do Nexo."));
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
}
