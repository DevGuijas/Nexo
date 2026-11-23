namespace Nexo.Support;

/// <summary>Texto do resultado de "Apps pré-instalados e ajustes do Windows" mostrado na tela, sem depender do WPF.</summary>
public static class DebloatPresentation
{
    /// <summary>Item cujo efeito completo só vem no próximo logon; o aviso sai no resultado quando a aplicação termina Ok.</summary>
    internal const string NextLogonItem = "visual-effects";

    public static string Result(DebloatResult result, bool undo)
    {
        var point = result.RestorePoint == null ? "" : result.RestorePoint.Ready
            ? $" Ponto de restauração {result.RestorePoint.SequenceNumber} confirmado."
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
