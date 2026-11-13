using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexo.Support;

public sealed record NetworkRequest(string Action, int Index, string Mode, string[] Servers, string Address, int Prefix, string Gateway)
{
    public string ExpectedGuid { get; init; } = "";
    public string Description => Action == "set_dns" ? $"Interface #{Index}: DNS {(Mode == "automatic" ? "automático (DHCP)" : string.Join(", ", Servers))}." : $"Interface #{Index}: {(Mode == "dhcp" ? "Obter um endereço IP automaticamente (DHCP). A rede precisa fornecer IP por DHCP; confirme se essa interface deve usar configuração automática" : $"IP {Address}/{Prefix}, gateway {(Gateway.Length == 0 ? "sem gateway" : Gateway)}")}. O DNS será preservado.";
}
public sealed record NetworkDeadlines(TimeSpan Applying, TimeSpan Confirming, TimeSpan Finishing)
{
    public static readonly NetworkDeadlines Default = new(TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90));
    public TimeSpan Total => Applying + Confirming + Finishing;
}
public static class NetworkChange
{
    public const string UnconfirmedFile = "unconfirmed.json";

    public static NetworkRequest Parse(string action, JsonElement args)
    {
        var index = WindowsTools.Number(args, "interface_index");
        if (index <= 0) throw new InvalidOperationException("Índice de interface inválido.");
        var mode = WindowsTools.Value(args, "mode");
        if (action == "set_dns")
        {
            if (mode is not ("manual" or "automatic")) throw new InvalidOperationException("Modo DNS inválido.");
            var servers = args.TryGetProperty("servers", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(s => WindowsTools.Ipv4(s.GetString() ?? "")).Distinct().ToArray() : mode == "automatic" ? [] : throw new InvalidOperationException("DNS manual exige servers com os endereços informados pelo usuário. Nenhum ajuste foi realizado.");
            if (mode == "manual" && servers.Length is < 1 or > 3 || mode == "automatic" && servers.Length != 0) throw new InvalidOperationException("Informe de 1 a 3 servidores no modo manual ou uma lista vazia no automático.");
            return new NetworkRequest(action, index, mode, servers, "", 0, "");
        }
        if (action != "set_ipv4" || mode is not ("static" or "dhcp")) throw new InvalidOperationException("Modo IPv4 inválido.");
        var address = WindowsTools.Value(args, "address"); var gateway = WindowsTools.Value(args, "gateway"); var prefix = WindowsTools.Number(args, "prefix");
        if (mode == "static") WindowsTools.ValidateStatic(address, prefix, gateway);
        else if (address.Length > 0 || gateway.Length > 0 || prefix != 0) throw new InvalidOperationException("Para DHCP, deixe IP e gateway vazios e prefixo 0.");
        return new NetworkRequest(action, index, mode, [], address, prefix, gateway);
    }

    public static Task<string> Apply(NetworkRequest request, Store store, Func<ChangeRequest, Task<bool>> confirm, IProgress<string> progress, CancellationToken token)
        => Apply(request, store, confirm, progress, token, NetworkRecovery.StartElevated);

    public static async Task<string> Apply(NetworkRequest request, Store store, Func<ChangeRequest, Task<bool>> confirm, IProgress<string> progress, CancellationToken token, Func<string, IElevatedProcess> start, NetworkDeadlines? deadlines = null, NetworkRecoveryTrust? trust = null)
    {
        deadlines ??= NetworkDeadlines.Default;
        trust ??= NetworkRecoveryTrust.Default;
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(store.Root, "operations", id); Directory.CreateDirectory(dir);
        var parameters = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
        using var source = typeof(NetworkChange).Assembly.GetManifestResourceStream("Nexo.NetworkTransaction.ps1") ?? throw new IOException("Executor de rede ausente.");
        using var reader = new StreamReader(source);
        var script = "$requestJson=[System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + parameters + "')); $operationId=" + WindowsTools.Quote(id) + "; $operationDir=" + WindowsTools.Quote(dir) + ";\n" + await reader.ReadToEndAsync(token);
        token.ThrowIfCancellationRequested();
        var record = new NetworkTransactionRecord(request.Action, request.Index, request.ExpectedGuid, DateTimeOffset.UtcNow);
        NetworkRecovery.WriteRecord(dir, record);
        IElevatedProcess process;
        // UAC recusado (Win32 1223) ou falha ao iniciar: nada foi alterado, e a abertura seguinte não pode mostrar isso
        // como "alteração de rede que não terminou".
        try { process = start(script); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            try { NetworkRecovery.MarkNotStarted(dir, ex.Message); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        using var started = process;
        NetworkRecovery.WriteRecord(dir, record with { ProcessId = process.Id });
        var secured = Path.Combine(trust.ProtectedRoot, id);
        var ready = Path.Combine(secured, "applied.json"); var final = Path.Combine(secured, "result.json"); var decision = Path.Combine(dir, "decision.txt");
        var backup = Path.Combine(secured, "before.json");
        var clock = Stopwatch.StartNew();
        var deadline = deadlines.Applying;
        var cancelled = false;
        var restoreChosen = false;
        while (!process.HasExited && !File.Exists(final))
        {
            if (token.IsCancellationRequested && !cancelled)
            {
                cancelled = true;
                if (!File.Exists(decision)) await File.WriteAllTextAsync(decision, "rollback", CancellationToken.None);
                if (!File.Exists(ready)) deadline = deadlines.Applying + deadlines.Finishing;
            }
            if (File.Exists(ready) && !File.Exists(decision) && trust.TrustsFile(secured, "applied.json"))
            {
                progress.Report("Configuração aplicada temporariamente. Conferindo o estado e aguardando sua confirmação…");
                var state = await File.ReadAllTextAsync(ready, CancellationToken.None);
                var keep = false;
                // Os 45 s do texto são os da janela (MainWindow), que grava "rollback" ao expirar. Os 60 s do
                // NetworkTransaction.ps1 são o prazo do processo elevado se o aplicativo não responder (README, Rede e recuperação).
                try
                {
                    if (!token.IsCancellationRequested)
                    {
                        var explicitRestoreSelected = false;
                        var answer = confirm(new ChangeRequest("Manter a configuração de rede?", request.Description + "\n\nEstado obtido após a mudança:\n" + state + "\n\nTeste a conexão. Se não confirmar em até 45 segundos, a configuração anterior será restaurada.", true)
                        { OnExplicitRestore = () => explicitRestoreSelected = true });
                        // O cancelamento do atendimento também encerra a espera pela janela: a decisão vira "rollback".
                        if (await Task.WhenAny(answer, Task.Delay(deadlines.Confirming, token)) == answer) { keep = await answer; restoreChosen = !keep && explicitRestoreSelected && !token.IsCancellationRequested; }
                        else progress.Report(token.IsCancellationRequested ? "Cancelamento solicitado; restaurando a configuração anterior." : "A confirmação não respondeu no prazo; restaurando a configuração anterior.");
                    }
                }
                catch (Exception ex) { progress.Report("Confirmação indisponível; restaurando a configuração anterior. " + ex.Message); }
                await File.WriteAllTextAsync(decision, keep && !token.IsCancellationRequested ? "keep" : "rollback", CancellationToken.None);
                deadline = clock.Elapsed + deadlines.Finishing;
            }
            if (clock.Elapsed >= deadline && !process.HasExited && !File.Exists(final)) return await Unconfirmed(dir, backup, decision, ready, cancelled || token.IsCancellationRequested, clock.Elapsed);
            await Task.Delay(250, CancellationToken.None);
        }
        if (File.Exists(final)) return MarkRestoreChosen(await ReadResult(dir, secured, trust), restoreChosen);
        await process.WaitForExitAsync(CancellationToken.None);
        if (File.Exists(final)) return MarkRestoreChosen(await ReadResult(dir, secured, trust), restoreChosen);
        var interrupted = NetworkRecovery.Inspect(dir, DateTimeOffset.UtcNow, _ => false, exited: true, trust: trust);
        if (interrupted == null) return JsonSerializer.Serialize(new { success = false, reverted = false, error = "Executor encerrado sem resultado. Verifique a rede; estado não confirmado.", backup, recoveryFolder = dir });
        if (interrupted.Resolution == null) progress.Report("O executor de rede terminou sem resultado. Oferecendo a restauração da configuração anterior…");
        var recovery = await NetworkRecovery.Recover(interrupted, confirm, start, NetworkRecovery.DefaultTimeout, CancellationToken.None);
        using var recoveryResult = JsonDocument.Parse(recovery);
        var unchanged = recoveryResult.RootElement.TryGetProperty("unchanged", out var flag) && flag.ValueKind == JsonValueKind.True;
        return JsonSerializer.Serialize(new { success = false, changed = !unchanged, reverted = NetworkRecovery.Restored(recovery), error = "Executor encerrado sem resultado. " + NetworkRecovery.Explain(recovery), backup, recoveryFolder = dir, recovery = recoveryResult.RootElement.Clone() });
    }

    // #98: o executor grava a mesma mensagem para "restaurar" escolhido na janela, prazo esgotado e cancelamento. Só o
    // aplicativo sabe que o usuário escolheu restaurar; o resultado ganha restoredByUser quando a restauração foi conferida.
    internal static string MarkRestoreChosen(string result, bool restoreChosen)
    {
        if (!restoreChosen) return result;
        try
        {
            if (JsonNode.Parse(result) is not JsonObject root) return result;
            if (root["success"]?.GetValueKind() != JsonValueKind.False || root["reverted"]?.GetValueKind() != JsonValueKind.True
                || root.ContainsKey("error") || root.ContainsKey("failureState")) return result;
            root["restoredByUser"] = true;
            return root.ToJsonString();
        }
        catch (JsonException) { return result; }
    }

    private static async Task<string> ReadResult(string dir, string secured, NetworkRecoveryTrust trust)
    {
        if (!trust.TrustsFile(secured, "result.json"))
            return JsonSerializer.Serialize(new { success = false, reverted = false, state = "unknown", error = "O resultado do executor de rede está numa pasta com dono ou permissões inesperados e foi ignorado. O estado da rede não pôde ser confirmado; consulte a rede antes de outra alteração.", backup = Path.Combine(secured, "before.json"), recoveryFolder = dir });
        var text = await File.ReadAllTextAsync(Path.Combine(secured, "result.json"), CancellationToken.None);
        try { await NetworkRecovery.WriteTextAtomic(Path.Combine(dir, "result.json"), text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return text;
    }

    private static async Task<string> Unconfirmed(string dir, string backup, string decision, string ready, bool cancelled, TimeSpan elapsed)
    {
        if (!File.Exists(decision)) await File.WriteAllTextAsync(decision, "rollback", CancellationToken.None);
        var chosen = File.ReadAllText(decision).Trim();
        var applied = File.Exists(ready);
        var phase = chosen == "keep" ? "keeping" : applied ? "restoring" : "applying";
        var seconds = (int)Math.Round(elapsed.TotalSeconds);
        var error = (cancelled ? "Cancelamento registrado. " : "") + phase switch
        {
            "keeping" => $"Você confirmou manter a nova configuração, mas o executor de rede não registrou o resultado em {seconds} segundos e ainda está em execução. A configuração atual não foi conferida; consulte a rede antes de outra alteração.",
            "restoring" => $"A restauração da configuração anterior não terminou em {seconds} segundos e o executor de rede ainda está em execução. Não há confirmação de que a configuração anterior voltou; o estado da rede não pôde ser confirmado.",
            _ => $"O executor de rede não chegou à confirmação em {seconds} segundos e ainda está em execução. A mudança pode ter sido aplicada parcialmente; o estado da rede não pôde ser confirmado e nenhuma restauração foi confirmada. Se o executor ainda chegar à confirmação, ele restaura a configuração anterior."
        } + " Se o executor terminar sem resultado, a restauração será oferecida na próxima abertura do Nexo.";
        var outcome = JsonSerializer.Serialize(new { success = false, reverted = false, state = "unknown", timedOut = true, processRunning = true, cancelled, phase, error, backup, recoveryFolder = dir, recordedAt = DateTimeOffset.UtcNow });
        var temporary = Path.Combine(dir, UnconfirmedFile + ".tmp");
        await File.WriteAllTextAsync(temporary, outcome, CancellationToken.None);
        File.Move(temporary, Path.Combine(dir, UnconfirmedFile), true);
        return outcome;
    }
}
