using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nexo.Support;

namespace Nexo.Checks;

// #143, etapas 2 e 3: paleta Ctrl+K, tela Início, barra de título e barra lateral. O WPF não roda no Mac nem no CI de checks: o
// catálogo, o filtro, o teclado e os dados da Início são testados aqui; a janela é lida como texto, e a interação real fica no
// --screenshot do Windows (ShellPreview.cs).
internal static class ShellChecks
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    internal static void Run(Action<bool, string> assert)
    {
        Palette(assert);
        HomeContent(assert);
        HomeHealth(assert);
        Window(assert);
    }

    private static void Palette(Action<bool, string> assert)
    {
        var all = CommandCatalog.Build(ThemeCatalog.Onyx, sidebarCollapsed: false);
        var pages = new[] { "home", "chat", "health", "history", "tickets", "tools", "settings", "audit" };
        assert(all.Select(item => item.Id).SequenceEqual(pages.Select(page => "page:" + page).Concat(["action:new-chat", "action:theme", "action:sidebar"]))
            && all.Select(item => item.Id).Distinct().Count() == all.Count, "#143: a paleta lista as oito telas e as três ações que já existem, sem Usuários");
        assert(all.Single(item => item.Id == "action:theme").Title.EndsWith("Glacial (claro)") && CommandCatalog.Build(ThemeCatalog.Glacial, false).Single(item => item.Id == "action:theme").Title.EndsWith("Ônix (escuro)")
            && CommandCatalog.Build(ThemeCatalog.Onyx, true).Single(item => item.Id == "action:sidebar").Title == "Expandir a barra lateral",
            "#143: as ações da paleta dizem o tema de destino e o estado da barra lateral");

        string? First(string query) => CommandCatalog.Filter(all, query).FirstOrDefault()?.Id;
        assert(CommandCatalog.Filter(all, "").Count == all.Count && CommandCatalog.Filter(all, "   ").Count == all.Count, "#143: a paleta sem busca mostra tudo");
        assert(First("saude") == "page:health" && First("SAÚDE") == "page:health" && First("registro acoes") == "page:audit"
            && First("configuracoes") == "page:settings" && First("inicio") == "page:home", "#143: o filtro ignora acento e caixa e exige todos os termos");
        assert(First("nova conversa") == "action:new-chat" && First("tema") == "action:theme" && First("glacial") == "action:theme" && First("lateral") == "action:sidebar",
            "#143: o filtro acha as ações pelas palavras-chave");
        assert(First("con") == "page:settings" && CommandCatalog.Filter(all, "con").Count > 1, "#143: o título que começa com o termo vem antes da palavra-chave");
        assert(CommandCatalog.Filter(all, "tela").Count == pages.Length && CommandCatalog.Filter(all, "ação").Count == 3, "#143: o tipo (tela ou ação) também filtra");
        assert(CommandCatalog.Filter(all, "zzzz").Count == 0 && CommandCatalog.Filter(all, "usuarios").Count == 0, "#143: busca sem resultado volta vazia e Usuários não aparece");

        var three = all.Take(3).ToList();
        assert(CommandCatalog.HandleKey("Down", 0, three).Selected == 1 && CommandCatalog.HandleKey("Down", 2, three).Selected == 0
            && CommandCatalog.HandleKey("Up", 0, three).Selected == 2 && CommandCatalog.HandleKey("End", 0, three).Selected == 2
            && CommandCatalog.HandleKey("Home", 2, three).Selected == 0, "#143: setas circulam pelos resultados; Home e End vão às pontas");
        var enter = CommandCatalog.HandleKey("Enter", 1, three);
        assert(enter.Execute == three[1].Id && enter.Close, "#143: Enter executa o item escolhido e fecha a paleta");
        var none = CommandCatalog.HandleKey("Enter", 0, []);
        assert(none.Execute == null && !none.Close && none.Selected == -1 && CommandCatalog.HandleKey("Down", -1, []).Selected == -1,
            "#143: Enter sem resultado não executa nada e mantém a paleta aberta");
        var escape = CommandCatalog.HandleKey("Escape", 1, three);
        assert(escape.Close && escape.Execute == null && CommandCatalog.HandleKey("Tab", 1, three) == (1, null, false)
            && CommandCatalog.HandleKey("Down", 9, three).Selected == 0, "#143: Esc fecha sem executar; tecla sem papel não muda nada");

        // Cada item da paleta chega a um código que já existe: a tela no ShowPage e a ação no RunPaletteCommand.
        var code = Source("MainWindow.xaml.cs"); var palette = Source("CommandPalette.cs"); var icons = Source("UiIcons.cs");
        assert(pages.All(page => page == "chat" ? code.Contains("if (target == \"chat\") return;") : code.Contains("case \"" + page + "\":")) && all.Where(item => item.Id.StartsWith("action:")).All(item => palette.Contains("case \"" + item.Id + "\":")),
            "#143: toda tela e ação da paleta tem destino no código");
        assert(all.All(item => icons.Contains("[\"" + item.Icon + "\"] =")), "#143: os ícones da paleta existem no UiIcons");
        assert(Regex.IsMatch(Source("WindowShell.cs"), @"key == Key\.K && modifiers == ModifierKeys\.Control") && palette.Contains("Key.Escape => \"Escape\"")
            && palette.Contains("Key.Down => \"Down\"") && palette.Contains("Key.Enter => \"Enter\"") && palette.Contains("CommandCatalog.HandleKey(")
            && Regex.IsMatch(Source("MainWindow.xaml"), "x:Name=\"PaletteHost\"[^>]*KeyboardNavigation.TabNavigation=\"Cycle\""),
            "#143: Ctrl+K abre; setas, Enter e Esc usam o teclado testado; Tab fica dentro da paleta");
    }

    private static void HomeContent(Action<bool, string> assert)
    {
        var fixes = HomeData.QuickFixes;
        assert(fixes.Select(fix => fix.Number).SequenceEqual(Enumerable.Range(1, fixes.Count)) && fixes.Count == 6
            && fixes.All(fix => fix.Prompt.Length > 5 && fix.Title.Length > 0 && Source("UiIcons.cs").Contains("[\"" + fix.Icon + "\"] =")),
            "#143: seis atalhos Resolver rápido numerados de 1 a 6, com ícone e pedido");
        string Topic(int number) => SupportService.Topic(fixes.Single(fix => fix.Number == number).Prompt);
        assert(Topic(1) == "network" && Topic(2) == "system" && Topic(4) == "audio" && Topic(5) == "camera",
            "#143: os pedidos dos atalhos caem no mesmo assunto do diagnóstico local (rede, sistema, áudio, câmera)");
        assert(fixes.Any(fix => fix.Prompt.Contains("lento")) && fixes.Any(fix => fix.Prompt.Contains("internet")) && fixes.Any(fix => fix.Prompt.Contains("impressora"))
            && fixes.Any(fix => fix.Prompt.Contains("som")), "#143: os atalhos cobrem PC lento, sem internet, impressora e som");

        // Atalho preenche e não envia: o clique e a tecla chamam FillAssistant, que só escreve no compositor.
        var home = Source("HomePage.cs"); var shell = Source("WindowShell.cs");
        var fill = Regex.Match(shell, @"private void FillAssistant\(string prompt\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        assert(fill.Contains("Composer.Text = prompt;") && fill.Contains("ShowPage(\"chat\")") && !fill.Contains("Send(") && !fill.Contains("Execute(")
            && home.Contains("button.Click += (_, _) => Guard(() => FillAssistant(fix.Prompt));") && !home.Contains("Send(")
            && shell.Contains("FillAssistant(fix.Prompt);"), "#143: atalhos e teclas 1 a 6 escrevem o pedido no Assistente sem enviar");
        var keys = Regex.Match(shell, @"private bool HandleWindowKey\(.*?\n    \}", RegexOptions.Singleline).Value;
        assert(Regex.IsMatch(keys, @"if \(PaletteOpen \|\| modifiers != ModifierKeys\.None \|\| page != ""home"" \|\| focused is TextBox or PasswordBox\) return false;")
            && keys.IndexOf("return false;", StringComparison.Ordinal) < keys.IndexOf("FillAssistant(", StringComparison.Ordinal),
            "#143: as teclas 1 a 6 só valem na Início, sem modificador, sem a paleta aberta e fora de campo de texto");

        var data = new Database();
        var start = new DateTime(2026, 11, 1, 9, 0, 0);
        for (var i = 0; i < 7; i++) data.Conversations.Add(new Conversation { UserId = "a", Title = "A" + i, Created = start.AddHours(i) });
        data.Conversations.Add(new Conversation { UserId = "b", Title = "B", Created = start.AddDays(3) });
        var recent = HomeData.RecentConversations(data, "a");
        assert(recent.Select(item => item.Title).SequenceEqual(["A6", "A5", "A4", "A3", "A2"]) && HomeData.RecentConversations(data, "x").Count == 0,
            "#143: atendimentos recentes: os cinco mais novos do próprio perfil; sem atendimento, lista vazia");
        assert(HomeData.Greeting("Maria Souza Lima") == "Olá, Maria." && HomeData.Greeting("  ") == "Olá." && HomeData.Greeting(null) == "Olá."
            && HomeData.DateLine(new DateTime(2026, 11, 22, 10, 0, 0)) == "domingo, 22 de novembro de 2026", "#143: saudação com o nome da sessão e data em português");

        // Nenhum texto fixo do protótipo (versão, nomes, máquina, diálogo inventado).
        var sources = home + Source("HomeData.cs") + shell + Source("CommandPalette.cs") + Source("MainWindow.xaml");
        var leftovers = new[] { "2.8.4", "Camila", "DESKTOP-", "Spooler reiniciado", "NexoMemTeste", "Ryzen", "Windows 11 Pro", "qwen3:8b", "Sincronizado há" }.Where(sources.Contains).ToArray();
        assert(leftovers.Length == 0, "#143: nenhum dado fixo do protótipo na janela e na Início" + (leftovers.Length > 0 ? ": " + string.Join(", ", leftovers) : ""));
    }

    private static void HomeHealth(Action<bool, string> assert)
    {
        var limits = new HealthThresholds();
        var unavailable = HomeData.Health(null, "disco cheio", limits, DateTimeOffset.UtcNow);
        assert(unavailable.State == HomeHealthState.Unavailable && unavailable.Detail.Contains("disco cheio") && unavailable.Today.Count == 0 && unavailable.Latest == null,
            "#143: sem histórico operacional, a Início mostra o monitor indisponível e o motivo");
        var root = Path.Combine(Path.GetTempPath(), "NexoHome-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OperationalStore(root);
            var now = DateTimeOffset.UtcNow;
            var loading = HomeData.Health(store, null, limits, now);
            assert(loading.State == HomeHealthState.Loading && loading.Latest == null && loading.Today.Count == 0, "#143: sem leitura, a Início mostra o estado de carregando");

            store.SaveSnapshot(new HealthSnapshot { Time = now.AddSeconds(-40), CpuPercent = 37, MemoryUsedPercent = 54, Connectivity = "local", CollectionStatus = "complete",
                Disks = [new DiskHealth("C:\\", 80, 200)] });
            var stable = HomeData.Health(store, null, limits, now);
            assert(stable.State == HomeHealthState.Stable && stable.Headline == "Estável" && stable.Latest?.CpuPercent == 37
                && stable.Detail.StartsWith("Última leitura às " + now.AddSeconds(-40).ToLocalTime().ToString("HH:mm")) && stable.Detail.Contains("completa"),
                "#143: com leitura e sem ocorrência, a Início mostra Estável e a hora da última leitura");

            for (var i = 3; i >= 1; i--)
                store.SaveSnapshot(new HealthSnapshot { Time = now.AddSeconds(-10 * i), CpuPercent = 95, MemoryUsedPercent = 54, Connectivity = "local", CollectionStatus = "complete",
                    Disks = [new DiskHealth("C:\\", 80, 200)] });
            store.SaveFindings([new HealthFinding { Id = "cpu-high:checks", Time = now.AddSeconds(-5), Severity = "warning", Problem = "Uso persistente de CPU" },
                new HealthFinding { Id = "cpu-high:ontem", Time = now.AddDays(-2), Severity = "warning", Problem = "Ocorrência de anteontem" }]);
            var attention = HomeData.Health(store, null, limits, now);
            var today = attention.Today.Select(item => item.Finding.Problem).ToArray();
            assert(today.SequenceEqual(["Uso persistente de CPU"]), "#143: ocorrências de hoje: só as gravadas desde a meia-noite local");
            assert(attention.State == HomeHealthState.Attention && attention.Headline == "Estável, com 1 ponto de atenção"
                && attention.Today.Single().Status.State == OccurrenceState.Persistent,
                "#143: ocorrência que persiste nas leituras vira ponto de atenção no estado do computador (" + attention.State + ", " + string.Join(", ", attention.Today.Select(item => item.Status.Label)) + ")");

            for (var i = 2; i >= 1; i--)
                store.SaveSnapshot(new HealthSnapshot { Time = now.AddSeconds(-2 * i), CpuPercent = 95, MemoryUsedPercent = 54, Connectivity = "local", CollectionStatus = "complete",
                    Disks = [new DiskHealth("C:\\", 5, 200)] });
            store.SaveFindings([new HealthFinding { Id = "disk-low:C:\\:checks", Time = now.AddSeconds(-1), Severity = "critical", Problem = "Pouco espaço no volume C:\\" }]);
            var critical = HomeData.Health(store, null, limits, now);
            assert(critical.State == HomeHealthState.Critical && critical.Headline == "1 ocorrência crítica" && critical.Today.Count == 2,
                "#143: ocorrência crítica que persiste vira o estado crítico (" + critical.State + ", " + string.Join(", ", critical.Today.Select(item => item.Finding.Problem + ": " + item.Status.Label)) + ")");
            var midnight = HomeData.StartOfToday(now);
            assert(midnight <= now && now - midnight < TimeSpan.FromDays(1) && midnight.ToLocalTime().TimeOfDay == TimeSpan.Zero, "#143: o dia da Início começa à meia-noite local");

            // O monitor grava a ocorrência uma vez, quando ela começa: a de antes da meia-noite que ainda persiste não some do estado.
            var carriedStore = new OperationalStore(Path.Combine(root, "ontem"));
            for (var i = 3; i >= 1; i--)
                carriedStore.SaveSnapshot(new HealthSnapshot { Time = now.AddSeconds(-10 * i), CpuPercent = 20, MemoryUsedPercent = 54, Connectivity = "local", CollectionStatus = "complete",
                    Disks = [new DiskHealth("C:\\", 5, 200)] });
            carriedStore.SaveFindings([new HealthFinding { Id = "disk-low:C:\\:ontem", Time = midnight.AddMinutes(-30), Severity = "critical", Problem = "Pouco espaço no volume C:\\" }]);
            var carried = HomeData.Health(carriedStore, null, limits, now);
            assert(carried.State == HomeHealthState.Critical && carried.Headline == "1 ocorrência crítica" && carried.Today.Count == 0
                && HomeData.Current(carriedStore, carried.Latest, limits, now).Single().Status.State == OccurrenceState.Persistent,
                "#143: ocorrência de antes da meia-noite que persiste continua no estado do computador, como na Saúde do PC (" + carried.State + ", " + carried.Headline + ")");
            // Leitura antiga (monitor parado ou falhando): estado desconhecido, como na Saúde do PC, e nunca "Estável".
            var staleStore = new OperationalStore(Path.Combine(root, "antiga"));
            staleStore.SaveSnapshot(new HealthSnapshot { Time = now.AddMinutes(-30), CpuPercent = 20, MemoryUsedPercent = 54, Connectivity = "local", CollectionStatus = "complete",
                Disks = [new DiskHealth("C:\\", 80, 200)] });
            var stale = HomeData.Health(staleStore, null, limits, now);
            assert(stale.State == HomeHealthState.Attention && stale.Headline == "Sem leitura recente" && stale.Detail.Contains("estado atual das ocorrências é desconhecido"),
                "#143: sem leitura recente, a Início diz que o estado é desconhecido em vez de Estável (" + stale.State + ", " + stale.Headline + ")");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static void Window(Action<bool, string> assert)
    {
        var xaml = Source("MainWindow.xaml"); var shell = Source("WindowShell.cs"); var app = Source("App.xaml");
        var document = XDocument.Parse(xaml);
        var chrome = document.Descendants(Presentation + "WindowChrome").Single(element => element.Attribute("CaptionHeight") != null);
        var row = document.Descendants(Presentation + "RowDefinition").First();
        assert((string?)chrome.Attribute("CaptionHeight") == (string?)row.Attribute("Height") && double.Parse((string)chrome.Attribute("ResizeBorderThickness")!, CultureInfo.InvariantCulture) >= 4
            && (string?)document.Root!.Attribute("ResizeMode") == "CanResize" && (string?)chrome.Attribute("UseAeroCaptionButtons") == "False",
            "#143: WindowChrome com a área de título da barra, borda de redimensionamento e botões próprios");
        var buttons = document.Descendants(Presentation + "Button").ToArray();
        string Name(XElement button) => (string?)button.Attribute(Xaml + "Name") ?? "";
        assert(new[] { "MinimizeButton", "MaximizeButton", "CloseButton" }.All(name => buttons.Any(button => Name(button) == name && ((string?)button.Attribute("Style"))?.Contains("Chrome") == true))
            && xaml.Contains("<Setter Property=\"WindowChrome.IsHitTestVisibleInChrome\" Value=\"True\"/>")
            && buttons.Where(button => Name(button) is "PaletteButton" or "ThemeButton").All(button => (string?)button.Attribute("WindowChrome.IsHitTestVisibleInChrome") == "True"),
            "#143: minimizar, maximizar, fechar, busca e tema recebem o clique dentro da área de título");
        var unnamed = buttons.Where(button => button.Attribute("AutomationProperties.Name") == null && button.Attribute("Content") == null).Select(Name).ToArray();
        assert(unnamed.Length == 0, "#143: todo botão da janela tem nome para o leitor de tela" + (unnamed.Length > 0 ? ": " + string.Join(", ", unnamed) : ""));
        assert(shell.Contains("WmNcHitTest = 0x0084") && shell.Contains("HtMaxButton = 9") && Regex.IsMatch(shell, @"case WmNcHitTest:.*?return new IntPtr\(HtMaxButton\);", RegexOptions.Singleline)
            && Regex.IsMatch(shell, @"case WmNcLButtonUp when wParam\.ToInt64\(\) == HtMaxButton:.*?ToggleMaximize\(\);", RegexOptions.Singleline)
            && shell.Contains("OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)") && shell.Contains("if (!SnapLayoutsSupported) return;"),
            "#143: snap layouts só no Windows 11: HTMAXBUTTON sobre o maximizar e o clique tratado pela área não cliente");
        assert(shell.Contains("((Grid)Content).Margin = maximized ? SystemParameters.WindowResizeBorderThickness") && shell.Contains("SystemCommands.MinimizeWindow(this)")
            && shell.Contains("SystemCommands.CloseWindow(this)") && shell.Contains("SystemCommands.RestoreWindow(this)"),
            "#143: maximizar não corta a borda; fechar passa pelo fechamento normal (bandeja e operações em andamento)");

        // Movimento: uma só animação, desligada quando o Windows pede menos movimento.
        var code = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ThemeSources", "Code"), "*.cs").ToDictionary(file => Path.GetFileName(file), File.ReadAllText);
        var animated = code.Where(file => Regex.IsMatch(file.Value, @"new (Double|Color|Thickness)Animation|Storyboard")).Select(file => file.Key).ToArray();
        var animate = Regex.Match(shell, @"private static void Animate\(.*?\n    \}", RegexOptions.Singleline).Value;
        assert(animated.SequenceEqual(["WindowShell.cs"]) && Regex.Matches(shell, @"BeginAnimation\(property, new").Count == 1
            && animate.IndexOf("!SystemParameters.ClientAreaAnimation) return;", StringComparison.Ordinal) is > 0 and var guard && guard < animate.IndexOf("new DoubleAnimation", StringComparison.Ordinal),
            "#143: animações só pelo Animate, que pula o movimento com SystemParameters.ClientAreaAnimation desligado");
        assert(app.Contains("<Trigger Property=\"IsKeyboardFocused\" Value=\"True\"><Setter TargetName=\"Surface\" Property=\"BorderBrush\" Value=\"{DynamicResource Accent}\"/>")
            && app.Contains("<Trigger Property=\"IsKeyboardFocusWithin\" Value=\"True\"><Setter TargetName=\"Chrome\" Property=\"BorderBrush\" Value=\"{DynamicResource Accent}\"/>")
            && !xaml.Contains("FocusVisualStyle=\"{x:Null}\"") && !code.Values.Any(text => text.Contains("FocusVisualStyle = null")),
            "#143: foco visível nos botões e campos (borda de acento), sem desligar o estilo de foco");

        // Nenhum texto abaixo de 11 px em todo o XAML e em todo o código da interface.
        var sizes = new List<(string File, double Size)>();
        foreach (var (file, text) in code.Append(new("MainWindow.xaml", xaml)).Append(new("App.xaml", app)))
        {
            foreach (Match match in Regex.Matches(text, @"FontSize(?:=""|\s*=\s*|"" Value="")([\d.]+)")) sizes.Add((file!, double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
            foreach (Match match in Regex.Matches(text, @"\bText\([^;\n]*?, ([\d.]+)(?:, ""\w+"")?\)")) sizes.Add((file!, double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
        }
        var small = sizes.Where(item => item.Size < 11).Select(item => item.File + " " + item.Size).Distinct().ToArray();
        assert(sizes.Count > 100 && sizes.Any(item => item.File == "HomePage.cs") && sizes.Any(item => item.File == "CommandPalette.cs") && small.Length == 0,
            "#143: nenhum texto abaixo de 11 px no XAML e em todo o código da interface" + (small.Length > 0 ? ": " + string.Join(", ", small) : ""));

        // Preferência da barra lateral no data.json; um arquivo antigo abre com ela expandida.
        var root = Path.Combine(Path.GetTempPath(), "NexoSidebar-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(Path.Combine(root, "a"), directAccess: true);
            assert(!store.Data.SidebarCollapsed, "#143: a barra lateral começa expandida");
            store.Data.SidebarCollapsed = true; store.Save();
            assert(new Store(Path.Combine(root, "a"), directAccess: true).Data.SidebarCollapsed, "#143: a barra lateral recolhida continua recolhida ao reabrir");
            var legacy = Path.Combine(root, "b"); Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "data.json"), JsonSerializer.Serialize(new { Model = "m", AiEnabled = true, AgentVersion = 2, Theme = "glacial" }));
            var old = new Store(legacy, directAccess: true);
            assert(!old.Data.SidebarCollapsed && old.Data.Theme == ThemeCatalog.Glacial, "#143: um data.json sem a preferência abre com a lateral expandida e mantém o tema");
            old.Data.SidebarCollapsed = true; old.Save();
            var saved = new Store(legacy, directAccess: true).Data;
            assert(saved.SidebarCollapsed && saved.Model == "m" && saved.AiEnabled && saved.AgentVersion == 2 && saved.Theme == ThemeCatalog.Glacial,
                "#143: gravar a lateral num data.json antigo mantém o modelo, o agente e o tema");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        // A restauração de rede da abertura (#128) escreve o resultado na conversa: com a Início de abertura, o Assistente é aberto.
        var recovery = Regex.Match(Source("MainWindow.xaml.cs"), @"private async Task OfferNetworkRecovery\(\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        assert(Regex.IsMatch(recovery, @"AddEntry\(""assistant"", ""Encontrei uma alteração de rede.*?posted = true;", RegexOptions.Singleline)
            && Regex.IsMatch(recovery, @"busy = false;\s*//[^\n]*\n\s*if \(posted && page != ""chat""\) ShowPage\(""chat""\);"),
            "#143: o resultado da restauração de rede da abertura aparece no Assistente, não fica escondido atrás da Início");
        assert(shell.Contains("store.Data.SidebarCollapsed = collapsed;") && shell.Contains("catch { store.Data.SidebarCollapsed = stored;"),
            "#143: a lateral grava a preferência e desfaz a troca se a gravação falhar");
    }

    private static string Source(string name)
    {
        var sources = Path.Combine(AppContext.BaseDirectory, "ThemeSources");
        var direct = Path.Combine(sources, name);
        return File.ReadAllText(File.Exists(direct) ? direct : Path.Combine(sources, "Code", name));
    }
}
