using System.Globalization;

namespace Nexo.Support;

/// <summary>Texto do resultado de "Apps pré-instalados e ajustes do Windows" mostrado na tela, sem depender do WPF.</summary>
public static class DebloatPresentation
{
    /// <summary>Item cujo efeito completo só vem no próximo logon; o aviso sai no resultado quando a aplicação termina Ok.</summary>
    internal const string NextLogonItem = "visual-effects";

    /// <summary>Aviso do cartão, abaixo do título.</summary>
    public const string CardNotice = "Antes de aplicar, o Nexo cria um ponto de restauração do Windows, e o Windows pede autorização de administrador. Se o Windows já criou um ponto nas últimas 24 horas, esse ponto é usado como proteção, porque ele cria no máximo um por dia. Sem ponto confirmado, só os apps e os ajustes do seu perfil são aplicados. Serviços, tarefas e ajustes do perfil podem ser desfeitos aqui. O Desfazer tenta restaurar os apps removidos pelo pacote que o Windows mantém neste PC; se não der, eles voltam pela Microsoft Store, quando ela ainda oferece o app. Não há garantia de ganho de desempenho.";

    /// <summary>A confirmação antes de aplicar, com os itens escolhidos.</summary>
    public static string ApplyQuestion(IEnumerable<string> titles) =>
        "O Nexo vai criar um ponto de restauração e depois aplicar:\n\n" + string.Join("\n", titles.Select(t => "• " + t))
        + "\n\nApps são removidos só do seu usuário. O Desfazer tenta restaurá-los pelo pacote que o Windows mantém neste PC; se não der, eles voltam pela Microsoft Store, quando ela ainda oferece o app. Continuar?";

    public static string Result(DebloatResult result, bool undo)
    {
        var point = result.RestorePoint == null ? "" : result.RestorePoint.Ready
            ? result.RestorePointReusedAt is { } reusedAt
                ? $" Ponto de restauração {result.RestorePoint.SequenceNumber}, de {reusedAt.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture)}, usado como proteção: o Windows cria no máximo um ponto por dia."
                : $" Ponto de restauração {result.RestorePoint.SequenceNumber} confirmado."
            : string.IsNullOrWhiteSpace(result.RestorePoint.Reason) ? " Ponto de restauração não confirmado."
            : " Ponto de restauração não confirmado: " + Sentence(result.RestorePoint.Reason);
        return (undo ? "Desfazer" : "Aplicação") + ": " + DebloatService.Summary(result.Status, result.Items) + point + "\n"
            + string.Join("\n", result.Items.Select(i => Line(i, undo)))
            + (result.Warning == null ? "" : "\n" + result.Warning)
            + "\nClique em Verificar este PC para ver o estado atual.";
    }

    private static string Line(DebloatOutcome item, bool undo)
    {
        var line = $"{item.Title}: {Label(item.Status)}. {item.Detail}";
        if (undo || item.Status != DebloatStatus.Ok || item.Id != NextLogonItem || DebloatCatalog.Find(item.Id)?.Warning is not { Length: > 0 } notice) return line;
        return $"{item.Title}: {Label(item.Status)}." + (item.Detail.Trim().Length == 0 ? "" : " " + Sentence(item.Detail)) + " " + notice;
    }

    private static string Label(DebloatStatus status) => status switch
    {
        DebloatStatus.Ok => "ok", DebloatStatus.Partial => "parcial", DebloatStatus.NotFound => "não encontrado",
        DebloatStatus.Failed => "falha", _ => "não aplicado",
    };

    /// <summary>Fecha a frase com ponto só quando ela ainda não termina em pontuação final.</summary>
    internal static string Sentence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed[^1] is '.' or '!' or '?' or '…' ? trimmed : trimmed + ".";
    }
}

/// <summary>
/// Trava do progresso da tela. O <see cref="Progress{T}"/> posta cada mensagem no Dispatcher; quando a operação termina sem
/// um await real, o último Report chega depois do resultado final e o sobrescreveria. Depois de <see cref="Finish"/>, nada passa.
/// </summary>
public sealed class DebloatProgressGate
{
    private volatile bool finished;
    public bool Finished => finished;
    public void Finish() => finished = true;
    public Action<string> Guard(Action<string> show) => message => { if (!finished) show(message); };
}
