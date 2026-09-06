using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexo.Support;

public static class QuickReplies
{
    // Conservative fallback for natural-language questions: never invent answers to open questions.
    public static string[] For(string message)
    {
        if (message.Count(c => c == '?') != 1) return [];
        var plain = new string(message.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace("**", "").Trim();
        const string boundary = @"(?:^|[.!]\s+|\n)\s*";
        if (Regex.IsMatch(plain, @"\bou\b[^?]*\?\s*$")) return [];
        if (Regex.IsMatch(plain, boundary + @"(?:funcionou|deu certo|o problema foi resolvido|a conexao (?:voltou|foi restabelecida)|conseguiu (?:conectar|acessar|imprimir|resolver))\b[^?\n]*\?\s*$"))
            return ["Sim, funcionou", "Ainda não"];
        if (Regex.IsMatch(plain, boundary + @"(?:voce\s+)?(?:deseja|quer|gostaria|posso|podemos|devo)\b[^?\n]*\?\s*$"))
            return ["Sim, por favor", "Não"];
        return [];
    }
}
