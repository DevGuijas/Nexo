using System.Net;
using System.Text;
using System.Text.Json;
using Nexo.Support;

// Triagem por palavras (#59 e #63): pedidos comuns em português liberam o componente certo e escolhem a
// observação inicial, mensagens curtas herdam o tópico, e a trava de tópico não deixa o modelo repetir.
// As frases reais são as dos cenários 3, 5 e 7 do ensaio de 08/11/2026.
internal static class TriageChecks
{
    internal static async Task Run(Store store, Account actor, Action<bool, string> assert)
    {
        var previousAi = store.Data.AiEnabled;
        store.Data.AiEnabled = true;
        try
        {
            Phrases(assert);
            await SpaceAndSlownessFlows(store, actor, assert);
            await NetworkRequestReachesApproval(store, actor, assert);
            await ContinuationInheritsTopic(store, actor, assert);
            await TopicLockDoesNotLoop(store, actor, assert);
        }
        finally { store.Data.AiEnabled = previousAi; }
    }

    private static void Phrases(Action<bool, string> assert)
    {
        static bool Incident(string text, string topic) => SupportIntake.IncidentTopics(text).Contains(topic);

        // Cenário 7: falta de espaço vai para o sistema, nas duas partes da triagem.
        foreach (var text in new[] { "meu pc ta sem espaco", "Meu PC tá sem espaço", "o computador está sem espaço", "estou com pouco espaço no disco",
            "o armazenamento do notebook acabou", "meu HD está cheio", "o disco C ficou lotado", "o pc tá cheio", "não consigo salvar meus arquivos, nao consigo salvar nada",
            "preciso liberar espaço", "quanto espaço livre tem no disco?" })
        {
            assert(Incident(text, "system"), "Triagem: falta de espaço libera o sistema: " + text);
            assert(SupportIntake.InitialTopic(text) == "system", "Triagem: falta de espaço abre a observação do sistema: " + text);
        }
        // Cenário 5: lentidão e travamento vão para o sistema.
        foreach (var text in new[] { "meu computador esta ficando lento e travado", "o pc tá lento", "tô com o notebook travando", "o computador travou de novo",
            "o Windows está congelando", "o computador ficou travado", "tudo está demorando muito no pc", "meu pc ta devagar", "a máquina fica congelada" })
        {
            assert(Incident(text, "system"), "Triagem: lentidão libera o sistema: " + text);
            assert(SupportIntake.InitialTopic(text) == "system", "Triagem: lentidão abre a observação do sistema: " + text);
        }
        // Cenário 3: sintomas do navegador vão para a rede; a rede não tem observação inicial fixa.
        foreach (var text in new[] { "Os sites que eu tava acessando nao tao abrindo mais", "os sites não abrem", "nenhum site abre", "as páginas não carregam",
            "o navegador não está abrindo as páginas", "não consigo acessar nenhum site", "n abre site nenhum", "o site do banco está fora do ar", "os sites estão demorando",
            "estou sem internet", "o site não funciona" })
        {
            assert(Incident(text, "network"), "Triagem: sintoma do navegador libera a rede: " + text);
            assert(SupportIntake.InitialTopic(text) == null, "Triagem: rede segue para o fluxo próprio, sem observação do sistema: " + text);
        }
        // Falsos positivos: a palavra aparece, mas o pedido não é sobre aquele componente.
        foreach (var text in new[] { "baixei o driver da impressora no site da HP e ela não imprime", "a impressora imprime página em branco e não funciona",
            "a impressora imprime só a primeira página", "quero bloquear um site para meu filho", "o programa não abre", "o arquivo do Word não abre", "o jogo não carrega" })
            assert(!Incident(text, "network"), "Triagem: palavra solta não libera a rede: " + text);
        foreach (var text in new[] { "a barra de espaço do teclado não funciona", "o espaço entre as letras ficou grande", "meu pc está cheio de vírus", "o computador ta cheio de propaganda",
            "Minha câmera não funciona", "Você ajuda com impressoras?" })
            assert(!Incident(text, "system") && SupportIntake.InitialTopic(text) != "system", "Triagem: palavra solta não abre o sistema: " + text);
        // O componente citado continua escolhendo a observação inicial, e a rede lenta não vira lentidão do PC.
        assert(SupportIntake.InitialTopic("a impressora está travada") == "printer" && SupportIntake.InitialTopic("a câmera está travando") == "camera"
            && SupportIntake.InitialTopic("o microfone fica travando") == "audio", "Triagem: componente citado vence o sintoma de lentidão");
        assert(SupportIntake.InitialTopic("a internet está lenta") == null && SupportIntake.InitialTopic("o wifi tá travando") == null
            && Incident("a internet está lenta", "network"), "Triagem: internet lenta é rede, sem observação do sistema");
        // Mensagens curtas não trazem tópico próprio: herdam o da mensagem anterior (conferido no fluxo).
        foreach (var text in new[] { "continue", "denovo", "de novo", "tenta de novo", "ok", "sim" })
            assert(SupportIntake.IncidentTopics(text).Count == 0 && SupportIntake.InitialTopic(text) == null, "Triagem: mensagem curta não tem tópico próprio: " + text);
        // Sintoma não é componente: "verifiquei... os sites não abrem" não exige consulta de rede para a frase ser aceita.
        assert(SupportIntake.ComponentTopics("Verifiquei: o pc está lento e os sites não abrem").Count == 0
            && SupportIntake.ComponentTopics("Verifiquei a rede e o disco").SetEquals(["network", "system"]), "Triagem: sintomas não contam como componente verificado");
        ProbePhrases(assert);
        SecondReview(assert);
        ThirdReview(assert);
        FinalReview(assert);
    }

    // Revisão final do @qa: "voltou a + verbo" é "de novo", "funcionou ontem" e "tá ok no celular" não resolvem, e uma
    // reclamação depois do marcador mantém o componente. Também o alvo como complemento ou objeto de outra coisa.
    private static void FinalReview(Action<bool, string> assert)
    {
        foreach (var (text, expected) in new (string, string)[]
        {
            ("a internet voltou a cair", "network"), ("o wifi voltou a desconectar", "network"), ("a câmera voltou a travar", "camera"), ("o som voltou a falhar", "audio"),
            ("a impressora voltou a dar erro", "printer"), ("o wifi funcionou ontem, hoje não conecta", "network"), ("a internet tá ok no celular mas no pc não", "network"),
            ("o som voltou mas tá chiando", "audio"), ("a internet voltou mas tá muito lenta", "network"), ("o som voltou a funcionar, mas tá chiando", "audio"),
            ("a internet voltou a funcionar mas a câmera não liga", "camera"), ("o wifi voltou ao normal", ""), ("a internet voltou a funcionar", ""),
            ("a internet caiu de novo, ontem tinha voltado", "network"), ("a impressora deu certo uma vez e travou de novo", "printer"),
            ("a internet nem voltou ao normal", "network"), ("o wifi n voltou a funcionar", "network"), ("a internet voltou a funcionar porém a câmera não liga", "camera"),
            ("a internet voltou a funcionar, a câmera não liga", "camera"), ("a internet está funcionando, mas a câmera trava", "camera"), ("o wifi funciona agora", ""),
            ("o jogo do google play não abre", ""), ("o jogo não carrega o youtube", ""), ("o pendrive não carrega o site", ""), ("não abre o site da receita", "network"),
            ("não abre o site da câmera", "camera"), ("o site não abre e meu celular também não", "network"), ("o site não abre,", "network"), ("o site não abre nunca", "network"),
            ("o site não abre aqui", "network"), ("o site não abre também", "network"), ("o pendrive está com vírus e o site não abre", "network"),
            ("meu pc não abre o site", "network"), ("a impressora funcionou uma vez só", "printer"), ("a internet tá ok no celular", "network"),
            ("a internet voltou a funcionar, mas tá lenta", "network"), ("a internet voltou a funcionar só que a câmera não liga", "camera"),
            ("meu pendrive não carrega o site", ""), ("o jogo não abre as páginas", ""), ("o jogo não abre nenhum site", ""),
            // Ressalva na própria oração do marcador (última confirmação do @qa).
            ("a internet tá funcionando muito lenta", "network"), ("a internet tá funcionando devagar", "network"), ("a impressora tá funcionando mal", "printer"),
            ("o wifi tá funcionando pela metade", "network"), ("a internet só tá funcionando no celular", "network"), ("a internet tá funcionando às vezes", "network"),
            ("o wifi tá funcionando com falha", "network"), ("a internet tá funcionando, mas devagar", "network"), ("o wifi funciona agora mas cai toda hora", "network"),
            ("a internet tá funcionando lento", "network"), ("o fone tá funcionando de novo", ""), ("o som voltou a sumir", "audio"),
            ("o wifi funcionou ontem à noite", "network"), ("a internet tá funcionando apenas no celular", "network"), ("o wifi tá funcionando somente no quarto", "network"),
            ("o wifi tá funcionando meio estranho", "network"),
        })
        {
            var got = string.Join(",", SupportIntake.IncidentTopics(text).Order());
            assert(got == expected, $"Triagem (revisão final do @qa): {text} -> {(expected.Length == 0 ? "nenhum" : expected)}");
        }
        assert(string.Join(",", SupportIntake.ConversationTopics(new[] { "ainda tá lento, mas a internet voltou a funcionar", "a impressora não imprime" }).Order()) == "printer,system",
            "Herança: componente resolvido não impede a continuação genérica de somar o tópico anterior");
        assert(string.Join(",", SupportIntake.ConversationTopics(new[] { "a internet voltou a cair", "os sites não abrem" })) == "network",
            "Herança (revisão final do @qa): os sites não abrem >> a internet voltou a cair -> network");
        assert(SupportIntake.InitialTopic("ainda tá lento, mas resolveu a internet") == "system" && SupportIntake.InitialTopic("a internet voltou mas tá muito lenta") == null,
            "Triagem: componente resolvido não segura a observação do sistema; componente ainda com reclamação, sim");
    }

    // Terceira revisão do @qa: "e/nem/mas" e advérbio depois da falha, o alvo como objeto e a herança com "resolveu".
    private static void ThirdReview(Action<bool, string> assert)
    {
        foreach (var text in new[]
        {
            "o gmail não abre e não baixa o anexo",
            "o gmail não abre nem baixa o anexo",
            "o site não abre nem mostra o boleto",
            "o site não carrega e não mostra a câmera",
            "o youtube não carrega e não toca o som",
            "o google meet não abre e não acha o microfone",
            "o site do banco não abre nem imprime o extrato",
            "o gmail não abre nem o anexo",
            "o site não abre nem a câmera",
            "o youtube não carrega nem o som",
            "o site não abre agora o boleto",
            "o gmail não abre hoje o anexo",
            "o site não abre ainda o boleto",
            "o site não abre também o boleto",
            "o site não carrega nunca a câmera",
            "o site não abre, a câmera que não liga",
            "o site não abre quando ligo a câmera",
            "o youtube não carrega quando conecto o fone",
            "o site não abre porque a webcam está bloqueada",
            "o site não abre depois que liguei o microfone",
            "o site não abre no chrome, só no edge",
            "não abre o gmail no celular",
            "não carrega o youtube na smart tv",
            "não abre o site da câmera",
            "não abre o google fotos do celular",
            "não carrega o site do microfone",
            "não abre o gmail, mas abre o anexo",
            "o gmail não abre hoje o anexo",
            "o site não abre agora o boleto",
            "o site não abre no navegador",
            "o site não abre quando ligo a VPN",
            "o site do banco não abre no celular",
            "não abre as páginas do curso",
            "não carrega nenhuma página do livro",
            "não consigo abrir o gmail no celular",
        })
            assert(!SupportIntake.IncidentTopics(text).Contains("network"), "Triagem (3ª revisão do @qa): oração sem sujeito próprio ou objeto depois da falha não é rede: " + text);
        foreach (var text in new[]
        {
            "o site não abre e o anexo também não",
            "o gmail não abre e o anexo não baixa",
            "o site não abre mas o whatsapp funciona",
            "o google não abre quando estou no wifi do trabalho",
            "o site não abre e a câmera também não liga",
            "os sites não carregam e o som do pc sumiu",
            "o youtube não abre desde ontem",
            "o site não abre faz dois dias",
            "o site não abre há uma semana",
            "o site não carrega nada no notebook",
            "não abre o youtube e a câmera também não",
            "o google não abre nem o youtube",
            "o site não abre pra mim",
            "o site do jogo não abre",
            "o site não abre, nem o google",
            "o site não abre hoje",
            "o site não abre, o google também não",
        })
            assert(SupportIntake.IncidentTopics(text).Contains("network"), "Triagem (3ª revisão do @qa): rede com oração de sujeito próprio, tempo ou palavra vazia: " + text);
        foreach (var (conversation, expected) in new (string, string)[]
        {
            ("os sites não abrem >> ainda tá lento a câmera", "camera"),
            ("os sites não abrem >> continua travando o fone", "audio"),
            ("os sites não abrem >> ainda tá demorando, mas a impressora voltou", "printer"),
            ("os sites não abrem >> a impressora está ok >> continua lento", "printer,system"),
            ("os sites não abrem >> continua lento >> continua lento >> ainda demora", "network,system"),
            ("continua lento", "system"),
            ("oi >> continua lento", "system"),
            ("a câmera não funciona >> os sites não abrem >> continua lento", "network,system"),
            ("os sites não abrem >> de novo tá lento", "network,system"),
            ("os sites não abrem >> segue travando", "network,system"),
            ("os sites não abrem >> continua lento no navegador", "network,system"),
            ("os sites não abrem >> continua lento o site", "network,system"),
            ("os sites não abrem >> o gmail não abre o anexo >> continua lento", "network,system"),
            ("o pc está sem espaço >> ainda tá lento", "system"),
            ("os sites não abrem >> ainda tá lento, mas resolveu a internet", "system"),
            ("os sites não abrem >> outra coisa: ainda tá lento", "system"),
            ("os sites não abrem >> a internet voltou, mas ainda tá lento", "network"),
            ("os sites não abrem >> ainda tá lento, mas a internet voltou a funcionar", "system"),
            ("os sites não abrem >> resolveu, mas ainda tá lento", "system"),
            ("os sites não abrem >> a internet não voltou e continua lento", "network"),
        })
        {
            var got = string.Join(",", SupportIntake.ConversationTopics(conversation.Split(" >> ").Reverse()).Order());
            assert(got == expected, $"Herança (3ª revisão do @qa): {conversation} -> {expected}");
        }
        var mixed = SupportIntake.IncidentTopics("a internet voltou a funcionar e a câmera trava");
        assert(mixed.Contains("camera") && !mixed.Contains("network"), "Herança: só a oração que diz ter voltado tira o componente");
        assert(!SupportIntake.OnlyGenericSymptom("ainda tá tudo lento e os sites não abrem") && !SupportIntake.OnlyGenericSymptom("ainda não abre")
            && SupportIntake.OnlyGenericSymptom("ainda tá tudo lento"), "Herança: continuação genérica exige sintoma do sistema e nenhum sintoma do navegador");
        assert(SupportIntake.SystemFocus("meu pc ta sem espaco") == "space" && SupportIntake.SystemFocus("o pc tá lento e sem espaço") == "space"
            && SupportIntake.SystemFocus("o pc tá lento") == "slowness" && SupportIntake.SystemFocus("o papel travou") == null && SupportIntake.SystemFocus("os sites não abrem") == null,
            "Triagem: assunto do sistema para ordenar a recomendação do monitor");
    }

    // Segunda revisão do @qa: 42 frases em que "não abre/não carrega" tem outro objeto ou falta reconhecer a rede,
    // e 18 conversas em que a mensagem nova muda de assunto ou continua o anterior.
    private static void SecondReview(Action<bool, string> assert)
    {
        foreach (var text in new[]
        {
            "o site da prefeitura não abre o boleto em PDF",
            "a página do banco não carrega no app",
            "o navegador não abre o arquivo",
            "não carrega o vídeo do youtube offline",
            "o gmail não abre o anexo",
            "o gmail não carrega a foto que me mandaram",
            "o youtube não carrega o vídeo que eu baixei",
            "o google não abre a foto do celular",
            "o google drive não abre a foto",
            "o site não abre a nota fiscal",
            "o site não carrega o boleto",
            "o site não abre o comprovante",
            "o site do banco não abre no celular",
            "o site não abre a câmera para a videochamada",
            "o site não carrega o som do vídeo",
            "o youtube não carrega o som",
            "o chrome não carrega a extensão",
            "o navegador não carrega o certificado digital",
            "o navegador não carrega o token do banco",
            "o site do sistema da empresa não abre o módulo de vendas",
            "o site não abre o leitor de código de barras",
            "o site da Caixa não abre o FGTS",
            "o site do gov não carrega a CNH digital",
            "o gmail não abre no Outlook",
            "o google não abre a planilha",
            "o youtube não abre o arquivo de vídeo",
            "o site não abre o zip",
            "o navegador não abre o mp4",
            "o google não carrega o calendário no celular",
            "os sites não abrem a webcam",
            "o google meet não abre o microfone",
            "o google não abre a tela de login do Windows",
            "o site não abre o vídeo da câmera de segurança",
            "o site não carrega o mapa do GPS",
            "nenhum site abre o boleto",
            "o chrome não carrega o leitor de pdf",
            "o chrome não carrega o leitor de documentos",
            "o site da escola não abre a atividade no tablet",
            "o youtube não abre no smart tv",
            "o site não abre a foto do scanner",
            "o site não carrega a imagem da impressora",
            "o site não carregou a fonte do texto",
        })
            assert(!SupportIntake.IncidentTopics(text).Contains("network"), "Triagem (2ª revisão do @qa): falha com outro objeto não é rede: " + text);
        foreach (var text in new[] { "nenhum site abre, nem o google", "não abre site nenhum", "os sites não abrem desde que mudei o DNS", "o google não carrega e o whatsapp web também não" })
            assert(SupportIntake.IncidentTopics(text).Contains("network"), "Triagem (2ª revisão do @qa): sintoma do navegador é rede: " + text);
        foreach (var (conversation, expected) in new (string, string)[]
        {
            ("os sites não abrem >> agora a impressora está demorando", "printer"),
            ("os sites não abrem >> agora o mouse está travando", "system"),
            ("os sites não abrem >> o word está lento", "system"),
            ("os sites não abrem >> obrigado, resolveu. agora o pc está lento", "system"),
            ("os sites não abrem >> resolveu. outra coisa: tá tudo travando", "system"),
            ("os sites não abrem >> continua lento", "network,system"),
            ("os sites não abrem >> o pc ta lento", "system"),
            ("a internet caiu >> o computador está lento", "system"),
            ("a internet caiu >> tá travando", "system"),
            ("o wifi não conecta >> agora o excel está travando", "system"),
            ("o wifi não conecta >> ainda tá demorando", "network,system"),
            ("a impressora não imprime >> continua lento", "printer,system"),
            ("a impressora não imprime >> os sites não abrem >> ainda demora", "network,system"),
            ("o site da prefeitura não abre o boleto em PDF >> continua lento", "system"),
            ("a câmera não funciona >> tá lento", "system"),
            ("os sites não abrem >> a câmera não funciona >> continua lento", "camera,system"),
            ("os sites não abrem >> beleza >> continua lento", "network,system"),
            ("os sites não abrem >> obrigado >> tá tudo travando", "system"),
            ("os sites não abrem >> obrigado, mas ainda tá lento", "system"),
            ("os sites não abrem >> o computador continua lento", "system"),
        })
        {
            var got = string.Join(",", SupportIntake.ConversationTopics(conversation.Split(" >> ").Reverse()).Order());
            assert(got == expected, $"Herança (2ª revisão do @qa): {conversation} -> {expected}");
        }
        // Palavras vazias, pontuação, fim e nova oração depois da falha mantêm a rede; um objeto não.
        foreach (var (text, network) in new[]
        {
            ("o site não abre de jeito nenhum", true), ("os sites não carregam direito", true), ("o google não abre, nem o youtube", true),
            ("o site não abre no celular", false), ("o chrome não carrega o leitor", false), ("nenhum site carrega a página", false),
            ("o pdf do site não abre", false), ("mandei o site. o programa não abre", false), ("o site não funciona no celular", false), ("site nenhum abre o boleto", false),
            ("as páginas não carregam as imagens", false), ("as páginas não carregam mais", true),
        })
            assert(SupportIntake.IncidentTopics(text).Contains("network") == network, $"Triagem: o que vem depois da falha decide a rede: {text} -> {(network ? "rede" : "sem rede")}");
    }

    // As 100 frases da revisão do @qa (#59/#63). Cada uma diz se deve liberar rede (N), sistema (S), as duas ou nenhuma;
    // os outros componentes ficam com a lista de componentes, que não mudou.
    private static void ProbePhrases(Action<bool, string> assert)
    {
        (string Text, string Expected)[] phrases =
        [
            // Falsos positivos de rede da primeira versão: nada aqui é rede, exceto o roteador.
            ("o site da impressora não abre", ""), ("não abre o programa", ""), ("o programa não abre", ""), ("página em branco no Word", ""),
            ("o Word abre página em branco e não carrega o documento", ""), ("o Word não responde e a página fica em branco", ""), ("a página do documento não carrega", ""),
            ("não consigo abrir o arquivo que baixei do site", ""), ("não abre o PDF que baixei do site da HP", ""), ("não consigo entrar na página de configurações do Windows", ""),
            ("a página de configurações não abre", ""), ("a página de login do Windows não aparece e não entra", ""), ("o aplicativo do banco não abre a página inicial", ""),
            ("não consigo acessar a página de impressão", ""), ("a impressora não imprime a página toda", ""), ("a impressora imprime página em branco e não carrega o papel", ""),
            ("o navegador não abre", ""), ("o navegador está travando", "S"), ("não carrega a página 2 do relatório no Excel", ""), ("nenhuma página do PDF abre", ""),
            ("o jogo não abre e a página de suporte diz erro", ""), ("baixei o driver no site da HP e não instala", ""), ("n entra no site", ""),
            ("o pendrive não entra e o site diz para formatar", ""), ("não acessa a página do roteador", ""), ("o site do roteador não abre", "N"),
            // Falsos positivos de sistema: o componente citado fica com o tópico dele.
            ("pc cheio de vírus", ""), ("meu pc está cheio de vírus", ""), ("meu pc está cheio de malware", ""), ("o pc tá cheio de lixo", "S"),
            ("o computador ta cheio de programa inútil", "S"), ("a impressora está demorando", ""), ("a impressora está travando o papel", ""), ("o site está demorando", "N"),
            ("a internet está lenta", "N"), ("a trava da câmera", ""), ("tela travada no login", "S"), ("meu fone de ouvido está lento para conectar", ""),
            ("o mouse está travando", "S"), ("não consigo salvar a senha do wifi", "N"), ("o espaço da tela ficou pequeno", ""), ("o teclado não tem espaço", ""),
            // Variações reais dos cenários 3, 5 e 7.
            ("Os sites que eu tava acessando nao tao abrindo mais", "N"), ("os sites nao abrem", "N"), ("os sites não tão abrindo", "N"), ("site nenhum abre", "N"),
            ("nenhum site carrega", "N"), ("não consigo entrar em nenhum site", "N"), ("o google não abre", "N"), ("o youtube não carrega", "N"),
            ("o chrome não abre os sites", "N"), ("o chrome não carrega nada", "N"), ("o navegador não carrega", "N"), ("não consigo navegar", "N"),
            ("não tô conseguindo acessar a internet", "N"), ("nada abre no navegador", "N"), ("as páginas não carregam", "N"), ("as paginas nao carregam", "N"),
            ("o site dá erro de DNS", "N"), ("aparece DNS_PROBE_FINISHED_NXDOMAIN", "N"), ("o edge não abre nenhuma página", "N"), ("fica carregando e não abre o site", "N"),
            ("os sites não abrem mais desde ontem", "N"), ("os sites demoram muito pra abrir", "N"),
            ("meu pc ta sem espaco", "S"), ("meu pc ta sem espaço", "S"), ("o pc ta sem memoria", "S"), ("o computador tá sem espaço no disco", "S"), ("disco cheio", "S"),
            ("o disco tá cheio", "S"), ("o C: tá cheio", "S"), ("o disco C: está quase cheio", "S"), ("acabou o espaço", "S"), ("o espaço acabou", "S"),
            ("espaço insuficiente", "S"), ("o windows diz que não há espaço suficiente", "S"), ("memória cheia", "S"),
            ("meu computador esta ficando lento e travado", "S"), ("o pc ta lento", "S"), ("o pc tá lerdo", "S"), ("o pc tá muito pesado", "S"),
            ("o computador demora pra ligar", "S"), ("tá tudo travando", "S"), ("o computador trava toda hora", "S"),
            // Continuações: as de lentidão trazem o sistema (e somam o tópico anterior no fluxo); as outras não trazem tópico.
            ("ainda tá demorando", "S"), ("continua lento", "S"), ("agora tá lento", "S"), ("tenta de novo, ainda não abre", ""), ("ainda não abre", ""),
            ("ainda não carrega", ""), ("não funcionou", ""),
            // Outros sentidos de "pesado" e "trava".
            ("o arquivo é muito pesado", ""), ("a trava do notebook quebrou", ""), ("tudo trava", "S"), ("o papel travou na impressora", ""), ("o papel travou", ""),
            ("não consigo salvar a senha", ""), ("o pc está lento e o som falha", "S"), ("o computador trava quando abro a câmera", "S"),
            // Exclusões e limites do sintoma do navegador.
            ("baixei do site e agora não abre", ""), ("as páginas ficam em branco e não carregam", ""), ("as páginas de configurações não abrem", ""),
            ("a página inicial não carrega", ""), ("o site da prefeitura pediu um cadastro enorme e depois o programa não abre", ""), ("não consigo navegar pelas pastas", ""),
        ];
        foreach (var (text, expected) in phrases)
        {
            var topics = SupportIntake.IncidentTopics(text);
            var got = (topics.Contains("network") ? "N" : "") + (topics.Contains("system") ? "S" : "");
            assert(got == expected, $"Triagem (frases do @qa): {text} -> {(expected.Length == 0 ? "nenhum" : expected)}");
        }
        // Espaço, memória e lentidão do computador abrem a observação do sistema; rede e componentes não.
        foreach (var text in new[] { "o C: tá cheio", "espaço insuficiente", "o windows diz que não há espaço suficiente", "o espaço acabou", "o pc ta sem memoria", "o pc tá lerdo", "o pc tá muito pesado", "o computador trava toda hora" })
            assert(SupportIntake.InitialTopic(text) == "system", "Triagem: observação do sistema para " + text);
        foreach (var text in new[] { "o google não abre", "aparece DNS_PROBE_FINISHED_NXDOMAIN", "a impressora está demorando", "a trava da câmera", "o site está demorando" })
            assert(SupportIntake.InitialTopic(text) != "system", "Triagem: sem observação do sistema para " + text);
        assert(SupportIntake.InitialTopic("o computador trava quando abro a câmera") == "camera" && SupportIntake.InitialTopic("o pc está lento e o som falha") == "audio",
            "Triagem: com o computador lento e um componente citado, a observação inicial é a do componente");
        // A continuação só com lentidão soma o tópico anterior; com componente ou sintoma do navegador, não.
        assert(SupportIntake.OnlyGenericSymptom("continua lento") && SupportIntake.OnlyGenericSymptom("ainda tá demorando") && !SupportIntake.OnlyGenericSymptom("a internet continua lenta")
            && !SupportIntake.OnlyGenericSymptom("os sites continuam demorando") && !SupportIntake.OnlyGenericSymptom("continue") && !SupportIntake.OnlyGenericSymptom("o disco continua cheio")
            && !SupportIntake.OnlyGenericSymptom("o pc continua lento e os sites não abrem"),
            "Triagem: só a continuação com sintoma genérico do sistema soma o tópico anterior");
    }

    private static async Task SpaceAndSlownessFlows(Store store, Account actor, Action<bool, string> assert)
    {
        var tools = new FlowTools((name, args) => name switch
        {
            "get_health_report" => WindowsTools.Ok(FinalAnswerChecks.DiskLowReport(withHistoricalCpu: false)),
            "diagnose_device" when args.GetProperty("topic").GetString() == "system" => WindowsTools.Ok("Memória livre: 20 GB de 31.9 GB\nDisco C:: 120.4 GB livres de 476.3 GB\nDisco T:: 0.1 GB livres de 2 GB"),
            _ => throw new Exception("Ferramenta inesperada no pedido de espaço: " + name)
        });
        var finish = Call("finish_support", new { message = "O volume T: está com pouco espaço livre. Revise o uso do disco; o Nexo não apaga arquivos." });
        var answer = await Run(store, actor, [User("Meu PC tá sem espaço")], tools, finish);
        assert(tools.Calls.SequenceEqual(["get_health_report", "diagnose_device"]) && answer.StartsWith("O volume T:", StringComparison.Ordinal),
            "Fluxo: falta de espaço consulta o relatório de saúde e o espaço por disco antes de o modelo agir");

        var history = tools.History;
        string Data(AgentMessage message) { using var parsed = JsonDocument.Parse(message.content); return parsed.RootElement.GetProperty("data").GetString() ?? ""; }
        assert(history.Count(m => m.role == "tool" && m.tool_name != "finish_support") == 2 && history.Any(m => m.role == "tool" && m.tool_name == "get_health_report" && Data(m).Contains("Pouco espaço no volume T")),
            "Fluxo: o modelo recebe o relatório de saúde com a ocorrência disk-low");

        var slow = new FlowTools((name, args) => name switch
        {
            "get_health_report" => WindowsTools.Ok("Nenhuma amostra de saúde foi coletada nas últimas 24 horas."),
            "diagnose_device" => WindowsTools.Ok("Memória livre: 5 GB de 31.9 GB"),
            _ => throw new Exception("Ferramenta inesperada no pedido de lentidão: " + name)
        });
        // O sintoma citado na resposta ("os sites não abrem") não é um componente que a resposta diz ter verificado.
        const string slowFinish = "Verifiquei a memória: há 5 GB livres de 31.9 GB, por isso o pc trava e até os sites não abrem direito.";
        var slowAnswer = await Run(store, actor, [User("o pc tá travando")], slow, Call("finish_support", new { message = slowFinish }));
        assert(slow.Calls.SequenceEqual(["get_health_report", "diagnose_device"]) && slowAnswer == slowFinish + "\n\n" + TurnFacts.Prefix + " nenhuma.",
            "Fluxo: travamento abre a mesma observação do sistema, e o sintoma citado na resposta não exige outra consulta");

        // Relatório de saúde não identifica alvo: sem a verificação do sistema, o acompanhamento de reconexão continua indisponível.
        var noTarget = new FlowTools((name, _) => name switch
        {
            "get_health_report" => WindowsTools.Ok("Nenhuma amostra de saúde foi coletada nas últimas 24 horas."),
            "diagnose_device" => throw new InvalidOperationException("O Windows não respondeu à consulta."),
            _ => throw new Exception("Acompanhamento sem alvo chegou ao executor: " + name)
        });
        var noTargetConversation = Conversation(actor, [User("o pc tá travando")]);
        var noTargetModel = new ModelSequence([Call("wait_for_reconnect", new { kind = "device", interface_index = 0, device_id = "USB\\X" }),
            Call("finish_support", new { message = "Não consegui consultar o sistema." })]);
        await Execute(store, actor, noTargetConversation, noTarget, noTargetModel);
        assert(noTarget.Calls.SequenceEqual(["get_health_report", "diagnose_device"]) && noTargetModel.Calls == 2,
            "Fluxo: só o relatório de saúde não libera o acompanhamento de reconexão");

        // Só o relatório de saúde consultado sustenta "consultei... o disco".
        var reportOnly = new FlowTools((name, _) => name == "get_health_report" ? WindowsTools.Ok(FinalAnswerChecks.DiskLowReport(withHistoricalCpu: false)) : throw new Exception("Ferramenta inesperada: " + name));
        const string reportFinish = "Consultei o relatório de saúde: o disco T: está com 7% livre. Revise o uso do disco; o Nexo não apaga arquivos.";
        var reportAnswer = await Run(store, actor, [User("Meu computador está estranho desde ontem")], reportOnly, Call("get_health_report", new { }), Call("finish_support", new { message = reportFinish }));
        assert(reportOnly.Calls.SequenceEqual(["get_health_report"]) && reportAnswer == reportFinish + "\n\n" + InvestigationEvidence.StorageGuidance + "\n\n" + TurnFacts.Prefix + " nenhuma.", "Fluxo: relatório de saúde conta como verificação do sistema na resposta do modelo (com pouco espaço atual, o Nexo acrescenta onde ver o uso do disco)");
    }

    private static string Network(bool dhcp = true) => WindowsTools.Ok(new { Adapters = new[] {
        new { Index = 13, Name = "Ethernet", Status = "Up", Dhcp = dhcp ? "Enabled" : "Disabled", Gateways = new[] { "192.168.1.1" }, Dns = new[] { "192.0.2.53" } } } });

    // Cenário 3: "os sites não abrem" libera set_dns até a confirmação do usuário (aqui, recusada).
    private static async Task NetworkRequestReachesApproval(Store store, Account actor, Action<bool, string> assert)
    {
        var tools = new FlowTools((name, _) => name switch
        {
            "get_network" => Network(),
            "set_dns" => JsonSerializer.Serialize(new { success = false, denied = true }),
            _ => throw new Exception("Ferramenta inesperada no cenário 3: " + name)
        });
        var answer = await Run(store, actor, [User("Os sites que eu tava acessando nao tao abrindo mais")], tools,
            Call("get_network", new { }), Call("set_dns", new { interface_index = 13, mode = "automatic" }));
        assert(tools.Calls.SequenceEqual(["get_network", "set_dns"]) && answer.Contains("Você recusou a alteração proposta"),
            "Cenário 3: com \"os sites não abrem\", set_dns passa pela trava de tópico e chega à confirmação do usuário");
    }

    // Cenário 3, segundo e terceiro turnos: "continue" e "denovo" herdam a rede da primeira mensagem da conversa.
    private static async Task ContinuationInheritsTopic(Store store, Account actor, Action<bool, string> assert)
    {
        foreach (var followUp in new[] { "continue", "denovo", "de novo", "tenta de novo", "ok", "sim" })
        {
            var tools = new FlowTools((name, _) => name switch
            {
                "get_network" => Network(),
                "set_dns" => JsonSerializer.Serialize(new { success = false, denied = true }),
                _ => throw new Exception("Ferramenta inesperada na continuação: " + name)
            });
            var answer = await Run(store, actor, [User("Os sites que eu tava acessando nao tao abrindo mais"), Assistant("A IA não conseguiu selecionar a próxima etapa."), User(followUp)], tools,
                Call("get_network", new { }), Call("set_dns", new { interface_index = 13, mode = "automatic" }));
            assert(tools.Calls.SequenceEqual(["get_network", "set_dns"]) && answer.Contains("Você recusou"), "Continuação herda a rede da mensagem anterior: " + followUp);
        }
        // "continua lento" numa conversa de rede soma o sistema à rede, mesmo encadeado em outra continuação genérica.
        foreach (var chain in new[] { new[] { "continua lento" }, new[] { "continua lento", "ainda tá demorando" } })
        {
            var tools = new FlowTools((name, _) => name switch
            {
                "get_health_report" => WindowsTools.Ok("Nenhuma amostra de saúde foi coletada nas últimas 24 horas."),
                "diagnose_device" => WindowsTools.Ok("Memória livre: 20 GB de 31.9 GB"),
                "get_network" => Network(),
                "set_dns" => JsonSerializer.Serialize(new { success = false, denied = true }),
                _ => throw new Exception("Ferramenta inesperada na continuação genérica: " + name)
            });
            Entry[] entries = [User("Os sites que eu tava acessando nao tao abrindo mais"), .. chain.SelectMany(text => new[] { Assistant("Verifiquei a rede."), User(text) })];
            var answer = await Run(store, actor, entries, tools, Call("get_network", new { }), Call("set_dns", new { interface_index = 13, mode = "automatic" }));
            assert(tools.Calls.SequenceEqual(["get_health_report", "diagnose_device", "get_network", "set_dns"]) && answer.Contains("Você recusou"),
                "Continuação genérica de lentidão soma o sistema à rede herdada: " + string.Join(" / ", chain));
        }
        // A soma para na mensagem com componente: depois da câmera, "continua lento" não traz de volta a rede de antes.
        var camera = new FlowTools((name, _) => name switch
        {
            "get_health_report" => WindowsTools.Ok("Nenhuma amostra de saúde foi coletada nas últimas 24 horas."),
            "diagnose_device" => WindowsTools.Ok("Memória livre: 20 GB de 31.9 GB"),
            "get_network" => Network(),
            _ => throw new Exception("Alteração de rede fora do incidente chegou ao executor: " + name)
        });
        var cameraAnswer = await Run(store, actor, [User("Os sites que eu tava acessando nao tao abrindo mais"), Assistant("Verifiquei a rede."), User("agora minha câmera não funciona"),
            Assistant("Verifiquei a câmera."), User("continua lento")], camera,
            Call("get_network", new { }), Call("set_dns", new { interface_index = 13, mode = "automatic" }), Call("set_dns", new { interface_index = 13, mode = "automatic" }));
        assert(camera.Calls.SequenceEqual(["get_health_report", "diagnose_device", "get_network"]) && cameraAnswer.Contains("porque o seu pedido não cita a rede ou a internet"),
            "Continuação genérica soma só até a mensagem anterior com componente");

        // Sem mensagem anterior com tópico, a continuação não libera nada.
        var locked = new FlowTools((name, _) => name == "get_network" ? Network() : throw new Exception("Continuação sem tópico chegou ao executor: " + name));
        var lockedAnswer = await Run(store, actor, [User("oi"), Assistant("Olá! Como posso ajudar?"), User("continue")], locked,
            Call("get_network", new { }), Call("set_dns", new { interface_index = 13, mode = "automatic" }), Call("set_dns", new { interface_index = 13, mode = "automatic" }));
        assert(locked.Calls.SequenceEqual(["get_network"]) && lockedAnswer.Contains("porque o seu pedido não cita a rede ou a internet"),
            "Continuação sem tópico anterior continua barrada pela trava de tópico");
    }

    // A trava de tópico barra uma vez e avisa o modelo; a repetição encerra o turno, sem as 10 tentativas do ensaio.
    private static async Task TopicLockDoesNotLoop(Store store, Account actor, Action<bool, string> assert)
    {
        var tools = new FlowTools((name, _) => name == "get_network" ? Network() : throw new Exception("Alteração barrada chegou ao executor: " + name));
        var dns = Call("set_dns", new { interface_index = 13, mode = "manual", servers = new[] { "8.8.8.8" } });
        var model = new ModelSequence([Call("get_network", new { }), .. Enumerable.Repeat(dns, 10)]);
        var conversation = Conversation(actor, [User("Meu computador está estranho desde ontem")]);
        var answer = await Execute(store, actor, conversation, tools, model);
        using var blocked = JsonDocument.Parse(conversation.AgentHistory.First(m => m.role == "tool" && m.tool_name == "set_dns").content);
        var lockMessage = blocked.RootElement.GetProperty("error").GetString() ?? "";
        assert(tools.Calls.SequenceEqual(["get_network"]) && model.Calls == 3, "Trava de tópico: a segunda chamada igual encerra o turno, sem chegar ao executor");
        assert(lockMessage.Contains("Não repita esta chamada") && lockMessage.Contains("não cita a rede ou a internet, então") && lockMessage.Contains("finish_support"),
            "Trava de tópico: o modelo recebe o motivo e a instrução de não repetir");
        assert(answer.Contains("O Nexo não executou a etapa “Configurando os servidores DNS” porque o seu pedido não cita a rede ou a internet")
            && answer.Split("O Nexo não executou").Length == 2 && !answer.Contains("Me diga qual aplicativo") && !answer.Contains("Se o problema continuar"),
            "Trava de tópico: a resposta final explica a trava uma vez e não acrescenta outro pedido");

        var variants = new FlowTools((name, _) => name == "get_network" ? Network() : throw new Exception("Alteração barrada chegou ao executor: " + name));
        var variantModel = new ModelSequence([Call("get_network", new { }), Call("set_dns", new { interface_index = 13, mode = "automatic" }),
            Call("set_ipv4", new { interface_index = 13, mode = "dhcp", address = "", prefix = 0, gateway = "" }), Call("flush_dns", new { })]);
        await Execute(store, actor, Conversation(actor, [User("Meu computador está estranho desde ontem")]), variants, variantModel);
        assert(variants.Calls.SequenceEqual(["get_network"]) && variantModel.Calls == 3, "Trava de tópico: outra alteração do mesmo componente também encerra o turno");

        var audio = new FlowTools((name, _) => name == "diagnose_device"
            ? WindowsTools.Ok(new { count = 2, devices = new[] { new Device("Alto-falantes", "HDAUDIO\\SPK1", 0, "OK"), new Device("Headset USB", "USB\\HEADSET1", 0, "OK") } })
            : throw new Exception("Alteração ambígua chegou ao executor: " + name));
        var restart = Call("restart_device", new { device_id = "HDAUDIO\\SPK1" });
        var audioModel = new ModelSequence([restart, restart, restart, restart]);
        var audioAnswer = await Execute(store, actor, Conversation(actor, [User("Estou sem som em todos os aplicativos.")]), audio, audioModel);
        assert(audio.Calls.SequenceEqual(["diagnose_device"]) && audioModel.Calls == 2 && audioAnswer.Contains("alvo não foi identificado sem ambiguidade"),
            "Trava de alvo ambíguo: a mesma chamada repetida encerra o turno");
    }

    private static Entry User(string text) => new() { Role = "user", Text = text };
    private static Entry Assistant(string text) => new() { Role = "assistant", Text = text };
    private static Conversation Conversation(Account actor, Entry[] entries) => new() { UserId = actor.Id, Entries = [.. entries] };
    private static string Call(string name, object args) => JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } } });

    private static async Task<string> Run(Store store, Account actor, Entry[] entries, FlowTools tools, params string[] responses)
    {
        var model = new ModelSequence(responses);
        var conversation = Conversation(actor, entries);
        var answer = await Execute(store, actor, conversation, tools, model);
        if (model.Calls != responses.Length) throw new Exception($"FAIL: o roteiro do modelo não foi consumido ({model.Calls} de {responses.Length}) em: {entries[^1].Text}");
        return answer;
    }

    private static async Task<string> Execute(Store store, Account actor, Conversation conversation, FlowTools tools, ModelSequence model)
    {
        using (model)
        using (var http = new HttpClient(model, disposeHandler: false))
        {
            var answer = await new AgentService(store, tools, http).Run(actor, conversation, new Progress<string>(), (_, _) => { }, CancellationToken.None);
            tools.History = conversation.AgentHistory;
            return answer;
        }
    }

    private sealed class FlowTools(Func<string, JsonElement, string> execute) : IAgentTools
    {
        internal List<string> Calls { get; } = [];
        internal List<AgentMessage> History { get; set; } = [];
        public Task<string> Execute(string name, JsonElement args, CancellationToken token)
        {
            Calls.Add(name);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(execute(name, args));
        }
    }

    private sealed class ModelSequence(string[] responses) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Calls >= responses.Length) throw new Exception("Modelo recebeu mais chamadas que o roteiro.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[Calls++], Encoding.UTF8, "application/json") });
        }
    }
}
