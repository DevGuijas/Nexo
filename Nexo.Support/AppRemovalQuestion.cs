using System.Text.RegularExpressions;

namespace Nexo.Support;

/// <summary>
/// Pergunta sobre remover um item conhecido (#127, validação no PC da 3a88880, 23/11/2026): depois da orientação do cartão,
/// "e o Xbox, posso tirar?" recebeu do modelo um passo a passo inventado ("Configurações > Sistema > Xbox > Remover"), sem o
/// aviso do login do Minecraft e do Game Pass, e uma oferta de "encontrar outros apps que você não usa". Para um item do
/// catálogo da #127, o Nexo responde com o próprio texto: o nome do item como aparece no cartão, se vem sugerido ou opcional,
/// o aviso dele e o caminho do cartão, sem dizer que o item existe no PC (quem confere é o "Verificar este PC"). Para um alvo
/// que a #127 deixa de fora de propósito (OneDrive, Store, Câmera…), diz que o cartão não o remove e por quê, sem indicar outro
/// caminho de remoção. Fora dos dois, a pergunta segue com o modelo ("posso tirar o Chrome?").
/// </summary>
public static class AppRemovalQuestion
{
    private const string Card = "“Apps pré-instalados e ajustes do Windows”, no menu Ferramentas";

    // Apelidos por item do catálogo, já normalizados (sem acento, minúsculas). Os mais específicos vêm antes: "login do xbox",
    // "game bar" e "serviços do xbox" antes de "xbox"; "botão de widgets" antes de "widgets".
    private static readonly (string Id, string Pattern)[] ItemAliases =
    [
        ("xbox-login", @"login do xbox|xbox identity|identity provider|xbox tcui"),
        ("xbox-game-bar", @"game ?bar|barra de jogos?|sobreposic\w* do xbox|xbox overlay"),
        ("xbox-services", @"servicos? do xbox|xbox live|xblgamesave|xboxnetapisvc"),
        ("widgets-button", @"botao (?:de |dos |do )?widgets?"),
        ("widgets-package", @"widgets?|web experience"),
        ("xbox-app", @"xbox|gaming app"),
        ("bing-news", @"noticias|bing news|microsoft news"),
        ("bing-weather", @"clima|bing weather|app do tempo"),
        ("tips", @"dicas|getstarted"),
        ("solitaire", @"solitaire|paciencia"),
        ("maps", @"mapas"),
        ("feedback-hub", @"hub de comentarios|feedback hub"),
        ("office-lens", @"office lens"),
        ("print3d", @"print ?3d"),
        ("network-speed", @"teste de velocidade(?: de rede)?|network speed"),
        ("mixed-reality", @"mixed reality|realidade mista"),
        ("cortana", @"cortana"),
        ("bing-search", @"bing search|app (?:do )?bing"),
        ("clipchamp", @"clipchamp"),
        ("linkedin", @"linkedin"),
        ("dev-home", @"dev home"),
        ("family", @"microsoft family|family|controle parental"),
        ("power-automate", @"power automate"),
        ("teams-personal", @"teams"),
        ("copilot-app", @"copilot"),
        ("recall", @"recall"),
        ("sysmain", @"sysmain|superfetch"),
        ("ceip-tasks", @"ceip"),
        ("advertising-id", @"id de publicidade"),
    ];

    // Alvos fora do escopo de propósito (DebloatCatalog.Excluded), com o motivo da #127.
    // Sem pronome: "a Microsoft Store", "a Câmera" e "as Notas Autoadesivas" não concordam com "removê-lo" (@qa).
    private const string BreaksData = "a remoção pode quebrar ou sumir com dados do usuário";
    private const string BreaksSupport = "a remoção quebra o suporte ou a gestão do PC";
    private const string RealUse = "é um app de uso real";
    private static readonly (string Name, string Pattern, string Why)[] ExcludedAliases =
    [
        ("o OneDrive", @"onedrive", BreaksData),
        ("a Microsoft Store", @"(?:microsoft |windows )?store|loja (?:da microsoft|do windows)", BreaksSupport),
        ("o Obter Ajuda", @"obter ajuda|get help", BreaksSupport),
        ("a Assistência Rápida", @"assistencia rapida|quick assist", BreaksSupport),
        ("a pesquisa do Windows", @"windows search|wsearch|pesquisa do windows|busca do windows", BreaksSupport),
        ("a Câmera", @"(?:app|aplicativo) (?:da |de )?camera|camera do windows", RealUse),
        ("o Email e Calendário", @"(?:app|aplicativo) (?:de |do )?(?:e-?mail|calendario)|e-?mail e calendario", RealUse),
        ("o Outlook", @"outlook", RealUse),
        ("o Pessoas", @"(?:app|aplicativo) (?:do |de )?pessoas", RealUse),
        ("as Notas Autoadesivas", @"notas autoadesivas|sticky notes", RealUse),
        ("o Alarmes e Relógio", @"alarmes e relogio|(?:app|aplicativo) (?:de |do )?alarmes?", RealUse),
        ("o Gravador de Som", @"gravador de (?:som|voz)", RealUse),
        ("o OneNote", @"onenote", RealUse),
        ("o Media Player", @"media player|groove", RealUse),
        ("o Filmes e TV", @"filmes e tv", RealUse),
        ("o To Do", @"microsoft to ?do|(?:app|aplicativo) (?:do )?to ?do", RealUse),
        ("o Phone Link", @"phone link|vincular? ao (?:celular|telefone)|seu telefone|your phone", RealUse),
        ("o Microsoft 365 (Office Hub)", @"office hub|microsoft 365", RealUse),
        // O Teams do Windows 10 (MicrosoftTeams) fica de fora; no 11, o Teams pessoal é um item do catálogo.
        ("o Teams do Windows 10", @"teams", RealUse),
    ];

    // O verbo de remover no presente, no infinitivo ou no imperativo; o passado ("removi", "tirou") relata o que o usuário fez.
    private const string VerbCore = @"(?:remov|desinstal|tir|exclu|apag|delet|elimin|arranc)(?!(?:ei|uei|ou|eu|iu|i|eram|aram|iram)\b)(?!\w*(?:id[oa]s?|ad[oa]s?)\b)\w*";
    private const string Verb = @"\b" + VerbCore;
    // Não contam: reinstalar ou ter de volta, tirar da inicialização, tirar um print ou tirar foto, e o fluxo de driver
    // ("a câmera não funciona, posso remover o driver?").
    private const string NotRemoval = @"\b(?:reinstal\w*|de volta|inicializac\w*|startup|segundo plano|abre\w* junto|abrem junto|iniciam? junto|prints?|fotos?|screenshots?|capturas?|drivers?|dispositivos?|atualizac\w*|updates?|notificac\w*)\b";
    // O item é o objeto do verbo, numa ordem ou na outra: "posso tirar o Xbox?", "dá pra desinstalar a Cortana?", "e o Xbox,
    // posso tirar?". Remover algo DO app ou o app DE um lugar não conta (@qa, gate da d8a2cce): "remover a senha do Xbox",
    // "excluir e-mails do Outlook", "como tiro a conta do Outlook?", "remover o ícone do Teams", "tirar o clima da barra de
    // tarefas", "como tirar a Cortana do Iniciar?", "remover o family da minha conta", "o OneDrive está cheio, posso apagar
    // arquivos?", "a store não abre, removo o cache?". "Notícias e interesses" é o painel da barra do Windows 10, não o app.
    private const string Determiner = @"(?:(?:o|a|os|as|esse|essa|esses|essas|este|esta|estes|estas|aquele|aquela|meu|minha|meus|minhas|seu|sua|seus|suas)\s+)?";
    private const string AppWord = @"(?:(?:app|aplicativo|programa)s?\s+(?:(?:do|da|de)\s+)?)?";
    private const string NotPlace = @"(?!\s+(?:e\s+interesses\b|(?:da|do|de|das|dos|na|no|nas|nos)\s+(?:(?:minha|meu|sua|seu)\s+)?(?:barra|iniciar|menu|tela|area|conta|inicializac|bandeja|desktop|lista|calendario|celular|telefone|navegador)))";
    private const string ObjectEnd = @"(?:-(?:lo|la|los|las))?(?=\s*(?:[?.!,;:]|$)|\s+(?:ele|ela|eles|elas|isso|tambem|agora|mesmo|de vez|do (?:pc|computador|windows|sistema))\b)";
    private static string Forward(string pattern) => Verb + @"(?:-(?:lo|la|los|las))?\s+" + Determiner + AppWord + @"(?:" + pattern + @")\b" + NotPlace;
    private static bool Mentions(string text, string pattern) => Regex.IsMatch(text,
        Forward(pattern) + @"|\b(?:" + pattern + @")\b" + NotPlace + @"\W+(?:[\w-]+\W+){0,5}?" + VerbCore + ObjectEnd);
    private static readonly Regex AnyItemObject = new(Forward(string.Join("|", ItemAliases.Select(alias => alias.Pattern))));
    /// <summary>A frase remove um item do catálogo, como objeto do verbo ("para remover o Xbox", "desinstale a Cortana"), sem
    /// negação logo antes ("não remova o Xbox"). Usada pela trava do passo a passo de remoção inventado.</summary>
    public static bool RemovesCatalogItem(string normalized) => AnyItemObject.Matches(normalized)
        .Any(match => !Regex.IsMatch(normalized[..match.Index], @"\b(?:nao|nem|nunca|evite)\s+(?:\w+\s+){0,2}$"));


    /// <summary>O texto determinístico para a pergunta, ou null quando ela não é sobre remover um item conhecido.</summary>
    public static string? Answer(string request, int build)
    {
        var text = AgentProtocol.Normalize(request);
        if (Regex.IsMatch(text, NotRemoval)) return null;
        var windows11 = DebloatCatalog.IsWindows11(build);
        foreach (var (id, pattern) in ItemAliases)
        {
            if (!Mentions(text, pattern)) continue;
            // O Teams do 10 é excluído; o do 11 é o item "Microsoft Teams (pessoal)".
            if (id == "teams-personal" && !windows11) break;
            var item = DebloatCatalog.Find(id)!;
            return item.Applies(build) ? ItemText(item) : NotOnThisWindows(item);
        }
        foreach (var (name, pattern, why) in ExcludedAliases)
            if (Mentions(text, pattern))
                return "O cartão " + Card + ", não remove " + name + " de propósito: " + why + ". O Nexo também não remove apps pelo chat.";
        return null;
    }

    private static string ItemText(DebloatItem item)
    {
        var parts = new List<string>
        {
            (item.Kind == DebloatKind.App ? "O Nexo não remove apps pelo chat. “" : "O Nexo não aplica esse ajuste pelo chat. “") + item.Title + "” é um item do cartão " + Card + ", e vem "
                + (item.Default ? "sugerido, já marcado." : "como opcional, desmarcado."),
        };
        if (item.Warning.Length > 0) parts.Add("Aviso do cartão: " + item.Warning);
        if (item.Gate == DebloatGate.XboxGames)
            parts.Add("Com o app Xbox, o Minecraft ou a Game Bar instalados, o cartão só remove esse item com a sua confirmação explícita.");
        if (item.Id == "xbox-app")
            parts.Add("O login do Xbox é outro item, “Login do Xbox”: sem ele, o login do Minecraft e do Game Pass para de funcionar, e com o app Xbox, o Minecraft ou a Game Bar instalados o cartão só o remove com a sua confirmação explícita.");
        parts.Add("Clique em “Verificar este PC”: o item só aparece se existir neste PC, e nada muda antes de você confirmar.");
        return string.Join(" ", parts);
    }

    private static string NotOnThisWindows(DebloatItem item) =>
        "“" + item.Title + "” é um item do cartão " + Card + ", só no "
        + (item.Windows == DebloatWindows.Windows11 ? "Windows 11" : "Windows 10") + "; neste Windows o cartão não o oferece. O Nexo não remove apps pelo chat.";
}
