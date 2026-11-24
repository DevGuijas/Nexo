using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexo.Support;

public static class AgentProtocol
{
    public static bool ClaimsExecution(string message) => Regex.IsMatch(Normalize(message), @"(?<!nao )\b(verifiquei|consultei|detectei|corrigi|configurei|ativei|desativei|reiniciei|instalei|atualizei|limpei|ajustei)\b");
    public static bool UserConfirmedFunction(string message)
    {
        var text = Normalize(message);
        return !text.Contains('?') && Regex.IsMatch(text, @"\b(funcionou|resolveu|deu certo|voltou a funcionar|esta funcionando)\b")
            && !Regex.IsMatch(text, @"\b(nao|nem|nunca)\b.{0,30}\b(funcionou|resolveu|deu certo|voltou a funcionar|esta funcionando)\b|\b(em parte|parcialmente|as vezes)\b");
    }
    public static bool RequestsReadOnly(string prompt) => Regex.IsMatch(Normalize(prompt),
        @"\b(apenas|somente|so)\s+(consulte|consultar|verifique|verificar|diagnostique|diagnosticar|leitura)\b|\bsem\s+(alterar|mudar|corrigir|alteracoes|mudancas)\b|\bnao\s+(mude|altere|corrija|faca\s+(alteracoes|mudancas))\b");

    // Some local models emit the control function as plain name + JSON rather than
    // Ollama tool_calls. Accept only the two non-mutating controller functions.
    public static JsonElement? ParseControl(string content)
    {
        var match = Regex.Match(content.Trim(), @"\A(finish_support|ask_user)\s*(\{[\s\S]*\})\z");
        if (!match.Success) return null;
        try
        {
            using var args = JsonDocument.Parse(match.Groups[2].Value);
            if (args.RootElement.ValueKind != JsonValueKind.Object) return null;
            return JsonSerializer.SerializeToElement(new[] { new { function = new { name = match.Groups[1].Value, arguments = args.RootElement.Clone() } } });
        }
        catch (JsonException) { return null; }
    }

    internal const string PendingActionReason = "Há uma ação pendente anunciada. Chame a ferramenta correspondente agora; uma promessa não encerra o turno. Se está bloqueado, explique a limitação sem prometer executar.";
    private static readonly Regex PendingAction = new(@"\b(vou|vamos|farei|iremos)\s+(agora\s+)?(fazer|executar|tentar|acessar|investigar|(?:test|verific|consult|list|limp|ativ|configur|habilit|reinici|instal)\w{1,8}(?:-(?:lo|la|los|las))?)\b");

    // O trecho que a regra da ação anunciada recusou, já normalizado (#88). Ele vai só na instrução ao modelo, nunca na
    // auditoria: na validação no PC, o qwen3:8b repetiu "Vamos verificar…" seis vezes sem saber qual parte era recusada.
    public static string? PendingActionPhrase(string message) => PendingAction.Match(Normalize(message)) is { Success: true } match ? match.Value : null;

    // Na validação da 76d9028 (10/11), "Vamos verificar se há algo que possa ser removido ou se há uma forma de liberar mais
    // espaço" foi recusado 13 vezes em 3 turnos: o motivo mandava chamar a ferramenta, e não existe ferramenta que procure ou
    // remova arquivos. Quando a mensagem recusada fala de liberar espaço, limpar o disco ou remover arquivos, a instrução diz
    // isso e manda orientar o caminho do Windows.
    internal const string NoCleanupTool = "O Nexo não tem ferramenta para procurar, remover ou apagar arquivos nem para liberar espaço: não anuncie nem ofereça essa verificação. Diga o espaço livre medido e oriente o usuário a abrir Configurações > Sistema > Armazenamento no Windows para ver o que ocupa espaço.";
    // Liberar espaço; ou remover, apagar ou limpar numa mensagem que fala de disco, espaço, arquivos ou pastas ("vou limpar o
    // cache DNS" fica com a instrução geral, porque flush_dns existe).
    private static bool Cleanup(string text) => Regex.IsMatch(text, @"\b(?:liber\w*|abr\w*|desocup\w*) (?:\w+ ){0,2}espaco\b")
        || Regex.IsMatch(text, @"\b(?:remov|apag|exclu|delet|limp)\w*") && Regex.IsMatch(text, @"\b(?:espaco|disco|discos|arquivos?|pastas?|armazenamento)\b") && !Regex.IsMatch(text, @"\bcache\b");

    // Remoção de apps (#127). Na validação no PC da 63f01f0 (23/11/2026), "remove os apps inúteis do windows pra mim" teve
    // 4 recusas pelo motivo genérico ("Use a ferramenta para a próxima consulta…") da oferta "Se você quiser remover
    // aplicativos específicos, posso ajudar com isso… me diga quais aplicativos você deseja remover", e o turno terminou em
    // "A IA repetiu a mesma resposta recusada", sem indicar o caminho. A remoção de apps existe só no menu Ferramentas: o
    // modelo não a oferece nem a executa. É recusada a frase em 1ª pessoa que oferece remover, desinstalar ou excluir apps ou
    // programas, ou que pede ao usuário a lista do que remover. Passam a negação ("não posso remover apps pelo chat"), a frase
    // que leva ao cartão de Ferramentas e a explicação de como o usuário faz ("posso explicar como desinstalar um programa").
    // Na repetição no PC da fcca5cd (23/11, #127), o modelo copiou o verbo da instrução para o usuário ("indique **Ferramentas >
    // Apps pré-instalados** e ajustes do Windows"), com o negrito cortando o nome do cartão. Na repetição da e7c8b32, o motivo
    // mandava dizer o caminho "com estas palavras": em "meu pc ta lento, remove os apps inúteis", o modelo obedeceu e a resposta
    // ficou só com o caminho, sem nada da lentidão consultada. O motivo não traz mais o caminho: pede para tirar a oferta e
    // manter o resto, e o próprio Nexo acrescenta o caminho (AppRemovalLine) à resposta aceita.
    internal const string AppRemovalReason = "A resposta oferece remover ou desinstalar apps ou programas, ou pergunta quais remover, e o Nexo não faz isso pelo chat. Tire essa oferta e essa pergunta e mantenha o resto da resposta: o que as consultas mostraram e a orientação sobre o problema do usuário. Não escreva onde remover os apps; o Nexo acrescenta essa orientação à resposta.";
    private const string AppRemovalCard = "“Apps pré-instalados e ajustes do Windows”, no menu Ferramentas";
    // Texto do Nexo, sem o modelo, para o pedido só de remover apps e para o fim do turno sem achados. Na repetição da e7c8b32,
    // "ok, e o que eu removo?" recebeu do modelo a cópia do texto anterior, que não respondia: o texto diz o que a verificação
    // mostra (a lista só com o que existe no PC, os itens com Default já marcados, os opcionais desmarcados, alguns com aviso).
    internal const string AppRemovalGuidance = "O Nexo não remove apps pelo chat. Para os apps que vêm com o Windows, use o cartão " + AppRemovalCard
        + ", e clique em “Verificar este PC”: a lista mostra só o que existe neste PC, com os itens sugeridos já marcados e os opcionais desmarcados, alguns com um aviso do que deixa de funcionar. Você escolhe os itens, confirma antes de qualquer mudança e pode desfazer depois. Outros programas são desinstalados em Configurações > Aplicativos do Windows.";
    // Linha curta que o Nexo acrescenta à resposta aceita de um pedido misto ou de um turno em que a oferta foi recusada, e ao fim
    // do turno com achados: o resultado das consultas continua sendo o principal da resposta.
    internal const string AppRemovalLine = "O Nexo não remove apps pelo chat: para os apps que vêm com o Windows, use o cartão " + AppRemovalCard
        + ", onde “Verificar este PC” mostra o que existe neste PC e nada muda antes de você confirmar.";
    // Fim do turno pelas recusas num seguimento sobre o que remover ("ok, e o que eu removo?"), numa conversa em que o Nexo já
    // indicou o cartão (repetição no PC da a6fcbea): uma explicação curta e diferente do texto pronto, sem inventar itens. A lista
    // só tem o que existe no PC (RenderDebloatList), marca os itens com Default disponíveis e mostra o detalhe (aviso) abaixo de
    // alguns; nada muda antes da confirmação do Aplicar.
    internal const string AppRemovalFollowUp = "Isso depende do que existe neste PC, e a escolha é sua. No cartão " + AppRemovalCard
        + ", “Verificar este PC” lista só o que existe aqui: os itens sugeridos já vêm marcados, os outros vêm desmarcados e alguns trazem um aviso abaixo do nome. Se ficar em dúvida sobre um item, deixe-o desmarcado; nada muda antes de você confirmar.";

    // Resposta que repete a anterior (#127, repetição no PC da a6fcbea): depois da resposta pronta sobre o cartão, "ok, e o que eu
    // removo?" recebeu do modelo o mesmo texto, palavra por palavra, apesar do prompt. Vale para qualquer conversa: a resposta do
    // modelo que cobre quase toda a última resposta do assistente (80% dos trechos de 4 palavras dela) não responde à mensagem nova.
    // Só contam respostas longas (60 caracteres e 10 palavras, depois de normalizar): "Certo.", "Tudo bem, até mais!" e
    // confirmações curtas se repetem por natureza. O relatório determinístico do Nexo não passa por esta trava (não é resposta do
    // modelo); se o modelo o copiar inteiro, a cópia também não responde à mensagem nova. O pedido explícito de repetir passa
    // ("repete", "repetir", "repita"). A conta vale nos dois sentidos (@qa): a maior parte (60%) da resposta nova também precisa vir
    // da anterior, para que uma resposta longa com conteúdo novo, que só reaproveita uma resposta anterior curta (o estado medido,
    // uma frase de teste), não seja recusada como cópia.
    internal const string RepeatedAnswerReason = "A resposta repete a sua resposta anterior nesta conversa e não responde à nova mensagem do usuário. Responda à pergunta nova com as suas palavras, sem copiar a resposta anterior; se ela já respondia, diga isso em uma frase e acrescente só o que for novo para esta pergunta.";
    private static string[] Words(string text) => Regex.Replace(Normalize(text), @"[^\p{L}\p{N}]+", " ").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    private static HashSet<string> Shingles(string[] words) => words.Length < 4 ? [string.Join(' ', words)]
        : [.. Enumerable.Range(0, words.Length - 3).Select(i => string.Join(' ', words, i, 4))];
    public static bool RepeatsPriorAnswer(string message, string? prior, string userRequest)
    {
        if (string.IsNullOrWhiteSpace(prior) || Regex.IsMatch(Normalize(userRequest), @"\b(?:repet|repit)\w*")) return false;
        var previous = Words(TurnFacts.Body(prior));
        var answer = Words(message);
        if (previous.Length < 10 || answer.Length < 10 || string.Join(' ', previous).Length < 60 || string.Join(' ', answer).Length < 60) return false;
        var covered = Shingles(previous);
        var written = Shingles(answer);
        return covered.Count(written.Contains) >= 0.8 * covered.Count && written.Count(covered.Contains) >= 0.6 * written.Count;
    }
    // Frases que só repetem o caminho do cartão (o modelo copiou o texto do Nexo): saem antes de medir se a resposta tem conteúdo.
    private static readonly string[] NexoRemovalSentences = [.. Regex.Split(AppRemovalGuidance + " " + AppRemovalLine, @"(?<=[.!?:])\s+")
        .Select(sentence => Regex.Replace(Normalize(sentence), @"\W+", " ").Trim()).Where(sentence => sentence.Length > 0)];
    public static string WithoutAppRemovalPath(string text) => string.Join(" ", Regex.Split(text.Trim(), @"(?<=[.!?])\s+|\n+").Where(sentence =>
    {
        var normalized = Normalize(sentence);
        var plain = Regex.Replace(normalized, @"\W+", " ").Trim();
        return plain.Length > 0 && !RemovalPath.IsMatch(normalized) && !(plain.Length >= 12 && NexoRemovalSentences.Any(nexo => nexo.Contains(plain, StringComparison.Ordinal)))
            && !Regex.IsMatch(normalized, @"\bnao remove\w*\b[^.]*\bpelo chat\b")
            // @qa: Configurações > Aplicativos só é cópia quando a frase fala de desinstalar ou remover; "abra Configurações >
            // Aplicativos > Inicialização e desative…" é orientação real de lentidão e fica.
            && !(Regex.IsMatch(normalized, @"\bconfigurac\w*\W+aplicativos\b") && Regex.IsMatch(normalized, @"\b(?:desinstal|remov)\w*"));
    }));
    // A resposta já leva ao cartão: o nome "Apps pré-instalados e ajustes" (com ou sem Ferramentas antes) basta.
    public static bool CitesAppRemovalPath(string normalized) => RemovalPath.IsMatch(normalized);
    private const string AppObject = @"(?:apps?|aplicativos?|programas?|softwares?|bloatwares?)";
    // Entre o verbo e o objeto cabem até 3 palavras, sem um alvo que não é app ("apagar o arquivo do programa", "limpar o cache
    // do aplicativo", "remover o vírus do programa"). Particípio não conta ("verificar se o app foi removido").
    private const string NotAppTarget = @"(?!(?:arquivos?|pastas?|dados|cache|historico|fila|atalhos?|icones?|virus|malware|notificac\w*|janelas?|abas?)\b)";
    // O verbo no passado não é oferta (@qa): "me diga qual programa você desinstalou", "desde que você removeu o app" são perguntas
    // de diagnóstico de quem desinstalou algo e viu outro problema aparecer.
    private const string PastForm = @"(?!(?:ei|uei|ou|eu|iu|i|eram|aram|iram)\b)";
    private const string RemovalVerb = @"(?:remov|desinstal|exclu|apag|delet|elimin)" + PastForm + @"(?!\w*(?:id[oa]s?|ad[oa]s?)\b)\w*";
    private static readonly Regex AppRemoval = new(@"\b" + RemovalVerb + @"(?:-(?:lo|la|los|las))?\s+(?:" + NotAppTarget + @"[\w-]+\s+){0,3}?" + AppObject + @"\b"
        + @"|\b" + AppObject + @"\s+(?:[\w-]+\s+){0,3}?" + RemovalVerb
        + @"|\blimp\w*\s+(?:(?:os|as|esses|estes|seus|alguns|todos os)\s+)?" + AppObject + @"\b|\bdebloat\w*"
        // Desinstalar um app pelo nome ("vou desinstalar o Clipchamp"); driver, dispositivo e atualização ficam de fora.
        + @"|\bdesinstal" + PastForm + @"(?!\w*(?:id[oa]s?|ad[oa]s?)\b)\w*\b(?:-(?:lo|la|los|las))?(?!\s+(?:\w+\s+){0,4}?(?:drivers?|dispositivos?|atualizac\w*|updates?)\b)"
        // Procurar os apps que o usuário não usa é o começo da mesma remoção (PC 3a88880: "Se quiser, posso ajudar a encontrar
        // outros apps que talvez você também não use"). Procurar os apps que mais consomem memória ou CPU não conta.
        + @"|\b(?:encontr|identific|ach|localiz|list|descobr|levant|mape)\w*\s+(?:[\w-]+\s+){0,3}?" + AppObject
        + @"\b[^.;!?\n]*?\b(?:nao (?:\w+ )?(?:usa|use|usam|usem|utiliza|utilize|utilizam|precisa|precise)\b|inute\w*|inutil|desnecessari\w*|sem uso|pouco usad\w*|esquecid\w*)");
    private static readonly Regex RemovalOffer = new(@"\b(?:posso|podemos|poderia|poderiamos|consigo|vou|vamos|irei|quer que eu|deseja que eu|gostaria que eu|prefere que eu|eu (?:te |lhe )?ajudo"
        + @"|me (?:diga|informe|fale|avise|mande|liste|passe)(?: (?:quais|qual|os|o nome|os nomes))?)\b");
    private static readonly Regex RemovalPath = new(@"\bferramentas\b[^.;!?]*\bpre-?\s?instalad|\bpre-?\s?instalad\w* e ajustes\b");
    private static readonly Regex RemovalExplanation = new(@"\b(?:explic|orient|ensin)\w*|\b(?:mostrar|dizer|indicar)(?: a voce| para voce)? (?:como|onde)\b");
    // Passo a passo de remoção inventado para um item do catálogo (PC 3a88880): "Para remover o Xbox: 1. Vá em **Configurações >
    // Sistema > Xbox**. 2. Clique em **Remover**." O caminho não existe, e o item tem aviso e trava no cartão. Conta o texto que
    // cita um item do catálogo, diz como remover ou desinstalar e leva a um caminho do Windows ou a um botão de remover. A frase
    // do Nexo "Outros programas são desinstalados em Configurações > Aplicativos do Windows" não cita item do catálogo e passa;
    // o caminho de outro programa ("para desinstalar o Chrome, abra Configurações > Aplicativos") também.
    private static readonly Regex WindowsPath = new(@"\b(?:configurac\w*|settings|painel de controle)\b\W*>|\bclique\W+(?:em\W+)?\W*(?:remov|desinstal)\w*");
    // O item do catálogo precisa ser o objeto do verbo de remover (@qa, gate da d8a2cce): "O Teams não abre. Para remover o cache
    // do Teams, abra Configurações > Aplicativos e clique em Redefinir" e "Para desinstalar a atualização…, abra Configurações >
    // Windows Update… O app Clima voltou a abrir?" são orientação real e passam.
    public static bool WritesAppRemovalSteps(string normalized) =>
        AppRemovalQuestion.RemovesCatalogItem(normalized) && WindowsPath.IsMatch(normalized) && !RemovalPath.IsMatch(normalized);
    // Trecho citado na 2ª recusa: a oferta ou o passo do caminho inventado.
    public static bool AppRemovalPassage(string normalized) => OffersAppRemoval(normalized)
        || AppRemovalQuestion.RemovesCatalogItem(normalized) || Regex.IsMatch(normalized, @"\bclique\W+(?:em\W+)?\W*(?:remov|desinstal)\w*");
    // Fim do turno pelas recusas da oferta de remover (PC f6a898c, "como tiro o Teams da barra de tarefas?"): o modelo deu os passos
    // da barra e terminou com "Se desejar, posso ajudar a desinstalar o Teams completamente"; recusado 3 vezes com o mesmo texto, o
    // turno acabava no texto pronto do cartão e a resposta sobre a barra se perdia. Aqui saem só as frases recusadas (a oferta, o
    // passo do caminho inventado) e as que falam de remover apps sem oferecer ("Entendo que você quer remover aplicativos
    // desnecessários"): esse assunto fica com a linha do cartão. As quebras de linha da resposta ficam, para uma lista numerada
    // continuar lista. Se sobrar pouco, o fim volta a ser o de antes (AgentService.SalvagedAnswer).
    public static string WithoutAppRemovalOffer(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(line => string.Join(" ", Regex.Split(line.Trim(), @"(?<=[.!?])\s+")
            .Where(sentence => sentence.Length > 0 && !AppRemovalPassage(Normalize(sentence)) && !RemovalTalk(Normalize(sentence)))));
        return Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
    }
    // @qa (bf6c7cd): "desinstale e reinstale o Teams" é reparo, não remoção; fica no resto aproveitado.
    private static bool RemovalTalk(string normalized) => AppRemoval.IsMatch(normalized) && !Regex.IsMatch(normalized, @"\breinstal\w*");
    public static bool OffersAppRemoval(string normalized) => Regex.Split(normalized, @"[.!?;\n]+").Any(sentence =>
        AppRemoval.IsMatch(sentence) && !RemovalPath.IsMatch(sentence) && !RemovalExplanation.IsMatch(sentence)
        && RemovalOffer.Matches(sentence).Any(offer => !Regex.IsMatch(sentence[..offer.Index], @"\b(?:nao|nem|nunca)\s+(?:\w+\s+){0,2}$")));

    // Componentes do Windows (#150): o Visual C++ Redistributable, o DirectX e o .NET são instalados só pelo cartão
    // "Componentes do Windows para programas", no menu Ferramentas, com os instaladores oficiais da Microsoft, a
    // assinatura conferida e o UAC. O modelo não oferece nem executa essa instalação pelo chat, como na remoção de apps (#127)
    // e no driver do sensor (#135). É recusada a oferta em 1ª pessoa de instalar, reinstalar, baixar, atualizar ou reparar um
    // desses componentes, com ele a até quatro palavras do verbo. Passam a negação antes da oferta ("não posso instalar o
    // DirectX pelo chat"), a orientação ("posso explicar como…", "posso indicar onde…") e a frase sem oferta ("você pode
    // instalar o DirectX pelo cartão…"). Driver, impressora e Office não são componentes do cartão.
    // @qa (f7bcb21): cada "posso/vou…" é testado sozinho (a busca é sobreposta), para que um "não consigo…" ou um "posso explicar…"
    // no começo não esconda o "mas posso instalar o Visual C++" seguinte. O verbo é só a forma verbal (infinitivo, subjuntivo,
    // futuro, gerúndio, ênclise, "eu instalo"), e não o substantivo nem o adjetivo: "versão instalada do .NET", "erro de
    // instalação do DirectX", "FPS baixo com o DirectX", "atualização do .NET" e "repare que o DirectX…" passam. "Fazer o
    // download" e "executar o instalador" também são oferta. A orientação só vale quando nada coordenado ("e", "mas", "ou",
    // "depois", "também", "já") separa o verbo de orientar da instalação: "posso explicar o erro e já instalar o VC++" é oferta.
    internal const string RuntimeInstallReason = "A resposta oferece instalar, baixar ou atualizar o Visual C++ Redistributable, o DirectX ou o .NET, e o Nexo não faz isso pelo chat. Tire essa oferta e mantenha o resto da resposta. Se o usuário quiser instalar esses componentes, indique o cartão “" + RuntimeCard + "”, no menu Ferramentas, onde “Verificar este PC” mostra o que falta neste PC e só os instaladores oficiais da Microsoft são usados.";
    internal const string RuntimeCard = "Componentes do Windows para programas";
    private const string RuntimeObject = @"(?:(?:visual )?c\+\+|vc\+\+|vc_?redist\w*|redistribu\w*|directx|dxsetup|dxwebsetup|d3dx\w*|xinput\w*|xaudio\w*|\.net\b|dotnet\b|net framework\b|bibliotecas? (?:do|de) (?:visual|runtime|jogos?)\b)";
    private const string RuntimeStem = @"(?:reinstal|instal|baix|atualiz|repar)";
    private const string RuntimeVerb = @"(?:(?!repar(?:e|em)\s+que\b)" + RuntimeStem + @"(?:ar(?:ei|emos|ia|iamos|mos)?|e|em|emos|ando|asse|assem|assemos|a-(?:lo|la|los|las))"
        + @"|(?<=\beu\s+(?:mesmo\s+)?)" + RuntimeStem + @"o|(?:fazer|faco|faca|facamos|farei)\s+o\s+download"
        + @"|(?:rodar|executar|abrir|iniciar|rodo|executo|abro|inicio|rode|execute|abra|inicie)(?=\s+(?:o\s+)?(?:instalador|vc_?redist|dxwebsetup|dxsetup)))";
    private static readonly Regex RuntimeOffer = new(@"(?=(?<head>\b(?:posso|podemos|poderia|poderiamos|consigo|vou|vamos|irei|quer que eu|deseja que eu|gostaria que eu|prefere que eu|eu (?:te |lhe )?ajudo"
        + @"|eu(?=\s+(?:mesmo\s+)?" + RuntimeStem + @"o\b))\b)(?<lead>[^;:!?\n]*?)\b" + RuntimeVerb + @"\b\s+(?:[^\s.;:!?]+\s+){0,4}?" + RuntimeObject + ")");
    private static readonly Regex RuntimeOrientation = new(@"\b(?:explic|orient|ensin|indic|mostr)\w*|\b(?:dizer|falar) (?:como|onde)\b");
    private static bool RuntimeOriented(string lead) => RuntimeOrientation.IsMatch(lead)
        && Regex.Matches(lead, @"\b(?:explic|orient|ensin|indic|mostr)\w*|\b(?:como|onde)\b").Last() is var anchor
        && !Regex.IsMatch(lead[(anchor.Index + anchor.Length)..], @"\b(?:e|mas|ou|depois|tambem|ja)\b");
    public static bool OffersRuntimeInstall(string normalized) => RuntimeOffer.Matches(normalized).Any(offer =>
        !Regex.IsMatch(normalized[..offer.Index], @"\b(?:nao|nem|nunca)\s+(?:\w+\s+){0,2}$") && !RuntimeOriented(offer.Groups["lead"].Value)
        && !Regex.IsMatch(offer.Groups["lead"].Value, @"(?:^|\s)(?:nao|nem|nunca)\s*$") && !Regex.IsMatch(offer.Groups["lead"].Value, @"[.]\s"));

    // Instrução que acompanha a recusa: o motivo fixo e, para a ação anunciada, o trecho e a saída quando não há ferramenta.
    public static string Guidance(string reason, string message) => reason == PendingActionReason && PendingActionPhrase(message) is { } phrase
        ? reason + " Trecho recusado: “" + phrase + "”. " + (Cleanup(Normalize(message)) ? NoCleanupTool
            : "Se nenhuma ferramenta disponível faz essa ação, não a anuncie nem a ofereça: diga o que as consultas mostraram, explique que o Nexo não faz isso e indique o que o usuário pode fazer por conta própria.")
        : reason;

    public static string? Validate(string name, JsonElement args, string message, bool readOnly = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Informe uma conclusão ou pergunta não vazia.";
        var text = Normalize(message);
        // Antes das regras gerais: com elas, a oferta de remover apps recebia "Use a ferramenta…", e não existe ferramenta (#127).
        if (OffersAppRemoval(text) || WritesAppRemovalSteps(text)) return AppRemovalReason;
        // Antes da ação anunciada e do "posso instalar…" genérico: não existe ferramenta que instale esses componentes (#150).
        if (OffersRuntimeInstall(text)) return RuntimeInstallReason;
        if (Regex.IsMatch(text, @"\b(posso|podemos|devo)\b[^?.]{0,80}\b(permissao|autorizacao)\b"))
            return "Não solicite permissão no chat para uma ferramenta. Consulte o estado e use a ferramenta adequada; ela apresenta a autorização concreta.";
        if (message.Contains('?') && Regex.IsMatch(text, @"\b(posso confirmar se|voce esta|esta|se sente)\b[^?]{0,90}\b(confortavel|de acordo)\b|\btudo bem se\b[^?]{0,70}\b(reinici|ativ|habilit|alter|limp|instal|configur)"))
            return "Não peça consentimento no chat para a alteração. Chame a ferramenta adequada; o executor mostra a confirmação concreta antes de executar.";
        if (message.Contains('?') && Regex.IsMatch(text, @"\b(qual|quais)\b[^?]*(interface|adaptador|ethernet|wi-fi)"))
            return "Não transfira a escolha da interface ao usuário. Consulte get_network e probe_network_interfaces para comparar as saídas. Se faltar evidência, pergunte sobre um sintoma observável, como outros aparelhos ou sites afetados.";
        if (Regex.IsMatch(text, @"\b(confirme se|confirma se|deseja obter|quer obter|autoriza)\b"))
            return readOnly ? "O usuário pediu apenas consulta. Apresente os dados e achados observados, sem pedir confirmação ou propor mudanças." : "Não peça confirmação no chat para uma mudança identificada. Use a ferramenta correspondente para apresentar a confirmação concreta; para uma informação ausente pergunte o dado específico.";
        if (PendingAction.IsMatch(text))
            return PendingActionReason;
        if (Regex.IsMatch(text, @"\b(posso|podemos|devo|deseja que eu|quer que eu)\s+(executar|execute|testar|teste|verificar|verifique|consultar|consulte|listar|liste|limpar|limpe|ativar|ative|configurar|configure|continuar|continue|prosseguir|prossiga|habilitar|habilita|habilite|reiniciar|reinicia|reinicie|instalar|instala|instale)\b"))
            return "Use a ferramenta para a próxima consulta ou correção. A autorização para mudanças é apresentada pelo executor; não encerre pedindo Ok no chat.";
        if (name == "ask_user")
        {
            if (!args.TryGetProperty("reason", out var reason) || reason.GetString() is not ("missing_information" or "ambiguous_target" or "manual_test"))
                return "ask_user exige motivo: missing_information, ambiguous_target ou manual_test.";
            if (Regex.IsMatch(text, @"\b(ok|continuar|prosseguir)\s*\?"))
                return "Não peça permissão ou Ok no chat. Para consultas use a ferramenta; para mudanças use a ferramenta de correção, que apresenta a confirmação concreta. Pergunte somente a informação que falta ou peça um teste manual específico.";
        }
        return null;
    }

    internal static string Normalize(string text) => new(text.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray());
}
