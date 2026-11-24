using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Nexo.Support;

/// <summary>
/// Tela Início (#143, etapa 3), a de abertura: estado do computador pelo monitor de saúde, o cartão "Agora" da #141, os atalhos
/// "Resolver rápido", as ocorrências de hoje e os atendimentos recentes. Só dados reais (HomeData); sem dado, estado de vazio,
/// carregando ou indisponível.
/// </summary>
public partial class MainWindow
{
    private void Home()
    {
        var kicker = Text(HomeData.DateLine(DateTime.Now).ToUpperInvariant(), 11, "Accent");
        kicker.FontWeight = FontWeights.SemiBold;
        PageContent.Children.Add(kicker);
        PageContent.Children.Add(new TextBlock { Text = HomeData.Greeting(user.Name), FontFamily = Font("DisplayFont"), FontWeight = FontWeights.SemiBold,
            FontSize = 34, Foreground = Brush("Ink"), Margin = new Thickness(0, 6, 0, 20), TextWrapping = TextWrapping.Wrap });

        var health = HomeData.Health(healthStore, healthError, healthSettings.Thresholds(), DateTimeOffset.UtcNow);
        PageContent.Children.Add(HomeHealthCard(health));
        LiveMetricsSection();

        PageContent.Children.Add(HomeSectionHeader("Resolver rápido", "Abre o Assistente com o pedido escrito. Você revisa e envia.", null, null));
        // Seis por linha quando cada atalho tem ao menos 140 px; na janela pequena, três.
        var fixes = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -12, 6) };
        fixes.SizeChanged += (_, e) => fixes.Columns = e.NewSize.Width >= 6 * (140 + 12) ? 6 : 3;
        foreach (var fix in HomeData.QuickFixes) fixes.Children.Add(QuickFixTile(fix));
        PageContent.Children.Add(fixes);

        var columns = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var occurrences = OccurrencesCard(health); var recent = RecentCard();
        Grid.SetColumn(recent, 2);
        columns.Children.Add(occurrences); columns.Children.Add(recent);
        PageContent.Children.Add(columns);
    }

    private Border HomeHealthCard(HomeHealth health)
    {
        var (tone, badge) = health.State switch
        {
            HomeHealthState.Unavailable => ("Warning", "Indisponível"),
            HomeHealthState.Loading => ("Secondary", "Carregando"),
            HomeHealthState.Critical => ("Danger", "Monitorando"),
            HomeHealthState.Attention => ("Warning", "Monitorando"),
            _ => ("Success", "Monitorando")
        };
        var panel = new StackPanel();
        var top = new DockPanel();
        var state = new Border { Background = Brush("SurfaceMuted"), CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3, 9, 3), VerticalAlignment = VerticalAlignment.Center };
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal };
        stateRow.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Brush(tone), VerticalAlignment = VerticalAlignment.Center });
        stateRow.Children.Add(new TextBlock { Text = badge, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush(tone),
            Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        state.Child = stateRow;
        DockPanel.SetDock(state, Dock.Right); top.Children.Add(state);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(UiIcons.Create("health", 17));
        title.Children.Add(new TextBlock { Text = "ESTADO DO COMPUTADOR", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush("Subtle"),
            Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(title);
        panel.Children.Add(top);
        panel.Children.Add(new TextBlock { Text = health.Headline, FontFamily = Font("DisplayFont"), FontWeight = FontWeights.SemiBold, FontSize = 28,
            Foreground = Brush(health.State switch { HomeHealthState.Critical => "Danger", HomeHealthState.Attention or HomeHealthState.Unavailable => "Warning", _ => "Ink" }),
            Margin = new Thickness(0, 12, 0, 4), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Text(health.Detail, 13, "Secondary"));
        // #142: a temperatura do leitor de sensores do sistema entra já na primeira renderização da Início (a tela de abertura),
        // pelo live.json lido agora, sem esperar o monitor de saúde nem o 1º tique; sem arquivo fresco, "aguardando o leitor…".
        var live = SystemSensorReader.Current();
        liveTemperatureState = live.State;
        if (SystemSensorReader.TemperatureText(health.Latest, live, PawnIoDriver.Installed()) is { } heat)
        {
            var temperature = Text(heat.Text, 12, heat.Tone is "Warning" or "Success" ? heat.Tone : "Secondary");
            temperature.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(temperature);
            if (live.State != "off") liveTemperatureViews.Add(temperature);
        }
        if (health.Latest is { } latest)
        {
            var storage = Text("Discos físicos: " + HealthReportPresentation.DescribeStorage(latest), 12, latest.StorageHealth is "warning" or "unhealthy" ? "Warning" : "Secondary");
            storage.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(storage);
        }
        if (healthMonitor?.LastError is { } monitorError)
        {
            var failure = Text("Monitoramento: " + monitorError, 12, "Warning"); failure.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(failure);
        }
        var open = Action("Ver saúde do PC", (_, _) => Guard(() => ShowPage("health")), primary: true, icon: "health");
        open.Tag = "home:health"; open.Margin = new Thickness(0, 16, 0, 0);
        panel.Children.Add(open);
        var card = Card(panel);
        AutomationProperties.SetName(card, "Estado do computador: " + health.Headline);
        return card;
    }

    private static DockPanel HomeSectionHeader(string title, string? description, string? actionLabel, Button? action)
    {
        var header = new DockPanel { Margin = new Thickness(0, 10, 0, 12) };
        if (action != null) { action.Content = actionLabel; DockPanel.SetDock(action, Dock.Right); header.Children.Add(action); }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontFamily = Font("DisplayFont"), FontWeight = FontWeights.SemiBold, FontSize = 17,
            Foreground = Brush("Ink"), TextWrapping = TextWrapping.Wrap });
        if (description != null) { var line = Text(description, 12, "Secondary"); line.Margin = new Thickness(0, 2, 0, 0); texts.Children.Add(line); }
        header.Children.Add(texts);
        return header;
    }

    private Button QuickFixTile(HomeQuickFix fix)
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Como no protótipo: ícone em cima à esquerda, número à direita, título e subtítulo embaixo.
        var glyph = UiIcons.Create(fix.Icon, 18); glyph.HorizontalAlignment = HorizontalAlignment.Left; glyph.VerticalAlignment = VerticalAlignment.Top;
        content.Children.Add(glyph);
        var number = new TextBlock { Text = fix.Number.ToString("00"), FontFamily = Font("MonoFont"), FontSize = 11, Foreground = Brush("Subtle"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        content.Children.Add(number);
        var name = Text(fix.Title, 13); name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0, 12, 0, 0); Grid.SetRow(name, 1); content.Children.Add(name);
        var sub = Text(fix.Subtitle, 11.5, "Secondary"); sub.Margin = new Thickness(0, 3, 0, 0); Grid.SetRow(sub, 2); content.Children.Add(sub);
        var button = new Button { Content = content, Tag = "home:fix:" + fix.Number, Style = (Style)FindResource("QuickTile"), Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 12, 12), MinHeight = 84,
            ToolTip = "Escreve no Assistente: \"" + fix.Prompt + "\". Tecla " + fix.Number + "." };
        AutomationProperties.SetName(button, fix.Number + ". " + fix.Title + ": escreve \"" + fix.Prompt + "\" no Assistente, sem enviar");
        // Preenche o compositor e para: o envio continua sendo do usuário.
        button.Click += (_, _) => Guard(() => FillAssistant(fix.Prompt));
        return button;
    }

    private Border OccurrencesCard(HomeHealth health)
    {
        var panel = new StackPanel();
        var all = new Button { Tag = "home:health-all", Style = (Style)FindResource("NavigationButton"), Padding = new Thickness(10, 6, 10, 6), FontSize = 12 };
        all.Click += (_, _) => Guard(() => ShowPage("health"));
        AutomationProperties.SetName(all, "Ver todas as ocorrências na Saúde do PC");
        var header = HomeSectionHeader("Ocorrências de hoje", null, "Ver tudo", all); header.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(header);
        if (health.State == HomeHealthState.Unavailable) panel.Children.Add(EmptyLine("Indisponível: " + health.Detail, "Warning"));
        else if (health.Today.Count == 0)
            panel.Children.Add(EmptyLine(health.State == HomeHealthState.Loading ? "Carregando: aguardando a primeira leitura do monitor." : "Nenhuma ocorrência registrada hoje.", "Secondary"));
        foreach (var (finding, status) in health.Today.Take(5))
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var tone = status.State switch
            {
                OccurrenceState.Persistent => finding.Severity == "critical" ? "Danger" : "Warning",
                OccurrenceState.Resolved => "Success",
                _ => "Secondary"
            };
            var time = new TextBlock { Text = finding.Time.ToLocalTime().ToString("HH:mm"), FontFamily = Font("MonoFont"), FontSize = 12, Foreground = Brush("Subtle"),
                Margin = new Thickness(10, 1, 0, 0) };
            DockPanel.SetDock(time, Dock.Right); row.Children.Add(time);
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Brush(tone), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 10, 0) };
            DockPanel.SetDock(dot, Dock.Left); row.Children.Add(dot);
            var texts = new StackPanel();
            var problem = Text(finding.Problem, 13); problem.FontWeight = FontWeights.SemiBold; texts.Children.Add(problem);
            texts.Children.Add(Text(status.Label, 12, tone));
            row.Children.Add(texts);
            AutomationProperties.SetName(row, finding.Problem + ", " + status.Label);
            panel.Children.Add(row);
        }
        if (health.Today.Count > 5) panel.Children.Add(Text("Mais " + (health.Today.Count - 5) + " na Saúde do PC.", 12, "Secondary"));
        return Card(panel);
    }

    private Border RecentCard()
    {
        var panel = new StackPanel();
        var all = new Button { Tag = "home:history", Style = (Style)FindResource("NavigationButton"), Padding = new Thickness(10, 6, 10, 6), FontSize = 12 };
        all.Click += (_, _) => Guard(() => ShowPage("history"));
        AutomationProperties.SetName(all, "Ver todos os atendimentos");
        var header = HomeSectionHeader("Atendimentos recentes", null, "Histórico", all); header.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(header);
        var items = HomeData.RecentConversations(store.Data, user.Id);
        if (items.Count == 0) panel.Children.Add(EmptyLine("Nenhum atendimento ainda. Use um atalho acima ou descreva o problema no Assistente.", "Secondary"));
        var index = 0;
        foreach (var item in items)
        {
            index++;
            var row = new DockPanel();
            var number = new TextBlock { Text = index.ToString("00"), FontFamily = Font("MonoFont"), FontSize = 12, Foreground = Brush("Accent"), Margin = new Thickness(0, 2, 12, 0),
                VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(number, Dock.Left); row.Children.Add(number);
            var tone = item.Status switch { "Resolvido" => "Success", "Encaminhamento preparado" => "Warning", _ => "Secondary" };
            var texts = new StackPanel();
            var title = new TextBlock { Text = item.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("Ink"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            texts.Children.Add(title);
            texts.Children.Add(Text(item.Created.ToString("g") + "  ·  " + item.Status, 12, tone));
            row.Children.Add(texts);
            var open = new Button { Content = row, Tag = "home:conversation:" + item.Id, Style = (Style)FindResource("NavigationButton"), Padding = new Thickness(8, 8, 8, 8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, FontWeight = FontWeights.Normal, Foreground = Brush("Ink"), ToolTip = item.Title };
            AutomationProperties.SetName(open, "Abrir atendimento: " + item.Title + ", " + item.Status);
            var conversationItem = item;
            open.Click += (_, _) => Guard(() => OpenConversation(conversationItem));
            panel.Children.Add(open);
        }
        return Card(panel);
    }

    private static TextBlock EmptyLine(string text, string tone)
    {
        var line = Text(text, 13, tone); line.Margin = new Thickness(0, 6, 0, 6); return line;
    }

    /// <summary>Mesma abertura da tela Atendimentos: mostra a conversa salva no Assistente.</summary>
    private void OpenConversation(Conversation item)
    {
        if (busy) return;
        conversation = item; conversationTools = null; Messages.Children.Clear();
        foreach (var entry in item.Entries) RenderEntry(entry);
        ProgressPanel.Visibility = Visibility.Collapsed; ProgressText.Text = ""; RefreshChatActions(); ShowPage("chat");
    }

    /// <summary>
    /// A cada leitura do monitor, Início e Saúde do PC são redesenhadas; o foco do teclado volta ao mesmo botão (pela Tag) e a
    /// rolagem fica onde estava.
    /// </summary>
    private void RefreshMonitoredPage()
    {
        if (page is not ("health" or "home") || busy) return;
        var focusedTag = (Keyboard.FocusedElement as FrameworkElement) is { Tag: string tag } focused && IsDescendantOf(focused, PageContent) ? tag : null;
        var position = PageScroll.VerticalOffset;
        ShowPage(page); PageScroll.ScrollToVerticalOffset(position);
        if (focusedTag != null) FindTagged(PageContent, focusedTag)?.Focus();
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current != null; current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static FrameworkElement? FindTagged(DependencyObject root, string tag)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is FrameworkElement { Tag: string value } element && value == tag) return element;
            if (FindTagged(child, tag) is { } found) return found;
        }
        return null;
    }
}
