using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
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
    public MainWindow(Store store)
    {
        InitializeComponent(); Title = "Nexo 1.6.2 • Suporte inteligente"; this.store = store; support = new SupportService(store);
        user = store.GetLocalSession();
        RefreshIdentity();
        NewConversation();
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
    private static TextBlock Text(string text, double size = 14, string color = "#E9EDF5") => new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
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
        var b = new Button { Content = label, Margin = new Thickness(0, 0, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary) b.Style = (Style)FindResource("Primary");
        b.Click += handler; return b;
    }
    private Border Card(UIElement content) => new() { Style = (Style)FindResource("Card"), Child = content, Margin = new Thickness(0, 0, 0, 14) };
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
        var welcome = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        welcome.Children.Add(Text("✦", 42, "#8CAEFF"));
        welcome.Children.Add(new TextBlock { Text = "Olá, " + user.Name.Split(' ')[0] + ".\nComo posso ajudar hoje?", FontFamily = new FontFamily("Georgia"), FontSize = 34, Margin = new Thickness(0, 18, 0, 12), LineHeight = 44 });
        welcome.Children.Add(Text("Me conte o que não está funcionando. Vamos encontrar o próximo passo juntos.", 14, "#939CAA"));
        var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 26, 0, 24) };
        foreach (var (title, subtitle, prompt) in new[] { ("▣  Câmera", "A imagem não aparece", "Estou com problema na câmera"), ("◎  Internet e rede", "Conexão, IP e DNS", "Estou com problema na internet"), ("♫  Áudio e microfone", "Não ouço ou não me ouvem", "Estou com problema no áudio"), ("ϟ  Desempenho", "Meu computador está lento", "Meu computador está lento"), ("ᛒ  Bluetooth", "Meus dispositivos não conectam", "Meu Bluetooth não está funcionando"), ("▤  Impressora", "Não consigo imprimir", "Minha impressora não imprime") })
        {
            var content = new StackPanel(); content.Children.Add(Text(title, 14)); var sub = Text(subtitle, 11, "#939CAA"); sub.Margin = new Thickness(0, 9, 0, 0); content.Children.Add(sub);
            var button = new Button { Content = content, Background = Brush("#141920"), Padding = new Thickness(18), Margin = new Thickness(0, 0, 10, 10), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += async (_, _) => await GuardAsync(() => Send(prompt)); tiles.Children.Add(button);
        }
        welcome.Children.Add(tiles); welcome.Children.Add(Text("COMECE POR UM ATALHO OU ESCREVA ABAIXO", 10, "#68758A")); Messages.Children.Add(welcome);
    }
    private void ShowPage(string target)
    {
        if (busy) return;
        if (target == "users") store.Require(user, a => a.Admin);
        page = target;
        ChatView.Visibility = target == "chat" ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.Visibility = target == "chat" ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in Navigation.Children.OfType<Button>()) button.Background = Brush((string?)button.Tag == target ? "#22324D" : "#101318");
        if (target == "chat") return;
        PageContent.Children.Clear();
        switch (target) { case "profile": Profile(); break; case "users": Users(); break; case "history": History(); break; case "tickets": Tickets(); break; case "settings": Settings(); break; case "audit": Audits(); break; }
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
        PageContent.Children.Add(new TextBlock { Text = title, FontFamily = new FontFamily("Georgia"), FontSize = 30 });
        var subtitle = Text(description, 14, "#939CAA"); subtitle.Margin = new Thickness(0, 10, 0, 28); PageContent.Children.Add(subtitle);
    }
    private static TextBox Field(Panel parent, string label, string value = "")
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 8) });
        var input = new TextBox { Text = value, MaxLength = 240 }; AutomationProperties.SetName(input, label); parent.Children.Add(input); return input;
    }
    private static PasswordBox Secret(Panel parent, string label)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 8) }); var input = new PasswordBox { MaxLength = 128 }; AutomationProperties.SetName(input, label); parent.Children.Add(input); return input;
    }
    private static CheckBox Check(Panel parent, string label, bool value)
    {
        var check = new CheckBox { Content = label, IsChecked = value }; parent.Children.Add(check); return check;
    }
    private void Profile()
    {
        Heading("Minha conta", "Seu perfil, seus dados e sua segurança.");
        var form = new StackPanel { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left, Width = 600 };
        if (user.MustChangePassword) { var notice = Text("Defina uma senha pessoal para substituir sua senha inicial.", 14, "#F2CB8B"); notice.Margin = new Thickness(0, 0, 0, 16); form.Children.Add(notice); }
        var photo = user.Photo;
        var avatar = new Border { Width = 76, Height = 76, CornerRadius = new CornerRadius(38), Background = Brush("#293C60"), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
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
        var login = Field(form, "Login", user.Login); login.IsReadOnly = true;
        var current = Secret(form, "Senha atual (para alterar a senha)"); var password = Secret(form, "Nova senha (mínimo de 8 caracteres)"); var confirm = Secret(form, "Confirme a nova senha");
        var save = Action("Salvar alterações", (_, _) => Guard(() => { if (password.Password != confirm.Password) throw new InvalidOperationException("As novas senhas não coincidem."); store.UpdateProfile(user, name.Text, email.Text, photo, current.Password, password.Password); RefreshIdentity(); ShowPage("profile"); MessageBox.Show(this, "Seu perfil foi atualizado.", "Tudo certo"); }), true);
        save.Margin = new Thickness(0, 24, 0, 0); form.Children.Add(save); PageContent.Children.Add(Card(form));
    }
    private void Users()
    {
        Heading("Usuários e acessos", "Gerencie contas e escolha o que cada pessoa pode fazer neste computador.");
        PageContent.Children.Add(Action("＋  Criar usuário", (_, _) => AccountEditor(null), true));
        foreach (var account in store.Data.Accounts.OrderBy(a => a.Name))
        {
            var row = new DockPanel(); var edit = Action("Editar acesso", (_, _) => AccountEditor(account)); DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
            var detail = new StackPanel(); detail.Children.Add(Text(account.Name + "  ·  " + account.Login, 16)); detail.Children.Add(Text((account.Admin ? "Administrador" : "Usuário") + "  /  " + (account.Active ? "Ativo" : "Desativado"), 12, "#939CAA")); row.Children.Add(detail); PageContent.Children.Add(Card(row));
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
            body.Children.Add(Text(title, 14, entry.Presentation == "error" ? "#E9C28B" : "#B8E8C6"));
            if (entry.DurationMs is > 0)
            {
                var elapsed = TimeSpan.FromMilliseconds(entry.DurationMs.Value);
                var timing = Text(elapsed.TotalMinutes >= 1 ? $"Verificação executada em {(int)elapsed.TotalMinutes} min {elapsed.Seconds} s" : $"Verificação executada em {Math.Max(1, (int)elapsed.TotalSeconds)} s", 11, "#9FBAA8");
                timing.Margin = new Thickness(0, 5, 0, 0); body.Children.Add(timing);
            }
        }
        else body.Children.Add(Text(entry.Role == "user" ? user.Name + "  ·  " + entry.Time.ToString("HH:mm") : "✦  Nexo  ·  " + entry.Time.ToString("HH:mm"), 11, entry.Role == "user" ? "#A3AEC1" : "#8CAEFF"));
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
        card.Background = Brush(entry.Role == "user" ? "#192334" : entry.Presentation == "error" ? "#29231C" : result ? "#1C2923" : "#12161D");
        if (result) card.BorderBrush = Brush(entry.Presentation == "error" ? "#645039" : "#385642");
        Messages.Children.Add(card); RefreshChatActions(); ChatScroll.ScrollToEnd();
    }
    private void RenderToolEntry(Entry entry)
    {
        var separator = entry.Text.IndexOf('\n');
        var label = (separator >= 0 ? entry.Text[..separator] : entry.Text).TrimEnd('…');
        var raw = separator >= 0 ? entry.Text[(separator + 1)..] : "";
        var (state, explanation, warning) = ToolResultPresentation.Describe(label, raw);
        var color = warning ? "#E9C28B" : "#A7C4FF";
        var detail = new TextBox { Text = explanation, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsReadOnlyCaretVisible = false, FontSize = 12 };
        var title = new TextBlock { Text = "▸  " + label + "  ·  " + state, Foreground = Brush(color), FontSize = 12 };
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
                if (!DriverWorkflow.Matches(prompt) && (!user.Repair || AgentProtocol.RequestsReadOnly(prompt) || !NetworkWorkflow.Matches(prompt))) await LocalModel.EnsureRunning(token);
                var events = new Progress<string>(UpdateProgress);
                var executor = conversationTools ??= new WindowsTools(store, user, ConfirmChange, events, text => AddEntry("assistant", text));
                var agent = new AgentService(store, executor);
                var answer = await agent.Run(user, conversation!, events, (label, result) => AddEntry("tool", label + "\n" + result), token);
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
    private Task<bool> ConfirmChange(ChangeRequest request)
    {
        var dialog = new Window { Owner = this, Title = request.Title, Width = 660, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Style = (Style)FindResource(typeof(Window)) };
        var panel = new DockPanel { Margin = new Thickness(26) };
        var title = Text(request.Title, 23); title.Margin = new Thickness(0, 0, 0, 18); DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(Action(request.KeepNetwork ? "Restaurar configuração anterior" : "Cancelar ação", (_, _) => dialog.DialogResult = false));
        buttons.Children.Add(Action(request.KeepNetwork ? "Manter configuração" : "Autorizar e executar", (_, _) => dialog.DialogResult = true, true)); panel.Children.Add(buttons);
        var countdown = Text("", 12, "#E9C28B"); DockPanel.SetDock(countdown, Dock.Bottom); panel.Children.Add(countdown);
        panel.Children.Add(new TextBox { Text = request.Details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Background = Brushes.Transparent });
        dialog.Content = panel;
        var seconds = 45;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { countdown.Text = $"Restauração automática em {--seconds} segundos."; if (seconds <= 0) { timer.Stop(); dialog.DialogResult = false; } };
        if (request.KeepNetwork) { countdown.Text = "Restauração automática em 45 segundos."; timer.Start(); }
        var accepted = dialog.ShowDialog() == true; timer.Stop(); return Task.FromResult(accepted);
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
        if (items.Count == 0) { PageContent.Children.Add(Card(Text("Você ainda não iniciou um atendimento. Seu histórico aparecerá aqui."))); return; }
        foreach (var item in items)
        {
            var row = new StackPanel(); row.Children.Add(Text(item.Title, 17)); row.Children.Add(Text(item.Created.ToString("g") + "  ·  " + item.Status, 12, "#939CAA"));
            var open = Action("Abrir conversa →", (_, _) => { conversation = item; conversationTools = null; Messages.Children.Clear(); foreach (var entry in item.Entries) RenderEntry(entry); ProgressPanel.Visibility = Visibility.Collapsed; ProgressText.Text = ""; RefreshChatActions(); ShowPage("chat"); }); open.Margin = new Thickness(0, 18, 0, 0); open.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(open); PageContent.Children.Add(Card(row));
        }
    }
    private void Tickets()
    {
        Heading("Encaminhamentos", "Relatórios locais prontos para compartilhar com a TI. Nenhum chamado é enviado automaticamente.");
        var items = store.Data.Tickets.Where(t => user.Admin || t.UserId == user.Id).OrderByDescending(t => t.Created).ToList();
        if (items.Count == 0) PageContent.Children.Add(Card(Text("Nenhum encaminhamento. Se o problema persistir após o atendimento, prepare um relatório pelo chat.")));
        foreach (var ticket in items)
        {
            var panel = new StackPanel(); panel.Children.Add(Text(ticket.Id + "  ·  Preparado para envio", 11, "#E2C18E")); panel.Children.Add(Text(ticket.Title, 18)); panel.Children.Add(Text(ticket.Created.ToString("g"), 12, "#939CAA"));
            var detail = new TextBox { Text = ticket.Report, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(new Expander { Header = "Revisar relatório", Content = detail, Foreground = Brush("#B3C2DA"), Margin = new Thickness(0, 18, 0, 18) });
            panel.Children.Add(Action("Exportar relatório .txt", (_, _) => Guard(() => { var dialog = new SaveFileDialog { FileName = ticket.Id + ".txt", Filter = "Relatório de texto|*.txt" }; if (dialog.ShowDialog(this) == true) { File.WriteAllText(dialog.FileName, ticket.Report); store.Log(user, "Relatório exportado: " + ticket.Id); MessageBox.Show(this, "Relatório exportado. Anexe-o ao canal de atendimento da sua TI."); } }))); PageContent.Children.Add(Card(panel));
        }
    }
    private void Settings()
    {
        Heading("Configurações", "Um agente local conectado às ferramentas deste computador.");
        var panel = new StackPanel(); panel.Children.Add(Text("Agente de suporte · Ollama", 18)); panel.Children.Add(Text("A IA escolhe ferramentas, consulta o Windows, solicita as alterações necessárias e verifica os resultados. O modelo recebe o histórico recente e as saídas das ferramentas no serviço local 127.0.0.1:11434.", 14, "#939CAA"));
        var enabled = Check(panel, "Usar agente IA para conduzir os atendimentos", store.Data.AiEnabled); var model = Field(panel, "Modelo com suporte a ferramentas", store.Data.Model);
        panel.Children.Add(Text("Dispositivos, Bluetooth, áudio, câmera, impressoras, IP, DNS, serviços, processos, logs e drivers do Windows Update. Alterações no Windows continuam exigindo autorização; operações elevadas também passam pelo UAC.", 12, "#939CAA"));
        var save = Action("Salvar configuração", (_, _) => Guard(() => { store.Require(user, a => a.Id == user.Id); if (enabled.IsChecked == true && string.IsNullOrWhiteSpace(model.Text)) throw new InvalidOperationException("Informe o nome do modelo instalado."); var previous = (store.Data.Model, store.Data.AiEnabled); store.Commit(user, "Configuração de IA atualizada", () => { store.Data.Model = model.Text.Trim(); store.Data.AiEnabled = enabled.IsChecked == true; }, () => { store.Data.Model = previous.Model; store.Data.AiEnabled = previous.AiEnabled; }); RefreshIdentity(); MessageBox.Show(this, "Configuração salva. O agente será utilizado no próximo atendimento."); }), true); save.Margin = new Thickness(0, 22, 0, 0); panel.Children.Add(save);
        var connectionStatus = Text("", 13, "#A7C4FF");
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
        PageContent.Children.Add(Card(Text("Atendimentos e registros ficam neste perfil do Windows. A administração central de vários computadores e a integração com uma ferramenta de chamados são próximas etapas do produto.", 13, "#939CAA")));
    }
    private void Audits()
    {
        Heading("Registro de ações", "Acessos, diagnósticos, autorizações e correções registrados localmente.");
        var metrics = SupportMetrics.Calculate(store.Data);
        var summary = metrics.Closed == 0
            ? "Nenhum atendimento encerrado ainda. As taxas aparecem após o usuário confirmar resolução ou preparar um encaminhamento."
            : $"Encerrados: {metrics.Closed}  ·  resolvidos pelo usuário: {metrics.Resolved} ({metrics.ResolutionRate:P0})  ·  encaminhados: {metrics.Escalated} ({metrics.EscalationRate:P0})\nTempo médio até o encerramento: {metrics.AverageClosureMinutes:F1} min  ·  em andamento: {metrics.Open}";
        PageContent.Children.Add(Card(new StackPanel { Children = { Text("Métricas dos atendimentos locais", 17), Text(summary, 13, "#A3AEC1") } }));
        foreach (var audit in store.Data.Audit.OrderByDescending(a => a.Time).Take(200))
        {
            var panel = new StackPanel(); panel.Children.Add(Text(audit.Action)); panel.Children.Add(Text(audit.Time.ToString("g") + "  ·  " + audit.Actor, 11, "#939CAA")); PageContent.Children.Add(Card(panel));
        }
    }
    public async void CapturePreview()
    {
        // Use only the isolated preview store selected by App; never authenticate against production data.
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
        if (Shell.Visibility != Visibility.Visible || !Messages.Children.OfType<StackPanel>().Any()) throw new InvalidOperationException("Falha na abertura direta do aplicativo.");
        ShowPage("chat"); await Capture("nexo-preview.png");
        store.Data.AiEnabled = false;
        NewConversation(); ShowPage("chat"); await Send("Olá, preciso de ajuda");
        if (conversation?.Entries.Count != 2 || busy) throw new InvalidOperationException("Falha no fluxo de conversa local.");
        await Capture("nexo-conversation.png");
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
            if (ticketTop >= composerTop || ticketRight > ChatView.ActualWidth || newChatRight > 232 || CancelButton.ActualHeight <= 0)
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
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-checks.txt"), "Abertura direta e navegação, envio e resposta local: aprovados.\nNovo atendimento na lateral; resolução e encaminhamento acima do compositor: aprovados em 1380x900 e 1060x720.\nAtalhos Sim/Não, rascunho e bloqueio de resposta antiga: aprovados.\nEnter livre para quebra de linha e manipulador Shift + Enter envia mensagem multilinha: aprovados.\n70 etapas com rolagem ao início/fim, controles acessíveis e rótulos alinhados. Nenhuma ação no Windows executada.");
        Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-checks.txt"), "Falha na prévia: " + ex);
            Application.Current.Shutdown(1);
        }
    }
}
