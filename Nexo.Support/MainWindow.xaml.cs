using Microsoft.Win32;
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
        InitializeComponent(); Title = "Nexo 1.5.0 • Suporte inteligente"; this.store = store; support = new SupportService(store);
        SizeChanged += (_, _) => ControlHint.Visibility = ActualHeight < 820 ? Visibility.Collapsed : Visibility.Visible;
        Closing += (_, e) => { if (busy) { e.Cancel = true; MessageBox.Show(this, "Aguarde a operação terminar ou cancele a verificação antes de fechar.", "Atendimento em andamento"); } };
    }
    private static TextBlock Text(string text, double size = 14, string color = "#E9EDF5") => new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    private Button Action(string label, RoutedEventHandler handler, bool primary = false)
    {
        var b = new Button { Content = label, Margin = new Thickness(0, 0, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary) b.Style = (Style)FindResource("Primary");
        b.Click += handler; return b;
    }
    private Border Card(UIElement content) => new() { Style = (Style)FindResource("Card"), Child = content, Margin = new Thickness(0, 0, 0, 14) };
    private void Guard(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Nexo", MessageBoxButton.OK, MessageBoxImage.Information); } }
    private void LoginKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) LoginClick(sender, e); }
    private void LoginClick(object sender, RoutedEventArgs e)
    {
        try
        {
            user = store.Login(LoginName.Text, LoginPassword.Password);
            LoginPassword.Clear(); LoginError.Text = ""; LoginView.Visibility = Visibility.Collapsed; Shell.Visibility = Visibility.Visible;
            RefreshIdentity(); NewConversation();
            if (user.MustChangePassword) ShowPage("profile");
        }
        catch (Exception ex) { LoginError.Text = ex.Message; }
    }
    private void RefreshIdentity()
    {
        user = store.Data.Accounts.Single(a => a.Id == user.Id);
        SidebarName.Text = user.Name; SidebarRole.Text = user.Admin ? "Administrador" : "Usuário";
        foreach (var el in new UIElement[] { AdminLabel, UsersNav, SettingsNav, AuditNav }) el.Visibility = user.Admin ? Visibility.Visible : Visibility.Collapsed;
        ModeBadge.Text = store.Data.AiEnabled ? "●  Agente local · " + store.Data.Model : "●  Diagnóstico local";
        TicketButton.IsEnabled = user.Tickets;
    }
    private void LogoutClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        store.Log(user, "Logout realizado"); conversation = null; conversationTools = null; Messages.Children.Clear(); Shell.Visibility = Visibility.Collapsed; LoginView.Visibility = Visibility.Visible; LoginPassword.Clear();
    }
    private void NavigateClick(object sender, RoutedEventArgs e) => Guard(() => ShowPage((string)((Button)sender).Tag));
    private void ProfileClick(object sender, RoutedEventArgs e) => ShowPage("profile");
    private void NewChatClick(object sender, RoutedEventArgs e) { if (!busy) { NewConversation(); ShowPage("chat"); } }
    private void NewConversation()
    {
        ClearQuickReplies();
        conversationTools = null;
        conversation = new Conversation { UserId = user.Id };
        Messages.Children.Clear(); Composer.Clear();
        ProgressText.Text = "Pronto para ouvir você.\n\nAs verificações e seus resultados aparecerão aqui.";
        var welcome = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        welcome.Children.Add(Text("✦", 42, "#8CAEFF"));
        welcome.Children.Add(new TextBlock { Text = "Olá, " + user.Name.Split(' ')[0] + ".\nComo posso ajudar hoje?", FontFamily = new FontFamily("Georgia"), FontSize = 34, Margin = new Thickness(0, 18, 0, 12), LineHeight = 44 });
        welcome.Children.Add(Text("Me conte o que não está funcionando. Vamos encontrar o próximo passo juntos.", 14, "#939CAA"));
        var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 26, 0, 24) };
        foreach (var (title, subtitle, prompt) in new[] { ("▣  Câmera", "A imagem não aparece", "Estou com problema na câmera"), ("◎  Internet e rede", "Conexão, IP e DNS", "Estou com problema na internet"), ("♫  Áudio e microfone", "Não ouço ou não me ouvem", "Estou com problema no áudio"), ("ϟ  Desempenho", "Meu computador está lento", "Meu computador está lento"), ("ᛒ  Bluetooth", "Meus dispositivos não conectam", "Meu Bluetooth não está funcionando"), ("▤  Impressora", "Não consigo imprimir", "Minha impressora não imprime") })
        {
            var content = new StackPanel(); content.Children.Add(Text(title, 14)); var sub = Text(subtitle, 11, "#939CAA"); sub.Margin = new Thickness(0, 9, 0, 0); content.Children.Add(sub);
            var button = new Button { Content = content, Background = Brush("#141920"), Padding = new Thickness(18), Margin = new Thickness(0, 0, 10, 10), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += async (_, _) => await Send(prompt); tiles.Children.Add(button);
        }
        welcome.Children.Add(tiles); welcome.Children.Add(Text("COMECE POR UM ATALHO OU ESCREVA ABAIXO", 10, "#68758A")); Messages.Children.Add(welcome);
    }
    private void ShowPage(string target)
    {
        if (busy) return;
        if (target is "users" or "settings" or "audit") store.Require(user, a => a.Admin);
        page = target;
        ChatView.Visibility = target == "chat" ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.Visibility = target == "chat" ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in Navigation.Children.OfType<Button>()) button.Background = Brush((string?)button.Tag == target ? "#22324D" : "#101318");
        if (target == "chat") return;
        PageContent.Children.Clear();
        switch (target) { case "profile": Profile(); break; case "users": Users(); break; case "history": History(); break; case "tickets": Tickets(); break; case "settings": Settings(); break; case "audit": Audits(); break; }
        PageScroll.ScrollToTop();
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
    private async void SendClick(object sender, RoutedEventArgs e) => await Send(Composer.Text);
    private async void ComposerKeyDown(object sender, KeyEventArgs e) => await HandleComposerKey(e, Keyboard.Modifiers);
    private async Task HandleComposerKey(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (e.Key == Key.Enter && modifiers == ModifierKeys.Shift) { e.Handled = true; if (!e.IsRepeat) await Send(Composer.Text); }
    }
    private void AddEntry(string role, string text)
    {
        if (conversation == null) return;
        var entry = new Entry { Role = role, Text = text }; conversation.Entries.Add(entry); store.Save(); RenderEntry(entry);
    }
    private void RenderEntry(Entry entry)
    {
        ClearQuickReplies();
        var body = new StackPanel(); body.Children.Add(Text(entry.Role == "user" ? user.Name + "  ·  " + entry.Time.ToString("HH:mm") : "✦  Nexo  ·  " + entry.Time.ToString("HH:mm"), 11, entry.Role == "user" ? "#A3AEC1" : "#8CAEFF"));
        var text = new TextBox { Text = entry.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 12, 0, 0), FontSize = 14, IsReadOnlyCaretVisible = false };
        if (entry.Role == "tool")
        {
            body.Children.Clear();
            text.MaxHeight = 240; text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            var outcomeLabel = "Resultado";
            try
            {
                using var result = JsonDocument.Parse(entry.Text[(entry.Text.IndexOf('\n') + 1)..]);
                outcomeLabel = result.RootElement.TryGetProperty("success", out var ok) && ok.GetBoolean() ? "Concluído" : "Não concluído";
                text.Text = JsonSerializer.Serialize(result.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }
            catch (JsonException) { }
            body.Children.Add(new Expander { Header = outcomeLabel + " · " + entry.Text.Split('\n')[0], Content = text, Foreground = Brush(outcomeLabel == "Não concluído" ? "#E9C28B" : "#A7C4FF") });
        }
        else body.Children.Add(text);
        if (entry.Role == "assistant" && conversation?.Status == "Em andamento" && ReferenceEquals(conversation.Entries.LastOrDefault(), entry))
        {
            var replies = QuickReplies.For(entry.Text);
            if (replies.Length > 0)
            {
                var ownerId = conversation.Id;
                quickReplyPanel = new WrapPanel { Margin = new Thickness(0, 18, 0, 0), IsEnabled = !busy };
                foreach (var reply in replies)
                {
                    var button = Action(reply, async (_, _) => await SubmitQuickReply(ownerId, entry, reply), reply == replies[0]);
                    button.Padding = new Thickness(15, 8, 15, 8);
                    button.ToolTip = "Enviar: " + reply;
                    AutomationProperties.SetName(button, "Responder: " + reply);
                    quickReplyPanel.Children.Add(button);
                }
                body.Children.Add(quickReplyPanel);
            }
        }
        var card = Card(body); card.Background = Brush(entry.Role == "user" ? "#192334" : "#12161D"); Messages.Children.Add(card); ChatScroll.ScrollToEnd();
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
        await Execute(async token =>
        {
            if (store.Data.AiEnabled)
            {
                UpdateProgress("Conectando o agente ao executor do Windows…");
                if (!user.Repair || AgentProtocol.RequestsReadOnly(prompt) || !NetworkWorkflow.Matches(prompt)) await LocalModel.EnsureRunning(token);
                var events = new Progress<string>(UpdateProgress);
                var executor = conversationTools ??= new WindowsTools(store, user, ConfirmChange, events, text => AddEntry("assistant", text));
                var agent = new AgentService(store, executor);
                AddEntry("assistant", await agent.Run(user, conversation!, events, (label, result) => AddEntry("tool", label + "\n" + result), token));
                return;
            }
            var topic = SupportService.Topic(prompt);
            if (topic != "general")
            {
                AddEntry("assistant", "Vou consultar o Windows para entender o que está acontecendo. Você pode acompanhar as etapas ao lado.");
                var diagnostic = await support.Diagnose(user, topic, new Progress<string>(UpdateProgress), token);
                AddEntry("assistant", "Verificação concluída.\n\n" + diagnostic.Summary); RenderActions(diagnostic);
            }
            else
            {
                UpdateProgress(store.Data.AiEnabled ? "Consultando o modelo de IA local…" : "Preparando orientação local…");
                AddEntry("assistant", await support.AskAi(user, conversation!, token));
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
        busy = true; operation = new CancellationTokenSource(); BusyBar.Visibility = Visibility.Visible; CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        if (quickReplyPanel != null) quickReplyPanel.IsEnabled = false;
        SendButton.IsEnabled = false; Navigation.IsEnabled = false; ProfileNav.IsEnabled = false; ResolvedButton.IsEnabled = false; TicketButton.IsEnabled = false; Composer.IsEnabled = false;
        ProgressText.Text = "ACOMPANHAMENTO";
        try { await action(operation.Token); UpdateProgress("Resultado disponível no chat."); }
        catch (OperationCanceledException) { AddEntry("assistant", "Atendimento interrompido. As ações já concluídas permanecem registradas acima; cancelar não desfaz uma correção concluída."); UpdateProgress("Atendimento interrompido."); }
        catch (Exception ex) { AddEntry("assistant", "Não consegui concluir esta etapa. " + ex.Message + "\n\nVocê pode tentar novamente ou gerar um encaminhamento para a TI."); UpdateProgress("Etapa não concluída."); store.Log(user, "Operação não concluída: " + ex.GetType().Name); }
        finally { busy = false; operation.Dispose(); operation = null; BusyBar.Visibility = Visibility.Collapsed; CancelButton.Visibility = Visibility.Collapsed; SendButton.IsEnabled = Navigation.IsEnabled = ProfileNav.IsEnabled = ResolvedButton.IsEnabled = Composer.IsEnabled = true; TicketButton.IsEnabled = user.Tickets; if (quickReplyPanel != null) quickReplyPanel.IsEnabled = true; ChatScroll.ScrollToEnd(); }
    }
    private void CancelClick(object sender, RoutedEventArgs e) { operation?.Cancel(); UpdateProgress("Cancelamento solicitado. Uma alteração do Windows já iniciada aguardará seu resultado; mudanças de rede pendentes serão revertidas."); }
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
            foreach (var device in diagnostic.Devices.Where(d => d.Code == 22)) actions.Children.Add(Action("Habilitar " + device.Name, async (_, _) => await Repair("enable-camera", device.Id, "Habilitar a câmera “" + device.Name + "”? O Windows solicitará autorização de administrador. Nenhum driver será instalado.")));
        }
        if (diagnostic.Topic == "network") actions.Children.Add(Action("Limpar cache DNS", async (_, _) => await Repair("dns", null, "Limpar o cache DNS deste computador? O Windows consultará novamente os endereços ao acessar sites.")));
        Messages.Children.Add(actions);
    }
    private async Task Repair(string action, string? id, string explanation)
    {
        if (busy) return;
        try { store.Require(user, a => a.Repair); } catch (Exception ex) { MessageBox.Show(this, ex.Message); return; }
        if (MessageBox.Show(this, explanation, "Autorizar correção", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Execute(async token => { UpdateProgress(action == "dns" ? "Limpando cache DNS…" : "Solicitando ativação da câmera ao Windows…"); AddEntry("assistant", await support.Repair(user, action, id, token)); }, false);
    }
    private void ResolvedClick(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (busy || conversation == null || conversation.Entries.Count == 0) return;
        conversation.Status = "Resolvido"; AddEntry("assistant", "Que bom que funcionou! Marquei este atendimento como resolvido por você. Quando precisar, é só começar uma nova conversa."); store.Log(user, "Resolução confirmada pelo usuário: " + conversation.Id);
    });
    private void EscalateClick(object sender, RoutedEventArgs e) => Guard(() =>
    {
        store.Require(user, a => a.Tickets);
        if (busy || conversation == null || conversation.Entries.Count == 0) { MessageBox.Show(this, "Descreva o problema no chat para preparar o relatório."); return; }
        if (store.Data.Tickets.Any(t => t.ConversationId == conversation.Id)) { ShowPage("tickets"); return; }
        var report = "NEXO • RELATÓRIO DE ATENDIMENTO\nUsuário: " + user.Name + " (" + user.Login + ")\nData: " + DateTime.Now.ToString("g") + "\nAssunto: " + conversation.Title + "\n\n" + string.Join("\n\n", conversation.Entries.Select(m => $"[{m.Time:g}] {(m.Role == "user" ? "Usuário" : "Nexo")}: {m.Text}"));
        var ticket = new Ticket { UserId = user.Id, ConversationId = conversation.Id, Title = conversation.Title, Report = report };
        store.Data.Tickets.Add(ticket); conversation.Status = "Encaminhamento preparado"; store.Log(user, "Relatório preparado: " + ticket.Id); ShowPage("tickets");
    });
    private void History()
    {
        Heading("Atendimentos", "Suas conversas, diagnósticos e resultados em um só lugar.");
        var items = store.Data.Conversations.Where(c => c.UserId == user.Id).OrderByDescending(c => c.Created).ToList();
        if (items.Count == 0) { PageContent.Children.Add(Card(Text("Você ainda não iniciou um atendimento. Seu histórico aparecerá aqui."))); return; }
        foreach (var item in items)
        {
            var row = new StackPanel(); row.Children.Add(Text(item.Title, 17)); row.Children.Add(Text(item.Created.ToString("g") + "  ·  " + item.Status, 12, "#939CAA"));
            var open = Action("Abrir conversa →", (_, _) => { conversation = item; conversationTools = null; Messages.Children.Clear(); foreach (var entry in item.Entries) RenderEntry(entry); ProgressText.Text = "Atendimento recuperado.\n\nPara uma nova ação, envie o problema novamente e obtenha um diagnóstico atualizado."; ShowPage("chat"); }); open.Margin = new Thickness(0, 18, 0, 0); open.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(open); PageContent.Children.Add(Card(row));
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
        panel.Children.Add(Text("Dispositivos, Bluetooth, áudio, câmera, impressoras, IP, DNS, serviços, processos, logs e drivers do Windows Update. As alterações respeitam as permissões da conta e a autorização exibida na tela.", 12, "#939CAA"));
        var save = Action("Salvar configuração", (_, _) => Guard(() => { store.Require(user, a => a.Admin); if (enabled.IsChecked == true && string.IsNullOrWhiteSpace(model.Text)) throw new InvalidOperationException("Informe o nome do modelo instalado."); store.Data.Model = model.Text.Trim(); store.Data.AiEnabled = enabled.IsChecked == true; store.Log(user, "Configuração de IA atualizada"); RefreshIdentity(); MessageBox.Show(this, "Configuração salva. O agente será utilizado no próximo atendimento."); }), true); save.Margin = new Thickness(0, 22, 0, 0); panel.Children.Add(save);
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
        PageContent.Children.Add(Card(Text("Esta versão mantém contas e histórico neste perfil do Windows. A administração central de vários computadores e a integração com uma ferramenta de chamados são próximas etapas do produto.", 13, "#939CAA")));
    }
    private void Audits()
    {
        Heading("Registro de ações", "Logins, permissões, diagnósticos e correções registrados localmente.");
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
        await Capture("nexo-login.png");
        LoginName.Text = "Guisbz"; LoginPassword.Password = "1234"; LoginClick(this, new RoutedEventArgs());
        if (Shell.Visibility != Visibility.Visible) throw new InvalidOperationException("Falha na verificação visual de login.");
        ShowPage("chat"); await Capture("nexo-preview.png");
        ShowPage("profile"); await Capture("nexo-profile.png");
        ShowPage("users"); await Capture("nexo-users.png");
        user.MustChangePassword = false; store.Data.AiEnabled = false;
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
            if (TicketButton.TranslatePoint(new Point(0, TicketButton.ActualHeight), ChatView).Y > ChatView.ActualHeight || CancelButton.ActualHeight <= 0) throw new InvalidOperationException("Controles de atendimento ficaram fora da janela.");
            var labelPositions = Navigation.Children.OfType<Button>().Select(b => ((StackPanel)b.Content).Children.OfType<TextBlock>().Last().TranslatePoint(new Point(), Navigation).X).ToArray();
            if (labelPositions.Max() - labelPositions.Min() > 1) throw new InvalidOperationException("Rótulos da navegação desalinhados.");
            await Capture(size.Item1 == 1380 ? "nexo-progress.png" : "nexo-progress-small.png");
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-checks.txt"), "Login, navegação, envio e resposta local: aprovados.\nAtalhos Sim/Não, rascunho e bloqueio de resposta antiga: aprovados.\nEnter livre para quebra de linha e manipulador Shift + Enter envia mensagem multilinha: aprovados.\n70 etapas com rolagem ao início/fim, controles acessíveis e rótulos alinhados em 1380x900 e 1060x720: aprovados. Nenhuma ação no Windows executada.");
        Application.Current.Shutdown();
    }
}
