using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nexo.Support;
public partial class MainWindow : Window
{
    private readonly Store store;
    private readonly SupportService support;
    private Account user = null!;
    private Conversation? conversation;
    private CancellationTokenSource? operation;
    private bool busy;
    // #128: a janela "Restaurar configuração de rede anterior?" aberta, e o fechamento do Nexo pedido enquanto ela estava aberta.
    private Window? postponableDialog;
    private bool closeAfterPrompt;
    private string page = "chat";
    private WindowsTools? conversationTools;
    private WrapPanel? quickReplyPanel;
    private OperationalStore? healthStore;
    private MonitoringEngine? healthMonitor;
    private HealthSettings healthSettings = new();
    // #141: uma instância só, devolvida à tela Saúde a cada reconstrução (o monitor recria a tela a cada leitura).
    private LiveMetricsPanel? liveMetrics;
    private string? healthError;
    private List<string> eventWarnings = [];
    private DateTimeOffset lastEventScan = DateTimeOffset.MinValue;
    private DateTimeOffset lastPrune = DateTimeOffset.MinValue;
    private int eventScanRunning;
    private System.Windows.Forms.NotifyIcon? trayIcon;
    private System.Drawing.Icon? trayImage;
    private bool exitRequested;
    private CancellationTokenSource? officeCancellation;
    private CancellationTokenSource? optimizationCancellation;
    private bool officeRunning;
    private bool optimizationRunning;
    private string officeStatus = "Nenhuma instalação iniciada nesta sessão.";
    private string optimizationStatus = "Nenhuma otimização iniciada nesta sessão.";
    private TextBlock? officeProgressView;
    private TextBlock? optimizationProgressView;
    private Button? officeInstallView;
    private Button? officeCancelView;
    private Button? optimizationStartView;
    private Button? optimizationCancelView;
    private readonly OfficeDeploymentService officeDeployment = new(new WindowsOfficeDeployment());
    private readonly object optimizationAuditSync = new();
    private CancellationTokenSource? debloatCancellation;
    private bool debloatRunning;
    private bool debloatCanUndo;
    private string debloatStatus = "Nenhuma verificação iniciada nesta sessão.";
    private DebloatPreview? debloatPreview;
    private readonly Dictionary<string, CheckBox> debloatChecks = new();
    private CheckBox? debloatRiskView;
    private StackPanel? debloatListView;
    private TextBlock? debloatProgressView;
    private Button? debloatScanView;
    private Button? debloatApplyView;
    private Button? debloatUndoView;
    private Button? debloatCancelView;
    private readonly object debloatAuditSync = new();
    // Componentes do Windows para programas (#150).
    private CancellationTokenSource? runtimeCancellation;
    private bool runtimeRunning;
    private string runtimeStatus = "Nenhuma verificação iniciada nesta sessão.";
    private RuntimeScan? runtimeScan;
    private readonly Dictionary<string, CheckBox> runtimeChecks = new();
    private StackPanel? runtimeListView;
    private TextBlock? runtimeProgressView;
    private Button? runtimeScanView;
    private Button? runtimeInstallView;
    private Button? runtimeCancelView;
    private readonly object runtimeAuditSync = new();
    public MainWindow(Store store)
    {
        InitializeComponent(); Title = "Nexo " + AppVersion.Current + " • Suporte inteligente"; this.store = store; support = new SupportService(store);
        ApplyNavigationIcons();
        InitializeShell();
        user = store.GetLocalSession();
        RefreshIdentity();
        NewConversation();
        try { healthSettings = HealthSettings.Load(store.Root); }
        catch (Exception ex) { healthError = "Preferências inválidas; usando valores padrão. " + ex.Message; }
        try { healthStore = new OperationalStore(store.Root); }
        catch (Exception ex) { healthError = "Histórico operacional indisponível. " + ex.Message; }
        // #143: a tela Início é a de abertura; ela lê o histórico operacional, por isso vem depois de abri-lo.
        ShowPage("home");
        Loaded += (_, _) => StartHealthMonitoring();
        Loaded += async (_, _) => await OfferNetworkRecovery();
        Closed += async (_, _) =>
        {
            DisposeTrayIcon();
            try { if (healthMonitor != null) { await healthMonitor.StopAsync(); healthMonitor.Dispose(); } }
            finally { healthStore?.Dispose(); Application.Current.Shutdown(); }
        };
        Closing += (_, e) =>
        {
            // #128: fechar o Nexo com a oferta de restauração aberta vale "Agora não": a janela é fechada sem escolha, nada é
            // restaurado nem gravado como recusa, e o Nexo fecha assim que a etapa em andamento terminar.
            if (postponableDialog is { } pending)
            {
                e.Cancel = true;
                closeAfterPrompt = true;
                operation?.Cancel();
                pending.Close();
                return;
            }
            if (officeRunning || optimizationRunning || debloatRunning || runtimeRunning)
            {
                e.Cancel = true;
                exitRequested = false;
                MessageBox.Show(this, "Aguarde a etapa em andamento. Use Cancelar em Ferramentas para interromper a preparação; uma instalação ou manutenção já iniciada precisa terminar antes de encerrar o Nexo.", "Ferramentas em andamento");
                return;
            }
            if (busy)
            {
                e.Cancel = true;
                exitRequested = false;
                var message = operation?.IsCancellationRequested == true
                    ? "O cancelamento foi solicitado. Aguarde o resultado das ações já iniciadas antes de fechar."
                    : "Aguarde o resultado da operação antes de fechar. Se o botão Cancelar estiver disponível, você pode solicitar a interrupção das próximas etapas.";
                MessageBox.Show(this, message, "Atendimento em andamento");
                return;
            }
            if (!exitRequested && healthSettings.BackgroundMode && trayIcon?.Visible == true)
            {
                e.Cancel = true;
                Hide();
                trayIcon.BalloonTipTitle = "Nexo em segundo plano";
                trayIcon.BalloonTipText = "O monitoramento continua. Abra o Nexo pelo ícone da bandeja.";
                trayIcon.ShowBalloonTip(2500);
            }
        };
        if (healthSettings.BackgroundMode) EnsureTrayIcon();
    }
    private void EnsureTrayIcon()
    {
        if (trayIcon != null) { trayIcon.Visible = true; return; }
        using var iconStream = typeof(MainWindow).Assembly.GetManifestResourceStream("Nexo.Tray.ico")
            ?? throw new InvalidOperationException("Ícone da bandeja indisponível.");
        trayImage = new System.Drawing.Icon(iconStream);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Nexo", null, (_, _) => Dispatcher.BeginInvoke(new Action(OpenFromTray)));
        menu.Items.Add("Encerrar Nexo", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => { exitRequested = true; Close(); })));
        trayIcon = new System.Windows.Forms.NotifyIcon { Icon = trayImage, Text = "Nexo • monitoramento local", ContextMenuStrip = menu, Visible = true };
        trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(OpenFromTray));
    }
    private void OpenFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    private void DisposeTrayIcon()
    {
        if (trayIcon == null) return;
        trayIcon.Visible = false;
        trayIcon.ContextMenuStrip?.Dispose();
        trayIcon.Dispose(); trayIcon = null;
        trayImage?.Dispose(); trayImage = null;
    }
    public async void VerifyTraySmoke()
    {
        var result = Path.Combine(AppContext.BaseDirectory, "tray-checks.txt");
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if (healthMonitor == null) throw new InvalidOperationException("O monitor não iniciou com a janela aberta.");
            healthSettings.BackgroundMode = true;
            EnsureTrayIcon();
            Close();
            if (IsVisible || trayIcon?.Visible != true || healthMonitor == null)
                throw new InvalidOperationException("Fechar não manteve o monitor na bandeja.");
            trayIcon.ContextMenuStrip?.Items[0].PerformClick();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if (!IsVisible) throw new InvalidOperationException("Abrir Nexo não restaurou a janela.");
            Close();
            if (IsVisible || trayIcon?.Visible != true || healthMonitor == null)
                throw new InvalidOperationException("Fechar pela segunda vez não manteve o monitor na bandeja.");
            trayIcon.ContextMenuStrip?.Items[0].PerformClick();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if (!IsVisible) throw new InvalidOperationException("A segunda abertura não restaurou a janela.");
            trayIcon.ContextMenuStrip?.Items[1].PerformClick();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if (IsVisible || trayIcon != null) throw new InvalidOperationException("Encerrar Nexo não liberou janela e bandeja.");
            File.WriteAllText(result, "Bandeja: dois fechamentos ocultaram a janela e mantiveram o monitor; duas aberturas restauraram; encerrar liberou o ícone.");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            File.WriteAllText(result, "Falha na bandeja: " + ex);
            Application.Current.Shutdown(1);
        }
    }
    private static TextBlock Text(string text, double size = 14, string color = "Ink") => new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };
    // #143: datas, horas, IDs e números em IBM Plex Mono.
    private static TextBlock Mono(string text, double size = 13, string color = "Ink") { var block = Text(text, size, color); block.FontFamily = Font("MonoFont"); return block; }
    // Linha de leitura do monitor: marcador na cor do estado e o texto de hoje, inteiro (as verificações da prévia procuram por ele).
    private static Grid StatusLine(string text, string tone, double indent = 0)
    {
        var line = new Grid { Margin = new Thickness(indent, 7, 0, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brush(tone switch { "Warning" => "Warning", "Success" => "Success", "Danger" => "Danger", _ => "Subtle" }),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) });
        var content = Text(text, 13, tone is "Warning" or "Danger" ? tone : "Secondary"); Grid.SetColumn(content, 1); line.Children.Add(content);
        return line;
    }
    private static string MetricPercent(double? value) => value.HasValue ? value.Value.ToString("F0") + "%" : "indisponível";
    // Chaves da paleta vêm do ThemeManager (#143): o pincel acompanha a troca de tema sem reconstruir a tela.
    private static Brush Brush(string color) => color.StartsWith('#') ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) : ThemeManager.Live(color);
    private static FontFamily Font(string key) => (FontFamily)Application.Current.FindResource(key);
    private void ApplyNavigationIcons()
    {
        foreach (var button in Navigation.Children.OfType<Button>())
        {
            if (button.Tag is not string section || button.Content is not StackPanel previous) continue;
            var label = previous.Children.OfType<TextBlock>().Last().Text;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(UiIcons.Create(section switch { "home" => "home", "chat" => "chat", "health" => "health", "history" => "history", "tickets" => "tickets", "tools" => "tools", "users" => "users", "settings" => "settings", "audit" => "audit", _ => "brand" }, 19));
            row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            button.Content = row;
            button.ToolTip = label;
            button.Style = (Style)FindResource("NavigationButton");
        }
    }
    private string OfflineHealthReport()
    {
        try { using var local = new OperationalStore(store.Root); return HealthReportPresentation.FromStore(local, healthSettings.Thresholds()); }
        catch (Exception ex) { return "O relatório de saúde local também está indisponível: " + ex.Message + ". O histórico de conversas permanece separado."; }
    }
    private string? interruptedFacts;
    private string InterruptedFacts() { var line = interruptedFacts; interruptedFacts = null; return line == null ? "" : "\n\n" + line; }
    private static string AnswerPresentation(string answer)
    {
        var normalized = AgentProtocol.Normalize(TurnFacts.Body(answer));
        return normalized.Contains("nao consegui") || normalized.Contains("nao conseguiu selecionar") || normalized.Contains("resposta invalida")
            || normalized.Contains("limite de etapas") || normalized.Contains("interrompi as acoes")
            || normalized.Contains("nao foi concluida") || normalized.Contains("nao foi confirmada")
            || normalized.Contains("formato invalido") || normalized.Contains("voce recusou")
            ? "error" : "result";
    }
    // #143: primário dourado ou secundário com contorno (estilos Primary e SecondaryAction do App.xaml), 36 px de altura e ícone opcional
    // à esquerda do texto. Com ícone, o nome acessível continua sendo o texto do botão.
    private Button Action(string label, RoutedEventHandler handler, bool primary = false, string? icon = null)
    {
        var b = new Button { Content = label, Margin = new Thickness(0, 0, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 36, Style = (Style)FindResource(primary ? "Primary" : "SecondaryAction") };
        if (icon != null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var glyph = UiIcons.Create(icon, 16, ThemeManager.Live(primary ? "OnAccent" : "Accent")); glyph.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(glyph);
            row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            b.Content = row; AutomationProperties.SetName(b, label);
        }
        b.Click += handler; return b;
    }
    private Border Card(UIElement content) => new() { Style = (Style)FindResource("Card"), Child = content, Margin = new Thickness(0, 0, 0, 14) };
    private static StackPanel SectionHeading(string icon, string title)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 12) };
        row.Children.Add(UiIcons.Create(icon, 19));
        row.Children.Add(new TextBlock { Text = title, Foreground = Brush("Ink"), FontSize = 21, FontFamily = Font("DisplayFont"),
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
    // #143: números em IBM Plex Mono; um estado em texto ("Interface ativa") usa a fonte do corpo numa linha só, sem quebrar em 1060x720.
    private static Border MetricTile(string label, string value, string icon, bool numeric = true)
    {
        var panel = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(UiIcons.Create(icon, 16));
        top.Children.Add(new TextBlock { Text = label, Foreground = Brush("Secondary"), FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(top);
        var valueText = new TextBlock { Text = value, Foreground = Brush("Ink"), FontWeight = FontWeights.Medium, Margin = new Thickness(0, 9, 0, 0),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value };
        if (numeric) { valueText.FontFamily = Font("MonoFont"); valueText.FontSize = 26; valueText.Foreground = Brush("Accent"); }
        else valueText.FontSize = 19;
        panel.Children.Add(valueText);
        AutomationProperties.SetName(panel, label + ": " + value);
        return new Border { Background = Brush("SurfaceMuted"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16), Margin = new Thickness(0, 0, 10, 0), Child = panel };
    }
    // Estado vazio (#143): ícone no acento, título em Barlow e a explicação de hoje.
    private Border EmptyState(string icon, string title, string description)
    {
        var content = new StackPanel { Margin = new Thickness(0, 7, 0, 7) };
        content.Children.Add(new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(8), BorderBrush = Brush("AccentBorder"), BorderThickness = new Thickness(1),
            Background = Brush("AccentSoft"), HorizontalAlignment = HorizontalAlignment.Left, Child = UiIcons.Create(icon, 20) });
        var heading = Text(title, 20); heading.FontWeight = FontWeights.SemiBold; heading.FontFamily = Font("DisplayFont");
        heading.Margin = new Thickness(0, 14, 0, 6); content.Children.Add(heading);
        content.Children.Add(Text(description, 14, "Secondary"));
        var card = Card(content); AutomationProperties.SetName(card, title + ". " + description);
        return card;
    }
    private void Guard(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Nexo", MessageBoxButton.OK, MessageBoxImage.Information); } }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            try { store.Log(user, "Falha de interface: " + ex.GetType().Name); } catch { /* The original failure is more useful to the user. */ }
            MessageBox.Show(this, "Não foi possível concluir esta etapa. " + ex.Message + "\n\nConfira o histórico antes de tentar novamente.", "Nexo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void RefreshIdentity()
    {
        SidebarName.Text = user.Name; SidebarRole.Text = "Sessão local do Windows";
        AdminLabel.Visibility = UsersNav.Visibility = Visibility.Collapsed;
        SettingsNav.Visibility = AuditNav.Visibility = Visibility.Visible;
        RefreshChatActions();
    }
    private void NavigateClick(object sender, RoutedEventArgs e) => Guard(() => ShowPage((string)((Button)sender).Tag));
    private void NewChatClick(object sender, RoutedEventArgs e) { if (!busy) { NewConversation(); ShowPage("chat"); } }
    private void NewConversation()
    {
        ClearQuickReplies();
        conversationTools = null;
        conversation = new Conversation { UserId = user.Id };
        Messages.Children.Clear(); Composer.Clear();
        ProgressText.Text = "";
        ProgressPanel.Visibility = Visibility.Collapsed;
        RefreshChatActions();
        var welcome = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
        var kicker = new StackPanel { Orientation = Orientation.Horizontal };
        kicker.Children.Add(UiIcons.Logo(16));
        kicker.Children.Add(new TextBlock { Text = "UM ESPAÇO PARA RESOLVER", FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("Accent"), Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        welcome.Children.Add(kicker);
        welcome.Children.Add(new TextBlock { Text = "Olá, " + user.Name.Split(' ')[0] + ".\nComo posso ajudar hoje?",
            FontFamily = Font("DisplayFont"), FontWeight = FontWeights.SemiBold, FontSize = 36, Foreground = Brush("Ink"),
            Margin = new Thickness(0, 17, 0, 12), LineHeight = 45 });
        welcome.Children.Add(Text("Escolha um assunto ou descreva o que aconteceu. Vamos entender o próximo passo juntos.", 15, "Secondary"));
        var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 30, 0, 20) };
        foreach (var (icon, title, subtitle, prompt) in new[] {
            ("camera", "Câmera", "Imagem e permissões", "Estou com problema na câmera"),
            ("network", "Internet e rede", "Conexão, IP e DNS", "Estou com problema na internet"),
            ("audio", "Áudio", "Som e microfone", "Estou com problema no áudio"),
            ("performance", "Desempenho", "Lentidão e travamentos", "Meu computador está lento"),
            ("bluetooth", "Bluetooth", "Dispositivos sem conexão", "Meu Bluetooth não está funcionando"),
            ("printer", "Impressora", "Fila e impressão", "Minha impressora não imprime") })
        {
            var content = new StackPanel();
            content.Children.Add(new Border { Width = 38, Height = 38, Background = Brush("AccentSoft"),
                CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left,
                Child = UiIcons.Create(icon, 20) });
            var name = Text(title, 14); name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0, 12, 0, 0);
            content.Children.Add(name);
            var sub = Text(subtitle, 12, "Secondary"); sub.Margin = new Thickness(0, 4, 0, 0); content.Children.Add(sub);
            var button = new Button { Content = content, Background = Brush("Surface"), Padding = new Thickness(17),
                Margin = new Thickness(0, 0, 12, 12), HorizontalContentAlignment = HorizontalAlignment.Left,
                MinHeight = 116, VerticalContentAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(button, title + ": " + subtitle);
            button.Click += async (_, _) => await GuardAsync(() => Send(prompt));
            tiles.Children.Add(button);
        }
        welcome.Children.Add(tiles);
        welcome.Children.Add(Text("Ou descreva o problema no campo abaixo.", 12, "Subtle"));
        Messages.Children.Add(welcome);
    }
    private void ShowPage(string target)
    {
        if (busy) return;
        if (target == "users") store.Require(user, a => a.Admin);
        page = target;
        ChatView.Visibility = target == "chat" ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.Visibility = target == "chat" ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in Navigation.Children.OfType<Button>())
        {
            var selected = (string?)button.Tag == target;
            button.Background = Brush(selected ? "AccentSoft" : "SidebarBg");
            button.Foreground = Brush(selected ? "Accent" : "Secondary");
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (target == "chat") return;
        PageContent.Children.Clear();
        switch (target) { case "home": Home(); break; case "profile": Profile(); break; case "users": Users(); break; case "history": History(); break; case "tickets": Tickets(); break; case "health": Health(); break; case "tools": Tools(); break; case "settings": Settings(); break; case "audit": Audits(); break; }
        PageScroll.ScrollToTop();
    }
    private void RefreshChatActions()
    {
        var available = conversation is { Status: "Em andamento" } && conversation.Entries.Count > 0;
        ChatActions.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ResolvedButton.IsEnabled = available && !busy;
        TicketButton.IsEnabled = available && !busy && user.Tickets;
    }
    // Cabeçalho das páginas (#143): linha de contexto com dado real, título em Barlow e a descrição no corpo (14 px).
    private void Heading(string title, string description, string? kicker = null)
    {
        if (kicker != null) PageContent.Children.Add(PageKit.Kicker(kicker));
        PageContent.Children.Add(new TextBlock { Text = title, FontFamily = Font("DisplayFont"),
            FontWeight = FontWeights.Bold, FontSize = 34, Foreground = Brush("Ink") });
        var subtitle = Text(description, 14, "Secondary"); subtitle.Margin = new Thickness(0, 6, 0, 24); PageContent.Children.Add(subtitle);
    }
    private void StartHealthMonitoring()
    {
        if (healthStore == null || healthMonitor != null) return;
        healthMonitor = new MonitoringEngine(new WindowsHealthSampler(healthSettings), healthStore,
            TimeSpan.FromSeconds(healthSettings.IntervalSeconds), healthSettings.Thresholds());
        healthMonitor.Updated += OnHealthUpdated;
        healthMonitor.Start();
    }
    private async void OnHealthUpdated(HealthSnapshot snapshot, IReadOnlyList<HealthFinding> findings)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (healthStore != null && now - lastPrune > TimeSpan.FromDays(1))
            {
                healthStore.Prune(TimeSpan.FromDays(healthSettings.RetentionDays)); lastPrune = now;
            }
            if (healthStore != null) PendingOperations.Reconcile(store.Root, healthStore, now);
            if (healthStore != null && now - lastEventScan > TimeSpan.FromMinutes(5) && Interlocked.Exchange(ref eventScanRunning, 1) == 0)
            {
                try
                {
                    var batch = await new WindowsEventCollector().Collect(TimeSpan.FromDays(1), 200, CancellationToken.None, healthStore.EventCursors());
                    healthStore.SaveEvents(batch.Events); healthStore.SaveEventGaps(batch.Gaps); healthStore.SaveEventCursors(batch.Cursors); eventWarnings = batch.Warnings; lastEventScan = DateTimeOffset.UtcNow;
                }
                finally { Interlocked.Exchange(ref eventScanRunning, 0); }
            }
            await Dispatcher.BeginInvoke(new Action(RefreshMonitoredPage));
        }
        catch (Exception ex) { healthError = ex.Message; }
    }
    private void LiveMetricsSection()
    {
        liveMetrics ??= new LiveMetricsPanel();
        if (liveMetrics.Parent is Panel previous) previous.Children.Remove(liveMetrics);
        PageContent.Children.Add(liveMetrics);
    }
    private void Health()
    {
        Heading("Saúde do computador", "Leituras locais enquanto o Nexo está em execução. Achados são hipóteses até a função ser testada.",
            // Sem o histórico operacional o monitor não inicia (StartHealthMonitoring): a linha não promete leitura periódica.
            healthStore == null ? "Monitoramento local · histórico indisponível" : "Monitoramento local · leitura a cada " + healthSettings.IntervalSeconds + " s");
        LiveMetricsSection();
        // Estados da tela (#143): erro (histórico indisponível), aviso (falha de coleta), carregando (sem a 1ª leitura) e vazio por seção.
        if (healthStore == null) { PageContent.Children.Add(PageKit.Notice("shield", "Histórico operacional indisponível", healthError ?? "", "Danger")); return; }
        if (healthError != null) PageContent.Children.Add(PageKit.Notice("shield", "Última falha de coleta", healthError, "Warning"));
        if (healthMonitor?.LastError != null) PageContent.Children.Add(PageKit.Notice("shield", "Falha no monitoramento", healthMonitor.LastError, "Warning"));
        try
        {
            var latest = healthStore.RecentSnapshots(DateTimeOffset.UtcNow.AddDays(-1), 1).FirstOrDefault();
            if (latest == null) PageContent.Children.Add(PageKit.Notice("performance", "Aguardando a primeira leitura",
                "A coleta começa quando o Nexo abre. Os indicadores aparecem quando houver dados disponíveis.", "Accent", loading: healthMonitor?.LastError == null));
            else
            {
                var overview = new StackPanel();
                var top = new DockPanel { LastChildFill = true };
                var collectionStatus = latest.CollectionStatus switch { "complete" => "completa", "partial" => "parcial", _ => latest.CollectionStatus };
                var collection = PageKit.Chip("Coleta: " + collectionStatus, latest.CollectionStatus switch { "complete" => "Success", "partial" => "Warning", _ => "Neutral" });
                DockPanel.SetDock(collection, Dock.Right); top.Children.Add(collection);
                var readingTitle = new StackPanel { Orientation = Orientation.Horizontal };
                readingTitle.Children.Add(new TextBlock { Text = "Última leitura", FontFamily = Font("DisplayFont"), FontWeight = FontWeights.SemiBold, FontSize = 21,
                    Foreground = Brush("Ink"), VerticalAlignment = VerticalAlignment.Center });
                var readingTime = Mono(latest.Time.ToLocalTime().ToString("g"), 13, "Secondary"); readingTime.Margin = new Thickness(12, 0, 0, 0);
                readingTime.VerticalAlignment = VerticalAlignment.Center; readingTitle.Children.Add(readingTime);
                top.Children.Add(readingTitle);
                overview.Children.Add(top);
                var metrics = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 15, -10, 4) };
                // Sem leitura, "indisponível" é texto: usa a fonte do corpo como o estado da rede, para não cortar com reticências.
                metrics.Children.Add(MetricTile("CPU", MetricPercent(latest.CpuPercent), "performance", numeric: latest.CpuPercent.HasValue));
                metrics.Children.Add(MetricTile("Memória", MetricPercent(latest.MemoryUsedPercent), "health", numeric: latest.MemoryUsedPercent.HasValue));
                metrics.Children.Add(MetricTile("Rede local", latest.Connectivity switch { "local" => "Interface ativa", "none" => "Sem interface", _ => "Indisponível" }, "network", numeric: false));
                overview.Children.Add(metrics);

                // Temperatura (#142): sem o driver PawnIO (ou sem sensor), a leitura fica indisponível e a tela diz por quê, sem tom de erro;
                // erro ou valor descartado aparece como atenção.
                overview.Children.Add(PageKit.Group("Sensores"));
                var temperatureText = "Temperatura da CPU: " + HealthReportPresentation.DescribeTemperature(latest);
                if (latest.CpuTemperature is { State: "read", Celsius: not null } || latest.CpuTemperature == null && latest.CpuTemperatureC.HasValue)
                    overview.Children.Add(StatusLine(temperatureText, "Success"));
                else
                {
                    var failed = latest.CpuTemperature?.State is "error" or "invalid";
                    // "invalid" é valor descartado; "error" é falha de leitura (o sensor não respondeu), não descarte.
                    var title = latest.CpuTemperature?.State == "invalid" ? "Leitura de temperatura descartada" : failed ? "Falha na leitura de temperatura" : "Temperatura indisponível neste computador";
                    var unavailable = PageKit.Notice("health", title, temperatureText, failed ? "Warning" : "Neutral");
                    unavailable.Margin = new Thickness(0, 8, 0, 0); overview.Children.Add(unavailable);
                }

                overview.Children.Add(PageKit.Group("Armazenamento"));
                overview.Children.Add(StatusLine("Discos físicos: " + HealthReportPresentation.DescribeStorage(latest), latest.StorageHealth is "warning" or "unhealthy" ? "Warning" : latest.StorageHealth == "healthy" ? "Success" : "Neutral"));
                foreach (var physical in latest.Storage?.Disks.Take(6) ?? [])
                    overview.Children.Add(StatusLine(HealthReportPresentation.DescribeDisk(physical), physical.State is "warning" or "unhealthy" ? "Warning" : physical.State == "healthy" ? "Success" : "Neutral", 16));
                if (latest.Storage is { Total: > 0 })
                {
                    var scope = Text(HealthReportPresentation.StorageScope, 12, "Secondary");
                    scope.Margin = new Thickness(16, 6, 0, 0); overview.Children.Add(scope);
                }
                foreach (var disk in latest.Disks.Take(6))
                {
                    var free = disk.TotalBytes > 0 ? 100.0 * disk.FreeBytes / disk.TotalBytes : 0;
                    var low = free < healthSettings.DiskLowFreePercent;
                    var volume = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
                    var freeText = Mono($"{free:F1}% livre", 13, low ? "Warning" : "Ink"); DockPanel.SetDock(freeText, Dock.Right); volume.Children.Add(freeText);
                    volume.Children.Add(Mono(disk.Name, 13, "Accent"));
                    var diskText = new StackPanel(); diskText.Children.Add(volume);
                    diskText.Children.Add(PageKit.Bar(1 - free / 100, low ? "Warning" : "Accent"));
                    AutomationProperties.SetName(diskText, $"{disk.Name}: {free:F1}% livre");
                    overview.Children.Add(diskText);
                }

                overview.Children.Add(PageKit.Group("Rede"));
                foreach (var (label, reading) in new[]
                {
                    ("Gateway", latest.GatewayProbe), ("DNS", latest.DnsProbe), ("Destino externo", latest.ExternalProbe)
                })
                    overview.Children.Add(StatusLine(label + ": " + HealthReportPresentation.DescribeProbe(reading),
                        reading?.State is "no_response" or "unexpected" ? "Warning" : reading?.State == "ok" ? "Success" : "Neutral"));
                PageContent.Children.Add(Card(overview));
            }
            // Parada normal sem prova de execução vai para o aviso informativo, não para as ocorrências (#57).
            var alerts = healthStore.RecentFindings(20).Where(finding => !HealthOccurrences.SupersededServiceStop(finding, latest)).ToList();
            var occurrenceNow = DateTimeOffset.UtcNow;
            var occurrenceHistory = healthStore.RecentSnapshots(occurrenceNow.AddHours(-6), 20);
            var occurrenceLimits = healthSettings.Thresholds();
            PageContent.Children.Add(SectionHeading("health", "Ocorrências preventivas"));
            PageContent.Children.Add(PageKit.Notice("history", "Coleta", HealthOccurrences.CollectionNotice(latest, occurrenceLimits, occurrenceNow), "Neutral"));
            if (alerts.Count == 0) PageContent.Children.Add(PageKit.Notice("check", "Nenhuma ocorrência registrada.", "", "Success"));
            var occurrences = HealthOccurrences.Classify(alerts, occurrenceHistory, occurrenceLimits, occurrenceNow);
            // Parada normal sem execução estável observada antes: visível, mas não conta como alerta (#57).
            if (HealthReportPresentation.CleanStopNotice(latest, occurrences) is { } cleanStops)
                PageContent.Children.Add(PageKit.Notice("shield", "Parada sem alerta", cleanStops, "Neutral"));
            foreach (var (finding, status) in occurrences)
            {
                var detail = new StackPanel();
                var tone = status.State != OccurrenceState.Persistent ? "Secondary" : finding.Severity == "critical" ? "Danger" : "Warning";
                var meta = new WrapPanel();
                meta.Children.Add(Mono($"{finding.Time.ToLocalTime():g}  ·  {HealthOccurrences.ShownSeverity(finding, status)}", 12, tone));
                detail.Children.Add(meta);
                var occurrenceState = Text(status.Label, 13, status.State switch { OccurrenceState.Persistent => "Warning", OccurrenceState.Resolved => "Success", _ => "Secondary" });
                occurrenceState.FontWeight = FontWeights.SemiBold; occurrenceState.Margin = new Thickness(0, 6, 0, 0); detail.Children.Add(occurrenceState);
                var problem = Text(finding.Problem, 20); problem.FontFamily = Font("DisplayFont"); problem.FontWeight = FontWeights.SemiBold; problem.Margin = new Thickness(0, 8, 0, 6); detail.Children.Add(problem);
                detail.Children.Add(Text($"{finding.Evidence}\nHipótese: {finding.Cause}\nPróximo passo: {finding.Recommendation}", 13, "Secondary"));
                var card = Card(detail);
                if (status.State == OccurrenceState.Persistent) { card.BorderBrush = Brush(tone); card.BorderThickness = new Thickness(3, 1, 1, 1); }
                PageContent.Children.Add(card);
            }
            PageContent.Children.Add(SectionHeading("audit", "Eventos do Windows"));
            foreach (var warning in eventWarnings) PageContent.Children.Add(PageKit.Notice("shield", "Aviso de canal", warning, "Warning"));
            foreach (var gap in healthStore.RecentEventGaps(5, DateTimeOffset.UtcNow.AddDays(-1)))
                PageContent.Children.Add(PageKit.Notice("shield", "Lacuna na coleta", $"{gap.Time.ToLocalTime():g} · {gap.Message}", "Warning"));
            var events = healthStore.RecentEvents(12);
            if (events.Count == 0) PageContent.Children.Add(lastEventScan == DateTimeOffset.MinValue
                ? PageKit.Notice("history", "Aguardando a primeira consulta de eventos.", "", "Accent", loading: true)
                : PageKit.Notice("audit", "Nenhum evento registrado nesta coleta.", "Consulte os avisos de canal antes de interpretar a ausência.", "Neutral"));
            else
            {
                var eventList = new StackPanel();
                foreach (var item in events)
                {
                    var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                    row.Children.Add(Mono($"{item.Time.ToLocalTime():g} · {item.Log} · ID {item.EventId} · nível {item.Level}", 12, "Secondary"));
                    var provider = Text(item.Provider, 14); provider.Margin = new Thickness(0, 3, 0, 0); row.Children.Add(provider);
                    eventList.Children.Add(row);
                }
                PageContent.Children.Add(Card(eventList));
            }
            PageContent.Children.Add(SectionHeading("shield", "Recuperação assistida"));
            var interventions = healthStore.RecentInterventions(12);
            if (interventions.Count == 0) PageContent.Children.Add(PageKit.Notice("shield", "Nenhuma intervenção registrada.", "", "Neutral"));
            else
            {
                var interventionList = new StackPanel();
                foreach (var item in interventions)
                {
                    var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                    row.Children.Add(Mono($"{item.Time.ToLocalTime():g} · {item.Action} · {HealthReportPresentation.InterventionState(item)}", 12, "Secondary"));
                    var summary = Text($"Autorizada: {(item.Authorized ? "sim" : "não")} · {item.Summary}", 14); summary.Margin = new Thickness(0, 3, 0, 0); row.Children.Add(summary);
                    interventionList.Children.Add(row);
                }
                PageContent.Children.Add(Card(interventionList));
            }
            PageContent.Children.Add(SectionHeading("settings", "Drivers instalados"));
            PageContent.Children.Add(Card(Text("O assistente consulta o INF e a versão do driver associados a um dispositivo identificado. A busca online e a instalação exigem um alvo observado e autorização específica.", 14, "Secondary")));
            PageContent.Children.Add(Action("Investigar um driver no assistente", async (_, _) => await GuardAsync(async () => { ShowPage("chat"); await Send("Verifique o driver do dispositivo com problema. Consulte o inventário e o INF instalado antes de sugerir qualquer alteração."); })));
        }
        catch (Exception ex) { PageContent.Children.Add(PageKit.Notice("shield", "Não foi possível ler o histórico operacional", ex.Message, "Danger")); }
    }
    private static TextBox Field(Panel parent, string label, string value = "", bool mono = false)
    {
        parent.Children.Add(new TextBlock { Text = label, Foreground = Brush("Secondary"), FontSize = 13,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 15, 0, 7) });
        var input = new TextBox { Text = value, MaxLength = 240, MinHeight = 42, VerticalContentAlignment = VerticalAlignment.Center };
        if (mono) input.FontFamily = Font("MonoFont");
        AutomationProperties.SetName(input, label); parent.Children.Add(input); return input;
    }
    private static PasswordBox Secret(Panel parent, string label)
    {
        parent.Children.Add(new TextBlock { Text = label, Foreground = Brush("Secondary"), FontSize = 13,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 15, 0, 7) });
        var input = new PasswordBox { MaxLength = 128, MinHeight = 42, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(input, label); parent.Children.Add(input); return input;
    }
    private static CheckBox Check(Panel parent, string label, bool value)
    {
        var check = new CheckBox { Content = label, IsChecked = value }; parent.Children.Add(check); return check;
    }
    private void Profile()
    {
        Heading("Minha conta", "Seu perfil, seus dados e sua segurança.");
        var form = new StackPanel { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left, Width = 600 };
        if (user.MustChangePassword) { var notice = Text("Defina uma senha pessoal para substituir sua senha inicial.", 14, "Warning"); notice.Margin = new Thickness(0, 0, 0, 16); form.Children.Add(notice); }
        var photo = user.Photo;
        var avatar = new Border { Width = 76, Height = 76, CornerRadius = new CornerRadius(38), Background = Brush("AccentSoft"), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
        void ShowAvatar() { if (!string.IsNullOrEmpty(photo)) { var img = new BitmapImage(); img.BeginInit(); img.StreamSource = new MemoryStream(Convert.FromBase64String(photo)); img.CacheOption = BitmapCacheOption.OnLoad; img.EndInit(); avatar.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }; } else avatar.Child = new TextBlock { Text = user.Name[..1].ToUpperInvariant(), FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
        ShowAvatar(); form.Children.Add(avatar);
        var upload = Action("Alterar foto", (_, _) => Guard(() =>
        {
            var dialog = new OpenFileDialog { Filter = "Imagens|*.png;*.jpg;*.jpeg" };
            if (dialog.ShowDialog(this) != true) return;
            if (new FileInfo(dialog.FileName).Length > 5 * 1024 * 1024) throw new InvalidOperationException("Escolha uma imagem de até 5 MB.");
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri(dialog.FileName); bitmap.DecodePixelWidth = 192; bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var buffer = new MemoryStream(); encoder.Save(buffer); photo = Convert.ToBase64String(buffer.ToArray()); avatar.Child = null; ShowAvatar();
        })); upload.HorizontalAlignment = HorizontalAlignment.Left; upload.Margin = new Thickness(0, 12, 0, 8); form.Children.Add(upload);
        var name = Field(form, "Nome", user.Name); var email = Field(form, "E-mail", user.Email);
        var login = Field(form, "Login", user.Login); login.IsReadOnly = true; login.Background = Brush("SurfaceMuted");
        var current = Secret(form, "Senha atual (para alterar a senha)"); var password = Secret(form, "Nova senha (mínimo de 8 caracteres)"); var confirm = Secret(form, "Confirme a nova senha");
        var save = Action("Salvar alterações", (_, _) => Guard(() => { if (password.Password != confirm.Password) throw new InvalidOperationException("As novas senhas não coincidem."); store.UpdateProfile(user, name.Text, email.Text, photo, current.Password, password.Password); RefreshIdentity(); ShowPage("profile"); MessageBox.Show(this, "Seu perfil foi atualizado.", "Tudo certo"); }), true);
        save.Margin = new Thickness(0, 24, 0, 0); form.Children.Add(save); PageContent.Children.Add(Card(form));
    }
    private void Users()
    {
        Heading("Usuários e acessos", "Gerencie contas e escolha o que cada pessoa pode fazer neste computador.");
        PageContent.Children.Add(Action("Criar usuário", (_, _) => AccountEditor(null), true, icon: "plus"));
        foreach (var account in store.Data.Accounts.OrderBy(a => a.Name))
        {
            var row = new DockPanel(); var edit = Action("Editar acesso", (_, _) => AccountEditor(account)); DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
            var detail = new StackPanel(); detail.Children.Add(Text(account.Name + "  ·  " + account.Login, 16)); detail.Children.Add(Text((account.Admin ? "Administrador" : "Usuário") + "  /  " + (account.Active ? "Ativo" : "Desativado"), 12, "Secondary")); row.Children.Add(detail); PageContent.Children.Add(Card(row));
        }
    }
    private void AccountEditor(Account? account)
    {
        PageContent.Children.Clear(); Heading(account == null ? "Criar usuário" : "Editar acesso", "Permissões são verificadas antes de cada operação.");
        var edited = account == null ? new Account() : JsonSerializer.Deserialize<Account>(JsonSerializer.Serialize(account))!;
        var form = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left, Width = 620 };
        var name = Field(form, "Nome", edited.Name); var login = Field(form, "Login", edited.Login); var email = Field(form, "E-mail", edited.Email);
        var password = Secret(form, account == null ? "Senha inicial" : "Redefinir senha (deixe vazio para manter)");
        var admin = Check(form, "Administrador — gerenciar usuários e configurações", edited.Admin);
        var diagnose = Check(form, "Realizar diagnósticos", edited.Diagnose); var repair = Check(form, "Autorizar correções no computador", edited.Repair); var tickets = Check(form, "Criar encaminhamentos para a TI", edited.Tickets); var active = Check(form, "Conta ativa", edited.Active);
        var actions = new WrapPanel { Margin = new Thickness(0, 22, 0, 0) };
        actions.Children.Add(Action("Salvar usuário", (_, _) => Guard(() =>
        {
            edited.Name = name.Text.Trim(); edited.Login = login.Text.Trim(); edited.Email = email.Text.Trim(); edited.Admin = admin.IsChecked == true; edited.Diagnose = diagnose.IsChecked == true; edited.Repair = repair.IsChecked == true; edited.Tickets = tickets.IsChecked == true; edited.Active = active.IsChecked == true;
            store.SaveAccount(user, edited, password.Password); RefreshIdentity(); ShowPage("users");
        }), true)); actions.Children.Add(Action("Cancelar", (_, _) => ShowPage("users"))); form.Children.Add(actions); PageContent.Children.Add(Card(form));
    }
    private async void SendClick(object sender, RoutedEventArgs e) => await GuardAsync(() => Send(Composer.Text));
    private async void ComposerKeyDown(object sender, KeyEventArgs e) => await GuardAsync(() => HandleComposerKey(e, Keyboard.Modifiers));
    private async Task HandleComposerKey(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (e.Key == Key.Enter && modifiers == ModifierKeys.Shift) { e.Handled = true; if (!e.IsRepeat) await Send(Composer.Text); }
    }
    private void AddEntry(string role, string text, string presentation = "", long? durationMs = null)
    {
        if (conversation == null) return;
        var entry = new Entry { Role = role, Text = text, Presentation = presentation, DurationMs = durationMs }; conversation.Entries.Add(entry);
        try { store.Save(); }
        catch { conversation.Entries.Remove(entry); throw; }
        RenderEntry(entry);
    }
    private void RenderEntry(Entry entry)
    {
        ClearQuickReplies();
        if (entry.Role == "tool")
        {
            RenderToolEntry(entry);
            RefreshChatActions();
            ChatScroll.ScrollToEnd();
            return;
        }
        var body = new StackPanel();
        var result = entry.Presentation is "result" or "error" or "resolved";
        if (result)
        {
            var title = entry.Presentation switch { "error" => "Etapa não concluída", "resolved" => "Resolvido por você", _ => "Resultado da verificação" };
            var stateColor = entry.Presentation == "error" ? "Warning" : "Success";
            var resultHeader = new StackPanel { Orientation = Orientation.Horizontal };
            resultHeader.Children.Add(UiIcons.Create(entry.Presentation == "error" ? "shield" : "check", 16, Brush(stateColor)));
            resultHeader.Children.Add(new TextBlock { Text = title, Foreground = Brush(stateColor), FontWeight = FontWeights.SemiBold,
                FontSize = 14, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(resultHeader);
            if (entry.DurationMs is > 0)
            {
                var elapsed = TimeSpan.FromMilliseconds(entry.DurationMs.Value);
                var timing = Mono(elapsed.TotalMinutes >= 1 ? $"Verificação executada em {(int)elapsed.TotalMinutes} min {elapsed.Seconds} s" : $"Verificação executada em {Math.Max(1, (int)elapsed.TotalSeconds)} s", 11, "Secondary");
                timing.Margin = new Thickness(0, 5, 0, 0); body.Children.Add(timing);
            }
        }
        else
        {
            var byline = new Grid();
            byline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            byline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (entry.Role != "user") byline.Children.Add(UiIcons.Logo(12));
            var bylineText = new TextBlock { Text = (entry.Role == "user" ? user.Name : "Nexo") + "  ·  " + entry.Time.ToString("HH:mm"),
                FontSize = 11, FontFamily = Font("MonoFont"), Foreground = Brush(entry.Role == "user" ? "Secondary" : "Accent"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(entry.Role == "user" ? 0 : 7, 0, 0, 0) };
            Grid.SetColumn(bylineText, 1); byline.Children.Add(bylineText);
            body.Children.Add(byline);
        }
        // #143: a linha de fatos (#73) sai do corpo e vira uma faixa própria no fim do cartão, com o mesmo texto.
        var shownText = entry.Role == "assistant" ? TurnFacts.Body(entry.Text) : entry.Text;
        var facts = shownText.Length < entry.Text.Length ? entry.Text[shownText.Length..].Trim() : "";
        if (facts.Length > 0 && shownText.Length == 0) { shownText = facts; facts = ""; }
        var text = new TextBox { Text = shownText, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 12, 0, 0), FontSize = 14, IsReadOnlyCaretVisible = false };
        AutomationProperties.SetName(text, (entry.Role == "user" ? "Mensagem de " + user.Name : "Resposta do Nexo"));
        body.Children.Add(text);
        if (facts.Length > 0) body.Children.Add(FactsLine(facts));
        if (entry.Role == "assistant" && conversation?.Status == "Em andamento" && ReferenceEquals(conversation.Entries.LastOrDefault(), entry))
        {
            var replies = QuickReplies.For(entry.Text);
            if (replies.Length > 0)
            {
                var ownerId = conversation.Id;
                quickReplyPanel = new WrapPanel { Margin = new Thickness(0, 18, 0, 0), IsEnabled = !busy };
                foreach (var reply in replies)
                {
                    var button = Action(reply, async (_, _) => await GuardAsync(() => SubmitQuickReply(ownerId, entry, reply)), reply == replies[0]);
                    button.Padding = new Thickness(15, 8, 15, 8);
                    button.ToolTip = "Enviar: " + reply;
                    AutomationProperties.SetName(button, "Responder: " + reply);
                    quickReplyPanel.Children.Add(button);
                }
                body.Children.Add(quickReplyPanel);
            }
        }
        var card = Card(body);
        card.Background = Brush(entry.Role == "user" ? "UserSurface" : entry.Presentation == "error" ? "WarningSoft" : result ? "SuccessSoft" : "Surface");
        if (result) card.BorderBrush = Brush(entry.Presentation == "error" ? "Warning" : "Success");
        Messages.Children.Add(card); RefreshChatActions(); ChatScroll.ScrollToEnd();
    }
    private void RenderToolEntry(Entry entry)
    {
        var separator = entry.Text.IndexOf('\n');
        var label = (separator >= 0 ? entry.Text[..separator] : entry.Text).TrimEnd('…');
        var raw = separator >= 0 ? entry.Text[(separator + 1)..] : "";
        var (state, explanation, warning) = ToolResultPresentation.Describe(label, raw);
        // O Describe só separa aviso do resto: "Nenhum dispositivo encontrado" e "Restaurado a seu pedido" não são sucesso. Fora do
        // aviso, a etapa mantém o escudo no acento de antes da #143; o check verde fica para o cartão de resultado.
        var color = warning ? "Warning" : "Accent";
        var detail = new TextBox { Text = explanation, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsReadOnlyCaretVisible = false, FontSize = 13 };
        AutomationProperties.SetName(detail, "Detalhe da etapa " + label);
        // #143: etapa expansível como no protótipo (marca de estado, nome da etapa e situação), aberta pelo teclado ou pelo clique.
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.Children.Add(new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Brush(warning ? "WarningSoft" : "AccentSoft"),
            BorderBrush = Brush(color), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top, Child = UiIcons.Create("shield", 11, Brush(color)) });
        var name = new TextBlock { Text = label + "  ·  " + state, Foreground = Brush(warning ? "Warning" : "Ink"), FontSize = 13, FontWeight = FontWeights.Medium,
            Margin = new Thickness(10, 1, 8, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(name, 1); title.Children.Add(name);
        var step = PageKit.Disclosure(title, "Etapa: " + label + ", " + state, detail);
        step.Margin = new Thickness(0, 0, 0, 4);
        Messages.Children.Add(step);
    }
    // Linha de fatos (#73) com o visual da #143: rótulo do prefixo e a lista em mono, sobre a superfície neutra; o texto não muda.
    private static Border FactsLine(string facts)
    {
        var prefixAt = facts.IndexOf(':');
        var line = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(UiIcons.Create("audit", 14, Brush("Secondary")));
        head.Children.Add(new TextBlock { Text = prefixAt > 0 ? facts[..(prefixAt + 1)] : facts, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("Secondary"),
            Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        line.Children.Add(head);
        if (prefixAt > 0 && prefixAt + 1 < facts.Length)
        {
            var list = new TextBox { Text = facts[(prefixAt + 1)..].Trim(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Padding = new Thickness(0), Margin = new Thickness(21, 4, 0, 0), FontSize = 13, FontFamily = Font("MonoFont"), IsReadOnlyCaretVisible = false };
            AutomationProperties.SetName(list, facts);
            line.Children.Add(list);
        }
        return new Border { Background = Brush("SurfaceMuted"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 14, 0, 0), Child = line };
    }
    private void ClearQuickReplies()
    {
        if (quickReplyPanel != null) { quickReplyPanel.IsEnabled = false; quickReplyPanel.Visibility = Visibility.Collapsed; quickReplyPanel = null; }
    }
    private async Task SubmitQuickReply(string ownerId, Entry question, string reply)
    {
        if (busy || conversation?.Id != ownerId || conversation.Status != "Em andamento" || !ReferenceEquals(conversation.Entries.LastOrDefault(), question)) return;
        ClearQuickReplies();
        await Send(reply, preserveDraft: true);
    }
    private async Task Send(string prompt, bool preserveDraft = false)
    {
        if (busy || string.IsNullOrWhiteSpace(prompt) || conversation == null) return;
        if (user.MustChangePassword) { ShowPage("profile"); MessageBox.Show(this, "Defina sua senha pessoal antes de iniciar o atendimento.", "Primeiro acesso"); return; }
        if (conversation.Status != "Em andamento") { NewConversation(); }
        if (conversation!.Entries.Count == 0) { Messages.Children.Clear(); conversation.Title = prompt.Length > 70 ? prompt[..70] + "…" : prompt; store.Data.Conversations.Add(conversation); }
        if (!preserveDraft) Composer.Clear(); AddEntry("user", prompt.Trim());
        var turnClock = Stopwatch.StartNew();
        await Execute(async token =>
        {
            if (store.Data.AiEnabled)
            {
                UpdateProgress("Conectando o agente ao executor do Windows…");
                if (!DriverWorkflow.Matches(prompt) && DriverWorkflow.ResumedRequest(conversation!, DateTime.UtcNow) == null && (!user.Repair || AgentProtocol.RequestsReadOnly(prompt) || !NetworkWorkflow.Matches(prompt)))
                {
                    try { await LocalModel.EnsureRunning(token); }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or TimeoutException)
                    {
                        AddEntry("assistant", "O agente Ollama está indisponível: " + ex.Message + "\n\n" + OfflineHealthReport(), "result", turnClock.ElapsedMilliseconds);
                        return;
                    }
                }
                var events = new Progress<string>(UpdateProgress);
                var executor = conversationTools ??= new WindowsTools(store, user, ConfirmChange, events, text => AddEntry("assistant", text)) { ConversationId = conversation!.Id };
                var agent = new AgentService(store, executor);
                string answer;
                try { answer = await agent.Run(user, conversation!, events, (label, result) => AddEntry("tool", label + "\n" + result), token); }
                catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
                {
                    AddEntry("assistant", "O agente Ollama ficou indisponível: " + ex.Message + "\n\n" + OfflineHealthReport() + "\n\n" + agent.TurnFactsLine, "result", turnClock.ElapsedMilliseconds);
                    return;
                }
                // Interrompido (cancelamento, erro): a mensagem de interrupção também diz o que o Nexo alterou no turno (#73).
                catch { interruptedFacts = agent.TurnFactsLine; throw; }
                AddEntry("assistant", answer, AnswerPresentation(answer), turnClock.ElapsedMilliseconds);
                return;
            }
            var topic = SupportService.Topic(prompt);
            if (topic != "general")
            {
                AddEntry("assistant", "Vou consultar o Windows para entender o que está acontecendo. Você pode acompanhar as etapas na conversa.");
                var diagnostic = await support.Diagnose(user, topic, new Progress<string>(UpdateProgress), token);
                AddEntry("assistant", "Verificação concluída.\n\n" + diagnostic.Summary, "result", turnClock.ElapsedMilliseconds); RenderActions(diagnostic);
            }
            else
            {
                UpdateProgress(store.Data.AiEnabled ? "Consultando o modelo de IA local…" : "Preparando orientação local…");
                AddEntry("assistant", await support.AskAi(user, conversation!, token), "result", turnClock.ElapsedMilliseconds);
            }
        });
    }
    private void UpdateProgress(string message)
    {
        var follow = ProgressScroll.ScrollableHeight - ProgressScroll.VerticalOffset < 24;
        ProgressText.Text += "\n• " + message;
        if (follow) Dispatcher.BeginInvoke(new Action(() => ProgressScroll.ScrollToEnd()));
    }
    private async Task Execute(Func<CancellationToken, Task> action, bool cancellable = true)
    {
        if (busy) return;
        busy = true; operation = new CancellationTokenSource(); BusyBar.Visibility = PageKit.MotionAllowed ? Visibility.Visible : Visibility.Collapsed; CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed; CancelButton.IsEnabled = cancellable;
        if (quickReplyPanel != null) quickReplyPanel.IsEnabled = false;
        SendButton.IsEnabled = false; Navigation.IsEnabled = false; NewChatButton.IsEnabled = false; Composer.IsEnabled = false; RefreshChatActions();
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "ACOMPANHAMENTO";
        try { await action(operation.Token); UpdateProgress("Resultado disponível no chat."); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { AddEntry("assistant", "Atendimento interrompido. As ações já concluídas permanecem registradas acima; cancelar não desfaz uma correção concluída." + InterruptedFacts(), "error"); UpdateProgress("Atendimento interrompido."); }
        catch (OperationCanceledException) { AddEntry("assistant", "A etapa expirou ou foi interrompida antes de confirmar o resultado. Confira os resultados registrados acima antes de tentar novamente ou encaminhar à TI." + InterruptedFacts(), "error"); UpdateProgress("Etapa não concluída: resultado não confirmado."); store.Log(user, "Operação interrompida sem cancelamento do usuário"); }
        catch (Exception ex) { AddEntry("assistant", "Não consegui concluir esta etapa. " + ex.Message + "\n\nVocê pode tentar novamente ou gerar um encaminhamento para a TI." + InterruptedFacts(), "error"); UpdateProgress("Etapa não concluída."); store.Log(user, "Operação não concluída: " + ex.GetType().Name); }
        finally { interruptedFacts = null; busy = false; operation.Dispose(); operation = null; BusyBar.Visibility = Visibility.Collapsed; CancelButton.Visibility = Visibility.Collapsed; ProgressPanel.Visibility = Visibility.Collapsed; CancelButton.IsEnabled = true; SendButton.IsEnabled = Navigation.IsEnabled = NewChatButton.IsEnabled = Composer.IsEnabled = true; RefreshChatActions(); if (quickReplyPanel != null) quickReplyPanel.IsEnabled = true; ChatScroll.ScrollToEnd(); ResumePendingClose(); }
    }
    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (operation == null || operation.IsCancellationRequested) return;
        CancelButton.IsEnabled = false;
        operation.Cancel();
        UpdateProgress("Cancelamento solicitado. Etapas ainda não iniciadas serão interrompidas. Se uma alteração do Windows já começou, aguarde o resultado registrado no chat para confirmar seu estado.");
    }
    private async Task OfferNetworkRecovery()
    {
        List<InterruptedNetworkTransaction> interrupted;
        try { interrupted = NetworkRecovery.Scan(store.Root, DateTimeOffset.UtcNow); }
        catch (Exception ex) { store.Log(user, "Verificação de transações de rede interrompidas indisponível: " + ex.Message); return; }
        if (interrupted.Count == 0 || busy) return;
        busy = true;
        var posted = false;
        try
        {
            foreach (var item in interrupted)
            {
                if (closeAfterPrompt) break;
                string outcome;
                try { outcome = await NetworkRecovery.Recover(item, ConfirmChange, NetworkRecovery.StartElevated, NetworkRecovery.DefaultTimeout, CancellationToken.None, NetworkRecovery.ReadCurrentState); }
                catch (Exception ex) { outcome = System.Text.Json.JsonSerializer.Serialize(new { success = false, error = ex.Message }); }
                var message = NetworkRecovery.Explain(outcome);
                store.Log(user, "Transação de rede interrompida (" + Path.GetFileName(item.Directory) + "): " + message);
                // A conferência sem elevação (#128) ou um estado que não pede decisão (keeping sem resultado, mais de 7 dias) já foi avisado
                // numa abertura anterior: só o registro, sem aviso no chat e sem trocar a tela de abertura.
                if (NetworkRecovery.AlreadyRecorded(outcome)) continue;
                // #128: o resultado também vai para a conversa em que a alteração começou, com a linha de fatos.
                Conversation? origin = null;
                try { origin = NetworkRecovery.AttachToOrigin(store, user, item.Directory, outcome, DateTimeOffset.Now); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { store.Log(user, "Resultado da recuperação de rede não anexado à conversa de origem: " + ex.Message); }
                // A marca de aviso já mostrado só é gravada depois de o aviso estar no chat: se a entrega falhar, ele volta na próxima abertura.
                if (origin != null && ReferenceEquals(origin, conversation)) { RenderEntry(origin.Entries[^1]); NetworkRecovery.MarkNotified(item, outcome); continue; }
                AddEntry("assistant", "Encontrei uma alteração de rede que não terminou.\n\n" + message
                    + (origin != null ? "\n\nO resultado também foi registrado no atendimento “" + origin.Title + "”." : ""), NetworkRecovery.Restored(outcome) ? "result" : "error");
                NetworkRecovery.MarkNotified(item, outcome);
                posted = true;
            }
        }
        finally
        {
            busy = false;
            // #143: o Nexo abre na Início; o resultado da restauração vai para a conversa, então o Assistente é aberto para ele ser visto.
            if (posted && page != "chat") ShowPage("chat");
            ResumePendingClose();
        }
    }
    private void ResumePendingClose()
    {
        if (!closeAfterPrompt || busy) return;
        closeAfterPrompt = false;
        Dispatcher.BeginInvoke(new Action(Close));
    }
    private Task<bool> ConfirmChange(ChangeRequest request) => ConfirmChange(request, null);
    private Task<bool> ConfirmChange(ChangeRequest request, Action<Window>? onRendered)
    {
        var dialog = new Window { Owner = this, Title = request.Title, Width = 700, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, UseLayoutRounding = true, Style = (Style)FindResource(typeof(Window)), Icon = Icon };
        AutomationProperties.SetName(dialog, "Confirmação de ação: " + request.Title);
        var panel = new DockPanel { Margin = new Thickness(25) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(6), Background = Brush("AccentSoft"), BorderBrush = Brush("AccentBorder"),
            BorderThickness = new Thickness(1), Child = UiIcons.Create("shield", 16) });
        label.Children.Add(new TextBlock { Text = "CONFIRMAÇÃO DE AÇÃO", FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = Font("MonoFont"),
            Foreground = Brush("Accent"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(label);
        var title = Text(request.Title, 26); title.FontWeight = FontWeights.SemiBold; title.FontFamily = Font("DisplayFont"); title.Margin = new Thickness(0, 12, 0, 0);
        header.Children.Add(title);
        // #143: o estado observado do alvo ("Estado atual: parado (início automático).") sobe para o cabeçalho do cartão de aprovação;
        // o restante do detalhe continua abaixo, com o mesmo texto.
        var detailLines = request.Details.Split('\n');
        var stateLines = detailLines.Where(line => line.StartsWith("Estado atual:", StringComparison.Ordinal) || line.StartsWith("Estado observado:", StringComparison.Ordinal)).ToArray();
        foreach (var stateLine in stateLines) { var observed = Text(stateLine, 13, "Secondary"); observed.Margin = new Thickness(0, 6, 0, 0); header.Children.Add(observed); }
        if (!request.KeepNetwork)
        {
            var uac = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
            uac.Children.Add(UiIcons.Create("shield", 15, Brush("Warning")));
            uac.Children.Add(new TextBlock { Text = "Se a ação precisar de permissão de administrador, o Windows mostrará o aviso do UAC.", FontSize = 13, Foreground = Brush("Warning"),
                Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            header.Children.Add(uac);
        }
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom);
        // #128: a janela fechada sem botão (X, Alt+F4 ou o Nexo encerrando) avisa quem pediu, para a oferta de restauração valer "Agora não".
        var decided = false;
        buttons.Children.Add(Action(request.KeepNetwork ? "Restaurar configuração anterior" : "Cancelar ação", (_, _) => { decided = true; if (request.KeepNetwork) request.OnExplicitRestore?.Invoke(); dialog.DialogResult = false; }));
        var authorize = Action(request.KeepNetwork ? "Manter configuração" : "Autorizar e executar", (_, _) => { decided = true; dialog.DialogResult = true; }, true);
        authorize.IsEnabled = request.LicenseUrl == null;
        buttons.Children.Add(authorize); panel.Children.Add(buttons);
        var countdown = Mono("", 12, "Warning"); countdown.Margin = new Thickness(0, 9, 0, 0);
        DockPanel.SetDock(countdown, Dock.Bottom); panel.Children.Add(countdown);
        if (request.LicenseUrl != null)
        {
            var license = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            license.Children.Add(Text("Licença do fornecedor: " + request.LicenseUrl, 12));
            var openLicense = Action("Abrir licença no navegador", (_, _) => Process.Start(new ProcessStartInfo(request.LicenseUrl) { UseShellExecute = true }));
            license.Children.Add(openLicense);
            var agreement = new CheckBox { Content = "Li e aceito os termos da licença da NVIDIA para este download e instalação.", Margin = new Thickness(0, 10, 0, 0) };
            agreement.Checked += (_, _) => authorize.IsEnabled = true;
            agreement.Unchecked += (_, _) => authorize.IsEnabled = false;
            license.Children.Add(agreement);
            DockPanel.SetDock(license, Dock.Bottom); panel.Children.Add(license);
        }
        var details = new TextBox { Text = string.Join('\n', detailLines.Where(line => !stateLines.Contains(line))), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0) };
        AutomationProperties.SetName(details, "Detalhes da ação");
        panel.Children.Add(new Border { Background = Brush("SurfaceMuted"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Child = details });
        dialog.Content = new Border { Background = Brush("Surface"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Margin = new Thickness(16), Child = panel };
        var seconds = 45;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { countdown.Text = $"Restauração automática em {--seconds} segundos."; if (seconds <= 0) { timer.Stop(); decided = true; dialog.DialogResult = false; } };
        if (request.KeepNetwork) { countdown.Text = "Restauração automática em 45 segundos."; timer.Start(); }
        Exception? previewFailure = null;
        if (onRendered != null) dialog.ContentRendered += (_, _) =>
        {
            try { onRendered(dialog); }
            catch (Exception ex) { previewFailure = ex; }
            finally { decided = true; dialog.DialogResult = false; }
        };
        dialog.Closed += (_, _) => { if (!decided) request.OnDismissed?.Invoke(); };
        if (request.Postponable) postponableDialog = dialog;
        bool accepted;
        try { accepted = dialog.ShowDialog() == true; }
        finally { timer.Stop(); if (ReferenceEquals(postponableDialog, dialog)) postponableDialog = null; }
        if (previewFailure != null) throw new InvalidOperationException("Falha na prévia do diálogo de autorização.", previewFailure);
        return Task.FromResult(accepted);
    }
    private void RenderActions(Diagnostic diagnostic)
    {
        var actions = new WrapPanel();
        actions.Children.Add(Action("Abrir configurações", (_, _) => Guard(() => { if (busy) return; SupportService.OpenSettings(diagnostic.Topic); store.Log(user, "Configurações abertas: " + diagnostic.Topic); AddEntry("assistant", "Abri as configurações do Windows para você conferir. Nenhuma opção foi alterada automaticamente."); })));
        if (diagnostic.Topic == "camera")
        {
            actions.Children.Add(Action("Buscar drivers no Windows Update", (_, _) => Guard(() => { if (busy) return; SupportService.OpenSettings("updates"); AddEntry("assistant", "Abri o Windows Update. Clique em Verificar atualizações e confira também as atualizações opcionais de drivers. Ainda não confirmei nenhuma instalação."); })));
            foreach (var device in diagnostic.Devices.Where(d => d.Code == 22)) actions.Children.Add(Action("Habilitar " + device.Name, async (_, _) => await GuardAsync(() => Repair("enable-camera", device.Id, "Habilitar a câmera “" + device.Name + "”? O Windows solicitará autorização de administrador. Nenhum driver será instalado."))));
        }
        if (diagnostic.Topic == "network") actions.Children.Add(Action("Limpar cache DNS", async (_, _) => await GuardAsync(() => Repair("dns", null, "Limpar o cache DNS deste computador? O Windows consultará novamente os endereços ao acessar sites."))));
        Messages.Children.Add(actions);
    }
    private async Task Repair(string action, string? id, string explanation)
    {
        if (busy) return;
        try { store.Require(user, a => a.Repair); } catch (Exception ex) { MessageBox.Show(this, ex.Message); return; }
        if (MessageBox.Show(this, explanation, "Autorizar correção", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Execute(async token => { UpdateProgress(action == "dns" ? "Limpando cache DNS…" : "Solicitando ativação da câmera ao Windows…"); AddEntry("assistant", await support.Repair(user, action, id, token)); }, action == "dns");
    }
    private void ResolvedClick(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (busy || conversation == null || conversation.Entries.Count == 0) return;
        var current = conversation; var previousStatus = current.Status;
        var entry = new Entry { Role = "assistant", Presentation = "resolved", Text = "Que bom que funcionou! Marquei este atendimento como resolvido por você. Quando precisar, é só começar uma nova conversa." };
        store.Commit(user, "Resolução confirmada pelo usuário: " + current.Id,
            () => { current.Status = "Resolvido"; current.Entries.Add(entry); },
            () => { current.Status = previousStatus; current.Entries.Remove(entry); });
        RenderEntry(entry);
    });
    private void EscalateClick(object sender, RoutedEventArgs e) => Guard(() =>
    {
        store.Require(user, a => a.Tickets);
        if (busy || conversation == null || conversation.Entries.Count == 0) { MessageBox.Show(this, "Descreva o problema no chat para preparar o relatório."); return; }
        if (store.Data.Tickets.Any(t => t.ConversationId == conversation.Id)) { ShowPage("tickets"); return; }
        var report = "NEXO • RELATÓRIO DE ATENDIMENTO\nUsuário: " + user.Name + " (" + user.Login + ")\nData: " + DateTime.Now.ToString("g") + "\nAssunto: " + conversation.Title + "\n\n" + string.Join("\n\n", conversation.Entries.Select(m => $"[{m.Time:g}] {(m.Role == "user" ? "Usuário" : "Nexo")}: {m.Text}"));
        var current = conversation; var previousStatus = current.Status;
        var ticket = new Ticket { UserId = user.Id, ConversationId = current.Id, Title = current.Title, Report = report };
        store.Commit(user, "Relatório preparado: " + ticket.Id,
            () => { store.Data.Tickets.Add(ticket); current.Status = "Encaminhamento preparado"; },
            () => { store.Data.Tickets.Remove(ticket); current.Status = previousStatus; });
        ShowPage("tickets");
    });
    private void History()
    {
        var items = store.Data.Conversations.Where(c => c.UserId == user.Id).OrderByDescending(c => c.Created).ToList();
        Heading("Atendimentos", "Suas conversas, diagnósticos e resultados em um só lugar.", items.Count == 1 ? "1 atendimento neste perfil" : items.Count + " atendimentos neste perfil");
        if (items.Count == 0) { PageContent.Children.Add(EmptyState("history", "Seu histórico começa aqui",
            "Quando você iniciar um atendimento, a conversa e os resultados aparecerão nesta página.")); return; }
        foreach (var item in items)
        {
            var row = new StackPanel();
            var title = Text(item.Title, 17); title.FontWeight = FontWeights.SemiBold; row.Children.Add(title);
            var meta = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var created = Mono(item.Created.ToString("g"), 12, "Secondary"); created.Margin = new Thickness(0, 0, 12, 0); created.VerticalAlignment = VerticalAlignment.Center; meta.Children.Add(created);
            meta.Children.Add(PageKit.Chip(item.Status, item.Status switch { "Resolvido" => "Success", "Encaminhamento preparado" => "Accent", _ => "Neutral" }));
            row.Children.Add(meta);
            var open = Action("Abrir conversa", (_, _) => { conversation = item; conversationTools = null; Messages.Children.Clear(); foreach (var entry in item.Entries) RenderEntry(entry); ProgressPanel.Visibility = Visibility.Collapsed; ProgressText.Text = ""; RefreshChatActions(); ShowPage("chat"); }); open.Margin = new Thickness(0, 15, 0, 0);
            AutomationProperties.SetName(open, "Abrir conversa: " + item.Title); row.Children.Add(open); PageContent.Children.Add(Card(row));
        }
    }
    private void Tickets()
    {
        Heading("Encaminhamentos", "Relatórios locais prontos para compartilhar com a TI. Nenhum chamado é enviado automaticamente.", "Relatórios locais · exportação manual");
        var items = store.Data.Tickets.Where(t => user.Admin || t.UserId == user.Id).OrderByDescending(t => t.Created).ToList();
        if (items.Count == 0) PageContent.Children.Add(EmptyState("tickets", "Nenhum encaminhamento preparado",
            "Se o problema persistir após o atendimento, você pode preparar um relatório pelo chat."));
        foreach (var ticket in items)
        {
            var panel = new StackPanel();
            var badge = new WrapPanel();
            var id = Mono(ticket.Id, 12, "Accent"); id.Margin = new Thickness(0, 0, 12, 0); id.VerticalAlignment = VerticalAlignment.Center; badge.Children.Add(id);
            badge.Children.Add(PageKit.Chip("Preparado para envio", "Warning")); panel.Children.Add(badge);
            var title = Text(ticket.Title, 18); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 10, 0, 4); panel.Children.Add(title);
            panel.Children.Add(Mono(ticket.Created.ToString("g"), 12, "Secondary"));
            var detail = new TextBox { Text = ticket.Report, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13 };
            AutomationProperties.SetName(detail, "Relatório " + ticket.Id);
            var review = Text("Revisar relatório", 14); review.FontWeight = FontWeights.SemiBold;
            var disclosure = PageKit.Disclosure(review, "Revisar relatório " + ticket.Id, detail); disclosure.Margin = new Thickness(-10, 12, 0, 12);
            panel.Children.Add(disclosure);
            panel.Children.Add(Action("Exportar relatório .txt", (_, _) => Guard(() => { var dialog = new SaveFileDialog { FileName = ticket.Id + ".txt", Filter = "Relatório de texto|*.txt" }; if (dialog.ShowDialog(this) == true) { File.WriteAllText(dialog.FileName, ticket.Report); store.Log(user, "Relatório exportado: " + ticket.Id); MessageBox.Show(this, "Relatório exportado. Anexe-o ao canal de atendimento da sua TI."); } }))); PageContent.Children.Add(Card(panel));
        }
    }
    private void UpdateToolsView()
    {
        if (page != "tools") return;
        if (officeProgressView != null) officeProgressView.Text = officeStatus;
        if (optimizationProgressView != null) optimizationProgressView.Text = optimizationStatus;
        if (officeInstallView != null) officeInstallView.IsEnabled = !officeRunning;
        if (officeCancelView != null) officeCancelView.IsEnabled = officeRunning;
        if (optimizationStartView != null) optimizationStartView.IsEnabled = !optimizationRunning;
        if (optimizationCancelView != null) optimizationCancelView.IsEnabled = optimizationRunning;
        if (debloatProgressView != null) debloatProgressView.Text = debloatStatus;
        if (debloatScanView != null) debloatScanView.IsEnabled = !debloatRunning;
        if (debloatApplyView != null) debloatApplyView.IsEnabled = !debloatRunning && debloatPreview != null && debloatChecks.Count > 0;
        if (debloatUndoView != null) debloatUndoView.IsEnabled = !debloatRunning && debloatCanUndo;
        if (debloatCancelView != null) debloatCancelView.IsEnabled = debloatRunning;
        foreach (var check in debloatChecks.Values) check.IsEnabled = !debloatRunning && check.Tag is not DebloatAvailability.AlreadyApplied;
        if (debloatRiskView != null) debloatRiskView.IsEnabled = !debloatRunning;
        if (runtimeProgressView != null) runtimeProgressView.Text = runtimeStatus;
        if (runtimeScanView != null) runtimeScanView.IsEnabled = !runtimeRunning;
        if (runtimeInstallView != null) runtimeInstallView.IsEnabled = !runtimeRunning && runtimeChecks.Count > 0;
        if (runtimeCancelView != null) runtimeCancelView.IsEnabled = runtimeRunning;
        foreach (var check in runtimeChecks.Values) check.IsEnabled = !runtimeRunning;
    }
    private void Tools()
    {
        Heading("Ferramentas", "Ações locais iniciadas por você, com resultado e histórico visíveis.", "Ferramentas locais · iniciadas por você");
        // Cartões com o número grande do protótipo (#143), só com o que existe nesta tela; o clique leva à seção da ferramenta.
        var overview = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -12, 8) };
        PageContent.Children.Add(overview);

        var officePanel = new StackPanel();
        officePanel.Children.Add(SectionHeading("tools", "Pacote de trabalho completo"));
        officePanel.Children.Add(Text(OfficeDeploymentService.LicenseNotice, 13, "Secondary"));
        officePanel.Children.Add(Text("Ao clicar em Instalar Office, você confirma que leu o aviso acima. O download e a instalação seguem sem assistente de avanço; o Windows pode pedir autorização de administrador.", 12, "Secondary"));
        officeProgressView = Text(officeStatus, 13, "Ink");
        officePanel.Children.Add(ToolStatus(officeProgressView));
        var officeActions = new WrapPanel();
        officeCancelView = Action("Cancelar espera", (_, _) => officeCancellation?.Cancel());
        officeInstallView = Action("Instalar Office", async (_, _) => await GuardAsync(async () =>
        {
            if (officeRunning) return;
            officeCancellation = new CancellationTokenSource(); officeRunning = true;
            officeStatus = "Verificando requisitos..."; UpdateToolsView();
            try
            {
                var progress = new Progress<OfficeDeploymentProgress>(item => { officeStatus = item.Message; UpdateToolsView(); });
                var result = await officeDeployment.InstallAsync(true, progress, officeCancellation.Token);
                officeStatus = result.Message; UpdateToolsView();
                store.Log(user, "Instalação Office LTSC 2024: " + result.State + (result.ExitCode.HasValue ? " (código " + result.ExitCode + ")" : ""));
            }
            finally { officeRunning = false; officeCancellation.Dispose(); officeCancellation = null; UpdateToolsView(); }
        }), true);
        officeActions.Children.Add(officeInstallView);
        officeActions.Children.Add(officeCancelView);
        officePanel.Children.Add(officeActions);
        officePanel.Children.Add(Text("Cancelar depois que o instalador iniciou interrompe somente a espera do Nexo; o Office pode continuar. Nesse caso, confira o resultado com a TI antes de repetir.", 11, "Subtle"));
        var officeCard = Card(officePanel); PageContent.Children.Add(officeCard);

        var optimizePanel = new StackPanel();
        optimizePanel.Children.Add(SectionHeading("performance", "Otimização básica"));
        optimizePanel.Children.Add(Text("Remove arquivos temporários com mais de sete dias das pastas temporárias do perfil e do Windows. Pula arquivos bloqueados e protegidos; preserva Documentos, Downloads, caches do navegador e a Lixeira. Depois, solicita ao Windows a manutenção nativa dos discos elegíveis; discos virtuais são ignorados.", 13, "Secondary"));
        optimizePanel.Children.Add(Text("O Windows pode pedir autorização de administrador uma vez para a etapa de disco. Arquivos removidos não podem ser recuperados pelo Nexo.", 12, "Secondary"));
        optimizationProgressView = Text(optimizationStatus, 13, "Ink");
        optimizePanel.Children.Add(ToolStatus(optimizationProgressView));
        var optimizeActions = new WrapPanel();
        optimizationCancelView = Action("Cancelar próximas etapas", (_, _) => optimizationCancellation?.Cancel());
        optimizationStartView = Action("Otimizar PC", async (_, _) => await GuardAsync(async () =>
        {
            if (optimizationRunning) return;
            optimizationCancellation = new CancellationTokenSource(); optimizationRunning = true;
            optimizationStatus = "Iniciando otimização..."; UpdateToolsView();
            try
            {
                var auditPath = Path.Combine(store.Root, "optimization-audit.ndjson");
                var service = new BasicOptimizationService(item =>
                {
                    lock (optimizationAuditSync) File.AppendAllText(auditPath, JsonSerializer.Serialize(item) + Environment.NewLine);
                }, OptimizationDiskExecutor.RunAsync);
                var progress = new Progress<OptimizationProgress>(item => { optimizationStatus = item.Message; UpdateToolsView(); });
                var result = await service.RunAsync(progress, optimizationCancellation.Token);
                var cleanup = result.Stages.First(stage => stage.Stage == "Cleanup");
                var disk = result.Stages.First(stage => stage.Stage == "Disk");
                var diskOutcome = disk.Status switch { "Completed" => "concluído", "Skipped" => "sem volume elegível", "Denied" => "autorização negada", "Failed" => "falhou", _ => "resultado não confirmado" };
                var overall = result.Status switch { "Completed" => "concluído", "Partial" => "parcial", "Canceled" => "cancelado", _ => "resultado não confirmado" };
                var details = string.Join("; ", result.Stages.SelectMany(stage => stage.Errors).Take(3));
                optimizationStatus = $"Resultado {overall}. Limpeza: {cleanup.Duration.TotalSeconds:F1}s, {cleanup.CompletedItems} arquivo(s) removido(s), {cleanup.SkippedItems} ignorado(s), {cleanup.LogicalBytesRemoved} bytes lógicos removidos, {cleanup.ErrorCount} erro(s). " +
                    $"Disco: {diskOutcome}, {disk.Duration.TotalSeconds:F1}s, {disk.CompletedItems?.ToString() ?? "?"} volume(s) concluído(s), {disk.ErrorCount} erro(s)." +
                    (details.Length == 0 ? "" : " Detalhes: " + details);
                UpdateToolsView();
                store.Log(user, $"Otimização básica: {result.Status}; {result.RemovedFiles} arquivos; disco {result.DiskStatus}");
            }
            finally { optimizationRunning = false; optimizationCancellation.Dispose(); optimizationCancellation = null; UpdateToolsView(); }
        }), true);
        optimizeActions.Children.Add(optimizationStartView);
        optimizeActions.Children.Add(optimizationCancelView);
        optimizePanel.Children.Add(optimizeActions);
        optimizePanel.Children.Add(Text("Se a etapa de disco já começou, o Nexo aguarda seu término para informar o resultado. A manutenção executada não é revertida pelo cancelamento.", 11, "Subtle"));
        var optimizeCard = Card(optimizePanel); PageContent.Children.Add(optimizeCard);
        var debloatCard = Card(DebloatCard()); PageContent.Children.Add(debloatCard);
        var runtimeCard = Card(RuntimeCard()); PageContent.Children.Add(runtimeCard);
        overview.Children.Add(ToolCard(1, "Instalação", "Instalar Office", "Office LTSC 2024 com o aviso de licença por volume.", "Microsoft · Office LTSC 2024", officeCard));
        overview.Children.Add(ToolCard(2, "Manutenção", "Otimizar PC", "Temporários antigos e manutenção nativa dos discos.", "Temporários · disco", optimizeCard));
        overview.Children.Add(ToolCard(3, "Privacidade · aparência", "Apps pré-instalados e ajustes do Windows", "Apps que vêm com o Windows e ajustes, aplicados só depois da sua confirmação.", "Apps · serviços · ajustes", debloatCard));
        overview.Children.Add(ToolCard(4, "Programas · bibliotecas", RuntimePresentation.CardTitle, "Visual C++, DirectX e .NET: o que falta neste PC, instalado só pelos instaladores oficiais da Microsoft.", "VC++ · DirectX · .NET", runtimeCard));
        UpdateToolsView();
    }
    // Situação da ferramenta numa faixa própria, com o rótulo em mono.
    private static Border ToolStatus(TextBlock status)
    {
        var panel = new StackPanel();
        panel.Children.Add(Mono("SITUAÇÃO", 11, "Secondary"));
        status.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(status);
        AutomationProperties.SetName(status, "Situação");
        return new Border { Background = Brush("SurfaceMuted"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 14, 0, 12), Child = panel };
    }
    private Button ToolCard(int number, string category, string title, string description, string footer, FrameworkElement section)
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(78) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var hero = new Grid();
        hero.Children.Add(new TextBlock { Text = category.ToUpper(System.Globalization.CultureInfo.GetCultureInfo("pt-BR")), FontFamily = Font("MonoFont"), FontSize = 11,
            Foreground = Brush("Secondary"), Margin = new Thickness(14, 12, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left });
        hero.Children.Add(new TextBlock { Text = number.ToString("00"), FontFamily = Font("DisplayFont"), FontWeight = FontWeights.Bold, FontSize = 54, Foreground = Brush("Accent"),
            Margin = new Thickness(0, 0, 14, -8), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right });
        hero.ClipToBounds = true;
        // #143: cartão com raio 8 e borda de 1 px; o cabeçalho segue o raio interno (7) só em cima e o conteúdo inteiro é recortado
        // no mesmo raio, para nenhum fundo vazar nos cantos. No hover o cabeçalho acompanha o AccentSoft do cartão.
        var heroBand = new Border { Background = Brush("SurfaceMuted"), CornerRadius = new CornerRadius(7, 7, 0, 0), Child = hero };
        content.Children.Add(heroBand);
        var text = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        text.Children.Add(new TextBlock { Text = title, FontFamily = Font("DisplayFont"), FontWeight = FontWeights.SemiBold, FontSize = 22, Foreground = Brush("Ink"), TextWrapping = TextWrapping.Wrap });
        var about = Text(description, 13, "Secondary"); about.Margin = new Thickness(0, 4, 0, 10); text.Children.Add(about);
        var bottom = new DockPanel();
        var arrow = UiIcons.Create("arrow", 14); DockPanel.SetDock(arrow, Dock.Right); bottom.Children.Add(arrow);
        bottom.Children.Add(Mono(footer, 11, "Secondary"));
        text.Children.Add(bottom);
        Grid.SetRow(text, 1); content.Children.Add(text);
        PageKit.ClipRounded(content, 7);
        var card = new Button { Content = content, Margin = new Thickness(0, 0, 12, 12), Style = (Style)FindResource("ToolCardButton") };
        card.MouseEnter += (_, _) => heroBand.Background = Brush("AccentSoft");
        card.MouseLeave += (_, _) => heroBand.Background = Brush("SurfaceMuted");
        AutomationProperties.SetName(card, number.ToString("00") + ", " + title + ": " + description);
        card.Click += (_, _) => section.BringIntoView();
        return card;
    }

    // Remoção de apps e ajustes (#127): só pelo menu, nunca pelo modelo. Verificar não altera nada; Aplicar pede confirmação.
    private DebloatUndoStore DebloatUndo() => new(Path.Combine(store.Root, "debloat-undo.json"));
    private DebloatService DebloatServiceFor() => new(new WindowsDebloatSystem(), DebloatUndo(), item =>
    {
        lock (debloatAuditSync) File.AppendAllText(Path.Combine(store.Root, "debloat-audit.ndjson"), JsonSerializer.Serialize(item) + Environment.NewLine);
    });

    private StackPanel DebloatCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(SectionHeading("shield", "Apps pré-instalados e ajustes do Windows"));
        panel.Children.Add(Text("Remove do seu usuário apps que vêm com o Windows e aplica ajustes de privacidade e de aparência. Primeiro o Nexo verifica o que existe neste PC e mostra cada item; nada muda antes de você clicar em Aplicar e confirmar.", 13, "Secondary"));
        panel.Children.Add(Text(DebloatPresentation.CardNotice, 12, "Secondary"));
        panel.Children.Add(Text(DebloatCatalog.Credit, 11, "Subtle"));
        debloatProgressView = Text(debloatStatus, 13, "Ink");
        panel.Children.Add(ToolStatus(debloatProgressView));
        debloatListView = new StackPanel();
        panel.Children.Add(debloatListView);
        RenderDebloatList();
        try { debloatCanUndo = DebloatServiceFor().UndoCandidate() != null; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { debloatCanUndo = false; }
        var actions = new WrapPanel();
        debloatScanView = Action("Verificar este PC", async (_, _) => await GuardAsync(ScanDebloat), icon: "search");
        debloatApplyView = Action("Aplicar selecionados", async (_, _) => await GuardAsync(ApplyDebloat), true);
        debloatUndoView = Action("Desfazer a última aplicação", async (_, _) => await GuardAsync(UndoDebloat));
        debloatCancelView = Action("Cancelar próximas etapas", (_, _) => debloatCancellation?.Cancel());
        actions.Children.Add(debloatScanView); actions.Children.Add(debloatApplyView); actions.Children.Add(debloatUndoView); actions.Children.Add(debloatCancelView);
        panel.Children.Add(actions);
        panel.Children.Add(Text("Uma etapa já iniciada (o ponto de restauração com os serviços, ou a remoção dos apps) termina antes de o cancelamento valer.", 11, "Subtle"));
        return panel;
    }

    private void RenderDebloatList()
    {
        if (debloatListView == null) return;
        debloatListView.Children.Clear(); debloatChecks.Clear(); debloatRiskView = null;
        if (debloatPreview == null) return;
        if (debloatPreview.Items.Count == 0) { debloatListView.Children.Add(Text("Nenhum item do catálogo foi encontrado neste PC.", 12, "Secondary")); return; }
        foreach (var group in debloatPreview.Items.GroupBy(i => i.Item.Category))
        {
            var heading = Text(group.Key, 13); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 10, 0, 4);
            debloatListView.Children.Add(heading);
            foreach (var entry in group)
            {
                var label = entry.Item.Title + (entry.Availability == DebloatAvailability.AlreadyApplied ? " (já aplicado)" : "");
                var check = new CheckBox { Content = label, IsChecked = entry.Item.Default && entry.Availability == DebloatAvailability.Available, Tag = entry.Availability, Margin = new Thickness(0, 4, 0, 0) };
                AutomationProperties.SetName(check, label);
                debloatChecks[entry.Item.Id] = check;
                debloatListView.Children.Add(check);
                if (entry.Detail.Length > 0) { var detail = Text(entry.Detail, 11, "Subtle"); detail.Margin = new Thickness(22, 0, 0, 2); debloatListView.Children.Add(detail); }
                if (entry.Availability == DebloatAvailability.NeedsConfirmation)
                {
                    debloatRiskView = new CheckBox { Content = "Confirmo que o login do Minecraft e do Game Pass vai parar de funcionar neste usuário.", Margin = new Thickness(22, 4, 0, 4) };
                    debloatListView.Children.Add(debloatRiskView);
                }
            }
        }
    }

    private async Task ScanDebloat()
    {
        if (debloatRunning) return;
        debloatRunning = true; debloatStatus = "Verificando apps, serviços e ajustes deste PC. Nada é alterado nesta etapa."; UpdateToolsView();
        try
        {
            debloatPreview = await DebloatServiceFor().PreviewAsync(CancellationToken.None);
            var windows = DebloatCatalog.IsWindows11(debloatPreview.Build) ? "Windows 11" : "Windows 10";
            debloatStatus = $"{windows} (build {debloatPreview.Build}): {debloatPreview.Items.Count} item(ns) encontrado(s). Revise a seleção e clique em Aplicar.";
        }
        finally { debloatRunning = false; RenderDebloatList(); UpdateToolsView(); }
    }

    private async Task ApplyDebloat()
    {
        if (debloatRunning || debloatPreview == null) return;
        var selected = debloatChecks.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToArray();
        if (selected.Length == 0) { debloatStatus = "Nenhum item selecionado."; UpdateToolsView(); return; }
        var question = DebloatPresentation.ApplyQuestion(debloatPreview.Items.Where(i => selected.Contains(i.Item.Id)).Select(i => i.Item.Title));
        if (MessageBox.Show(this, question, "Confirmar remoção de apps e ajustes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        debloatCancellation = new CancellationTokenSource(); debloatRunning = true;
        debloatStatus = "Iniciando..."; UpdateToolsView();
        var gate = new DebloatProgressGate();
        try
        {
            var progress = new Progress<string>(gate.Guard(message => { debloatStatus = message; UpdateToolsView(); }));
            var result = await DebloatServiceFor().ApplyAsync(debloatPreview, selected, debloatRiskView?.IsChecked == true, progress, debloatCancellation.Token);
            gate.Finish();
            debloatStatus = DebloatPresentation.Result(result, undo: false);
            RecordDebloat("Remover apps e ajustes (Ferramentas)", selected, result);
            debloatPreview = null;
        }
        finally { gate.Finish(); debloatRunning = false; debloatCancellation.Dispose(); debloatCancellation = null; RefreshDebloatUndo(); RenderDebloatList(); UpdateToolsView(); }
    }

    private async Task UndoDebloat()
    {
        if (debloatRunning) return;
        var run = DebloatServiceFor().UndoCandidate();
        if (run == null) { debloatCanUndo = false; debloatStatus = "Não há aplicação para desfazer."; UpdateToolsView(); return; }
        var plan = string.Join("\n", DebloatServiceFor().DescribeUndo(run).Select(line => "• " + line));
        if (MessageBox.Show(this, $"Desfazer a aplicação de {run.At.ToLocalTime():g}? O Nexo vai mudar:\n\n{plan}\n\nO Windows pode pedir autorização de administrador. Continuar?",
                "Desfazer remoção de apps e ajustes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        debloatCancellation = new CancellationTokenSource(); debloatRunning = true;
        debloatStatus = "Desfazendo..."; UpdateToolsView();
        var gate = new DebloatProgressGate();
        try
        {
            var progress = new Progress<string>(gate.Guard(message => { debloatStatus = message; UpdateToolsView(); }));
            var result = await DebloatServiceFor().UndoAsync(run, progress, debloatCancellation.Token);
            gate.Finish();
            debloatStatus = DebloatPresentation.Result(result, undo: true);
            RecordDebloat("Desfazer remoção de apps e ajustes (Ferramentas)", run.Entries.Select(e => e.ItemId).Concat(run.ProtectedItems).Distinct(), result);
            debloatPreview = null;
        }
        finally { gate.Finish(); debloatRunning = false; debloatCancellation.Dispose(); debloatCancellation = null; RefreshDebloatUndo(); RenderDebloatList(); UpdateToolsView(); }
    }

    private void RefreshDebloatUndo()
    {
        try { debloatCanUndo = DebloatServiceFor().UndoCandidate() != null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { debloatCanUndo = false; }
    }

    private void RecordDebloat(string action, IEnumerable<string> ids, DebloatResult result)
    {
        var summary = DebloatService.Summary(result.Status, result.Items);
        try { store.Log(user, action + ": " + summary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (healthStore == null) return;
        try
        {
            healthStore.SaveIntervention(new InterventionRecord
            {
                Action = action, Target = string.Join(", ", ids), Authorized = true, Risk = "médio",
                State = result.Status switch { "Completed" => "concluída", "Partial" => "parcial", "Canceled" => "cancelada", _ => "incerta" },
                After = JsonSerializer.Serialize(result.Items), Summary = summary, RecoveryPointSequence = result.RestorePoint?.SequenceNumber,
            });
        }
        catch (OperationalStoreException ex) { debloatStatus += "\nO registro no histórico operacional falhou: " + ex.Message; }
    }
    // Componentes do Windows (#150): só pelo menu, nunca pelo modelo. Verificar só lê o registro e os arquivos; Instalar pede
    // confirmação com a lista e um UAC por item.
    private StackPanel RuntimeCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(SectionHeading("tools", RuntimePresentation.CardTitle));
        panel.Children.Add(Text("Muitos programas não abrem quando falta um Visual C++ Redistributable ou uma DLL do DirectX antigo (d3dx9_43.dll, xinput1_3.dll). Primeiro o Nexo verifica o que existe neste PC, sem alterar nada; depois instala só o que você marcar.", 13, "Secondary"));
        panel.Children.Add(Text(RuntimePresentation.CardNotice, 12, "Secondary"));
        runtimeProgressView = Text(runtimeStatus, 13, "Ink");
        panel.Children.Add(ToolStatus(runtimeProgressView));
        runtimeListView = new StackPanel();
        panel.Children.Add(runtimeListView);
        RenderRuntimeList();
        var actions = new WrapPanel();
        runtimeScanView = Action("Verificar este PC", async (_, _) => await GuardAsync(ScanRuntimes), icon: "search");
        runtimeInstallView = Action("Instalar selecionados", async (_, _) => await GuardAsync(InstallRuntimes), true);
        runtimeCancelView = Action("Cancelar próximas etapas", (_, _) => runtimeCancellation?.Cancel());
        actions.Children.Add(runtimeScanView); actions.Children.Add(runtimeInstallView); actions.Children.Add(runtimeCancelView);
        panel.Children.Add(actions);
        panel.Children.Add(Text("Um instalador já iniciado termina antes de o cancelamento valer. Se algum pedir reinício, o Nexo avisa e não reinicia o PC.", 11, "Subtle"));
        return panel;
    }

    private void RenderRuntimeList()
    {
        if (runtimeListView == null) return;
        runtimeListView.Children.Clear(); runtimeChecks.Clear();
        if (runtimeScan == null) return;
        foreach (var group in runtimeScan.Items.Where(s => s.State != RuntimeState.NotApplicable).GroupBy(s => s.Item.Kind))
        {
            var heading = Text(group.Key == RuntimeKind.DirectX ? "DirectX" : "Visual C++ Redistributable", 13); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 10, 0, 4);
            runtimeListView.Children.Add(heading);
            foreach (var status in group)
            {
                var line = RuntimePresentation.StatusLine(status);
                if (status.State == RuntimeState.Missing)
                {
                    var check = new CheckBox { Content = line, IsChecked = RuntimeCatalog.DefaultSelected(status), Margin = new Thickness(0, 4, 0, 0) };
                    AutomationProperties.SetName(check, line);
                    runtimeChecks[status.Item.Id] = check;
                    runtimeListView.Children.Add(check);
                }
                else { var installed = Text(line, 12, "Secondary"); installed.Margin = new Thickness(22, 4, 0, 0); runtimeListView.Children.Add(installed); }
                var detail = Text(status.Detail, 11, "Subtle"); detail.Margin = new Thickness(22, 0, 0, 2); runtimeListView.Children.Add(detail);
            }
        }
    }

    private async Task ScanRuntimes()
    {
        if (runtimeRunning) return;
        runtimeRunning = true; runtimeStatus = "Verificando o registro e os arquivos do Windows. Nada é alterado nesta etapa."; UpdateToolsView();
        try
        {
            runtimeScan = await Task.Run(() => RuntimeInspector.Scan(new WindowsRuntimeSystem()));
            runtimeStatus = RuntimePresentation.ScanText(runtimeScan);
        }
        finally { runtimeRunning = false; RenderRuntimeList(); UpdateToolsView(); }
    }

    private async Task InstallRuntimes()
    {
        if (runtimeRunning || runtimeScan == null) return;
        var items = runtimeScan.Missing.Where(s => runtimeChecks.TryGetValue(s.Item.Id, out var check) && check.IsChecked == true).Select(s => s.Item).ToArray();
        if (items.Length == 0) { runtimeStatus = "Nenhum item selecionado."; UpdateToolsView(); return; }
        if (MessageBox.Show(this, RuntimePresentation.ConfirmQuestion(items), "Confirmar instalação dos componentes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        runtimeCancellation = new CancellationTokenSource(); runtimeRunning = true;
        runtimeStatus = "Iniciando..."; UpdateToolsView();
        try
        {
            var auditPath = Path.Combine(store.Root, "runtimes-audit.ndjson");
            var progress = new Progress<string>(message => { if (runtimeRunning) { runtimeStatus = message; UpdateToolsView(); } });
            var results = await RuntimeInstaller.InstallAsync(new WindowsRuntimeSystem(), items, progress, item =>
            {
                lock (runtimeAuditSync) File.AppendAllText(auditPath, JsonSerializer.Serialize(item) + Environment.NewLine);
            }, runtimeCancellation.Token);
            runtimeRunning = false;
            runtimeStatus = RuntimePresentation.Result(results);
            RecordRuntimes(results);
            runtimeScan = null;
        }
        finally { runtimeRunning = false; runtimeCancellation.Dispose(); runtimeCancellation = null; RenderRuntimeList(); UpdateToolsView(); }
    }

    private void RecordRuntimes(IReadOnlyList<RuntimeInstallResult> results)
    {
        var summary = RuntimeInstaller.Summary(results);
        try { store.Log(user, "Componentes do Windows (Ferramentas): " + summary + " · " + string.Join("; ", results.Select(r => r.Item.Id + " " + r.Outcome + (r.ExitCode is { } code ? " (" + code + ")" : "")))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (healthStore == null) return;
        try
        {
            healthStore.SaveIntervention(new InterventionRecord
            {
                Action = "Instalar componentes do Windows (Ferramentas)", Target = string.Join(", ", results.Select(r => r.Item.Id)), Authorized = true, Risk = "baixo",
                State = RuntimeInstaller.State(results), RebootRequired = results.Any(r => r.Outcome == RuntimeOutcome.RebootPending),
                After = JsonSerializer.Serialize(results.Select(r => new { r.Item.Id, Outcome = r.Outcome.ToString(), r.ExitCode, r.Signer, r.Sha256, r.Message })), Summary = summary,
            });
        }
        catch (OperationalStoreException ex) { runtimeStatus += "\nO registro no histórico operacional falhou: " + ex.Message; }
    }
    // Aparência (#143): o tema muda na hora e a preferência fica no data.json deste perfil.
    private Border AppearanceCard()
    {
        var panel = new StackPanel();
        var heading = SectionHeading("settings", "Aparência"); heading.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(heading);
        panel.Children.Add(Text("O tema muda na hora e fica salvo neste perfil do Windows.", 13, "Secondary"));
        var options = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var theme in ThemeCatalog.Names)
        {
            var selected = ThemeManager.Current == theme;
            var option = Action(ThemeCatalog.DisplayName(theme), (_, _) => Guard(() => ApplyTheme(theme, persist: true)), selected);
            option.Tag = "theme:" + theme;
            AutomationProperties.SetName(option, "Tema " + ThemeCatalog.DisplayName(theme) + (selected ? ", selecionado" : ""));
            options.Children.Add(option);
        }
        panel.Children.Add(options);
        return Card(panel);
    }
    private void ApplyTheme(string theme, bool persist)
    {
        var previous = ThemeManager.Current;
        ThemeManager.Apply(theme);
        if (persist)
        {
            var stored = store.Data.Theme;
            store.Data.Theme = ThemeManager.Current;
            try { store.Save(); }
            catch { store.Data.Theme = stored; ThemeManager.Apply(previous); throw; }
        }
        RefreshThemeButton();
        if (page == "settings") ShowPage("settings");
    }
    private void Settings()
    {
        Heading("Configurações", "Um agente local conectado às ferramentas deste computador.", "Preferências deste perfil · Nexo " + AppVersion.Current);
        PageContent.Children.Add(AppearanceCard());
        var panel = new StackPanel(); var agentHeading = SectionHeading("chat", "Agente de suporte · Ollama"); agentHeading.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(agentHeading);
        panel.Children.Add(Text("A IA escolhe ferramentas, consulta o Windows, solicita as alterações necessárias e verifica os resultados. O modelo recebe o histórico recente e as saídas das ferramentas no serviço local 127.0.0.1:11434.", 14, "Secondary"));
        var enabled = Check(panel, "Usar agente IA para conduzir os atendimentos", store.Data.AiEnabled); var model = Field(panel, "Modelo com suporte a ferramentas", store.Data.Model, mono: true);
        panel.Children.Add(Text("Dispositivos, Bluetooth, áudio, câmera, impressoras, IP, DNS, serviços, processos, logs e drivers do Windows Update. Alterações no Windows continuam exigindo autorização; operações elevadas também passam pelo UAC.", 13, "Secondary"));
        var save = Action("Salvar configuração", (_, _) => Guard(() => { store.Require(user, a => a.Id == user.Id); if (enabled.IsChecked == true && string.IsNullOrWhiteSpace(model.Text)) throw new InvalidOperationException("Informe o nome do modelo instalado."); var previous = (store.Data.Model, store.Data.AiEnabled); store.Commit(user, "Configuração de IA atualizada", () => { store.Data.Model = model.Text.Trim(); store.Data.AiEnabled = enabled.IsChecked == true; }, () => { store.Data.Model = previous.Model; store.Data.AiEnabled = previous.AiEnabled; }); RefreshIdentity(); MessageBox.Show(this, "Configuração salva. O agente será utilizado no próximo atendimento."); }), true);
        // #143: Salvar (primário) e Verificar (secundário) lado a lado, com os 10 px de margem direita do Action entre eles.
        var agentActions = new WrapPanel { Margin = new Thickness(0, 22, 0, 0) }; agentActions.Children.Add(save); panel.Children.Add(agentActions);
        var connectionStatus = Text("", 13, "Accent"); connectionStatus.Margin = new Thickness(0, 8, 0, 0);
        agentActions.Children.Add(Action("Verificar modelo local", async (_, _) =>
        {
            try
            {
                connectionStatus.Text = "Conectando ao Ollama…"; await LocalModel.EnsureRunning(CancellationToken.None);
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var response = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http, "http://127.0.0.1:11434/api/show", new { model = model.Text.Trim() }); response.EnsureSuccessStatusCode();
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var supported = result.RootElement.GetProperty("capabilities").EnumerateArray().Any(v => v.GetString() == "tools");
                connectionStatus.Text = supported ? "Modelo disponível e compatível com ferramentas." : "Este modelo não informa suporte a ferramentas. Use qwen3:8b.";
            }
            catch (Exception ex) { connectionStatus.Text = "Não foi possível verificar: " + ex.Message; }
        })); panel.Children.Add(connectionStatus); PageContent.Children.Add(Card(panel));
        var healthPanel = new StackPanel(); var monitorHeading = SectionHeading("health", "Monitoramento local"); monitorHeading.Margin = new Thickness(0, 0, 0, 8); healthPanel.Children.Add(monitorHeading);
        healthPanel.Children.Add(Text("A coleta funciona enquanto o Nexo está em execução. A leitura de temperatura usa um componente integrado; se o hardware ou o acesso ao sensor não permitir a leitura, ela aparece como indisponível. O estado dos discos é o informado pelo Windows, não um SMART completo.", 13, "Secondary"));
        var backgroundMode = Check(healthPanel, "Continuar monitorando em segundo plano ao fechar a janela", healthSettings.BackgroundMode);
        healthPanel.Children.Add(Text("Com esta opção, o Nexo fica na bandeja do Windows. Use 'Abrir Nexo' para voltar ou 'Encerrar Nexo' para parar a coleta. O monitor não executa correções sozinho.", 13, "Secondary"));
        // #143: só os limites que existem em HealthSettings (CPU, espaço livre e memória por processo); não há limite de memória em %.
        healthPanel.Children.Add(PageKit.Group("Coleta"));
        var interval = Field(healthPanel, "Intervalo entre amostras (segundos: 10–3600)", healthSettings.IntervalSeconds.ToString(), mono: true);
        var retention = Field(healthPanel, "Retenção do histórico operacional (dias: 1–365)", healthSettings.RetentionDays.ToString(), mono: true);
        healthPanel.Children.Add(PageKit.Group("Limites de alerta"));
        var cpu = Field(healthPanel, "Alerta de CPU persistente (%)", healthSettings.CpuHighPercent.ToString("F0"), mono: true);
        var disk = Field(healthPanel, "Alerta de espaço livre abaixo de (%)", healthSettings.DiskLowFreePercent.ToString("F0"), mono: true);
        var processGrowth = Field(healthPanel, "Alerta de crescimento de memória por processo (pontos da RAM: 1–50)", healthSettings.ProcessMemoryGrowthPoints.ToString(), mono: true);
        var processSamples = Field(healthPanel, "Leituras seguidas para esse alerta (3–19)", healthSettings.ProcessMemorySamples.ToString(), mono: true);
        healthPanel.Children.Add(PageKit.Group("Sondagens de rede (opcionais)", 26));
        healthPanel.Children.Add(Text("Ao ativar DNS ou destino externo, o Nexo envia consultas aos endereços abaixo. Um teste sem resposta não confirma a causa da falha.", 13, "Secondary"));
        var cadence = Text("As sondagens ligadas rodam a cada leitura do monitor (hoje, a cada " + healthSettings.IntervalSeconds + " s).", 13, "Secondary");
        cadence.Margin = new Thickness(0, 6, 0, 0); healthPanel.Children.Add(cadence);
        var checkGateway = Check(healthPanel, "Verificar gateway por ICMP", healthSettings.CheckGateway);
        var checkDns = Check(healthPanel, "Verificar DNS do nome configurado", healthSettings.CheckDns);
        var checkExternal = Check(healthPanel, "Verificar destino externo por HTTP(S)", healthSettings.CheckExternal);
        foreach (var option in new[] { checkGateway, checkDns, checkExternal }) option.Margin = new Thickness(0, 10, 0, 0);
        var dnsHost = Field(healthPanel, "Nome para consulta DNS", healthSettings.DnsProbeHost, mono: true);
        var httpUrl = Field(healthPanel, "URL do destino externo", healthSettings.HttpProbeUrl, mono: true);
        var expectedText = Field(healthPanel, "Texto esperado da resposta HTTP", healthSettings.HttpExpectedText);
        void RefreshProbeFields()
        {
            dnsHost.IsEnabled = checkDns.IsChecked == true;
            httpUrl.IsEnabled = expectedText.IsEnabled = checkExternal.IsChecked == true;
        }
        checkDns.Checked += (_, _) => RefreshProbeFields(); checkDns.Unchecked += (_, _) => RefreshProbeFields();
        checkExternal.Checked += (_, _) => RefreshProbeFields(); checkExternal.Unchecked += (_, _) => RefreshProbeFields();
        RefreshProbeFields();
        healthPanel.Children.Add(Action("Salvar monitoramento", async (_, _) => await GuardAsync(async () =>
        {
            if (!int.TryParse(interval.Text, out var intervalValue) || !HealthSettings.TryParsePercent(cpu.Text, System.Globalization.CultureInfo.CurrentCulture, out var cpuValue)
                || !HealthSettings.TryParsePercent(disk.Text, System.Globalization.CultureInfo.CurrentCulture, out var diskValue) || !int.TryParse(retention.Text, out var retentionValue)
                || !HealthSettings.TryParseGrowthPoints(processGrowth.Text, System.Globalization.CultureInfo.CurrentCulture, out var processGrowthValue) || !int.TryParse(processSamples.Text, out var processSamplesValue))
                throw new InvalidOperationException("Informe números válidos para as preferências de saúde.");
            var next = new HealthSettings { IntervalSeconds = intervalValue, CpuHighPercent = cpuValue, DiskLowFreePercent = diskValue,
                RetentionDays = retentionValue, CheckGateway = checkGateway.IsChecked == true, CheckDns = checkDns.IsChecked == true,
                CheckExternal = checkExternal.IsChecked == true, DnsProbeHost = dnsHost.Text.Trim(), HttpProbeUrl = httpUrl.Text.Trim(),
                HttpExpectedText = expectedText.Text.Trim(), ProcessMemoryGrowthPoints = processGrowthValue,
                ProcessMemorySamples = processSamplesValue, BackgroundMode = backgroundMode.IsChecked == true };
            next.Save(store.Root);
            if (healthMonitor != null) { await healthMonitor.StopAsync(); healthMonitor.Dispose(); healthMonitor = null; }
            healthSettings = next; healthStore?.Prune(TimeSpan.FromDays(next.RetentionDays)); StartHealthMonitoring();
            if (next.BackgroundMode) EnsureTrayIcon(); else DisposeTrayIcon();
            MessageBox.Show(this, "Preferências do monitoramento salvas.", "Nexo");
        }), true));
        PageContent.Children.Add(Card(healthPanel));
        var about = new StackPanel();
        about.Children.Add(PageKit.Row("Versão do Nexo", AppVersion.Current, mono: true));
        about.Children.Add(PageKit.Row("Tema", ThemeCatalog.DisplayName(ThemeManager.Current)));
        var local = Text("Atendimentos e registros ficam neste perfil do Windows. A administração central de vários computadores e a integração com uma ferramenta de chamados são próximas etapas do produto.", 13, "Secondary");
        local.Margin = new Thickness(0, 12, 0, 0); about.Children.Add(local);
        PageContent.Children.Add(Card(about));
    }
    private void Audits()
    {
        Heading("Registro de ações", "Acessos, diagnósticos, autorizações e correções registrados localmente.", "Auditoria local · somente leitura");
        var metrics = SupportMetrics.Calculate(store.Data);
        var metricsPanel = new StackPanel(); var metricsHeading = SectionHeading("performance", "Métricas dos atendimentos locais");
        metricsHeading.Margin = new Thickness(0, 0, 0, 10); metricsPanel.Children.Add(metricsHeading);
        if (metrics.Closed == 0)
            metricsPanel.Children.Add(Text("Nenhum atendimento encerrado ainda. As taxas aparecem após o usuário confirmar resolução ou preparar um encaminhamento.", 14, "Secondary"));
        else
        {
            var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 4, -10, 0) };
            foreach (var tile in new[] { MetricTile("Encerrados", metrics.Closed.ToString(), "history"), MetricTile("Resolvidos pelo usuário", $"{metrics.Resolved} ({metrics.ResolutionRate:P0})", "check"),
                MetricTile("Encaminhados", $"{metrics.Escalated} ({metrics.EscalationRate:P0})", "tickets"), MetricTile("Tempo médio até o encerramento", $"{metrics.AverageClosureMinutes:F1} min", "performance") })
            { tile.Margin = new Thickness(0, 0, 10, 10); tiles.Children.Add(tile); }
            metricsPanel.Children.Add(tiles);
            metricsPanel.Children.Add(Text($"Em andamento: {metrics.Open}", 14, "Secondary"));
        }
        PageContent.Children.Add(Card(metricsPanel));
        var audits = store.Data.Audit.OrderByDescending(a => a.Time).Take(200).ToList();
        if (audits.Count == 0) { PageContent.Children.Add(EmptyState("audit", "Nenhuma ação registrada", "Acessos, diagnósticos, autorizações e correções aparecem aqui quando forem registrados.")); return; }
        // Tabela única (#143): data e hora em mono, ação e autor; antes, um cartão por registro.
        var table = new StackPanel();
        Grid TableRow(UIElement time, UIElement action, UIElement actor)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            var line = new Border { BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(0, 0, 0, 1) }; Grid.SetColumnSpan(line, 3); row.Children.Add(line);
            foreach (var (cell, column) in new[] { (time, 0), (action, 1), (actor, 2) })
            { if (cell is FrameworkElement element) element.Margin = new Thickness(column == 0 ? 0 : 10, 9, 0, 9); Grid.SetColumn(cell, column); row.Children.Add(cell); }
            return row;
        }
        table.Children.Add(TableRow(Mono("DATA E HORA", 11, "Subtle"), Mono("AÇÃO", 11, "Subtle"), Mono("AUTOR", 11, "Subtle")));
        foreach (var audit in audits)
        {
            var action = Text(audit.Action); action.FontWeight = FontWeights.SemiBold;
            var row = TableRow(Mono(audit.Time.ToString("g"), 12, "Secondary"), action, Text(audit.Actor, 13, "Secondary"));
            AutomationProperties.SetName(row, audit.Time.ToString("g") + ", " + audit.Action + ", " + audit.Actor);
            table.Children.Add(row);
        }
        var tableHeading = SectionHeading("audit", "Atividade registrada"); tableHeading.Margin = new Thickness(0, 0, 0, 6);
        var tablePanel = new StackPanel(); tablePanel.Children.Add(tableHeading);
        tablePanel.Children.Add(Mono(audits.Count == 1 ? "1 registro" : audits.Count + " registros mais recentes", 12, "Secondary"));
        tablePanel.Children.Add(table);
        PageContent.Children.Add(Card(tablePanel));
    }
    public async void CapturePreview()
    {
        // Use only the isolated preview store selected by App; never authenticate against production data.
        static bool HasVectorIcon(DependencyObject root)
        {
            if (root is System.Windows.Shapes.Path) return true;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (HasVectorIcon(VisualTreeHelper.GetChild(root, i))) return true;
            return false;
        }
        static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T found) yield return found;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }
        // #143: com "--theme glacial", toda captura da prévia (telas e diálogos) ganha o prefixo do tema e não sobrescreve as do Ônix.
        static string PreviewFile(string name) => ThemeManager.Current == ThemeCatalog.Onyx ? name : ThemeManager.Current + "-" + name;
        async Task Capture(string name, bool themed = true)
        {
            await Task.Delay(200); UpdateLayout();
            var visual = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var file = themed ? PreviewFile(name) : name;
            using var output = File.Create(Path.Combine(AppContext.BaseDirectory, file)); encoder.Save(output);
        }
        try
        {
        if (((Grid)Content).Background is not SolidColorBrush shellBrush || shellBrush.Color != ((SolidColorBrush)ThemeManager.Live("CanvasBg")).Color)
            throw new InvalidOperationException("O tema " + ThemeCatalog.DisplayName(ThemeManager.Current) + " não foi aplicado à janela principal.");
        if (!HasVectorIcon(ChatNav) || !HasVectorIcon(HomeNav)) throw new InvalidOperationException("A navegação não usa ícones vetoriais.");
        if (Shell.Visibility != Visibility.Visible || !Messages.Children.OfType<StackPanel>().Any()) throw new InvalidOperationException("Falha na abertura direta do aplicativo.");
        // #143: o Nexo abre na Início com o Assistente recolhido, e um botão recolhido não tem o modelo visual aplicado.
        // Os atalhos do Assistente são conferidos com ele visível, e a prévia volta à Início antes das checagens do shell.
        ShowPage("chat"); UpdateLayout();
        var shortcutGrid = Messages.Children.OfType<StackPanel>().SelectMany(s => s.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>()).FirstOrDefault();
        if (shortcutGrid?.Children.Count != 6 || shortcutGrid.Children.OfType<Button>().Any(button => !HasVectorIcon(button)))
            throw new InvalidOperationException("Os seis atalhos iniciais precisam de ícones vetoriais.");
        ShowPage("home"); UpdateLayout();
        if (Sidebar.ActualWidth < 220) throw new InvalidOperationException("A navegação ficou estreita para o novo layout.");
        // #143, etapas 2 e 3: tela Início de abertura, atalhos, paleta Ctrl+K, barra de título e barra lateral.
        await VerifyShellPreview(name => Capture(name));
        ShowPage("chat"); await Capture("nexo-preview.png");
        Width = 1060; Height = 720; UpdateLayout(); await Capture("nexo-preview-small.png");
        Width = 1380; Height = 900; UpdateLayout();
        ShowPage("health");
        if (!PageContent.Children.OfType<TextBlock>().Any(t => t.Text == "Saúde do computador")) throw new InvalidOperationException("Painel de saúde não abriu.");
        if (!PageContent.Children.OfType<StackPanel>().Any(section => section.Children.OfType<TextBlock>().Any(t => t.Text == "Ocorrências preventivas")
            && section.Children.OfType<Viewbox>().Any()))
            throw new InvalidOperationException("As seções de saúde precisam de cabeçalhos com ícones vetoriais.");
        await Capture("nexo-health.png");
        if (healthStore != null)
        {
            healthStore.SaveSnapshot(new HealthSnapshot { CpuPercent = 37, MemoryUsedPercent = 54, Connectivity = "local",
                GatewayProbe = new NetworkProbeReading("no_response", "192.0.2.1"),
                DnsProbe = new NetworkProbeReading("ok", "example.com"),
                ExternalProbe = new NetworkProbeReading("unexpected", "example.com"),
                CpuTemperatureC = 52, CpuTemperature = new TemperatureReading("read", 52, "CPU Package (/intelcpu/0/temperature/0)", HardwareSensors.TemperatureSource, DateTimeOffset.UtcNow),
                StorageHealth = "warning", Storage = new StorageReading("warning", HardwareSensors.StorageSource, DateTimeOffset.UtcNow, 2,
                    [new PhysicalDiskReading("1", "Disco de exemplo HDD", "HDD", "SATA", "Warning", ["Predictive Failure"], "warning"), new PhysicalDiskReading("0", "Disco de exemplo NVMe", "SSD", "NVMe", "Healthy", ["OK"], "healthy")]),
                CollectionStatus = "complete", Disks = [new DiskHealth("C:\\", 80, 200)] });
            ShowPage("health");
            if (!new[] { "Temperatura da CPU: 52 °C", "Discos físicos: atenção informada pelo Windows", "Disco 1 Disco de exemplo HDD", "não é uma leitura SMART completa" }.All(label => Descendants<TextBlock>(PageContent).Any(text => text.Text.Contains(label))))
                throw new InvalidOperationException("O painel não mostra temperatura, estado dos discos e o limite da leitura do Windows.");
            if (!new[] { "Gateway:", "DNS:", "Destino externo:" }.All(label => Descendants<TextBlock>(PageContent).Any(text => text.Text.Contains(label))))
                throw new InvalidOperationException("O painel não separa gateway, DNS e destino externo.");
            await Capture("nexo-health-data.png");
            Width = 1060; Height = 720; UpdateLayout(); await Capture("nexo-health-data-small.png");
            Width = 1380; Height = 900; UpdateLayout();
            var occurrenceSample = DateTimeOffset.UtcNow;
            for (var i = 0; i < 3; i++)
                healthStore.SaveSnapshot(new HealthSnapshot { Time = occurrenceSample.AddMilliseconds(i), CpuPercent = 95, MemoryUsedPercent = 54, Connectivity = "local",
                    CollectionStatus = "complete", Disks = [new DiskHealth("C:\\", 80, 200)] });
            HealthFinding Occurrence(string id, string severity, string title) => new() { Id = id + ":preview", Time = occurrenceSample.AddMinutes(-5), Severity = severity, Problem = title,
                Evidence = "Leituras simuladas da prévia", Cause = "Exemplo da prévia", Recommendation = "Nenhuma ação: dados de exemplo" };
            healthStore.SaveFindings([Occurrence("cpu-high", "warning", "Uso persistente de CPU"), Occurrence("disk-low:C:\\", "critical", "Pouco espaço no volume C:\\"),
                Occurrence("service-stopped:Spooler", "warning", "Serviço automático interrompido")]);
            ShowPage("health");
            var occurrenceTexts = Descendants<TextBlock>(PageContent).ToArray();
            foreach (var expected in new[] { "Última coleta", "Persiste nas leituras atuais", "Resolvida:", "estado atual desconhecido" })
                if (!occurrenceTexts.Any(text => text.Text.Contains(expected))) throw new InvalidOperationException("O painel não mostra o estado da ocorrência: " + expected);
            async Task CaptureAt(string text, string file, double extraScroll = 0)
            {
                UpdateLayout(); occurrenceTexts.First(item => item.Text.Contains(text)).BringIntoView();
                if (extraScroll > 0) { UpdateLayout(); PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset + extraScroll); }
                await Capture(file);
            }
            await CaptureAt("Última coleta", "nexo-health-occurrences.png");
            await CaptureAt("Uso persistente de CPU", "nexo-health-occurrences-active.png", 100);
            Width = 1060; Height = 720;
            await CaptureAt("Última coleta", "nexo-health-occurrences-small.png");
            await CaptureAt("Uso persistente de CPU", "nexo-health-occurrences-active-small.png", 100);
            Width = 1380; Height = 900; UpdateLayout();
        }
        ShowPage("settings");
        if (!PageContent.Children.OfType<Border>().Any(b => b.Child is StackPanel s && s.Children.OfType<StackPanel>().Any(h => h.Children.OfType<TextBlock>().Any(t => t.Text == "Monitoramento local")))) throw new InvalidOperationException("Controles de monitoramento não abriram.");
        if (!Descendants<CheckBox>(PageContent).Any(check => check.Content?.ToString()?.Contains("segundo plano") == true && check.IsChecked == false))
            throw new InvalidOperationException("A opção da bandeja precisa começar desligada.");
        if (!new[] { "Verificar gateway", "Verificar DNS", "Verificar destino externo" }.All(label => Descendants<CheckBox>(PageContent).Any(check => check.Content?.ToString()?.Contains(label) == true && check.IsChecked == false)))
            throw new InvalidOperationException("As três sondagens precisam de opções independentes desligadas por padrão.");
        await Capture("nexo-health-settings.png");
        // #143: a troca de tema atualiza a janela e o texto já criado na conversa, sem gravar a preferência na prévia.
        var themeOptions = Descendants<Button>(PageContent).Where(button => (button.Tag as string)?.StartsWith("theme:", StringComparison.Ordinal) == true).ToArray();
        if (themeOptions.Length != ThemeCatalog.Names.Count || themeOptions.Count(button => ReferenceEquals(button.Style, FindResource("Primary"))) != 1)
            throw new InvalidOperationException("A aparência precisa mostrar os dois temas, com o atual destacado.");
        var originalTheme = ThemeManager.Current; var storedTheme = store.Data.Theme;
        var otherTheme = originalTheme == ThemeCatalog.Onyx ? ThemeCatalog.Glacial : ThemeCatalog.Onyx;
        var shellBefore = ((SolidColorBrush)((Grid)Content).Background).Color;
        var chatText = Descendants<TextBlock>(Messages).First(text => text.Foreground is SolidColorBrush { IsFrozen: false });
        var chatBefore = ((SolidColorBrush)chatText.Foreground).Color;
        ApplyTheme(otherTheme, persist: false); UpdateLayout();
        if (((SolidColorBrush)((Grid)Content).Background).Color == shellBefore || ((SolidColorBrush)chatText.Foreground).Color == chatBefore || store.Data.Theme != storedTheme)
            throw new InvalidOperationException("A troca de tema não atualizou a janela e o texto já criado, ou gravou a preferência na prévia.");
        // O nome diz a troca (origem e destino), não o tema de destino. Configurações é redesenhada pelo ApplyTheme (para destacar o
        // tema escolhido), então a 1ª captura é igual à da tela aberta no outro tema; a 2ª é a conversa, que não é recriada e prova a
        // recoloração ao vivo do texto já criado.
        var switchName = "nexo-theme-switch-" + originalTheme + "-to-" + otherTheme;
        await Capture(switchName + ".png", themed: false);
        ShowPage("chat"); await Capture(switchName + "-chat.png", themed: false); ShowPage("settings");
        ApplyTheme(originalTheme, persist: false); UpdateLayout();
        Width = 1060; Height = 720; UpdateLayout(); await Capture("nexo-health-settings-small.png");
        Width = 1380; Height = 900; UpdateLayout();
        ShowPage("tools");
        var toolButtons = Descendants<Button>(PageContent).Select(b => b.Content?.ToString()).ToArray();
        if (!toolButtons.Contains("Instalar Office") || !toolButtons.Contains("Otimizar PC") ||
            !Descendants<TextBlock>(PageContent).Any(t => t.Text.Contains("A licença por volume e a ativação são responsabilidade da TI")))
            throw new InvalidOperationException("Ferramentas e aviso de licença não abriram.");
        await Capture("nexo-tools.png");
        Width = 1060; Height = 720; UpdateLayout(); await Capture("nexo-tools-small.png");
        Width = 980; Height = 680; UpdateLayout(); await Capture("nexo-tools-compact.png");
        Width = 1380; Height = 900; UpdateLayout();
        foreach (var (target, file) in new[] { ("history", "nexo-history-empty.png"), ("tickets", "nexo-tickets.png"),
            ("audit", "nexo-audit.png"), ("profile", "nexo-profile.png") })
        {
            ShowPage(target);
            if (PageContent.Children.Count == 0) throw new InvalidOperationException("A página " + target + " está vazia.");
            await Capture(file);
        }
        ShowPage("chat");
        store.Data.AiEnabled = false;
        NewConversation(); ShowPage("chat"); await Send("Olá, preciso de ajuda");
        if (conversation?.Entries.Count != 2 || busy) throw new InvalidOperationException("Falha no fluxo de conversa local.");
        await Capture("nexo-conversation.png");
        ShowPage("history"); await Capture("nexo-history.png"); ShowPage("chat");
        await VerifyHomeData(name => Capture(name));
        AddEntry("assistant", "Deseja que eu ative a conexão?");
        if (quickReplyPanel?.Children.Count != 2) throw new InvalidOperationException("Atalhos não apareceram na pergunta de confirmação.");
        Composer.Text = "Meu rascunho";
        var staleButton = (Button)quickReplyPanel.Children[0];
        staleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        while (busy) await Task.Delay(25);
        if (conversation.Entries.Last(e => e.Role == "user").Text != "Sim, por favor" || Composer.Text != "Meu rascunho") throw new InvalidOperationException("O atalho não enviou a resposta ou apagou o rascunho.");
        var entryCount = conversation.Entries.Count;
        staleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (conversation.Entries.Count != entryCount) throw new InvalidOperationException("Uma resposta antiga foi enviada duas vezes.");
        AddEntry("assistant", "Deseja que eu execute o teste novamente?");
        ((Button)quickReplyPanel!.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        while (busy) await Task.Delay(25);
        if (conversation.Entries.Last(e => e.Role == "user").Text != "Não") throw new InvalidOperationException("O atalho negativo não enviou a resposta.");
        AddEntry("assistant", "Qual aplicativo está com problema?");
        if (quickReplyPanel != null) throw new InvalidOperationException("Uma pergunta aberta recebeu atalhos incorretos.");
        AddEntry("assistant", "Funcionou?");
        if (((Button)quickReplyPanel!.Children[1]).Content.ToString() != "Ainda não") throw new InvalidOperationException("Atalhos de resultado incorretos.");
        Composer.Clear();
        NewConversation(); Messages.Children.Clear();
        store.Data.Conversations.Add(conversation!);
        AddEntry("user", "Estou sem internet. Pode verificar a conexão?");
        AddEntry("assistant", "Deseja que eu verifique a conexão?");
        await Capture("nexo-quick-replies.png");
        var inputSource = PresentationSource.FromVisual(Composer)!;
        var enter = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        var beforeKeyboard = conversation!.Entries.Count;
        Composer.RaiseEvent(enter);
        if (enter.Handled || conversation.Entries.Count != beforeKeyboard || !Composer.AcceptsReturn) throw new InvalidOperationException("Enter deve permanecer disponível para quebra de linha.");
        store.Data.AiEnabled = false;
        Composer.Text = "Mensagem\ncom duas linhas";
        var shiftEnter = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        await HandleComposerKey(shiftEnter, ModifierKeys.Shift);
        if (!shiftEnter.Handled || conversation.Entries.Last(e => e.Role == "user").Text != "Mensagem\ncom duas linhas" || Composer.Text.Length != 0) throw new InvalidOperationException("Shift + Enter não enviou a mensagem multilinha.");
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "ACOMPANHAMENTO";
        for (var i = 0; i < 70; i++) UpdateProgress($"Etapa {i + 1}: verificando interface, IP e gateway…");
        BusyBar.Visibility = CancelButton.Visibility = Visibility.Visible;
        foreach (var size in new[] { (1380d, 900d), (1060d, 720d) })
        {
            Width = size.Item1; Height = size.Item2; UpdateLayout();
            await Task.Delay(100);
            if (ProgressScroll.ScrollableHeight <= 0 || ProgressScroll.ViewportHeight <= 0) throw new InvalidOperationException("Etapas sem área de rolagem.");
            ProgressScroll.ScrollToTop(); UpdateLayout();
            if (ProgressScroll.VerticalOffset != 0) throw new InvalidOperationException("Não foi possível voltar às primeiras etapas.");
            ProgressScroll.ScrollToEnd(); UpdateLayout();
            if (ProgressScroll.VerticalOffset <= 0) throw new InvalidOperationException("Não foi possível rolar às últimas etapas.");
            var ticketTop = TicketButton.TranslatePoint(new Point(), ChatView).Y;
            var ticketRight = TicketButton.TranslatePoint(new Point(TicketButton.ActualWidth, 0), ChatView).X;
            var composerTop = Composer.TranslatePoint(new Point(), ChatView).Y;
            var newChatRight = NewChatButton.TranslatePoint(new Point(NewChatButton.ActualWidth, 0), Shell).X;
            if (ticketTop >= composerTop || ticketRight > ChatView.ActualWidth || newChatRight > Shell.ColumnDefinitions[0].ActualWidth || CancelButton.ActualHeight <= 0)
                throw new InvalidOperationException("Ações da conversa ou novo atendimento fora da posição esperada.");
            var labelPositions = Navigation.Children.OfType<Button>().Where(b => b.IsVisible).Select(b => ((StackPanel)b.Content).Children.OfType<TextBlock>().Last().TranslatePoint(new Point(), Navigation).X).ToArray();
            if (labelPositions.Max() - labelPositions.Min() > 1) throw new InvalidOperationException("Rótulos da navegação desalinhados: " + string.Join(", ", labelPositions.Select(value => value.ToString("F1"))));
            await Capture(size.Item1 == 1380 ? "nexo-progress.png" : "nexo-progress-small.png");
        }
        Width = 1380; Height = 900;
        NewConversation(); Messages.Children.Clear();
        AddEntry("user", "Atualize os drivers da minha placa de vídeo");
        AddEntry("tool", ToolCatalog.Label("get_hardware_inventory") + "\n" + WindowsTools.Ok(new { Devices = new[] { new Device("GPU de exemplo", "PCI\\GPU1", 0, "OK") { DriverVersion = "1.0" } } }));
        AddEntry("tool", ToolCatalog.Label("scan_driver_updates") + "\n" + WindowsTools.Ok("[]"));
        AddEntry("assistant", "Identifiquei GPU de exemplo (driver 1.0). A fonte de atualizações configurada no Windows não ofereceu driver compatível agora. Nenhuma alteração foi feita.", "result", 14300);
        await Capture("nexo-driver-result.png");
        var originalName = user.Name;
        try
        {
            user.Name = string.Join(' ', Enumerable.Repeat("NomeExtensoDeExemplo", 12));
            NewConversation(); Messages.Children.Clear();
            AddEntry("user", "Conferir alinhamento do cabeçalho com um nome longo.");
            Width = 1060; Height = 720; UpdateLayout();
            var longCard = Messages.Children.OfType<Border>().Last();
            var longByline = ((StackPanel)longCard.Child).Children[0] as FrameworkElement;
            if (longByline == null || longByline.ActualWidth > longCard.ActualWidth - 24)
                throw new InvalidOperationException("Nome longo ultrapassou o cartão da conversa.");
            await Capture("nexo-long-name-small.png");
            conversation!.Title = string.Join(' ', Enumerable.Repeat("DiagnósticoDeExemplo", 10));
            store.Data.Conversations.Add(conversation);
            ShowPage("history"); UpdateLayout();
            var historyCard = PageContent.Children.OfType<Border>().FirstOrDefault(card => card.Child is StackPanel row
                && row.Children.OfType<TextBlock>().Any(title => title.Text == conversation.Title));
            var historyButton = ((StackPanel?)historyCard?.Child)?.Children.OfType<Button>().FirstOrDefault();
            if (historyCard == null || historyButton == null || historyButton.TranslatePoint(new Point(historyButton.ActualWidth, historyButton.ActualHeight), historyCard).X > historyCard.ActualWidth)
                throw new InvalidOperationException("Título longo cortou a ação do atendimento.");
            await Capture("nexo-long-history-small.png");
            ShowPage("chat");
            AddEntry("assistant", string.Join(' ', Enumerable.Repeat("Resultado de exemplo com explicação detalhada.", 25)), "result");
            UpdateLayout();
            if (TicketButton.ActualHeight < 30 || TicketButton.TranslatePoint(new Point(TicketButton.ActualWidth, 0), ChatView).X > ChatView.ActualWidth)
                throw new InvalidOperationException("Resultado longo cortou as ações da conversa.");
            await Capture("nexo-long-result-small.png");
        }
        finally { user.Name = originalName; }
        Width = 1380; Height = 900; UpdateLayout();
        foreach (var (request, file) in new[]
        {
            (new ChangeRequest("Confirmar ação de exemplo", string.Join('\n', Enumerable.Repeat("Detalhe de autorização para leitura e rolagem.", 24))), "nexo-confirmation.png"),
            (new ChangeRequest("Instalar driver com licença do fornecedor", "Leia a licença antes de autorizar.", LicenseUrl: "https://example.com/license"), "nexo-confirmation-license.png"),
            (new ChangeRequest("Manter a configuração de rede após a verificação", "Teste a conexão e confirme a escolha.", KeepNetwork: true), "nexo-confirmation-network.png")
        })
        {
            var accepted = await ConfirmChange(request, dialog =>
            {
                var panel = (DockPanel)((Border)dialog.Content).Child;
                var buttons = panel.Children.OfType<WrapPanel>().Single();
                var options = buttons.Children.OfType<Button>().ToArray();
                var details = panel.Children.OfType<Border>().Single();
                var visual = (FrameworkElement)dialog.Content;
                if (options.Length != 2 || options.Any(button => button.ActualHeight < 30
                    || button.TranslatePoint(new Point(button.ActualWidth, button.ActualHeight), dialog).X > dialog.ActualWidth
                    || button.TranslatePoint(new Point(button.ActualWidth, button.ActualHeight), visual).Y > visual.ActualHeight - 8)
                    || details.ActualHeight < 100)
                    throw new InvalidOperationException("Ações ou detalhes do diálogo de autorização estão inacessíveis.");
                var license = panel.Children.OfType<StackPanel>().SelectMany(section => section.Children.OfType<CheckBox>()).SingleOrDefault();
                if (request.LicenseUrl != null)
                {
                    if (license == null || options[1].IsEnabled) throw new InvalidOperationException("Licença não bloqueou a autorização.");
                    license.IsChecked = true;
                    if (!options[1].IsEnabled) throw new InvalidOperationException("Aceite da licença não habilitou a autorização.");
                    license.IsChecked = false;
                    if (options[1].IsEnabled) throw new InvalidOperationException("Revogação da licença não bloqueou a autorização.");
                    license.IsChecked = true;
                }
                else if (license != null || !options[1].IsEnabled) throw new InvalidOperationException("A autorização comum ficou indisponível.");
                visual.UpdateLayout();
                // #143: o cartão tem margem de 16 px na janela e o RenderTargetBitmap desenha o visual já deslocado por ela, o que cortava a borda
                // da direita e de baixo. A captura usa a raiz da área cliente do diálogo (sem deslocamento), com o cartão inteiro e a margem.
                var root = (FrameworkElement)VisualTreeHelper.GetChild(dialog, 0);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(AppContext.BaseDirectory, PreviewFile(file))); encoder.Save(output);
            });
            if (accepted) throw new InvalidOperationException("A prévia não cancelou o diálogo de autorização.");
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-checks.txt"), "Abertura direta e navegação, envio e resposta local: aprovados.\nPainel de saúde e controles de monitoramento renderizados.\nNovo atendimento na lateral; resolução e encaminhamento acima do compositor: aprovados em 1380x900 e 1060x720.\nAtalhos Sim/Não, rascunho e bloqueio de resposta antiga: aprovados.\nEnter livre para quebra de linha e manipulador Shift + Enter envia mensagem multilinha: aprovados.\nNomes, títulos e resultados longos verificados em 1060x720.\nDiálogos de autorização, licença e restauração de rede renderizados e cancelados sem mudanças no Windows.\n70 etapas com rolagem ao início/fim, controles acessíveis e rótulos alinhados.\nInício de abertura com estados de vazio e dados reais, atalhos que escrevem no Assistente sem enviar, paleta Ctrl+K (filtro, setas, Enter, Esc e ações), barra de título (botões, maximizar/restaurar, área do snap layouts), lateral recolhida e nenhum texto abaixo de 11 px: aprovados. Nenhuma ação no Windows executada.");
        Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-checks.txt"), "Falha na prévia: " + ex);
            Application.Current.Shutdown(1);
        }
    }
}
