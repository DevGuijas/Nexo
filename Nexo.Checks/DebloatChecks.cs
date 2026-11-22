using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nexo.Support;
namespace Nexo.Checks;

// Remoção de apps e ajustes do menu Ferramentas (#127). Tudo roda com sistema falso: nenhum app, serviço ou registro real muda.
// Os scripts PowerShell rodam com cmdlets falsos quando há um PowerShell disponível (DebloatScriptChecks).
internal static class DebloatChecks
{
    private const int Win10 = 19045;
    private const int Win11 = 22631;

    public static async Task Run(Action<bool, string> check)
    {
        Catalog(check);
        Scripts(check);
        ToolExposure(check);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "pt-BR", "en-US", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Preview(check, culture);
                await Apply(check, culture);
                await Undo(check, culture);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
        await Store(check);
        await DebloatScriptChecks.Run(check);
    }

    private static void Catalog(Action<bool, string> check)
    {
        var ten = DebloatCatalog.ForBuild(Win10).Select(i => i.Id).ToHashSet();
        var eleven = DebloatCatalog.ForBuild(Win11).Select(i => i.Id).ToHashSet();
        check(!ten.Overlaps(["bing-search", "clipchamp", "linkedin", "dev-home", "family", "power-automate", "teams-personal", "copilot-app", "recall"])
            && ten.IsSupersetOf(["bing-news", "cortana", "xbox-services", "sysmain", "visual-effects", "file-extensions"]),
            "Debloat: o build 19045 usa o catálogo do Windows 10, sem os itens só do 11");
        check(eleven.IsSupersetOf(["bing-search", "clipchamp", "linkedin", "dev-home", "recall", "bing-news", "xbox-login", "widgets-package"]),
            "Debloat: o build 22631 usa o catálogo do Windows 11 com os itens comuns");
        check(!DebloatCatalog.IsWindows11(21999) && DebloatCatalog.IsWindows11(22000), "Debloat: o Windows 11 começa no build 22000");

        var targets = DebloatCatalog.Items.SelectMany(i => i.Targets.Concat(i.Values.SelectMany(v => new[] { v.Key, v.Name }))).ToArray();
        var excluded = DebloatCatalog.Excluded.Where(x => targets.Any(t => t.Contains(x, StringComparison.OrdinalIgnoreCase))).ToArray();
        check(excluded.Length == 0, "Debloat: nenhum item fora do escopo da #127 entra no catálogo" + (excluded.Length == 0 ? "" : ": " + string.Join(", ", excluded)));
        check(DebloatCatalog.Items.SelectMany(i => i.Values).All(v => !v.Key.Contains("Policies", StringComparison.OrdinalIgnoreCase)
                && !v.Key.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase) && !v.Key.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)
                && !v.Key.Contains("CLSID", StringComparison.OrdinalIgnoreCase)),
            "Debloat: o registro é só HKCU, sem chave de política nem de CLSID");
        check(DebloatCatalog.Items.Select(i => i.Id).Distinct().Count() == DebloatCatalog.Items.Count, "Debloat: cada item tem um identificador único");
        check(DebloatCatalog.Items.All(i => i.Elevated == (i.Kind is DebloatKind.Service or DebloatKind.ScheduledTask or DebloatKind.OptionalFeature))
            && DebloatCatalog.Items.Where(i => i.Kind == DebloatKind.App || i.Kind == DebloatKind.UserRegistry).All(i => !i.Elevated),
            "Debloat: só serviços, tarefas e recursos opcionais usam o executor elevado");
        var login = DebloatCatalog.Find("xbox-login")!; var sysmain = DebloatCatalog.Find("sysmain")!;
        check(login.Gate == DebloatGate.XboxGames && !login.Default && sysmain.Gate == DebloatGate.SystemSsd && !sysmain.Default
            && !DebloatCatalog.Find("xbox-services")!.Default && !DebloatCatalog.Find("widgets-package")!.Default,
            "Debloat: login do Xbox, SysMain, serviços e pacote de Widgets vêm desmarcados, com as travas");
        check(DebloatCatalog.Credit.Contains("rivaed/Windows10-Debloat") && DebloatCatalog.Credit.Contains("rivaed/Windows11-Debloat") && DebloatCatalog.Credit.Contains("MIT"),
            "Debloat: o crédito cita os dois repositórios de origem e a licença MIT");
    }

    private static void Scripts(Action<bool, string> check)
    {
        var remove = DebloatScripts.RemoveApps([("Microsoft.BingNews", "Microsoft.BingNews_4.55.62231.0_x64__8wekyb3d8bbwe")]);
        check(remove.Contains("Remove-AppxPackage -Package $p") && remove.Contains("'Microsoft.BingNews_4.55.62231.0_x64__8wekyb3d8bbwe'")
            && !remove.Contains("AllUsers", StringComparison.OrdinalIgnoreCase) && !remove.Contains("Provisioned", StringComparison.OrdinalIgnoreCase),
            "Debloat: a remoção de apps é só do usuário atual, sem -AllUsers e sem desprovisionar");
        check(remove.Contains("continua instalado"), "Debloat: a remoção confere que o pacote saiu antes de dizer Ok");
        bool Rejects(string name, string full) { try { DebloatScripts.RemoveApps([(name, full)]); return false; } catch (ArgumentException) { return true; } }
        check(Rejects("Microsoft.BingNews", "Microsoft.BingNews_1';Remove-Item C:\\ -Recurse;'") && Rejects("Microsoft.BingNews", "Microsoft.WindowsStore_1.0_x64__8wekyb3d8bbwe")
            && Rejects("Microsoft.Bing News", "Microsoft.Bing News_1_x64__8"),
            "Debloat: nome de pacote fora do formato, ou de outro pacote, é recusado antes de gerar o script");

        var inventory = DebloatScripts.Inventory(DebloatCatalog.ForBuild(Win11));
        check(inventory.Contains("Get-AppxPackage -ErrorAction SilentlyContinue") && !inventory.Contains("AllUsers", StringComparison.OrdinalIgnoreCase)
            && inventory.Contains("'Microsoft.MinecraftUWP'") && inventory.Contains("Win32_OptionalFeature") && inventory.Contains("Get-PhysicalDisk"),
            "Debloat: o inventário lê só os pacotes do usuário, os jogos Xbox, o Recall e o disco do sistema");
        check(!DebloatScripts.Inventory(DebloatCatalog.ForBuild(Win10)).Contains("Win32_OptionalFeature"), "Debloat: o Windows 10 não consulta o Recall");

        var elevatedItems = DebloatCatalog.Items.Where(i => i.Elevated).ToArray();
        var apply = DebloatScripts.Apply(elevatedItems);
        check(apply.IndexOf("Checkpoint-Computer", StringComparison.Ordinal) >= 0 && apply.IndexOf("Checkpoint-Computer", StringComparison.Ordinal) < apply.IndexOf("Set-Service", StringComparison.Ordinal)
            && apply.Contains(DebloatService.RestorePointDescription) && !apply.Contains("Nexo before driver installation"),
            "Debloat: o ponto de restauração vem antes de qualquer serviço, com descrição própria");
        check(apply.Contains("Set-Service -Name 'SysMain' -StartupType Disabled") && apply.Contains("Disable-ScheduledTask") && apply.Contains("Disable-WindowsOptionalFeature -Online -FeatureName 'Recall' -NoRestart")
            && !apply.Contains("Remove-Appx") && !apply.Contains("HKCU", StringComparison.OrdinalIgnoreCase),
            "Debloat: o script elevado só mexe em serviços, tarefas e recursos; apps e HKCU ficam fora dele");
        check(!apply.Contains('"') && !DebloatScripts.Undo([new("sysmain", DebloatKind.Service, "SysMain", "AutomaticDelayed|True")]).Contains('"'),
            "Debloat: os scripts elevados não usam aspas duplas, porque vão em -Command");
        bool ApplyRejects(DebloatItem item) { try { DebloatScripts.Apply([item]); return false; } catch (ArgumentException) { return true; } }
        check(ApplyRejects(DebloatCatalog.Find("bing-news")!) && ApplyRejects(DebloatCatalog.Find("visual-effects")!), "Debloat: app ou registro nunca entra no script elevado");
    }

    private static void ToolExposure(Action<bool, string> check)
    {
        var names = ToolCatalog.Definitions.Select(d => JsonSerializer.SerializeToElement(d).GetProperty("function")).ToArray();
        var text = string.Join("\n", names.Select(n => n.GetRawText()));
        check(!DebloatCatalog.Items.Any(i => ToolCatalog.IsKnown(i.Id)) && !ToolCatalog.IsKnown("debloat")
            && !text.Contains("Remove-Appx", StringComparison.OrdinalIgnoreCase) && !text.Contains("debloat", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("bloatware", StringComparison.OrdinalIgnoreCase),
            "Debloat: a ferramenta não é exposta ao modelo; nenhuma ferramenta do catálogo remove apps");
    }

    internal static DebloatInventory Inventory(bool games = false, string disk = "SSD", string? recall = "Enabled", string sysmainStart = "Automatic") => new(
        [
            new("Microsoft.BingNews", "Microsoft.BingNews_4.55.1.0_x64__8wekyb3d8bbwe", false),
            new("Microsoft.BingWeather", "Microsoft.BingWeather_4.53.1.0_x64__8wekyb3d8bbwe", false),
            new("Microsoft.XboxIdentityProvider", "Microsoft.XboxIdentityProvider_12.1.0_x64__8wekyb3d8bbwe", false),
            new("Microsoft.Xbox.TCUI", "Microsoft.Xbox.TCUI_1.24.1.0_x64__8wekyb3d8bbwe", false),
            new("Microsoft.WindowsFeedbackHub", "Microsoft.WindowsFeedbackHub_1.0_x64__8wekyb3d8bbwe", true),
            new("Microsoft.Windows.DevHome", "Microsoft.Windows.DevHome_0.1_x64__8wekyb3d8bbwe", false),
            .. games ? new[] { new DebloatInstalledApp("Microsoft.MinecraftUWP", "Microsoft.MinecraftUWP_1.21.0_x64__8wekyb3d8bbwe", false) } : [],
        ],
        [new("XblGameSave", "Manual", false), new("XboxNetApiSvc", "Manual", true), new("SysMain", sysmainStart, true)],
        [new(@"\Microsoft\Windows\Customer Experience Improvement Program\", "Consolidator", "Ready"), new(@"\Microsoft\Windows\Autochk\", "Proxy", "Disabled")],
        disk, recall);

    private static void Preview(Action<bool, string> check, string culture)
    {
        var registry = new FakeRegistry();
        var items = DebloatService.Preview(Win11, Inventory(), registry).ToDictionary(i => i.Item.Id);
        check(items.ContainsKey("bing-news") && items.ContainsKey("dev-home") && !items.ContainsKey("clipchamp") && !items.ContainsKey("solitaire"),
            "Debloat: só aparecem os apps instalados no PC (" + culture + ")");
        check(!items.ContainsKey("feedback-hub"), "Debloat: pacote marcado como não removível não aparece (" + culture + ")");
        check(items["xbox-login"].Availability == DebloatAvailability.Available
            && DebloatService.Preview(Win11, Inventory(games: true), registry).Single(i => i.Item.Id == "xbox-login") is { Availability: DebloatAvailability.NeedsConfirmation } gated
            && gated.Detail.Contains("Microsoft.MinecraftUWP") && gated.Detail.Contains("Minecraft e do Game Pass"),
            "Debloat: com Minecraft ou app Xbox instalado, o login do Xbox exige confirmação explícita (" + culture + ")");
        check(items.ContainsKey("sysmain") && !DebloatService.Preview(Win11, Inventory(disk: "HDD"), registry).Any(i => i.Item.Id == "sysmain")
            && !DebloatService.Preview(Win11, Inventory(disk: ""), registry).Any(i => i.Item.Id == "sysmain"),
            "Debloat: o SysMain só aparece com o disco do sistema em SSD; disco desconhecido também o esconde (" + culture + ")");
        check(DebloatService.Preview(Win11, Inventory(sysmainStart: "Disabled"), registry).Single(i => i.Item.Id == "sysmain").Availability == DebloatAvailability.AlreadyApplied,
            "Debloat: serviço já desativado aparece como já aplicado (" + culture + ")");
        check(items["ceip-tasks"].PresentTargets.Count == 2 && items["ceip-tasks"].Availability == DebloatAvailability.Available, "Debloat: tarefas ausentes do build não contam (" + culture + ")");
        check(items["recall"].Availability == DebloatAvailability.Available && !DebloatService.Preview(Win11, Inventory(recall: null), registry).Any(i => i.Item.Id == "recall")
            && !DebloatService.Preview(Win11, Inventory(recall: "Unknown"), registry).Any(i => i.Item.Id == "recall")
            && DebloatService.Preview(Win11, Inventory(recall: "Disabled"), registry).Single(i => i.Item.Id == "recall").Availability == DebloatAvailability.AlreadyApplied
            && !DebloatService.Preview(Win10, Inventory(), registry).Any(i => i.Item.Id == "recall"),
            "Debloat: o Recall só aparece no Windows 11 onde o recurso existe (" + culture + ")");
        registry.Set(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", DebloatValueKind.DWord, "0");
        registry.Set(@"Control Panel\Desktop", "UserPreferencesMask", DebloatValueKind.Binary, "9e-1e-07-80-12-00-00-00");
        var again = DebloatService.Preview(Win11, Inventory(), registry).ToDictionary(i => i.Item.Id);
        check(again["file-extensions"].Availability == DebloatAvailability.AlreadyApplied && again["visual-effects"].Availability == DebloatAvailability.Available,
            "Debloat: ajuste do registro já igual aparece como já aplicado; diferente continua disponível (" + culture + ")");
    }

    private static async Task Apply(Action<bool, string> check, string culture)
    {
        using var dir = new TempDir();
        var system = new FakeSystem(Inventory());
        var audit = new List<DebloatAudit>();
        var undo = new DebloatUndoStore(Path.Combine(dir.Path, "undo.json"));
        var service = new DebloatService(system, undo, audit.Add);
        var preview = await service.PreviewAsync(CancellationToken.None);
        system.Registry.Set(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", DebloatValueKind.DWord, "1");
        var result = await service.ApplyAsync(preview, ["bing-news", "file-extensions", "xbox-services", "ceip-tasks"], false, null, CancellationToken.None);
        var byId = result.Items.ToDictionary(i => i.Id);
        check(result.Status == "Completed" && result.RestorePoint is { Ready: true, SequenceNumber: 41 } && byId.Values.All(i => i.Status == DebloatStatus.Ok),
            "Debloat: aplicação completa confirma o ponto de restauração e termina Ok em cada item (" + culture + ")");
        check(system.Elevated.Count == 1 && system.Elevated[0].Contains("XblGameSave") && system.Elevated[0].Contains("Consolidator") && !system.Elevated[0].Contains("SysMain"),
            "Debloat: um único script elevado, só com os itens escolhidos (" + culture + ")");
        check(system.Registry.Value(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt") == "0" && system.Registry.Notified == 1,
            "Debloat: o registro do usuário é gravado no processo e o Windows é avisado (" + culture + ")");
        var removal = system.User.Single(s => s.Contains("Remove-AppxPackage"));
        check(removal.Contains("Microsoft.BingNews_4.55.1.0_x64__8wekyb3d8bbwe") && !removal.Contains("BingWeather"), "Debloat: só o app escolhido é removido (" + culture + ")");
        var run = undo.Latest()!;
        check(result.RunId == run.Id && run.RestorePoint == 41 && run.Entries.Any(e => e.Target == "XblGameSave" && e.Previous == "Manual|False")
            && run.Entries.Any(e => e.Target == "XboxNetApiSvc" && e.Previous == "Manual|True")
            && run.Entries.Any(e => e.Kind == DebloatKind.UserRegistry && e.Target.EndsWith("|HideFileExt") && e.Previous == "DWord:1")
            && run.Entries.Any(e => e.Kind == DebloatKind.ScheduledTask && e.Previous == "Ready")
            && run.Entries.Any(e => e.Kind == DebloatKind.App && e.Target == "Microsoft.BingNews"),
            "Debloat: o estado anterior de cada alvo fica guardado para o Desfazer (" + culture + ")");
        check(audit.First().Action == "Debloat" && audit.First().Outcome == "Started" && audit.Last() is { Action: "Debloat", Outcome: "Completed" }
            && audit.Count(a => a.Action == "Item") == 4 && audit.Any(a => a.Action == "RestorePoint" && a.Outcome == "Confirmed"),
            "Debloat: a auditoria registra início, ponto de restauração, cada item e o fim (" + culture + ")");

        // UAC recusado: nada elevado roda, mas os itens do usuário seguem e o resultado é parcial.
        var denied = new FakeSystem(Inventory()) { ElevatedError = new Win32Exception(1223) };
        var deniedService = new DebloatService(denied, new DebloatUndoStore(Path.Combine(dir.Path, "denied.json")), _ => { });
        var deniedResult = await deniedService.ApplyAsync(await deniedService.PreviewAsync(CancellationToken.None), ["bing-news", "advertising-id", "sysmain"], false, null, CancellationToken.None);
        var d = deniedResult.Items.ToDictionary(i => i.Id);
        check(deniedResult.Status == "Partial" && deniedResult.RestorePoint is { Ready: false } && d["sysmain"].Status == DebloatStatus.NotApplied
            && d["sysmain"].Detail.Contains("Ponto de restauração não confirmado") && d["sysmain"].Detail.Contains("recusada")
            && d["bing-news"].Status == DebloatStatus.Ok && d["advertising-id"].Status == DebloatStatus.Ok,
            "Debloat: UAC recusado não altera serviços, mantém os itens do usuário e vira resultado parcial (" + culture + ")");

        // Ponto de restauração não confirmado pelo Windows: mesmo efeito.
        var noPoint = new FakeSystem(Inventory()) { ElevatedOutput = _ => "{\"success\":false,\"error\":\"O novo ponto de restauração não foi confirmado.\"}" };
        var noPointService = new DebloatService(noPoint, new DebloatUndoStore(Path.Combine(dir.Path, "nopoint.json")), _ => { });
        var noPointResult = await noPointService.ApplyAsync(await noPointService.PreviewAsync(CancellationToken.None), ["ceip-tasks", "file-extensions"], false, null, CancellationToken.None);
        check(noPointResult.Items.Single(i => i.Id == "ceip-tasks").Status == DebloatStatus.NotApplied && noPointResult.Items.Single(i => i.Id == "file-extensions").Status == DebloatStatus.Ok
            && noPointResult.Status == "Partial", "Debloat: sem ponto de restauração confirmado, só os itens do usuário seguem (" + culture + ")");
        var zeroPoint = new FakeSystem(Inventory()) { ElevatedOutput = _ => "{\"success\":true,\"data\":{\"RestorePoint\":0,\"Items\":[]}}" };
        var zeroService = new DebloatService(zeroPoint, new DebloatUndoStore(Path.Combine(dir.Path, "zero.json")), _ => { });
        var zeroResult = await zeroService.ApplyAsync(await zeroService.PreviewAsync(CancellationToken.None), ["ceip-tasks"], false, null, CancellationToken.None);
        check(zeroResult.RestorePoint is { Ready: false } && zeroResult.Items.Single().Status == DebloatStatus.NotApplied, "Debloat: sucesso sem número de ponto não conta como ponto confirmado (" + culture + ")");

        // Login do Xbox com jogos: sem confirmação fica bloqueado; com confirmação, sai.
        var games = new FakeSystem(Inventory(games: true));
        var gamesService = new DebloatService(games, new DebloatUndoStore(Path.Combine(dir.Path, "games.json")), _ => { });
        var gamesPreview = await gamesService.PreviewAsync(CancellationToken.None);
        var blocked = await gamesService.ApplyAsync(gamesPreview, ["xbox-login"], false, null, CancellationToken.None);
        check(blocked.Items.Single().Status == DebloatStatus.NotApplied && blocked.Items.Single().Detail.StartsWith("Bloqueado") && !games.User.Any(s => s.Contains("Remove-AppxPackage")),
            "Debloat: login do Xbox com jogos e sem confirmação não é removido (" + culture + ")");
        var confirmed = await gamesService.ApplyAsync(gamesPreview, ["xbox-login"], true, null, CancellationToken.None);
        check(confirmed.Items.Single().Status == DebloatStatus.Ok && games.User.Single(s => s.Contains("Remove-AppxPackage")).Contains("Microsoft.XboxIdentityProvider_") && games.User.Single(s => s.Contains("Remove-AppxPackage")).Contains("Microsoft.Xbox.TCUI_"),
            "Debloat: com a confirmação explícita, os dois pacotes do login do Xbox são removidos (" + culture + ")");

        // Resultados parciais: um serviço falha, um app continua instalado.
        var partial = new FakeSystem(Inventory()) { FailService = "XboxNetApiSvc", FailPackage = "Microsoft.BingWeather" };
        var partialService = new DebloatService(partial, new DebloatUndoStore(Path.Combine(dir.Path, "partial.json")), _ => { });
        var partialResult = await partialService.ApplyAsync(await partialService.PreviewAsync(CancellationToken.None), ["xbox-services", "bing-news", "bing-weather", "sysmain"], false, null, CancellationToken.None);
        var p = partialResult.Items.ToDictionary(i => i.Id);
        check(p["xbox-services"].Status == DebloatStatus.Partial && p["bing-weather"].Status == DebloatStatus.Failed && p["bing-weather"].Detail.Contains("continua instalado")
            && p["bing-news"].Status == DebloatStatus.Ok && p["sysmain"].Status == DebloatStatus.Ok && partialResult.Status == "Partial",
            "Debloat: falha num alvo vira Parcial no item e Falha quando nada saiu (" + culture + ")");
        check(DebloatService.Aggregate(["NotFound", "NotFound"]) == DebloatStatus.NotFound && DebloatService.Aggregate(["Ok", "NotFound"]) == DebloatStatus.Ok
            && DebloatService.Aggregate(["Failed"]) == DebloatStatus.Failed && DebloatService.Aggregate(["Weird"]) == DebloatStatus.Failed && DebloatService.Aggregate([]) == DebloatStatus.NotFound,
            "Debloat: status desconhecido do Windows conta como falha; alvo ausente não é sucesso (" + culture + ")");

        // Seleção inválida e cancelamento.
        var odd = await service.ApplyAsync(preview, ["clipchamp", "inexistente", "feedback-hub"], false, null, CancellationToken.None);
        check(odd.Items.All(i => i.Status == DebloatStatus.NotApplied) && odd.Status == "Partial", "Debloat: item ausente do PC ou desconhecido não é aplicado (" + culture + ")");
        var already = new FakeSystem(Inventory(sysmainStart: "Disabled"));
        var alreadyService = new DebloatService(already, new DebloatUndoStore(Path.Combine(dir.Path, "already.json")), _ => { });
        var alreadyResult = await alreadyService.ApplyAsync(await alreadyService.PreviewAsync(CancellationToken.None), ["sysmain"], false, null, CancellationToken.None);
        check(alreadyResult.Items.Single().Status == DebloatStatus.NotApplied && !already.Elevated.Single().Contains("SysMain"), "Debloat: item já aplicado não roda de novo (" + culture + ")");
        var canceledSystem = new FakeSystem(Inventory());
        var canceledService = new DebloatService(canceledSystem, new DebloatUndoStore(Path.Combine(dir.Path, "canceled.json")), _ => { });
        var canceledPreview = await canceledService.PreviewAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var canceled = await canceledService.ApplyAsync(canceledPreview, ["bing-news", "sysmain"], false, null, cancel.Token);
        check(canceled.Status == "Canceled" && canceled.Items.All(i => i.Status == DebloatStatus.NotApplied) && canceledSystem.Elevated.Count == 0 && canceledSystem.User.Count == 1,
            "Debloat: cancelar antes de iniciar não roda nenhum script de alteração (" + culture + ")");
        var midway = new FakeSystem(Inventory());
        using var late = new CancellationTokenSource();
        midway.AfterElevated = late.Cancel;
        var midService = new DebloatService(midway, new DebloatUndoStore(Path.Combine(dir.Path, "mid.json")), _ => { });
        var mid = await midService.ApplyAsync(await midService.PreviewAsync(CancellationToken.None), ["sysmain", "bing-news", "file-extensions"], false, null, late.Token);
        var m = mid.Items.ToDictionary(i => i.Id);
        check(mid.Status == "Canceled" && m["sysmain"].Status == DebloatStatus.Ok && m["bing-news"].Status == DebloatStatus.NotApplied && m["file-extensions"].Status == DebloatStatus.NotApplied
            && !midway.User.Any(s => s.Contains("Remove-AppxPackage")),
            "Debloat: cancelar depois da etapa elevada mantém o resultado dela e não inicia as próximas (" + culture + ")");
    }

    private static async Task Undo(Action<bool, string> check, string culture)
    {
        using var dir = new TempDir();
        var system = new FakeSystem(Inventory());
        var store = new DebloatUndoStore(Path.Combine(dir.Path, "undo.json"));
        var service = new DebloatService(system, store, _ => { });
        const string Desktop = @"Control Panel\Desktop";
        system.Registry.Set(Desktop, "UserPreferencesMask", DebloatValueKind.Binary, "9E-3E-07-80-12-00-00-00");
        var preview = await service.PreviewAsync(CancellationToken.None);
        await service.ApplyAsync(preview, ["visual-effects", "sysmain", "ceip-tasks", "bing-news", "recall"], false, null, CancellationToken.None);
        check(system.Registry.Value(Desktop, "UserPreferencesMask") == "90-12-03-80-12-00-00-00" && system.Registry.Value(Desktop, "DragFullWindows") == "0",
            "Debloat: efeitos visuais gravam a máscara de desempenho (" + culture + ")");
        var run = store.Latest()!;
        var result = await service.UndoAsync(run, null, CancellationToken.None);
        var byId = result.Items.ToDictionary(i => i.Id);
        var script = system.Elevated.Last();
        check(script.Contains("Set-Service -Name 'SysMain' -StartupType Automatic") && script.Contains("Start-Service -Name 'SysMain'")
            && script.Contains("-TaskName 'Consolidator'") && script.Contains("Enable-ScheduledTask") && !script.Contains("'Proxy'")
            && script.Contains("Enable-WindowsOptionalFeature -Online -FeatureName 'Recall'") && !script.Contains("Checkpoint-Computer"),
            "Debloat: o Desfazer volta serviço, tarefa e recurso ao estado anterior, sem criar outro ponto (" + culture + ")");
        check(system.Registry.Value(Desktop, "UserPreferencesMask") == "9E-3E-07-80-12-00-00-00" && system.Registry.Value(Desktop, "DragFullWindows") == null,
            "Debloat: o Desfazer devolve o valor anterior e apaga o que não existia (" + culture + ")");
        check(byId["bing-news"].Status == DebloatStatus.NotApplied && byId["bing-news"].Detail.Contains("Microsoft Store") && byId["bing-news"].Detail.Contains("Microsoft.BingNews")
            && byId["visual-effects"].Status == DebloatStatus.Ok && byId["sysmain"].Status == DebloatStatus.Ok && result.Status == "Completed"
            && store.Latest()!.UndoneAt != null && !store.Latest()!.CanUndo,
            "Debloat: app removido é indicado para reinstalar pela Store e a aplicação fica marcada como desfeita (" + culture + ")");

        var delayed = DebloatScripts.Undo([new("sysmain", DebloatKind.Service, "SysMain", "AutomaticDelayed|False")]);
        check(delayed.Contains("sc.exe config 'SysMain' 'start=' 'delayed-auto'") && !delayed.Contains("Start-Service"), "Debloat: início atrasado volta como atrasado (" + culture + ")");

        // Arquivo do Desfazer adulterado: alvo fora do catálogo ou estado inválido não chega ao script elevado nem ao registro.
        var forged = new DebloatRun
        {
            At = DateTimeOffset.UtcNow,
            Entries =
            [
                new("sysmain", DebloatKind.Service, "WinDefend", "Disabled|False"),
                new("sysmain", DebloatKind.Service, "SysMain", "Boot|False"),
                new("ceip-tasks", DebloatKind.ScheduledTask, @"\Microsoft\Windows\Defrag\|ScheduledDefrag", "Ready"),
                new("file-extensions", DebloatKind.UserRegistry, @"Software\Microsoft\Windows\CurrentVersion\Run|Malware", "String:C:\\x.exe"),
                new("file-extensions", DebloatKind.UserRegistry, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|HideFileExt", "QWord:1"),
                new("bing-news", DebloatKind.Service, "SysMain", "Disabled|False"),
            ],
        };
        var forgedSystem = new FakeSystem(Inventory());
        var forgedResult = await new DebloatService(forgedSystem, new DebloatUndoStore(Path.Combine(dir.Path, "forged.json")), _ => { }).UndoAsync(forged, null, CancellationToken.None);
        check(forgedResult.Items.Count == 6 && forgedResult.Items.All(i => i.Status == DebloatStatus.Failed) && forgedSystem.Elevated.Count == 0 && forgedSystem.Registry.Writes == 0
            && forgedResult.Status == "Partial" && forged.UndoneAt == null,
            "Debloat: Desfazer adulterado não aciona o executor elevado nem grava registro (" + culture + ")");
        var deniedSystem = new FakeSystem(Inventory()) { ElevatedError = new Win32Exception(1223) };
        var deniedRun = new DebloatRun { At = DateTimeOffset.UtcNow, Entries = [new("sysmain", DebloatKind.Service, "SysMain", "Manual|False"), new("file-extensions", DebloatKind.UserRegistry, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|HideFileExt", "DWord:1")] };
        var deniedUndo = await new DebloatService(deniedSystem, new DebloatUndoStore(Path.Combine(dir.Path, "denied-undo.json")), _ => { }).UndoAsync(deniedRun, null, CancellationToken.None);
        check(deniedUndo.Status == "Partial" && deniedUndo.Items.Single(i => i.Id == "sysmain").Status == DebloatStatus.NotApplied
            && deniedUndo.Items.Single(i => i.Id == "file-extensions").Status == DebloatStatus.Ok && deniedRun.UndoneAt == null,
            "Debloat: Desfazer com UAC recusado restaura o registro e deixa o resto para outra tentativa (" + culture + ")");
    }

    private static async Task Store(Action<bool, string> check)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sub", "undo.json");
        var store = new DebloatUndoStore(path);
        check(store.Load().Count == 0 && store.Latest() == null, "Debloat: sem arquivo, não há o que desfazer");
        var older = new DebloatRun { At = DateTimeOffset.UtcNow.AddHours(-1), Entries = [new("sysmain", DebloatKind.Service, "SysMain", "Manual|False")] };
        var newer = new DebloatRun { At = DateTimeOffset.UtcNow, Entries = [new("file-extensions", DebloatKind.UserRegistry, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|HideFileExt", "absent")] };
        store.Save(older); store.Save(newer);
        var reloaded = new DebloatUndoStore(path);
        check(reloaded.Latest()!.Id == newer.Id && reloaded.Load().Count == 2 && reloaded.Load().Single(r => r.Id == older.Id).Entries.Single().Previous == "Manual|False" && !File.Exists(path + ".tmp"),
            "Debloat: o Desfazer persiste em disco e volta na próxima abertura");
        for (var i = 0; i < 25; i++) store.Save(new DebloatRun { At = DateTimeOffset.UtcNow.AddMinutes(i) });
        check(store.Load().Count == 20, "Debloat: o histórico do Desfazer guarda só as 20 aplicações mais recentes");
        await File.WriteAllTextAsync(path, "{ quebrado");
        check(store.Load().Count == 0, "Debloat: arquivo do Desfazer ilegível vira lista vazia, sem exceção");
        check(DebloatScripts.ParseState("absent") == DebloatRegistryState.Absent && DebloatScripts.ParseState("DWord:1")?.Value == "1" && DebloatScripts.ParseState("DWord:-1") != null
            && DebloatScripts.ParseState("Binary:90-12") != null && DebloatScripts.ParseState("Binary:zz") == null && DebloatScripts.ParseState("DWord:1,5") == null
            && DebloatScripts.ParseState("QWord:1") == null && DebloatScripts.ParseState("99:1") == null && DebloatScripts.ParseState("String:") != null,
            "Debloat: o estado anterior do registro só é aceito nos formatos que o Nexo grava");
        var parsed = DebloatScripts.ParseInventory("{\"Apps\":{\"Name\":\"Microsoft.BingNews\",\"FullName\":\"Microsoft.BingNews_1_x64__8\",\"NonRemovable\":false},\"Services\":[],\"Tasks\":null,\"SystemDisk\":\"SSD\",\"Recall\":null}");
        check(parsed.Apps.Single().Name == "Microsoft.BingNews" && parsed.Services.Count == 0 && parsed.Tasks.Count == 0 && parsed.SystemDisk == "SSD" && parsed.Recall == null,
            "Debloat: o inventário aceita objeto único, lista vazia e nulo do ConvertTo-Json");
        check(DebloatScripts.ParseRemoval("{\"Package\":\"A_1\",\"Status\":\"Ok\",\"Detail\":\"\"}")["A_1"].Status == "Ok"
            && DebloatScripts.ParseRemoval("[{\"Package\":\"A_1\",\"Status\":\"Estranho\"}]")["A_1"].Status == "Failed",
            "Debloat: a remoção só conta Ok com evidência explícita");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NexoDebloat-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    internal sealed class FakeRegistry : IDebloatRegistry
    {
        private readonly Dictionary<string, DebloatRegistryState> values = new(StringComparer.OrdinalIgnoreCase);
        public int Notified, Writes;
        public void Set(string key, string name, DebloatValueKind kind, string value) => values[key + "|" + name] = new(true, kind, value);
        public string? Value(string key, string name) => values.TryGetValue(key + "|" + name, out var v) ? v.Value : null;
        public DebloatRegistryState Read(string key, string name) => values.TryGetValue(key + "|" + name, out var v) ? v : DebloatRegistryState.Absent;
        public void Write(string key, string name, DebloatValueKind kind, string value) { Writes++; Set(key, name, kind, value); }
        public void Delete(string key, string name) => values.Remove(key + "|" + name);
        public void NotifySettingsChanged() => Notified++;
    }

    private sealed class FakeSystem(DebloatInventory inventory) : IDebloatSystem
    {
        public int Build { get; set; } = Win11;
        public FakeRegistry Registry { get; } = new();
        IDebloatRegistry IDebloatSystem.Registry => Registry;
        public List<string> User { get; } = [];
        public List<string> Elevated { get; } = [];
        public Exception? ElevatedError { get; init; }
        public Func<string, string>? ElevatedOutput { get; init; }
        public string? FailService { get; init; }
        public string? FailPackage { get; init; }
        public Action? AfterElevated { get; set; }

        public Task<string> RunUserScriptAsync(string script, int timeoutSeconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            User.Add(script);
            if (!script.Contains("Remove-AppxPackage")) return Task.FromResult(JsonSerializer.Serialize(new
            {
                Apps = inventory.Apps.Select(a => new { a.Name, a.FullName, a.NonRemovable }),
                Services = inventory.Services.Select(s => new { s.Name, s.StartType, s.Running }),
                Tasks = inventory.Tasks.Select(t => new { t.Path, t.Name, t.State }),
                inventory.SystemDisk, inventory.Recall,
            }));
            var removed = inventory.Apps.Where(a => script.Contains("'" + a.FullName + "'")).Select(a => new
            {
                Package = a.FullName,
                Status = FailPackage != null && a.Name == FailPackage ? "Failed" : "Ok",
                Detail = FailPackage != null && a.Name == FailPackage ? "O pacote continua instalado." : "",
            });
            return Task.FromResult(JsonSerializer.Serialize(removed));
        }

        public Task<string> RunElevatedAsync(string script, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Elevated.Add(script);
            if (ElevatedError != null) throw ElevatedError;
            var output = ElevatedOutput?.Invoke(script) ?? Simulate(script);
            AfterElevated?.Invoke();
            return Task.FromResult(output);
        }

        // Interpreta o script gerado só pelo que importa ao serviço: cada alvo citado devolve um resultado com o estado anterior.
        private string Simulate(string script)
        {
            var undo = !script.Contains("Checkpoint-Computer");
            var items = new List<object>();
            foreach (var item in DebloatCatalog.Items.Where(i => i.Elevated))
                foreach (var target in item.Targets)
                {
                    var quoted = item.Kind == DebloatKind.ScheduledTask ? "-TaskName '" + target.Split('|')[1] + "'" : "'" + target + "'";
                    if (!script.Contains("Id='" + item.Id + "'") || !script.Contains(quoted)) continue;
                    string previous = "", status = "Ok";
                    switch (item.Kind)
                    {
                        case DebloatKind.Service:
                            var state = inventory.Services.FirstOrDefault(s => s.Name == target);
                            if (state == null) { status = "NotFound"; break; }
                            previous = state.StartType + "|" + state.Running;
                            if (target == FailService) { status = "Failed"; previous = ""; }
                            break;
                        case DebloatKind.ScheduledTask:
                            var parts = target.Split('|');
                            var task = inventory.Tasks.FirstOrDefault(t => t.Path == parts[0] && t.Name == parts[1]);
                            if (task == null) { status = "NotFound"; break; }
                            previous = task.State;
                            break;
                        case DebloatKind.OptionalFeature:
                            previous = inventory.Recall ?? "";
                            if (previous.Length == 0) status = "NotFound";
                            break;
                    }
                    items.Add(new { Id = item.Id, Target = target, Status = status, Previous = undo ? "" : previous, Detail = status == "Failed" ? "Acesso negado" : "" });
                }
            return JsonSerializer.Serialize(new { success = true, data = undo ? (object)new { Items = items } : new { RestorePoint = 41, Items = items } });
        }
    }
}

// Os scripts reais rodam num PowerShell novo com os cmdlets do Windows trocados por funções falsas: nada é alterado.
// No Windows usa o Windows PowerShell 5.1 (o alvo do Nexo); fora dele, só com NEXO_CHECKS_PWSH. Sem shell, IGNORADO fora do CI.
internal static class DebloatScriptChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var configured = Environment.GetEnvironmentVariable("NEXO_CHECKS_PWSH");
        var shell = !string.IsNullOrWhiteSpace(configured) ? configured
            : OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : null;
        var ci = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
        if (shell == null)
        {
            check(!ci, "Debloat: scripts com cmdlets falsos exigem PowerShell no CI");
            Console.WriteLine("IGNORADO: Debloat: scripts com cmdlets falsos (sem PowerShell; defina NEXO_CHECKS_PWSH)");
            return;
        }

        const string inventoryFakes = """
            function Get-AppxPackage { @(
              [pscustomobject]@{Name='Microsoft.BingNews';PackageFullName='Microsoft.BingNews_4.55_x64__8wekyb3d8bbwe';NonRemovable=$false},
              [pscustomobject]@{Name='Microsoft.WindowsStore';PackageFullName='Microsoft.WindowsStore_1_x64__8wekyb3d8bbwe';NonRemovable=$false},
              [pscustomobject]@{Name='Microsoft.MinecraftUWP';PackageFullName='Microsoft.MinecraftUWP_1_x64__8wekyb3d8bbwe';NonRemovable=$false}) }
            function Get-Service { param($Name,$ErrorAction) if($Name -eq 'SysMain'){ [pscustomobject]@{Name='SysMain';StartType='Automatic';Status='Running'} } elseif($Name -eq 'XblGameSave'){ [pscustomobject]@{Name='XblGameSave';StartType='Manual';Status='Stopped'} } }
            function Get-ItemProperty { param($LiteralPath,$Name,$ErrorAction) if($LiteralPath -like '*SysMain'){ [pscustomobject]@{DelayedAutostart=1} } else { throw 'sem valor' } }
            function Get-ScheduledTask { param($TaskPath,$TaskName,$ErrorAction) if($TaskName -eq 'Consolidator'){ [pscustomobject]@{State='Ready'} } }
            function Get-Partition { param($DriveLetter,$ErrorAction) if($DriveLetter -ne 'C'){ throw 'letra errada' }; [pscustomobject]@{Number=0} }
            function Get-Disk { [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject) process { [pscustomobject]@{Number=1} } }
            function Get-PhysicalDisk { param($ErrorAction) @([pscustomobject]@{DeviceId='0';MediaType='HDD'},[pscustomobject]@{DeviceId='1';MediaType='SSD'}) }
            function Get-CimInstance { param($ClassName,$Filter,$ErrorAction) [pscustomobject]@{InstallState=1} }
            $env:SystemDrive='C:'
            """;
        var inventoryJson = await Run(shell, inventoryFakes + "\n" + DebloatScripts.Inventory(DebloatCatalog.ForBuild(22631)));
        DebloatInventory? inventory = null;
        try { inventory = DebloatScripts.ParseInventory(inventoryJson); } catch (JsonException) { }
        check(inventory != null && inventory.Apps.Select(a => a.Name).SequenceEqual(["Microsoft.BingNews", "Microsoft.MinecraftUWP"])
            && inventory.Services.Any(s => s.Name == "SysMain" && s.StartType == "AutomaticDelayed" && s.Running)
            && inventory.Services.Any(s => s.Name == "XblGameSave" && s.StartType == "Manual" && !s.Running)
            && inventory.Tasks.Single().Name == "Consolidator" && inventory.SystemDisk == "SSD" && inventory.Recall == "Enabled",
            "Debloat: o script de inventário real lê pacotes do catálogo, início atrasado, tarefas, o disco do sistema e o Recall: " + Short(inventoryJson));

        const string applyFakes = """
            $script:calls=@()
            $script:seq=10
            function Get-ComputerRestorePoint { param($ErrorAction) [pscustomobject]@{SequenceNumber=$script:seq} }
            function Checkpoint-Computer { param($Description,$RestorePointType,$ErrorAction) $script:calls+=('Checkpoint:'+$Description); $script:seq=11 }
            function Get-Service { param($Name,$ErrorAction) if($Name -eq 'XboxNetApiSvc'){ throw 'falha inesperada' }; if($Name -eq 'SysMain'){ [pscustomobject]@{Name='SysMain';StartType='Automatic';Status='Running'} } }
            function Get-ItemProperty { param($LiteralPath,$Name,$ErrorAction) throw 'sem valor' }
            function Set-Service { param($Name,$StartupType,$ErrorAction) $script:calls+=('Set:'+$Name+':'+$StartupType) }
            function Stop-Service { param($Name,[switch]$Force,$ErrorAction) $script:calls+=('Stop:'+$Name) }
            function Get-ScheduledTask { param($TaskPath,$TaskName,$ErrorAction) if($TaskName -eq 'Consolidator'){ [pscustomobject]@{State='Ready';TaskName=$TaskName} } }
            function Disable-ScheduledTask { [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject) process { $script:calls+=('DisableTask:'+$InputObject.TaskName); $InputObject } }
            function Get-WindowsOptionalFeature { param([switch]$Online,$FeatureName,$ErrorAction) [pscustomobject]@{State='Enabled'} }
            function Disable-WindowsOptionalFeature { param([switch]$Online,$FeatureName,[switch]$NoRestart,$ErrorAction) $script:calls+=('DisableFeature:'+$FeatureName) }
            """;
        var items = new[] { "sysmain", "xbox-services", "ceip-tasks", "recall" }.Select(id => DebloatCatalog.Find(id)!).ToArray();
        var wrapped = applyFakes + "\ntry { $data=& { " + DebloatScripts.Apply(items) + " }; $result=@{success=$true;data=$data} } catch { $result=@{success=$false;error=$_.Exception.Message} }\n"
            + "ConvertTo-Json -InputObject ([pscustomobject]@{result=$result;calls=$script:calls}) -Depth 8 -Compress";
        var applyJson = await Run(shell, wrapped);
        string[] calls = []; string resultJson = "";
        try
        {
            using var doc = JsonDocument.Parse(applyJson);
            calls = doc.RootElement.GetProperty("calls").EnumerateArray().Select(c => c.GetString()!).ToArray();
            resultJson = doc.RootElement.GetProperty("result").GetRawText();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        var parsed = resultJson.Length == 0 ? default : DebloatScripts.ParseElevated(resultJson, true);
        check(calls.Length > 0 && calls[0] == "Checkpoint:" + DebloatService.RestorePointDescription
            && calls.Contains("Set:SysMain:Disabled") && calls.Contains("Stop:SysMain") && calls.Contains("DisableTask:Consolidator") && calls.Contains("DisableFeature:Recall")
            && !calls.Any(c => c.Contains("XblGameSave")),
            "Debloat: o script elevado real cria o ponto antes e só desativa o que existe: " + string.Join(",", calls));
        check(parsed.Point is { Ready: true, SequenceNumber: 11 } && parsed.Results != null
            && parsed.Results.Any(r => r.Target == "SysMain" && r.Status == "Ok" && r.Previous == "Automatic|True")
            && parsed.Results.Any(r => r.Target == "XblGameSave" && r.Status == "NotFound")
            && parsed.Results.Any(r => r.Target == "XboxNetApiSvc" && r.Status == "Failed" && r.Detail.Contains("falha inesperada"))
            && parsed.Results.Count(r => r.Id == "ceip-tasks" && r.Status == "NotFound") == 5 && parsed.Results.Any(r => r.Id == "ceip-tasks" && r.Status == "Ok" && r.Previous == "Ready")
            && parsed.Results.Any(r => r.Target == "Recall" && r.Previous == "Enabled"),
            "Debloat: o resultado elevado real traz o ponto confirmado, o estado anterior de cada alvo e isola o erro de um alvo: " + Short(resultJson));

        var noPoint = applyFakes.Replace("$script:seq=11", "") + "\ntry { $data=& { " + DebloatScripts.Apply([DebloatCatalog.Find("sysmain")!]) + " }; $result=@{success=$true;data=$data} } catch { $result=@{success=$false;error=$_.Exception.Message} }\n"
            + "ConvertTo-Json -InputObject ([pscustomobject]@{result=$result;calls=$script:calls}) -Depth 8 -Compress";
        var noPointJson = await Run(shell, noPoint);
        check(noPointJson.Contains("\"success\":false") && !noPointJson.Contains("Set:SysMain"), "Debloat: sem ponto novo confirmado, o script elevado real para antes dos serviços: " + Short(noPointJson));

        const string removeFakes = """
            $script:installed=@('Microsoft.BingNews_1_x64__8wekyb3d8bbwe','Microsoft.BingWeather_1_x64__8wekyb3d8bbwe')
            function Remove-AppxPackage { param($Package,[switch]$AllUsers,$ErrorAction) if($AllUsers){ throw 'AllUsers usado' }; if($Package -like 'Microsoft.BingNews*'){ $script:installed=@($script:installed | Where-Object { $_ -ne $Package }) } }
            function Get-AppxPackage { param($ErrorAction) $script:installed | ForEach-Object { [pscustomobject]@{PackageFullName=$_} } }
            """;
        var removal = await Run(shell, removeFakes + "\n" + DebloatScripts.RemoveApps([("Microsoft.BingNews", "Microsoft.BingNews_1_x64__8wekyb3d8bbwe"), ("Microsoft.BingWeather", "Microsoft.BingWeather_1_x64__8wekyb3d8bbwe")]));
        Dictionary<string, (string Status, string Detail)> removed = [];
        try { removed = DebloatScripts.ParseRemoval(removal); } catch (JsonException) { }
        check(removed.TryGetValue("Microsoft.BingNews_1_x64__8wekyb3d8bbwe", out var news) && news.Status == "Ok"
            && removed.TryGetValue("Microsoft.BingWeather_1_x64__8wekyb3d8bbwe", out var weather) && weather.Status == "Failed" && weather.Detail.Contains("continua instalado"),
            "Debloat: o script real de remoção só diz Ok quando o pacote saiu: " + Short(removal));

        var undoFakes = """
            $script:calls=@()
            function Set-Service { param($Name,$StartupType,$ErrorAction) $script:calls+=('Set:'+$Name+':'+$StartupType) }
            function Start-Service { param($Name,$ErrorAction) $script:calls+=('Start:'+$Name) }
            function sc.exe { $script:calls+=('sc:'+($args -join ' ')); $global:LASTEXITCODE=0 }
            function Get-ScheduledTask { param($TaskPath,$TaskName,$ErrorAction) [pscustomobject]@{TaskName=$TaskName} }
            function Enable-ScheduledTask { [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject) process { $script:calls+=('EnableTask:'+$InputObject.TaskName); $InputObject } }
            function Enable-WindowsOptionalFeature { param([switch]$Online,$FeatureName,[switch]$NoRestart,$ErrorAction) $script:calls+=('EnableFeature:'+$FeatureName) }
            """;
        var undoScript = DebloatScripts.Undo(
        [
            new("sysmain", DebloatKind.Service, "SysMain", "AutomaticDelayed|True"),
            new("xbox-services", DebloatKind.Service, "XblGameSave", "Manual|False"),
            new("ceip-tasks", DebloatKind.ScheduledTask, @"\Microsoft\Windows\Customer Experience Improvement Program\|Consolidator", "Ready"),
            new("recall", DebloatKind.OptionalFeature, "Recall", "Enabled"),
        ]);
        var undoJson = await Run(shell, undoFakes + "\n$data=& { " + undoScript + " }\nConvertTo-Json -InputObject ([pscustomobject]@{data=$data;calls=$script:calls}) -Depth 8 -Compress");
        check(undoJson.Contains("Set:SysMain:Automatic") && undoJson.Contains("sc:config SysMain start= delayed-auto") && undoJson.Contains("Start:SysMain")
            && undoJson.Contains("Set:XblGameSave:Manual") && !undoJson.Contains("Start:XblGameSave") && undoJson.Contains("EnableTask:Consolidator") && undoJson.Contains("EnableFeature:Recall")
            && !undoJson.Contains("\"Status\":\"Failed\""),
            "Debloat: o script real do Desfazer restaura início, execução, tarefa e recurso: " + Short(undoJson));
    }

    private static string Short(string text) => text.Length <= 400 ? text : text[..400] + "…";

    private static async Task<string> Run(string shell, string script)
    {
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8\n" + script)) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = (await output).Trim();
        return text.Length > 0 ? text : "ERRO: " + (await error).Trim();
    }
}
