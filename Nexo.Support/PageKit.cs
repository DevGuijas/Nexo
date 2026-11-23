using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Nexo.Support;

/// <summary>
/// Peças visuais das páginas do novo layout (#143, etapas 4 a 6): linha de contexto, selo de estado, linhas de rótulo e valor,
/// barra de uso, bloco expansível e avisos de carregando, vazio e erro. Cores só pelo <see cref="ThemeManager.Live"/> (a troca de
/// tema alcança o que já foi montado); números em IBM Plex Mono, títulos em Barlow Condensed; nenhum texto abaixo de 11 px.
/// </summary>
internal static class PageKit
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    // "Mostrar animações no Windows" desligado: a barra de carregamento não se move (#143, redução de movimento).
    internal static bool MotionAllowed => SystemParameters.ClientAreaAnimation;

    private static Brush Brush(string key) => ThemeManager.Live(key);
    internal static FontFamily Font(string key) => (FontFamily)Application.Current.FindResource(key);

    /// <summary>Linha acima do título da página: marcador no acento e um contexto real em caixa alta, em mono.</summary>
    internal static StackPanel Kicker(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new Border { Width = 7, Height = 7, Background = Brush("Accent"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text.ToUpper(Portuguese), FontFamily = Font("MonoFont"), FontSize = 11, Foreground = Brush("Secondary"),
            Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        return row;
    }

    /// <summary>Subtítulo de um grupo dentro de um cartão, em Barlow e caixa alta.</summary>
    internal static TextBlock Group(string text, double top = 22)
        => new() { Text = text.ToUpper(Portuguese), FontFamily = Font("DisplayFont"), FontWeight = FontWeights.SemiBold, FontSize = 14,
            Foreground = Brush("Secondary"), Margin = new Thickness(0, top, 0, 4), TextWrapping = TextWrapping.Wrap };

    /// <summary>Selo de estado. "Danger" usa a superfície como fundo, o par conferido no contraste AA.</summary>
    internal static Border Chip(string text, string tone)
    {
        var (background, foreground) = tone switch
        {
            "Success" => ("SuccessSoft", "Success"),
            "Warning" => ("WarningSoft", "Warning"),
            "Accent" => ("AccentSoft", "Accent"),
            "Danger" => ("Surface", "Danger"),
            _ => ("SurfaceMuted", "Secondary")
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brush(foreground), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush(foreground),
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap });
        return new Border { Background = Brush(background), BorderBrush = Brush(tone == "Neutral" ? "Stroke" : foreground), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, Child = row };
    }

    /// <summary>Linha de rótulo e valor com separador, como as listas do protótipo; o valor numérico vai em mono.</summary>
    internal static Grid Row(string label, string value, string tone = "Ink", bool mono = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var border = new Border { BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 9, 0, 9) };
        Grid.SetColumnSpan(border, 2); grid.Children.Add(border);
        var name = new TextBlock { Text = label, FontSize = 13, Foreground = Brush("Secondary"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 9, 16, 9), TextWrapping = TextWrapping.Wrap };
        grid.Children.Add(name);
        var shown = new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Brush(tone), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 9, 0, 9), TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        if (mono) shown.FontFamily = Font("MonoFont");
        Grid.SetColumn(shown, 1); grid.Children.Add(shown);
        AutomationProperties.SetName(grid, label + ": " + value);
        return grid;
    }

    /// <summary>Barra de ocupação (0 a 1) com trilho na cor da linha; a parte cheia tem contraste gráfico de 3:1 com ele.</summary>
    internal static Grid Bar(double fraction, string tone)
    {
        var used = Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1);
        var grid = new Grid { Height = 6, Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(used, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - used, GridUnitType.Star) });
        var track = new Border { Background = Brush("Stroke"), CornerRadius = new CornerRadius(3) };
        Grid.SetColumnSpan(track, 2); grid.Children.Add(track);
        grid.Children.Add(new Border { Background = Brush(tone), CornerRadius = new CornerRadius(3) });
        return grid;
    }

    /// <summary>
    /// Bloco expansível por botão (teclado: Tab e Enter/Espaço). O leitor de tela ouve o nome e o estado ("expandido" ou "recolhido").
    /// </summary>
    internal static StackPanel Disclosure(UIElement header, string name, UIElement content, string chevronColor = "Secondary")
    {
        var root = new StackPanel();
        var chevron = new Path { Data = Geometry.Parse("M9,6 L15,12 9,18"), Stroke = Brush(chevronColor), StrokeThickness = 2, Width = 12, Height = 12,
            Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(0) };
        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(chevron, Dock.Right); line.Children.Add(chevron);
        line.Children.Add(header);
        var toggle = new Button { Content = line, Padding = new Thickness(10, 8, 10, 8), FontWeight = FontWeights.Normal };
        // Estilo sem fundo da navegação (hover no acento suave e foco com borda); sem ele, fundo transparente fixo.
        if (Application.Current.TryFindResource("NavigationButton") is Style plain) toggle.Style = plain;
        else { toggle.Background = Brushes.Transparent; toggle.BorderThickness = new Thickness(0); }
        toggle.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var body = new Border { BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(2, 0, 0, 0), Margin = new Thickness(17, 2, 0, 8),
            Padding = new Thickness(14, 4, 4, 4), Child = content, Visibility = Visibility.Collapsed };
        void Refresh()
        {
            var open = body.Visibility == Visibility.Visible;
            ((RotateTransform)chevron.RenderTransform).Angle = open ? 90 : 0;
            AutomationProperties.SetItemStatus(toggle, open ? "expandido" : "recolhido");
        }
        toggle.Click += (_, _) => { body.Visibility = body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; Refresh(); };
        AutomationProperties.SetName(toggle, name);
        Refresh();
        root.Children.Add(toggle); root.Children.Add(body);
        return root;
    }

    /// <summary>
    /// Aviso de estado da página (carregando, vazio, erro, indisponível): faixa lateral na cor do estado, título e explicação.
    /// No carregando, uma barra indeterminada só quando o Windows permite animações.
    /// </summary>
    internal static Border Notice(string icon, string title, string detail, string tone, bool loading = false)
    {
        var accent = tone switch { "Warning" => "Warning", "Danger" => "Danger", "Success" => "Success", "Neutral" => "Secondary", _ => "Accent" };
        var content = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(UiIcons.Create(icon, 17, Brush(accent)));
        head.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush("Ink"),
            Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(head);
        if (detail.Length > 0)
            content.Children.Add(new TextBlock { Text = detail, FontSize = 13, Foreground = Brush("Secondary"), Margin = new Thickness(26, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (loading && MotionAllowed)
            content.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(26, 12, 0, 0), Foreground = Brush("Accent"),
                Background = Brush("AccentSoft"), BorderThickness = new Thickness(0) });
        var card = new Border { Background = Brush("Surface"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 14), ClipToBounds = true };
        var layout = new DockPanel();
        var stripe = new Border { Width = 3, Background = Brush(accent) };
        DockPanel.SetDock(stripe, Dock.Left); layout.Children.Add(stripe);
        layout.Children.Add(new Border { Padding = new Thickness(16, 14, 18, 14), Child = content });
        card.Child = layout;
        AutomationProperties.SetName(card, title + (detail.Length > 0 ? ". " + detail : ""));
        if (loading) AutomationProperties.SetItemStatus(card, "carregando");
        return card;
    }
}
