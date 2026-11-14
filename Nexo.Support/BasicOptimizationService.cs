using System.IO;
using System.Text.Json;
namespace Nexo.Support;

public sealed record OptimizationProgress(string Stage, string Message, int RemovedFiles = 0, long RemovedBytes = 0, int SkippedFiles = 0);
public sealed record OptimizationStageResult(string Stage, string Status, DateTimeOffset? StartedAt, TimeSpan Duration, int? CompletedItems, int? SkippedItems, long? LogicalBytesRemoved, int ErrorCount, IReadOnlyList<string> Errors);
public sealed record OptimizationAudit(DateTimeOffset At, string Action, string Target, string Outcome) { public OptimizationStageResult? Stage { get; init; } }
public sealed record BasicOptimizationResult(string Status, long RemovedBytes, int RemovedFiles, int SkippedFiles, string DiskStatus) { public IReadOnlyList<OptimizationStageResult> Stages { get; init; } = []; }

/// <summary>Explicit one-click maintenance. Does not depend on the assistant and never selects user data folders.</summary>
public sealed class BasicOptimizationService
{
    private readonly string[] roots;
    private readonly Action<OptimizationAudit> audit;
    private readonly Func<string, CancellationToken, Task<string>>? disk;
    private static int running;
    private readonly TimeProvider time;

    public BasicOptimizationService(Action<OptimizationAudit> audit, Func<string, CancellationToken, Task<string>>? elevatedDiskExecutor = null)
        : this(DefaultRoots(), audit, elevatedDiskExecutor) { }

    internal BasicOptimizationService(IEnumerable<string> roots, Action<OptimizationAudit> audit, Func<string, CancellationToken, Task<string>>? disk = null, TimeProvider? time = null)
    {
        this.roots = roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.disk = disk;
        this.time = time ?? TimeProvider.System;
    }

    private static string[] DefaultRoots() =>
    [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")];

    public async Task<BasicOptimizationResult> RunAsync(IProgress<OptimizationProgress>? progress, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Já existe uma otimização em execução.");
        long bytes = 0; int removed = 0, skipped = 0, cleanupErrors = 0;
        var errors = new List<string>();
        var status = "Completed";
        var cleanupStatus = "Skipped";
        var cleanupAt = time.GetUtcNow(); var cleanupTick = time.GetTimestamp(); var cleanupDuration = TimeSpan.Zero;
        var diskAt = cleanupAt; var diskTick = cleanupTick; var diskDuration = TimeSpan.Zero;
        var diskSummary = new DiskSummary("Skipped", 0, 0, []);
        var cleanupStarted = false; var cleanupFinished = false; var diskStarted = false; var auditBroken = false;
        var stages = new List<OptimizationStageResult>();
        var progressClock = System.Diagnostics.Stopwatch.StartNew();
        void Log(string action, string target, string outcome, OptimizationStageResult? stage = null)
        {
            try { audit(new(time.GetUtcNow(), action, target, outcome) { Stage = stage }); }
            catch (Exception ex) { auditBroken = true; throw new AuditPersistenceException(ex); }
        }
        void LogCleanup()
        {
            if (stages.Any(s => s.Stage == "Cleanup")) return;
            var stage = new OptimizationStageResult("Cleanup", cleanupStatus, cleanupAt, cleanupDuration, removed, skipped - cleanupErrors, bytes, cleanupErrors, errors.ToArray());
            Log("Stage", "Cleanup", stage.Status, stage); stages.Add(stage);
        }
        void LogDisk()
        {
            if (stages.Any(s => s.Stage == "Disk")) return;
            var stage = new OptimizationStageResult("Disk", diskSummary.Status, diskStarted ? diskAt : null, diskDuration, diskSummary.Completed, diskSummary.Skipped, null, diskSummary.Errors.Count, diskSummary.Errors);
            Log("Stage", "Disk", stage.Status, stage); stages.Add(stage);
        }
        try
        {
            Log("Optimization", "", "Started");
            cleanupAt = time.GetUtcNow(); cleanupTick = time.GetTimestamp(); cleanupStarted = true;
            token.ThrowIfCancellationRequested();
            progress?.Report(new("Cleanup", "Limpando arquivos temporários com mais de sete dias."));
            try
            {
                await Task.Run(() =>
                {
                    foreach (var root in roots)
                    {
                        token.ThrowIfCancellationRequested();
                        SafeTemporaryFiles.Clean(root, time.GetUtcNow().UtcDateTime.AddDays(-7), (path, outcome, length) =>
                        {
                            if (outcome == "Completed") { removed++; bytes += length; }
                            else if (outcome != "Deleting")
                            {
                                skipped++;
                                if (outcome is "Unavailable" or "DepthLimit")
                                {
                                    cleanupErrors++;
                                    if (errors.Count < 100) errors.Add(path + ": " + outcome);
                                }
                            }
                            Log("DeleteFile", path, outcome);
                            if (progressClock.ElapsedMilliseconds >= 200)
                            {
                                progress?.Report(new("Cleanup", "Limpando arquivos temporários antigos.", removed, bytes, skipped));
                                progressClock.Restart();
                            }
                        }, token);
                    }
                }, token);
                cleanupStatus = cleanupErrors > 0 ? "Partial" : "Completed";
                cleanupFinished = true;
            }
            finally { cleanupDuration = time.GetElapsedTime(cleanupTick); }
            LogCleanup();
            token.ThrowIfCancellationRequested();
            if (disk != null)
            {
                progress?.Report(new("Disk", "Executando a manutenção nativa nos volumes elegíveis."));
                Log("OptimizeVolumes", "", "Started");
                diskAt = time.GetUtcNow(); diskTick = time.GetTimestamp(); diskStarted = true;
                try { diskSummary = ParseDiskSummary(await disk(DiskScript, token)); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { diskSummary = new("Denied", null, null, [ex.Message]); }
                catch (UnauthorizedAccessException ex) { diskSummary = new("Denied", null, null, [ex.Message]); }
                catch (Exception ex) { diskSummary = new("Unknown", null, null, [ex.Message]); }
                finally { diskDuration = time.GetElapsedTime(diskTick); }
                Log("OptimizeVolumes", "", diskSummary.Status);
            }
            status = cleanupErrors > 0 || diskSummary.Status is "Failed" or "Unknown" or "Denied" ? "Partial" : "Completed";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            status = "Canceled";
            if (!cleanupFinished) cleanupStatus = "Canceled";
            if (diskStarted) diskSummary = new("Unknown", null, null, ["Operação nativa cancelada; conclusão não comprovada."]);
        }
        finally
        {
            try
            {
                if (!auditBroken)
                {
                    if (cleanupStarted && !cleanupFinished) cleanupDuration = time.GetElapsedTime(cleanupTick);
                    LogCleanup(); LogDisk();
                }
            }
            finally { Interlocked.Exchange(ref running, 0); }
        }
        Log("Optimization", "", status);
        progress?.Report(new(status, $"{removed} arquivo(s) removido(s); {bytes} bytes lógicos em arquivos removidos.", removed, bytes, skipped));
        return new(status, bytes, removed, skipped, diskSummary.Status) { Stages = stages.ToArray() };
    }

    private sealed class AuditPersistenceException(Exception inner) : Exception("Não foi possível persistir a auditoria da otimização; execução interrompida.", inner);
    private sealed record DiskSummary(string Status, int? Completed, int? Skipped, IReadOnlyList<string> Errors);

    internal static string ParseDiskResult(string output) => ParseDiskSummary(output).Status;

    private static DiskSummary ParseDiskSummary(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            {
                var denied = root.TryGetProperty("denied", out var denial) && denial.ValueKind == JsonValueKind.True;
                var reason = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString()! : "Execução nativa não autorizada ou falhou.";
                return new(denied ? "Denied" : "Failed", null, null, [reason]);
            }
            var data = root.GetProperty("data");
            if (data.ValueKind == JsonValueKind.String) { using var nested = JsonDocument.Parse(data.GetString()!); return DiskState(nested.RootElement); }
            return DiskState(data);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return new("Unknown", null, null, ["Saída nativa sem evidência válida de conclusão."]); }
    }

    private static DiskSummary DiskState(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) return new("Unknown", null, null, ["Lista de resultados nativos ausente."]);
        int completed = 0, skipped = 0; var errors = new List<string>();
        foreach (var volume in data.EnumerateArray())
        {
            switch (volume.GetProperty("Status").GetString())
            {
                case "Completed": completed++; break;
                case "Skipped": skipped++; break;
                case "Failed":
                    var drive = volume.TryGetProperty("Drive", out var letter) ? letter.GetString() : "Volume";
                    var reason = volume.TryGetProperty("Error", out var error) ? error.GetString() : "Falha na operação nativa.";
                    errors.Add(drive + ": " + reason); break;
                default: return new("Unknown", null, null, ["Estado de volume não reconhecido."]);
            }
        }
        return new(errors.Count > 0 ? "Failed" : completed > 0 ? "Completed" : "Skipped", completed, skipped, errors.ToArray());
    }

    // Fixed script: no user input, no forced defrag, no trim override, no chkdsk, no update/cache/recycle cleanup.
    public const string DiskScript = """
        $ErrorActionPreference='Stop'
        $results=@()
        foreach($volume in @(Get-Volume -ErrorAction Stop)) {
            if(-not $volume.DriveLetter -or $volume.DriveType -ne 'Fixed' -or $volume.HealthStatus -ne 'Healthy' -or
               @($volume.OperationalStatus) -notcontains 'OK' -or $volume.FileSystemType -notin @('NTFS','ReFS')) {
                $results += [pscustomobject]@{Drive=[string]$volume.DriveLetter;Status='Skipped'}; continue
            }
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
            } catch { $results += [pscustomobject]@{Drive=[string]$letter;Status='Failed';Error=$_.Exception.Message} }
        }
        ConvertTo-Json -InputObject @($results) -Compress
        """;
}
