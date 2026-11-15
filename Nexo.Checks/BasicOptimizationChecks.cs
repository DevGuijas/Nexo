using Microsoft.Extensions.Time.Testing;
using Nexo.Support;
using System.Diagnostics;
namespace Nexo.Checks;
internal static class BasicOptimizationChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        await NativeOptimizationChecks.Run(check);
        var root = Path.Combine(Path.GetTempPath(), "NexoOptimizationChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var junction = Path.Combine(root, "temp", "linked");
        var rootJunction = Path.Combine(root, "root-link");
        void Old(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "12345"); File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-10)); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10)); }
        try
        {
            var temp = Path.Combine(root, "temp"); Directory.CreateDirectory(temp);
            var old = Path.Combine(temp, "old.tmp"); Old(old);
            var recent = Path.Combine(temp, "recent.tmp"); File.WriteAllText(recent, "recent");
            var recentCreated = Path.Combine(temp, "new-with-old-write.tmp"); Old(recentCreated); File.SetCreationTimeUtc(recentCreated, DateTime.UtcNow);
            var protectedFiles = new[] { "Documents", "Downloads", "Cache", "Chrome", "SoftwareDistribution", "$Recycle.Bin" }.Select(n => Path.Combine(temp, n, "old.tmp")).ToArray();
            foreach (var path in protectedFiles) Old(path);
            var outside = Path.Combine(root, "outside", "old.tmp"); Old(outside);
            CreateJunction(junction, Path.GetDirectoryName(outside)!);
            CreateJunction(rootJunction, Path.GetDirectoryName(outside)!);
            var locked = Path.Combine(temp, "locked.tmp"); Old(locked);
            var audit = new List<OptimizationAudit>();
            BasicOptimizationResult result;
            using (var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                result = await new BasicOptimizationService([temp, rootJunction], audit.Add).RunAsync(null, CancellationToken.None);
            check(!File.Exists(old) && result.RemovedFiles == 1 && result.RemovedBytes == 5, "Otimização: remove somente fixture antiga e conta bytes reais");
            check(File.Exists(recent) && File.Exists(recentCreated), "Otimização: conserva arquivos recentes por escrita ou criação");
            check(protectedFiles.All(File.Exists), "Otimização: preserva documentos, downloads, cache, navegador, atualizações e lixeira");
            check(File.Exists(outside) && Directory.Exists(junction) && File.Exists(locked), "Otimização: não segue junction raiz/nested e ignora arquivo bloqueado");
            check(result.Status == "Partial" && result.DiskStatus == "Skipped", "Otimização: raiz insegura impede alegar conclusão integral");
            check(audit.FindIndex(a => a.Outcome == "Deleting") < audit.FindIndex(a => a.Outcome == "Completed" && a.Action == "DeleteFile") && audit.Last().Outcome == "Partial", "Otimização: audita intenção, remoção e estado terminal");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var cancelResult = await new BasicOptimizationService([temp], audit.Add).RunAsync(null, canceled.Token);
            check(cancelResult.Status == "Canceled" && File.Exists(locked), "Otimização: cancelamento anterior não remove arquivos");
            var auditFailure = Path.Combine(root, "audit-failure", "old.tmp"); Old(auditFailure);
            bool rejected = false;
            try { await new BasicOptimizationService([Path.GetDirectoryName(auditFailure)!], a => { if(a.Outcome == "Deleting") throw new IOException("audit unavailable"); }).RunAsync(null, CancellationToken.None); }
            catch { rejected = true; }
            check(rejected && File.Exists(auditFailure), "Otimização: falha em persistir intenção interrompe antes de excluir");
            check(BasicOptimizationService.ParseDiskResult("{\"success\":true,\"data\":[{\"Drive\":\"C\",\"Status\":\"Completed\"}]}") == "Completed", "Otimização: conclusão de disco exige evidência positiva");
            check(BasicOptimizationService.ParseDiskResult("{\"success\":true,\"data\":[]}") == "Skipped" && BasicOptimizationService.ParseDiskResult("{\"success\":false}") == "Failed" && BasicOptimizationService.ParseDiskResult("bad") == "Unknown", "Otimização: distingue disco inelegível, falha e saída desconhecida");
            var protectedRoot = await new BasicOptimizationService([Path.Combine(temp, "Downloads")], audit.Add).RunAsync(null, CancellationToken.None);
            check(protectedRoot.RemovedFiles == 0 && protectedRoot.Status == "Partial" && File.Exists(Path.Combine(temp, "Downloads", "old.tmp")), "Otimização: valida exclusões também nos ancestrais da raiz");
            var deniedRoot = Path.Combine(root, "denied");
            var denied = Path.Combine(deniedRoot, "old.tmp"); Old(denied);
            var deniedAudit = new List<OptimizationAudit>();
            try
            {
                var deniedResult = await new BasicOptimizationService([deniedRoot], a =>
                {
                    deniedAudit.Add(a);
                    // Exercise a real native disposition error after the handle has been validated.
                    if (a.Outcome == "Deleting") File.SetAttributes(denied, FileAttributes.ReadOnly);
                }).RunAsync(null, CancellationToken.None);
                check(deniedResult.Status == "Partial" && deniedResult.RemovedFiles == 0 && File.Exists(denied) && deniedAudit.Any(a => a.Outcome == "Unavailable"), "Otimização: erro nativo de exclusão preserva arquivo e produz Partial auditado");
            }
            finally { if(File.Exists(denied)) File.SetAttributes(denied, FileAttributes.Normal); }
            var lockRoot = Path.Combine(root, "lock-only");
            var lockOnly = Path.Combine(lockRoot, "old.tmp"); Old(lockOnly);
            var lockedAudit = new List<OptimizationAudit>();
            using (var lockHandle = new FileStream(lockOnly, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var lockResult = await new BasicOptimizationService([lockRoot], lockedAudit.Add).RunAsync(null, CancellationToken.None);
                check(lockResult.Status == "Completed" && lockResult.SkippedFiles == 1 && File.Exists(lockOnly) && lockedAudit.Any(a => a.Outcome == "Locked"), "Otimização: arquivo em uso é skip esperado e não falha operacional");
            }
            var cancelRoot = Path.Combine(root, "cancel-between-files");
            Old(Path.Combine(cancelRoot, "first.tmp")); Old(Path.Combine(cancelRoot, "second.tmp"));
            using var midCancel = new CancellationTokenSource();
            var midResult = await new BasicOptimizationService([cancelRoot], a => { if(a.Action == "DeleteFile" && a.Outcome == "Completed") midCancel.Cancel(); }).RunAsync(null, midCancel.Token);
            check(midResult.Status == "Canceled" && midResult.RemovedFiles == 1 && Directory.GetFiles(cancelRoot).Length == 1 && midResult.DiskStatus == "Skipped", "Otimização: cancelamento entre arquivos conserva restante e não inicia disco");
            using var diskCancel = new CancellationTokenSource();
            var unknownDisk = await new BasicOptimizationService([], audit.Add, (_, _) => { diskCancel.Cancel(); throw new OperationCanceledException(diskCancel.Token); }).RunAsync(null, diskCancel.Token);
            check(unknownDisk.Status == "Canceled" && unknownDisk.DiskStatus == "Unknown", "Otimização: cancelamento durante operação nativa não inventa conclusão nem rollback");
            var diskFailure = await new BasicOptimizationService([], audit.Add, (_, _) => Task.FromResult("{\"success\":true,\"data\":[{\"Status\":\"Failed\"}]}")).RunAsync(null, CancellationToken.None);
            check(diskFailure.Status == "Partial" && diskFailure.DiskStatus == "Failed", "Otimização: falha nativa não vira sucesso geral");
            var timedRoot = Path.Combine(root, "timed"); var timedFile = Path.Combine(timedRoot, "old.tmp"); Old(timedFile);
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var stageAudit = new List<OptimizationAudit>();
            var timedResult = await new BasicOptimizationService([timedRoot], a => { stageAudit.Add(a); if(a.Outcome == "Deleting") clock.Advance(TimeSpan.FromSeconds(2)); },
                (_, _) => { clock.Advance(TimeSpan.FromSeconds(3)); return Task.FromResult("{\"success\":true,\"data\":[{\"Drive\":\"C\",\"Status\":\"Completed\"},{\"Drive\":\"D\",\"Status\":\"Skipped\"},{\"Drive\":\"E\",\"Status\":\"Failed\",\"Error\":\"fixture error\"}]}"); }, clock).RunAsync(null, CancellationToken.None);
            check(timedResult.Stages.Count == 2, "Otimização: retorna resumo das duas etapas executadas");
            var cleanupStage = timedResult.Stages.Single(s => s.Stage == "Cleanup");
            var diskStage = timedResult.Stages.Single(s => s.Stage == "Disk");
            check(cleanupStage.Duration == TimeSpan.FromSeconds(2) && cleanupStage.CompletedItems == 1 && cleanupStage.LogicalBytesRemoved == 5 && cleanupStage.ErrorCount == 0, "Otimização: etapa limpeza registra duração monotônica e contadores observados");
            check(diskStage.Duration == TimeSpan.FromSeconds(3) && diskStage.CompletedItems == 1 && diskStage.SkippedItems == 1 && diskStage.ErrorCount == 1 && diskStage.LogicalBytesRemoved == null && diskStage.Errors.Contains("E: fixture error"), "Otimização: etapa disco registra volumes, tempo e erros sem alegar bytes liberados");
            check(stageAudit.FindIndex(a => a.Stage?.Stage == "Cleanup") < stageAudit.FindIndex(a => a.Action == "OptimizeVolumes" && a.Outcome == "Started") && stageAudit.Count(a => a.Stage != null) == 2 && stageAudit.Where(a => a.Stage != null).Select(a => a.Stage).SequenceEqual(timedResult.Stages), "Otimização: auditoria preserva os mesmos resumos por etapa do resultado");
            var deniedDisk = await new BasicOptimizationService([], _ => {}, (_, _) => Task.FromResult("{\"success\":false,\"denied\":true,\"error\":\"UAC recusado\"}")).RunAsync(null, CancellationToken.None);
            check(deniedDisk.Status == "Partial" && deniedDisk.DiskStatus == "Denied" && deniedDisk.Stages.Single(s => s.Stage == "Disk") is { ErrorCount: 1, CompletedItems: null, LogicalBytesRemoved: null }, "Otimização: UAC recusado é parcial explícito sem inventar contagem de volumes");
            var paused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new BasicOptimizationService([], audit.Add, (_, _) => paused.Task).RunAsync(null, CancellationToken.None);
            bool duplicate = false;
            try { await new BasicOptimizationService([], audit.Add).RunAsync(null, CancellationToken.None); } catch (InvalidOperationException) { duplicate = true; }
            paused.SetResult("{\"success\":true,\"data\":[]}"); await first;
            check(duplicate, "Otimização: bloqueia execução concorrente entre instâncias");
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            if (Directory.Exists(rootJunction)) Directory.Delete(rootJunction);
            Directory.Delete(root, true);
        }
    }
    private static void CreateJunction(string path, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(path); start.ArgumentList.Add(target);
        using var process = Process.Start(start)!; process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Fixture junction indisponível: " + process.StandardError.ReadToEnd());
    }
}
