namespace Nexo.Support;

public enum DebloatKind { App, Service, UserRegistry, ScheduledTask, OptionalFeature }
public enum DebloatWindows { Both, Windows10, Windows11 }
public enum DebloatGate { None, XboxGames, SystemSsd }
public enum DebloatValueKind { DWord, String, Binary }

/// <summary>Valor gravado em HKCU. A chave é relativa ao HKEY_CURRENT_USER; o catálogo não tem chave de política nem de HKLM.</summary>
public sealed record DebloatRegistryValue(string Key, string Name, DebloatValueKind Kind, string Value);

/// <summary>
/// Item do catálogo. Targets são nomes exatos: pacote Appx, serviço, tarefa ("\Caminho\|Nome") ou recurso opcional.
/// Os itens Service, ScheduledTask e OptionalFeature exigem o executor elevado; os demais rodam no processo do Nexo.
/// </summary>
public sealed record DebloatItem(string Id, string Category, string Title, DebloatWindows Windows, DebloatKind Kind, bool Default,
    IReadOnlyList<string> Targets, IReadOnlyList<DebloatRegistryValue> Values, string Warning = "", DebloatGate Gate = DebloatGate.None)
{
    public bool Elevated => Kind is DebloatKind.Service or DebloatKind.ScheduledTask or DebloatKind.OptionalFeature;
    public bool Applies(int build) => Windows switch
    {
        DebloatWindows.Windows10 => !DebloatCatalog.IsWindows11(build),
        DebloatWindows.Windows11 => DebloatCatalog.IsWindows11(build),
        _ => true,
    };
}

/// <summary>
/// Catálogo adaptado de rivaed/Windows10-Debloat e rivaed/Windows11-Debloat (MIT), com o recorte da #127. O script deles
/// não é executado: o Nexo remove os apps só do usuário atual, grava apenas HKCU e guarda o estado anterior de cada item.
/// </summary>
public static class DebloatCatalog
{
    public const string Credit = "Catálogo adaptado de rivaed/Windows10-Debloat e rivaed/Windows11-Debloat (licença MIT).";
    public const int Windows11Build = 22000;
    public static bool IsWindows11(int build) => build >= Windows11Build;

    public const string AppsCategory = "Apps";
    public const string PrivacyCategory = "Privacidade";
    public const string PerformanceCategory = "Desempenho e interface";

    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    private static DebloatItem App(string id, string title, DebloatWindows windows, bool selected, string warning, params string[] packages) =>
        new(id, AppsCategory, title, windows, DebloatKind.App, selected, packages, [], warning);
    private static DebloatRegistryValue DWord(string key, string name, int value) => new(key, name, DebloatValueKind.DWord, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Pacotes cuja presença indica que o usuário joga com conta Xbox: remover o login quebraria Minecraft e Game Pass.</summary>
    public static readonly IReadOnlyList<string> XboxGameIndicators =
        ["Microsoft.XboxApp", "Microsoft.GamingApp", "Microsoft.XboxGamingOverlay", "Microsoft.MinecraftUWP", "Microsoft.4297127D64EC6"];

    public static readonly IReadOnlyList<DebloatItem> Items =
    [
        App("bing-news", "Notícias (Bing News)", DebloatWindows.Both, true, "", "Microsoft.BingNews"),
        App("bing-weather", "Clima (Bing Weather)", DebloatWindows.Both, true, "", "Microsoft.BingWeather"),
        App("tips", "Dicas", DebloatWindows.Both, true, "", "Microsoft.Getstarted"),
        App("solitaire", "Solitaire Collection", DebloatWindows.Both, true, "", "Microsoft.MicrosoftSolitaireCollection"),
        App("maps", "Mapas", DebloatWindows.Both, true, "", "Microsoft.WindowsMaps"),
        App("feedback-hub", "Hub de Comentários", DebloatWindows.Both, true, "", "Microsoft.WindowsFeedbackHub"),
        App("office-lens", "Office Lens", DebloatWindows.Both, true, "", "Microsoft.Office.Lens"),
        App("print3d", "Print 3D", DebloatWindows.Both, true, "", "Microsoft.Print3D"),
        App("network-speed", "Teste de Velocidade de Rede", DebloatWindows.Both, true, "", "Microsoft.NetworkSpeedTest"),
        App("mixed-reality", "Mixed Reality Portal", DebloatWindows.Both, true, "", "Microsoft.MixedReality.Portal"),
        App("cortana", "Cortana", DebloatWindows.Both, true, "", "Microsoft.549981C3F5F10"),
        App("bing-search", "Bing Search", DebloatWindows.Windows11, true, "", "Microsoft.BingSearch"),
        App("clipchamp", "Clipchamp", DebloatWindows.Windows11, true, "", "Clipchamp.Clipchamp"),
        App("linkedin", "LinkedIn", DebloatWindows.Windows11, true, "", "7EE7776C.LinkedInforWindows"),
        App("dev-home", "Dev Home", DebloatWindows.Windows11, true, "", "Microsoft.Windows.DevHome"),
        App("family", "Microsoft Family", DebloatWindows.Windows11, false, "É o controle parental da conta Microsoft. Remova só se ninguém usa.", "MicrosoftCorporationII.MicrosoftFamily"),
        App("power-automate", "Power Automate", DebloatWindows.Windows11, false, "Fluxos automatizados do usuário deixam de rodar.", "Microsoft.PowerAutomateDesktop"),
        App("teams-personal", "Microsoft Teams (pessoal)", DebloatWindows.Windows11, false, "Conversas do Teams pessoal ficam só na web.", "MSTeams"),
        App("copilot-app", "Copilot (app)", DebloatWindows.Windows11, false, "", "Microsoft.Copilot"),
        App("xbox-app", "Xbox (app)", DebloatWindows.Both, false, "Quem joga pela conta Xbox ou pelo Game Pass deve manter.", "Microsoft.XboxApp", "Microsoft.GamingApp"),
        App("xbox-game-bar", "Xbox Game Bar e sobreposições", DebloatWindows.Both, false, "A Game Bar (Win+G) também grava a tela; quem joga deve manter.", "Microsoft.XboxGameOverlay", "Microsoft.XboxGamingOverlay", "Microsoft.XboxSpeechToTextOverlay"),
        App("widgets-package", "Widgets (pacote)", DebloatWindows.Both, false, "O painel de Widgets some por completo. O Desfazer tenta restaurá-lo pelo pacote que o Windows mantém neste PC; se não der, ele volta reinstalando o Windows Web Experience Pack pela Microsoft Store, quando ela ainda o oferece.", "MicrosoftWindows.Client.WebExperience"),
        new("xbox-login", AppsCategory, "Login do Xbox", DebloatWindows.Both, DebloatKind.App, false, ["Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI"], [],
            "O login do Minecraft e do Game Pass para de funcionar.", DebloatGate.XboxGames),

        new("content-delivery", PrivacyCategory, "Apps promovidos e sugestões (Content Delivery)", DebloatWindows.Both, DebloatKind.UserRegistry, true, [],
        [
            DWord(ContentDelivery, "ContentDeliveryAllowed", 0), DWord(ContentDelivery, "SilentInstalledAppsEnabled", 0),
            DWord(ContentDelivery, "OemPreInstalledAppsEnabled", 0), DWord(ContentDelivery, "PreInstalledAppsEnabled", 0),
            DWord(ContentDelivery, "SystemPaneSuggestionsEnabled", 0), DWord(ContentDelivery, "RotatingLockScreenOverlayEnabled", 0),
            DWord(ContentDelivery, "SubscribedContent-338388Enabled", 0), DWord(ContentDelivery, "SubscribedContent-338389Enabled", 0),
            DWord(ContentDelivery, "SubscribedContent-353694Enabled", 0), DWord(ContentDelivery, "SubscribedContent-310093Enabled", 0),
        ]),
        new("advertising-id", PrivacyCategory, "ID de publicidade", DebloatWindows.Both, DebloatKind.UserRegistry, true, [],
            [DWord(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)]),
        // TaskbarDa só existe no Windows 11 (validação no PC da 63f01f0, Windows 10 22H2, 23/11/2026: o item aparecia no 10 e não
        // tinha efeito). O botão do 10 é "Notícias e interesses" (Feeds\ShellFeedsTaskbarViewMode), que fica de fora: gravar esse
        // valor direto não é estável nas versões recentes do 10, e a política EnableFeeds faz o Windows mostrar "gerenciado pela
        // organização", como a telemetria por política, que já está fora do escopo.
        new("widgets-button", PrivacyCategory, "Ocultar o botão de Widgets", DebloatWindows.Windows11, DebloatKind.UserRegistry, true, [],
            [DWord(Advanced, "TaskbarDa", 0)]),
        new("ceip-tasks", PrivacyCategory, "Tarefas agendadas do CEIP", DebloatWindows.Both, DebloatKind.ScheduledTask, false,
        [
            @"\Microsoft\Windows\Application Experience\|Microsoft Compatibility Appraiser",
            @"\Microsoft\Windows\Application Experience\|ProgramDataUpdater",
            @"\Microsoft\Windows\Customer Experience Improvement Program\|Consolidator",
            @"\Microsoft\Windows\Customer Experience Improvement Program\|UsbCeip",
            @"\Microsoft\Windows\Autochk\|Proxy",
            @"\Microsoft\Windows\DiskDiagnostic\|Microsoft-Windows-DiskDiagnosticDataCollector",
        ], []),
        new("recall", PrivacyCategory, "Recall (capturas de tela do Copilot+)", DebloatWindows.Windows11, DebloatKind.OptionalFeature, false, ["Recall"], [],
            "Desativa o recurso opcional; o Windows pede reinício para concluir."),

        new("visual-effects", PerformanceCategory, "Efeitos visuais para desempenho", DebloatWindows.Both, DebloatKind.UserRegistry, false, [],
        [
            DWord(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 3),
            // Máscara de "melhor desempenho" mantendo a suavização de fontes, como no catálogo de origem.
            new(@"Control Panel\Desktop", "UserPreferencesMask", DebloatValueKind.Binary, "90-12-03-80-12-00-00-00"),
            new(@"Control Panel\Desktop", "DragFullWindows", DebloatValueKind.String, "0"),
            new(@"Control Panel\Desktop", "FontSmoothing", DebloatValueKind.String, "2"),
            new(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", DebloatValueKind.String, "0"),
            DWord(Advanced, "TaskbarAnimations", 0), DWord(Advanced, "ListviewAlphaSelect", 0), DWord(Advanced, "ListviewShadow", 0),
            DWord(@"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0),
        ], "Muda a aparência do Windows; o efeito completo vem no próximo logon."),
        new("file-extensions", PerformanceCategory, "Mostrar extensões de arquivo", DebloatWindows.Both, DebloatKind.UserRegistry, true, [],
            [DWord(Advanced, "HideFileExt", 0)]),
        new("xbox-services", PerformanceCategory, "Serviços do Xbox Live", DebloatWindows.Both, DebloatKind.Service, false, ["XblGameSave", "XboxNetApiSvc"], [],
            "Quem joga perde o salvamento de jogos na nuvem."),
        new("sysmain", PerformanceCategory, "SysMain (Superfetch)", DebloatWindows.Both, DebloatKind.Service, false, ["SysMain"], [],
            "Oferecido só com o disco do sistema em SSD; não há garantia de ganho.", DebloatGate.SystemSsd),
    ];

    /// <summary>
    /// Alvos que a #127 deixa de fora de propósito. Um teste garante que nenhum entra no catálogo: OneDrive, Store, busca,
    /// MDM, telemetria por política, ferramentas de suporte e apps de uso real.
    /// </summary>
    public static readonly IReadOnlyList<string> Excluded =
    [
        "OneDrive", "Microsoft.OneDrive", "Microsoft.StorePurchaseApp", "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller",
        "WSearch", "dmwappushservice", "DiagTrack", "BITS", "wuauserv", "Fax",
        "Microsoft.GetHelp", "MicrosoftCorporationII.QuickAssist",
        "Microsoft.WindowsCamera", "microsoft.windowscommunicationsapps", "Microsoft.OutlookForWindows", "Microsoft.People",
        "Microsoft.MicrosoftStickyNotes", "Microsoft.WindowsAlarms", "Microsoft.WindowsSoundRecorder", "Microsoft.Office.OneNote",
        "Microsoft.ZuneMusic", "Microsoft.ZuneVideo", "Microsoft.Todos", "Microsoft.YourPhone", "MicrosoftWindows.CrossDevice",
        "Microsoft.MicrosoftOfficeHub", "MicrosoftTeams",
        // Menu de contexto clássico do Windows 11.
        "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}",
    ];

    public static IReadOnlyList<DebloatItem> ForBuild(int build) => Items.Where(item => item.Applies(build)).ToArray();
    public static DebloatItem? Find(string id) => Items.FirstOrDefault(item => item.Id == id);
}
