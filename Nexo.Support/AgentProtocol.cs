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

    public static string? Validate(string name, JsonElement args, string message, bool readOnly = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Informe uma conclusão ou pergunta não vazia.";
        var text = Normalize(message);
        if (Regex.IsMatch(text, @"\b(posso|podemos|devo)\b[^?.]{0,80}\b(permissao|autorizacao)\b"))
            return "Não solicite permissão no chat para uma ferramenta. Consulte o estado e use a ferramenta adequada; ela apresenta a autorização concreta.";
        if (message.Contains('?') && Regex.IsMatch(text, @"\b(posso confirmar se|voce esta|esta|se sente)\b[^?]{0,90}\b(confortavel|de acordo)\b|\btudo bem se\b[^?]{0,70}\b(reinici|ativ|habilit|alter|limp|instal|configur)"))
            return "Não peça consentimento no chat para a alteração. Chame a ferramenta adequada; o executor mostra a confirmação concreta antes de executar.";
        if (message.Contains('?') && Regex.IsMatch(text, @"\b(qual|quais)\b[^?]*(interface|adaptador|ethernet|wi-fi)"))
            return "Não transfira a escolha da interface ao usuário. Consulte get_network e probe_network_interfaces para comparar as saídas. Se faltar evidência, pergunte sobre um sintoma observável, como outros aparelhos ou sites afetados.";
        if (Regex.IsMatch(text, @"\b(confirme se|confirma se|deseja obter|quer obter|autoriza)\b"))
            return readOnly ? "O usuário pediu apenas consulta. Apresente os dados e achados observados, sem pedir confirmação ou propor mudanças." : "Não peça confirmação no chat para uma mudança identificada. Use a ferramenta correspondente para apresentar a confirmação concreta; para uma informação ausente pergunte o dado específico.";
        if (Regex.IsMatch(text, @"\b(vou|vamos|farei|iremos)\s+(agora\s+)?(fazer|executar|testar|verificar|consultar|listar|limpar|ativar|configurar|tentar|acessar|habilitar|reiniciar|instalar|investigar)\b"))
            return "Há uma ação pendente anunciada. Chame a ferramenta correspondente agora; uma promessa não encerra o turno. Se está bloqueado, explique a limitação sem prometer executar.";
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
