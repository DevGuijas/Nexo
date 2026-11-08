using System.Text.RegularExpressions;

namespace Nexo.Support;

// A read-only starting observation, not a fixed repair plan.
public static class SupportIntake
{
    // Pedidos de falta de espaço (#59). "espaço" sozinho também é a tecla do teclado ("barra de espaço"), e
    // "cheio" sozinho aparece em "cheio de vírus": por isso as duas palavras só contam em combinação.
    private const string Space = @"\b(sem|pouco|falta de|acabou o|acabando o|liberar|libere)\s+espaco\b|\bespaco\s+(livre|em disco|no disco|do disco|no hd|no ssd|no pc|no computador|no notebook|de armazenamento|insuficiente)\b"
        + @"|\bnao\s+(ha|existe)\s+espaco\b|\bespaco\s+((ja|ta|esta)\s+)?acab\w*|\bsem\s+memoria\b|\bmemoria\s+((ta|esta|ficou)\s+)?(cheia|lotada|acabando)\b"
        + @"|\barmazenamento\b|\b(disco|hd|ssd|unidade|particao)\s+(\w+\s+){0,3}?(cheio|cheia|lotado|lotada)\b|\b[c-h]:\s+(\w+\s+){0,2}?(cheio|cheia|lotado|lotada)\b"
        + @"|\b(pc|computador|notebook|note)\s+(\w+\s+){0,2}?(cheio|lotado)\b(?!\s+de\s+(virus|malware|spyware|propaganda|anuncios?|janelas?|erros?|problemas?))"
        + @"|\bnao\s+(consigo|da para|da pra|deixa)\s+salvar\b(?!\s+((a|as|minha|minhas)\s+)?(senhas?|configurac\w*|alterac\w*|login))";

    // Lentidão e travamento (#59, cenário 5). "trava" solta também é peça ("a trava da câmera") e "travando o
    // papel" é a impressora: esses usos não contam. O componente citado continua escolhendo a observação inicial.
    private const string Slowness = @"\b(lento|lenta|lentos|lentas|lentidao|devagar|lerdo|lerda|lerdeza|travando|travado|travada|travou|travamentos?|congelando|congelado|congelada|congelou|congela|demorando|demorado|demorada|demora|demoram|demorar)\b"
        + @"|(?<!\b(a|da|na)\s)\btrava\b(?!\s+d[aeo]s?\b)|\b(pc|computador|notebook|note|windows|sistema|maquina)\s+(\w+\s+){0,3}?pesad[oa]\b";
    private const string PaperJam = @"\bpapel\s+(\w+\s+)?trav\w*|\btrav\w*\s+(o\s+)?papel\b";
    // Sujeito que é o próprio computador: "o pc está lento" é do sistema mesmo junto de outro componente.
    private const string Machine = @"\b(pc|computador|notebook|note|desktop|windows|sistema|maquina|tudo)\b";

    // Lentidão e falta de espaço são do sistema quando falam do computador. "A impressora está demorando" e
    // "a internet está lenta" ficam com o componente que citam.
    private static bool SystemSymptom(string text, HashSet<string> components) =>
        (Regex.IsMatch(text, Space) || Regex.IsMatch(text, Slowness) && !Regex.IsMatch(text, PaperJam))
        && (Regex.IsMatch(text, Machine) || components.All(topic => topic == "system") && !WebSymptom(text));

    // Rede por sintoma do navegador (#63, cenário 3). "site" e "página" aparecem em pedidos que não são de rede
    // ("baixei o driver no site da HP", "a página do documento não carrega"): nada conta quando o pedido fala de
    // documento, arquivo, impressora ou configurações, e a falha precisa estar perto do objeto.
    private const string NotWeb = @"\b(documentos?|word|excel|powerpoint|pdf|planilhas?|relatorios?|arquivos?|impressoras?|impressao|imprim\w*|em branco|configurac\w*)\b"
        + @"|\bbaix\w*\s+(\w+\s+){0,3}?(do|no|pelo|num|de um)\s+site\b";
    // "página" só conta no plural ("as páginas não carregam"), com "nenhuma" ou "da internet": "a página inicial" não.
    private const string Target = @"(sites?|google|youtube|gmail|paginas da internet|pagina da internet)";
    private const string Fails = @"(nao|n|nem)\s+(\w+\s+)?(abr|carreg)\w*";
    // Depois do "não abre/não carrega" (ou do alvo, quando ele é o objeto), só pode vir (#63, revisões do @qa):
    // - palavras vazias ("nada", "mais", "nenhum", "direito", "de jeito nenhum", "pra mim"), o fim ou o fim da frase;
    // - vírgula ou "nem" seguidos de outro alvo web ("nenhum site abre, nem o google");
    // - "e/mas" com uma oração que tem sujeito próprio ("e o anexo também não", "mas o word abre");
    // - tempo ("desde ontem", "faz dois dias", "há uma semana", "hoje" no fim) ou "no pc/notebook".
    // Um objeto depois do verbo tira a rede ("o gmail não abre o anexo"), e "e não baixa o anexo", "nem o anexo",
    // "agora o boleto", "quando ligo a câmera" ou "no celular" também: falam de outra coisa.
    private const string WebWord = @"(sites?|google|youtube|gmail)";
    private const string Filler = @"(\s+(nada|mais|nenhum[a]?|direito|de jeito nenhum|nunca|tambem|ainda|aqui|pra mim|para mim))*";
    private const string Ends = @"(?=" + Filler + @"\s*($|[.;!?]"
        + @"|,\s*($|(nem\s+)?((o|a|os|as)\s+)?" + WebWord + @"\b)"
        + @"|,?\s+(e|mas)\s+(o|a|os|as|meu|minha|meus|minhas)\s+\w+"
        + @"|,?\s+nem\s+((o|a|os|as)\s+)?" + WebWord + @"\b"
        + @"|\s+(desde|ha|faz)\b|\s+(hoje|ontem|agora)\s*($|[.,;!?])|\s+n[oa]\s+(pc|computador|notebook)\b))";
    // O alvo é o sujeito, não o complemento de outra coisa: "o jogo do google play não abre" fala do jogo (@qa, revisão final).
    private const string Owned = @"(?<!\b(do|da|dos|das|no|na|nos|nas|pelo|pela)\s)";
    // Quando o alvo é o objeto, quem falha é o navegador, o computador ou ninguém citado: "o jogo não carrega o youtube" e
    // "o pendrive não carrega o site" falam do jogo e do pendrive.
    private const string Subject = @"(?<!\b(o|a|os|as|meu|minha|meus|minhas)\s+(?!(chrome|edge|firefox|navegador|browser|pc|computador|notebook)\b)\w+\s+)";
    // O nome do site pode vir depois do alvo ("não abre o site da receita"), desde que não seja um componente ("o site da câmera").
    private const string SiteName = @"(\s+(do|da|dos|das|de)\s+(?!(camera|webcam|microfone|fone|som|audio|impressora|bluetooth|celular|tablet)\b)\w+)?";
    private static readonly string[] WebPatterns =
    [
        @"\b" + Owned + Target + @"\b[^.,;!?]{0,30}?\b" + Fails + Ends,
        @"\b(as|todas as|nenhuma)\s+paginas?\b[^.,;!?]{0,20}?\b" + Fails + Ends,
        Subject + @"\b" + Fails + @"\s+((o|os)\s+)?" + Target + @"\b" + SiteName + Ends,
        Subject + @"\b" + Fails + @"\s+(as\s+)?paginas\b" + Ends,
        Subject + @"\b" + Fails + @"\s+nenhum[a]?\s+(sites?|paginas?)\b" + Ends,
        @"\bnenhum[a]?\s+(sites?|paginas?)\s+(\w+\s+)?(abr|carreg|funcion)\w*" + Ends,
        @"\b(sites?|paginas?)\s+nenhum[a]?\s+(abr|carreg)\w*" + Ends,
        @"\bnao\s+(consigo|to conseguindo|estou conseguindo|da pra|da para)\s+(entrar|acessar|abrir)\s+((em|no|nos|o|os)\s+)?(nenhum[a]?\s+)?(sites?|google|youtube|gmail)\b" + Ends,
        @"\b(chrome|edge|firefox|navegador(es)?|browser)\b[^.,;!?]{0,20}?\b(nao|n)\s+(\w+\s+)?carreg\w*" + Ends,
        @"\bnada\s+(abre|carrega)\s+(\w+\s+){0,2}?(no|pelo)\s+(navegador|chrome|edge|firefox)\b",
        @"\b(nao|n)\s+(\w+\s+){0,2}?navegar\b(?=\s*($|[.,;!?])|\s+(na internet|em nada|em nenhum|em sites?|nos sites))",
        @"\b(dns_probe\w*|err_name_not_resolved|err_internet_disconnected|err_connection_\w+|err_address_unreachable|err_timed_out|err_network_changed)\b",
        @"\b(sites?|google|youtube|gmail)\b[^.,;!?]{0,25}?\bfora do ar\b",
        @"\b(sites?|google|youtube)\b[^.,;!?]{0,20}?\b(nao|n)\s+(\w+\s+)?funcion\w*" + Ends,
        @"\b(sites?|paginas da internet|google|youtube|navegador)\b[^.,;!?]{0,25}?\b(demor\w*|lent[oa]s?|lentidao)\b",
    ];

    private static bool WebSymptom(string text) => !Regex.IsMatch(text, NotWeb) && WebPatterns.Any(pattern => Regex.IsMatch(text, pattern));

    // "O instalador pediu reinicialização; prepare para eu retomar" pede o ponto de retomada,
    // não uma nova busca ou instalação. Uma ordem de atualizar o driver continua sendo pedido
    // de driver, mas "depois de atualizar" e "para eu instalar" só descrevem o contexto.
    public static bool AsksRestartResume(string request)
    {
        var text = AgentProtocol.Normalize(request);
        return Regex.IsMatch(text, @"\b(reinici\w*|restart\w*|reboot\w*)\b")
            && Regex.IsMatch(text, @"\b(prepar\w*|retom\w*)\b")
            && !Regex.IsMatch(text, @"(?<!\b(ao|apos|depois de|para eu) )\b(atualiz(a|e|ar|em)|instal(a|e|ar|em)|busc(a|ar)|busqu(e|em)|procur(a|e|ar|em)|verific(a|ar)|verifiqu(e|em))\b");
    }

    // Componentes citados pelo nome. Também conferem se uma resposta que diz "verifiquei X" tem consulta
    // bem sucedida daquele componente; sintomas como "lento" ou "o site não abre" não são componentes.
    public static HashSet<string> ComponentTopics(string request)
    {
        var text = AgentProtocol.Normalize(request);
        var topics = new HashSet<string>();
        if (Regex.IsMatch(text, @"\b(internet|rede|conexao|network|wireless|wifi|wi-fi|ethernet|dns|dhcp|ipv4|gateway|adaptador de rede|cache dns|wlan)\b")) topics.Add("network");
        if (Regex.IsMatch(text, @"\b(camera|webcam|imagem da camera)\b")) topics.Add("camera");
        if (Regex.IsMatch(text, @"\b(audio|media|sound|som|microfone|fone|alto.falante|audiosrv)\b")) topics.Add("audio");
        if (Regex.IsMatch(text, @"\b(bluetooth|bthserv)\b")) topics.Add("bluetooth");
        if (Regex.IsMatch(text, @"\b(impressoras?|imprimir|impressao|printer|print queue|fila de impressao|spooler)\b")) topics.Add("printer");
        if (Regex.IsMatch(text, @"\b(lentidao|desempenho|memoria|disco|processos)\b")) topics.Add("system");
        if (Regex.IsMatch(text, @"\b(hardware|drivers?|controladores?|placa de video|gpu|dispositivo desconhecido|gerenciador de dispositivos)\b")) topics.Add("hardware");
        return topics;
    }

    // Componente que a mensagem diz estar resolvido ("mas resolveu a internet", "o wifi voltou ao normal") não é mais o
    // incidente. Contam só "voltou a funcionar", "voltou ao normal", "resolveu", "está funcionando", "funciona agora" e
    // "normalizou", na mesma oração do componente e sem negação. "voltou a cair" é "de novo", "funcionou ontem" e "tá ok
    // no celular" não dizem que voltou, e uma reclamação sem outro componente depois do marcador ("voltou a funcionar,
    // mas tá chiando") continua falando do mesmo componente, que fica. Uma ressalva na mesma oração ("tá funcionando mal",
    // "só tá funcionando no celular") também mantém o componente (@qa, terceira revisão e revisões finais).
    private const string Resolved = @"\b(volt(ou|aram) a funcionar|volt(ou|aram) ao normal|resolveu|resolvid[oa]|(esta|ta|estao|tao) funcionando|funcionam? agora|normalizou)\b";
    private const string Complaint = @"\b(nao|n|nem|falh\w*|trav\w*|lent[oa]s?|lentidao|devagar|demor\w*|chia\w*|ruim|cai\w*|desconect\w*|erro|oscil\w*|picot\w*)\b";
    // Ressalva na própria oração do marcador: "tá funcionando devagar", "tá funcionando mal", "só tá funcionando no celular".
    private const string Partly = @"\b(mal|so|somente|apenas|pela metade|meio|as vezes)\b";
    private static HashSet<string> ResolvedComponents(string text)
    {
        var resolved = new HashSet<string>();
        var clauses = Regex.Split(text, @"[,;.!?]|\s+(?:mas|e|porem|so que)\s+");
        for (var i = 0; i < clauses.Length; i++)
        {
            // Na própria oração, a negação ("não voltou ao normal", que a Complaint já inclui), outra reclamação ("tá funcionando
            // muito lenta") ou uma ressalva ("tá funcionando mal") mantêm o componente, além das orações seguintes.
            if (!Regex.IsMatch(clauses[i], Resolved) || Regex.IsMatch(clauses[i], Complaint) || Regex.IsMatch(clauses[i], Partly)) continue;
            var later = clauses.Skip(i + 1).Where(clause => Regex.IsMatch(clause, Complaint)).ToArray();
            if (later.Any(clause => ComponentTopics(clause).Count == 0)) continue;
            resolved.UnionWith(ComponentTopics(clauses[i]));
        }
        return resolved;
    }

    // Tópicos do incidente, que liberam as alterações: os componentes citados e os sintomas comuns que apontam para eles.
    public static HashSet<string> IncidentTopics(string request)
    {
        var text = AgentProtocol.Normalize(request);
        var topics = ComponentTopics(request);
        topics.ExceptWith(ResolvedComponents(text));
        var components = topics.ToHashSet();
        if (WebSymptom(text)) topics.Add("network");
        if (SystemSymptom(text, components)) topics.Add("system");
        return topics;
    }

    // Continuação só com sintoma genérico do sistema ("continua lento", "ainda tá demorando"): numa conversa que já
    // tinha tópico, a mensagem soma o sistema ao que veio antes, em vez de trocar. Exige a marca de continuação; quem
    // fala do computador, de outro componente, agradece ou diz que resolveu começa outro assunto (#59/#63, @qa).
    private const string Continues = @"\b(continua\w*|ainda|de novo|denovo|segue|seguindo|mesmo problema|novamente|outra vez|persiste)\b";
    private const string NewSubject = @"\b(pc|computador|notebook|note|desktop|windows|sistema|maquina|obrigad\w*|valeu|resolveu|resolvido|funcionou|deu certo|outra coisa)\b";
    public static bool OnlyGenericSymptom(string request)
    {
        var text = AgentProtocol.Normalize(request);
        var components = ComponentTopics(request);
        components.ExceptWith(ResolvedComponents(text));
        return components.Count == 0 && !WebSymptom(text) && SystemSymptom(text, components)
            && Regex.IsMatch(text, Continues) && !Regex.IsMatch(text, NewSubject);
    }

    // Assunto do sistema no pedido, para ordenar a recomendação do monitor: falta de espaço ou lentidão.
    public static string? SystemFocus(string request)
    {
        var text = AgentProtocol.Normalize(request);
        return Regex.IsMatch(text, Space) ? "space" : Regex.IsMatch(text, Slowness) && !Regex.IsMatch(text, PaperJam) ? "slowness" : null;
    }

    // Tópicos do incidente numa conversa: os da última mensagem do usuário que tinha tópico, somados aos anteriores
    // enquanto a mensagem for só uma continuação genérica do sistema. O que uma mensagem mais nova diz ter voltado
    // ("a internet voltou, mas ainda tá lento") não volta a ser herdado.
    public static HashSet<string> ConversationTopics(IEnumerable<string> userMessagesNewestFirst)
    {
        var topics = new HashSet<string>();
        var resolved = new HashSet<string>();
        foreach (var said in userMessagesNewestFirst)
        {
            resolved.UnionWith(ResolvedComponents(AgentProtocol.Normalize(said)));
            var own = IncidentTopics(said);
            own.ExceptWith(resolved);
            if (own.Count == 0) continue;
            topics.UnionWith(own);
            if (!OnlyGenericSymptom(said)) break;
        }
        return topics;
    }

    public static string? InitialTopic(string request)
    {
        var text = AgentProtocol.Normalize(request);
        var hasSymptom = Regex.IsMatch(text, @"\b(nao\s+(funciona|aparece|conecta|imprime|consigo|liga)|sem\s+(som|audio|imagem|camera|microfone|bluetooth)|sumiu|desapareceu|preso|presos|presa|travou|travada|problema|falha|parou|para quando|desconectar e conectar)\b");
        var hardwareRequest = Regex.IsMatch(text, @"\b(hardware|drivers?|controladores?|placa de video|gpu|dispositivo desconhecido|gerenciador de dispositivos)\b")
            && Regex.IsMatch(text, @"\b(atualiz\w*|instal\w*|verific\w*|detect\w*|diagnostic\w*)\b")
            && !AsksRestartResume(request);
        var systemSymptom = Regex.IsMatch(text, Space) || Regex.IsMatch(text, Slowness);
        if (!hasSymptom && !hardwareRequest && !systemSymptom) return null;
        if (Regex.IsMatch(text, @"\b(camera|webcam)\b")) return "camera";
        if (Regex.IsMatch(text, @"\b(impressoras?|imprimir|impressao)\b")) return "printer";
        if (Regex.IsMatch(text, @"\b(som|audio|microfone|fone)\b")) return "audio";
        if (text.Contains("bluetooth")) return "bluetooth";
        if (Regex.IsMatch(text, @"\b(hardware|drivers?|controladores?|placa de video|gpu|dispositivo desconhecido|gerenciador de dispositivos)\b")) return "hardware";
        // "A internet está lenta" e "o site está demorando" são de rede, que tem o próprio fluxo; o sistema fica para o PC.
        // O componente que a mensagem diz estar resolvido não segura a observação do sistema ("ainda tá lento, mas resolveu a internet").
        var open = ComponentTopics(request);
        open.ExceptWith(ResolvedComponents(text));
        if (SystemSymptom(text, open)) return "system";
        return null;
    }
}
