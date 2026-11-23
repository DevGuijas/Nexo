using System.Windows;
using System.Windows.Media;

namespace Nexo.Support;

/// <summary>
/// Troca de tema em tempo real (#143). O XAML usa DynamicResource e acompanha a troca do dicionário. As telas montadas em
/// código recebem, pelo <see cref="Live"/>, um pincel próprio por chave, nunca congelado, cuja cor é atualizada na troca;
/// assim o texto já criado (conversa, páginas) muda de cor sem reconstruir a tela.
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, SolidColorBrush> live = new(StringComparer.Ordinal);
    private static ResourceDictionary? current;
    public static string Current { get; private set; } = ThemeCatalog.Onyx;

    public static void Apply(string? name)
    {
        var theme = ThemeCatalog.Normalize(name);
        var dictionary = new ResourceDictionary { Source = new Uri("pack://application:,,,/Nexo;component/" + ThemeCatalog.ResourcePath(theme), UriKind.Absolute) };
        var merged = Application.Current.Resources.MergedDictionaries;
        // O App.xaml carrega o Ônix; na primeira troca ele é achado pela chave (o BAML pode mudar a grafia do Source) e removido.
        // O WPF consulta os dicionários mesclados do último para o primeiro, então o tema novo entra no fim.
        current ??= merged.FirstOrDefault(item => item.Contains("CanvasBg"));
        if (current != null) merged.Remove(current);
        merged.Add(dictionary);
        current = dictionary;
        Current = theme;
        foreach (var (key, brush) in live)
            if (dictionary[key] is SolidColorBrush source) brush.Color = source.Color;
    }

    public static Brush Live(string key)
    {
        if (!live.TryGetValue(key, out var brush))
        {
            var source = (SolidColorBrush)Application.Current.FindResource(key);
            brush = new SolidColorBrush(source.Color);
            live[key] = brush;
        }
        return brush;
    }
}
