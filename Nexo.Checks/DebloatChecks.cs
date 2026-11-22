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
    private const string RunA = "0123456789abcdef0123456789abcdef";
    private const string RunB = "fedcba9876543210fedcba9876543210";

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
        await Review(check);
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
        check(!DebloatCatalog.Find("visual-effects")!.Default && DebloatCatalog.Find("file-extensions")!.Default,
            "Debloat: os efeitos visuais vêm desmarcados, porque mudam a aparência de quem aplica tudo");
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
        var apply = DebloatScripts.Apply(elevatedItems, RunA);
        int At(string text) => apply.IndexOf(text, StringComparison.Ordinal);
        check(At("Checkpoint-Computer") >= 0 && At("Checkpoint-Computer") < At("Set-Service -Name")
            && apply.Contains(DebloatService.RestorePointDescription) && !apply.Contains("Nexo before driver installation"),
            "Debloat: o ponto de restauração vem antes de qualquer serviço, com descrição própria");
        check(apply.Contains("-RestorePointType MODIFY_SETTINGS") && !apply.Contains("APPLICATION_INSTALL") && RestorePointProtection.Script.Contains("-RestorePointType APPLICATION_INSTALL"),
            "Debloat: o ponto de restauração do debloat é de mudança de configuração; o do driver continua de instalação");
        var openAt = apply.IndexOf("\nOpen-DebloatStore", StringComparison.Ordinal); var saveAt = At("try { Save-DebloatUndo $before }");
        check(openAt > 0 && saveAt > openAt && saveAt < At("$rp=& {") && At("$rp=& {") < At("Set-Service -Name 'SysMain' -StartupType Disabled")
            && apply.Contains("$nxRunId='" + RunA + "'") && apply.Contains("NexoSupport\\debloat"),
            "Debloat: o Aplicar abre a pasta protegida e grava nela o estado anterior antes do ponto de restauração e de qualquer mudança");
        check(!DebloatScripts.Apply([], RunA).Contains("Open-DebloatStore") && DebloatScripts.Apply([], RunA).Contains("Checkpoint-Computer"),
            "Debloat: só com itens do perfil, o Aplicar não abre a pasta protegida");
        bool RejectsRun(Func<string> make) { try { make(); return false; } catch (ArgumentException) { return true; } }
        check(RejectsRun(() => DebloatScripts.Apply(elevatedItems, "..\\x")) && RejectsRun(() => DebloatScripts.Undo("ABCDEF")) && RejectsRun(() => DebloatScripts.Undo(RunA.ToUpperInvariant()))
            && RejectsRun(() => DebloatScripts.Undo(RunA + "'; Remove-Item C:\\ ;'")),
            "Debloat: identificador de aplicação fora do formato é recusado antes de gerar o script elevado");
        var undoA = DebloatScripts.Undo(RunA); var undoB = DebloatScripts.Undo(RunB);
        check(undoA.Replace(RunA, RunB) == undoB && undoA.Contains("Read-DebloatUndo") && !undoA.Contains("Checkpoint-Computer")
            && undoA.Contains("'sysmain|Service|SysMain'") && undoA.Contains("'recall|OptionalFeature|Recall'") && !undoA.Contains("WinDefend"),
            "Debloat: o script do Desfazer só carrega o identificador e a lista do catálogo; o estado anterior vem da pasta protegida");
        var length = WindowsTools.CommandLength(["-NoProfile", "-NonInteractive", "-Command", OptimizationDiskExecutor.BuildScript(apply,
            @"C:\Program Files\NexoOptimize-" + RunA, @"C:\Program Files\NexoOptimize-" + RunA + @"\result.json", "S-1-5-21-1000000000-2000000000-3000000000-1001")]);
        check(length < WindowsTools.CommandLineLimit - 300, "Debloat: o Aplicar com todos os itens elevados cabe na linha de comando do Windows (" + length + " caracteres)");
        // Como vai de fato ao executor: compactado no carregador da rede, com margem larga para o catálogo crescer.
        string Sent(string script) => OptimizationDiskExecutor.BuildScript(DebloatScripts.ForElevation(script),
            @"C:\Program Files\NexoOptimize-" + RunA, @"C:\Program Files\NexoOptimize-" + RunA + @"\result.json", "S-1-5-21-1000000000-2000000000-3000000000-1001");
        var sentApply = WindowsTools.CommandLength(["-NoProfile", "-NonInteractive", "-Command", Sent(apply)]);
        var sentUndo = WindowsTools.CommandLength(["-NoProfile", "-NonInteractive", "-Command", Sent(DebloatScripts.Undo(RunA))]);
        check(sentApply < WindowsTools.CommandLineLimit / 2 && sentUndo < WindowsTools.CommandLineLimit / 2 && !Sent(apply).Contains('"')
            && DebloatScripts.ForElevation(apply).EndsWith(NetworkRecovery.LoaderRun, StringComparison.Ordinal),
            "Debloat: o script elevado vai compactado no carregador da rede, com menos da metade do limite da linha de comando (Aplicar " + sentApply + ", Desfazer " + sentUndo + " caracteres)");
        var store = ProtectedFolderScript.Text;
        check(ProtectedFolderScript.Functions.All(f => store.Contains("function " + f)) && store.StartsWith("$trustedSids = @('S-1-5-18', 'S-1-5-32-544'", StringComparison.Ordinal)
            && !store.Contains('"') && store.Contains("SetAccessRuleProtection($true, $false)") && store.Contains("[IO.FileMode]::CreateNew")
            && DebloatScripts.StoreFunctions.StartsWith(store, StringComparison.Ordinal),
            "Debloat: a pasta protegida usa as funções da cópia protegida da rede, extraídas do próprio NetworkTransaction.ps1");
        var network = ProtectedFolderScript.Source();
        bool ExtractFails(string source) { try { ProtectedFolderScript.Extract(source); return false; } catch (InvalidOperationException) { return true; } }
        check(ExtractFails(network.Replace("function Assert-Trusted(", "function Assert-Confiavel(")) && ExtractFails(network.Replace("$trustedSids = @(", "$sids = @(")),
            "Debloat: sem uma função ou a lista de SIDs no script de rede, o Desfazer não é gerado sem proteção");
        check(apply.Contains("Set-Service -Name 'SysMain' -StartupType Disabled") && apply.Contains("Disable-ScheduledTask") && apply.Contains("Disable-WindowsOptionalFeature -Online -FeatureName 'Recall' -NoRestart")
            && !apply.Contains("Remove-Appx") && !apply.Contains("HKCU", StringComparison.OrdinalIgnoreCase),
            "Debloat: o script elevado só mexe em serviços, tarefas e recursos; apps e HKCU ficam fora dele");
        check(!apply.Contains('"') && !DebloatScripts.Undo(RunA).Contains('"') && !DebloatScripts.Apply([], RunA).Contains('"'),
            "Debloat: os scripts elevados não usam aspas duplas, porque vão em -Command");
        bool ApplyRejects(DebloatItem item) { try { DebloatScripts.Apply([item], RunA); return false; } catch (ArgumentException) { return true; } }
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
        var stored = system.Protected[run.Id];
        check(result.RunId == run.Id && run.RestorePoint == 41 && stored.Any(e => e.Target == "XblGameSave" && e.Previous == "Manual|False")
            && stored.Any(e => e.Target == "XboxNetApiSvc" && e.Previous == "Manual|True") && stored.Any(e => e.Kind == DebloatKind.ScheduledTask && e.Previous == "Ready")
            && run.ProtectedItems.OrderBy(i => i).SequenceEqual(["ceip-tasks", "xbox-services"])
            && run.Entries.Any(e => e.Kind == DebloatKind.UserRegistry && e.Target.EndsWith("|HideFileExt") && e.Previous == "DWord:1")
            && run.Entries.Any(e => e.Kind == DebloatKind.App && e.Target == "Microsoft.BingNews"),
            "Debloat: o estado anterior de cada alvo fica guardado para o Desfazer (" + culture + ")");
        check(run.Entries.All(e => e.Kind is DebloatKind.UserRegistry or DebloatKind.App) && !File.ReadAllText(undo.Path).Contains("XblGameSave"),
            "Debloat: o arquivo local do Desfazer não guarda o estado de serviços, tarefas nem recursos; ele fica na pasta protegida (" + culture + ")");

        // Pasta protegida recusada pelo executor: nada elevado muda, a mensagem diz por quê, e os itens do perfil seguem.
        var hostile = new FakeSystem(Inventory()) { ElevatedOutput = _ => JsonSerializer.Serialize(new { success = false, error = DebloatScripts.StoreRefusal + "\"C:\\ProgramData\\NexoSupport\": dono não confiável (S-1-5-21-1-1001). Nenhum serviço, tarefa ou recurso foi alterado." }) };
        var hostileService = new DebloatService(hostile, new DebloatUndoStore(Path.Combine(dir.Path, "hostile.json")), _ => { });
        var hostileResult = await hostileService.ApplyAsync(await hostileService.PreviewAsync(CancellationToken.None), ["sysmain", "advertising-id"], false, null, CancellationToken.None);
        var h = hostileResult.Items.ToDictionary(i => i.Id);
        check(h["sysmain"].Status == DebloatStatus.NotApplied && h["sysmain"].Detail.StartsWith(DebloatScripts.StoreRefusal) && h["sysmain"].Detail.Contains("dono não confiável")
            && !h["sysmain"].Detail.Contains("Ponto de restauração") && h["advertising-id"].Status == DebloatStatus.Ok && hostileResult.Status == "Partial",
            "Debloat: pasta protegida hostil recusa os itens elevados com a explicação e mantém os do perfil (" + culture + ")");

        // Arquivo local do Desfazer que não pode ser gravado: o resultado volta inteiro, com o aviso e o ponto de restauração.
        var blocker = Path.Combine(dir.Path, "bloqueio"); File.WriteAllText(blocker, "arquivo no lugar da pasta");
        var unsaved = new FakeSystem(Inventory());
        var unsavedService = new DebloatService(unsaved, new DebloatUndoStore(Path.Combine(blocker, "undo.json")), _ => { });
        unsaved.Registry.Set(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", DebloatValueKind.DWord, "1");
        DebloatResult? unsavedResult = null;
        try { unsavedResult = await unsavedService.ApplyAsync(await unsavedService.PreviewAsync(CancellationToken.None), ["sysmain", "file-extensions", "bing-news"], false, null, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var u = unsavedResult?.Items.ToDictionary(i => i.Id);
        check(unsavedResult != null && u!["sysmain"].Status == DebloatStatus.Ok && u["bing-news"].Status == DebloatStatus.Ok && unsavedResult.Status == "Partial"
            && unsavedResult.Warning is { } w && w.Contains("não foi salvo") && w.Contains("ponto de restauração 41") && w.Contains("pasta protegida"),
            "Debloat: sem gravar o arquivo do Desfazer, a aplicação não lança, devolve o resultado e indica o ponto de restauração (" + culture + ")");
        // Só itens elevados, todos ok: a falha do registro local sozinha já deixa o resultado parcial, com o aviso.
        var onlyElevated = new FakeSystem(Inventory());
        var onlyElevatedService = new DebloatService(onlyElevated, new DebloatUndoStore(Path.Combine(blocker, "so-elevados.json")), _ => { });
        var onlyElevatedResult = await onlyElevatedService.ApplyAsync(await onlyElevatedService.PreviewAsync(CancellationToken.None), ["sysmain"], false, null, CancellationToken.None);
        check(onlyElevatedResult.Items.Single().Status == DebloatStatus.Ok && onlyElevatedResult.Status == "Partial" && onlyElevatedResult.Warning is { } ow && ow.Contains("não foi salvo"),
            "Debloat: com tudo ok, a falha ao gravar o arquivo do Desfazer sozinha deixa o resultado parcial, com o aviso (" + culture + ")");
        check(u != null && u["file-extensions"].Status == DebloatStatus.Failed && u["file-extensions"].Detail.Contains("não pôde ser guardado")
            && unsaved.Registry.Value(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt") == "1",
            "Debloat: valor do perfil sem o estado anterior guardado não é gravado (" + culture + ")");
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
        var failedOnly = new FakeSystem(Inventory()) { FailService = "SysMain" };
        var failedStore = new DebloatUndoStore(Path.Combine(dir.Path, "failed-only.json"));
        var failedService = new DebloatService(failedOnly, failedStore, _ => { });
        var failedResult = await failedService.ApplyAsync(await failedService.PreviewAsync(CancellationToken.None), ["sysmain"], false, null, CancellationToken.None);
        check(failedResult.Items.Single().Status == DebloatStatus.Failed && failedResult.RunId == null && failedStore.Load().Count == 0,
            "Debloat: item elevado que não mudou não entra no índice do Desfazer (" + culture + ")");
        check(DebloatService.Aggregate(["NotFound", "NotFound"]) == DebloatStatus.NotFound && DebloatService.Aggregate(["Ok", "NotFound"]) == DebloatStatus.Ok
            && DebloatService.Aggregate(["Failed"]) == DebloatStatus.Failed && DebloatService.Aggregate(["Weird"]) == DebloatStatus.Failed && DebloatService.Aggregate([]) == DebloatStatus.NotFound,
            "Debloat: status desconhecido do Windows conta como falha; alvo ausente não é sucesso (" + culture + ")");

        // Seleção inválida e cancelamento.
        var odd = await service.ApplyAsync(preview, ["clipchamp", "inexistente", "feedback-hub"], false, null, CancellationToken.None);
        check(odd.Items.All(i => i.Status == DebloatStatus.NotApplied) && odd.Status == "Partial", "Debloat: item ausente do PC ou desconhecido não é aplicado (" + culture + ")");
        var already = new FakeSystem(Inventory(sysmainStart: "Disabled"));
        var alreadyService = new DebloatService(already, new DebloatUndoStore(Path.Combine(dir.Path, "already.json")), _ => { });
        var alreadyResult = await alreadyService.ApplyAsync(await alreadyService.PreviewAsync(CancellationToken.None), ["sysmain"], false, null, CancellationToken.None);
        check(alreadyResult.Items.Single().Status == DebloatStatus.NotApplied && already.Elevated.Count == 0 && alreadyResult.RestorePoint == null, "Debloat: item já aplicado não roda de novo nem pede UAC só para o ponto de restauração (" + culture + ")");
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
        check(script == DebloatScripts.Undo(run.Id) && !script.Contains("Checkpoint-Computer")
            && system.Restored.Any(e => e.Target == "SysMain" && e.Previous == "Automatic|True") && system.Restored.Any(e => e.Target.EndsWith("|Consolidator") && e.Previous == "Ready")
            && system.Restored.Any(e => e.Target == "Recall" && e.Previous == "Enabled") && system.Restored.Where(e => e.Target.EndsWith("|Proxy")).All(e => e.Previous == "Disabled"),
            "Debloat: o Desfazer volta serviço, tarefa e recurso ao estado da pasta protegida, sem criar outro ponto (" + culture + ")");
        check(system.Registry.Value(Desktop, "UserPreferencesMask") == "9E-3E-07-80-12-00-00-00" && system.Registry.Value(Desktop, "DragFullWindows") == null,
            "Debloat: o Desfazer devolve o valor anterior e apaga o que não existia (" + culture + ")");
        check(byId["bing-news"].Status == DebloatStatus.NotApplied && byId["bing-news"].Detail.Contains("Microsoft Store") && byId["bing-news"].Detail.Contains("Microsoft.BingNews")
            && byId["visual-effects"].Status == DebloatStatus.Ok && byId["sysmain"].Status == DebloatStatus.Ok && result.Status == "Completed"
            && store.Latest()!.UndoneAt != null && !store.Latest()!.CanUndo,
            "Debloat: app removido é indicado para reinstalar pela Store e a aplicação fica marcada como desfeita (" + culture + ")");

        var delayed = DebloatScripts.Undo(RunA);
        check(delayed.Contains("& sc.exe config $t 'start=' 'delayed-auto'") && delayed.Contains("if($parts[1] -ceq 'True'){ Start-Service -Name $t"), "Debloat: início atrasado volta como atrasado (" + culture + ")");

        // Arquivo local adulterado: as entradas de serviço dele não chegam ao executor; o que vale é a pasta protegida.
        var tampered = new FakeSystem(Inventory());
        var tamperedRun = new DebloatRun
        {
            At = DateTimeOffset.UtcNow, RestorePoint = 41, ProtectedItems = ["sysmain"],
            Entries = [new("sysmain", DebloatKind.Service, "SysMain", "Disabled|False"), new("recall", DebloatKind.OptionalFeature, "Recall", "Enabled"), new("sysmain", DebloatKind.Service, "WinDefend", "Disabled|False")],
        };
        tampered.Protected[tamperedRun.Id] = [new("sysmain", DebloatKind.Service, "SysMain", "Manual|False")];
        // O plano é mostrado antes do Desfazer (depois dele, o marcador diz que a aplicação já foi desfeita).
        var tamperedPlan = DebloatService.DescribeUndo(tamperedRun, tampered.ReadProtectedUndo(tamperedRun.Id));
        var tamperedResult = await new DebloatService(tampered, new DebloatUndoStore(Path.Combine(dir.Path, "tampered.json")), _ => { }).UndoAsync(tamperedRun, null, CancellationToken.None);
        check(tampered.Elevated.Count == 1 && tampered.Elevated[0] == DebloatScripts.Undo(tamperedRun.Id) && !tampered.Elevated[0].Contains("WinDefend")
            && tampered.Restored.Count == 1 && tampered.Restored[0] == new DebloatUndoEntry("sysmain", DebloatKind.Service, "SysMain", "Manual|False")
            && tamperedResult.Items.Count(i => i.Status == DebloatStatus.Failed && i.Detail.Contains("ignorado")) == 3
            && tamperedResult.Items.Any(i => i.Id == "sysmain" && i.Status == DebloatStatus.Ok),
            "Debloat: entradas de serviço ou recurso plantadas no arquivo local não mudam o que o Desfazer elevado restaura (" + culture + ")");
        check(tamperedPlan.Contains("SysMain: volta ao início manual.") && !tamperedPlan.Any(l => l.Contains("volta a ser ativado")) && tamperedPlan.Count(l => l.Contains("ignorado")) == 3,
            "Debloat: o plano do Desfazer mostra o estado da pasta protegida e marca as entradas locais ignoradas (" + culture + ")");

        // Pasta protegida ausente ou não confiável: o Desfazer elevado recusa sem pedir UAC e diz o motivo.
        foreach (var problem in new[] { @"C:\ProgramData\NexoSupport\debloat\x\undo.json: não existe", @"C:\ProgramData\NexoSupport\debloat: ACE de escrita para Usuários (Write)" })
        {
            var missing = new FakeSystem(Inventory()) { ProtectedProblem = problem };
            var missingRun = new DebloatRun { At = DateTimeOffset.UtcNow, RestorePoint = 41, ProtectedItems = ["sysmain", "recall"], Entries = [new("file-extensions", DebloatKind.UserRegistry, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|HideFileExt", "DWord:1")] };
            var missingResult = await new DebloatService(missing, new DebloatUndoStore(Path.Combine(dir.Path, "missing.json")), _ => { }).UndoAsync(missingRun, null, CancellationToken.None);
            var mm = missingResult.Items.ToDictionary(i => i.Id);
            check(missing.Elevated.Count == 0 && mm["sysmain"].Status == DebloatStatus.NotApplied && mm["recall"].Status == DebloatStatus.NotApplied
                && mm["sysmain"].Detail.Contains("não é confiável") && mm["sysmain"].Detail.Contains(problem) && mm["sysmain"].Detail.Contains("ponto de restauração 41")
                && mm["file-extensions"].Status == DebloatStatus.Ok && missingResult.Status == "Partial" && missingRun.UndoneAt == null,
                "Debloat: registro protegido ausente ou não confiável recusa o Desfazer elevado com o motivo, sem UAC (" + culture + ")");
            check(DebloatService.DescribeUndo(missingRun, missing.ReadProtectedUndo(missingRun.Id)).Any(l => l.Contains("não serão restaurados") && l.Contains(problem)),
                "Debloat: o plano do Desfazer avisa que serviços, tarefas e recursos não serão restaurados (" + culture + ")");
        }

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
        var deniedRun = new DebloatRun { At = DateTimeOffset.UtcNow, ProtectedItems = ["sysmain"], Entries = [new("file-extensions", DebloatKind.UserRegistry, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|HideFileExt", "DWord:1")] };
        deniedSystem.Protected[deniedRun.Id] = [new("sysmain", DebloatKind.Service, "SysMain", "Manual|False")];
        var deniedUndo = await new DebloatService(deniedSystem, new DebloatUndoStore(Path.Combine(dir.Path, "denied-undo.json")), _ => { }).UndoAsync(deniedRun, null, CancellationToken.None);
        check(deniedUndo.Status == "Partial" && deniedUndo.Items.Single(i => i.Id == "sysmain").Status == DebloatStatus.NotApplied
            && deniedUndo.Items.Single(i => i.Id == "file-extensions").Status == DebloatStatus.Ok && deniedRun.UndoneAt == null,
            "Debloat: Desfazer com UAC recusado restaura o registro e deixa o resto para outra tentativa (" + culture + ")");

        // Marcador do Desfazer na pasta protegida: apagar o UndoneAt local não reabre um Desfazer que o executor marcou.
        check(system.Done.TryGetValue(run.Id, out var marked) && marked.Contains("sysmain|Service|SysMain") && marked.Contains("recall|OptionalFeature|Recall"),
            "Debloat: o Desfazer elevado bem-sucedido deixa o marcador com os alvos restaurados (" + culture + ")");
        var reopened = store.Latest()!;
        reopened.UndoneAt = null; store.Save(reopened);
        var elevatedBefore = system.Elevated.Count;
        var candidate = service.UndoCandidate();
        check(candidate == null && store.Latest()!.UndoneAt != null,
            "Debloat: com o UndoneAt local apagado, a aplicação marcada pelo executor não é oferecida e a marca local é refeita (" + culture + ")");
        var replay = await service.UndoAsync(reopened, null, CancellationToken.None);
        check(system.Elevated.Count == elevatedBefore && replay.Status == "Completed"
            && replay.Items.Where(i => i.Id is "sysmain" or "recall").All(i => i.Status == DebloatStatus.NotApplied && i.Detail.Contains(DebloatProtectedUndo.AlreadyUndoneText) && !i.Detail.Contains("não é confiável"))
            && DebloatService.DescribeUndo(reopened, system.ReadProtectedUndo(reopened.Id)).Any(l => l.Contains("já foi desfeita") && l.Contains("nada será alterado")),
            "Debloat: Desfazer de aplicação já desfeita é recusado sem UAC, com a mensagem, também no plano (" + culture + ")");

        // Desfazer parcial: só o que foi restaurado fica marcado; um 2º Desfazer cuida só do que falhou.
        var partialSystem = new FakeSystem(Inventory()) { FailUndoTarget = "SysMain" };
        var partialRun = new DebloatRun { At = DateTimeOffset.UtcNow, RestorePoint = 41, ProtectedItems = ["sysmain", "recall"] };
        partialSystem.Protected[partialRun.Id] = [new("sysmain", DebloatKind.Service, "SysMain", "Manual|False"), new("recall", DebloatKind.OptionalFeature, "Recall", "Enabled")];
        var partialStore = new DebloatUndoStore(Path.Combine(dir.Path, "partial-undo.json"));
        partialStore.Save(partialRun);
        var partialService = new DebloatService(partialSystem, partialStore, _ => { });
        var first = await partialService.UndoAsync(partialRun, null, CancellationToken.None);
        check(first.Status == "Partial" && partialSystem.Done[partialRun.Id].SequenceEqual(["recall|OptionalFeature|Recall"]) && partialService.UndoCandidate()?.Id == partialRun.Id,
            "Debloat: Desfazer parcial marca só o restaurado e a aplicação continua oferecida (" + culture + ")");
        partialSystem.FailUndoTarget = null;
        partialSystem.Restored.Clear();
        var second = await partialService.UndoAsync(partialRun, null, CancellationToken.None);
        var sb = second.Items.ToDictionary(i => i.Id);
        check(partialSystem.Restored.Count == 1 && partialSystem.Restored[0].Target == "SysMain" && sb["sysmain"].Status == DebloatStatus.Ok
            && sb["recall"].Status == DebloatStatus.NotApplied && sb["recall"].Detail.Contains("Desfazer anterior") && second.Status == "Completed"
            && partialService.UndoCandidate() == null,
            "Debloat: o 2º Desfazer restaura só o que falhou, não reaplica o resto e conclui (" + culture + ")");

        // Marcador não gravado: o resultado diz que o Desfazer foi feito, mas pode ser oferecido de novo.
        var unmarked = new FakeSystem(Inventory()) { MarkFails = true };
        var unmarkedRun = new DebloatRun { At = DateTimeOffset.UtcNow, ProtectedItems = ["sysmain"] };
        unmarked.Protected[unmarkedRun.Id] = [new("sysmain", DebloatKind.Service, "SysMain", "Manual|False")];
        var unmarkedResult = await new DebloatService(unmarked, new DebloatUndoStore(Path.Combine(dir.Path, "unmarked.json")), _ => { }).UndoAsync(unmarkedRun, null, CancellationToken.None);
        check(unmarkedResult.Items.Single().Status == DebloatStatus.Ok && unmarkedResult.Warning is { } markWarning && markWarning.Contains("marcador") && markWarning.Contains("disco cheio") && markWarning.Contains("de novo"),
            "Debloat: marcador não gravado aparece no resultado, sem esconder que o Desfazer pode ser oferecido de novo (" + culture + ")");
        var silent = new FakeSystem(Inventory()) { ElevatedOutput = _ => "{\"success\":true,\"data\":{\"Items\":[{\"Id\":\"sysmain\",\"Target\":\"SysMain\",\"Status\":\"Ok\"}]}}" };
        var silentRun = new DebloatRun { At = DateTimeOffset.UtcNow, ProtectedItems = ["sysmain"] };
        silent.Protected[silentRun.Id] = [new("sysmain", DebloatKind.Service, "SysMain", "Manual|False")];
        var silentResult = await new DebloatService(silent, new DebloatUndoStore(Path.Combine(dir.Path, "silent.json")), _ => { }).UndoAsync(silentRun, null, CancellationToken.None);
        check(silentResult.Warning?.Contains("não confirmou o marcador") == true,
            "Debloat: saída elevada sem confirmar o marcador conta como marcador não gravado (" + culture + ")");
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
        var protectedOnly = new DebloatRun { At = DateTimeOffset.UtcNow.AddMinutes(-5), ProtectedItems = ["sysmain"] };
        store.Save(protectedOnly);
        check(new DebloatUndoStore(path).Load().Single(r => r.Id == protectedOnly.Id) is { CanUndo: true, ProtectedItems: ["sysmain"] },
            "Debloat: aplicação só com itens elevados fica no índice local e pode ser desfeita");
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
        // Leitura da pasta protegida: arquivo com BOM do Write-ProtectedFile, conferência de confiança, identificador e tipos.
        var root = Path.Combine(dir.Path, "debloat");
        var runDir = Path.Combine(root, RunA); Directory.CreateDirectory(runDir);
        var file = Path.Combine(runDir, DebloatProtectedStore.FileName);
        void Write(string json) => File.WriteAllText(file, json, new UTF8Encoding(true));
        Write("{\"RunId\":\"" + RunA + "\",\"Items\":[{\"Id\":\"sysmain\",\"Kind\":\"Service\",\"Target\":\"SysMain\",\"Previous\":\"Manual|True\"},{\"Id\":\"recall\",\"Kind\":\"OptionalFeature\",\"Target\":\"Recall\",\"Previous\":\"Enabled\"}]}");
        string? seen = null;
        var read = DebloatProtectedStore.Read(root, RunA, (d, f) => { seen = Path.Combine(d, f); return null; });
        check(read.Problem == null && read.Entries.Count == 2 && read.Entries[0] == new DebloatUndoEntry("sysmain", DebloatKind.Service, "SysMain", "Manual|True") && seen == file,
            "Debloat: o registro protegido é lido com BOM e a conferência recebe a pasta da aplicação e o undo.json");
        check(DebloatProtectedStore.Read(root, RunA, (_, _) => "debloat: ACE de escrita para Usuários").Problem == "debloat: ACE de escrita para Usuários"
            && DebloatProtectedStore.Read(root, RunB, (_, _) => null).Problem != null && DebloatProtectedStore.Read(root, "..", (_, _) => null).Problem != null
            && DebloatProtectedStore.Read(root, RunA.ToUpperInvariant(), (_, _) => null).Problem != null,
            "Debloat: pasta não confiável, aplicação sem registro ou identificador fora do formato são recusados");
        Write("{\"RunId\":\"" + RunB + "\",\"Items\":[]}");
        var other = DebloatProtectedStore.Read(root, RunA, (_, _) => null).Problem;
        Write("{\"RunId\":\"" + RunA + "\",\"Items\":[{\"Id\":\"file-extensions\",\"Kind\":\"UserRegistry\",\"Target\":\"x\",\"Previous\":\"absent\"}]}");
        var kind = DebloatProtectedStore.Read(root, RunA, (_, _) => null).Problem;
        Write("{ quebrado");
        var broken = DebloatProtectedStore.Read(root, RunA, (_, _) => null).Problem;
        Write("{\"RunId\":\"" + RunA + "\",\"Items\":{\"Id\":\"sysmain\",\"Kind\":\"Service\",\"Target\":\"SysMain\",\"Previous\":\"Manual|False\"}}");
        var single = DebloatProtectedStore.Read(root, RunA, (_, _) => null);
        Write("{\"RunId\":\"" + RunA + "\",\"Items\":[{\"Id\":\"bing-news\",\"Kind\":\"App\",\"Target\":\"Microsoft.BingNews\",\"Previous\":\"removed\"}]}");
        var app = DebloatProtectedStore.Read(root, RunA, (_, _) => null).Problem;
        check(other != null && other.Contains("outra aplicação") && kind != null && app != null && broken != null && single.Problem == null && single.Entries.Count == 1,
            "Debloat: registro protegido de outra aplicação, com item do perfil ou de app, ou ilegível é recusado; item único do ConvertTo-Json é aceito");
        // Marcador undone.json: alvos já restaurados saem; todos restaurados é "já foi desfeita"; marcador não confiável recusa.
        Write("{\"RunId\":\"" + RunA + "\",\"Items\":[{\"Id\":\"sysmain\",\"Kind\":\"Service\",\"Target\":\"SysMain\",\"Previous\":\"Manual|True\"},{\"Id\":\"recall\",\"Kind\":\"OptionalFeature\",\"Target\":\"Recall\",\"Previous\":\"Enabled\"}]}");
        var doneFile = Path.Combine(runDir, DebloatProtectedStore.DoneFileName);
        void Mark(string json) => File.WriteAllText(doneFile, json, new UTF8Encoding(true));
        Mark("{\"RunId\":\"" + RunA + "\",\"At\":\"2026-11-22T18:00:00Z\",\"Done\":[\"recall|OptionalFeature|Recall\"]}");
        var checkedFiles = new List<string>();
        var half = DebloatProtectedStore.Read(root, RunA, (_, f) => { checkedFiles.Add(f); return null; });
        check(half.Problem == null && !half.AlreadyUndone && half.Entries.Single().Target == "SysMain" && half.DoneTargets.Single() == "recall|OptionalFeature|Recall"
            && checkedFiles.SequenceEqual([DebloatProtectedStore.FileName, DebloatProtectedStore.DoneFileName]),
            "Debloat: o marcador tira do Desfazer os alvos já restaurados e passa pela mesma conferência de confiança");
        Mark("{\"RunId\":\"" + RunA + "\",\"Done\":[\"sysmain|Service|SysMain\",\"recall|OptionalFeature|Recall\"]}");
        var whole = DebloatProtectedStore.Read(root, RunA, (_, _) => null);
        Mark("{\"RunId\":\"" + RunA + "\",\"Done\":\"sysmain|Service|SysMain\"}");
        var singleDone = DebloatProtectedStore.Read(root, RunA, (_, _) => null);
        Mark("{\"RunId\":\"" + RunA + "\",\"Done\":[\"SYSMAIN|Service|SysMain\"]}");
        var caseDone = DebloatProtectedStore.Read(root, RunA, (_, _) => null);
        check(whole.AlreadyUndone && whole.Entries.Count == 0 && whole.Problem == DebloatProtectedUndo.AlreadyUndoneText
            && singleDone.Entries.Single().Target == "Recall" && caseDone.Entries.Count == 2,
            "Debloat: com todos os alvos no marcador, a aplicação já foi desfeita; item único e maiúsculas diferentes não marcam além do gravado");
        Mark("{\"RunId\":\"" + RunB + "\",\"Done\":[]}");
        var foreignMark = DebloatProtectedStore.Read(root, RunA, (_, _) => null).Problem;
        Mark("{ quebrado");
        var brokenMark = DebloatProtectedStore.Read(root, RunA, (_, _) => null).Problem;
        Mark("{\"RunId\":\"" + RunA + "\",\"Done\":[]}");
        var untrustedMark = DebloatProtectedStore.Read(root, RunA, (_, f) => f == DebloatProtectedStore.DoneFileName ? "undone.json: dono não confiável" : null);
        check(foreignMark?.Contains("outra aplicação") == true && brokenMark != null && untrustedMark.Problem == "undone.json: dono não confiável" && untrustedMark.Entries.Count == 0 && !untrustedMark.AlreadyUndone,
            "Debloat: marcador de outra aplicação, ilegível ou não confiável recusa o Desfazer elevado");
        File.Delete(doneFile);
        check(DebloatScripts.ParseRemoval("{\"Package\":\"A_1\",\"Status\":\"Ok\",\"Detail\":\"\"}")["A_1"].Status == "Ok"
            && DebloatScripts.ParseRemoval("[{\"Package\":\"A_1\",\"Status\":\"Estranho\"}]")["A_1"].Status == "Failed",
            "Debloat: a remoção só conta Ok com evidência explícita");
    }

    // Gate @qa: resultado elevado sem linha do item, Desfazer da aplicação anterior e o plano mostrado antes do UAC.
    private static async Task Review(Action<bool, string> check)
    {
        using var dir = new TempDir();
        const string Empty = "{\"success\":true,\"data\":{\"RestorePoint\":41,\"Items\":[]}}";
        var silent = new FakeSystem(Inventory()) { ElevatedOutput = _ => Empty };
        var silentStore = new DebloatUndoStore(Path.Combine(dir.Path, "silent.json"));
        var silentService = new DebloatService(silent, silentStore, _ => { });
        var silentResult = await silentService.ApplyAsync(await silentService.PreviewAsync(CancellationToken.None), ["sysmain"], false, null, CancellationToken.None);
        check(silentResult.Items.Single().Status == DebloatStatus.Failed && silentResult.Items.Single().Detail == DebloatService.NoEvidence
            && silentResult.Status == "Partial" && silentStore.Load().Count == 0,
            "Debloat: resultado elevado sem a linha do item vira falha com estado desconhecido, não \"não encontrado\"");
        var silentSystem = new FakeSystem(Inventory()) { ElevatedOutput = _ => "{\"success\":true,\"data\":{\"Items\":[]}}" };
        var silentRun = new DebloatRun { At = DateTimeOffset.UtcNow, ProtectedItems = ["sysmain"] };
        silentSystem.Protected[silentRun.Id] = [new("sysmain", DebloatKind.Service, "SysMain", "Manual|False")];
        var silentUndo = await new DebloatService(silentSystem, new DebloatUndoStore(Path.Combine(dir.Path, "silent-undo.json")), _ => { })
            .UndoAsync(silentRun, null, CancellationToken.None);
        check(silentUndo.Items.Single().Status == DebloatStatus.Failed && silentUndo.Status == "Partial",
            "Debloat: Desfazer sem a linha do item não conta como restaurado");

        var store = new DebloatUndoStore(Path.Combine(dir.Path, "undo.json"));
        var older = new DebloatRun { At = DateTimeOffset.UtcNow.AddHours(-2), Entries = [new("sysmain", DebloatKind.Service, "SysMain", "Manual|False")] };
        var newer = new DebloatRun { At = DateTimeOffset.UtcNow, Entries = [new("bing-news", DebloatKind.App, "Microsoft.BingNews", "installed")], UndoneAt = DateTimeOffset.UtcNow };
        store.Save(older); store.Save(newer);
        check(store.Latest()!.Id == newer.Id && store.LatestUndoable()!.Id == older.Id,
            "Debloat: depois de desfazer a última aplicação, a anterior continua podendo ser desfeita");
        older.UndoneAt = DateTimeOffset.UtcNow; store.Save(older);
        check(store.LatestUndoable() == null, "Debloat: sem aplicação pendente, não há o que desfazer");

        var plan = DebloatService.DescribeUndo(new DebloatRun
        {
            ProtectedItems = ["sysmain", "xbox-services", "ceip-tasks", "recall"],
            Entries =
            [
                new("file-extensions", DebloatKind.UserRegistry, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|HideFileExt", "DWord:1"),
                new("bing-news", DebloatKind.App, "Microsoft.BingNews", "installed"),
            ],
        }, new DebloatProtectedUndo(
            [
                new("sysmain", DebloatKind.Service, "SysMain", "AutomaticDelayed|True"),
                new("xbox-services", DebloatKind.Service, "XblGameSave", "Manual|False"),
                new("ceip-tasks", DebloatKind.ScheduledTask, @"\Microsoft\Windows\Customer Experience Improvement Program\|Consolidator", "Ready"),
                new("ceip-tasks", DebloatKind.ScheduledTask, @"\Microsoft\Windows\Autochk\|Proxy", "Disabled"),
                new("recall", DebloatKind.OptionalFeature, "Recall", "Enabled"),
                new("sysmain", DebloatKind.Service, "WinDefend", "Disabled|False"),
            ], null));
        check(plan.Contains("SysMain: volta ao início automático (atraso) e é iniciado.") && plan.Contains("XblGameSave: volta ao início manual.")
            && plan.Contains("Tarefa Consolidator: volta a ficar ativa.") && !plan.Any(l => l.Contains("Proxy"))
            && plan.Any(l => l.StartsWith("Recall") && l.Contains("volta a ser ativado")) && plan.Any(l => l.StartsWith("Mostrar extensões"))
            && plan.Any(l => l.StartsWith("Notícias") && l.Contains("Microsoft Store")) && plan.Any(l => l.Contains("WinDefend") && l.Contains("inválido")),
            "Debloat: a confirmação do Desfazer lista o que vai mudar, inclusive o Recall, e aponta a entrada inválida");

        var recall = DebloatCatalog.Find("recall")!; var ceip = DebloatCatalog.Find("ceip-tasks")!;
        var undoScript = DebloatScripts.Undo(RunA);
        check(undoScript.Contains("if($p -ceq 'Enabled'){ Enable-WindowsOptionalFeature -Online -FeatureName $t -NoRestart"),
            "Debloat: o Desfazer só reativa o Recall que estava ativo, e sem reiniciar o Windows sozinho");
        check(!DebloatScripts.ValidUndo(recall, new("recall", DebloatKind.OptionalFeature, "Microsoft-Hyper-V-All", "Enabled"))
            && !DebloatScripts.ValidUndo(recall, new("recall", DebloatKind.OptionalFeature, "Recall", "Installed"))
            && !DebloatScripts.ValidUndo(ceip, new("ceip-tasks", DebloatKind.ScheduledTask, @"\Microsoft\Windows\Customer Experience Improvement Program\|Consolidator", "Ready;Start-Process"))
            && DebloatScripts.ValidUndo(ceip, new("ceip-tasks", DebloatKind.ScheduledTask, @"\Microsoft\Windows\Customer Experience Improvement Program\|Consolidator", "Ready")),
            "Debloat: o Desfazer recusa recurso fora do catálogo e estado de tarefa ou recurso que o Nexo não grava");
        check(undoScript.Contains("Get-ScheduledTask -TaskPath $tp[0] -TaskName $tp[1]") && undoScript.Contains(@"'ceip-tasks|ScheduledTask|\Microsoft\Windows\Customer Experience Improvement Program\|Consolidator'"),
            "Debloat: o Desfazer reativa a tarefa pelo caminho completo, não por um nome que outra pasta também tenha");

        // Desfazer concluído sem conseguir marcar o arquivo local: não lança e avisa.
        var blocker = Path.Combine(dir.Path, "bloqueio"); File.WriteAllText(blocker, "arquivo no lugar da pasta");
        var doneRun = new DebloatRun { At = DateTimeOffset.UtcNow, Entries = [new("file-extensions", DebloatKind.UserRegistry, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|HideFileExt", "DWord:1")] };
        DebloatResult? done = null;
        try { done = await new DebloatService(new FakeSystem(Inventory()), new DebloatUndoStore(Path.Combine(blocker, "undo.json")), _ => { }).UndoAsync(doneRun, null, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        check(done is { Status: "Completed", Warning: { } dw } && dw.Contains("não foi atualizado") && done.Items.Single().Status == DebloatStatus.Ok,
            "Debloat: Desfazer que não consegue marcar o arquivo local devolve o resultado com o aviso, sem exceção");
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
        // A pasta protegida simulada: o executor elevado falso grava aqui no Aplicar e lê daqui no Desfazer.
        public Dictionary<string, List<DebloatUndoEntry>> Protected { get; } = [];
        public string? ProtectedProblem { get; init; }
        public List<DebloatUndoEntry> Restored { get; } = [];
        // O marcador undone.json simulado: os alvos que o executor elevado falso já restaurou.
        public Dictionary<string, List<string>> Done { get; } = [];
        public string? FailUndoTarget { get; set; }
        public bool MarkFails { get; set; }

        public DebloatProtectedUndo ReadProtectedUndo(string runId) => ProtectedProblem != null ? DebloatProtectedUndo.Refused(ProtectedProblem)
            : Protected.TryGetValue(runId, out var entries) ? DebloatProtectedUndo.Filter(entries.ToArray(), Done.TryGetValue(runId, out var done) ? done : [])
            : DebloatProtectedUndo.Refused(runId + @"\undo.json: não existe");

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
            var runId = System.Text.RegularExpressions.Regex.Match(script, @"\$nxRunId='([0-9a-f]{32})'").Groups[1].Value;
            if (undo)
            {
                if (!Protected.TryGetValue(runId, out var all)) return JsonSerializer.Serialize(new { success = false, error = DebloatScripts.StoreRefusal + runId + ": não existe. Nada foi restaurado." });
                var read = ReadProtectedUndo(runId);
                if (read.AlreadyUndone) return JsonSerializer.Serialize(new { success = false, error = "Esta aplicação já foi desfeita; nada foi restaurado." });
                var ok = read.Entries.Where(e => e.Target != FailUndoTarget).ToArray();
                Restored.AddRange(ok);
                if (!MarkFails && ok.Length > 0) Done[runId] = [.. read.DoneTargets, .. ok.Select(DebloatProtectedUndo.Key)];
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    data = new
                    {
                        Items = read.Entries.Select(e => new { Id = e.ItemId, e.Target, Status = e.Target == FailUndoTarget ? "Failed" : "Ok", Previous = "", Detail = e.Target == FailUndoTarget ? "Acesso negado" : "restaurado" }),
                        Marked = !MarkFails, MarkError = MarkFails ? "disco cheio" : "",
                    },
                });
            }
            var saved = new List<DebloatUndoEntry>();
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
                    items.Add(new { Id = item.Id, Target = target, Status = status, Previous = previous, Detail = status == "Failed" ? "Acesso negado" : "" });
                    if (status == "Ok" && previous.Length > 0) saved.Add(new(item.Id, item.Kind, target, previous));
                }
            if (runId.Length > 0) Protected[runId] = saved;
            return JsonSerializer.Serialize(new { success = true, data = new { RestorePoint = 41, Items = items } });
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
        await LongScript(shell, check);
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
            function Checkpoint-Computer { param($Description,$RestorePointType,$ErrorAction) $script:calls+=('Checkpoint:'+$Description+':'+$RestorePointType); $script:seq=11 }
            function Get-Service { param($Name,$ErrorAction) if($Name -eq 'XboxNetApiSvc'){ throw 'falha inesperada' }; if($Name -eq 'SysMain'){ [pscustomobject]@{Name='SysMain';StartType='Automatic';Status='Running'} } }
            function Get-ItemProperty { param($LiteralPath,$Name,$ErrorAction) throw 'sem valor' }
            function Set-Service { param($Name,$StartupType,$ErrorAction) $script:calls+=('Set:'+$Name+':'+$StartupType) }
            function Stop-Service { param($Name,[switch]$Force,$ErrorAction) $script:calls+=('Stop:'+$Name) }
            function Get-ScheduledTask { param($TaskPath,$TaskName,$ErrorAction) if($TaskName -eq 'Consolidator'){ [pscustomobject]@{State='Ready';TaskName=$TaskName} } }
            function Disable-ScheduledTask { [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject) process { $script:calls+=('DisableTask:'+$InputObject.TaskName); $InputObject } }
            function Get-WindowsOptionalFeature { param([switch]$Online,$FeatureName,$ErrorAction) [pscustomobject]@{State='Enabled'} }
            function Disable-WindowsOptionalFeature { param([switch]$Online,$FeatureName,[switch]$NoRestart,$ErrorAction) $script:calls+=('DisableFeature:'+$FeatureName) }
            """;
        // A pasta protegida real exige ACL do Windows: aqui o bloco dela vira funções falsas que registram a ordem e o conteúdo.
        const string storeFakes = """
            function Open-DebloatStore { $script:calls+='Open' }
            function Save-DebloatUndo($entries) { $script:calls+=('Save:'+((@($entries) | ForEach-Object { $_.Id+'/'+$_.Kind+'/'+$_.Target+'='+$_.Previous }) -join ',')) }

            """;
        const string RunId = "0123456789abcdef0123456789abcdef";
        string Elevated(string script, string store) => "try { $data=& { " + script.Replace(DebloatScripts.StoreFunctions, store) + " }; $result=@{success=$true;data=$data} } catch { $result=@{success=$false;error=$_.Exception.Message} }\n"
            + "ConvertTo-Json -InputObject ([pscustomobject]@{result=$result;calls=$script:calls}) -Depth 8 -Compress";
        var items = new[] { "sysmain", "xbox-services", "ceip-tasks", "recall" }.Select(id => DebloatCatalog.Find(id)!).ToArray();
        var applyScript = DebloatScripts.Apply(items, RunId);
        check(applyScript.Contains(DebloatScripts.StoreFunctions), "Debloat: o script elevado do Aplicar traz o bloco da pasta protegida");
        var wrapped = applyFakes + "\n" + Elevated(applyScript, storeFakes);
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
        check(calls.Length > 3 && calls[0] == "Open" && calls[1].StartsWith("Save:") && calls[2] == "Checkpoint:" + DebloatService.RestorePointDescription + ":MODIFY_SETTINGS"
            && calls.Contains("Set:SysMain:Disabled") && calls.Contains("Stop:SysMain") && calls.Contains("DisableTask:Consolidator") && calls.Contains("DisableFeature:Recall")
            && !calls.Any(c => c.Contains("XblGameSave")),
            "Debloat: o script elevado real abre a pasta protegida, grava o estado anterior, cria o ponto e só então desativa o que existe: " + string.Join(",", calls));
        var before = calls.Length > 1 ? calls[1] : ""; var after = calls.LastOrDefault() ?? "";
        check(before.Contains("sysmain/Service/SysMain=Automatic|True") && before.Contains(@"ceip-tasks/ScheduledTask/\Microsoft\Windows\Customer Experience Improvement Program\|Consolidator=Ready")
            && before.Contains("recall/OptionalFeature/Recall=Enabled") && !before.Contains("XboxNetApiSvc") && !before.Contains("XblGameSave")
            && after.StartsWith("Save:") && after.Contains("sysmain/Service/SysMain=Automatic|True") && after.Contains("recall/OptionalFeature/Recall=Enabled"),
            "Debloat: o registro protegido recebe o estado anterior de cada alvo lido antes das mudanças e é regravado no fim: " + Short(before + " || " + after));
        check(parsed.Point is { Ready: true, SequenceNumber: 11 } && parsed.Results != null
            && parsed.Results.Any(r => r.Target == "SysMain" && r.Status == "Ok" && r.Previous == "Automatic|True")
            && parsed.Results.Any(r => r.Target == "XblGameSave" && r.Status == "NotFound")
            && parsed.Results.Any(r => r.Target == "XboxNetApiSvc" && r.Status == "Failed" && r.Detail.Contains("falha inesperada"))
            && parsed.Results.Count(r => r.Id == "ceip-tasks" && r.Status == "NotFound") == 5 && parsed.Results.Any(r => r.Id == "ceip-tasks" && r.Status == "Ok" && r.Previous == "Ready")
            && parsed.Results.Any(r => r.Target == "Recall" && r.Previous == "Enabled"),
            "Debloat: o resultado elevado real traz o ponto confirmado, o estado anterior de cada alvo e isola o erro de um alvo: " + Short(resultJson));

        // O mesmo script, compactado como vai ao executor real: o carregador da rede o roda no mesmo escopo, com o mesmo efeito.
        var compactJson = await Run(shell, applyFakes + "\n" + "try { $data=& { " + DebloatScripts.ForElevation(applyScript.Replace(DebloatScripts.StoreFunctions, storeFakes))
            + " }; $result=@{success=$true;data=$data} } catch { $result=@{success=$false;error=$_.Exception.Message} }\n"
            + "ConvertTo-Json -InputObject ([pscustomobject]@{result=$result;calls=$script:calls}) -Depth 8 -Compress");
        string[] compactCalls = []; string compactResult = "";
        try
        {
            using var doc = JsonDocument.Parse(compactJson);
            compactCalls = doc.RootElement.GetProperty("calls").EnumerateArray().Select(c => c.GetString()!).ToArray();
            compactResult = doc.RootElement.GetProperty("result").GetProperty("data").GetRawText();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        string Data(string json) { try { using var d = JsonDocument.Parse(json); return d.RootElement.GetProperty("data").GetRawText(); } catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return "sem dados"; } }
        check(calls.Length > 3 && compactCalls.SequenceEqual(calls) && compactResult.Length > 0 && compactResult == Data(resultJson),
            "Debloat: compactado no carregador da rede, o script elevado real faz as mesmas chamadas e devolve o mesmo resultado: " + Short(compactJson));

        var stuckJson = await Run(shell, applyFakes.Replace("function Set-Service { param($Name,$StartupType,$ErrorAction) $script:calls+=('Set:'+$Name+':'+$StartupType) }",
            "function Set-Service { param($Name,$StartupType,$ErrorAction) throw 'acesso negado' }") + "\n" + Elevated(applyScript, storeFakes));
        string[] stuck = [];
        try { using var doc = JsonDocument.Parse(stuckJson); stuck = doc.RootElement.GetProperty("calls").EnumerateArray().Select(c => c.GetString()!).ToArray(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        check(stuck.Length > 3 && stuck[1].Contains("sysmain/Service/SysMain=") && stuck[^1].StartsWith("Save:") && !stuck[^1].Contains("SysMain") && stuck[^1].Contains("recall/OptionalFeature/Recall"),
            "Debloat: no fim, o registro protegido fica só com o que mudou; serviço que não mudou sai dele: " + string.Join(" | ", stuck.Where(c => c.StartsWith("Save:"))));

        // As funções próprias da pasta do Desfazer, com as da rede trocadas por falsas e a raiz numa pasta temporária.
        var own = DebloatScripts.StoreFunctions[ProtectedFolderScript.Text.Length..];
        var temp = Path.Combine(Path.GetTempPath(), "NexoDebloatOwn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var rooted = own.Replace("$nxRoot=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NexoSupport\\debloat'", "$nxRoot=Join-Path " + WindowsTools.Quote(temp) + " 'NexoSupport\\debloat'");
            const string networkFakes = """
                $script:calls=@()
                function Use-ProtectedDirectory($path) { $script:calls+=('Use:'+(Split-Path -Leaf $path)); if($script:hostile -and (Split-Path -Leaf $path) -eq 'debloat'){ throw ('debloat: ACE de escrita para S-1-5-32-545') } }
                function New-ProtectedDirectory($path) { $script:calls+=('New:'+(Split-Path -Leaf $path)); [void][IO.Directory]::CreateDirectory($path) }
                function Assert-Trusted($path) { $script:calls+=('Trust:'+(Split-Path -Leaf $path)); if($script:untrusted -and $path -like '*undo.json'){ throw ($path + ': dono não confiável') }; if($script:untrustedDone -and $path -like '*undone.json'){ throw ('undone.json: dono não confiável') } }
                function Write-ProtectedFile($path, $text) { $script:calls+=('Write:'+(Split-Path -Leaf $path)); [IO.File]::WriteAllText($path, $text) }
                """;
            string Own(string body, string setup = "") => "$nxRunId='" + RunId + "'\n" + networkFakes + "\n" + setup + "\n" + rooted + "\n"
                + "try { $value=& { " + body + " }; $result=@{success=$true;value=$value} } catch { $result=@{success=$false;error=$_.Exception.Message} }\n"
                + "ConvertTo-Json -InputObject ([pscustomobject]@{result=$result;calls=$script:calls}) -Depth 6 -Compress";
            var opened = await Run(shell, Own("Open-DebloatStore; Save-DebloatUndo @([pscustomobject]@{Id='sysmain';Kind='Service';Target='SysMain';Previous='Manual|False'}); @(Read-DebloatUndo)"));
            check(rooted != own && opened.Contains("\"success\":true") && opened.Contains("\"calls\":[\"Use:NexoSupport\",\"Use:debloat\",\"New:" + RunId + "\",\"Trust:" + RunId + "\",\"Write:undo.json\",\"Trust:NexoSupport\",\"Trust:debloat\",\"Trust:" + RunId + "\",\"Trust:undo.json\"]")
                && opened.Contains("\"Target\":\"SysMain\"") && opened.Contains("\"Previous\":\"Manual|False\""),
                "Debloat: a pasta do Desfazer confere NexoSupport e debloat, cria a da aplicação protegida e lê de volta conferindo cada nível: " + Short(opened));
            var again = await Run(shell, Own("Open-DebloatStore", "[void][IO.Directory]::CreateDirectory((Join-Path " + WindowsTools.Quote(temp) + " ('NexoSupport\\debloat\\' + '" + RunId + "')))"));
            check(again.Contains("\"success\":false") && again.Contains(DebloatScripts.StoreRefusal + "a pasta ") && again.Contains("já existia") && !again.Contains("New:"),
                "Debloat: pasta da aplicação que já existia é recusada, sem ser adotada: " + Short(again));
            var hostileOwn = await Run(shell, Own("Open-DebloatStore", "$script:hostile=$true"));
            check(hostileOwn.Contains(DebloatScripts.StoreRefusal + "debloat: ACE de escrita para S-1-5-32-545") && hostileOwn.Contains("Nenhum serviço, tarefa ou recurso foi alterado") && !hostileOwn.Contains("New:"),
                "Debloat: pasta debloat hostil é recusada com o motivo antes de criar qualquer coisa: " + Short(hostileOwn));
            var unreadable = await Run(shell, Own("@(Read-DebloatUndo)", "$script:untrusted=$true; $d=Join-Path " + WindowsTools.Quote(temp) + " ('NexoSupport\\debloat\\" + RunId + "'); [void][IO.Directory]::CreateDirectory($d); [IO.File]::WriteAllText((Join-Path $d 'undo.json'), '{}')"));
            var foreign = await Run(shell, Own("@(Read-DebloatUndo)", "$d=Join-Path " + WindowsTools.Quote(temp) + " ('NexoSupport\\debloat\\" + RunId + "'); [void][IO.Directory]::CreateDirectory($d); [IO.File]::WriteAllText((Join-Path $d 'undo.json'), '{\"RunId\":\"fedcba9876543210fedcba9876543210\",\"Items\":[]}')"));
            var absent = await Run(shell, Own("@(Read-DebloatUndo)", "Remove-Item -LiteralPath " + WindowsTools.Quote(Path.Combine(temp, "NexoSupport")) + " -Recurse -Force -ErrorAction SilentlyContinue"));
            check(unreadable.Contains("dono não confiável") && unreadable.Contains("Nada foi restaurado") && foreign.Contains("outra aplicação") && absent.Contains("não existe") && absent.Contains("\"success\":false"),
                "Debloat: o Desfazer elevado recusa registro protegido não confiável, de outra aplicação ou ausente: " + Short(unreadable + " | " + foreign + " | " + absent));
            // Marcador: Save-DebloatDone grava pela função protegida; Read-DebloatUndo tira o já restaurado e recusa o todo desfeito.
            string Seed(string done) => "Remove-Item -LiteralPath " + WindowsTools.Quote(Path.Combine(temp, "NexoSupport")) + " -Recurse -Force -ErrorAction SilentlyContinue; "
                + "$d=Join-Path " + WindowsTools.Quote(temp) + " ('NexoSupport\\debloat\\" + RunId + "'); [void][IO.Directory]::CreateDirectory($d); "
                + "[IO.File]::WriteAllText((Join-Path $d 'undo.json'), '{\"RunId\":\"" + RunId + "\",\"Items\":[{\"Id\":\"sysmain\",\"Kind\":\"Service\",\"Target\":\"SysMain\",\"Previous\":\"Manual|False\"},{\"Id\":\"recall\",\"Kind\":\"OptionalFeature\",\"Target\":\"Recall\",\"Previous\":\"Enabled\"}]}')"
                + (done.Length == 0 ? "" : "; [IO.File]::WriteAllText((Join-Path $d 'undone.json'), '" + done + "')");
            var saved = await Run(shell, Own("Save-DebloatDone @('recall|OptionalFeature|Recall'); @(Read-DebloatUndo)", Seed("")));
            var allDone = await Run(shell, Own("@(Read-DebloatUndo)", Seed("{\"RunId\":\"" + RunId + "\",\"Done\":[\"sysmain|Service|SysMain\",\"recall|OptionalFeature|Recall\"]}")));
            var foreignDone = await Run(shell, Own("@(Read-DebloatUndo)", Seed("{\"RunId\":\"fedcba9876543210fedcba9876543210\",\"Done\":[]}")));
            var untrustedDone = await Run(shell, Own("@(Read-DebloatUndo)", "$script:untrustedDone=$true; " + Seed("{\"RunId\":\"" + RunId + "\",\"Done\":[]}")));
            var caseOnly = await Run(shell, Own("@(Read-DebloatUndo)", Seed("{\"RunId\":\"" + RunId + "\",\"Done\":[\"SYSMAIN|Service|SysMain\",\"recall|OptionalFeature|Recall\"]}")));
            check(caseOnly.Contains("\"success\":true") && caseOnly.Contains("\"Target\":\"SysMain\""),
                "Debloat: no executor elevado, o marcador só tira o alvo gravado exatamente igual: " + Short(caseOnly));
            check(saved.Contains("\"success\":true") && saved.Contains("Write:undone.json") && saved.Contains("Trust:undone.json") && saved.Contains("\"Target\":\"SysMain\"") && !saved.Contains("\"Target\":\"Recall\""),
                "Debloat: o marcador é gravado pela função protegida e o Desfazer elevado seguinte só lê o que não foi restaurado: " + Short(saved));
            check(allDone.Contains("\"success\":false") && allDone.Contains("Esta aplicação já foi desfeita") && foreignDone.Contains("marcador do Desfazer é de outra aplicação")
                && untrustedDone.Contains("undone.json: dono não confiável") && untrustedDone.Contains("Nada foi restaurado"),
                "Debloat: o executor elevado recusa aplicação já desfeita e marcador de outra aplicação ou não confiável: " + Short(allDone + " | " + foreignDone + " | " + untrustedDone));
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }

        var sysmainOnly = DebloatScripts.Apply([DebloatCatalog.Find("sysmain")!], RunId);
        var noPointJson = await Run(shell, applyFakes.Replace("$script:seq=11", "") + "\n" + Elevated(sysmainOnly, storeFakes));
        check(noPointJson.Contains("\"success\":false") && !noPointJson.Contains("Set:SysMain"), "Debloat: sem ponto novo confirmado, o script elevado real para antes dos serviços: " + Short(noPointJson));
        var hostileStore = storeFakes.Replace("{ $script:calls+='Open' }", "{ throw 'Pasta protegida do Desfazer recusada: dono não confiável. Nenhum serviço, tarefa ou recurso foi alterado.' }");
        var hostileJson = await Run(shell, applyFakes + "\n" + Elevated(sysmainOnly, hostileStore));
        check(hostileStore != storeFakes && hostileJson.Contains("\"success\":false") && hostileJson.Contains("Pasta protegida do Desfazer recusada: dono") && !hostileJson.Contains("Checkpoint:") && !hostileJson.Contains("Set:SysMain"),
            "Debloat: pasta protegida recusada para o Aplicar antes do ponto de restauração e de qualquer serviço: " + Short(hostileJson));
        var unsavedStore = storeFakes.Replace("{ $script:calls+=('Save:'", "{ throw 'disco cheio'; $script:calls+=('Save:'");
        var unsavedJson = await Run(shell, applyFakes + "\n" + Elevated(sysmainOnly, unsavedStore));
        check(unsavedStore != storeFakes && unsavedJson.Contains("\"success\":false") && unsavedJson.Contains("o estado anterior não pôde ser gravado (disco cheio)") && !unsavedJson.Contains("Checkpoint:") && !unsavedJson.Contains("Set:SysMain"),
            "Debloat: sem gravar o estado anterior na pasta protegida, nenhum serviço muda e nenhum ponto é criado: " + Short(unsavedJson));

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
        // O Desfazer lê da pasta protegida (aqui, Read-DebloatUndo falso). Entradas fora do catálogo, de tipo trocado ou com
        // estado que o Nexo não grava são recusadas pelo próprio script elevado, sem chegar a nenhum comando.
        const string readFake = """
            function Read-DebloatUndo { @(
              [pscustomobject]@{Id='sysmain';Kind='Service';Target='SysMain';Previous='AutomaticDelayed|True'},
              [pscustomobject]@{Id='xbox-services';Kind='Service';Target='XblGameSave';Previous='Manual|False'},
              [pscustomobject]@{Id='ceip-tasks';Kind='ScheduledTask';Target='\Microsoft\Windows\Customer Experience Improvement Program\|Consolidator';Previous='Ready'},
              [pscustomobject]@{Id='ceip-tasks';Kind='ScheduledTask';Target='\Microsoft\Windows\Autochk\|Proxy';Previous='Disabled'},
              [pscustomobject]@{Id='recall';Kind='OptionalFeature';Target='Recall';Previous='Enabled'},
              [pscustomobject]@{Id='sysmain';Kind='Service';Target='WinDefend';Previous='Disabled|False'},
              [pscustomobject]@{Id='sysmain';Kind='Service';Target='SysMain';Previous='Boot|False'},
              [pscustomobject]@{Id='sysmain';Kind='OptionalFeature';Target='SysMain';Previous='Enabled'},
              [pscustomobject]@{Id='recall';Kind='OptionalFeature';Target='Microsoft-Hyper-V-All';Previous='Enabled'},
              [pscustomobject]@{Id='ceip-tasks';Kind='ScheduledTask';Target='\Microsoft\Windows\Customer Experience Improvement Program\|Consolidator';Previous='Ready;Start-Process calc'},
              [pscustomobject]@{Id='sysmain';Kind='Service';Target='SYSMAIN';Previous='Manual|False'}) }
            function Read-DebloatDone { @('xbox-services|Service|XboxNetApiSvc') }
            function Save-DebloatDone($done) { $script:calls+=('Done:'+(@($done) -join ',')) }

            """;
        var undoScript = DebloatScripts.Undo(RunId);
        var undoJson = await Run(shell, undoFakes + "\n$data=& { " + undoScript.Replace(DebloatScripts.StoreFunctions, readFake) + " }\nConvertTo-Json -InputObject ([pscustomobject]@{data=$data;calls=$script:calls}) -Depth 8 -Compress");
        string[] restoredCalls = [];
        try { using var doc = JsonDocument.Parse(undoJson); restoredCalls = doc.RootElement.GetProperty("calls").EnumerateArray().Select(c => c.GetString()!).ToArray(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        check(undoScript.Contains(DebloatScripts.StoreFunctions)
            && restoredCalls.SequenceEqual(["Set:SysMain:Automatic", "sc:config SysMain start= delayed-auto", "Start:SysMain", "Set:XblGameSave:Manual", "EnableTask:Consolidator", "EnableFeature:Recall",
                @"Done:xbox-services|Service|XboxNetApiSvc,sysmain|Service|SysMain,xbox-services|Service|XblGameSave,ceip-tasks|ScheduledTask|\Microsoft\Windows\Customer Experience Improvement Program\|Consolidator,ceip-tasks|ScheduledTask|\Microsoft\Windows\Autochk\|Proxy,recall|OptionalFeature|Recall"]),
            "Debloat: o script real do Desfazer restaura início, execução, tarefa e recurso do registro protegido: " + string.Join(",", restoredCalls) + " " + Short(undoJson));
        check(undoJson.Split("fora do catálogo").Length - 1 == 6 && undoJson.Split("\"Status\":\"Ok\"").Length - 1 == 5,
            "Debloat: o script elevado do Desfazer recusa alvo fora do catálogo, tipo trocado, maiúsculas diferentes e estado que o Nexo não grava: " + Short(undoJson));
        check(undoJson.Contains("\"Marked\":true") && DebloatScripts.ParseMarker("{\"success\":true,\"data\":" + JsonDocument.Parse(undoJson).RootElement.GetProperty("data").GetRawText() + "}") == null,
            "Debloat: o script real do Desfazer confirma o marcador na saída: " + Short(undoJson));
        var markFailJson = await Run(shell, undoFakes + "\n" + Elevated(undoScript, readFake.Replace("{ $script:calls+=('Done:'", "{ throw 'disco cheio'; $script:calls+=('Done:'")));
        check(markFailJson.Contains("\"success\":true") && DebloatScripts.ParseMarker(ResultOf(markFailJson)) == "disco cheio" && markFailJson.Contains("\"Status\":\"Ok\""),
            "Debloat: falha ao gravar o marcador não esconde o resultado e chega ao aplicativo com o motivo: " + Short(markFailJson));
        var nothingJson = await Run(shell, undoFakes + "\n" + Elevated(undoScript, "function Read-DebloatUndo { @([pscustomobject]@{Id='sysmain';Kind='Service';Target='WinDefend';Previous='Disabled|False'}) }\nfunction Read-DebloatDone { @() }\nfunction Save-DebloatDone($done) { throw 'não devia gravar' }\n"));
        check(DebloatScripts.ParseMarker(ResultOf(nothingJson)) == null && nothingJson.Contains("fora do catálogo") && !nothingJson.Contains("não devia gravar"),
            "Debloat: sem nada restaurado, o Desfazer não grava marcador e não falha por isso: " + Short(nothingJson));
        var refusedJson = await Run(shell, undoFakes + "\n" + Elevated(undoScript, "function Read-DebloatUndo { throw 'Pasta protegida do Desfazer recusada: undo.json: não existe. Nada foi restaurado.' }\n"));
        check(refusedJson.Contains("\"success\":false") && refusedJson.Contains("undo.json: não existe") && !refusedJson.Contains("Set:"),
            "Debloat: sem registro protegido confiável, o script elevado do Desfazer não restaura nada: " + Short(refusedJson));

        // O bloco real da pasta protegida (funções da rede sem aspas duplas) é PowerShell válido e decide como a rede.
        var block = Path.Combine(Path.GetTempPath(), "NexoDebloatStore-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(block, DebloatScripts.StoreFunctions, new UTF8Encoding(true));
        string parseJson;
        try
        {
            parseJson = await Run(shell, "$e=$null; $t=$null; $ast=[System.Management.Automation.Language.Parser]::ParseFile('" + block.Replace("'", "''") + "', [ref]$t, [ref]$e); "
                + "$names=@($ast.FindAll({param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst]}, $true) | ForEach-Object { $_.Name }); "
                + "ConvertTo-Json -InputObject ([pscustomobject]@{errors=@($e).Count;names=$names}) -Compress");
        }
        finally { File.Delete(block); }
        check(parseJson.Contains("\"errors\":0") && ProtectedFolderScript.Functions.Concat(["Open-DebloatStore", "Save-DebloatUndo", "Read-DebloatUndo", "Read-DebloatDone", "Save-DebloatDone"]).All(f => parseJson.Contains("\"" + f + "\"")),
            "Debloat: o bloco da pasta protegida é PowerShell válido e define as funções da rede e as do Desfazer: " + Short(parseJson));
        const string reasonCases = """

            $w=[pscustomobject]@{Sid='S-1-5-32-545';Rights=0x2;Type='Allow';Propagation=0}
            $r=[pscustomobject]@{Sid='S-1-5-32-545';Rights=0x1200A9;Type='Allow';Propagation=0}
            $i=[pscustomobject]@{Sid='S-1-5-32-545';Rights=0x2;Type='Allow';Propagation=2}
            $s=[pscustomobject]@{Sid='S-1-5-18';Rights=0x1F01FF;Type='Allow';Propagation=0}
            ConvertTo-Json -Compress -InputObject @(
              [string](Get-UntrustedReason 'C:\x' $true 'S-1-5-21-1-1001' $true @($r)),
              [string](Get-UntrustedReason 'C:\x' $true 'S-1-5-32-544' $false @($r)),
              [string](Get-UntrustedReason 'C:\x' $true 'S-1-5-32-544' $true @($w)),
              [string](Get-UntrustedReason 'C:\x' $true 'S-1-5-32-544' $true @($r,$i,$s)))
            """;
        var reasons = await Run(shell, ProtectedFolderScript.Text + reasonCases);
        string[] verdicts = [];
        try { verdicts = JsonSerializer.Deserialize<string[]>(reasons) ?? []; } catch (JsonException) { }
        check(verdicts.Length == 4 && verdicts[0].StartsWith(@"'C:\x': dono não confiável") && verdicts[1].Contains("herda permissões") && verdicts[2].Contains("ACE de escrita para S-1-5-32-545") && verdicts[3] == "",
            "Debloat: a conferência da pasta protegida recusa dono, herança e escrita de Usuários como na rede: " + Short(reasons));
    }

    private static string Short(string text) => text.Length <= 400 ? text : text[..400] + "…";

    // A saída do embrulho Elevated traz {result, calls}; o executor real devolve só o result.
    private static string ResultOf(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty("result").GetRawText(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return json; }
    }

    // O script vai num .ps1 temporário com -File, não em -EncodedCommand: com os cmdlets falsos, o Aplicar sem compactar
    // passa de 32.767 caracteres em Base64 (UTF-16) e o Windows recusa iniciar o processo (Win32Exception 206, CI do PR
    // #130). O pwsh fora do Windows não tem esse limite, por isso a suíte passava no Mac. O arquivo leva BOM UTF-8 para o
    // Windows PowerShell 5.1 ler os acentos, e é apagado mesmo quando o processo falha ao iniciar.
    internal static IReadOnlyList<string> FileArguments(string path) => ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path];
    internal const string Prelude = "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8\n";

    private static async Task<string> Run(string shell, string script)
    {
        var path = Path.Combine(Path.GetTempPath(), "nexo-debloat-check-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            File.WriteAllText(path, Prelude + script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var start = new ProcessStartInfo(shell) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var argument in FileArguments(path)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var text = (await output).Trim();
            return text.Length > 0 ? text : "ERRO: " + (await error).Trim();
        }
        finally { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // O harness precisa aguentar scripts maiores que a linha de comando do Windows; a linha passada ao processo não cresce
    // com o script. Fora do Windows o limite não existe, então a prova é dupla: o script longo roda e devolve o marcador, e
    // os argumentos ficam muito abaixo do limite, ao contrário do que o -EncodedCommand daria com o mesmo texto.
    private static async Task LongScript(string shell, Action<bool, string> check)
    {
        var filler = "# " + new string('x', 120) + "\n";
        var script = string.Concat(Enumerable.Repeat(filler, 400)) + "$v='ação'; Write-Output ('longo-ok:' + $v)";
        var encoded = WindowsTools.CommandLength(["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Prelude + script))]);
        var viaFile = WindowsTools.CommandLength(FileArguments(Path.Combine(Path.GetTempPath(), "nexo-debloat-check-" + new string('0', 32) + ".ps1")));
        check(script.Length > 32_000 && encoded > 32_767 && viaFile < 1_000,
            $"Debloat: o harness passa o script por arquivo, sem depender do tamanho dele ({script.Length} caracteres; {encoded} em -EncodedCommand; {viaFile} por arquivo)");
        var before = Directory.GetFiles(Path.GetTempPath(), "nexo-debloat-check-*.ps1").Length;
        var output = await Run(shell, script);
        check(output == "longo-ok:ação", "Debloat: o harness roda um script acima de 32 mil caracteres e preserva os acentos: " + Short(output));
        check(Directory.GetFiles(Path.GetTempPath(), "nexo-debloat-check-*.ps1").Length <= before, "Debloat: o harness apaga o script temporário depois de rodar");
        var missing = Path.Combine(Path.GetTempPath(), "nexo-debloat-check-shell-inexistente-" + Guid.NewGuid().ToString("N"));
        var failedBefore = Directory.GetFiles(Path.GetTempPath(), "nexo-debloat-check-*.ps1").Length;
        var failed = false;
        try { await Run(missing, "Write-Output 1"); } catch (System.ComponentModel.Win32Exception) { failed = true; }
        check(failed && Directory.GetFiles(Path.GetTempPath(), "nexo-debloat-check-*.ps1").Length <= failedBefore, "Debloat: o harness apaga o script temporário mesmo quando o processo não inicia");
    }
}
