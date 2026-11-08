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
    private string page = "chat";
    private WindowsTools? conversationTools;
    private WrapPanel? quickReplyPanel;
    private OperationalStore? healthStore;
    private MonitoringEngine? healthMonitor;
    private HealthSettings healthSettings = new();
    private string? healthError;
    private List<string> eventWarnings = [];
    private DateTimeOffset lastEventScan = DateTimeOffset.MinValue;
    private DateTimeOffset lastPrune = DateTimeOffset.MinValue;
    private int eventScanRunning;
    public MainWindow(Store store)
    {
        InitializeComponent(); Title = "Nexo 1.6.3 • Suporte inteligente"; this.store = store; support = new SupportService(store);
        ApplyNavigationIcons();
        user = store.GetLocalSession();
        RefreshIdentity();
        NewConversation();
        ShowPage("chat");
        try { healthSettings = HealthSettings.Load(store.Root); }
        catch (Exception ex) { healthError = "Preferências inválidas; usando valores padrão. " + ex.Message; }
        try { healthStore = new OperationalStore(store.Root); }
        catch (Exception ex) { healthError = "Histórico operacional indisponível. " + ex.Message; }
        Loaded += (_, _) => StartHealthMonitoring();
        Closed += async (_, _) => { if (healthMonitor != null) { await healthMonitor.StopAsync(); healthMonitor.Dispose(); } healthStore?.Dispose(); };
        Closing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            var message = operation?.IsCancellationRequested == true
                ? "O cancelamento foi solicitado. Aguarde o resultado das ações já iniciadas antes de fechar."
                : "Aguarde o resultado da operação antes de fechar. Se o botão Cancelar estiver disponível, você pode solicitar a interrupção das próximas etapas.";
            MessageBox.Show(this, message, "Atendimento em andamento");
        };
    }
    private static TextBlock Text(string text, double size = 14, string color = "Ink") => new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };
    private static string MetricPercent(double? value) => value.HasValue ? value.Value.ToString("F0") + "%" : "indisponível";
    private static Brush Brush(string color) => color.StartsWith('#') ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) : (Brush)Application.Current.FindResource(color);
    private void ApplyNavigationIcons()
    {
        foreach (var button in Navigation.Children.OfType<Button>())
        {
            if (button.Tag is not string section || button.Content is not StackPanel previous) continue;
            var label = previous.Children.OfType<TextBlock>().Last().Text;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(UiIcons.Create(section switch { "chat" => "chat", "health" => "health", "history" => "history", "tickets" => "tickets", "users" => "users", "settings" => "settings", "audit" => "audit", _ => "brand" }, 19));
            row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            button.Content = row;
            button.Style = (Style)FindResource("NavigationButton");
        }
    }
    private string OfflineHealthReport()
    {
        try { using var local = new OperationalStore(store.Root); return HealthReportPresentation.FromStore(local, healthSettings.Thresholds()); }
        catch (Exception ex) { return "O relatório de saúde local também está indisponível: " + ex.Message + ". O histórico de conversas permanece separado."; }
    }
    private static string AnswerPresentation(string answer)
    {
        var normalized = AgentProtocol.Normalize(answer);
        return normalized.Contains("nao consegui") || normalized.Contains("nao conseguiu selecionar") || normalized.Contains("resposta invalida")
            || normalized.Contains("limite de etapas") || normalized.Contains("interrompi as acoes")
            || normalized.Contains("nao foi concluida") || normalized.Contains("nao foi confirmada")
            || normalized.Contains("formato invalido") || normalized.Contains("voce recusou")
            ? "error" : "result";
    }
    private Button Action(string label, RoutedEventHandler handler, bool primary = false)
    {
        var b = new Button { Content = label, Margin = new Thickness(0, 0, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 38 };
        if (primary) b.Style = (Style)FindResource("Primary");
        b.Click += handler; return b;
    }
    private Border Card(UIElement content) => new() { Style = (Style)FindResource("Card"), Child = content, Margin = new Thickness(0, 0, 0, 14) };
    private static StackPanel SectionHeading(string icon, string title)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 13, 0, 12) };
        row.Children.Add(UiIcons.Create(icon, 19));
        row.Children.Add(new TextBlock { Text = title, Foreground = Brush("Ink"), FontSize = 18,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0) });
        return row;
    }
    private static Border MetricTile(string label, string value, string icon)
    {
        var panel = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(UiIcons.Create(icon, 16));
        top.Children.Add(new TextBlock { Text = label, Foreground = Brush("Secondary"), FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0) });
        panel.Children.Add(top);
        panel.Children.Add(new TextBlock { Text = value, Foreground = Brush("Ink"), FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 0) });
        return new Border { Background = Brush("SurfaceMuted"), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16), Margin = new Thickness(0, 0, 10, 0), Child = panel };
    }
    private Border EmptyState(string icon, string title, string description)
    {
        var content = new StackPanel { Margin = new Thickness(0, 7, 0, 7) };
        content.Children.Add(new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(11),
            Background = Brush("AccentSoft"), HorizontalAlignment = HorizontalAlignment.Left, Child = UiIcons.Create(icon, 20) });
        var heading = Text(title, 16); heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 14, 0, 6); content.Children.Add(heading);
        content.Children.Add(Text(description, 13, "Secondary"));
        return Card(content);
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
        kicker.Children.Add(UiIcons.Create("brand", 16));
        kicker.Children.Add(new TextBlock { Text = "UM ESPAÇO PARA RESOLVER", FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("Accent"), Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        welcome.Children.Add(kicker);
        welcome.Children.Add(new TextBlock { Text = "Olá, " + user.Name.Split(' ')[0] + ".\nComo posso ajudar hoje?",
            FontFamily = new FontFamily("Georgia"), FontSize = 36, Foreground = Brush("Ink"),
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
        switch (target) { case "profile": Profile(); break; case "users": Users(); break; case "history": History(); break; case "tickets": Tickets(); break; case "health": Health(); break; case "settings": Settings(); break; case "audit": Audits(); break; }
        PageScroll.ScrollToTop();
    }
    private void RefreshChatActions()
    {
        var available = conversation is { Status: "Em andamento" } && conversation.Entries.Count > 0;
        ChatActions.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ResolvedButton.IsEnabled = available && !busy;
        TicketButton.IsEnabled = available && !busy && user.Tickets;
    }
    private void Heading(string title, string description)
    {
        PageContent.Children.Add(new TextBlock { Text = title, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = FontWeights.SemiBold, FontSize = 30, Foreground = Brush("Ink") });
        var subtitle = Text(description, 14, "Secondary"); subtitle.Margin = new Thickness(0, 9, 0, 27); PageContent.Children.Add(subtitle);
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
            await Dispatcher.BeginInvoke(new Action(() =>
            {
                if (page != "health" || busy) return;
                var position = PageScroll.VerticalOffset;
                ShowPage("health"); PageScroll.ScrollToVerticalOffset(position);
            }));
        }
        catch (Exception ex) { healthError = ex.Message; }
    }
    private void Health()
    {
        Heading("Saúde do computador", "Leituras locais enquanto o Nexo está aberto. Achados são hipóteses até a função ser testada.");
        if (healthStore == null) { PageContent.Children.Add(Card(Text("Histórico operacional indisponível: " + healthError, 14, "Warning"))); return; }
        if (healthError != null) PageContent.Children.Add(Card(Text("Última falha de coleta: " + healthError, 13, "Warning")));
        if (healthMonitor?.LastError != null) PageContent.Children.Add(Card(Text("Monitoramento: " + healthMonitor.LastError, 13, "Warning")));
        try
        {
            var latest = healthStore.RecentSnapshots(DateTimeOffset.UtcNow.AddDays(-1), 1).FirstOrDefault();
            if (latest == null) PageContent.Children.Add(EmptyState("performance", "Aguardando a primeira leitura",
                "A coleta começa quando esta janela abre. Os indicadores aparecem quando houver dados disponíveis."));
            else
            {
                var overview = new StackPanel();
                overview.Children.Add(Text("Última leitura · " + latest.Time.ToLocalTime().ToString("g"), 12, "Secondary"));
                var metrics = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 15, 0, 14) };
                metrics.Children.Add(MetricTile("CPU", MetricPercent(latest.CpuPercent), "performance"));
                metrics.Children.Add(MetricTile("Memória", MetricPercent(latest.MemoryUsedPercent), "health"));
                metrics.Children.Add(MetricTile("Rede local", latest.Connectivity switch { "local" => "Interface ativa", "none" => "Sem interface", _ => "Indisponível" }, "network"));
                overview.Children.Add(metrics);
                var collectionStatus = latest.CollectionStatus switch { "complete" => "completa", "partial" => "parcial", _ => latest.CollectionStatus };
                overview.Children.Add(Text("Coleta: " + collectionStatus, 12, "Secondary"));
                var temperature = Text("Temperatura da CPU: " + HealthReportPresentation.DescribeTemperature(latest), 12, latest.CpuTemperature?.State is "error" or "invalid" ? "Warning" : "Secondary");
                temperature.Margin = new Thickness(0, 6, 0, 0); overview.Children.Add(temperature);
                var storage = Text("Discos físicos: " + HealthReportPresentation.DescribeStorage(latest), 12, latest.StorageHealth is "warning" or "unhealthy" ? "Warning" : "Secondary");
                storage.Margin = new Thickness(0, 6, 0, 0); overview.Children.Add(storage);
                foreach (var physical in latest.Storage?.Disks.Take(6) ?? [])
                {
                    var physicalText = Text(HealthReportPresentation.DescribeDisk(physical), 12, physical.State is "warning" or "unhealthy" ? "Warning" : "Secondary");
                    physicalText.Margin = new Thickness(12, 4, 0, 0); overview.Children.Add(physicalText);
                }
                if (latest.Storage is { Total: > 0 })
                {
                    var scope = Text(HealthReportPresentation.StorageScope, 11, "Secondary");
                    scope.Margin = new Thickness(0, 4, 0, 0); overview.Children.Add(scope);
                }
                foreach (var (label, reading) in new[]
                {
                    ("Gateway", latest.GatewayProbe), ("DNS", latest.DnsProbe), ("Destino externo", latest.ExternalProbe)
                })
                {
                    var status = Text(label + ": " + HealthReportPresentation.DescribeProbe(reading), 12,
                        reading?.State is "no_response" or "unexpected" ? "Warning" : "Secondary");
                    status.Margin = new Thickness(0, 6, 0, 0);
                    overview.Children.Add(status);
                }
                foreach (var disk in latest.Disks.Take(6))
                {
                    var diskText = Text($"{disk.Name}: {(disk.TotalBytes > 0 ? 100.0 * disk.FreeBytes / disk.TotalBytes : 0):F1}% livre", 13, "Secondary");
                    diskText.Margin = new Thickness(0, 7, 0, 0); overview.Children.Add(diskText);
                }
                PageContent.Children.Add(Card(overview));
            }
            var alerts = healthStore.RecentFindings(20);
            var occurrenceNow = DateTimeOffset.UtcNow;
            var occurrenceHistory = healthStore.RecentSnapshots(occurrenceNow.AddHours(-6), 20);
            var occurrenceLimits = healthSettings.Thresholds();
            PageContent.Children.Add(SectionHeading("health", "Ocorrências preventivas"));
            PageContent.Children.Add(Card(Text(HealthOccurrences.CollectionNotice(latest, occurrenceLimits, occurrenceNow), 12, "Secondary")));
            if (alerts.Count == 0) PageContent.Children.Add(Card(Text("Nenhuma ocorrência registrada.", 13, "Secondary")));
            foreach (var (finding, status) in HealthOccurrences.Classify(alerts, occurrenceHistory, occurrenceLimits, occurrenceNow))
            {
                var detail = new StackPanel();
                detail.Children.Add(Text($"{finding.Time.ToLocalTime():g}  ·  {finding.Severity}", 11, status.State != OccurrenceState.Persistent ? "Secondary" : finding.Severity == "critical" ? "Danger" : "Warning"));
                var occurrenceState = Text(status.Label, 12, status.State switch { OccurrenceState.Persistent => "Warning", OccurrenceState.Resolved => "Success", _ => "Secondary" });
                occurrenceState.FontWeight = FontWeights.SemiBold; occurrenceState.Margin = new Thickness(0, 5, 0, 0); detail.Children.Add(occurrenceState);
                var problem = Text(finding.Problem, 15); problem.FontWeight = FontWeights.SemiBold; problem.Margin = new Thickness(0, 7, 0, 7); detail.Children.Add(problem);
                detail.Children.Add(Text($"{finding.Evidence}\nHipótese: {finding.Cause}\nPróximo passo: {finding.Recommendation}", 13, "Secondary"));
                PageContent.Children.Add(Card(detail));
            }
            PageContent.Children.Add(SectionHeading("audit", "Eventos do Windows"));
            foreach (var warning in eventWarnings) PageContent.Children.Add(Card(Text(warning, 12, "Warning")));
            foreach (var gap in healthStore.RecentEventGaps(5, DateTimeOffset.UtcNow.AddDays(-1)))
                PageContent.Children.Add(Card(Text($"{gap.Time.ToLocalTime():g} · Lacuna na coleta · {gap.Message}", 12, "Warning")));
            var events = healthStore.RecentEvents(12);
            if (events.Count == 0) PageContent.Children.Add(Card(Text(lastEventScan == DateTimeOffset.MinValue ? "Aguardando a primeira consulta de eventos." : "Nenhum evento registrado nesta coleta. Consulte os avisos de canal antes de interpretar a ausência.", 13, "Secondary")));
            foreach (var item in events) PageContent.Children.Add(Card(Text($"{item.Time.ToLocalTime():g} · {item.Log} · ID {item.EventId} · nível {item.Level}\n{item.Provider}", 12)));
            PageContent.Children.Add(SectionHeading("shield", "Recuperação assistida"));
            var interventions = healthStore.RecentInterventions(12);
            if (interventions.Count == 0) PageContent.Children.Add(Card(Text("Nenhuma intervenção registrada.", 13, "Secondary")));
            foreach (var item in interventions) PageContent.Children.Add(Card(Text($"{item.Time.ToLocalTime():g} · {item.Action} · {HealthReportPresentation.InterventionState(item)}\nAutorizada: {(item.Authorized ? "sim" : "não")} · {item.Summary}", 12)));
            PageContent.Children.Add(SectionHeading("settings", "Drivers instalados"));
            PageContent.Children.Add(Card(Text("O assistente consulta o INF e a versão do driver associados a um dispositivo identificado. A busca online e a instalação exigem um alvo observado e autorização específica.", 13, "Secondary")));
            PageContent.Children.Add(Action("Investigar um driver no assistente", async (_, _) => await GuardAsync(async () => { ShowPage("chat"); await Send("Verifique o driver do dispositivo com problema. Consulte o inventário e o INF instalado antes de sugerir qualquer alteração."); })));
        }
        catch (Exception ex) { PageContent.Children.Add(Card(Text("Não foi possível ler o histórico operacional: " + ex.Message, 13, "Warning"))); }
    }
    private static TextBox Field(Panel parent, string label, string value = "")
    {
        parent.Children.Add(new TextBlock { Text = label, Foreground = Brush("Secondary"), FontSize = 12,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 17, 0, 7) });
        var input = new TextBox { Text = value, MaxLength = 240, MinHeight = 42, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(input, label); parent.Children.Add(input); return input;
    }
    private static PasswordBox Secret(Panel parent, string label)
    {
        parent.Children.Add(new TextBlock { Text = label, Foreground = Brush("Secondary"), FontSize = 12,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 17, 0, 7) });
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
        PageContent.Children.Add(Action("Criar usuário", (_, _) => AccountEditor(null), true));
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
                FontSize = 13, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(resultHeader);
            if (entry.DurationMs is > 0)
            {
                var elapsed = TimeSpan.FromMilliseconds(entry.DurationMs.Value);
                var timing = Text(elapsed.TotalMinutes >= 1 ? $"Verificação executada em {(int)elapsed.TotalMinutes} min {elapsed.Seconds} s" : $"Verificação executada em {Math.Max(1, (int)elapsed.TotalSeconds)} s", 11, "Secondary");
                timing.Margin = new Thickness(0, 5, 0, 0); body.Children.Add(timing);
            }
        }
        else
        {
            var byline = new Grid();
            byline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            byline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (entry.Role != "user") byline.Children.Add(UiIcons.Create("brand", 12));
            var bylineText = new TextBlock { Text = (entry.Role == "user" ? user.Name : "Nexo") + "  ·  " + entry.Time.ToString("HH:mm"),
                FontSize = 11, Foreground = Brush(entry.Role == "user" ? "Secondary" : "Accent"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(entry.Role == "user" ? 0 : 7, 0, 0, 0) };
            Grid.SetColumn(bylineText, 1); byline.Children.Add(bylineText);
            body.Children.Add(byline);
        }
        var text = new TextBox { Text = entry.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 12, 0, 0), FontSize = 14, IsReadOnlyCaretVisible = false };
        body.Children.Add(text);
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
        var color = warning ? "Warning" : "Accent";
        var detail = new TextBox { Text = explanation, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsReadOnlyCaretVisible = false, FontSize = 12 };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(UiIcons.Create("shield", 15, Brush(color)));
        title.Children.Add(new TextBlock { Text = label + "  ·  " + state, Foreground = Brush(color), FontSize = 12,
            Margin = new Thickness(7, 0, 0, 0) });
        var expander = new Expander { Header = title, Content = detail, Foreground = Brush(color), Margin = new Thickness(0, 0, 0, 9) };
        Messages.Children.Add(expander);
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
                if (!DriverWorkflow.Matches(prompt) && (!user.Repair || AgentProtocol.RequestsReadOnly(prompt) || !NetworkWorkflow.Matches(prompt)))
                {
                    try { await LocalModel.EnsureRunning(token); }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or TimeoutException)
                    {
                        AddEntry("assistant", "O agente Ollama está indisponível: " + ex.Message + "\n\n" + OfflineHealthReport(), "result", turnClock.ElapsedMilliseconds);
                        return;
                    }
                }
                var events = new Progress<string>(UpdateProgress);
                var executor = conversationTools ??= new WindowsTools(store, user, ConfirmChange, events, text => AddEntry("assistant", text));
                var agent = new AgentService(store, executor);
                string answer;
                try { answer = await agent.Run(user, conversation!, events, (label, result) => AddEntry("tool", label + "\n" + result), token); }
                catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
                {
                    AddEntry("assistant", "O agente Ollama ficou indisponível: " + ex.Message + "\n\n" + OfflineHealthReport(), "result", turnClock.ElapsedMilliseconds);
                    return;
                }
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
        busy = true; operation = new CancellationTokenSource(); BusyBar.Visibility = Visibility.Visible; CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed; CancelButton.IsEnabled = cancellable;
        if (quickReplyPanel != null) quickReplyPanel.IsEnabled = false;
        SendButton.IsEnabled = false; Navigation.IsEnabled = false; NewChatButton.IsEnabled = false; Composer.IsEnabled = false; RefreshChatActions();
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "ACOMPANHAMENTO";
        try { await action(operation.Token); UpdateProgress("Resultado disponível no chat."); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { AddEntry("assistant", "Atendimento interrompido. As ações já concluídas permanecem registradas acima; cancelar não desfaz uma correção concluída.", "error"); UpdateProgress("Atendimento interrompido."); }
        catch (OperationCanceledException) { AddEntry("assistant", "A etapa expirou ou foi interrompida antes de confirmar o resultado. Confira os resultados registrados acima antes de tentar novamente ou encaminhar à TI.", "error"); UpdateProgress("Etapa não concluída: resultado não confirmado."); store.Log(user, "Operação interrompida sem cancelamento do usuário"); }
        catch (Exception ex) { AddEntry("assistant", "Não consegui concluir esta etapa. " + ex.Message + "\n\nVocê pode tentar novamente ou gerar um encaminhamento para a TI.", "error"); UpdateProgress("Etapa não concluída."); store.Log(user, "Operação não concluída: " + ex.GetType().Name); }
        finally { busy = false; operation.Dispose(); operation = null; BusyBar.Visibility = Visibility.Collapsed; CancelButton.Visibility = Visibility.Collapsed; ProgressPanel.Visibility = Visibility.Collapsed; CancelButton.IsEnabled = true; SendButton.IsEnabled = Navigation.IsEnabled = NewChatButton.IsEnabled = Composer.IsEnabled = true; RefreshChatActions(); if (quickReplyPanel != null) quickReplyPanel.IsEnabled = true; ChatScroll.ScrollToEnd(); }
    }
    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (operation == null || operation.IsCancellationRequested) return;
        CancelButton.IsEnabled = false;
        operation.Cancel();
        UpdateProgress("Cancelamento solicitado. Etapas ainda não iniciadas serão interrompidas. Se uma alteração do Windows já começou, aguarde o resultado registrado no chat para confirmar seu estado.");
    }
    private Task<bool> ConfirmChange(ChangeRequest request) => ConfirmChange(request, null);
    private Task<bool> ConfirmChange(ChangeRequest request, Action<Window>? onRendered)
    {
        var dialog = new Window { Owner = this, Title = request.Title, Width = 700, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Style = (Style)FindResource(typeof(Window)) };
        var panel = new DockPanel { Margin = new Thickness(25) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 19) };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(UiIcons.Create("shield", 18));
        label.Children.Add(new TextBlock { Text = "CONFIRMAÇÃO DE AÇÃO", FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("Accent"), Margin = new Thickness(8, 0, 0, 0) });
        header.Children.Add(label);
        var title = Text(request.Title, 23); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 12, 0, 0);
        header.Children.Add(title); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(Action(request.KeepNetwork ? "Restaurar configuração anterior" : "Cancelar ação", (_, _) => dialog.DialogResult = false));
        var authorize = Action(request.KeepNetwork ? "Manter configuração" : "Autorizar e executar", (_, _) => dialog.DialogResult = true, true);
        authorize.IsEnabled = request.LicenseUrl == null;
        buttons.Children.Add(authorize); panel.Children.Add(buttons);
        var countdown = Text("", 12, "Warning"); countdown.Margin = new Thickness(0, 9, 0, 0);
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
        var details = new TextBox { Text = request.Details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0) };
        panel.Children.Add(new Border { Background = Brush("SurfaceMuted"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Child = details });
        dialog.Content = new Border { Background = Brush("Surface"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Margin = new Thickness(16), Child = panel };
        var seconds = 45;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { countdown.Text = $"Restauração automática em {--seconds} segundos."; if (seconds <= 0) { timer.Stop(); dialog.DialogResult = false; } };
        if (request.KeepNetwork) { countdown.Text = "Restauração automática em 45 segundos."; timer.Start(); }
        Exception? previewFailure = null;
        if (onRendered != null) dialog.ContentRendered += (_, _) =>
        {
            try { onRendered(dialog); }
            catch (Exception ex) { previewFailure = ex; }
            finally { dialog.DialogResult = false; }
        };
        var accepted = dialog.ShowDialog() == true; timer.Stop();
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
        Heading("Atendimentos", "Suas conversas, diagnósticos e resultados em um só lugar.");
        var items = store.Data.Conversations.Where(c => c.UserId == user.Id).OrderByDescending(c => c.Created).ToList();
        if (items.Count == 0) { PageContent.Children.Add(EmptyState("history", "Seu histórico começa aqui",
            "Quando você iniciar um atendimento, a conversa e os resultados aparecerão nesta página.")); return; }
        foreach (var item in items)
        {
            var row = new StackPanel();
            var title = Text(item.Title, 17); title.FontWeight = FontWeights.SemiBold; row.Children.Add(title);
            row.Children.Add(Text(item.Created.ToString("g") + "  ·  " + item.Status, 12, "Secondary"));
            var open = Action("Abrir conversa", (_, _) => { conversation = item; conversationTools = null; Messages.Children.Clear(); foreach (var entry in item.Entries) RenderEntry(entry); ProgressPanel.Visibility = Visibility.Collapsed; ProgressText.Text = ""; RefreshChatActions(); ShowPage("chat"); }); open.Margin = new Thickness(0, 17, 0, 0); row.Children.Add(open); PageContent.Children.Add(Card(row));
        }
    }
    private void Tickets()
    {
        Heading("Encaminhamentos", "Relatórios locais prontos para compartilhar com a TI. Nenhum chamado é enviado automaticamente.");
        var items = store.Data.Tickets.Where(t => user.Admin || t.UserId == user.Id).OrderByDescending(t => t.Created).ToList();
        if (items.Count == 0) PageContent.Children.Add(EmptyState("tickets", "Nenhum encaminhamento preparado",
            "Se o problema persistir após o atendimento, você pode preparar um relatório pelo chat."));
        foreach (var ticket in items)
        {
            var panel = new StackPanel(); panel.Children.Add(Text(ticket.Id + "  ·  Preparado para envio", 11, "Warning"));
            var title = Text(ticket.Title, 18); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 7, 0, 4); panel.Children.Add(title);
            panel.Children.Add(Text(ticket.Created.ToString("g"), 12, "Secondary"));
            var detail = new TextBox { Text = ticket.Report, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(new Expander { Header = "Revisar relatório", Content = detail, Foreground = Brush("Secondary"), Margin = new Thickness(0, 18, 0, 18) });
            panel.Children.Add(Action("Exportar relatório .txt", (_, _) => Guard(() => { var dialog = new SaveFileDialog { FileName = ticket.Id + ".txt", Filter = "Relatório de texto|*.txt" }; if (dialog.ShowDialog(this) == true) { File.WriteAllText(dialog.FileName, ticket.Report); store.Log(user, "Relatório exportado: " + ticket.Id); MessageBox.Show(this, "Relatório exportado. Anexe-o ao canal de atendimento da sua TI."); } }))); PageContent.Children.Add(Card(panel));
        }
    }
    private void Settings()
    {
        Heading("Configurações", "Um agente local conectado às ferramentas deste computador.");
        var panel = new StackPanel(); var agentHeading = SectionHeading("chat", "Agente de suporte · Ollama"); agentHeading.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(agentHeading);
        panel.Children.Add(Text("A IA escolhe ferramentas, consulta o Windows, solicita as alterações necessárias e verifica os resultados. O modelo recebe o histórico recente e as saídas das ferramentas no serviço local 127.0.0.1:11434.", 14, "Secondary"));
        var enabled = Check(panel, "Usar agente IA para conduzir os atendimentos", store.Data.AiEnabled); var model = Field(panel, "Modelo com suporte a ferramentas", store.Data.Model);
        panel.Children.Add(Text("Dispositivos, Bluetooth, áudio, câmera, impressoras, IP, DNS, serviços, processos, logs e drivers do Windows Update. Alterações no Windows continuam exigindo autorização; operações elevadas também passam pelo UAC.", 12, "Secondary"));
        var save = Action("Salvar configuração", (_, _) => Guard(() => { store.Require(user, a => a.Id == user.Id); if (enabled.IsChecked == true && string.IsNullOrWhiteSpace(model.Text)) throw new InvalidOperationException("Informe o nome do modelo instalado."); var previous = (store.Data.Model, store.Data.AiEnabled); store.Commit(user, "Configuração de IA atualizada", () => { store.Data.Model = model.Text.Trim(); store.Data.AiEnabled = enabled.IsChecked == true; }, () => { store.Data.Model = previous.Model; store.Data.AiEnabled = previous.AiEnabled; }); RefreshIdentity(); MessageBox.Show(this, "Configuração salva. O agente será utilizado no próximo atendimento."); }), true); save.Margin = new Thickness(0, 22, 0, 0); panel.Children.Add(save);
        var connectionStatus = Text("", 13, "Accent");
        panel.Children.Add(Action("Verificar modelo local", async (_, _) =>
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
        healthPanel.Children.Add(Text("A coleta funciona enquanto a janela está aberta. A temperatura da CPU só aparece quando o Libre Hardware Monitor está em execução e publica o sensor por WMI. O estado dos discos é o informado pelo Windows, não um SMART completo.", 12, "Secondary"));
        var interval = Field(healthPanel, "Intervalo entre amostras (segundos: 10–3600)", healthSettings.IntervalSeconds.ToString());
        var cpu = Field(healthPanel, "Alerta de CPU persistente (%)", healthSettings.CpuHighPercent.ToString("F0"));
        var disk = Field(healthPanel, "Alerta de espaço livre abaixo de (%)", healthSettings.DiskLowFreePercent.ToString("F0"));
        var retention = Field(healthPanel, "Retenção do histórico operacional (dias: 1–365)", healthSettings.RetentionDays.ToString());
        var processGrowth = Field(healthPanel, "Alerta de crescimento de memória por processo (pontos da RAM: 1–50)", healthSettings.ProcessMemoryGrowthPoints.ToString());
        var processSamples = Field(healthPanel, "Leituras seguidas para esse alerta (3–19)", healthSettings.ProcessMemorySamples.ToString());
        var networkHeading = Text("Sondagens de rede (opcionais)", 15); networkHeading.FontWeight = FontWeights.SemiBold;
        networkHeading.Margin = new Thickness(0, 24, 0, 8); healthPanel.Children.Add(networkHeading);
        healthPanel.Children.Add(Text("Ao ativar DNS ou destino externo, o Nexo envia consultas aos endereços abaixo. Um teste sem resposta não confirma a causa da falha.", 12, "Secondary"));
        var checkGateway = Check(healthPanel, "Verificar gateway por ICMP", healthSettings.CheckGateway);
        var checkDns = Check(healthPanel, "Verificar DNS do nome configurado", healthSettings.CheckDns);
        var checkExternal = Check(healthPanel, "Verificar destino externo por HTTP(S)", healthSettings.CheckExternal);
        foreach (var option in new[] { checkGateway, checkDns, checkExternal }) option.Margin = new Thickness(0, 10, 0, 0);
        var dnsHost = Field(healthPanel, "Nome para consulta DNS", healthSettings.DnsProbeHost);
        var httpUrl = Field(healthPanel, "URL do destino externo", healthSettings.HttpProbeUrl);
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
                ProcessMemorySamples = processSamplesValue };
            next.Save(store.Root);
            if (healthMonitor != null) { await healthMonitor.StopAsync(); healthMonitor.Dispose(); healthMonitor = null; }
            healthSettings = next; healthStore?.Prune(TimeSpan.FromDays(next.RetentionDays)); StartHealthMonitoring();
            MessageBox.Show(this, "Preferências do monitoramento salvas.", "Nexo");
        }), true));
        PageContent.Children.Add(Card(healthPanel));
        PageContent.Children.Add(Card(Text("Atendimentos e registros ficam neste perfil do Windows. A administração central de vários computadores e a integração com uma ferramenta de chamados são próximas etapas do produto.", 13, "Secondary")));
    }
    private void Audits()
    {
        Heading("Registro de ações", "Acessos, diagnósticos, autorizações e correções registrados localmente.");
        var metrics = SupportMetrics.Calculate(store.Data);
        var summary = metrics.Closed == 0
            ? "Nenhum atendimento encerrado ainda. As taxas aparecem após o usuário confirmar resolução ou preparar um encaminhamento."
            : $"Encerrados: {metrics.Closed}  ·  resolvidos pelo usuário: {metrics.Resolved} ({metrics.ResolutionRate:P0})  ·  encaminhados: {metrics.Escalated} ({metrics.EscalationRate:P0})\nTempo médio até o encerramento: {metrics.AverageClosureMinutes:F1} min  ·  em andamento: {metrics.Open}";
        var metricsPanel = new StackPanel(); var metricsHeading = SectionHeading("performance", "Métricas dos atendimentos locais");
        metricsHeading.Margin = new Thickness(0, 0, 0, 10); metricsPanel.Children.Add(metricsHeading);
        metricsPanel.Children.Add(Text(summary, 13, "Secondary")); PageContent.Children.Add(Card(metricsPanel));
        foreach (var audit in store.Data.Audit.OrderByDescending(a => a.Time).Take(200))
        {
            var panel = new StackPanel(); var action = Text(audit.Action); action.FontWeight = FontWeights.SemiBold; panel.Children.Add(action);
            panel.Children.Add(Text(audit.Time.ToString("g") + "  ·  " + audit.Actor, 11, "Secondary")); PageContent.Children.Add(Card(panel));
        }
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
        async Task Capture(string name)
        {
            await Task.Delay(200); UpdateLayout();
            var visual = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(output);
        }
        try
        {
        if (((Grid)Content).Background is not SolidColorBrush shellBrush || shellBrush.Color.R < 230 || shellBrush.Color.G < 230 || shellBrush.Color.B < 230)
            throw new InvalidOperationException("O tema claro não foi aplicado à janela principal.");
        if (!HasVectorIcon(ChatNav)) throw new InvalidOperationException("A navegação não usa ícones vetoriais.");
        if (Shell.Visibility != Visibility.Visible || !Messages.Children.OfType<StackPanel>().Any()) throw new InvalidOperationException("Falha na abertura direta do aplicativo.");
        var shortcutGrid = Messages.Children.OfType<StackPanel>().SelectMany(s => s.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>()).FirstOrDefault();
        if (shortcutGrid?.Children.Count != 6 || shortcutGrid.Children.OfType<Button>().Any(button => !HasVectorIcon(button)))
            throw new InvalidOperationException("Os seis atalhos iniciais precisam de ícones vetoriais.");
        if (Shell.ColumnDefinitions[0].ActualWidth < 240) throw new InvalidOperationException("A navegação ficou estreita para o novo layout.");
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
        if (!new[] { "Verificar gateway", "Verificar DNS", "Verificar destino externo" }.All(label => Descendants<CheckBox>(PageContent).Any(check => check.Content?.ToString()?.Contains(label) == true && check.IsChecked == false)))
            throw new InvalidOperationException("As três sondagens precisam de opções independentes desligadas por padrão.");
        await Capture("nexo-health-settings.png");
        Width = 1060; Height = 720; UpdateLayout(); await Capture("nexo-health-settings-small.png");
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
                var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(AppContext.BaseDirectory, file)); encoder.Save(output);
            });
            if (accepted) throw new InvalidOperationException("A prévia não cancelou o diálogo de autorização.");
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-checks.txt"), "Abertura direta e navegação, envio e resposta local: aprovados.\nPainel de saúde e controles de monitoramento renderizados.\nNovo atendimento na lateral; resolução e encaminhamento acima do compositor: aprovados em 1380x900 e 1060x720.\nAtalhos Sim/Não, rascunho e bloqueio de resposta antiga: aprovados.\nEnter livre para quebra de linha e manipulador Shift + Enter envia mensagem multilinha: aprovados.\nNomes, títulos e resultados longos verificados em 1060x720.\nDiálogos de autorização, licença e restauração de rede renderizados e cancelados sem mudanças no Windows.\n70 etapas com rolagem ao início/fim, controles acessíveis e rótulos alinhados. Nenhuma ação no Windows executada.");
        Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-checks.txt"), "Falha na prévia: " + ex);
            Application.Current.Shutdown(1);
        }
    }
}
