using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Nexo.Support;

namespace Nexo.Checks;

internal static class NetworkRecoveryChecks
{
    private const string Guid = "00000000-0000-0000-0000-000000000007";
    private static readonly NetworkRequest Request = new("set_ipv4", 7, "static", [], "10.0.0.20", 24, "10.0.0.1") { ExpectedGuid = Guid };
    private static readonly string Before = JsonSerializer.Serialize(new { Index = 7, Name = "Ethernet", Guid = "{" + Guid + "}", Dhcp = "Disabled", StaticDns = "10.0.0.2", Dns = new[] { "10.0.0.2" }, Addresses = new[] { new { IPAddress = "10.0.0.10", PrefixLength = 24, PrefixOrigin = 1, AddressState = 4 } }, Routes = new[] { new { NextHop = "10.0.0.1", RouteMetric = 256 } } });
    // DNS que um processo sem elevação tentaria aplicar com o UAC do usuário (#10, revisão do @qa).
    private static readonly string Forged = Before.Replace("10.0.0.2", "203.0.113.66");

    public static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "NexoNetworkRecovery-" + System.Guid.NewGuid().ToString("N"));
        var protectedRoot = Path.Combine(root, "programdata", "NexoSupport", "operations");
        var untrusted = new HashSet<string>();
        var trust = new NetworkRecoveryTrust(protectedRoot, directory => !untrusted.Contains(Path.GetFileName(directory)));
        try
        {
            var store = new Store(root);

            var prompts = new List<string>();
            var beforeBackup = await Apply(store, trust, async (dir, _) => await Task.Yield(), prompts, true);
            check(Bool(beforeBackup.Result, "changed") == false && !Flag(beforeBackup.Result, "reverted") && prompts.Count == 0 && beforeBackup.RecoveryStarts == 0
                && Text(beforeBackup.Result.GetProperty("recovery"), "message").Contains("não chegou a alterar"),
                "Rede: executor encerrado antes do backup registra que nada foi alterado, sem oferecer restauração");

            // #97: executor que sai sem resultado e sem pasta protegida, com a cadeia NexoSupport recusada.
            prompts.Clear();
            const string HostileReason = @"C:\ProgramData\NexoSupport: é link (reparse point)";
            var hostileTrust = trust with { ChainProblem = _ => HostileReason };
            var hostile = await Apply(store, hostileTrust, async (dir, _) => await Task.Yield(), prompts, true);
            var hostileError = Text(hostile.Result, "error");
            check(Flag(hostile.Result, "protectedFolderProblem") && Bool(hostile.Result, "changed") == null && !hostile.Result.TryGetProperty("recovery", out _) && prompts.Count == 0 && hostile.RecoveryStarts == 0
                && hostileError.Contains("%ProgramData%\\NexoSupport") && hostileError.Contains(HostileReason)
                && hostileError.Contains("não há confirmação") && hostileError.Contains("Rede e recuperação") && hostileError.Contains("takeown") && hostileError.Contains("icacls") && hostileError.Contains("rmdir"),
                "Rede: pasta NexoSupport hostil é diagnosticada sem atribuir causa ou estado da rede sem prova (#97)");
            var precreated = await Apply(store, hostileTrust, async (dir, _) => { Directory.CreateDirectory(Secured(protectedRoot, dir)); await Task.Yield(); }, prompts, true);
            check(Flag(precreated.Result, "protectedFolderProblem"), "Rede: pasta da operação pré-criada não esconde problema na cadeia protegida (#97)");
            prompts.Clear();
            var trustedChain = await Apply(store, trust with { ChainProblem = _ => null }, async (dir, _) => await Task.Yield(), prompts, true);
            check(!Flag(trustedChain.Result, "protectedFolderProblem") && Text(trustedChain.Result, "error").StartsWith("Executor encerrado sem resultado."),
                "Rede: com a pasta NexoSupport confiável, a mensagem de executor encerrado continua a mesma (#97)");
            prompts.Clear();
            var backedUp = await Apply(store, hostileTrust, async (dir, _) => await Backup(dir, protectedRoot, Before), prompts, true);
            check(!Flag(backedUp.Result, "protectedFolderProblem") && backedUp.RecoveryStarts == 1,
                "Rede: executor que chegou a gravar o backup protegido não é explicado como pasta recusada (#97)");
            if (OperatingSystem.IsWindows()) await HostileChainOnWindows(check, root);

            prompts.Clear();
            var applying = await Apply(store, trust, async (dir, _) => await Backup(dir, protectedRoot, Before), prompts, true);
            check(prompts.SequenceEqual(["Restaurar configuração de rede anterior?"]) && applying.RecoveryStarts == 1 && Flag(applying.Result, "reverted") && Bool(applying.Result, "changed") == true,
                "Rede: executor encerrado após o backup oferece restauração e confirma o resultado");
            check(File.Exists(Path.Combine(applying.Directory, NetworkRecovery.ResultFile)) && File.ReadAllText(Path.Combine(applying.Directory, NetworkRecovery.ResultFile)).Contains("restaurada e conferida"),
                "Rede: o aplicativo copia sem elevação o resultado que o processo elevado gravou na pasta protegida");
            var id = Path.GetFileName(applying.Directory);
            check(applying.RecoveryScript.Contains("$recoveryMode=$true") && applying.RecoveryScript.Contains("$operationId='" + id + "'")
                && !applying.RecoveryScript.Contains("$recoveryBeforeJson") && Decoded(applying.RecoveryScript).All(s => !s.Contains("10.0.0.10") && !s.Contains("10.0.0.2\"")),
                "Rede: restauração não recebe o estado anterior do aplicativo; o processo elevado lê a cópia protegida pelo id da operação");
            check(applying.ForwardScript.Contains("$operationId='" + id + "'"), "Rede: executor recebe o id da operação para gravar a cópia protegida");
            // O executor inteiro em -EncodedCommand passa do limite da linha de comando do Windows; os argumentos que a
            // produção usa (NetworkRecovery.ElevatedArguments) levam o carregador compactado, com folga.
            var forwardLength = NetworkRecovery.CommandLength(applying.ForwardScript);
            var recoveryLength = NetworkRecovery.CommandLength(applying.RecoveryScript);
            check(NetworkRecovery.UncompactedLength(applying.ForwardScript) > NetworkRecovery.CommandLineLimit && forwardLength < NetworkRecovery.CommandLineLimit && recoveryLength < NetworkRecovery.CommandLineLimit
                && NetworkRecovery.ElevatedArguments(applying.ForwardScript)[^1] == WindowsTools.ElevatedArguments(NetworkRecovery.Compact(applying.ForwardScript))[^1],
                "Rede: comando elevado real de aplicação (" + forwardLength + ") e de restauração (" + recoveryLength + ") cabe no limite de " + NetworkRecovery.CommandLineLimit + " caracteres");
            await RoundTrip(check, root, [("aplicação", applying.ForwardScript), ("restauração", applying.RecoveryScript)]);
            await CompactRuns(check, root);

            prompts.Clear();
            var restoring = await Apply(store, trust, async (dir, stop) => { await Backup(dir, protectedRoot, Before); await Write(Secured(protectedRoot, dir), "applied.json", "{}"); await WaitFor(Path.Combine(dir, "decision.txt"), stop); }, prompts, true, keep: false);
            check(prompts.SequenceEqual(["Manter a configuração de rede?", "Restaurar configuração de rede anterior?"]) && Flag(restoring.Result, "reverted"),
                "Rede: executor encerrado durante a restauração própria tem a restauração oferecida novamente");

            prompts.Clear();
            var kept = await Apply(store, trust, async (dir, stop) => { await Backup(dir, protectedRoot, Before); await Write(Secured(protectedRoot, dir), "applied.json", "{}"); await WaitFor(Path.Combine(dir, "decision.txt"), stop); }, prompts, true, keep: true);
            check(prompts.SequenceEqual(["Manter a configuração de rede?"]) && kept.RecoveryStarts == 0 && !Flag(kept.Result, "reverted")
                && Text(kept.Result.GetProperty("recovery"), "message").Contains("não foi conferida") && File.Exists(Path.Combine(kept.Directory, NetworkRecovery.ResultFile)),
                "Rede: executor encerrado após confirmação de manter registra a incerteza sem desfazer a escolha");

            prompts.Clear();
            var declined = await Apply(store, trust, async (dir, _) => await Backup(dir, protectedRoot, Before), prompts, false);
            check(declined.RecoveryStarts == 0 && !Flag(declined.Result, "reverted") && Flag(declined.Result.GetProperty("recovery"), "declined")
                && NetworkRecovery.Scan(root, DateTimeOffset.UtcNow, _ => false, trust).All(i => i.Directory != declined.Directory),
                "Rede: restauração recusada fica registrada e não é repetida");

            prompts.Clear();
            var silent = await Apply(store, trust, async (dir, _) => await Backup(dir, protectedRoot, Before), prompts, true, recoveryWrites: false);
            check(!Flag(silent.Result, "reverted") && Text(silent.Result.GetProperty("recovery"), "state") == "unknown" && File.Exists(Path.Combine(silent.Directory, "recovery-unconfirmed.json"))
                && NetworkRecovery.Scan(root, DateTimeOffset.UtcNow, _ => false, trust).Any(i => i.Directory == silent.Directory && i.Resolution == null),
                "Rede: restauração sem resultado registra incerteza e volta a ser oferecida");

            // UAC recusado (Win32 1223): nada foi alterado e a abertura seguinte não mostra alerta de transação interrompida.
            var refusedDir = "";
            Func<string, IElevatedProcess> refuse = script => { refusedDir = OperationDir(script); throw new Win32Exception(1223, "A operação foi cancelada pelo usuário."); };
            var refusedThrown = false;
            try { await NetworkChange.Apply(Request, store, _ => Task.FromResult(true), new Silent(), CancellationToken.None, refuse, trust: trust); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { refusedThrown = true; }
            var refusedResult = Path.Combine(refusedDir, NetworkRecovery.ResultFile);
            check(refusedThrown && File.Exists(refusedResult) && File.ReadAllText(refusedResult).Contains("\"started\":false")
                && NetworkRecovery.Scan(root, DateTimeOffset.UtcNow.AddHours(1), _ => false, trust).All(i => i.Directory != refusedDir),
                "Rede: UAC recusado propaga a recusa e não deixa transação órfã para a próxima abertura");

            var startup = Path.Combine(root, "startup");
            var running = Transaction(startup, protectedRoot, 4242);
            var dead = Transaction(startup, protectedRoot, 4243);
            var fresh = Transaction(startup, protectedRoot, null);
            var old = Transaction(startup, protectedRoot, null, DateTimeOffset.UtcNow.AddHours(-1));
            var pending = NetworkRecovery.Scan(startup, DateTimeOffset.UtcNow, pid => pid == 4242, trust);
            check(pending.Select(p => p.Directory).OrderBy(d => d).SequenceEqual(new[] { dead, old }.OrderBy(d => d)) && pending.All(p => p.Resolution == null && p.Phase == "applying"),
                "Rede: abertura do Nexo encontra transações interrompidas e ignora executor ainda ativo ou recém-iniciado");

            var tampered = Transaction(startup, protectedRoot, 1, before: Before.Replace(Guid, "00000000-0000-0000-0000-000000000099"));
            var invalidIp = Transaction(startup, protectedRoot, 1, before: Before.Replace("10.0.0.10", "1"));
            var askedForTampered = false;
            foreach (var directory in new[] { tampered, invalidIp })
            {
                var item = NetworkRecovery.Inspect(directory, DateTimeOffset.UtcNow, _ => false, trust: trust)!;
                await NetworkRecovery.Recover(item, _ => { askedForTampered = true; return Task.FromResult(true); }, _ => throw new InvalidOperationException("não deveria iniciar"), TimeSpan.FromSeconds(1), CancellationToken.None);
                check(item.Resolution != null && NetworkRecovery.Explain(item.Resolution).Contains("não pôde ser validado") && item.Before == null, "Rede: backup adulterado ou inválido não é usado para restaurar (" + (directory == tampered ? "GUID" : "IP") + ")");
            }
            check(!askedForTampered, "Rede: backup inválido não pede autorização de restauração");

            var extra = Transaction(startup, protectedRoot, 1, before: Before.Replace("\"Index\"", "\"Evil\":\"Remove-Item C:\\\\\",\"Index\""));
            var sanitized = NetworkRecovery.Inspect(extra, DateTimeOffset.UtcNow, _ => false, trust: trust)!;
            check(sanitized.Before != null && !sanitized.Before.Contains("Evil") && NetworkRecovery.Describe(sanitized).Contains("10.0.0.10/24"),
                "Rede: somente campos de rede validados seguem para o processo elevado");

            var quick = new NetworkDeadlines(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            check(NetworkDeadlines.Default.Total == TimeSpan.FromMinutes(5) && NetworkDeadlines.Default.Confirming == TimeSpan.FromSeconds(60) && NetworkDeadlines.Default.Applying > TimeSpan.FromSeconds(63),
                "Rede: prazo total de 5 minutos cobre aplicação com espera DHCP, confirmação de 60 s e conclusão");

            prompts.Clear();
            var clock = Stopwatch.StartNew();
            var hung = await Apply(store, trust, async (dir, stop) => { await Backup(dir, protectedRoot, Before); await Task.Delay(Timeout.Infinite, stop); }, prompts, true, deadlines: quick);
            var hungElapsed = clock.Elapsed;
            check(hungElapsed < TimeSpan.FromSeconds(10) && Text(hung.Result, "state") == "unknown" && Flag(hung.Result, "timedOut") && Flag(hung.Result, "processRunning") && Text(hung.Result, "phase") == "applying",
                "Rede: executor elevado travado antes da confirmação encerra a espera no prazo com estado desconhecido");
            check(!Flag(hung.Result, "reverted") && Bool(hung.Result, "success") == false && !Text(hung.Result, "error").Contains("restaurada") && Text(hung.Result, "error").Contains("não pôde ser confirmado"),
                "Rede: prazo esgotado não anuncia restauração inexistente");
            check(prompts.Count == 0 && hung.RecoveryStarts == 0 && Decision(hung.Directory) == "rollback" && File.Exists(Path.Combine(hung.Directory, NetworkChange.UnconfirmedFile)),
                "Rede: prazo esgotado pede restauração ao executor ativo, registra a incerteza e não inicia restauração concorrente");
            check(NetworkRecovery.Scan(root, DateTimeOffset.UtcNow, _ => true, trust).All(i => i.Directory != hung.Directory)
                && NetworkRecovery.Scan(root, DateTimeOffset.UtcNow, _ => false, trust).Any(i => i.Directory == hung.Directory && i.Resolution == null),
                "Rede: transação com executor ainda ativo só é oferecida para restauração depois que ele termina");

            prompts.Clear();
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                clock.Restart();
                var cancelledHung = await Apply(store, trust, async (dir, stop) => { await Backup(dir, protectedRoot, Before); await Task.Delay(Timeout.Infinite, stop); }, prompts, true, deadlines: quick, token: cancel.Token);
                check(clock.Elapsed < TimeSpan.FromSeconds(10) && Flag(cancelledHung.Result, "cancelled") && Text(cancelledHung.Result, "state") == "unknown" && !Flag(cancelledHung.Result, "reverted")
                    && Decision(cancelledHung.Directory) == "rollback" && Text(cancelledHung.Result, "error").StartsWith("Cancelamento registrado.") && prompts.Count == 0,
                    "Rede: cancelamento com executor travado termina no prazo sem confirmar restauração");
            }

            prompts.Clear();
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                var honoured = await Apply(store, trust, async (dir, stop) =>
                {
                    await Backup(dir, protectedRoot, Before);
                    await WaitFor(Path.Combine(dir, "decision.txt"), stop);
                    if (Decision(dir) == "rollback") await Write(Secured(protectedRoot, dir), "result.json", "{\"success\":false,\"reverted\":true,\"message\":\"Configuração anterior restaurada por cancelamento ou ausência de confirmação.\"}");
                }, prompts, true, deadlines: quick, token: cancel.Token);
                check(Flag(honoured.Result, "reverted") && !honoured.Result.TryGetProperty("state", out _) && prompts.Count == 0,
                    "Rede: cancelamento atendido pelo executor devolve a restauração registrada por ele");
            }

            // Cancelado antes do applied.json, o prazo ganha a fase de conclusão (#9): o executor que só chega à
            // confirmação depois do prazo simples de aplicação ainda devolve a restauração registrada por ele.
            prompts.Clear();
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                var slow = new NetworkDeadlines(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
                var late = await Apply(store, trust, async (dir, stop) =>
                {
                    await Backup(dir, protectedRoot, Before);
                    await Task.Delay(TimeSpan.FromSeconds(3), stop);
                    await Write(Secured(protectedRoot, dir), "applied.json", "{}");
                    if (Decision(dir) == "rollback") await Write(Secured(protectedRoot, dir), "result.json", "{\"success\":false,\"reverted\":true,\"message\":\"Configuração anterior restaurada por cancelamento ou ausência de confirmação.\"}");
                }, prompts, true, deadlines: slow, token: cancel.Token);
                check(Flag(late.Result, "reverted") && !late.Result.TryGetProperty("state", out _) && prompts.Count == 0 && Decision(late.Directory) == "rollback",
                    "Rede: cancelamento antes da aplicação estende o prazo e aceita a restauração que o executor registra depois do prazo simples");
            }

            // Cancelar com a janela de confirmação aberta encerra a espera na hora, sem aguardar o prazo da janela.
            prompts.Clear();
            using (var cancel = new CancellationTokenSource())
            {
                var patient = new NetworkDeadlines(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2));
                clock.Restart();
                var open = await Apply(store, trust, async (dir, stop) =>
                {
                    await Backup(dir, protectedRoot, Before); await Write(Secured(protectedRoot, dir), "applied.json", "{}");
                    cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
                    await WaitFor(Path.Combine(dir, "decision.txt"), stop);
                    await Write(Secured(protectedRoot, dir), "result.json", Decision(dir) == "keep" ? "{\"success\":true}" : "{\"success\":false,\"reverted\":true}");
                }, prompts, true, deadlines: patient, token: cancel.Token, unanswered: true);
                check(clock.Elapsed < TimeSpan.FromSeconds(10) && Decision(open.Directory) == "rollback" && Flag(open.Result, "reverted") && prompts.SequenceEqual(["Manter a configuração de rede?"]),
                    "Rede: cancelamento com a janela de confirmação aberta grava a restauração sem esperar o prazo da janela");
            }

            prompts.Clear();
            var keptHung = await Apply(store, trust, async (dir, stop) => { await Backup(dir, protectedRoot, Before); await Write(Secured(protectedRoot, dir), "applied.json", "{}"); await WaitFor(Path.Combine(dir, "decision.txt"), stop); await Task.Delay(Timeout.Infinite, stop); }, prompts, true, keep: true, deadlines: quick);
            check(Text(keptHung.Result, "phase") == "keeping" && Decision(keptHung.Directory) == "keep" && Text(keptHung.Result, "error").Contains("não foi conferida") && !Flag(keptHung.Result, "reverted"),
                "Rede: executor travado após confirmar manter preserva a escolha e informa que o estado não foi conferido");

            prompts.Clear();
            var restoringHung = await Apply(store, trust, async (dir, stop) => { await Backup(dir, protectedRoot, Before); await Write(Secured(protectedRoot, dir), "applied.json", "{}"); await WaitFor(Path.Combine(dir, "decision.txt"), stop); await Task.Delay(Timeout.Infinite, stop); }, prompts, true, keep: false, deadlines: quick);
            check(Text(restoringHung.Result, "phase") == "restoring" && Text(restoringHung.Result, "error").Contains("Não há confirmação de que a configuração anterior voltou") && !Flag(restoringHung.Result, "reverted"),
                "Rede: restauração do executor que não termina no prazo fica como estado desconhecido");

            prompts.Clear();
            clock.Restart();
            var unanswered = await Apply(store, trust, async (dir, stop) =>
            {
                await Backup(dir, protectedRoot, Before); await Write(Secured(protectedRoot, dir), "applied.json", "{}");
                await WaitFor(Path.Combine(dir, "decision.txt"), stop);
                await Write(Secured(protectedRoot, dir), "result.json", Decision(dir) == "keep" ? "{\"success\":true}" : "{\"success\":false,\"reverted\":true}");
            }, prompts, true, deadlines: quick, unanswered: true);
            check(clock.Elapsed < TimeSpan.FromSeconds(10) && Decision(unanswered.Directory) == "rollback" && Flag(unanswered.Result, "reverted") && prompts.SequenceEqual(["Manter a configuração de rede?"]),
                "Rede: confirmação sem resposta no prazo grava a restauração em vez de esperar indefinidamente");

            // Transação forjada só em %LOCALAPPDATA%, sem a cópia protegida: nada é oferecido nem iniciado.
            var forged = Transaction(startup, protectedRoot, 1, before: Forged, secure: false);
            var forgedItem = NetworkRecovery.Inspect(forged, DateTimeOffset.UtcNow, _ => false, trust: trust)!;
            var forgedAsked = false; var forgedStarted = false;
            var forgedOutcome = await NetworkRecovery.Recover(forgedItem, _ => { forgedAsked = true; return Task.FromResult(true); }, _ => { forgedStarted = true; throw new InvalidOperationException("não deveria iniciar"); }, TimeSpan.FromSeconds(1), CancellationToken.None);
            check(forgedItem.Before == null && forgedItem.Resolution != null && !forgedAsked && !forgedStarted && NetworkRecovery.Explain(forgedOutcome).Contains("não chegou a alterar") && !forgedOutcome.Contains("203.0.113.66"),
                "Rede: transação forjada sem cópia protegida não gera oferta nem UAC");

            // A cópia do usuário adulterada não muda o que é mostrado: vale a protegida.
            var swapped = Transaction(startup, protectedRoot, 1);
            File.WriteAllText(Path.Combine(swapped, "before.json"), Forged);
            var swappedItem = NetworkRecovery.Inspect(swapped, DateTimeOffset.UtcNow, _ => false, trust: trust)!;
            check(swappedItem.Before != null && !swappedItem.Before.Contains("203.0.113.66") && !NetworkRecovery.Describe(swappedItem).Contains("203.0.113.66") && NetworkRecovery.Describe(swappedItem).Contains("DNS: 10.0.0.2"),
                "Rede: a janela de restauração mostra a cópia protegida, não o backup em %LOCALAPPDATA%");

            // Cópia protegida com dono ou permissões fora do esperado (pasta criada por outro usuário).
            var unsafeCopy = Transaction(startup, protectedRoot, 1, before: Forged);
            untrusted.Add(Path.GetFileName(unsafeCopy));
            var unsafeItem = NetworkRecovery.Inspect(unsafeCopy, DateTimeOffset.UtcNow, _ => false, trust: trust)!;
            check(unsafeItem.Before == null && unsafeItem.Resolution != null && NetworkRecovery.Explain(unsafeItem.Resolution).Contains("permissões inesperados"),
                "Rede: cópia protegida com dono ou permissões inesperados não gera oferta");

            // Registro do usuário diferente do protegido.
            var mismatch = Transaction(startup, protectedRoot, 1, secureRecord: new NetworkTransactionRecord("set_dns", 7, Guid, DateTimeOffset.UtcNow));
            var mismatchItem = NetworkRecovery.Inspect(mismatch, DateTimeOffset.UtcNow, _ => false, trust: trust)!;
            check(mismatchItem.Before == null && mismatchItem.Resolution != null && NetworkRecovery.Explain(mismatchItem.Resolution).Contains("não corresponde à cópia protegida"),
                "Rede: registro do usuário diferente do protegido não gera oferta");

            // Limite de idade: 8 dias não oferece, 6 dias oferece.
            var stale = Transaction(startup, protectedRoot, 1, created: DateTimeOffset.UtcNow.AddDays(-8));
            var staleItem = NetworkRecovery.Inspect(stale, DateTimeOffset.UtcNow, _ => false, trust: trust)!;
            var recent = Transaction(startup, protectedRoot, 1, created: DateTimeOffset.UtcNow.AddDays(-6));
            var recentItem = NetworkRecovery.Inspect(recent, DateTimeOffset.UtcNow, _ => false, trust: trust)!;
            check(staleItem.Before == null && staleItem.Resolution != null && NetworkRecovery.Explain(staleItem.Resolution).Contains("mais de 7 dias") && recentItem.Resolution == null && recentItem.Before != null,
                "Rede: transação interrompida há mais de 7 dias não tem a restauração oferecida");

            // Mais de 7 dias e a pasta protegida já apagada pela limpeza: não se afirma que a rede ficou como estava.
            var cleaned = Transaction(startup, protectedRoot, 1, created: DateTimeOffset.UtcNow.AddDays(-8), secure: false);
            var cleanedItem = NetworkRecovery.Inspect(cleaned, DateTimeOffset.UtcNow, _ => false, trust: trust)!;
            check(cleanedItem.Before == null && cleanedItem.Resolution != null && NetworkRecovery.Explain(cleanedItem.Resolution).Contains("mais de 7 dias")
                && NetworkRecovery.Explain(cleanedItem.Resolution).Contains("confira a rede") && !NetworkRecovery.Explain(cleanedItem.Resolution).Contains("não chegou a alterar"),
                "Rede: transação com mais de 7 dias sem a pasta protegida pede para conferir a rede, sem afirmar que nada mudou");

            // Pasta que não é id de operação, ou caminho com "..": ignorada, sem gravar nada.
            var named = Path.Combine(startup, "operations", "nao-e-guid");
            Directory.CreateDirectory(named);
            NetworkRecovery.WriteRecord(named, new NetworkTransactionRecord("set_ipv4", 7, Guid, DateTimeOffset.UtcNow.AddHours(-1), 1));
            File.WriteAllText(Path.Combine(named, "before.json"), Before);
            var dotted = Path.Combine(startup, "operations", "..", "operations", Path.GetFileName(dead));
            check(NetworkRecovery.Scan(startup, DateTimeOffset.UtcNow, _ => false, trust).All(i => i.Directory != named) && NetworkRecovery.Inspect(named, DateTimeOffset.UtcNow, _ => false, trust: trust) == null
                && !File.Exists(Path.Combine(named, NetworkRecovery.ResultFile)) && NetworkRecovery.Inspect(dotted, DateTimeOffset.UtcNow, _ => false, trust: trust) == null
                && NetworkRecovery.IsOperationId(Path.GetFileName(dead)) && !NetworkRecovery.IsOperationId(Path.GetFileName(dead).ToUpperInvariant()),
                "Rede: só pastas com id de operação (GUID \"N\") e sem \"..\" são inspecionadas");

            var odd = Transaction(startup, protectedRoot, 1, before: Before.Replace("\"Ethernet\"", "\"Eth\\nernet \\u0022x\\u0022 <b>\""));
            var oddItem = NetworkRecovery.Inspect(odd, DateTimeOffset.UtcNow, _ => false, trust: trust)!;
            check(NetworkRecovery.Describe(oddItem).Contains("#7 Eth_ernet _x_ _b_:"), "Rede: nome do adaptador na janela de restauração passa pelo filtro de caracteres");

            // Resultado plantado numa pasta protegida não confiável não é copiado nem encerra a transação.
            var planted = Transaction(startup, protectedRoot, 1);
            File.WriteAllText(Path.Combine(protectedRoot, Path.GetFileName(planted), NetworkRecovery.ResultFile), "{\"success\":true,\"restored\":true,\"message\":\"PLANTADO\"}");
            untrusted.Add(Path.GetFileName(planted));
            var plantedItem = NetworkRecovery.Inspect(planted, DateTimeOffset.UtcNow, _ => false, trust: trust);
            check(plantedItem?.Resolution != null && !File.ReadAllText(Path.Combine(planted, NetworkRecovery.ResultFile)).Contains("PLANTADO"),
                "Rede: resultado plantado em pasta protegida não confiável não é copiado");

            // Caminho elevado genérico: o maior script real (NVIDIA) cabe, e um comando maior que o limite falha antes do UAC.
            var nvidia = WindowsTools.CommandLength(WindowsTools.ElevatedArguments(WindowsTools.ElevatedWrapper(NvidiaAudioScriptChecks.Script(), @"C:\Users\x\AppData\Local\NexoSupport\pending\00000000000000000000000000000000\result.json")));
            var oversized = "";
            try { WindowsTools.EnsureElevatedFits(new string('x', 20000)); } catch (InvalidOperationException ex) { oversized = ex.Message; }
            check(nvidia < NetworkRecovery.CommandLineLimit && oversized.Contains("não foi iniciada") && oversized.Contains("32767"),
                "Elevação: o maior script real (NVIDIA, " + nvidia + " caracteres) cabe, e um comando acima do limite falha com mensagem clara");

            // A fase da janela vem do registro protegido, não de applied.json/decision.txt em %LOCALAPPDATA%.
            var phased = Transaction(startup, protectedRoot, 1, secureRecord: new NetworkTransactionRecord("set_ipv4", 7, Guid, DateTimeOffset.UtcNow, Phase: "applying"));
            File.WriteAllText(Path.Combine(phased, "applied.json"), "{}");
            File.WriteAllText(Path.Combine(phased, "decision.txt"), "rollback");
            var phasedItem = NetworkRecovery.Inspect(phased, DateTimeOffset.UtcNow, _ => false, trust: trust)!;
            check(phasedItem.Phase == "applying" && NetworkRecovery.Describe(phasedItem).Contains("durante a aplicação da mudança"),
                "Rede: a fase mostrada na restauração vem do registro protegido");

            // Resultado elevado que ficou só na pasta protegida (o aplicativo fechou antes de copiá-lo): é copiado e não há nova oferta.
            var copied = Transaction(startup, protectedRoot, 1);
            File.WriteAllText(Path.Combine(protectedRoot, Path.GetFileName(copied), NetworkRecovery.ResultFile), "{\"success\":true,\"restored\":true,\"message\":\"Configuração anterior restaurada e conferida.\"}");
            check(NetworkRecovery.Inspect(copied, DateTimeOffset.UtcNow, _ => false, trust: trust) == null && File.Exists(Path.Combine(copied, NetworkRecovery.ResultFile)),
                "Rede: resultado elevado pendente na pasta protegida é copiado e encerra a transação");

            await CheckResultsInProtectedFolder(check, store, trust, protectedRoot, startup, untrusted);
            CheckUserCreatedFolderOnWindows(check, root);
            CheckWritesOnInterfaceThread(check, trust, protectedRoot, startup);

            // O executor tira o caminho protegido da API do Windows e nunca reescreve uma ACL.
            using (var resource = typeof(NetworkRecovery).Assembly.GetManifestResourceStream("Nexo.NetworkTransaction.ps1")!)
            using (var reader = new StreamReader(resource))
            {
                var executor = reader.ReadToEnd();
                check(!executor.Contains("$env:ProgramData", StringComparison.OrdinalIgnoreCase) && executor.Contains("[Environment]::GetFolderPath('CommonApplicationData')")
                    && !executor.Contains("Set-Acl", StringComparison.OrdinalIgnoreCase) && executor.Contains("[IO.Directory]::CreateDirectory($path, $security)")
                    && System.Text.RegularExpressions.Regex.Match(executor, @"function Save-Protected\(.*?\n\}", System.Text.RegularExpressions.RegexOptions.Singleline).Value is var saveProtected
                    && saveProtected.Contains("Write-ProtectedFile") && !saveProtected.Contains("Set-Content") && executor.Contains("New-Object System.IO.FileStream($temporary, [IO.FileMode]::CreateNew, $rights, [IO.FileShare]::None, 4096, [IO.FileOptions]::None, $security)")
                    && executor.Contains("::Create((New-Object System.IO.FileInfo($temporary)), [IO.FileMode]::CreateNew, $rights, [IO.FileShare]::None, 4096, [IO.FileOptions]::None, $security)"),
                    "Rede: o executor usa a API do Windows para %ProgramData%, cria a pasta já protegida e não reescreve ACL");
                RealAcl(check, executor);
            }

            prompts.Clear();
            if (OperatingSystem.IsWindows())
            {
                var killed = await ApplyWithRealProcess(store, trust, protectedRoot, prompts);
                check(killed.Killed && prompts.Contains("Restaurar configuração de rede anterior?") && Flag(killed.Result, "reverted"),
                    "Rede: processo PowerShell real encerrado à força após o backup leva à restauração oferecida");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Ida e volta no Windows PowerShell 5.1 (#10): o carregador real descompacta os scripts reais com o mesmo SHA-256 e
    // eles passam pelo parser sem erros, sem executar. No GitHub Actions, pular é falha.
    private static async Task RoundTrip(Action<bool, string> check, string root, (string Label, string Script)[] scripts)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") check(false, "Rede: ida e volta do carregador no Windows PowerShell 5.1 precisa de Windows no CI");
            return;
        }
        foreach (var (label, script) in scripts)
        {
            var loader = NetworkRecovery.Compact(script);
            var inspect = loader[..^NetworkRecovery.LoaderRun.Length]
                + "$hash=[BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($nexoScript))).Replace('-',''); $errors=$null; "
                + "[void][System.Management.Automation.Language.Parser]::ParseInput($nexoScript,[ref]$null,[ref]$errors); Write-Output ($hash + ' ' + @($errors).Count + ' ' + $PSVersionTable.PSVersion.Major)";
            using var process = Process.Start(new ProcessStartInfo(WindowsTools.ElevatedShell, string.Join(' ', WindowsTools.ElevatedArguments(inspect))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(script))) + " 0 5";
            check(loader.EndsWith(NetworkRecovery.LoaderRun, StringComparison.Ordinal) && output == expected,
                "Rede: carregador do script de " + label + " no Windows PowerShell 5.1 devolve o mesmo SHA-256 e passa pelo parser sem erros (" + (output.Length > 0 ? output : error.Trim()) + ")");
        }
    }

    // O carregador compactado executa o script no mesmo escopo: variáveis do prefixo, funções e "return" no topo.
    private static async Task CompactRuns(Action<bool, string> check, string root)
    {
        var shell = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : Environment.GetEnvironmentVariable("NEXO_CHECKS_PWSH");
        if (string.IsNullOrEmpty(shell)) return;
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "compact-ok.txt");
        var script = "$requestJson='pedido'; $operationDir=" + "'" + output.Replace("'", "''") + "';\n"
            + "$ErrorActionPreference = 'Stop'\nfunction Save-Result($value) { Set-Content -LiteralPath $operationDir -Value $value -Encoding UTF8 }\n"
            + "if ($requestJson) { Save-Result ($requestJson + ' configuração ' + ('x' * 40000).Length); return }\nSave-Result 'return ignorado'\n";
        using var process = Process.Start(new ProcessStartInfo(shell, "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(NetworkRecovery.Compact(script)))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = File.Exists(output) ? File.ReadAllText(output).Trim() : "";
        check(process.ExitCode == 0 && text == "pedido configuração 40000", "Rede: carregador compactado executa o executor com o prefixo, as funções e o return (" + (text.Length > 0 ? text : error.Trim()) + ")");
    }

    // ACL de verdade (#10): só no Windows como administrador. No GitHub Actions, pular é falha.
    private static void RealAcl(Action<bool, string> check, string executor)
    {
        if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") check(false, "Rede: ACL real da pasta protegida precisa de Windows como administrador no CI");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "NexoAcl-" + System.Guid.NewGuid().ToString("N"));
        var nexo = Path.Combine(root, "NexoSupport");
        var operation = Path.Combine(nexo, "operations", System.Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            // As funções reais do executor criam a cadeia de pastas e os dois arquivos.
            var executorPath = Path.Combine(root, "executor.ps1");
            File.WriteAllText(executorPath, executor, new UTF8Encoding(true));
            var wrapperPath = Path.Combine(root, "acl.ps1");
            File.WriteAllText(wrapperPath, "$source=Get-Content -LiteralPath '" + executorPath.Replace("'", "''") + "' -Raw -Encoding UTF8\n"
                + "$ast=[System.Management.Automation.Language.Parser]::ParseInput($source,[ref]$null,[ref]$null)\n"
                + "$definitions=@(($source -split \"`r?`n\") | Where-Object {$_ -match '^\\$trustedSids = '})\n"
                + "foreach($function in 'Get-UntrustedReason','New-ProtectedSecurity','New-ProtectedDirectory','Assert-Trusted','Use-ProtectedDirectory','New-ProtectedFileSecurity','Write-ProtectedFile'){ $definitions+=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $function},$true).Extent.Text }\n"
                + ". ([scriptblock]::Create($definitions -join \"`n\"))\n"
                + "foreach($path in @('" + nexo.Replace("'", "''") + "','" + Path.Combine(nexo, "operations").Replace("'", "''") + "','" + operation.Replace("'", "''") + "')){ Use-ProtectedDirectory $path }\n"
                + "foreach($name in 'before.json','transaction.json','result.json'){ Write-ProtectedFile (Join-Path '" + operation.Replace("'", "''") + "' $name) '{}' }\n", new UTF8Encoding(true));
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + wrapperPath + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(60000);
            var reason = process.ExitCode == 0 ? NetworkRecovery.UntrustedReasonOnWindows(operation) : "executor saiu com " + process.ExitCode;
            check(reason == null, "Rede: pasta criada pelas funções reais do executor é aceita pela conferência de ACL do aplicativo" + (reason == null ? "" : " (" + reason + "; " + error.Trim() + ")"));
            check(NetworkRecovery.TrustedFileOnWindows(operation, "result.json"), "Rede: resultado gravado pelas funções reais do executor é aceito pela conferência por arquivo");
            var file = new FileInfo(Path.Combine(operation, "before.json"));
            var security = file.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify, AccessControlType.Allow));
            file.SetAccessControl(security);
            var refused = NetworkRecovery.UntrustedReasonOnWindows(operation);
            check(refused != null && refused.Contains("ACE de escrita"), "Rede: arquivo protegido com Users:Modify é recusado pela conferência de ACL do aplicativo (" + refused + ")");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task CheckResultsInProtectedFolder(Action<bool, string> check, Store store, NetworkRecoveryTrust trust, string protectedRoot, string startup, HashSet<string> untrusted)
    {
        var prompts = new List<string>();
        var plantedInUserFolder = await Apply(store, trust, async (dir, _) =>
        {
            await Write(dir, "result.json", "{\"success\":true,\"message\":\"PLANTADO\"}");
            await Backup(dir, protectedRoot, Before);
            await Write(Secured(protectedRoot, dir), "result.json", "{\"success\":false,\"reverted\":true,\"message\":\"Configuração anterior restaurada por cancelamento ou ausência de confirmação.\"}");
        }, prompts, true);
        check(Flag(plantedInUserFolder.Result, "reverted") && !plantedInUserFolder.Result.ToString().Contains("PLANTADO")
            && File.ReadAllText(Path.Combine(plantedInUserFolder.Directory, "result.json")).Contains("restaurada por cancelamento"),
            "Rede: resultado plantado na pasta do usuário é ignorado; vale o da pasta protegida, copiado pelo aplicativo sem elevação");

        var distrusted = await Apply(store, trust, async (dir, _) =>
        {
            untrusted.Add(Path.GetFileName(dir));
            await Backup(dir, protectedRoot, Before);
            await Write(Secured(protectedRoot, dir), "result.json", "{\"success\":true,\"message\":\"PLANTADO\"}");
        }, prompts, true);
        check(!Flag(distrusted.Result, "success") && Text(distrusted.Result, "state") == "unknown" && Text(distrusted.Result, "error").Contains("permissões inesperados")
            && Text(distrusted.Result, "backup").EndsWith("before.json") && !File.Exists(Path.Combine(distrusted.Directory, "result.json")),
            "Rede: resultado em pasta protegida com dono ou permissões inesperados é ignorado e fica como estado desconhecido");

        // applied.json numa pasta protegida não confiável não abre "Manter a configuração de rede?": a espera termina no
        // prazo com estado desconhecido e a restauração é pedida ao executor.
        prompts.Clear();
        var quick = new NetworkDeadlines(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var distrustedApplied = await Apply(store, trust, async (dir, stop) =>
        {
            untrusted.Add(Path.GetFileName(dir));
            await Backup(dir, protectedRoot, Before);
            await Write(Secured(protectedRoot, dir), "applied.json", "{\"Dns\":\"203.0.113.66\"}");
            await WaitFor(Path.Combine(dir, "decision.txt"), stop);
            await Task.Delay(Timeout.Infinite, stop);
        }, prompts, true, keep: true, deadlines: quick);
        check(prompts.Count == 0 && !Flag(distrustedApplied.Result, "success") && Text(distrustedApplied.Result, "state") == "unknown" && Decision(distrustedApplied.Directory) == "rollback",
            "Rede: estado aplicado em pasta protegida não confiável não abre a confirmação e termina como estado desconhecido");

        var finished = Transaction(startup, protectedRoot, 1);
        File.WriteAllText(Path.Combine(Secured(protectedRoot, finished), "result.json"), "{\"success\":true,\"message\":\"Configuração aplicada, estado consultado e permanência confirmada pelo usuário.\"}");
        check(NetworkRecovery.Inspect(finished, DateTimeOffset.UtcNow, _ => false, trust: trust) == null
            && File.ReadAllText(Path.Combine(finished, "result.json")).Contains("permanência confirmada"),
            "Rede: resultado que ficou só na pasta protegida é copiado na abertura e não gera oferta de restauração");

        var distrustedPending = Transaction(startup, protectedRoot, 1);
        File.WriteAllText(Path.Combine(Secured(protectedRoot, distrustedPending), "result.json"), "{\"success\":true,\"message\":\"PLANTADO\"}");
        untrusted.Add(Path.GetFileName(distrustedPending));
        var distrustedItem = NetworkRecovery.Inspect(distrustedPending, DateTimeOffset.UtcNow, _ => false, trust: trust);
        check(distrustedItem?.Resolution != null && !File.Exists(Path.Combine(distrustedPending, "result.json")) && !distrustedItem.Resolution.Contains("PLANTADO"),
            "Rede: resultado pendente em pasta protegida não confiável não é copiado na abertura");
    }

    private static void CheckUserCreatedFolderOnWindows(Action<bool, string> check, string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        var userCreated = Path.Combine(root, "user-created", "NexoSupport", "operations", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userCreated);
        File.WriteAllText(Path.Combine(userCreated, "result.json"), "{\"success\":true}");
        var reason = NetworkRecovery.UntrustedReasonOnWindows(userCreated, ["result.json"]);
        check(reason != null && !NetworkRecovery.TrustedFileOnWindows(userCreated, "result.json"),
            "Rede: resultado numa pasta criada sem elevação é recusado pela conferência real de ACL (" + reason + ")");
    }

    private static void CheckWritesOnInterfaceThread(Action<bool, string> check, NetworkRecoveryTrust trust, string protectedRoot, string startup)
    {
        var recorded = Path.Combine(startup, "operations", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(recorded);
        var recordWritten = CompletesOnInterfaceThread(() => NetworkRecovery.WriteRecord(recorded, new NetworkTransactionRecord("set_dns", 7, Guid, DateTimeOffset.UtcNow)));
        check(recordWritten && File.Exists(Path.Combine(recorded, NetworkRecovery.RecordFile)),
            "Rede: registro da transação é gravado na thread da interface sem esperar por ela (sem travar depois da autorização)");

        var pending = Transaction(startup, protectedRoot, 1);
        File.WriteAllText(Path.Combine(Secured(protectedRoot, pending), "result.json"), "{\"success\":false,\"reverted\":true}");
        var copied = CompletesOnInterfaceThread(() => NetworkRecovery.Inspect(pending, DateTimeOffset.UtcNow, _ => false, trust: trust));
        check(copied && File.Exists(Path.Combine(pending, "result.json")),
            "Rede: cópia do resultado protegido na abertura do Nexo roda na thread da interface sem travar");
    }

    private static bool CompletesOnInterfaceThread(Action action)
    {
        var completed = false;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new UnpumpedContext());
            action();
            completed = true;
        }) { IsBackground = true };
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(10)) && completed;
    }

    private sealed class UnpumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) { }
    }

    private sealed record Outcome(JsonElement Result, string Directory, int RecoveryStarts, string RecoveryScript, string ForwardScript = "", bool Killed = false);

    // #97: a conferência real da cadeia %ProgramData%\NexoSupport\operations, com pastas criadas sem elevação e com junção.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task HostileChainOnWindows(Action<bool, string> check, string root)
    {
        var chainRoot = Path.Combine(root, "chain-" + System.Guid.NewGuid().ToString("N"));
        var missing = Path.Combine(chainRoot, "ausente", "NexoSupport", "operations");
        check(NetworkRecovery.ProtectedChainProblemOnWindows(missing) == null, "Rede: cadeia NexoSupport ainda inexistente não é recusada; o executor a cria (#97)");
        var fileParent = Path.Combine(chainRoot, "arquivo", "NexoSupport");
        Directory.CreateDirectory(Path.GetDirectoryName(fileParent)!);
        File.WriteAllText(fileParent, "ocupado");
        check(NetworkRecovery.ProtectedChainProblemOnWindows(Path.Combine(fileParent, "operations"))?.Contains("não é pasta") == true,
            "Rede: arquivo no lugar da pasta NexoSupport é diagnosticado (#97)");
        check(NetworkRecovery.HostileChainMessage(fileParent + ": não é pasta").Contains("arquivo")
            && !NetworkRecovery.HostileChainMessage(fileParent + ": não é pasta").Contains("rmdir /S"),
            "Rede: arquivo no caminho recebe orientação própria, sem comando de remover pasta (#97)");

        var plain = Path.Combine(chainRoot, "comum", "NexoSupport", "operations");
        Directory.CreateDirectory(plain);
        var plainReason = NetworkRecovery.ProtectedChainProblemOnWindows(plain);
        check(plainReason != null && plainReason.StartsWith(Path.Combine(chainRoot, "comum", "NexoSupport") + ":") && (plainReason.Contains("herda permissões") || plainReason.Contains("dono não confiável") || plainReason.Contains("ACE de escrita")),
            "Rede: pasta NexoSupport criada sem elevação é recusada com o motivo (" + plainReason + ") (#97)");

        var target = Path.Combine(chainRoot, "destino");
        Directory.CreateDirectory(Path.Combine(target, "operations"));
        var linkParent = Path.Combine(chainRoot, "link");
        Directory.CreateDirectory(linkParent);
        var junction = Path.Combine(linkParent, "NexoSupport");
        using (var mklink = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c mklink /J \"" + junction + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await mklink.StandardOutput.ReadToEndAsync(); await mklink.StandardError.ReadToEndAsync(); await mklink.WaitForExitAsync();
        }
        var linkReason = NetworkRecovery.ProtectedChainProblemOnWindows(Path.Combine(junction, "operations"));
        check(linkReason == junction + ": é link (reparse point)", "Rede: pasta NexoSupport que é junção é recusada como link (#97)");
        Directory.Delete(junction);
        check(Directory.Exists(Path.Combine(target, "operations")), "Rede: o teste da junção não apaga o destino");
    }

    private static async Task<Outcome> Apply(Store store, NetworkRecoveryTrust trust, Func<string, CancellationToken, Task> executor, List<string> prompts, bool restore, bool keep = false, bool recoveryWrites = true, NetworkDeadlines? deadlines = null, CancellationToken token = default, bool unanswered = false)
    {
        string directory = "";
        var starts = 0;
        var recoveryScript = "";
        var forwardScript = "";
        Func<string, IElevatedProcess> start = script =>
        {
            var dir = OperationDir(script);
            if (script.Contains("$recoveryMode=$true"))
            {
                starts++; recoveryScript = script;
                // O processo elevado grava o resultado só na pasta protegida (#10).
                return new SimulatedProcess(async _ => { if (recoveryWrites) await Write(Path.Combine(trust.ProtectedRoot, Path.GetFileName(dir)), NetworkRecovery.ResultFile, "{\"success\":true,\"restored\":true,\"message\":\"Configuração anterior restaurada e conferida.\"}"); });
            }
            directory = dir; forwardScript = script;
            return new SimulatedProcess(stop => executor(dir, stop));
        };
        Func<ChangeRequest, Task<bool>> confirm = request => { prompts.Add(request.Title); return unanswered && request.KeepNetwork ? new TaskCompletionSource<bool>().Task : Task.FromResult(request.KeepNetwork ? keep : restore); };
        var output = await NetworkChange.Apply(Request, store, confirm, new Silent(), token, start, deadlines, trust);
        using var document = JsonDocument.Parse(output);
        return new Outcome(document.RootElement.Clone(), directory, starts, recoveryScript, forwardScript);
    }

    private static async Task<Outcome> ApplyWithRealProcess(Store store, NetworkRecoveryTrust trust, string protectedRoot, List<string> prompts)
    {
        var killed = false;
        string directory = "";
        Func<string, IElevatedProcess> start = script =>
        {
            var dir = OperationDir(script);
            if (script.Contains("$recoveryMode=$true")) return new SimulatedProcess(async _ => await Write(Path.Combine(protectedRoot, Path.GetFileName(dir)), NetworkRecovery.ResultFile, "{\"success\":true,\"restored\":true}"));
            directory = dir;
            var secured = Path.Combine(protectedRoot, Path.GetFileName(dir));
            Directory.CreateDirectory(secured);
            File.WriteAllText(Path.Combine(secured, NetworkRecovery.RecordFile), Record(DateTimeOffset.UtcNow));
            var child = "Set-Content -LiteralPath '" + Path.Combine(secured, "before.json").Replace("'", "''") + "' -Value '" + Before.Replace("'", "''") + "'; Start-Sleep -Seconds 60";
            var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(child))) { UseShellExecute = false, CreateNoWindow = true })!;
            _ = Task.Run(async () =>
            {
                var clock = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(secured, "before.json")) && clock.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
                process.Kill(true); killed = true;
            });
            return new RealProcess(process);
        };
        Func<ChangeRequest, Task<bool>> confirm = request => { prompts.Add(request.Title); return Task.FromResult(!request.KeepNetwork); };
        var output = await NetworkChange.Apply(Request, store, confirm, new Silent(), CancellationToken.None, start, trust: trust);
        using var document = JsonDocument.Parse(output);
        return new Outcome(document.RootElement.Clone(), directory, 0, "", "", killed);
    }

    private static async Task Backup(string dir, string protectedRoot, string before)
    {
        var secured = Secured(protectedRoot, dir);
        Directory.CreateDirectory(secured);
        await Write(secured, NetworkRecovery.RecordFile, Record(DateTimeOffset.UtcNow));
        await Write(secured, "before.json", before);
    }

    private static string Secured(string protectedRoot, string dir) => Path.Combine(protectedRoot, Path.GetFileName(dir));

    private static string Record(DateTimeOffset created) => JsonSerializer.Serialize(new { Action = "set_ipv4", Index = 7, ExpectedGuid = Guid, Created = created.ToString("o") });

    private static string Transaction(string root, string protectedRoot, int? pid, DateTimeOffset? created = null, string? before = null, bool secure = true, NetworkTransactionRecord? secureRecord = null)
    {
        var id = System.Guid.NewGuid().ToString("N");
        var dir = Path.Combine(root, "operations", id);
        Directory.CreateDirectory(dir);
        var when = created ?? DateTimeOffset.UtcNow;
        NetworkRecovery.WriteRecord(dir, new NetworkTransactionRecord("set_ipv4", 7, Guid, when, pid));
        File.WriteAllText(Path.Combine(dir, "before.json"), before ?? Before);
        if (secure)
        {
            var secured = Path.Combine(protectedRoot, id);
            Directory.CreateDirectory(secured);
            File.WriteAllText(Path.Combine(secured, NetworkRecovery.RecordFile), secureRecord is { } record ? JsonSerializer.Serialize(record) : Record(when));
            File.WriteAllText(Path.Combine(secured, "before.json"), before ?? Before);
        }
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
