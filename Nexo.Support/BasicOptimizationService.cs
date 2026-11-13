using System.IO;
using System.Text.Json;
namespace Nexo.Support;

public sealed record OptimizationProgress(string Stage, string Message, int RemovedFiles = 0, long RemovedBytes = 0, int SkippedFiles = 0);
public sealed record OptimizationAudit(DateTimeOffset At, string Action, string Target, string Outcome);
public sealed record BasicOptimizationResult(string Status, long RemovedBytes, int RemovedFiles, int SkippedFiles, string DiskStatus);

/// <summary>Explicit one-click maintenance. Does not depend on the assistant and never selects user data folders.</summary>
public sealed class BasicOptimizationService
{
    private readonly string[] roots;
    private readonly Action<OptimizationAudit> audit;
    private readonly Func<string, CancellationToken, Task<string>>? disk;
    private static int running;

    public BasicOptimizationService(Action<OptimizationAudit> audit, Func<string, CancellationToken, Task<string>>? elevatedDiskExecutor = null)
        : this(DefaultRoots(), audit, elevatedDiskExecutor) { }

    internal BasicOptimizationService(IEnumerable<string> roots, Action<OptimizationAudit> audit, Func<string, CancellationToken, Task<string>>? disk = null)
    {
        this.roots = roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.disk = disk;
    }

    private static string[] DefaultRoots() =>
    [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")];

    public async Task<BasicOptimizationResult> RunAsync(IProgress<OptimizationProgress>? progress, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Já existe uma otimização em execução.");
        long bytes = 0; int removed = 0, skipped = 0; bool unavailable = false;
        var diskStatus = "Skipped";
        var status = "Completed";
        var diskStarted = false;
        var progressClock = System.Diagnostics.Stopwatch.StartNew();
        void Log(string action, string target, string outcome)
        {
            try { audit(new(DateTimeOffset.UtcNow, action, target, outcome)); }
            catch (Exception ex) { throw new AuditPersistenceException(ex); }
        }
        try
        {
            Log("Optimization", "", "Started");
            token.ThrowIfCancellationRequested();
            progress?.Report(new("Cleanup", "Limpando arquivos temporários com mais de sete dias."));
            await Task.Run(() =>
            {
                foreach (var root in roots)
                {
                    token.ThrowIfCancellationRequested();
                    SafeTemporaryFiles.Clean(root, DateTime.UtcNow.AddDays(-7), (path, outcome, length) =>
                    {
                        if (outcome == "Completed") { removed++; bytes += length; }
                        else if (outcome != "Deleting") { skipped++; unavailable |= outcome is "Unavailable" or "DepthLimit"; }
                        Log("DeleteFile", path, outcome);
                        if (progressClock.ElapsedMilliseconds >= 200)
                        {
                            progress?.Report(new("Cleanup", "Limpando arquivos temporários antigos.", removed, bytes, skipped));
                            progressClock.Restart();
                        }
                    }, token);
                }
            }, token);
            token.ThrowIfCancellationRequested();
            if (disk != null)
            {
                progress?.Report(new("Disk", "Executando a manutenção nativa nos volumes elegíveis."));
                Log("OptimizeVolumes", "", "Started");
                // The executor must await native completion; a canceled/lost process returns Unknown, never Completed.
                diskStarted = true;
                try { diskStatus = ParseDiskResult(await disk(DiskScript, token)); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { diskStatus = "Unknown"; }
                Log("OptimizeVolumes", "", diskStatus);
            }
            else Log("OptimizeVolumes", "", "Skipped");
            status = unavailable || diskStatus is "Failed" or "Unknown" ? "Partial" : "Completed";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { status = "Canceled"; if (diskStarted) diskStatus = "Unknown"; }
        finally { Interlocked.Exchange(ref running, 0); }
        Log("Optimization", "", status);
        progress?.Report(new(status, $"{removed} arquivo(s) removido(s); {bytes} bytes em arquivos removidos.", removed, bytes, skipped));
        return new(status, bytes, removed, skipped, diskStatus);
    }

    private sealed class AuditPersistenceException(Exception inner) : Exception("Não foi possível persistir a auditoria da otimização; execução interrompida.", inner);

    internal static string ParseDiskResult(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) return "Failed";
            var data = root.GetProperty("data");
            if (data.ValueKind == JsonValueKind.String) { using var nested = JsonDocument.Parse(data.GetString()!); return DiskState(nested.RootElement); }
            return DiskState(data);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return "Unknown"; }
    }

    private static string DiskState(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) return "Unknown";
        var outcomes = data.EnumerateArray().Select(v => v.GetProperty("Status").GetString()).ToArray();
        if (outcomes.Length == 0) return "Skipped";
        if (outcomes.Any(s => s is not ("Completed" or "Skipped" or "Failed"))) return "Unknown";
        return outcomes.Contains("Failed") ? "Failed" : outcomes.Contains("Completed") ? "Completed" : "Skipped";
    }

    // Fixed script: no user input, no forced defrag, no trim override, no chkdsk, no update/cache/recycle cleanup.
    public const string DiskScript = """
        $ErrorActionPreference='Stop'
        $results=@()
        foreach($volume in @(Get-Volume -ErrorAction Stop)) {
            if(-not $volume.DriveLetter -or $volume.DriveType -ne 'Fixed' -or $volume.HealthStatus -ne 'Healthy' -or
               @($volume.OperationalStatus) -notcontains 'OK' -or $volume.FileSystemType -notin @('NTFS','ReFS')) { continue }
            $letter=[char]$volume.DriveLetter
            if($letter -notmatch '^[A-Za-z]$'){continue}
            try {
                $disks=@(Get-Partition -DriveLetter $letter -ErrorAction Stop | Get-Disk -ErrorAction Stop)
                if($disks.Count -ne 1 -or $disks[0].HealthStatus -ne 'Healthy' -or $disks[0].IsOffline -or $disks[0].IsReadOnly -or
                   @($disks[0].OperationalStatus) -notcontains 'Online') {
                    $results += [pscustomobject]@{Drive=[string]$letter;Status='Skipped'}; continue
                }
                Optimize-Volume -DriveLetter $letter -ErrorAction Stop
                $results += [pscustomobject]@{Drive=[string]$letter;Status='Completed'}
            } catch { $results += [pscustomobject]@{Drive=[string]$letter;Status='Failed'} }
        }
        ConvertTo-Json -InputObject @($results) -Compress
        """;
}
