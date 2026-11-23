using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nexo.Support;

// #143, etapa 1: paletas Ônix e Glacial, troca de tema, fontes embutidas e logo. O WPF não roda no Mac nem no CI de checks;
// por isso o teste lê os dicionários, o XAML e o código como texto, e a troca ao vivo fica no --screenshot do Windows.
internal static class ThemeChecks
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    internal static void Run(Action<bool, string> assert)
    {
        var baseDir = AppContext.BaseDirectory;
        var themes = ThemeCatalog.Names.ToDictionary(name => name, name => Palette(Path.Combine(baseDir, ThemeCatalog.ResourcePath(name))));
        var onyx = themes[ThemeCatalog.Onyx]; var glacial = themes[ThemeCatalog.Glacial];
        assert(onyx.Count > 0 && onyx.Keys.ToHashSet().SetEquals(glacial.Keys), "Os temas Ônix e Glacial definem as mesmas chaves de cor");
        assert(themes.Values.SelectMany(p => p.Values).All(color => Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$")), "As cores dos temas usam #RRGGBB opaco");
        assert(onyx["CanvasBg"] == "#0A0A0B" && onyx["Surface"] == "#121214" && onyx["Stroke"] == "#26262B" && onyx["Ink"] == "#EDEDED"
            && onyx["Accent"] == "#C9A54C" && onyx["AccentHover"] == "#E7C873" && onyx["Success"] == "#6FAF7B" && onyx["Warning"] == "#D9A441",
            "O Ônix segue os valores da #143 (fundo, superfície, linha, texto, acento dourado, ok e atenção)");
        assert(glacial["CanvasBg"] == "#F3F5F8" && glacial["Surface"] == "#FFFFFF" && glacial["Ink"] == "#1B1F24" && glacial["LogoStrong"] == "#0E77B6",
            "O Glacial segue os valores da #143 e a logo usa o azul amostrado da imagem");

        foreach (var (name, palette) in themes)
        {
            var failures = ThemeCatalog.ReadablePairs.Where(pair => ThemeCatalog.Contrast(palette[pair.Text], palette[pair.Background]) < 4.5)
                .Select(pair => $"{pair.Text}/{pair.Background} {ThemeCatalog.Contrast(palette[pair.Text], palette[pair.Background]):F2}").ToArray();
            assert(failures.Length == 0, $"Contraste AA (4,5:1) de texto e fundo no tema {ThemeCatalog.DisplayName(name)}" + (failures.Length > 0 ? ": " + string.Join(", ", failures) : ""));
            assert(ThemeCatalog.Contrast(palette["LogoStrong"], palette["SidebarBg"]) >= 3 && ThemeCatalog.Contrast(palette["Accent"], palette["Stroke"]) >= 3,
                $"Logo e borda de foco têm contraste gráfico de 3:1 no tema {ThemeCatalog.DisplayName(name)}");
        }
        assert(Math.Abs(ThemeCatalog.Contrast("#FFFFFF", "#000000") - 21) < 0.01 && Math.Abs(ThemeCatalog.Contrast("#777777", "#FFFFFF") - 4.48) < 0.01,
            "A luminância relativa segue a fórmula da WCAG");
        assert(ThemeCatalog.Contrast("#0E77B6", "#F3F5F8") < 4.5, "O azul original da logo não passa como texto no Glacial; o acento usa o tom escurecido");

        // Chaves: o XAML usa DynamicResource para as cores (a troca não alcança StaticResource), toda chave usada existe nos dois
        // temas e toda chave dos temas é usada.
        var sources = Path.Combine(baseDir, "ThemeSources");
        var app = File.ReadAllText(Path.Combine(sources, "App.xaml")); var window = File.ReadAllText(Path.Combine(sources, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(sources, "MainWindow.xaml.cs")) + File.ReadAllText(Path.Combine(sources, "UiIcons.cs"));
        var keys = onyx.Keys.ToHashSet(StringComparer.Ordinal);
        var dynamicKeys = Regex.Matches(app + window, @"\{DynamicResource (\w+)\}").Select(m => m.Groups[1].Value).ToHashSet();
        var staticColorKeys = Regex.Matches(app + window, @"\{StaticResource (\w+)\}").Select(m => m.Groups[1].Value).Where(keys.Contains).ToArray();
        assert(dynamicKeys.Count > 0 && dynamicKeys.All(keys.Contains), "Toda cor dinâmica do XAML existe nos dois temas");
        assert(staticColorKeys.Length == 0, "Nenhuma cor do tema é lida por StaticResource" + (staticColorKeys.Length > 0 ? ": " + string.Join(", ", staticColorKeys) : ""));
        assert(!Regex.IsMatch(app + window, "#[0-9A-Fa-f]{6}\\b|\"(White|Black)\""), "App.xaml e MainWindow.xaml não fixam cores fora dos temas");
        // Brush("X"), Live("X"), Brush(c ? "X" : "Y") e Text(…, 12, "X"): só as palavras entre aspas que começam com maiúscula.
        var codeKeys = Regex.Matches(code, @"\b(?:Brush|Live)\(([^;\n]*?)\)").SelectMany(m => Regex.Matches(m.Groups[1].Value, @"""([A-Z]\w*)""").Select(q => q.Groups[1].Value))
            .Concat(Regex.Matches(code, @"\bText\([^;\n]*?, [\d.]+, ""([A-Z]\w*)""\)").Select(m => m.Groups[1].Value)).ToHashSet();
        var unknown = codeKeys.Where(key => !keys.Contains(key)).ToArray();
        assert(unknown.Length == 0 && codeKeys.Contains("Accent") && codeKeys.Contains("SurfaceMuted"),
            "As chaves de cor usadas no código existem nos dois temas" + (unknown.Length > 0 ? ": " + string.Join(", ", unknown) : ""));
        var unused = keys.Where(key => !dynamicKeys.Contains(key) && !Regex.IsMatch(code, "\"" + key + "\"")).ToArray();
        assert(unused.Length == 0, "Toda chave dos temas é usada pela interface" + (unused.Length > 0 ? ": " + string.Join(", ", unused) : ""));
        assert(app.Contains("<ResourceDictionary Source=\"Themes/Onyx.xaml\"/>") && !app.Contains("WhiteBrush") && !window.Contains("WhiteBrush"),
            "O App.xaml carrega o Ônix como padrão e a chave antiga WhiteBrush saiu");

        // Persistência: o tema fica no data.json; ausente ou desconhecido volta ao Ônix.
        var root = Path.Combine(Path.GetTempPath(), "NexoTheme-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(Path.Combine(root, "a"), directAccess: true);
            assert(store.Data.Theme == ThemeCatalog.Onyx, "O tema padrão de uma instalação nova é o Ônix");
            store.Data.Theme = ThemeCatalog.Glacial; store.Save();
            assert(new Store(Path.Combine(root, "a"), directAccess: true).Data.Theme == ThemeCatalog.Glacial, "A escolha do Glacial persiste ao reabrir");
            var legacy = Path.Combine(root, "b"); Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "data.json"), JsonSerializer.Serialize(new { Model = "qwen3:8b", AiEnabled = true, AgentVersion = 2 }));
            var old = new Store(legacy, directAccess: true);
            assert(old.Data.Theme == ThemeCatalog.Onyx && old.Data.Model == "qwen3:8b", "Um data.json anterior à #143 abre no Ônix sem perder as outras preferências");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        assert(ThemeCatalog.Normalize(" GLACIAL ") == ThemeCatalog.Glacial && ThemeCatalog.Normalize("neon") == ThemeCatalog.Onyx
            && ThemeCatalog.Normalize(null) == ThemeCatalog.Onyx && ThemeCatalog.Normalize("") == ThemeCatalog.Onyx, "Tema desconhecido ou vazio volta ao Ônix");
        assert(ThemeCatalog.ResourcePath("glacial") == "Themes/Glacial.xaml" && ThemeCatalog.ResourcePath("x") == "Themes/Onyx.xaml", "O caminho do dicionário segue o tema normalizado");

        // Fontes: arquivos oficiais sem modificação (hash do ThirdParty/Fonts/NOTICE.md) e famílias citadas no App.xaml.
        var expected = new Dictionary<string, (string Hash, string Family, int Weight)>
        {
            ["BarlowCondensed-SemiBold.ttf"] = ("7b619d14bc2327509a9ef32b0890f709626f7ecc9ff61191c2a4314c5499d2d9", "Barlow Condensed", 600),
            ["BarlowCondensed-Bold.ttf"] = ("e476562ec9c1e16cf16475895b511f08c804f438cc9a9f80a44ea50a0eeb5b65", "Barlow Condensed", 700),
            ["IBMPlexMono-Regular.ttf"] = ("6a3412f058c7d8dfd9170c41e85ade48e5156ecb89356110ca57a0a27734af46", "IBM Plex Mono", 400),
            ["IBMPlexMono-Medium.ttf"] = ("a9b4c49bb299e05b5f6c481e7fb5e78943d2793249a0c8874ab574a2d1ea6755", "IBM Plex Mono", 500),
            ["IBMPlexSans-Regular.ttf"] = ("b47ebd72b5ce53b60f909033ff84d6fc12358cc147e2981f8c321ad3f36246b6", "IBM Plex Sans", 400),
            ["IBMPlexSans-Medium.ttf"] = ("a494d5a69fa094f7071a9ffbc98e86c85337d1a29f3fc1ff7c2e1c46161ddf27", "IBM Plex Sans", 500),
            ["IBMPlexSans-SemiBold.ttf"] = ("c51119b25f4a296da2b66edf3201b9aa1735091ed7efc1fca0313346f3851a4d", "IBM Plex Sans", 600),
        };
        var fontDir = Path.Combine(baseDir, "Fonts");
        var present = Directory.GetFiles(fontDir, "*.ttf").Select(Path.GetFileName).OfType<string>().ToHashSet();
        assert(present.SetEquals(expected.Keys), "Só as sete fontes da #143 são embutidas");
        foreach (var (file, (hash, family, weight)) in expected)
        {
            var bytes = File.ReadAllBytes(Path.Combine(fontDir, file));
            var (names, os2Weight) = FontInfo(bytes);
            assert(Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase) && names.Contains(family) && os2Weight == weight,
                $"{file}: arquivo oficial, família {family} e peso {weight}");
        }
        foreach (var family in new[] { "IBM Plex Sans", "Barlow Condensed", "IBM Plex Mono" })
            assert(app.Contains("component/Fonts/#" + family + "</FontFamily>"), "O App.xaml referencia a fonte embutida " + family);
        var notice = File.ReadAllText(Path.Combine(sources, "NOTICE.md"));
        assert(expected.All(item => notice.Contains(item.Key) && notice.Contains(item.Value.Hash)) && notice.Contains("Open Font License"),
            "O aviso de terceiros das fontes cita cada arquivo, o hash e a OFL");
        assert(new[] { "BarlowCondensed-OFL.txt", "IBMPlexSans-OFL.txt", "IBMPlexMono-OFL.txt" }.All(file =>
            File.ReadAllText(Path.Combine(sources, file)).Contains("SIL OPEN FONT LICENSE Version 1.1")), "O texto da OFL de cada família acompanha o Nexo");

        // Tamanho mínimo de 11 px no XAML e no código.
        var sizes = Regex.Matches(window, @"FontSize=""([\d.]+)""").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Concat(Regex.Matches(code, @"FontSize = ([\d.]+)").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .Concat(Regex.Matches(code, @"\bText\([^;\n]*?, ([\d.]+)(?:, ""\w+"")?\)").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        assert(sizes.Length > 20 && sizes.Min() >= 11, "Nenhum texto da interface fica abaixo de 11 px");

        // Logo: geometrias compartilhadas pelo XAML e pelo UiIcons, e a marca antiga (estrela) saiu.
        assert(new[] { "LogoSoftLine", "LogoSoftNodes", "LogoStrongBar", "LogoStrongNodes" }.All(key => app.Contains("x:Key=\"" + key + "\"")
            && window.Contains("{StaticResource " + key + "}") && code.Contains("\"" + key + "\"")), "A logo nova usa as mesmas geometrias na lateral e nos ícones");
        assert(!app.Contains("M12,1 L14.7,9.3") && !window.Contains("M12,1 L14.7,9.3") && !code.Contains("M12,1 L14.7,9.3"), "A marca antiga em estrela foi substituída");
    }

    private static Dictionary<string, string> Palette(string path)
        => XDocument.Load(path).Root!.Elements().Where(e => e.Name.LocalName == "SolidColorBrush")
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => ((string)e.Attribute("Color")!).ToUpperInvariant(), StringComparer.Ordinal);

    // Lê os nomes de família (IDs 1 e 16, plataforma Windows) e o peso da tabela OS/2.
    private static (HashSet<string> Names, int Weight) FontInfo(byte[] data)
    {
        static int U16(byte[] d, int o) => d[o] << 8 | d[o + 1];
        static int U32(byte[] d, int o) => d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3];
        var tables = new Dictionary<string, int>();
        for (var i = 0; i < U16(data, 4); i++) tables[Encoding.ASCII.GetString(data, 12 + 16 * i, 4)] = U32(data, 20 + 16 * i);
        var name = tables["name"]; var count = U16(data, name + 2); var strings = name + U16(data, name + 4);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var record = name + 6 + 12 * i;
            if (U16(data, record) == 3 && U16(data, record + 4) == 0x409 && U16(data, record + 6) is 1 or 16)
                names.Add(Encoding.BigEndianUnicode.GetString(data, strings + U16(data, record + 10), U16(data, record + 8)));
        }
        return (names, U16(data, tables["OS/2"] + 4));
    }
}
