using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nexo.Support;

// #143, etapas 4 a 6: páginas no visual novo (Saúde do PC, Assistente, Ferramentas, Atendimentos, Encaminhamentos, Registro e
// Configurações). O WPF não roda no Mac nem no CI de checks: o teste lê o código das páginas como texto. A renderização e as
// posições ficam no --screenshot do Windows (scripts/Check-UiPreview.ps1 -Theme onyx|glacial).
internal static class PageLayoutChecks
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // Textos fixos do protótipo do v0 que não podem chegar ao Nexo (#143, "Só dados reais"): versão, máquina, processador, processo
    // de teste, o diálogo do Spooler num pedido de lentidão, o T: contraditório, limite de memória em %, intervalo fixo da sondagem,
    // itens de debloat fora do catálogo da #127 e integração com GLPI/Jira.
    internal static readonly string[] PrototypeTexts =
    [
        "2.8.4", "Memória 90%", "Memória 90 %", "a cada 60 s", "DESKTOP-VICTOR", "Ryzen 7 5700X", "NexoMemTeste-toca", "7.193 MB",
        "Posso reiniciar o serviço de impressão", "Spooler reiniciado", "Volume T: desconectado", "Disco virtual VHDX", "17,3 de 32 GB",
        "63,8", "Windows 11 Pro 24H2", "Atualizado há 30 s", "Telemetria opcional", "Apps em segundo plano", "Xbox Login", "GLPI", "Jira",
        "Ver detalhes", "Seu PC, suas regras"
    ];

    internal static void Run(Action<bool, string> assert)
    {
        var sources = Path.Combine(AppContext.BaseDirectory, "ThemeSources");
        var code = Path.Combine(sources, "Code");
        var window = File.ReadAllText(Path.Combine(sources, "MainWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(sources, "MainWindow.xaml"));
        var kit = File.ReadAllText(Path.Combine(code, "PageKit.cs"));
        var live = File.ReadAllText(Path.Combine(code, "LiveMetricsPanel.cs"));
        var pages = new Dictionary<string, string> { ["MainWindow.xaml.cs"] = window, ["PageKit.cs"] = kit, ["LiveMetricsPanel.cs"] = live };

        // 1. Nenhum texto abaixo de 11 px nas páginas: literais de FontSize, o tamanho passado a Text(...) e Mono(...), e nenhum
        // FontSize calculado fora dos próprios helpers (que recebem "size" e são conferidos nas chamadas).
        var small = new List<string>(); var computed = new List<string>(); var sizes = 0;
        foreach (var (file, source) in pages)
        {
            foreach (Match match in Regex.Matches(source, @"FontSize\s*=\s*([^,;}\s)]+)"))
            {
                var value = match.Groups[1].Value;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)) { sizes++; if (size < 11) small.Add(file + ": " + match.Value); }
                else if (value != "size") computed.Add(file + ": " + match.Value);
            }
            foreach (var size in SizeArguments(source)) { sizes++; if (size.Value < 11) small.Add(file + ": " + size.Call); }
        }
        foreach (Match match in Regex.Matches(xaml, @"FontSize=""([\d.]+)""")) { sizes++; if (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) < 11) small.Add("MainWindow.xaml: " + match.Value); }
        assert(sizes > 80 && small.Count == 0, "#143: nenhum texto das páginas abaixo de 11 px" + (small.Count > 0 ? ": " + string.Join(" | ", small) : ""));
        assert(computed.Count == 0, "#143: o tamanho do texto das páginas é literal ou vem do helper conferido" + (computed.Count > 0 ? ": " + string.Join(" | ", computed) : ""));
        assert(SizeArguments("x = Text(\"a\", 10); y = Mono($\"{free:F1}% livre\", 13, low ? \"Warning\" : \"Ink\"); z = Text(item.Title, 17);")
                .Select(s => s.Value).SequenceEqual([10d, 13d, 17d]),
            "#143: a leitura do tamanho entende interpolação, parênteses e cor condicional");
        // O corpo fica entre 13 e 14 px: o padrão de Text e Mono, a descrição das páginas e os textos dos avisos e das linhas.
        assert(window.Contains("TextBlock Text(string text, double size = 14,") && window.Contains("TextBlock Mono(string text, double size = 13,")
            && window.Contains("var subtitle = Text(description, 14, \"Secondary\");") && window.Contains("content.Children.Add(Text(description, 14, \"Secondary\"));"),
            "#143: o corpo das páginas usa 13–14 px (padrão do texto, descrição da página e estado vazio)");
        assert(Regex.Matches(kit, @"Text = (?:detail|label|value), FontSize = (\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToArray() is { Length: 3 } bodySizes && bodySizes.All(s => s is 13 or 14),
            "#143: os avisos e as linhas de rótulo e valor usam o corpo de 13 px");

        // 2. Nenhum texto fixo do protótipo nas fontes da interface.
        var interfaceSources = new Dictionary<string, string>(pages) { ["MainWindow.xaml"] = xaml, ["App.xaml"] = File.ReadAllText(Path.Combine(sources, "App.xaml")),
            ["UiIcons.cs"] = File.ReadAllText(Path.Combine(sources, "UiIcons.cs")) };
        var leftovers = interfaceSources.SelectMany(source => PrototypeTexts.Where(text => source.Value.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(text => source.Key + ": " + text)).ToArray();
        assert(leftovers.Length == 0, "#143: nenhum texto fixo do protótipo na interface" + (leftovers.Length > 0 ? ": " + string.Join(", ", leftovers) : ""));
        assert(!Regex.IsMatch(window + kit, @"""[^""\n]*a cada \d+ s"), "#143: o intervalo mostrado vem das preferências, nunca de um número fixo no texto");
        var settings = Method(window, "private void Settings()");
        assert(settings.Length > 0 && new[] { "IntervalSeconds", "RetentionDays", "CpuHighPercent", "DiskLowFreePercent", "ProcessMemoryGrowthPoints", "ProcessMemorySamples",
                "CheckGateway", "CheckDns", "CheckExternal", "BackgroundMode" }.All(setting => settings.Contains("healthSettings." + setting)),
            "#143: Configurações mostra os limites e opções que existem em HealthSettings");
        assert(!Regex.IsMatch(settings, @"Field\([^;]*""[^""]*mem[oó]ria[^""]*%", RegexOptions.IgnoreCase) && Regex.IsMatch("Field(panel, \"Alerta de memória (%)\", \"90\")", @"Field\([^;]*""[^""]*mem[oó]ria[^""]*%", RegexOptions.IgnoreCase), "#143: Configurações não oferece limite de memória em % (não existe no monitor)");
        assert(settings.Contains("AppVersion.Current") && settings.Contains("healthSettings.IntervalSeconds + \" s"),
            "#143: a versão e a cadência das sondagens em Configurações são as reais");
        var tickets = Method(window, "private void Tickets()");
        assert(tickets.Contains("Nenhum chamado é enviado automaticamente") && tickets.Contains("Exportar relatório .txt") && !Regex.IsMatch(tickets, "(?i)glpi|jira|enviar chamado"),
            "#143: Encaminhamentos é o relatório local exportável, sem envio a ferramenta de chamados");
        var tools = Method(window, "private void Tools()");
        assert(Regex.Matches(tools, @"ToolCard\(\d+,").Count == 3 && Regex.IsMatch(tools, @"ToolCard\(1, [^;]*officeCard\)") && Regex.IsMatch(tools, @"ToolCard\(2, [^;]*optimizeCard\)")
            && Regex.IsMatch(tools, @"ToolCard\(3, ""[^""]+"", ""Apps pré-instalados e ajustes do Windows"", [^;]*debloatCard\)") && tools.Contains("var debloatCard = Card(DebloatCard());"),
            "#143: os cartões numerados de Ferramentas são só as ferramentas da própria tela, com o de apps e ajustes (#127) como o 3º");

        // 3. Estados da Saúde do PC e do Assistente.
        var health = Method(window, "private void Health()");
        assert(health.Contains("\"Histórico operacional indisponível\"") && health.Contains("\"Danger\"") && health.Contains("loading: healthMonitor?.LastError == null")
            && health.Contains("\"Nenhuma ocorrência registrada.\"") && health.Contains("\"Não foi possível ler o histórico operacional\""),
            "#143: a Saúde do PC tem os estados de erro, carregando e vazio");
        assert(health.Contains("Temperatura indisponível neste computador") && health.Contains("temperatureText, failed ? \"Warning\" : \"Neutral\")")
            && health.Contains("State is \"error\" or \"invalid\"") && health.Contains("State == \"invalid\" ? \"Leitura de temperatura descartada\" : failed ? \"Falha na leitura de temperatura\""),
            "#143: temperatura sem driver ou sensor (#142) aparece como indisponível, sem tom de erro; falha de leitura e valor descartado como atenção, cada um com o seu título");
        assert(health.Contains("healthStore == null ? \"Monitoramento local · histórico indisponível\" : \"Monitoramento local · leitura a cada \" + healthSettings.IntervalSeconds"),
            "#143: sem o histórico operacional (monitor parado), a Saúde do PC não promete leitura periódica");
        assert(health.IndexOf("LiveMetricsSection();", StringComparison.Ordinal) is var liveAt && liveAt > 0 && liveAt < health.IndexOf("if (healthStore == null)", StringComparison.Ordinal),
            "#143: o cartão Agora (#141) continua antes do histórico, mesmo com o histórico indisponível");
        assert(health.Contains("healthSettings.DiskLowFreePercent") && health.Contains("\"Temperatura da CPU: \" + HealthReportPresentation.DescribeTemperature(latest)"),
            "#143: a barra do volume usa o limite real de espaço livre e a temperatura mantém o texto do monitor");
        assert(kit.Contains("internal static bool MotionAllowed => SystemParameters.ClientAreaAnimation;") && kit.Contains("if (loading && MotionAllowed)") && window.Contains("BusyBar.Visibility = PageKit.MotionAllowed ? Visibility.Visible : Visibility.Collapsed"),
            "#143: com as animações do Windows desligadas, nenhuma barra indeterminada se move");
        var render = Method(window, "private void RenderEntry(Entry entry)");
        assert(render.Contains("TurnFacts.Body(entry.Text)") && render.Contains("if (facts.Length > 0) body.Children.Add(FactsLine(facts));")
            && render.Contains("if (facts.Length > 0 && shownText.Length == 0) { shownText = facts; facts = \"\"; }") && render.Contains("QuickReplies.For(entry.Text)"),
            "#143: a linha de fatos (#73) ganha faixa própria e os atalhos continuam lendo o texto inteiro");
        // A etapa que não é aviso (ex.: "Nenhum dispositivo encontrado", "Restaurado a seu pedido") não ganha o check verde de sucesso:
        // o Describe só distingue aviso, e fora dele a etapa usa o escudo no acento, como antes da #143.
        var toolEntry = Method(window, "private void RenderToolEntry(Entry entry)");
        assert(toolEntry.Contains("var color = warning ? \"Warning\" : \"Accent\";") && toolEntry.Contains("Brush(warning ? \"WarningSoft\" : \"AccentSoft\")")
            && toolEntry.Contains("UiIcons.Create(\"shield\", 11, Brush(color))") && !Regex.IsMatch(toolEntry, @"""Success(?:Soft)?""|""check""")
            && !ToolResultPresentation.Describe("Identificando hardware e drivers instalados", "{\"success\":true,\"data\":{\"Devices\":[]}}").Warning
            && ToolResultPresentation.Describe("Identificando hardware e drivers instalados", "{\"success\":true,\"data\":{\"Devices\":[]}}").State == "Nenhum dispositivo encontrado",
            "#143: etapa sem aviso, como \"Nenhum dispositivo encontrado\" e \"Restaurado a seu pedido\", fica com o escudo no acento, sem o check verde de sucesso");
        var confirm = Method(window, "private Task<bool> ConfirmChange(ChangeRequest request, Action<Window>? onRendered)");
        assert(confirm.Contains("\"Estado atual:\"") && confirm.Contains("aviso do UAC") && confirm.Contains("detailLines.Where(line => !stateLines.Contains(line))"),
            "#143: o cartão de aprovação destaca o estado atual e avisa do UAC sem repetir o detalhe");
        assert(!Regex.IsMatch(window, @"new Expander\b") && kit.Contains("AutomationProperties.SetItemStatus(toggle, open ? \"expandido\" : \"recolhido\")"),
            "#143: etapas e relatório expansíveis por botão, com estado para o leitor de tela");

        // 4. Acessibilidade: nome acessível nos controles novos e foco visível no estilo dos botões.
        foreach (var (name, body) in new[] { ("ToolCard", Method(window, "private Button ToolCard(")), ("Disclosure", Method(kit, "internal static StackPanel Disclosure(")),
            ("Notice", Method(kit, "internal static Border Notice(")), ("Row", Method(kit, "internal static Grid Row(")) })
            assert(body.Contains("AutomationProperties.SetName("), $"#143: {name} tem nome acessível");
        var app = File.ReadAllText(Path.Combine(sources, "App.xaml"));
        assert(app.Contains("<Trigger Property=\"IsKeyboardFocused\" Value=\"True\"><Setter TargetName=\"Surface\" Property=\"BorderBrush\" Value=\"{DynamicResource Accent}\"/>"),
            "#143: os botões das páginas (cartões, etapas, temas) mostram o foco do teclado com a borda no acento");

        // 5. Contraste dos pares que as páginas usam, nos dois temas: texto 4,5:1 (AA) e barra/selo 3:1 sobre o trilho.
        var text = new (string Text, string Background)[]
        {
            ("Ink", "Surface"), ("Secondary", "Surface"), ("Subtle", "Surface"), ("Accent", "Surface"), ("Warning", "Surface"), ("Success", "Surface"), ("Danger", "Surface"),
            ("Ink", "SurfaceMuted"), ("Secondary", "SurfaceMuted"), ("Accent", "SurfaceMuted"),
            ("Success", "SuccessSoft"), ("Warning", "WarningSoft"), ("Accent", "AccentSoft"), ("Ink", "SuccessSoft"), ("Ink", "WarningSoft"), ("Ink", "UserSurface"),
            ("Secondary", "UserSurface"), ("Accent", "UserSurface"), ("Ink", "CanvasBg"), ("Warning", "CanvasBg"), ("Secondary", "CanvasBg"), ("Ink", "AccentSoft")
        };
        var graphic = new (string Fill, string Track)[] { ("Accent", "Stroke"), ("Warning", "Stroke") };
        foreach (var theme in ThemeCatalog.Names)
        {
            var palette = XDocument.Load(Path.Combine(AppContext.BaseDirectory, ThemeCatalog.ResourcePath(theme))).Root!.Elements()
                .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => (string)e.Attribute("Color")!);
            var failures = text.Where(pair => ThemeCatalog.Contrast(palette[pair.Text], palette[pair.Background]) < 4.5).Select(pair => pair.Text + "/" + pair.Background)
                .Concat(graphic.Where(pair => ThemeCatalog.Contrast(palette[pair.Fill], palette[pair.Track]) < 3).Select(pair => pair.Fill + "/" + pair.Track + " (gráfico)")).ToArray();
            assert(failures.Length == 0, $"#143: contraste dos pares das páginas no tema {ThemeCatalog.DisplayName(theme)}" + (failures.Length > 0 ? ": " + string.Join(", ", failures) : ""));
        }
        var chipPairs = Regex.Matches(Method(kit, "internal static Border Chip("), @"\(""(\w+)"", ""(\w+)""\)").Select(m => (m.Groups[2].Value, m.Groups[1].Value)).ToArray();
        assert(chipPairs.Length == 5 && chipPairs.All(pair => text.Contains(pair) || ThemeCatalog.ReadablePairs.Contains(pair)),
            "#143: todo selo de estado usa um par de cor conferido no contraste AA");
    }

    /// <summary>Tamanho literal passado como 2º argumento de Text(...) e Mono(...); declarações dos helpers ficam de fora.</summary>
    internal static IEnumerable<(double Value, string Call)> SizeArguments(string source)
    {
        foreach (Match match in Regex.Matches(source, @"\b(?:Text|Mono)\("))
        {
            var arguments = Arguments(source, match.Index + match.Length);
            if (arguments.Count < 2 || arguments[0].StartsWith("string ", StringComparison.Ordinal)) continue;
            if (double.TryParse(arguments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
                yield return (size, match.Value + string.Join(", ", arguments) + ")");
        }
    }

    // Separa os argumentos de nível zero a partir do "(" já consumido, ignorando vírgulas dentro de strings, chaves e parênteses.
    private static List<string> Arguments(string source, int start)
    {
        var result = new List<string>(); var depth = 0; var current = new System.Text.StringBuilder();
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '"')
            {
                var end = i + 1;
                while (end < source.Length && source[end] != '"') end += source[end] == '\\' ? 2 : 1;
                current.Append(source, i, Math.Min(end + 1, source.Length) - i); i = end; continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) { result.Add(current.ToString().Trim()); return result; }
                depth--;
            }
            else if (c == ',' && depth == 0) { result.Add(current.ToString().Trim()); current.Clear(); continue; }
            else if (c is ';' or '\n' && depth == 0) return result;
            current.Append(c);
        }
        return result;
    }

    // Corpo de um método: da assinatura até a próxima declaração no mesmo nível de indentação.
    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return "";
        var next = Regex.Match(source[(start + signature.Length)..], @"\n    (?:private|internal|public) ");
        return next.Success ? source.Substring(start, signature.Length + next.Index) : source[start..];
    }
}
