using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nexo.Support;

namespace Nexo.Checks;

internal static class NetworkRecoveryChecks
{
    private const string Guid = "00000000-0000-0000-0000-000000000007";
    private static readonly NetworkRequest Request = new("set_ipv4", 7, "static", [], "10.0.0.20", 24, "10.0.0.1") { ExpectedGuid = Guid };
    private static readonly string Before = JsonSerializer.Serialize(new { Index = 7, Name = "Ethernet", Guid = "{" + Guid + "}", Dhcp = "Disabled", StaticDns = "10.0.0.2", Dns = new[] { "10.0.0.2" }, Addresses = new[] { new { IPAddress = "10.0.0.10", PrefixLength = 24, PrefixOrigin = 1, AddressState = 4 } }, Routes = new[] { new { NextHop = "10.0.0.1", RouteMetric = 256 } } });

    public static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "NexoNetworkRecovery-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(root);

            var prompts = new List<string>();
            var beforeBackup = await Apply(store, async (dir, _) => await Task.Yield(), prompts, true);
            check(Bool(beforeBackup.Result, "changed") == false && !Flag(beforeBackup.Result, "reverted") && prompts.Count == 0 && beforeBackup.RecoveryStarts == 0
                && Text(beforeBackup.Result.GetProperty("recovery"), "message").Contains("não chegou a alterar"),
                "Rede: executor encerrado antes do backup registra que nada foi alterado, sem oferecer restauração");

            prompts.Clear();
            var applying = await Apply(store, async (dir, _) => await Write(dir, "before.json", Before), prompts, true);
            check(prompts.SequenceEqual(["Restaurar configuração de rede anterior?"]) && applying.RecoveryStarts == 1 && Flag(applying.Result, "reverted") && Bool(applying.Result, "changed") == true,
                "Rede: executor encerrado após o backup oferece restauração e confirma o resultado");
            check(applying.RecoveryScript.Contains("$recoveryBeforeJson=") && Decoded(applying.RecoveryScript).Any(s => s.Contains("\"Guid\":\"{" + Guid + "}\"") && s.Contains("10.0.0.10")),
                "Rede: restauração recebe o estado anterior validado e o GUID registrado");

            prompts.Clear();
            var restoring = await Apply(store, async (dir, stop) => { await Write(dir, "before.json", Before); await Write(dir, "applied.json", "{}"); await WaitFor(Path.Combine(dir, "decision.txt"), stop); }, prompts, true, keep: false);
            check(prompts.SequenceEqual(["Manter a configuração de rede?", "Restaurar configuração de rede anterior?"]) && Flag(restoring.Result, "reverted"),
                "Rede: executor encerrado durante a restauração própria tem a restauração oferecida novamente");

            prompts.Clear();
            var kept = await Apply(store, async (dir, stop) => { await Write(dir, "before.json", Before); await Write(dir, "applied.json", "{}"); await WaitFor(Path.Combine(dir, "decision.txt"), stop); }, prompts, true, keep: true);
            check(prompts.SequenceEqual(["Manter a configuração de rede?"]) && kept.RecoveryStarts == 0 && !Flag(kept.Result, "reverted")
                && Text(kept.Result.GetProperty("recovery"), "message").Contains("não foi conferida") && File.Exists(Path.Combine(kept.Directory, NetworkRecovery.ResultFile)),
                "Rede: executor encerrado após confirmação de manter registra a incerteza sem desfazer a escolha");

            prompts.Clear();
            var declined = await Apply(store, async (dir, _) => await Write(dir, "before.json", Before), prompts, false);
            check(declined.RecoveryStarts == 0 && !Flag(declined.Result, "reverted") && Flag(declined.Result.GetProperty("recovery"), "declined")
                && NetworkRecovery.Scan(root, DateTimeOffset.UtcNow, _ => false).All(i => i.Directory != declined.Directory),
                "Rede: restauração recusada fica registrada e não é repetida");

            prompts.Clear();
            var silent = await Apply(store, async (dir, _) => await Write(dir, "before.json", Before), prompts, true, recoveryWrites: false);
            check(!Flag(silent.Result, "reverted") && Text(silent.Result.GetProperty("recovery"), "state") == "unknown" && File.Exists(Path.Combine(silent.Directory, "recovery-unconfirmed.json"))
                && NetworkRecovery.Scan(root, DateTimeOffset.UtcNow, _ => false).Any(i => i.Directory == silent.Directory && i.Resolution == null),
                "Rede: restauração sem resultado registra incerteza e volta a ser oferecida");

            var startup = Path.Combine(root, "startup");
            var running = Transaction(startup, "running", 4242);
            var dead = Transaction(startup, "dead", 4243);
            var fresh = Transaction(startup, "fresh", null);
            var old = Transaction(startup, "old", null, DateTimeOffset.UtcNow.AddHours(-1));
            var pending = NetworkRecovery.Scan(startup, DateTimeOffset.UtcNow, pid => pid == 4242);
            check(pending.Select(p => p.Directory).OrderBy(d => d).SequenceEqual(new[] { dead, old }.OrderBy(d => d)) && pending.All(p => p.Resolution == null && p.Phase == "applying"),
                "Rede: abertura do Nexo encontra transações interrompidas e ignora executor ainda ativo ou recém-iniciado");

            var tampered = Transaction(startup, "tampered", 1, before: Before.Replace(Guid, "00000000-0000-0000-0000-000000000099"));
            var invalidIp = Transaction(startup, "invalid-ip", 1, before: Before.Replace("10.0.0.10", "1"));
            var askedForTampered = false;
            foreach (var directory in new[] { tampered, invalidIp })
            {
                var item = NetworkRecovery.Inspect(directory, DateTimeOffset.UtcNow, _ => false)!;
                await NetworkRecovery.Recover(item, _ => { askedForTampered = true; return Task.FromResult(true); }, _ => throw new InvalidOperationException("não deveria iniciar"), TimeSpan.FromSeconds(1), CancellationToken.None);
                check(item.Resolution != null && NetworkRecovery.Explain(item.Resolution).Contains("não pôde ser validado") && item.Before == null, "Rede: backup adulterado ou inválido não é usado para restaurar (" + Path.GetFileName(directory) + ")");
            }
            check(!askedForTampered, "Rede: backup inválido não pede autorização de restauração");

            var extra = Transaction(startup, "extra-field", 1, before: Before.Replace("\"Index\"", "\"Evil\":\"Remove-Item C:\\\\\",\"Index\""));
            var sanitized = NetworkRecovery.Inspect(extra, DateTimeOffset.UtcNow, _ => false)!;
            check(sanitized.Before != null && !sanitized.Before.Contains("Evil") && NetworkRecovery.Describe(sanitized).Contains("10.0.0.10/24"),
                "Rede: somente campos de rede validados seguem para o processo elevado");

            var quick = new NetworkDeadlines(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            check(NetworkDeadlines.Default.Total == TimeSpan.FromMinutes(5) && NetworkDeadlines.Default.Confirming == TimeSpan.FromSeconds(60) && NetworkDeadlines.Default.Applying > TimeSpan.FromSeconds(63),
                "Rede: prazo total de 5 minutos cobre aplicação com espera DHCP, confirmação de 60 s e conclusão");

            prompts.Clear();
            var clock = Stopwatch.StartNew();
            var hung = await Apply(store, async (dir, stop) => { await Write(dir, "before.json", Before); await Task.Delay(Timeout.Infinite, stop); }, prompts, true, deadlines: quick);
            var hungElapsed = clock.Elapsed;
            check(hungElapsed < TimeSpan.FromSeconds(10) && Text(hung.Result, "state") == "unknown" && Flag(hung.Result, "timedOut") && Flag(hung.Result, "processRunning") && Text(hung.Result, "phase") == "applying",
                "Rede: executor elevado travado antes da confirmação encerra a espera no prazo com estado desconhecido");
            check(!Flag(hung.Result, "reverted") && Bool(hung.Result, "success") == false && !Text(hung.Result, "error").Contains("restaurada") && Text(hung.Result, "error").Contains("não pôde ser confirmado"),
                "Rede: prazo esgotado não anuncia restauração inexistente");
            check(prompts.Count == 0 && hung.RecoveryStarts == 0 && Decision(hung.Directory) == "rollback" && File.Exists(Path.Combine(hung.Directory, NetworkChange.UnconfirmedFile)),
                "Rede: prazo esgotado pede restauração ao executor ativo, registra a incerteza e não inicia restauração concorrente");
            check(NetworkRecovery.Scan(root, DateTimeOffset.UtcNow, _ => true).All(i => i.Directory != hung.Directory)
                && NetworkRecovery.Scan(root, DateTimeOffset.UtcNow, _ => false).Any(i => i.Directory == hung.Directory && i.Resolution == null),
                "Rede: transação com executor ainda ativo só é oferecida para restauração depois que ele termina");

            prompts.Clear();
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                clock.Restart();
                var cancelledHung = await Apply(store, async (dir, stop) => { await Write(dir, "before.json", Before); await Task.Delay(Timeout.Infinite, stop); }, prompts, true, deadlines: quick, token: cancel.Token);
                check(clock.Elapsed < TimeSpan.FromSeconds(10) && Flag(cancelledHung.Result, "cancelled") && Text(cancelledHung.Result, "state") == "unknown" && !Flag(cancelledHung.Result, "reverted")
                    && Decision(cancelledHung.Directory) == "rollback" && Text(cancelledHung.Result, "error").StartsWith("Cancelamento registrado.") && prompts.Count == 0,
                    "Rede: cancelamento com executor travado termina no prazo sem confirmar restauração");
            }

            prompts.Clear();
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                var honoured = await Apply(store, async (dir, stop) =>
                {
                    await Write(dir, "before.json", Before);
                    await WaitFor(Path.Combine(dir, "decision.txt"), stop);
                    if (Decision(dir) == "rollback") await Write(dir, "result.json", "{\"success\":false,\"reverted\":true,\"message\":\"Configuração anterior restaurada por cancelamento ou ausência de confirmação.\"}");
                }, prompts, true, deadlines: quick, token: cancel.Token);
                check(Flag(honoured.Result, "reverted") && !honoured.Result.TryGetProperty("state", out _) && prompts.Count == 0,
                    "Rede: cancelamento atendido pelo executor devolve a restauração registrada por ele");
            }

            prompts.Clear();
            var keptHung = await Apply(store, async (dir, stop) => { await Write(dir, "before.json", Before); await Write(dir, "applied.json", "{}"); await WaitFor(Path.Combine(dir, "decision.txt"), stop); await Task.Delay(Timeout.Infinite, stop); }, prompts, true, keep: true, deadlines: quick);
            check(Text(keptHung.Result, "phase") == "keeping" && Decision(keptHung.Directory) == "keep" && Text(keptHung.Result, "error").Contains("não foi conferida") && !Flag(keptHung.Result, "reverted"),
                "Rede: executor travado após confirmar manter preserva a escolha e informa que o estado não foi conferido");

            prompts.Clear();
            var restoringHung = await Apply(store, async (dir, stop) => { await Write(dir, "before.json", Before); await Write(dir, "applied.json", "{}"); await WaitFor(Path.Combine(dir, "decision.txt"), stop); await Task.Delay(Timeout.Infinite, stop); }, prompts, true, keep: false, deadlines: quick);
            check(Text(restoringHung.Result, "phase") == "restoring" && Text(restoringHung.Result, "error").Contains("Não há confirmação de que a configuração anterior voltou") && !Flag(restoringHung.Result, "reverted"),
                "Rede: restauração do executor que não termina no prazo fica como estado desconhecido");

            prompts.Clear();
            clock.Restart();
            var unanswered = await Apply(store, async (dir, stop) =>
            {
                await Write(dir, "before.json", Before); await Write(dir, "applied.json", "{}");
                await WaitFor(Path.Combine(dir, "decision.txt"), stop);
                await Write(dir, "result.json", Decision(dir) == "keep" ? "{\"success\":true}" : "{\"success\":false,\"reverted\":true}");
            }, prompts, true, deadlines: quick, unanswered: true);
            check(clock.Elapsed < TimeSpan.FromSeconds(10) && Decision(unanswered.Directory) == "rollback" && Flag(unanswered.Result, "reverted") && prompts.SequenceEqual(["Manter a configuração de rede?"]),
                "Rede: confirmação sem resposta no prazo grava a restauração em vez de esperar indefinidamente");

            prompts.Clear();
            var killed = await ApplyWithRealProcess(store, prompts);
            check(killed.Killed && prompts.Contains("Restaurar configuração de rede anterior?") && Flag(killed.Result, "reverted"),
                "Rede: processo PowerShell real encerrado à força após o backup leva à restauração oferecida");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record Outcome(JsonElement Result, string Directory, int RecoveryStarts, string RecoveryScript, bool Killed = false);

    private static async Task<Outcome> Apply(Store store, Func<string, CancellationToken, Task> executor, List<string> prompts, bool restore, bool keep = false, bool recoveryWrites = true, NetworkDeadlines? deadlines = null, CancellationToken token = default, bool unanswered = false)
    {
        string directory = "";
        var starts = 0;
        var recoveryScript = "";
        Func<string, IElevatedProcess> start = script =>
        {
            var dir = OperationDir(script);
            if (script.Contains("$recoveryBeforeJson="))
            {
                starts++; recoveryScript = script;
                return new SimulatedProcess(async _ => { if (recoveryWrites) await Write(dir, NetworkRecovery.ResultFile, "{\"success\":true,\"restored\":true,\"message\":\"Configuração anterior restaurada e conferida.\"}"); });
            }
            directory = dir;
            return new SimulatedProcess(stop => executor(dir, stop));
        };
        Func<ChangeRequest, Task<bool>> confirm = request => { prompts.Add(request.Title); return unanswered && request.KeepNetwork ? new TaskCompletionSource<bool>().Task : Task.FromResult(request.KeepNetwork ? keep : restore); };
        var output = await NetworkChange.Apply(Request, store, confirm, new Silent(), token, start, deadlines);
        using var document = JsonDocument.Parse(output);
        return new Outcome(document.RootElement.Clone(), directory, starts, recoveryScript);
    }

    private static async Task<Outcome> ApplyWithRealProcess(Store store, List<string> prompts)
    {
        var killed = false;
        string directory = "";
        Func<string, IElevatedProcess> start = script =>
        {
            var dir = OperationDir(script);
            if (script.Contains("$recoveryBeforeJson=")) return new SimulatedProcess(async _ => await Write(dir, NetworkRecovery.ResultFile, "{\"success\":true,\"restored\":true}"));
            directory = dir;
            var child = "Set-Content -LiteralPath '" + Path.Combine(dir, "before.json").Replace("'", "''") + "' -Value '" + Before.Replace("'", "''") + "'; Start-Sleep -Seconds 60";
            var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(child))) { UseShellExecute = false, CreateNoWindow = true })!;
            _ = Task.Run(async () =>
            {
                var clock = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(dir, "before.json")) && clock.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
                process.Kill(true); killed = true;
            });
            return new RealProcess(process);
        };
        Func<ChangeRequest, Task<bool>> confirm = request => { prompts.Add(request.Title); return Task.FromResult(!request.KeepNetwork); };
        var output = await NetworkChange.Apply(Request, store, confirm, new Silent(), CancellationToken.None, start);
        using var document = JsonDocument.Parse(output);
        return new Outcome(document.RootElement.Clone(), directory, 0, "", killed);
    }

    private static string Transaction(string root, string name, int? pid, DateTimeOffset? created = null, string? before = null)
    {
        var dir = Path.Combine(root, "operations", name);
        Directory.CreateDirectory(dir);
        NetworkRecovery.WriteRecord(dir, new NetworkTransactionRecord("set_ipv4", 7, Guid, created ?? DateTimeOffset.UtcNow, pid));
        File.WriteAllText(Path.Combine(dir, "before.json"), before ?? Before);
        return dir;
    }

    private static string OperationDir(string script)
    {
        var start = script.IndexOf("$operationDir='", StringComparison.Ordinal) + "$operationDir='".Length;
        return script[start..script.IndexOf("';", start, StringComparison.Ordinal)].Replace("''", "'");
    }

    private static IEnumerable<string> Decoded(string script)
    {
        foreach (var part in script.Split("FromBase64String('").Skip(1))
            yield return Encoding.UTF8.GetString(Convert.FromBase64String(part[..part.IndexOf('\'')]));
    }

    private static async Task Write(string dir, string name, string content)
    {
        var temporary = Path.Combine(dir, name + ".tmp");
        await File.WriteAllTextAsync(temporary, content);
        File.Move(temporary, Path.Combine(dir, name), true);
    }

    private static async Task WaitFor(string path, CancellationToken stop)
    {
        while (!File.Exists(path)) await Task.Delay(10, stop);
        await Task.Delay(20, stop);
    }

    private static string Decision(string dir) => File.Exists(Path.Combine(dir, "decision.txt")) ? File.ReadAllText(Path.Combine(dir, "decision.txt")).Trim() : "";
    private static bool? Bool(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : null;
    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";

    private sealed class Silent : IProgress<string> { public void Report(string value) { } }

    private sealed class SimulatedProcess : IElevatedProcess
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task work;
        public SimulatedProcess(Func<CancellationToken, Task> behavior) => work = Task.Run(async () => { try { await behavior(stop.Token); } catch (OperationCanceledException) { } });
        public int? Id => 999999;
        public bool HasExited => work.IsCompleted;
        public Task WaitForExitAsync(CancellationToken token) => work.WaitAsync(token);
        public void Dispose() => stop.Cancel();
    }

    private sealed class RealProcess(Process process) : IElevatedProcess
    {
        public int? Id => process.Id;
        public bool HasExited => process.HasExited;
        public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
        public void Dispose() => process.Dispose();
    }
}
