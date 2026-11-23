using System.Globalization;

namespace Nexo.Support;

/// <summary>Temas da interface (#143), sem dependência do WPF para o Nexo.Checks.</summary>
public static class ThemeCatalog
{
    public const string Onyx = "onyx";
    public const string Glacial = "glacial";
    public static IReadOnlyList<string> Names { get; } = [Onyx, Glacial];

    // Valor ausente, vazio ou desconhecido no data.json volta ao padrão escuro, sem falhar a abertura do Nexo.
    public static string Normalize(string? name)
        => string.Equals(name?.Trim(), Glacial, StringComparison.OrdinalIgnoreCase) ? Glacial : Onyx;

    public static string DisplayName(string name) => Normalize(name) == Glacial ? "Glacial (claro)" : "Ônix (escuro)";

    /// <summary>Caminho do dicionário dentro do assembly, relativo à raiz do projeto.</summary>
    public static string ResourcePath(string name) => Normalize(name) == Glacial ? "Themes/Glacial.xaml" : "Themes/Onyx.xaml";

    /// <summary>Luminância relativa da WCAG 2.x para uma cor #RRGGBB.</summary>
    public static double RelativeLuminance(string color)
    {
        var hex = color.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];
        if (hex.Length != 6) throw new FormatException("Cor fora do formato #RRGGBB: " + color);
        static double Channel(string part)
        {
            var value = int.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(hex[..2]) + 0.7152 * Channel(hex[2..4]) + 0.0722 * Channel(hex[4..6]);
    }

    public static double Contrast(string first, string second)
    {
        var a = RelativeLuminance(first); var b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Pares de texto e fundo usados na interface que precisam de 4,5:1 (AA para texto normal) nos dois temas.</summary>
    public static IReadOnlyList<(string Text, string Background)> ReadablePairs { get; } = BuildPairs();

    private static List<(string, string)> BuildPairs()
    {
        var pairs = new List<(string, string)>();
        foreach (var text in new[] { "Ink", "Secondary" })
            foreach (var background in new[] { "CanvasBg", "SidebarBg", "Surface", "SurfaceMuted", "UserSurface", "AccentSoft", "SuccessSoft", "WarningSoft" })
                pairs.Add((text, background));
        foreach (var background in new[] { "CanvasBg", "SidebarBg", "Surface", "SurfaceMuted", "AccentSoft" }) pairs.Add(("Subtle", background));
        foreach (var background in new[] { "CanvasBg", "SidebarBg", "Surface", "SurfaceMuted", "AccentSoft", "UserSurface" }) pairs.Add(("Accent", background));
        pairs.Add(("OnAccent", "Accent")); pairs.Add(("OnAccent", "AccentHover"));
        foreach (var (state, soft) in new[] { ("Success", "SuccessSoft"), ("Warning", "WarningSoft"), ("Danger", "Surface") })
            foreach (var background in new[] { "CanvasBg", "Surface", "SurfaceMuted", soft }.Distinct()) pairs.Add((state, background));
        return pairs;
    }
}
