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
    internal const string AppRemovalReason = "O Nexo não remove nem desinstala apps ou programas pelo chat: não ofereça remover e não pergunte quais apps remover. Para os apps que vêm com o Windows, indique Ferramentas > Apps pré-instalados e ajustes do Windows, onde o usuário verifica o PC, escolhe os itens e confirma antes de qualquer mudança; outros programas o usuário desinstala em Configurações > Aplicativos do Windows.";
    // Resposta do controlador quando o turno de um pedido de remoção de apps termina sem resposta aceita.
    internal const string AppRemovalGuidance = "O Nexo não remove apps pelo chat. Para os apps que vêm com o Windows, abra Ferramentas > Apps pré-instalados e ajustes do Windows: o Nexo verifica o que existe neste PC, você escolhe os itens e confirma antes de qualquer mudança, e pode desfazer depois. Outros programas são desinstalados em Configurações > Aplicativos do Windows.";
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
        + @"|\bdesinstal" + PastForm + @"(?!\w*(?:id[oa]s?|ad[oa]s?)\b)\w*\b(?:-(?:lo|la|los|las))?(?!\s+(?:\w+\s+){0,4}?(?:drivers?|dispositivos?|atualizac\w*|updates?)\b)");
    private static readonly Regex RemovalOffer = new(@"\b(?:posso|podemos|poderia|poderiamos|consigo|vou|vamos|irei|quer que eu|deseja que eu|gostaria que eu|prefere que eu|eu (?:te |lhe )?ajudo"
        + @"|me (?:diga|informe|fale|avise|mande|liste|passe)(?: (?:quais|qual|os|o nome|os nomes))?)\b");
    private static readonly Regex RemovalPath = new(@"\bferramentas\b[^.;!?]*\bpre-?\s?instalad|\bpre-?\s?instalad\w* e ajustes\b");
    private static readonly Regex RemovalExplanation = new(@"\b(?:explic|orient|ensin)\w*|\b(?:mostrar|dizer|indicar)(?: a voce| para voce)? (?:como|onde)\b");
    public static bool OffersAppRemoval(string normalized) => Regex.Split(normalized, @"[.!?;\n]+").Any(sentence =>
        AppRemoval.IsMatch(sentence) && !RemovalPath.IsMatch(sentence) && !RemovalExplanation.IsMatch(sentence)
        && RemovalOffer.Matches(sentence).Any(offer => !Regex.IsMatch(sentence[..offer.Index], @"\b(?:nao|nem|nunca)\s+(?:\w+\s+){0,2}$")));

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
        if (OffersAppRemoval(text)) return AppRemovalReason;
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
