using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace Nexo.Support;

/// <summary>
/// Verificações do modo --screenshot para a moldura nova (#143, etapas 2 e 3): tela Início, atalhos, paleta Ctrl+K, barra de
/// título e barra lateral. Rodam só na prévia, com o armazenamento isolado; nada é feito no Windows.
/// </summary>
public partial class MainWindow
{
    private static IEnumerable<T> VisualItems<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var nested in VisualItems<T>(child)) yield return nested;
        }
    }

    private static bool HasVector(DependencyObject root) => root is System.Windows.Shapes.Path || VisualItems<System.Windows.Shapes.Path>(root).Any();

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    /// <summary>Nenhum texto visível abaixo de 11 px (#143).</summary>
    private void CheckReadableText(string where)
    {
        UpdateLayout();
        var small = VisualItems<TextBlock>((DependencyObject)Content).Where(text => text.IsVisible && text.FontSize < 11)
            .Select(text => $"\"{text.Text}\" {text.FontSize:F1}px").Concat(VisualItems<Control>((DependencyObject)Content)
            .Where(control => control.IsVisible && control.FontSize < 11).Select(control => control.GetType().Name + " " + control.FontSize.ToString("F1") + "px")).Take(5).ToArray();
        Require(small.Length == 0, "Texto abaixo de 11 px em " + where + ": " + string.Join("; ", small));
    }

    /// <summary>Todo botão da moldura, da paleta e da Início tem nome para o leitor de tela.</summary>
    private void CheckAccessibleButtons(string where, params DependencyObject[] roots)
    {
        var unnamed = roots.SelectMany(VisualItems<Button>).Where(button => button.IsVisible
            && string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) && button.Content is not string { Length: > 0 }).ToArray();
        Require(unnamed.Length == 0, unnamed.Length + " botão(ões) sem nome acessível em " + where + ".");
    }

    private Button[] QuickFixButtons() => VisualItems<Button>(PageContent).Where(button => (button.Tag as string)?.StartsWith("home:fix:", StringComparison.Ordinal) == true).ToArray();

    private async Task VerifyShellPreview(Func<string, Task> capture)
    {
        // 1. Abertura na Início, com os estados de vazio (a prévia começa sem atendimento) e sem texto do protótipo.
        Require(page == "home" && PageScroll.Visibility == Visibility.Visible && ChatView.Visibility == Visibility.Collapsed, "O Nexo não abriu na tela Início.");
        UpdateLayout();
        var texts = VisualItems<TextBlock>(PageContent).Select(text => text.Text).ToArray();
        foreach (var expected in new[] { HomeData.Greeting(user.Name), "ESTADO DO COMPUTADOR", "RESOLVER RÁPIDO", "OCORRÊNCIAS DE HOJE", "ATENDIMENTOS RECENTES", "Nenhum atendimento ainda" })
            Require(texts.Any(text => text.Contains(expected)), "A tela Início não mostra: " + expected);
        Require(texts.Any(text => text.Contains("Carregando: aguardando a primeira leitura") || text.Contains("Nenhuma ocorrência registrada hoje") || text.StartsWith("Indisponível:")),
            "As ocorrências de hoje sem dados precisam do estado de carregando, vazio ou indisponível.");
        foreach (var prototype in new[] { "2.8.4", "Camila", "DESKTOP-VICTOR", "Spooler reiniciado", "NexoMemTeste", "Ryzen" })
            Require(!texts.Any(text => text.Contains(prototype)), "Texto fixo do protótipo na Início: " + prototype);
        Require(VisualItems<LiveMetricsPanel>(PageContent).Count() == 1, "A Início precisa do cartão Agora da #141.");
        var fixes = QuickFixButtons();
        Require(fixes.Length == HomeData.QuickFixes.Count && fixes.All(HasVector)
            && HomeData.QuickFixes.All(fix => VisualItems<TextBlock>(PageContent).Any(text => text.Text == fix.Number.ToString("00"))),
            "Os atalhos Resolver rápido precisam de número e ícone vetorial.");
        CheckReadableText("Início"); CheckAccessibleButtons("Início", Sidebar, TitleBar, PageContent);
        await capture("nexo-home.png");
        foreach (var (width, height, file) in new[] { (1060d, 720d, "nexo-home-small.png"), (980d, 680d, "nexo-home-compact.png") })
        {
            Width = width; Height = height; UpdateLayout();
            Require(QuickFixButtons().All(button => button.ActualHeight >= 96 && button.ActualWidth >= 150
                && button.TranslatePoint(new Point(button.ActualWidth, 0), PageScroll).X <= PageScroll.ActualWidth + 1),
                $"Atalhos da Início cortados ou pequenos em {width}x{height}.");
            await capture(file);
        }
        Width = 1380; Height = 900; UpdateLayout();

        // 2. Atalho e tecla numérica escrevem o pedido no Assistente e não enviam.
        var conversations = store.Data.Conversations.Count;
        QuickFixButtons()[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(page == "chat" && Composer.Text == HomeData.QuickFixes[0].Prompt && conversation?.Entries.Count == 0 && !busy
            && store.Data.Conversations.Count == conversations, "O atalho precisa abrir o Assistente com o pedido escrito, sem enviar.");
        Composer.Clear(); ShowPage("home");
        Require(HandleWindowKey(Key.D4, ModifierKeys.None, null) && page == "chat" && Composer.Text == HomeData.QuickFixes[3].Prompt
            && conversation?.Entries.Count == 0, "A tecla 4 na Início precisa escrever o 4º atalho sem enviar.");
        Require(!HandleWindowKey(Key.D2, ModifierKeys.None, Composer) && Composer.Text == HomeData.QuickFixes[3].Prompt, "Números digitados no Assistente não podem disparar atalhos.");
        Composer.Clear();

        // 3. Paleta Ctrl+K: abre, filtra sem acento, escolhe pelas setas, executa com Enter e fecha com Esc.
        ShowPage("home");
        Require(HandleWindowKey(Key.K, ModifierKeys.Control, null) && PaletteOpen && (paletteInput!.IsKeyboardFocused || FocusManager.GetFocusedElement(this) == paletteInput),
            "Ctrl+K precisa abrir a paleta com o foco na busca.");
        Require(paletteList!.Children.Count == CommandCatalog.Build(ThemeManager.Current, sidebarCollapsed).Count && paletteSelected == 0, "A paleta vazia lista todas as telas e ações.");
        CheckReadableText("paleta"); CheckAccessibleButtons("paleta", PaletteHost);
        await capture("nexo-palette.png");
        paletteInput!.Text = "saude";
        Require(paletteVisible.FirstOrDefault()?.Id == "page:health", "O filtro sem acento não achou a Saúde do PC.");
        HandlePaletteKey(Key.Down); HandlePaletteKey(Key.Up);
        Require(paletteSelected == 0, "As setas não voltaram ao primeiro resultado.");
        HandlePaletteKey(Key.Enter);
        Require(page == "health" && !PaletteOpen, "Enter na paleta não abriu a Saúde do PC.");
        HandleWindowKey(Key.K, ModifierKeys.Control, null); paletteInput.Text = "zzzz";
        Require(paletteVisible.Count == 0 && paletteEmpty!.Visibility == Visibility.Visible, "Busca sem resultado precisa do estado vazio.");
        HandlePaletteKey(Key.Enter);
        Require(PaletteOpen && page == "health", "Enter sem resultado não pode executar nada.");
        HandlePaletteKey(Key.Escape);
        Require(!PaletteOpen && page == "health", "Esc precisa fechar a paleta sem navegar.");
        var originalTheme = ThemeManager.Current;
        for (var round = 0; round < 2; round++)
        {
            HandleWindowKey(Key.K, ModifierKeys.Control, null); paletteInput.Text = "tema"; HandlePaletteKey(Key.Enter);
            Require(!PaletteOpen && (ThemeManager.Current != originalTheme) == (round == 0) && store.Data.Theme == ThemeManager.Current
                && ((string)ThemeButton.Content == "Glacial") == (ThemeManager.Current == ThemeCatalog.Glacial), "A ação de tema da paleta não trocou e gravou o tema.");
        }
        HandleWindowKey(Key.K, ModifierKeys.Control, null); paletteInput.Text = "nova conversa"; HandlePaletteKey(Key.Enter);
        Require(page == "chat" && conversation?.Entries.Count == 0, "A ação Novo atendimento da paleta não abriu uma conversa nova.");
        HandleWindowKey(Key.K, ModifierKeys.Control, null);
        Require(HandleWindowKey(Key.K, ModifierKeys.Control, null) && !PaletteOpen, "Ctrl+K com a paleta aberta precisa fechá-la.");

        // 4. Barra de título própria.
        ShowPage("home"); UpdateLayout();
        var chrome = WindowChrome.GetWindowChrome(this);
        Require(chrome != null && Math.Abs(chrome.CaptionHeight - Shell.RowDefinitions[0].ActualHeight) < 1 && chrome.ResizeBorderThickness.Left >= 4
            && ResizeMode == ResizeMode.CanResize, "WindowChrome sem área de título ou sem borda de redimensionamento.");
        foreach (var width in new[] { 1380d, 980d })
        {
            Width = width; UpdateLayout();
            var root = (FrameworkElement)Content;
            double Left(FrameworkElement element) => element.TranslatePoint(new Point(), root).X;
            double Right(FrameworkElement element) => element.TranslatePoint(new Point(element.ActualWidth, 0), root).X;
            var buttons = new[] { MinimizeButton, MaximizeButton, CloseButton };
            Require(buttons.All(button => button.ActualWidth >= 44 && button.ActualHeight >= 40 && WindowChrome.GetIsHitTestVisibleInChrome(button)
                && button.TranslatePoint(new Point(), root).Y < 2 && !string.IsNullOrEmpty(AutomationProperties.GetName(button)) && HasVector(button))
                && Math.Abs(Right(CloseButton) - root.ActualWidth) <= 2 && Right(MaximizeButton) <= Left(CloseButton) + 1 && Right(MinimizeButton) <= Left(MaximizeButton) + 1,
                $"Botões da janela fora da posição ou pequenos em {width} px.");
            Require(WindowChrome.GetIsHitTestVisibleInChrome(PaletteButton) && WindowChrome.GetIsHitTestVisibleInChrome(ThemeButton)
                && Right(PaletteButton) <= Left(ThemeButton) && Right(ThemeButton) <= Left(MinimizeButton) && Left(PaletteButton) >= Right(Sidebar),
                $"Busca e tema sobrepostos na barra de título em {width} px.");
        }
        Width = 1380; UpdateLayout();
        // O teste de HTMAXBUTTON usa a mesma função do gancho: sobre o maximizar responde 9 (snap layouts); sobre o fechar, não.
        IntPtr ScreenPoint(FrameworkElement element)
        {
            var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            return new IntPtr(((int)point.Y << 16) | ((int)point.X & 0xFFFF));
        }
        var hit = false; var maximize = SnapLayoutHook(IntPtr.Zero, WmNcHitTest, IntPtr.Zero, ScreenPoint(MaximizeButton), ref hit);
        var miss = false; var close = SnapLayoutHook(IntPtr.Zero, WmNcHitTest, IntPtr.Zero, ScreenPoint(CloseButton), ref miss);
        SetMaximizeHot(false);
        Require(hit && maximize == new IntPtr(HtMaxButton) && !miss && close == IntPtr.Zero, "O teste de área do maximizar (snap layouts) não reconheceu o botão.");
        ToggleMaximize(); await Task.Delay(150); UpdateLayout();
        Require(WindowState == WindowState.Maximized && AutomationProperties.GetName(MaximizeButton) == "Restaurar" && ((Grid)Content).Margin == SystemParameters.WindowResizeBorderThickness,
            "Maximizar não ajustou a janela e o botão Restaurar.");
        await capture("nexo-home-maximized.png");
        ToggleMaximize(); await Task.Delay(150);
        Require(WindowState == WindowState.Normal && AutomationProperties.GetName(MaximizeButton) == "Maximizar" && ((Grid)Content).Margin == new Thickness(0),
            "Restaurar não devolveu a janela ao tamanho anterior.");
        Width = 1380; Height = 900; UpdateLayout();

        // 5. Barra lateral recolhida: só ícones, com nome e dica; a preferência da prévia não é gravada.
        var storedSidebar = store.Data.SidebarCollapsed;
        SetSidebar(true, persist: false); await Task.Delay(300); Sidebar.BeginAnimation(WidthProperty, null); UpdateLayout();
        var navButtons = Navigation.Children.OfType<Button>().Where(button => button.IsVisible).ToArray();
        Require(Sidebar.ActualWidth <= SidebarCollapsedWidth + 1 && navButtons.All(button => HasVector(button) && button.ActualWidth <= SidebarCollapsedWidth
            && ((StackPanel)button.Content).Children.OfType<TextBlock>().All(label => !label.IsVisible) && button.ToolTip is string { Length: > 0 }
            && !string.IsNullOrEmpty(AutomationProperties.GetName(button))) && !NewChatLabel.IsVisible && !SidebarWordmark.IsVisible
            && AutomationProperties.GetName(SidebarToggle) == "Expandir a barra lateral" && store.Data.SidebarCollapsed == storedSidebar,
            "A barra lateral recolhida precisa mostrar só os ícones, com nome acessível.");
        CheckReadableText("lateral recolhida"); CheckAccessibleButtons("lateral recolhida", Sidebar);
        await capture("nexo-sidebar-collapsed.png");
        Width = 980; Height = 680; UpdateLayout(); await capture("nexo-sidebar-collapsed-compact.png");
        Width = 1380; Height = 900;
        SetSidebar(false, persist: false); await Task.Delay(300); Sidebar.BeginAnimation(WidthProperty, null); UpdateLayout();
        Require(Sidebar.ActualWidth >= SidebarExpandedWidth - 1 && NewChatLabel.IsVisible && AutomationProperties.GetName(SidebarToggle) == "Recolher a barra lateral",
            "A barra lateral não voltou a expandir.");

        // 6. Nenhum texto abaixo de 11 px e botões da moldura com nome em todas as telas.
        foreach (var target in new[] { "home", "chat", "health", "history", "tickets", "tools", "settings", "audit" })
        {
            ShowPage(target); CheckReadableText(target); CheckAccessibleButtons(target, Sidebar, TitleBar);
        }
        ShowPage("home");
    }

    /// <summary>Início com dados reais: as leituras e ocorrências gravadas pela prévia e o atendimento já enviado.</summary>
    private async Task VerifyHomeData(Func<string, Task> capture)
    {
        ShowPage("home"); UpdateLayout();
        var texts = VisualItems<TextBlock>(PageContent).Select(text => text.Text).ToArray();
        var now = DateTimeOffset.UtcNow;
        if (healthStore != null)
        {
            var latest = healthStore.RecentSnapshots(now.AddDays(-1), 1).FirstOrDefault();
            Require(latest != null && texts.Any(text => text.StartsWith("Última leitura às " + latest.Time.ToLocalTime().ToString("HH:mm"), StringComparison.Ordinal)),
                "A Início não mostra a última leitura do monitor.");
            foreach (var (finding, status) in HomeData.Today(healthStore, latest, healthSettings.Thresholds(), now).Take(5))
                Require(texts.Contains(finding.Problem) && texts.Contains(status.Label), "A Início não mostra a ocorrência de hoje: " + finding.Problem);
        }
        var recent = HomeData.RecentConversations(store.Data, user.Id);
        Require(recent.Count > 0 && recent.All(item => texts.Contains(item.Title)) && !texts.Any(text => text.Contains("Nenhum atendimento ainda")),
            "A Início não mostra os atendimentos recentes.");
        await capture("nexo-home-data.png");
        var first = VisualItems<Button>(PageContent).First(button => button.Tag as string == "home:conversation:" + recent[0].Id);
        first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(page == "chat" && ReferenceEquals(conversation, recent[0]), "O atendimento recente não abriu no Assistente.");
    }
}
