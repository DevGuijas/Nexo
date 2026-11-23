using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Nexo.Support;

/// <summary>
/// Paleta Ctrl+K (#143, etapa 2): busca as telas e as ações que já existem. Teclado completo: digitar filtra, setas, Home e End
/// escolhem, Enter executa, Esc fecha e devolve o foco a quem o tinha; Tab circula só dentro da paleta enquanto ela está aberta.
/// O catálogo, o filtro e as teclas ficam no CommandCatalog, testado no Nexo.Checks.
/// </summary>
public partial class MainWindow
{
    private Border? paletteCard;
    private TextBox? paletteInput;
    private StackPanel? paletteList;
    private ScrollViewer? paletteScroll;
    private TextBlock? paletteEmpty;
    private IReadOnlyList<PaletteCommand> paletteAll = [];
    private IReadOnlyList<PaletteCommand> paletteVisible = [];
    private int paletteSelected = -1;
    private IInputElement? paletteReturnFocus;

    private bool PaletteOpen => PaletteHost.Visibility == Visibility.Visible;

    private void OpenPalette()
    {
        if (busy || PaletteOpen) return;
        BuildPalette();
        paletteReturnFocus = Keyboard.FocusedElement;
        paletteAll = CommandCatalog.Build(ThemeManager.Current, sidebarCollapsed);
        paletteInput!.Text = "";
        RefreshPalette();
        PaletteHost.Visibility = Visibility.Visible;
        Animate(paletteCard!, OpacityProperty, 0, 1, 120);
        paletteInput.Focus();
        if (!paletteInput.IsKeyboardFocused) Dispatcher.BeginInvoke(new Action(() => paletteInput.Focus()), DispatcherPriority.Input);
    }

    private void ClosePalette(bool restoreFocus = true)
    {
        if (!PaletteOpen) return;
        PaletteHost.Visibility = Visibility.Collapsed;
        if (restoreFocus && paletteReturnFocus is UIElement { IsVisible: true } previous) previous.Focus();
        paletteReturnFocus = null;
    }

    private void BuildPalette()
    {
        if (paletteCard != null) return;
        var scrim = new Border { Background = Brush("CanvasBg"), Opacity = 0.78 };
        scrim.MouseDown += (_, _) => ClosePalette();
        var input = new TextBox { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, FontSize = 15,
            Padding = new Thickness(10, 12, 10, 12), VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 120 };
        AutomationProperties.SetName(input, "Buscar telas e ações");
        AutomationProperties.SetHelpText(input, "Setas para escolher, Enter para abrir, Esc para fechar.");
        input.TextChanged += (_, _) => RefreshPalette();
        var esc = new TextBlock { Text = "Esc", FontFamily = Font("MonoFont"), FontSize = 11, Foreground = Brush("Subtle"), VerticalAlignment = VerticalAlignment.Center };
        var search = new DockPanel { Margin = new Thickness(16, 4, 16, 4) };
        DockPanel.SetDock(esc, Dock.Right); search.Children.Add(esc);
        var icon = UiIcons.Create("search", 18, Brush("Secondary")); DockPanel.SetDock(icon, Dock.Left); search.Children.Add(icon);
        search.Children.Add(input);
        var list = new StackPanel { Margin = new Thickness(8) };
        var scroll = new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var empty = Text("Nenhuma tela ou ação encontrada. Tente outro termo.", 13, "Secondary");
        empty.Margin = new Thickness(20, 14, 20, 14);
        var footer = Text("↑ ↓ escolhem  ·  Enter abre  ·  Esc fecha  ·  Ctrl K abre de qualquer tela", 11, "Subtle");
        footer.Margin = new Thickness(18, 10, 18, 12);
        var layout = new StackPanel();
        layout.Children.Add(search);
        layout.Children.Add(new Border { Height = 1, Background = Brush("Stroke") });
        layout.Children.Add(scroll);
        layout.Children.Add(empty);
        layout.Children.Add(new Border { Height = 1, Background = Brush("Stroke") });
        layout.Children.Add(footer);
        var card = new Border { Background = Brush("Surface"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Width = 580, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 72, 16, 16), Child = layout };
        AutomationProperties.SetName(card, "Paleta de comandos");
        PaletteHost.Children.Add(scrim);
        PaletteHost.Children.Add(card);
        PaletteHost.PreviewKeyDown += (_, e) => { if (HandlePaletteKey(e.Key)) e.Handled = true; };
        paletteCard = card; paletteInput = input; paletteList = list; paletteScroll = scroll; paletteEmpty = empty;
    }

    private void RefreshPalette()
    {
        if (paletteList == null || paletteInput == null) return;
        paletteVisible = CommandCatalog.Filter(paletteAll, paletteInput.Text);
        paletteSelected = paletteVisible.Count > 0 ? 0 : -1;
        paletteList.Children.Clear();
        foreach (var command in paletteVisible)
        {
            var row = new DockPanel();
            var kind = new TextBlock { Text = command.Kind, FontSize = 11, Foreground = Brush("Subtle"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(kind, Dock.Right); row.Children.Add(kind);
            var glyph = UiIcons.Create(command.Icon, 17); DockPanel.SetDock(glyph, Dock.Left); row.Children.Add(glyph);
            row.Children.Add(new TextBlock { Text = command.Title, FontSize = 14, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            var button = new Button { Content = row, Tag = command.Id, Style = (Style)FindResource("NavigationButton"), Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 2), HorizontalContentAlignment = HorizontalAlignment.Stretch, FontWeight = FontWeights.Normal };
            AutomationProperties.SetName(button, command.Title + " (" + command.Kind.ToLowerInvariant() + ")");
            var id = command.Id;
            button.Click += (_, _) => RunPaletteCommand(id);
            paletteList.Children.Add(button);
        }
        paletteEmpty!.Visibility = paletteVisible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HighlightPalette();
    }

    private void HighlightPalette()
    {
        if (paletteList == null) return;
        for (var i = 0; i < paletteList.Children.Count; i++)
        {
            var button = (Button)paletteList.Children[i];
            var selected = i == paletteSelected;
            // Sem seleção, o botão volta ao estilo (e ao realce de passar o mouse).
            if (selected) { button.Background = Brush("AccentSoft"); button.Foreground = Brush("Accent"); }
            else { button.ClearValue(BackgroundProperty); button.Foreground = Brush("Ink"); }
            if (selected) button.BringIntoView();
        }
        // O leitor de tela ouve o item escolhido pela caixa de busca, onde o foco fica.
        if (paletteInput != null)
            AutomationProperties.SetItemStatus(paletteInput, paletteSelected >= 0 ? paletteVisible[paletteSelected].Title : "Nenhum resultado");
    }

    private bool HandlePaletteKey(Key key)
    {
        var name = key switch { Key.Down => "Down", Key.Up => "Up", Key.Enter => "Enter", Key.Escape => "Escape", Key.PageUp => "Home", Key.PageDown => "End", _ => null };
        if (name == null || !PaletteOpen) return false;
        // Com o foco num item (Tab), Enter é o clique do próprio botão.
        if (name == "Enter" && Keyboard.FocusedElement is Button { Tag: string focusedId } && paletteVisible.Any(item => item.Id == focusedId))
        {
            RunPaletteCommand(focusedId);
            return true;
        }
        var (selected, execute, close) = CommandCatalog.HandleKey(name, paletteSelected, paletteVisible);
        paletteSelected = selected;
        HighlightPalette();
        if (execute != null) RunPaletteCommand(execute);
        else if (close) ClosePalette();
        return true;
    }

    private void RunPaletteCommand(string id)
    {
        ClosePalette(restoreFocus: false);
        Guard(() =>
        {
            switch (id)
            {
                case "action:new-chat":
                    if (busy) return;
                    NewConversation(); ShowPage("chat"); Composer.Focus();
                    break;
                case "action:theme":
                    ApplyTheme(OtherTheme, persist: true);
                    break;
                case "action:sidebar":
                    SetSidebar(!sidebarCollapsed, persist: true);
                    SidebarToggle.Focus();
                    break;
                default:
                    if (!id.StartsWith("page:", StringComparison.Ordinal)) throw new InvalidOperationException("Comando desconhecido: " + id);
                    ShowPage(id[5..]);
                    // O foco vai para a tela aberta: o compositor no Assistente, o item da lateral nas demais.
                    if (page == "chat") Composer.Focus();
                    else Navigation.Children.OfType<Button>().FirstOrDefault(button => (string?)button.Tag == page)?.Focus();
                    break;
            }
        });
    }
}
