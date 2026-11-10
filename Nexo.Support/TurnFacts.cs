using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexo.Support;

// Situação de uma alteração pedida no turno, da mais forte para a mais fraca: feita, sem confirmação do resultado,
// concluída sem mudança, não concluída, recusada por você e barrada pelo Nexo.
public enum ChangeState { Changed, Unconfirmed, Unchanged, NotCompleted, Denied, Refused }

// Linha de fatos de cada resposta final do turno (#73, opção A): diz quais alterações o Nexo fez, a partir só dos
// resultados do executor e das travas deste turno, nunca do texto do modelo. Ela vem sempre por último, separada do texto.
public sealed class TurnFacts
{
    public const string Prefix = "Alterações feitas pelo Nexo neste turno:";
    // No histórico que o modelo relê nos turnos seguintes, a linha fala daquele turno, não do atual.
    public const string HistoryPrefix = "Alterações feitas pelo Nexo naquele turno:";
    // Marca, numa exceção do executor, que a alteração não chegou a começar (validação ou aprovação antes de qualquer mudança,
    // processo elevado que não iniciou). Sem ela, uma exceção depois do envio fica "sem confirmação do resultado".
    public const string NotStartedKey = "Nexo.ChangeNotStarted";
    private readonly List<(string Key, string Description, ChangeState State)> changes = [];

    // Uma alteração é a ferramenta e o alvo canônico; aparece uma vez, na ordem em que foi pedida pela primeira vez, com o
    // estado mais forte e a descrição mais recente (depois de uma consulta, o nome legível no lugar do ID).
    public void Record(string key, string description, ChangeState state)
    {
        var index = changes.FindIndex(c => c.Key == key);
        if (index < 0) changes.Add((key, description, state));
        else changes[index] = (key, description, state < changes[index].State ? state : changes[index].State);
    }

    public ChangeState? State(string key) => changes.FindIndex(c => c.Key == key) is var index and >= 0 ? changes[index].State : null;

    // Exceção depois do envio ao executor: sem confirmação, a menos que o executor marque que a alteração nem começou.
    public static ChangeState Thrown(Exception exception) => exception.Data.Contains(NotStartedKey) ? ChangeState.NotCompleted : ChangeState.Unconfirmed;

    // O estado vem só dos campos que o executor devolve. "Não concluída" só quando ele afirma que nada mudou (changed=false
    // explícito, restauração confirmada ou proteção indisponível antes do instalador); na dúvida, "sem confirmação".
    public static ChangeState Classify(string name, string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ChangeState.Unconfirmed;
            bool Flag(JsonElement source, string key) => source.ValueKind == JsonValueKind.Object && source.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
            if (Flag(root, "denied")) return ChangeState.Denied;
            var data = root.TryGetProperty("data", out var payload) ? payload : default;
            var verification = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("Verification", out var check) && check.ValueKind == JsonValueKind.String ? check.GetString() : null;
            if (Flag(root, "success"))
            {
                if (InvestigationEvidence.ExplicitlyUnchanged(root) || verification == "unchanged" || name == "allow_camera" && CameraAlreadyAllowed(data)) return ChangeState.Unchanged;
                return verification is null or "version_changed" or "expected_version_confirmed" or "pending_restart" ? ChangeState.Changed : ChangeState.Unconfirmed;
            }
            if (Flag(root, "installationReported")) return ChangeState.Unconfirmed;
            if (Flag(root, "reverted") || Flag(root, "recoveryProtectionUnavailable") || InvestigationEvidence.ExplicitlyUnchanged(root)) return ChangeState.NotCompleted;
            return ChangeState.Unconfirmed;
        }
        catch (JsonException) { return ChangeState.Unconfirmed; }
    }

    // allow_camera devolve o valor do registro antes e depois; já "Allow" antes é nada a mudar.
    private static bool CameraAlreadyAllowed(JsonElement data)
    {
        try
        {
            if (data.ValueKind != JsonValueKind.String) return false;
            using var privacy = JsonDocument.Parse(data.GetString() ?? "");
            return privacy.RootElement.ValueKind == JsonValueKind.Object && privacy.RootElement.TryGetProperty("Before", out var before)
                && before.ValueKind == JsonValueKind.String && before.GetString() == "Allow";
        }
        catch (JsonException) { return false; }
    }

    public string Line()
    {
        string List(params ChangeState[] states) => string.Join("; ", changes.Where(c => states.Contains(c.State))
            .Select(c => c.Description + c.State switch { ChangeState.Denied => " (recusada por você)", ChangeState.Refused => " (barrada pelo Nexo)", _ => "" }));
        var done = List(ChangeState.Changed);
        var unconfirmed = List(ChangeState.Unconfirmed);
        var line = new StringBuilder(Prefix + " " + (done.Length > 0 ? done : unconfirmed.Length > 0 ? "nenhuma confirmada" : "nenhuma") + ".");
        if (unconfirmed.Length > 0) line.Append(" Sem confirmação do resultado: ").Append(unconfirmed).Append(" — confira antes de continuar.");
        void Add(string label, string items) { if (items.Length > 0) line.Append(' ').Append(label).Append(' ').Append(items).Append('.'); }
        Add("Concluídas sem mudança:", List(ChangeState.Unchanged));
        Add("Não concluídas:", List(ChangeState.NotCompleted));
        Add("Não executadas:", List(ChangeState.Denied, ChangeState.Refused));
        return line.ToString();
    }

    // A resposta final: o texto (do modelo ou do Nexo) sem as frases que imitem a linha de fatos, e a linha verdadeira no fim.
    // A conversa é exibida num TextBox simples, sem Markdown; quebras de linha Unicode viram \n e caracteres de formatação
    // (largura zero, controle de direção) saem, para que nada empurre, esconda ou inverta a linha na tela.
    public static string Compose(string text, string line)
    {
        var lines = Regex.Split(Plain(text), "\n").Select(WithoutForgery).Where(l => l != null);
        var body = Regex.Replace(string.Join("\n", lines), @"[ \t]+\n", "\n");
        body = Regex.Replace(body, @"\n{3,}", "\n\n").Trim();
        return body.Length == 0 ? line : body + "\n\n" + line;
    }

    // Versão guardada no histórico do modelo: a mesma resposta, com a linha falando "naquele turno".
    public static string ForHistory(string answer)
    {
        var at = answer.LastIndexOf(Prefix, StringComparison.Ordinal);
        return at >= 0 && (at == 0 || answer[at - 1] == '\n') && answer.IndexOf('\n', at) < 0 ? answer[..at] + HistoryPrefix + answer[(at + Prefix.Length)..] : answer;
    }

    // O texto acima da linha de fatos, para quem analisa a resposta (respostas rápidas, cor do cartão).
    public static string Body(string answer)
    {
        var at = answer.LastIndexOf("\n" + Prefix, StringComparison.Ordinal);
        if (at >= 0 && answer.IndexOf('\n', at + 1) < 0) return answer[..at].TrimEnd();
        return answer.StartsWith(Prefix, StringComparison.Ordinal) && !answer.Contains('\n') ? "" : answer;
    }

    private static string Plain(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var rune in text.Replace("\r\n", "\n").EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value is '\n' or '\r' or 0x0B or 0x0C or 0x85 || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) clean.Append('\n');
            else if (rune.Value == '\t') clean.Append(' ');
            else if (category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            else clean.Append(rune.ToString());
        }
        return clean.ToString();
    }

    // Frases que imitam a linha de fatos (ou a do relatório antigo), depois de NFKC, sem caixa, acento, Markdown, aspas e
    // pontuação.
    private static readonly string[] Signatures = ["alteracoes feitas pelo nexo", "feitas pelo nexo neste turno", "feitas pelo nexo naquele turno",
        "alteracoes feitas neste turno", "concluidas sem mudanca", "sem confirmacao do resultado", "nenhuma confirmada", "nao executadas", "nao concluidas",
        "nenhuma alteracao concluida pelo nexo", "operacoes concluidas neste turno"];
    internal static bool Forged(string line) => Match(line) != null;

    // A linha sem as frases que imitam a de fatos: sai a frase inteira em que a imitação está, e o resto da linha fica.
    // Linha que só tinha imitação some (null).
    internal static string? WithoutForgery(string line)
    {
        var changed = false;
        while (Match(line) is { } found)
        {
            var start = found.Start;
            while (start > 0 && !(IsTerminator(line[start - 1]) && (start == line.Length || char.IsWhiteSpace(line[start])))) start--;
            var end = found.End;
            while (end < line.Length && !(IsTerminator(line[end]) && (end + 1 == line.Length || char.IsWhiteSpace(line[end + 1])))) end++;
            line = (line[..start] + (end < line.Length ? line[(end + 1)..] : "")).Trim();
            changed = true;
        }
        return changed && line.Length == 0 ? null : line;
    }
    private static bool IsTerminator(char c) => c is '.' or '!' or '?' or '…';

    // Onde está a imitação no texto original: o índice do primeiro caractere e o fim do trecho que bate com a assinatura.
    private static (int Start, int End)? Match(string line)
    {
        var letters = new StringBuilder(line.Length);
        var origin = new List<int>(line.Length);
        var space = true;
        for (var i = 0; i < line.Length; i += char.IsSurrogatePair(line, i) ? 2 : 1)
        {
            var piece = (char.IsSurrogatePair(line, i) ? line.Substring(i, 2) : line[i].ToString()).Normalize(NormalizationForm.FormKC).Normalize(NormalizationForm.FormD);
            foreach (var c in piece)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format) continue;
                if (char.IsLetterOrDigit(c)) { letters.Append(char.ToLowerInvariant(c)); origin.Add(i); space = false; }
                else if (!space) { letters.Append(' '); origin.Add(i); space = true; }
            }
        }
        var text = letters.ToString();
        var best = -1; var length = 0;
        foreach (var signature in Signatures)
            if (text.IndexOf(signature, StringComparison.Ordinal) is var at and >= 0 && (best < 0 || at < best)) { best = at; length = signature.Length; }
        return best < 0 ? null : (origin[best], origin[best + length - 1]);
    }
}
