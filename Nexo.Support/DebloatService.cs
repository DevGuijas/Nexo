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
/// <summary>Resultado de uma aplicação ou do Desfazer. Warning: as mudanças foram feitas, mas o registro do Desfazer não foi salvo.</summary>
public sealed record DebloatResult(string Status, RecoveryPointResult? RestorePoint, IReadOnlyList<DebloatOutcome> Items, string? RunId, string? Warning = null);
public sealed record DebloatAudit(DateTimeOffset At, string Action, string Target, string Outcome, string Detail = "");

/// <summary>Estado anterior de um alvo, para o Desfazer. Para apps, só registra o que foi removido (volta pela Microsoft Store).</summary>
public sealed record DebloatUndoEntry(string ItemId, DebloatKind Kind, string Target, string Previous);
/// <summary>
/// Uma aplicação, no arquivo local do Desfazer. Entries guarda só o que não passa por elevação (valores do HKCU e apps
/// removidos). O estado anterior de serviços, tarefas e recursos fica na pasta protegida, gravado pelo executor elevado;
/// ProtectedItems é só o índice de que ele existe, e o Desfazer o lê de lá pelo Id.
/// </summary>
public sealed class DebloatRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; set; }
    public int Build { get; set; }
    public int? RestorePoint { get; set; }
    public List<DebloatUndoEntry> Entries { get; set; } = [];
    public List<string> ProtectedItems { get; set; } = [];
    public DateTimeOffset? UndoneAt { get; set; }
    public bool CanUndo => UndoneAt == null && (Entries.Count > 0 || ProtectedItems.Count > 0);
}

/// <summary>
/// Estado anterior dos itens elevados lido da pasta protegida, ou o motivo de não usá-lo. Done são os alvos ("Id|Kind|Target")
/// que um Desfazer elevado anterior já restaurou, pelo marcador undone.json; eles saem de Entries. AlreadyUndone: todos já
/// foram restaurados, e a aplicação não deve ser oferecida de novo.
/// </summary>
public sealed record DebloatProtectedUndo(IReadOnlyList<DebloatUndoEntry> Entries, string? Problem, IReadOnlyList<string>? Done = null, bool AlreadyUndone = false)
{
    public const string AlreadyUndoneText = "Esta aplicação já foi desfeita";
    public IReadOnlyList<string> DoneTargets => Done ?? [];
    public static DebloatProtectedUndo Refused(string problem) => new([], problem);
    public static string Key(DebloatUndoEntry entry) => entry.ItemId + "|" + entry.Kind + "|" + entry.Target;

    /// <summary>Tira de entries os alvos já restaurados; se sobrar nada de uma lista não vazia, a aplicação já foi desfeita.</summary>
    public static DebloatProtectedUndo Filter(IReadOnlyList<DebloatUndoEntry> entries, IReadOnlyCollection<string> done)
    {
        if (done.Count == 0) return new(entries, null);
        var left = entries.Where(e => !done.Contains(Key(e), StringComparer.Ordinal)).ToArray();
        return entries.Count > 0 && left.Length == 0 ? new([], AlreadyUndoneText, done.ToArray(), true) : new(left, null, done.ToArray());
    }
}

/// <summary>
/// %ProgramData%\NexoSupport\debloat\&lt;id&gt;\undo.json: gravado só pelo executor elevado, com o mesmo esquema da cópia
/// protegida da rede. O aplicativo lê para mostrar o plano do Desfazer, e só se a pasta e o arquivo passam na mesma
/// conferência (NetworkRecovery.UntrustedReasonOnWindows); o executor elevado do Desfazer confere de novo antes de usar.
/// </summary>
public static class DebloatProtectedStore
{
    public const string FileName = "undo.json";
    /// <summary>Marcador gravado pelo executor elevado depois de um Desfazer: os alvos já restaurados desta aplicação.</summary>
    public const string DoneFileName = "undone.json";
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NexoSupport", "debloat");
    public static bool ValidRunId(string? id) => id != null && Regex.IsMatch(id, "^[0-9a-f]{32}$");

    /// <param name="untrusted">Motivo para não confiar na pasta (e nas pastas acima dela) ou no arquivo; null quando confiável.</param>
    public static DebloatProtectedUndo Read(string root, string runId, Func<string, string, string?> untrusted)
    {
        if (!ValidRunId(runId)) return DebloatProtectedUndo.Refused("o identificador da aplicação é inválido");
        var directory = Path.Combine(root, runId);
        try
        {
            if (untrusted(directory, FileName) is { } reason) return DebloatProtectedUndo.Refused(reason);
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, FileName)));
            var data = document.RootElement;
            if (!data.TryGetProperty("RunId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != runId)
                return DebloatProtectedUndo.Refused("o registro protegido é de outra aplicação");
            var items = data.TryGetProperty("Items", out var list) ? list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : list.ValueKind == JsonValueKind.Object ? [list] : [] : [];
            var entries = new List<DebloatUndoEntry>();
            foreach (var item in items)
            {
                string T(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                if (!Enum.TryParse<DebloatKind>(T("Kind"), false, out var kind) || kind is not (DebloatKind.Service or DebloatKind.ScheduledTask or DebloatKind.OptionalFeature))
                    return DebloatProtectedUndo.Refused("o registro protegido tem um item de tipo desconhecido");
                entries.Add(new(T("Id"), kind, T("Target"), T("Previous")));
            }
            // O marcador do Desfazer passa pela mesma conferência; marcador não confiável ou de outra aplicação recusa tudo.
            var done = new List<string>();
            if (File.Exists(Path.Combine(directory, DoneFileName)))
            {
                if (untrusted(directory, DoneFileName) is { } doneReason) return DebloatProtectedUndo.Refused(doneReason);
                using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, DoneFileName)));
                var m = marker.RootElement;
                if (!m.TryGetProperty("RunId", out var markerId) || markerId.ValueKind != JsonValueKind.String || markerId.GetString() != runId)
                    return DebloatProtectedUndo.Refused("o marcador do Desfazer é de outra aplicação");
                if (m.TryGetProperty("Done", out var keys))
                    foreach (var key in keys.ValueKind == JsonValueKind.Array ? keys.EnumerateArray().ToArray() : [keys])
                        if (key.ValueKind == JsonValueKind.String) done.Add(key.GetString()!);
            }
            return DebloatProtectedUndo.Filter(entries, done);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return DebloatProtectedUndo.Refused("o registro protegido não pôde ser lido (" + ex.Message + ")"); }
    }
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
    /// <summary>Estado anterior dos itens elevados de uma aplicação, da pasta protegida, só se ela for confiável.</summary>
    DebloatProtectedUndo ReadProtectedUndo(string runId);
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
    /// <summary>A aplicação mais recente que ainda pode ser desfeita: depois de desfazer a última, a anterior continua alcançável.</summary>
    public DebloatRun? LatestUndoable() => Load().Where(run => run.CanUndo).OrderByDescending(run => run.At).FirstOrDefault();
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
    internal const string NoEvidence = "O Windows não devolveu resultado para este item; o estado atual é desconhecido.";

    internal const string LocalElevatedIgnored = "ignorado: o estado anterior de serviços, tarefas e recursos só vale da pasta protegida, e o arquivo local não é usado; nada foi alterado.";

    /// <summary>
    /// A aplicação que o botão Desfazer oferece: a mais recente pendente no arquivo local cujo registro protegido não diz que
    /// ela já foi desfeita. Apagar o UndoneAt local não reabre um Desfazer que o executor elevado marcou; a marca local é
    /// refeita quando dá.
    /// </summary>
    public DebloatRun? UndoCandidate()
    {
        foreach (var run in undo.Load().Where(r => r.CanUndo).OrderByDescending(r => r.At))
        {
            if (run.ProtectedItems.Count == 0 || !system.ReadProtectedUndo(run.Id).AlreadyUndone) return run;
            run.UndoneAt = time.GetUtcNow();
            try { undo.Save(run); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ArgumentException)
            { Log("UndoRecord", undo.Path, "NotSaved", ex.Message); }
        }
        return null;
    }

    /// <summary>O que o Desfazer vai mudar, item a item, para a confirmação antes do UAC. Serviços, tarefas e recursos vêm da pasta protegida.</summary>
    public IReadOnlyList<string> DescribeUndo(DebloatRun run) => DescribeUndo(run, run.ProtectedItems.Count > 0 ? system.ReadProtectedUndo(run.Id) : null);

    public static IReadOnlyList<string> DescribeUndo(DebloatRun run, DebloatProtectedUndo? stored)
    {
        var lines = new List<string>();
        if (stored?.AlreadyUndone == true)
            lines.Add("Serviços, tarefas e recursos: " + DebloatProtectedUndo.AlreadyUndoneText.ToLowerInvariant() + " pelo executor elevado; nada será alterado.");
        else if (stored?.Problem != null)
            lines.Add("Serviços, tarefas e recursos: não serão restaurados, porque o registro protegido do Desfazer não é confiável (" + stored.Problem + ").");
        foreach (var entry in run.Entries.Where(e => IsElevated(e.Kind)))
            lines.Add($"{DebloatCatalog.Find(entry.ItemId)?.Title ?? entry.ItemId}: entrada local de {entry.Target} {LocalElevatedIgnored}");
        foreach (var group in run.Entries.Where(e => !IsElevated(e.Kind)).Concat(stored?.Entries ?? []).GroupBy(e => e.ItemId))
        {
            var item = DebloatCatalog.Find(group.Key);
            var title = item?.Title ?? group.Key;
            foreach (var entry in group.Where(e => item == null || !DebloatScripts.ValidUndo(item, e)))
                lines.Add($"{title}: estado anterior inválido para {entry.Target}; não será alterado.");
            var valid = item == null ? Array.Empty<DebloatUndoEntry>() : group.Where(e => DebloatScripts.ValidUndo(item, e)).ToArray();
            if (valid.Length == 0) continue;
            switch (item!.Kind)
            {
                case DebloatKind.Service:
                    foreach (var e in valid)
                    {
                        var parts = e.Previous.Split('|');
                        lines.Add($"{e.Target}: volta ao início {StartLabel(parts[0])}" + (parts[1] == "True" ? " e é iniciado." : "."));
                    }
                    break;
                case DebloatKind.ScheduledTask:
                    foreach (var e in valid.Where(e => e.Previous != "Disabled"))
                        lines.Add($"Tarefa {e.Target.Split('|', 2)[1]}: volta a ficar ativa.");
                    break;
                case DebloatKind.OptionalFeature:
                    foreach (var e in valid.Where(e => e.Previous == "Enabled"))
                        lines.Add($"{title}: o recurso volta a ser ativado (o Windows pede reinício).");
                    break;
                case DebloatKind.UserRegistry:
                    lines.Add($"{title}: os valores do seu perfil voltam ao que eram.");
                    break;
                case DebloatKind.App:
                    lines.Add($"{title}: não é reinstalado pelo Nexo (Microsoft Store).");
                    break;
            }
        }
        return lines;

        static string StartLabel(string start) => start switch
        {
            "Automatic" => "automático", "AutomaticDelayed" => "automático (atraso)", "Manual" => "manual", _ => "desativado",
        };
    }

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
        string? undoProblem = null;
        // Uma falha ao gravar o arquivo local do Desfazer não derruba a aplicação nem apaga o resultado do que já mudou.
        bool SaveUndo()
        {
            try { undo.Save(run); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ArgumentException)
            { undoProblem ??= ex.Message; Log("UndoRecord", undo.Path, "Failed", ex.Message); return false; }
        }
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

            // Nada a aplicar (tudo já aplicado, ausente ou bloqueado): sem UAC e sem ponto de restauração à toa.
            if (chosen.Count > 0)
            {
            // 1) Ponto de restauração e itens elevados num único UAC. Sem ponto confirmado, nenhum item elevado roda.
            var elevated = chosen.Where(c => c.Item.Elevated).ToList();
            progress?.Report("Criando o ponto de restauração. O Windows pode pedir autorização de administrador.");
            Log("RestorePoint", "", "Requested");
            var (point, results, error, _) = await RunElevated(DebloatScripts.Apply(elevated.Select(e => e.Item), run.Id), token);
            restore = point;
            Log("RestorePoint", point.SequenceNumber?.ToString(CultureInfo.InvariantCulture) ?? "", point.Ready ? "Confirmed" : "NotConfirmed", point.Reason);
            foreach (var item in elevated)
            {
                if (!point.Ready || results == null)
                {
                    var reason = error ?? point.Reason;
                    Add(item.Item, DebloatStatus.NotApplied, reason.StartsWith(DebloatScripts.StoreRefusal, StringComparison.Ordinal)
                        ? reason : "Ponto de restauração não confirmado (" + reason + "); serviços, tarefas e recursos não foram alterados.");
                    continue;
                }
                var mine = results.Where(r => r.Id == item.Item.Id).ToArray();
                // Sem linha do Windows para o item, o estado é desconhecido: não vira "não encontrado".
                if (mine.Length == 0) { Add(item.Item, DebloatStatus.Failed, NoEvidence); continue; }
                // O estado anterior ficou na pasta protegida, gravado pelo executor antes da mudança; aqui só o índice.
                if (mine.Any(r => r.Status is "Ok" or "Partial")) run.ProtectedItems.Add(item.Item.Id);
                Add(item.Item, Aggregate(mine.Select(r => r.Status)), Details(mine));
            }
            run.RestorePoint = point.SequenceNumber;
            if (run.ProtectedItems.Count > 0) SaveUndo();
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
                        var entry = new DebloatUndoEntry(item.Item.Id, DebloatKind.UserRegistry, value.Key + "|" + value.Name, DebloatScripts.Describe(previous));
                        run.Entries.Add(entry);
                        // Sem o valor anterior guardado, este valor não é gravado: não haveria como desfazê-lo.
                        if (!SaveUndo()) { run.Entries.Remove(entry); failures.Add(value.Name + ": não alterado, porque o valor anterior não pôde ser guardado"); continue; }
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
            if (packages.Count > 0 && run.Entries.Any(e => e.Kind == DebloatKind.App)) SaveUndo();
            }
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
        else if (outcomes.Any(o => o.Status is DebloatStatus.Partial or DebloatStatus.Failed or DebloatStatus.NotApplied) || undoProblem != null) status = "Partial";
        var warning = undoProblem == null ? null : UndoNotSaved(undoProblem, restore, run.ProtectedItems.Count > 0);
        foreach (var outcome in outcomes) Log("Item", outcome.Id, outcome.Status.ToString(), outcome.Detail);
        if (warning != null) Log("UndoRecord", undo.Path, "NotSaved", warning);
        Log("Debloat", "", status);
        progress?.Report(Summary(status, outcomes) + (warning == null ? "" : " " + warning));
        return new(status, restore, outcomes, run.CanUndo ? run.Id : null, warning);

        void Add(DebloatItem item, DebloatStatus state, string detail) => outcomes.Add(new(item.Id, item.Title, state, detail));
    }

    internal static bool IsElevated(DebloatKind kind) => kind is DebloatKind.Service or DebloatKind.ScheduledTask or DebloatKind.OptionalFeature;

    internal static string UndoNotSaved(string problem, RecoveryPointResult? point, bool protectedState) =>
        "As mudanças acima foram feitas, mas o registro para desfazer não foi salvo (" + problem + ")."
        + (protectedState ? " O estado anterior dos serviços, tarefas e recursos ficou na pasta protegida, mas esta aplicação não aparece em Desfazer." : "")
        + (point is { Ready: true, SequenceNumber: { } sequence }
            ? $" Para voltar, use o ponto de restauração {sequence.ToString(CultureInfo.InvariantCulture)} (rstrui.exe), criado antes desta aplicação."
            : " Não há ponto de restauração confirmado desta aplicação.");

    public async Task<DebloatResult> UndoAsync(DebloatRun run, IProgress<string>? progress, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("Já existe uma remoção de apps e ajustes em execução.");
        var outcomes = new List<DebloatOutcome>();
        var status = "Completed";
        string? warning = null;
        var settled = new HashSet<string>(StringComparer.Ordinal); // já restaurados por um Desfazer elevado anterior
        try
        {
            Log("DebloatUndo", run.Id, "Started");
            token.ThrowIfCancellationRequested();
            var valid = new List<DebloatUndoEntry>();
            foreach (var entry in run.Entries)
            {
                var item = DebloatCatalog.Find(entry.ItemId);
                // O arquivo local não define nada que rode com elevação: um serviço, tarefa ou recurso dele é ignorado.
                if (IsElevated(entry.Kind))
                { outcomes.Add(new(entry.ItemId, item?.Title ?? entry.ItemId, DebloatStatus.Failed, "Entrada local de " + entry.Target + " " + LocalElevatedIgnored)); continue; }
                if (item == null || !DebloatScripts.ValidUndo(item, entry))
                { outcomes.Add(new(entry.ItemId, item?.Title ?? entry.ItemId, DebloatStatus.Failed, "Estado anterior inválido para " + entry.Target + "; nada foi alterado.")); continue; }
                valid.Add(entry);
            }

            if (run.ProtectedItems.Count > 0)
            {
                // O estado vem da pasta protegida, conferida aqui (antes do UAC) e de novo pelo executor elevado.
                var stored = system.ReadProtectedUndo(run.Id);
                var expected = run.ProtectedItems.Distinct(StringComparer.Ordinal).ToList();
                if (stored.AlreadyUndone)
                {
                    Log("ProtectedUndo", run.Id, "AlreadyUndone");
                    foreach (var id in expected)
                    {
                        settled.Add(id);
                        outcomes.Add(new(id, DebloatCatalog.Find(id)?.Title ?? id, DebloatStatus.NotApplied, DebloatProtectedUndo.AlreadyUndoneText + " pelo executor elevado; nada foi alterado."));
                    }
                }
                else if (stored.Problem != null)
                {
                    Log("ProtectedUndo", run.Id, "Refused", stored.Problem);
                    foreach (var id in expected)
                        outcomes.Add(new(id, DebloatCatalog.Find(id)?.Title ?? id, DebloatStatus.NotApplied,
                            "Não restaurado: o registro protegido do Desfazer não é confiável (" + stored.Problem + "). Nenhum serviço, tarefa ou recurso foi alterado"
                            + (run.RestorePoint is { } point ? $"; o ponto de restauração {point.ToString(CultureInfo.InvariantCulture)} continua disponível." : ".")));
                }
                else
                {
                    var restorable = new List<DebloatUndoEntry>();
                    foreach (var entry in stored.Entries)
                    {
                        var item = DebloatCatalog.Find(entry.ItemId);
                        if (item == null || !DebloatScripts.ValidUndo(item, entry))
                        { outcomes.Add(new(entry.ItemId, item?.Title ?? entry.ItemId, DebloatStatus.Failed, "Estado anterior inválido para " + entry.Target + "; nada foi alterado.")); continue; }
                        restorable.Add(entry);
                    }
                    foreach (var id in expected.Where(id => stored.Entries.All(e => e.ItemId != id)))
                    {
                        if (stored.DoneTargets.Any(key => key.StartsWith(id + "|", StringComparison.Ordinal)))
                        { settled.Add(id); outcomes.Add(new(id, DebloatCatalog.Find(id)?.Title ?? id, DebloatStatus.NotApplied, "Já restaurado num Desfazer anterior; nada foi alterado.")); continue; }
                        outcomes.Add(new(id, DebloatCatalog.Find(id)?.Title ?? id, DebloatStatus.Failed, "O registro protegido não tem o estado anterior deste item; nada foi alterado."));
                    }
                    if (restorable.Count > 0)
                    {
                        progress?.Report("Restaurando serviços e tarefas. O Windows pode pedir autorização de administrador.");
                        var (_, results, error, output) = await RunElevated(DebloatScripts.Undo(run.Id), token, restorePoint: false);
                        if (results != null && DebloatScripts.ParseMarker(output) is { } markError)
                        {
                            warning = "O Desfazer restaurou os itens elevados, mas o marcador na pasta protegida não foi gravado (" + markError + "); esta aplicação ainda pode ser oferecida para desfazer de novo.";
                            Log("UndoMarker", run.Id, "NotSaved", markError);
                        }
                        foreach (var group in restorable.GroupBy(e => e.ItemId))
                        {
                            var item = DebloatCatalog.Find(group.Key)!;
                            if (results == null) { outcomes.Add(new(item.Id, item.Title, DebloatStatus.NotApplied, "Não restaurado: " + error)); continue; }
                            var mine = results.Where(r => r.Id == item.Id).ToArray();
                            if (mine.Length == 0) { outcomes.Add(new(item.Id, item.Title, DebloatStatus.Failed, NoEvidence)); continue; }
                            outcomes.Add(new(item.Id, item.Title, Aggregate(mine.Select(r => r.Status)), Details(mine)));
                        }
                    }
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

        if (status != "Canceled" && outcomes.Any(o => o.Status is DebloatStatus.Partial or DebloatStatus.Failed
                || o.Status == DebloatStatus.NotApplied && DebloatCatalog.Find(o.Id)?.Kind != DebloatKind.App && !settled.Contains(o.Id)))
            status = "Partial";
        if (status == "Completed")
        {
            run.UndoneAt = time.GetUtcNow();
            try { undo.Save(run); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ArgumentException)
            {
                var local = "O Desfazer foi concluído, mas o registro não foi atualizado (" + ex.Message + "); a aplicação ainda pode aparecer como pendente.";
                warning = warning == null ? local : warning + " " + local;
            }
        }
        foreach (var outcome in outcomes) Log("UndoItem", outcome.Id, outcome.Status.ToString(), outcome.Detail);
        if (warning != null) Log("UndoRecord", undo.Path, "NotSaved", warning);
        Log("DebloatUndo", run.Id, status);
        progress?.Report(Summary(status, outcomes) + (warning == null ? "" : " " + warning));
        return new(status, null, outcomes, run.Id, warning);
    }

    private async Task<(RecoveryPointResult Point, IReadOnlyList<DebloatScripts.TargetResult>? Results, string? Error, string Output)> RunElevated(string script, CancellationToken token, bool restorePoint = true)
    {
        string output;
        try { output = await system.RunElevatedAsync(script, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { const string denied = "a autorização de administrador foi recusada"; return (new(false, null, denied), null, denied, ""); }
        catch (Exception ex) { return (new(false, null, ex.Message), null, ex.Message, ""); }
        var (point, results, error) = DebloatScripts.ParseElevated(output, restorePoint);
        return (point, results, error, output);
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

    // Executável e unidade do sistema pelo GetSystemDirectory, nunca por $env:WINDIR/$env:SystemDrive: o usuário define essas
    // variáveis em HKCU\Environment, e o processo (elevado ou não) as recebe (#131).
    internal static readonly string ScExecutable = PowerShellModulePath.SystemExecutable("sc.exe");
    internal const string SystemDriveLetter = "[IO.Path]::GetPathRoot([Environment]::SystemDirectory).TrimEnd([char]92,[char]58)";

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
            try { $number=(Get-Partition -DriveLetter ({{SystemDriveLetter}}) -ErrorAction Stop | Get-Disk -ErrorAction Stop).Number; $pd=@(Get-PhysicalDisk -ErrorAction Stop | Where-Object { [string]$_.DeviceId -eq [string]$number }); if($pd.Count -eq 1){ $media=[string]$pd[0].MediaType } } catch {}
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

    /// <summary>Início da mensagem de recusa da pasta protegida do Desfazer, para o resultado não falar em ponto de restauração.</summary>
    public const string StoreRefusal = "Pasta protegida do Desfazer recusada: ";

    /// <summary>
    /// O script elevado como vai ao executor: compactado (GZip) no mesmo carregador curto do executor de rede (#10), em
    /// -Command, executado no mesmo escopo. Com todos os itens, o Aplicar sem compactar fica perto do limite de 32 767
    /// caracteres da linha de comando do Windows; compactado, sobra margem para o catálogo crescer.
    /// </summary>
    public static string ForElevation(string script) => NetworkRecovery.Compact(script);

    /// <summary>
    /// Funções da pasta protegida do Desfazer: as da cópia protegida da rede (ProtectedFolderScript) e as próprias do
    /// debloat, em %ProgramData%\NexoSupport\debloat\&lt;id&gt;. A pasta da aplicação nasce nova e protegida; NexoSupport e
    /// debloat só são usadas se já forem confiáveis (#97). Os testes trocam este bloco inteiro por funções falsas.
    /// </summary>
    public static string StoreFunctions => ProtectedFolderScript.Text + OwnStore;

    private const string OwnStore = """
        $nxRoot=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NexoSupport\debloat'
        $nxDir=Join-Path $nxRoot $nxRunId
        $nxFile=Join-Path $nxDir 'undo.json'
        $nxDone=Join-Path $nxDir 'undone.json'
        function Open-DebloatStore {
          try {
            Use-ProtectedDirectory (Split-Path -Parent $nxRoot)
            Use-ProtectedDirectory $nxRoot
            if(Test-Path -LiteralPath $nxDir){ throw ('a pasta ' + $nxDir + ' já existia antes desta aplicação.') }
            New-ProtectedDirectory $nxDir
            Assert-Trusted $nxDir
          } catch { throw ('Pasta protegida do Desfazer recusada: ' + $_.Exception.Message + ' Nenhum serviço, tarefa ou recurso foi alterado.') }
        }
        function Save-DebloatUndo($entries) {
          Write-ProtectedFile $nxFile (ConvertTo-Json -InputObject ([pscustomobject]@{RunId=$nxRunId;Items=@($entries)}) -Depth 5)
        }
        function Read-DebloatDone {
          if(-not (Test-Path -LiteralPath $nxDone)){ return @() }
          Assert-Trusted $nxDone
          $d=Get-Content -LiteralPath $nxDone -Raw -Encoding UTF8 | ConvertFrom-Json
          if([string]$d.RunId -cne $nxRunId){ throw 'o marcador do Desfazer é de outra aplicação.' }
          @($d.Done | ForEach-Object { [string]$_ })
        }
        function Save-DebloatDone($done) {
          Write-ProtectedFile $nxDone (ConvertTo-Json -InputObject ([pscustomobject]@{RunId=$nxRunId;At=(Get-Date).ToUniversalTime().ToString('o');Done=@($done)}) -Depth 3)
        }
        function Read-DebloatUndo {
          try {
            foreach($p in @((Split-Path -Parent $nxRoot),$nxRoot,$nxDir,$nxFile)){ if(-not (Test-Path -LiteralPath $p)){ throw ($p + ': não existe.') }; Assert-Trusted $p }
            $data=Get-Content -LiteralPath $nxFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if([string]$data.RunId -cne $nxRunId){ throw 'o registro protegido é de outra aplicação.' }
            $done=@(Read-DebloatDone)
          } catch { throw ('Pasta protegida do Desfazer recusada: ' + $_.Exception.Message + ' Nada foi restaurado.') }
          $all=@($data.Items)
          $left=@($all | Where-Object { $done -cnotcontains ([string]$_.Id+'|'+[string]$_.Kind+'|'+[string]$_.Target) })
          if($all.Count -gt 0 -and $left.Count -eq 0){ throw 'Esta aplicação já foi desfeita; nada foi restaurado.' }
          $left
        }

        """;

    private static string RunIdLine(string runId) => DebloatProtectedStore.ValidRunId(runId)
        ? "$nxRunId=" + Q(runId) + "\n" : throw new ArgumentException("Identificador de aplicação inválido: " + runId);

    /// <summary>
    /// Script elevado do Aplicar. Com itens elevados: abre a pasta protegida, lê o estado anterior de cada alvo e o grava lá
    /// antes de qualquer mudança (sem gravar, nada muda); cria e confirma o ponto de restauração; só então desativa. No fim,
    /// regrava o registro protegido só com o que mudou. Sem aspas duplas: o executor passa o script em -Command.
    /// </summary>
    public static string Apply(IEnumerable<DebloatItem> items, string runId)
    {
        var list = items.ToArray();
        foreach (var item in list) if (!item.Elevated) throw new ArgumentException("Item sem etapa elevada: " + item.Id);
        var restorePoint = RestorePointProtection.Script;
        const string driverDescription = "'Nexo before driver installation'", driverType = "-RestorePointType APPLICATION_INSTALL";
        if (!restorePoint.Contains(driverDescription, StringComparison.Ordinal) || !restorePoint.Contains(driverType, StringComparison.Ordinal))
            throw new InvalidOperationException("O script do ponto de restauração mudou; o debloat não adapta a descrição e o tipo sem ele.");
        restorePoint = restorePoint.Replace(driverDescription, Q(DebloatService.RestorePointDescription)).Replace(driverType, "-RestorePointType MODIFY_SETTINGS");

        var body = new StringBuilder(RunIdLine(runId));
        if (list.Length > 0)
        {
            body.Append(StoreFunctions);
            body.AppendLine("Open-DebloatStore");
            body.AppendLine("$items=@(); $before=@(); $prev=@{}; $readError=@{}");
            // 1) Só leitura: o estado anterior de cada alvo.
            foreach (var (item, target) in Targets(list))
            {
                var key = Q(item.Id + "|" + target); var row = Row(item, target);
                body.AppendLine("try {");
                switch (item.Kind)
                {
                    case DebloatKind.Service:
                        body.AppendLine($$"""
                            $s=Get-Service -Name {{Q(target)}} -ErrorAction SilentlyContinue
                            if($s){ $d=$false; try { $d=[int](Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+{{Q(target)}}) -Name DelayedAutostart -ErrorAction Stop).DelayedAutostart -eq 1 } catch {}
                              $t=[string]$s.StartType; if($t -eq 'Automatic' -and $d){ $t='AutomaticDelayed' }
                              $prev[{{key}}]=$t+'|'+([string]$s.Status -eq 'Running'); $before+={{row}}$prev[{{key}}]} }
                            """);
                        break;
                    case DebloatKind.ScheduledTask:
                        var parts = target.Split('|', 2);
                        body.AppendLine($$"""
                            $x=Get-ScheduledTask -TaskPath {{Q(parts[0])}} -TaskName {{Q(parts[1])}} -ErrorAction SilentlyContinue
                            if($x){ $prev[{{key}}]=[string]$x.State; $before+={{row}}$prev[{{key}}]} }
                            """);
                        break;
                    case DebloatKind.OptionalFeature:
                        body.AppendLine($$"""
                            $f=$null; try { $f=Get-WindowsOptionalFeature -Online -FeatureName {{Q(target)}} -ErrorAction Stop } catch {}
                            if($f){ $prev[{{key}}]=[string]$f.State; $before+={{row}}$prev[{{key}}]} }
                            """);
                        break;
                }
                body.AppendLine($"}} catch {{ $readError[{key}]=$_.Exception.Message }}");
            }
            // 2) Sem o estado anterior gravado na pasta protegida, nada muda e nenhum ponto de restauração é criado.
            body.AppendLine("try { Save-DebloatUndo $before } catch { throw ('Pasta protegida do Desfazer recusada: o estado anterior não pôde ser gravado (' + $_.Exception.Message + '). Nenhum serviço, tarefa ou recurso foi alterado.') }");
        }
        // 3) Ponto de restauração confirmado antes de qualquer mudança.
        body.AppendLine("$rp=& { " + restorePoint + " }");
        body.AppendLine("$seq=[int]$rp.SequenceNumber");
        if (list.Length == 0) { body.Append("[pscustomobject]@{RestorePoint=$seq;Items=@()}"); return body.ToString(); }

        // 4) As mudanças, cada alvo no seu try: um erro num alvo não derruba os demais.
        foreach (var (item, target) in Targets(list))
        {
            var id = Q(item.Id); var key = Q(item.Id + "|" + target);
            string Result(string status, string previous, string detail) => $"$items+=[pscustomobject]@{{Id={id};Target={Q(target)};Status='{status}';Previous={previous};Detail={detail}}}";
            body.AppendLine("try {");
            body.AppendLine($"if($readError.ContainsKey({key})){{ {Result("Failed", "''", $"$readError[{key}]")} }}");
            var missing = item.Kind switch { DebloatKind.Service => "serviço inexistente neste Windows", DebloatKind.ScheduledTask => "tarefa inexistente neste Windows", _ => "recurso inexistente neste PC" };
            body.AppendLine($"elseif(-not $prev.ContainsKey({key})){{ {Result("NotFound", "''", Q(missing))} }}");
            switch (item.Kind)
            {
                case DebloatKind.Service:
                    body.AppendLine($$"""
                        else { $p=$prev[{{key}}]
                          try { Set-Service -Name {{Q(target)}} -StartupType Disabled -ErrorAction Stop
                            try { if($p.EndsWith('|True')){ Stop-Service -Name {{Q(target)}} -Force -ErrorAction Stop }; {{Result("Ok", "$p", "'desativado'")}} }
                            catch { {{Result("Partial", "$p", "('desativado, mas não parou: '+$_.Exception.Message)")}} } }
                          catch { {{Result("Failed", "''", "$_.Exception.Message")}} } }
                        """);
                    break;
                case DebloatKind.ScheduledTask:
                    var parts = target.Split('|', 2);
                    body.AppendLine($$"""
                        else { $p=$prev[{{key}}]
                          try { Get-ScheduledTask -TaskPath {{Q(parts[0])}} -TaskName {{Q(parts[1])}} -ErrorAction Stop | Disable-ScheduledTask -ErrorAction Stop | Out-Null; {{Result("Ok", "$p", "'desativada'")}} }
                          catch { {{Result("Failed", "''", "$_.Exception.Message")}} } }
                        """);
                    break;
                case DebloatKind.OptionalFeature:
                    body.AppendLine($$"""
                        elseif($prev[{{key}}] -eq 'Disabled'){ {{Result("Ok", "'Disabled'", "'já estava desativado'")}} }
                        else { $p=$prev[{{key}}]
                          try { Disable-WindowsOptionalFeature -Online -FeatureName {{Q(target)}} -NoRestart -ErrorAction Stop | Out-Null; {{Result("Ok", "$p", "'desativado; reinicie o Windows para concluir'")}} }
                          catch { {{Result("Failed", "''", "$_.Exception.Message")}} } }
                        """);
                    break;
            }
            body.AppendLine($"}} catch {{ {Result("Failed", "''", "$_.Exception.Message")} }}");
        }
        // 5) O registro protegido fica só com o que mudou. Se esta regravação falhar, vale a de antes (que tem todos os alvos lidos).
        body.AppendLine("$changed=@($before | Where-Object { $b=$_; @($items | Where-Object { $_.Id -ceq $b.Id -and $_.Target -ceq $b.Target -and $_.Status -in 'Ok','Partial' }).Count -gt 0 })");
        body.AppendLine("try { Save-DebloatUndo $changed } catch {}");
        body.Append("[pscustomobject]@{RestorePoint=$seq;Items=$items}");
        return body.ToString();

        static string Row(DebloatItem item, string target) => $"[pscustomobject]@{{Id={Q(item.Id)};Kind='{item.Kind}';Target={Q(target)};Previous=";
    }

    private static IEnumerable<(DebloatItem Item, string Target)> Targets(IEnumerable<DebloatItem> items) => items.SelectMany(item => item.Targets.Select(target => (item, target)));

    /// <summary>
    /// Script elevado do Desfazer. Recebe só o identificador da aplicação: o estado anterior vem da pasta protegida, conferida
    /// de novo aqui, e cada entrada precisa estar na lista de alvos do catálogo, com um estado que o Nexo grava. Os alvos
    /// entram nos comandos como parâmetros, nunca como código.
    /// </summary>
    public static string Undo(string runId)
    {
        var allowed = DebloatCatalog.Items.Where(i => i.Elevated).SelectMany(i => i.Targets.Select(t => Q(i.Id + "|" + i.Kind + "|" + t)));
        return RunIdLine(runId) + StoreFunctions + "$allowed=@(" + string.Join(",", allowed) + ")\n" + """
            $items=@()
            $pending=@(Read-DebloatUndo)
            $prev=@(Read-DebloatDone)
            foreach($e in $pending){
              $id=[string]$e.Id; $kind=[string]$e.Kind; $t=[string]$e.Target; $p=[string]$e.Previous
              $ok=($allowed -ccontains ($id+'|'+$kind+'|'+$t)) -and (($kind -ceq 'Service' -and $p -cmatch '^(Automatic|AutomaticDelayed|Manual|Disabled)\|(True|False)$') -or
                ($kind -ceq 'ScheduledTask' -and $p -cmatch '^(Ready|Running|Queued|Disabled|Unknown)$') -or
                ($kind -ceq 'OptionalFeature' -and $p -cmatch '^(Enabled|Disabled|EnablePending|DisablePending)$'))
              if(-not $ok){ $items+=[pscustomobject]@{Id=$id;Target=$t;Status='Failed';Previous='';Detail='estado anterior fora do catálogo; não alterado'}; continue }
              try {
                if($kind -ceq 'Service'){
                  $parts=$p.Split('|')
                  if($parts[0] -ceq 'AutomaticDelayed'){ Set-Service -Name $t -StartupType Automatic -ErrorAction Stop; & __NEXO_SC__ config $t 'start=' 'delayed-auto' | Out-Null; if($LASTEXITCODE -ne 0){ throw 'sc.exe não aplicou o início atrasado' } }
                  else { Set-Service -Name $t -StartupType $parts[0] -ErrorAction Stop }
                  if($parts[1] -ceq 'True'){ Start-Service -Name $t -ErrorAction Stop }
                  $detail='restaurado para '+$parts[0]
                } elseif($kind -ceq 'ScheduledTask'){
                  $tp=$t.Split('|',2)
                  if($p -cne 'Disabled'){ Get-ScheduledTask -TaskPath $tp[0] -TaskName $tp[1] -ErrorAction Stop | Enable-ScheduledTask -ErrorAction Stop | Out-Null }
                  $detail='restaurada'
                } else {
                  if($p -ceq 'Enabled'){ Enable-WindowsOptionalFeature -Online -FeatureName $t -NoRestart -ErrorAction Stop | Out-Null }
                  $detail='restaurado; reinicie o Windows para concluir'
                }
                $items+=[pscustomobject]@{Id=$id;Target=$t;Status='Ok';Previous='';Detail=$detail;Key=($id+'|'+$kind+'|'+$t)}
              } catch { $items+=[pscustomobject]@{Id=$id;Target=$t;Status='Failed';Previous='';Detail=$_.Exception.Message} }
            }
            # Marcador antes do resultado: o que foi restaurado não volta num 2º Desfazer; o que falhou continua pendente.
            $marked=$true; $markError=''
            $restoredKeys=@($items | Where-Object { $_.Status -ceq 'Ok' } | ForEach-Object { [string]$_.Key })
            if($restoredKeys.Count -gt 0){ try { Save-DebloatDone (@($prev) + $restoredKeys) } catch { $marked=$false; $markError=$_.Exception.Message } }
            [pscustomobject]@{Items=$items;Marked=$marked;MarkError=$markError}
            """.Replace("__NEXO_SC__", ScExecutable);
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

    /// <summary>
    /// Marcador do Desfazer na saída do executor elevado: null quando ele confirmou a gravação (Marked=true); senão, o
    /// motivo. Sem o campo, o marcador não está confirmado.
    /// </summary>
    internal static string? ParseMarker(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var data = document.RootElement.GetProperty("data");
            using var nested = data.ValueKind == JsonValueKind.String ? JsonDocument.Parse(data.GetString()!) : null;
            var body = nested?.RootElement ?? data;
            if (body.TryGetProperty("Marked", out var marked) && marked.ValueKind == JsonValueKind.True) return null;
            return body.TryGetProperty("MarkError", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } text
                ? text : "o executor elevado não confirmou o marcador";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return "o executor elevado não confirmou o marcador"; }
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
