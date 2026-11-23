using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Shell;

namespace Nexo.Support;

/// <summary>
/// Moldura da janela (#143, etapa 2): barra de título própria, barra lateral que expande e recolhe, atalhos de teclado da janela
/// e o pedido escrito no Assistente pelos atalhos da tela Início. O conteúdo das páginas fica no MainWindow.xaml.cs.
/// </summary>
public partial class MainWindow
{
    private const double SidebarExpandedWidth = 232, SidebarCollapsedWidth = 68;
    private const int WmNcHitTest = 0x0084, WmNcLButtonDown = 0x00A1, WmNcLButtonUp = 0x00A2, WmNcMouseLeave = 0x02A2, HtMaxButton = 9;
    private const int DwmWindowCornerPreference = 33, DwmCornerRound = 2;
    private bool sidebarCollapsed;
    private bool maximizeHot;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Snap layouts do Windows 11: o menu de encaixe aparece quando a janela responde HTMAXBUTTON sobre o botão maximizar.</summary>
    private static bool SnapLayoutsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    private void InitializeShell()
    {
        TitleText.Text = Title;
        MinimizeButton.Content = UiIcons.Create("minimize", 12, Brush("Ink"));
        CloseButton.Content = UiIcons.Create("close", 12, Brush("Ink"));
        sidebarCollapsed = store.Data.SidebarCollapsed;
        ApplySidebar(animate: false);
        RefreshThemeButton();
        SyncWindowState();
        StateChanged += (_, _) => SyncWindowState();
        PreviewKeyDown += (_, e) => { if (HandleWindowKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, Keyboard.FocusedElement)) e.Handled = true; };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (!SnapLayoutsSupported) return;
            // Cantos arredondados do Windows 11 numa janela sem a moldura do sistema; falha aqui só deixa os cantos retos.
            var corner = DwmCornerRound;
            try { _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref corner, sizeof(int)); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            HwndSource.FromHwnd(handle)?.AddHook(SnapLayoutHook);
        };
    }

    // ---- Barra de título ----
    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    private void PaletteClick(object sender, RoutedEventArgs e) => OpenPalette();
    private void ThemeClick(object sender, RoutedEventArgs e) => Guard(() => ApplyTheme(OtherTheme, persist: true));
    private static string OtherTheme => ThemeManager.Current == ThemeCatalog.Onyx ? ThemeCatalog.Glacial : ThemeCatalog.Onyx;

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    /// <summary>Chamado também pelo ApplyTheme: o botão mostra o tema atual e diz para qual troca.</summary>
    private void RefreshThemeButton()
    {
        var current = ThemeManager.Current == ThemeCatalog.Glacial ? "Glacial" : "Ônix";
        ThemeButton.Content = current;
        var hint = "Tema atual: " + ThemeCatalog.DisplayName(ThemeManager.Current) + ". Trocar para " + ThemeCatalog.DisplayName(OtherTheme);
        ThemeButton.ToolTip = hint;
        AutomationProperties.SetName(ThemeButton, hint);
    }

    private void SyncWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        // Maximizada sem a moldura do sistema, a janela passa da tela pela espessura da borda de redimensionamento.
        ((Grid)Content).Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        WindowFrame.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        MaximizeButton.Content = UiIcons.Create(maximized ? "restore" : "maximize", 12, Brush("Ink"));
        var label = maximized ? "Restaurar" : "Maximizar";
        MaximizeButton.ToolTip = label;
        AutomationProperties.SetName(MaximizeButton, label);
    }

    private IntPtr SnapLayoutHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmNcHitTest:
                var over = OverMaximizeButton(lParam);
                SetMaximizeHot(over);
                if (over) { handled = true; return new IntPtr(HtMaxButton); }
                break;
            // Sobre o HTMAXBUTTON o botão do WPF não recebe o mouse: o clique chega como mensagem de área não cliente.
            case WmNcLButtonDown when wParam.ToInt64() == HtMaxButton:
                handled = true;
                break;
            case WmNcLButtonUp when wParam.ToInt64() == HtMaxButton:
                handled = true;
                SetMaximizeHot(false);
                ToggleMaximize();
                break;
            case WmNcMouseLeave:
                SetMaximizeHot(false);
                break;
        }
        return IntPtr.Zero;
    }

    private bool OverMaximizeButton(IntPtr lParam)
    {
        if (!MaximizeButton.IsVisible || PresentationSource.FromVisual(MaximizeButton) == null) return false;
        var value = lParam.ToInt64();
        var screen = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        try
        {
            var point = MaximizeButton.PointFromScreen(screen);
            return point.X >= 0 && point.Y >= 0 && point.X < MaximizeButton.ActualWidth && point.Y < MaximizeButton.ActualHeight;
        }
        catch (InvalidOperationException) { return false; }
    }

    private void SetMaximizeHot(bool hot)
    {
        if (hot == maximizeHot) return;
        maximizeHot = hot;
        if (hot) MaximizeButton.Background = Brush("AccentSoft");
        else MaximizeButton.ClearValue(BackgroundProperty);
    }

    // ---- Barra lateral ----
    private void SidebarToggleClick(object sender, RoutedEventArgs e) => Guard(() => SetSidebar(!sidebarCollapsed, persist: true));

    private void SetSidebar(bool collapsed, bool persist)
    {
        if (collapsed == sidebarCollapsed) return;
        sidebarCollapsed = collapsed;
        if (persist)
        {
            var stored = store.Data.SidebarCollapsed;
            store.Data.SidebarCollapsed = collapsed;
            try { store.Save(); }
            catch { store.Data.SidebarCollapsed = stored; sidebarCollapsed = stored; ApplySidebar(animate: false); throw; }
        }
        ApplySidebar(animate: true);
    }

    /// <summary>Recolhida, a lateral mostra só os ícones; o nome de cada item continua no leitor de tela e na dica.</summary>
    private void ApplySidebar(bool animate)
    {
        var labels = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarWordmark.Visibility = WorkLabel.Visibility = NewChatLabel.Visibility = SidebarIdentity.Visibility = labels;
        foreach (var button in Navigation.Children.OfType<Button>())
            if (button.Content is StackPanel row && row.Children.OfType<TextBlock>().LastOrDefault() is { } label) label.Visibility = labels;
        NewChatButton.HorizontalContentAlignment = sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        var action = sidebarCollapsed ? "Expandir a barra lateral" : "Recolher a barra lateral";
        var toggle = new StackPanel { Orientation = Orientation.Horizontal };
        toggle.Children.Add(UiIcons.Create("sidebar", 19, Brush("Secondary")));
        if (!sidebarCollapsed) toggle.Children.Add(new TextBlock { Text = "Recolher", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap });
        SidebarToggle.Content = toggle;
        SidebarToggle.ToolTip = action;
        AutomationProperties.SetName(SidebarToggle, action);
        var target = sidebarCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth;
        Animate(Sidebar, WidthProperty, double.IsNaN(Sidebar.Width) ? target : Sidebar.Width, target, animate ? 180 : 0);
    }

    /// <summary>
    /// Única animação da janela: com "Efeitos de animação" desligado no Windows (SystemParameters.ClientAreaAnimation falso),
    /// o valor final entra direto, sem movimento.
    /// </summary>
    private static void Animate(UIElement element, DependencyProperty property, double from, double to, int milliseconds)
    {
        element.BeginAnimation(property, null);
        element.SetValue(property, to);
        if (milliseconds <= 0 || from.Equals(to) || !SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---- Teclado da janela ----
    /// <summary>Ctrl+K abre ou fecha a paleta; na tela Início, as teclas 1 a 6 usam os atalhos "Resolver rápido".</summary>
    private bool HandleWindowKey(Key key, ModifierKeys modifiers, IInputElement? focused)
    {
        if (key == Key.K && modifiers == ModifierKeys.Control)
        {
            if (PaletteOpen) ClosePalette(); else OpenPalette();
            return true;
        }
        if (PaletteOpen || modifiers != ModifierKeys.None || page != "home" || focused is TextBox or PasswordBox) return false;
        var number = key is >= Key.D1 and <= Key.D9 ? key - Key.D0 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad0 : 0;
        var fix = HomeData.QuickFixes.FirstOrDefault(item => item.Number == number);
        if (fix == null) return false;
        FillAssistant(fix.Prompt);
        return true;
    }

    /// <summary>
    /// Abre o Assistente com o pedido escrito e o cursor no fim, sem enviar: a pessoa revisa e aperta Enviar (#143, etapa 3).
    /// Um atendimento já começado fica no histórico; o pedido abre uma conversa nova.
    /// </summary>
    private void FillAssistant(string prompt)
    {
        if (busy) return;
        if (conversation is not { Entries.Count: 0, Status: "Em andamento" }) NewConversation();
        ShowPage("chat");
        Composer.Text = prompt;
        Composer.CaretIndex = Composer.Text.Length;
        Composer.Focus();
    }
}
