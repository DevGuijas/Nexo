using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Nexo.Support;

public enum DebloatStatus { Ok, Partial, NotFound, Failed, NotApplied }
public enum DebloatAvailability { Available, AlreadyApplied, NeedsConfirmation }

public sealed record DebloatInstalledApp(string Name, string FullName, bool NonRemovable);
public sealed record DebloatServiceState(string Name, string StartType, bool Running);
public sealed record DebloatTaskState(string Path, string Name, string State);
public sealed record DebloatInventory(IReadOnlyList<DebloatInstalledApp> Apps, IReadOnlyList<DebloatServiceState> Services,
    IReadOnlyList<DebloatTaskState> Tasks, string SystemDisk, string? Recall);
public sealed record DebloatRegistryState(bool Exists, DebloatValueKind Kind, string Value)
{
    public static readonly DebloatRegistryState Absent = new(false, DebloatValueKind.DWord, "");
}

public sealed record DebloatPreviewItem(DebloatItem Item, DebloatAvailability Availability, IReadOnlyList<string> PresentTargets, string Detail);
public sealed record DebloatPreview(int Build, DebloatInventory Inventory, IReadOnlyList<DebloatPreviewItem> Items);
public sealed record DebloatOutcome(string Id, string Title, DebloatStatus Status, string Detail);
public sealed record DebloatResult(string Status, RecoveryPointResult? RestorePoint, IReadOnlyList<DebloatOutcome> Items, string? RunId);
public sealed record DebloatAudit(DateTimeOffset At, string Action, string Target, string Outcome, string Detail = "");

/// <summary>Estado anterior de um alvo, para o Desfazer. Para apps, só registra o que foi removido (volta pela Microsoft Store).</summary>
public sealed record DebloatUndoEntry(string ItemId, DebloatKind Kind, string Target, string Previous);
public sealed class DebloatRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; set; }
    public int Build { get; set; }
    public int? RestorePoint { get; set; }
    public List<DebloatUndoEntry> Entries { get; set; } = [];
    public DateTimeOffset? UndoneAt { get; set; }
    public bool CanUndo => UndoneAt == null && Entries.Count > 0;
}

/// <summary>Leitura e escrita no HKCU do usuário do Nexo. O processo não é elevado, então os ajustes caem no perfil atendido.</summary>
public interface IDebloatRegistry
{
    DebloatRegistryState Read(string key, string name);
    void Write(string key, string name, DebloatValueKind kind, string value);
    void Delete(string key, string name);
    void NotifySettingsChanged();
}

/// <summary>Executa os scripts do Nexo: sem elevação (Windows PowerShell 5.1) ou pelo executor elevado supervisionado.</summary>
public interface IDebloatSystem
{
    int Build { get; }
    IDebloatRegistry Registry { get; }
    Task<string> RunUserScriptAsync(string script, int timeoutSeconds, CancellationToken token);
    /// <summary>Devolve {"success":true,"data":...} ou {"success":false,"error":...}; UAC cancelado lança Win32Exception 1223.</summary>
    Task<string> RunElevatedAsync(string script, CancellationToken token);
}

/// <summary>Guarda as aplicações e o estado anterior em JSON na pasta do Nexo. Arquivo ilegível vira lista vazia.</summary>
public sealed class DebloatUndoStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Path { get; } = path;
    public List<DebloatRun> Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<List<DebloatRun>>(File.ReadAllText(Path)) ?? [] : []; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return []; }
    }
    public DebloatRun? Latest() => Load().OrderByDescending(run => run.At).FirstOrDefault();
    public void Save(DebloatRun run)
    {
        var runs = Load().Where(item => item.Id != run.Id).Append(run).OrderByDescending(item => item.At).Take(20).ToList();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(runs, Json));
        File.Move(temp, Path, true);
    }
}

/// <summary>
/// Remoção de apps e ajustes do menu Ferramentas (#127). Mostra o que fará, cria e confirma um ponto de restauração,
/// aplica e guarda o estado anterior de cada item. Não é exposta ao modelo e não promete ganho de desempenho.
/// </summary>
public sealed class DebloatService
{
    private readonly IDebloatSystem system;
    private readonly DebloatUndoStore undo;
    private readonly Action<DebloatAudit> audit;
    private readonly TimeProvider time;
    private static int running;

    public DebloatService(IDebloatSystem system, DebloatUndoStore undo, Action<DebloatAudit> audit, TimeProvider? time = null)
    {
        this.system = system ?? throw new ArgumentNullException(nameof(system));
        this.undo = undo ?? throw new ArgumentNullException(nameof(undo));
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.time = time ?? TimeProvider.System;
    }

    public const string RestorePointDescription = "Nexo antes de remover apps e ajustes";
    public const string StoreGuidance = "Para reinstalar, abra a Microsoft Store e procure o app pelo nome.";

    public async Task<DebloatPreview> PreviewAsync(CancellationToken token)
    {
        var build = system.Build;
        var items = DebloatCatalog.ForBuild(build);
        var json = await system.RunUserScriptAsync(DebloatScripts.Inventory(items), 120, token);
        var inventory = DebloatScripts.ParseInventory(json);
        return new(build, inventory, Preview(build, inventory, system.Registry));
    }

    /// <summary>Itens do catálogo deste build que existem no PC. Pacote, serviço, tarefa ou recurso ausente não aparece.</summary>
    public static IReadOnlyList<DebloatPreviewItem> Preview(int build, DebloatInventory inventory, IDebloatRegistry registry)
    {
        var result = new List<DebloatPreviewItem>();
        foreach (var item in DebloatCatalog.ForBuild(build))
        {
            switch (item.Kind)
            {
                case DebloatKind.App:
                {
                    var present = item.Targets.Where(target => inventory.Apps.Any(app => Same(app.Name, target) && !app.NonRemovable)).ToArray();
                    if (present.Length == 0) continue;
                    if (item.Gate == DebloatGate.XboxGames)
                    {
                        var games = DebloatCatalog.XboxGameIndicators.Where(name => inventory.Apps.Any(app => Same(app.Name, name))).ToArray();
                        if (games.Length > 0)
                        {
                            result.Add(new(item, DebloatAvailability.NeedsConfirmation, present,
                                $"Este PC tem {string.Join(", ", games)}. {item.Warning} Só é removido com a sua confirmação explícita."));
                            continue;
                        }
                    }
                    result.Add(new(item, DebloatAvailability.Available, present, item.Warning));
                    break;
                }
                case DebloatKind.Service:
                {
                    if (item.Gate == DebloatGate.SystemSsd && !Same(inventory.SystemDisk, "SSD")) continue;
                    var present = inventory.Services.Where(service => item.Targets.Any(target => Same(target, service.Name))).ToArray();
                    if (present.Length == 0) continue;
                    var applied = present.All(service => Same(service.StartType, "Disabled"));
                    result.Add(new(item, applied ? DebloatAvailability.AlreadyApplied : DebloatAvailability.Available, present.Select(s => s.Name).ToArray(),
                        applied ? "Já está desativado." : item.Warning));
                    break;
                }
                case DebloatKind.ScheduledTask:
                {
                    var present = inventory.Tasks.Where(task => item.Targets.Contains(task.Path + "|" + task.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
                    if (present.Length == 0) continue;
                    var applied = present.All(task => Same(task.State, "Disabled"));
                    result.Add(new(item, applied ? DebloatAvailability.AlreadyApplied : DebloatAvailability.Available, present.Select(t => t.Path + "|" + t.Name).ToArray(),
                        applied ? "Já estão desativadas." : item.Warning));
                    break;
                }
                case DebloatKind.OptionalFeature:
                {
                    if (inventory.Recall is null or "Unknown") continue;
                    var applied = Same(inventory.Recall, "Disabled");
                    result.Add(new(item, applied ? DebloatAvailability.AlreadyApplied : DebloatAvailability.Available, item.Targets,
                        applied ? "Já está desativado." : item.Warning));
                    break;
                }
                case DebloatKind.UserRegistry:
                {
                    var applied = item.Values.All(value => Matches(registry.Read(value.Key, value.Name), value));
                    result.Add(new(item, applied ? DebloatAvailability.AlreadyApplied : DebloatAvailability.Available,
                        item.Values.Select(v => v.Key + "|" + v.Name).ToArray(), applied ? "Já está aplicado." : item.Warning));
                    break;
                }
            }
        }
        return result;
    }

    public async Task<DebloatResult> ApplyAsync(DebloatPreview preview, IReadOnlyCollection<string> selected, bool riskConfirmed,
        IProgress<string>? progress, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("Já existe uma remoção de apps e ajustes em execução.");
        var outcomes = new List<DebloatOutcome>();
        var run = new DebloatRun { At = time.GetUtcNow(), Build = preview.Build };
        RecoveryPointResult? restore = null;
        var status = "Completed";
        try
        {
            var chosen = new List<DebloatPreviewItem>();
            foreach (var id in selected.Distinct(StringComparer.Ordinal))
            {
                var item = preview.Items.FirstOrDefault(candidate => candidate.Item.Id == id);
                var catalog = DebloatCatalog.Find(id);
                if (item == null) { outcomes.Add(new(id, catalog?.Title ?? id, DebloatStatus.NotApplied, "Item indisponível neste PC.")); continue; }
                if (item.Availability == DebloatAvailability.AlreadyApplied) { outcomes.Add(new(id, item.Item.Title, DebloatStatus.NotApplied, "Já estava aplicado; nada foi alterado.")); continue; }
                if (item.Availability == DebloatAvailability.NeedsConfirmation && !riskConfirmed)
                { outcomes.Add(new(id, item.Item.Title, DebloatStatus.NotApplied, "Bloqueado: confirme o aviso do item para removê-lo.")); continue; }
                chosen.Add(item);
            }
            Log("Debloat", string.Join(",", chosen.Select(c => c.Item.Id)), "Started");
            token.ThrowIfCancellationRequested();

            // 1) Ponto de restauração e itens elevados num único UAC. Sem ponto confirmado, nenhum item elevado roda.
            var elevated = chosen.Where(c => c.Item.Elevated).ToList();
            progress?.Report("Criando o ponto de restauração. O Windows pode pedir autorização de administrador.");
            Log("RestorePoint", "", "Requested");
            var (point, results, error) = await RunElevated(DebloatScripts.Apply(elevated.Select(e => e.Item)), token);
            restore = point;
            Log("RestorePoint", point.SequenceNumber?.ToString(CultureInfo.InvariantCulture) ?? "", point.Ready ? "Confirmed" : "NotConfirmed", point.Reason);
            foreach (var item in elevated)
            {
                if (!point.Ready || results == null)
                {
                    Add(item.Item, DebloatStatus.NotApplied, "Ponto de restauração não confirmado (" + (error ?? point.Reason) + "); serviços, tarefas e recursos não foram alterados.");
                    continue;
                }
                var mine = results.Where(r => r.Id == item.Item.Id).ToArray();
                foreach (var r in mine.Where(r => r.Status is "Ok" or "Partial" && r.Previous.Length > 0))
                    run.Entries.Add(new(item.Item.Id, item.Item.Kind, r.Target, r.Previous));
                Add(item.Item, Aggregate(mine.Select(r => r.Status)), Details(mine));
            }
            run.RestorePoint = point.SequenceNumber;
            if (run.Entries.Count > 0) undo.Save(run);
            token.ThrowIfCancellationRequested();

            // 2) Registro do usuário, no processo do Nexo. O estado anterior é gravado antes de cada escrita.
            var changedSettings = false;
            foreach (var item in chosen.Where(c => c.Item.Kind == DebloatKind.UserRegistry))
            {
                progress?.Report("Aplicando: " + item.Item.Title);
                var failures = new List<string>(); var applied = 0;
                foreach (var value in item.Item.Values)
                {
                    try
                    {
                        var previous = system.Registry.Read(value.Key, value.Name);
                        run.Entries.Add(new(item.Item.Id, DebloatKind.UserRegistry, value.Key + "|" + value.Name, DebloatScripts.Describe(previous)));
                        undo.Save(run);
                        system.Registry.Write(value.Key, value.Name, value.Kind, value.Value);
                        applied++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add(value.Name + ": " + ex.Message); }
                }
                changedSettings |= applied > 0;
                var state = failures.Count == 0 ? DebloatStatus.Ok : applied > 0 ? DebloatStatus.Partial : DebloatStatus.Failed;
                Add(item.Item, state, failures.Count == 0 ? $"{applied} valor(es) gravado(s) no seu perfil." : string.Join("; ", failures));
            }
            if (changedSettings) { try { system.Registry.NotifySettingsChanged(); } catch (Exception ex) when (ex is not OperationCanceledException) { Log("NotifySettings", "", "Failed", ex.Message); } }
            token.ThrowIfCancellationRequested();

            // 3) Apps: só o usuário atual, pelo nome exato do pacote. Depois de iniciada, a remoção não é interrompida.
            var apps = chosen.Where(c => c.Item.Kind == DebloatKind.App).ToList();
            var packages = apps.SelectMany(app => preview.Inventory.Apps.Where(installed => app.PresentTargets.Any(name => Same(name, installed.Name)) && !installed.NonRemovable)
                .Select(installed => (app.Item, installed))).ToList();
            if (packages.Count > 0)
            {
                progress?.Report("Removendo apps do seu usuário.");
                Log("RemoveApps", string.Join(",", packages.Select(p => p.installed.Name)), "Requested");
                Dictionary<string, (string Status, string Detail)> removed;
                try { removed = DebloatScripts.ParseRemoval(await system.RunUserScriptAsync(DebloatScripts.RemoveApps(packages.Select(p => (p.installed.Name, p.installed.FullName))), 600, CancellationToken.None)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { removed = packages.ToDictionary(p => p.installed.FullName, _ => ("Failed", ex.Message), StringComparer.OrdinalIgnoreCase); }
                foreach (var app in apps)
                {
                    var mine = packages.Where(p => p.Item.Id == app.Item.Id).Select(p => (p.installed, removed.TryGetValue(p.installed.FullName, out var r) ? r : ("Failed", "Sem resultado da remoção."))).ToArray();
                    foreach (var (installed, r) in mine.Where(m => m.Item2.Item1 == "Ok"))
                        run.Entries.Add(new(app.Item.Id, DebloatKind.App, installed.Name, "installed"));
                    var details = string.Join("; ", mine.Select(m => m.installed.Name + ": " + (m.Item2.Item1 == "Ok" ? "removido do seu usuário" : m.Item2.Item2)));
                    Add(app.Item, Aggregate(mine.Select(m => m.Item2.Item1)), details);
                }
            }
            if (run.Entries.Count > 0) undo.Save(run);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            status = "Canceled";
        }
        finally { Interlocked.Exchange(ref running, 0); }

        if (status == "Canceled")
        {
            foreach (var id in selected.Distinct(StringComparer.Ordinal).Where(id => outcomes.All(o => o.Id != id)))
                outcomes.Add(new(id, DebloatCatalog.Find(id)?.Title ?? id, DebloatStatus.NotApplied, "Cancelado antes desta etapa; nada foi alterado neste item."));
        }
        else if (outcomes.Any(o => o.Status is DebloatStatus.Partial or DebloatStatus.Failed or DebloatStatus.NotApplied)) status = "Partial";
        foreach (var outcome in outcomes) Log("Item", outcome.Id, outcome.Status.ToString(), outcome.Detail);
        Log("Debloat", "", status);
        progress?.Report(Summary(status, outcomes));
        return new(status, restore, outcomes, run.Entries.Count > 0 ? run.Id : null);

        void Add(DebloatItem item, DebloatStatus state, string detail) => outcomes.Add(new(item.Id, item.Title, state, detail));
    }

    public async Task<DebloatResult> UndoAsync(DebloatRun run, IProgress<string>? progress, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("Já existe uma remoção de apps e ajustes em execução.");
        var outcomes = new List<DebloatOutcome>();
        var status = "Completed";
        try
        {
            Log("DebloatUndo", run.Id, "Started");
            token.ThrowIfCancellationRequested();
            var valid = new List<DebloatUndoEntry>();
            foreach (var entry in run.Entries)
            {
                var item = DebloatCatalog.Find(entry.ItemId);
                if (item == null || !DebloatScripts.ValidUndo(item, entry))
                { outcomes.Add(new(entry.ItemId, item?.Title ?? entry.ItemId, DebloatStatus.Failed, "Estado anterior inválido para " + entry.Target + "; nada foi alterado.")); continue; }
                valid.Add(entry);
            }

            var elevated = valid.Where(e => e.Kind is DebloatKind.Service or DebloatKind.ScheduledTask or DebloatKind.OptionalFeature).ToList();
            if (elevated.Count > 0)
            {
                progress?.Report("Restaurando serviços e tarefas. O Windows pode pedir autorização de administrador.");
                var (_, results, error) = await RunElevated(DebloatScripts.Undo(elevated), token, restorePoint: false);
                foreach (var group in elevated.GroupBy(e => e.ItemId))
                {
                    var item = DebloatCatalog.Find(group.Key)!;
                    if (results == null) { outcomes.Add(new(item.Id, item.Title, DebloatStatus.NotApplied, "Não restaurado: " + error)); continue; }
                    var mine = results.Where(r => r.Id == item.Id).ToArray();
                    outcomes.Add(new(item.Id, item.Title, Aggregate(mine.Select(r => r.Status)), Details(mine)));
                }
            }
            token.ThrowIfCancellationRequested();

            var settings = false;
            foreach (var group in valid.Where(e => e.Kind == DebloatKind.UserRegistry).GroupBy(e => e.ItemId))
            {
                var item = DebloatCatalog.Find(group.Key)!;
                var failures = new List<string>(); var restored = 0;
                // A primeira leitura de cada valor é o estado de antes do Nexo.
                foreach (var entry in group.GroupBy(e => e.Target, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
                {
                    var parts = entry.Target.Split('|', 2);
                    try
                    {
                        var previous = DebloatScripts.ParseState(entry.Previous)!;
                        if (previous.Exists) system.Registry.Write(parts[0], parts[1], previous.Kind, previous.Value);
                        else system.Registry.Delete(parts[0], parts[1]);
                        restored++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add(parts[1] + ": " + ex.Message); }
                }
                settings |= restored > 0;
                outcomes.Add(new(item.Id, item.Title, failures.Count == 0 ? DebloatStatus.Ok : restored > 0 ? DebloatStatus.Partial : DebloatStatus.Failed,
                    failures.Count == 0 ? $"{restored} valor(es) restaurado(s)." : string.Join("; ", failures)));
            }
            if (settings) { try { system.Registry.NotifySettingsChanged(); } catch (Exception ex) when (ex is not OperationCanceledException) { Log("NotifySettings", "", "Failed", ex.Message); } }

            foreach (var group in valid.Where(e => e.Kind == DebloatKind.App).GroupBy(e => e.ItemId))
            {
                var item = DebloatCatalog.Find(group.Key)!;
                outcomes.Add(new(item.Id, item.Title, DebloatStatus.NotApplied, "O Nexo não reinstala apps. " + StoreGuidance + " Removido: " + string.Join(", ", group.Select(e => e.Target)) + "."));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { status = "Canceled"; }
        finally { Interlocked.Exchange(ref running, 0); }

        if (status != "Canceled" && outcomes.Any(o => o.Status is DebloatStatus.Partial or DebloatStatus.Failed || o.Status == DebloatStatus.NotApplied && DebloatCatalog.Find(o.Id)?.Kind != DebloatKind.App))
            status = "Partial";
        if (status == "Completed") { run.UndoneAt = time.GetUtcNow(); undo.Save(run); }
        foreach (var outcome in outcomes) Log("UndoItem", outcome.Id, outcome.Status.ToString(), outcome.Detail);
        Log("DebloatUndo", run.Id, status);
        progress?.Report(Summary(status, outcomes));
        return new(status, null, outcomes, run.Id);
    }

    private async Task<(RecoveryPointResult Point, IReadOnlyList<DebloatScripts.TargetResult>? Results, string? Error)> RunElevated(string script, CancellationToken token, bool restorePoint = true)
    {
        string output;
        try { output = await system.RunElevatedAsync(script, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { const string denied = "a autorização de administrador foi recusada"; return (new(false, null, denied), null, denied); }
        catch (Exception ex) { return (new(false, null, ex.Message), null, ex.Message); }
        return DebloatScripts.ParseElevated(output, restorePoint);
    }

    private void Log(string action, string target, string outcome, string detail = "") => audit(new(time.GetUtcNow(), action, target, outcome, detail));

    internal static DebloatStatus Aggregate(IEnumerable<string> statuses)
    {
        var all = statuses.ToArray();
        int ok = all.Count(s => s == "Ok"), partial = all.Count(s => s == "Partial"), failed = all.Count(s => s is not ("Ok" or "Partial" or "NotFound"));
        if (failed == 0 && partial == 0) return ok > 0 ? DebloatStatus.Ok : DebloatStatus.NotFound;
        return ok + partial > 0 ? DebloatStatus.Partial : DebloatStatus.Failed;
    }

    private static string Details(IEnumerable<DebloatScripts.TargetResult> results)
    {
        var text = string.Join("; ", results.Select(r => r.Target.Replace('|', ' ').Trim() + ": " + (r.Detail.Length > 0 ? r.Detail : r.Status)));
        return text.Length == 0 ? "Sem resultado do Windows para este item." : text;
    }

    public static string Summary(string status, IReadOnlyList<DebloatOutcome> outcomes)
    {
        var label = status switch { "Completed" => "concluído", "Partial" => "parcial", "Canceled" => "cancelado", _ => "resultado não confirmado" };
        int Count(DebloatStatus s) => outcomes.Count(o => o.Status == s);
        return $"Resultado {label}: {Count(DebloatStatus.Ok)} ok, {Count(DebloatStatus.Partial)} parcial(is), {Count(DebloatStatus.NotFound)} não encontrado(s), {Count(DebloatStatus.Failed)} com falha, {Count(DebloatStatus.NotApplied)} não aplicado(s).";
    }

    internal static bool Matches(DebloatRegistryState state, DebloatRegistryValue value) =>
        state.Exists && state.Kind == value.Kind && string.Equals(DebloatScripts.Normalize(value.Kind, state.Value), DebloatScripts.Normalize(value.Kind, value.Value), StringComparison.OrdinalIgnoreCase);
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Scripts fixos gerados a partir do catálogo e o parse das saídas. Nenhum texto do usuário entra nos scripts.</summary>
public static class DebloatScripts
{
    internal sealed record TargetResult(string Id, string Target, string Status, string Previous, string Detail);

    private static readonly Regex PackageName = new(@"^[A-Za-z0-9][A-Za-z0-9.\-]*$");
    private static readonly Regex PackageFullName = new(@"^[A-Za-z0-9][A-Za-z0-9.\-_~]*$");
    private static readonly string[] StartTypes = ["Automatic", "AutomaticDelayed", "Manual", "Disabled"];

    private static string Q(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>Inventário sem elevação: só os pacotes do usuário atual (sem -AllUsers), serviços, tarefas, disco do sistema e Recall.</summary>
    public static string Inventory(IEnumerable<DebloatItem> items)
    {
        var list = items.ToArray();
        var packages = list.Where(i => i.Kind == DebloatKind.App).SelectMany(i => i.Targets).Concat(DebloatCatalog.XboxGameIndicators).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var services = list.Where(i => i.Kind == DebloatKind.Service).SelectMany(i => i.Targets).ToArray();
        var tasks = list.Where(i => i.Kind == DebloatKind.ScheduledTask).SelectMany(i => i.Targets).ToArray();
        var recall = list.Any(i => i.Kind == DebloatKind.OptionalFeature);
        string Array(IEnumerable<string> values) => "@(" + string.Join(",", values.Select(Q)) + ")";
        return $$"""
            $names={{Array(packages)}}
            $services={{Array(services)}}
            $tasks={{Array(tasks)}}
            $apps=@(Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.Name } | ForEach-Object { [pscustomobject]@{Name=[string]$_.Name;FullName=[string]$_.PackageFullName;NonRemovable=[bool]$_.NonRemovable} })
            $svc=@(foreach($n in $services){ $s=Get-Service -Name $n -ErrorAction SilentlyContinue; if($s){ $d=$false; try { $d=[int](Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$n) -Name DelayedAutostart -ErrorAction Stop).DelayedAutostart -eq 1 } catch {}; $t=[string]$s.StartType; if($t -eq 'Automatic' -and $d){$t='AutomaticDelayed'}; [pscustomobject]@{Name=[string]$s.Name;StartType=$t;Running=([string]$s.Status -eq 'Running')} } })
            $tk=@(foreach($t in $tasks){ $p=$t.Split('|'); $x=Get-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction SilentlyContinue; if($x){ [pscustomobject]@{Path=$p[0];Name=$p[1];State=[string]$x.State} } })
            $media=''
            try { $number=(Get-Partition -DriveLetter ($env:SystemDrive.TrimEnd(':')) -ErrorAction Stop | Get-Disk -ErrorAction Stop).Number; $pd=@(Get-PhysicalDisk -ErrorAction Stop | Where-Object { [string]$_.DeviceId -eq [string]$number }); if($pd.Count -eq 1){ $media=[string]$pd[0].MediaType } } catch {}
            $recall=$null
            {{(recall ? "try { $f=Get-CimInstance -ClassName Win32_OptionalFeature -Filter \"Name='Recall'\" -ErrorAction Stop; if($f){ $recall=switch([int]$f.InstallState){ 1 {'Enabled'} 2 {'Disabled'} default {'Unknown'} } } } catch {}" : "")}}
            ConvertTo-Json -InputObject ([pscustomobject]@{Apps=$apps;Services=$svc;Tasks=$tk;SystemDisk=$media;Recall=$recall}) -Depth 5 -Compress
            """;
    }

    public static DebloatInventory ParseInventory(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        static IEnumerable<JsonElement> Many(JsonElement root, string name) =>
            !root.TryGetProperty(name, out var value) ? [] : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : value.ValueKind == JsonValueKind.Object ? [value] : [];
        static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var apps = Many(root, "Apps").Select(a => new DebloatInstalledApp(Text(a, "Name"), Text(a, "FullName"), Flag(a, "NonRemovable"))).Where(a => a.Name.Length > 0).ToArray();
        var services = Many(root, "Services").Select(s => new DebloatServiceState(Text(s, "Name"), Text(s, "StartType"), Flag(s, "Running"))).Where(s => s.Name.Length > 0).ToArray();
        var tasks = Many(root, "Tasks").Select(t => new DebloatTaskState(Text(t, "Path"), Text(t, "Name"), Text(t, "State"))).Where(t => t.Name.Length > 0).ToArray();
        var recall = root.TryGetProperty("Recall", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        return new(apps, services, tasks, Text(root, "SystemDisk"), recall);
    }

    /// <summary>Remove só para o usuário atual, pelo nome completo exato, e confere que o pacote saiu. Sem -AllUsers e sem desprovisionar.</summary>
    public static string RemoveApps(IEnumerable<(string Name, string FullName)> packages)
    {
        var list = packages.ToArray();
        foreach (var (name, full) in list)
            if (!PackageName.IsMatch(name) || !PackageFullName.IsMatch(full) || !full.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Nome de pacote fora do formato esperado: " + full);
        return "$r=@()\n" +
            "foreach($p in @(" + string.Join(",", list.Select(p => Q(p.FullName))) + ")){\n" +
            "  try { Remove-AppxPackage -Package $p -ErrorAction Stop\n" +
            "    if(@(Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.PackageFullName -eq $p }).Count -gt 0){ throw 'O pacote continua instalado.' }\n" +
            "    $r+=[pscustomobject]@{Package=$p;Status='Ok';Detail=''} }\n" +
            "  catch { $r+=[pscustomobject]@{Package=$p;Status='Failed';Detail=$_.Exception.Message} }\n" +
            "}\n" +
            "ConvertTo-Json -InputObject @($r) -Compress";
    }

    public static Dictionary<string, (string Status, string Detail)> ParseRemoval(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : root.ValueKind == JsonValueKind.Object ? [root] : [];
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var package = item.TryGetProperty("Package", out var p) ? p.GetString() ?? "" : "";
            var status = item.TryGetProperty("Status", out var s) ? s.GetString() ?? "" : "";
            var detail = item.TryGetProperty("Detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
            if (package.Length > 0) result[package] = (status == "Ok" ? "Ok" : "Failed", detail);
        }
        return result;
    }

    /// <summary>
    /// Script elevado: ponto de restauração confirmado e, só depois dele, os serviços, tarefas e recursos escolhidos, com o estado
    /// anterior de cada alvo. Sem aspas duplas: o executor passa o script em -Command.
    /// </summary>
    public static string Apply(IEnumerable<DebloatItem> items)
    {
        var body = new StringBuilder();
        body.AppendLine("$rp=& { " + RestorePointProtection.Script.Replace("'Nexo before driver installation'", Q(DebloatService.RestorePointDescription)) + " }");
        body.AppendLine("$seq=[int]$rp.SequenceNumber");
        body.AppendLine("$items=@()");
        foreach (var item in items)
        {
            if (!item.Elevated) throw new ArgumentException("Item sem etapa elevada: " + item.Id);
            foreach (var target in item.Targets)
            {
                var id = Q(item.Id);
                // Cada alvo tem seu próprio try: um erro inesperado num alvo não derruba os demais depois do ponto de restauração.
                body.AppendLine("try {");
                switch (item.Kind)
                {
                    case DebloatKind.Service:
                        body.AppendLine($$"""
                            $s=Get-Service -Name {{Q(target)}} -ErrorAction SilentlyContinue
                            if(-not $s){ $items+=[pscustomobject]@{Id={{id}};Target={{Q(target)}};Status='NotFound';Previous='';Detail='serviço inexistente neste Windows'} } else {
                              $d=$false; try { $d=[int](Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+{{Q(target)}}) -Name DelayedAutostart -ErrorAction Stop).DelayedAutostart -eq 1 } catch {}
                              $t=[string]$s.StartType; if($t -eq 'Automatic' -and $d){ $t='AutomaticDelayed' }
                              $prev=$t+'|'+([string]$s.Status -eq 'Running')
                              try { Set-Service -Name {{Q(target)}} -StartupType Disabled -ErrorAction Stop
                                try { if([string]$s.Status -eq 'Running'){ Stop-Service -Name {{Q(target)}} -Force -ErrorAction Stop }; $items+=[pscustomobject]@{Id={{id}};Target={{Q(target)}};Status='Ok';Previous=$prev;Detail='desativado'} }
                                catch { $items+=[pscustomobject]@{Id={{id}};Target={{Q(target)}};Status='Partial';Previous=$prev;Detail=('desativado, mas não parou: '+$_.Exception.Message)} } }
                              catch { $items+=[pscustomobject]@{Id={{id}};Target={{Q(target)}};Status='Failed';Previous='';Detail=$_.Exception.Message} } }
                            """);
                        break;
                    case DebloatKind.ScheduledTask:
                        var parts = target.Split('|', 2);
                        body.AppendLine($$"""
                            $x=Get-ScheduledTask -TaskPath {{Q(parts[0])}} -TaskName {{Q(parts[1])}} -ErrorAction SilentlyContinue
                            if(-not $x){ $items+=[pscustomobject]@{Id={{id}};Target={{Q(target)}};Status='NotFound';Previous='';Detail='tarefa inexistente neste Windows'} } else {
                              $prev=[string]$x.State
                              try { $x | Disable-ScheduledTask -ErrorAction Stop | Out-Null; $items+=[pscustomobject]@{Id={{id}};Target={{Q(target)}};Status='Ok';Previous=$prev;Detail='desativada'} }
                              catch { $items+=[pscustomobject]@{Id={{id}};Target={{Q(target)}};Status='Failed';Previous='';Detail=$_.Exception.Message} } }
                            """);
                        break;
                    case DebloatKind.OptionalFeature:
                        body.AppendLine($$"""
                            $f=$null; try { $f=Get-WindowsOptionalFeature -Online -FeatureName {{Q(target)}} -ErrorAction Stop } catch {}
                            if(-not $f){ $items+=[pscustomobject]@{Id={{id}};Target={{Q(target)}};Status='NotFound';Previous='';Detail='recurso inexistente neste PC'} }
                            elseif([string]$f.State -eq 'Disabled'){ $items+=[pscustomobject]@{Id={{id}};Target={{Q(target)}};Status='Ok';Previous='Disabled';Detail='já estava desativado'} }
                            else { $prev=[string]$f.State
                              try { Disable-WindowsOptionalFeature -Online -FeatureName {{Q(target)}} -NoRestart -ErrorAction Stop | Out-Null; $items+=[pscustomobject]@{Id={{id}};Target={{Q(target)}};Status='Ok';Previous=$prev;Detail='desativado; reinicie o Windows para concluir'} }
                              catch { $items+=[pscustomobject]@{Id={{id}};Target={{Q(target)}};Status='Failed';Previous='';Detail=$_.Exception.Message} } }
                            """);
                        break;
                }
                body.AppendLine($"}} catch {{ $items+=[pscustomobject]@{{Id={id};Target={Q(target)};Status='Failed';Previous='';Detail=$_.Exception.Message}} }}");
            }
        }
        body.Append("[pscustomobject]@{RestorePoint=$seq;Items=$items}");
        return body.ToString();
    }

    /// <summary>Restaura serviços, tarefas e recursos a partir do estado anterior já validado contra o catálogo.</summary>
    public static string Undo(IEnumerable<DebloatUndoEntry> entries)
    {
        var body = new StringBuilder("$items=@()\n");
        foreach (var entry in entries)
        {
            var item = DebloatCatalog.Find(entry.ItemId) ?? throw new ArgumentException("Item desconhecido: " + entry.ItemId);
            if (!ValidUndo(item, entry)) throw new ArgumentException("Estado anterior inválido: " + entry.Target);
            var id = Q(entry.ItemId); var target = Q(entry.Target);
            switch (entry.Kind)
            {
                case DebloatKind.Service:
                    var parts = entry.Previous.Split('|');
                    var start = parts[0] == "AutomaticDelayed"
                        ? $"Set-Service -Name {target} -StartupType Automatic -ErrorAction Stop; & sc.exe config {target} 'start=' 'delayed-auto' | Out-Null; if($LASTEXITCODE -ne 0){{ throw 'sc.exe não aplicou o início atrasado' }}"
                        : $"Set-Service -Name {target} -StartupType {parts[0]} -ErrorAction Stop";
                    var run = parts[1] == "True" ? $"; Start-Service -Name {target} -ErrorAction Stop" : "";
                    body.AppendLine($"try {{ {start}{run}; $items+=[pscustomobject]@{{Id={id};Target={target};Status='Ok';Previous='';Detail='restaurado para {parts[0]}'}} }} catch {{ $items+=[pscustomobject]@{{Id={id};Target={target};Status='Failed';Previous='';Detail=$_.Exception.Message}} }}");
                    break;
                case DebloatKind.ScheduledTask:
                    var task = entry.Target.Split('|', 2);
                    var action = entry.Previous == "Disabled" ? "" : $"Get-ScheduledTask -TaskPath {Q(task[0])} -TaskName {Q(task[1])} -ErrorAction Stop | Enable-ScheduledTask -ErrorAction Stop | Out-Null; ";
                    body.AppendLine($"try {{ {action}$items+=[pscustomobject]@{{Id={id};Target={target};Status='Ok';Previous='';Detail='restaurada'}} }} catch {{ $items+=[pscustomobject]@{{Id={id};Target={target};Status='Failed';Previous='';Detail=$_.Exception.Message}} }}");
                    break;
                case DebloatKind.OptionalFeature:
                    var enable = entry.Previous == "Enabled" ? $"Enable-WindowsOptionalFeature -Online -FeatureName {target} -NoRestart -ErrorAction Stop | Out-Null; " : "";
                    body.AppendLine($"try {{ {enable}$items+=[pscustomobject]@{{Id={id};Target={target};Status='Ok';Previous='';Detail='restaurado; reinicie o Windows para concluir'}} }} catch {{ $items+=[pscustomobject]@{{Id={id};Target={target};Status='Failed';Previous='';Detail=$_.Exception.Message}} }}");
                    break;
                default: throw new ArgumentException("Entrada sem etapa elevada: " + entry.ItemId);
            }
        }
        body.Append("[pscustomobject]@{Items=$items}");
        return body.ToString();
    }

    /// <summary>
    /// Só aceita alvos que estão no catálogo e estados anteriores reconhecidos. O arquivo do Desfazer fica na pasta do usuário;
    /// um conteúdo adulterado não consegue apontar o executor elevado para outro serviço, tarefa ou chave.
    /// </summary>
    public static bool ValidUndo(DebloatItem item, DebloatUndoEntry entry)
    {
        if (entry.Kind != item.Kind) return false;
        switch (entry.Kind)
        {
            case DebloatKind.Service:
                var parts = entry.Previous.Split('|');
                return item.Targets.Contains(entry.Target, StringComparer.Ordinal) && parts.Length == 2 && StartTypes.Contains(parts[0]) && parts[1] is "True" or "False";
            case DebloatKind.ScheduledTask:
                return item.Targets.Contains(entry.Target, StringComparer.Ordinal) && entry.Previous is "Ready" or "Running" or "Queued" or "Disabled" or "Unknown";
            case DebloatKind.OptionalFeature:
                return item.Targets.Contains(entry.Target, StringComparer.Ordinal) && entry.Previous is "Enabled" or "Disabled" or "EnablePending" or "DisablePending";
            case DebloatKind.UserRegistry:
                return item.Values.Any(v => v.Key + "|" + v.Name == entry.Target) && ParseState(entry.Previous) != null;
            case DebloatKind.App:
                return item.Targets.Contains(entry.Target, StringComparer.OrdinalIgnoreCase);
            default: return false;
        }
    }

    internal static (RecoveryPointResult Point, IReadOnlyList<TargetResult>? Results, string? Error) ParseElevated(string output, bool restorePoint)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            {
                var reason = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() ?? "erro não detalhado" : "o Windows não confirmou a execução";
                return (new(false, null, reason), null, reason);
            }
            var data = root.GetProperty("data");
            if (data.ValueKind == JsonValueKind.String) { using var nested = JsonDocument.Parse(data.GetString()!); return Read(nested.RootElement); }
            return Read(data);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { const string unknown = "saída do Windows sem evidência válida"; return (new(false, null, unknown), null, unknown); }

        (RecoveryPointResult, IReadOnlyList<TargetResult>?, string?) Read(JsonElement data)
        {
            RecoveryPointResult point = new(true, null, "Sem ponto de restauração nesta etapa.");
            if (restorePoint)
            {
                if (!data.TryGetProperty("RestorePoint", out var number) || !number.TryGetInt32(out var sequence) || sequence <= 0)
                    return (new(false, null, "o novo ponto de restauração não foi confirmado"), null, "o novo ponto de restauração não foi confirmado");
                point = new(true, sequence, "Ponto de restauração confirmado.");
            }
            var list = new List<TargetResult>();
            if (data.TryGetProperty("Items", out var items))
            {
                var elements = items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToArray() : items.ValueKind == JsonValueKind.Object ? [items] : [];
                foreach (var e in elements)
                {
                    string T(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    var status = T("Status");
                    list.Add(new(T("Id"), T("Target"), status is "Ok" or "Partial" or "NotFound" ? status : "Failed", T("Previous"), T("Detail")));
                }
            }
            return (point, list, null);
        }
    }

    /// <summary>"absent" ou "Tipo:valor" (Binary em hexadecimal com hífens).</summary>
    public static string Describe(DebloatRegistryState state) => state.Exists ? state.Kind + ":" + Normalize(state.Kind, state.Value) : "absent";

    public static DebloatRegistryState? ParseState(string text)
    {
        if (text == "absent") return DebloatRegistryState.Absent;
        var split = text.IndexOf(':');
        if (split <= 0 || !Enum.TryParse<DebloatValueKind>(text[..split], false, out var kind) || !Enum.IsDefined(kind)) return null;
        var value = text[(split + 1)..];
        return kind switch
        {
            DebloatValueKind.DWord when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _) || int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) => new(true, kind, value),
            DebloatValueKind.Binary when Regex.IsMatch(value, "^(?:[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2})*)?$") => new(true, kind, value),
            DebloatValueKind.String => new(true, kind, value),
            _ => null,
        };
    }

    internal static string Normalize(DebloatValueKind kind, string value) => kind == DebloatValueKind.Binary ? value.ToUpperInvariant() : value;
}
