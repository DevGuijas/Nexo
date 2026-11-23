using System.IO;
using System.Text;

namespace Nexo.Support;

/// <summary>
/// As funções da cópia protegida do executor de rede (#10, #97), lidas do próprio NetworkTransaction.ps1 embutido, para o
/// Desfazer da #127 usar o mesmo esquema sem uma segunda implementação: pasta criada já com o descritor (dono
/// Administradores, herança desligada, SYSTEM e Administradores com controle total, Usuários só leitura), pasta existente
/// nunca adotada sem conferência, link recusado, arquivo gravado já protegido e conferido depois da troca.
/// O script de rede não muda. As aspas duplas, que só aparecem dentro de strings de aspas simples das mensagens, viram
/// aspas simples escapadas (''), porque o executor da otimização passa o script em -Command.
/// </summary>
internal static class ProtectedFolderScript
{
    internal static readonly string[] Functions =
        ["New-ProtectedSecurity", "New-ProtectedDirectory", "Get-UntrustedReason", "Assert-Trusted", "Use-ProtectedDirectory", "New-ProtectedFileSecurity", "Write-ProtectedFile"];

    private static readonly Lazy<string> text = new(() => Extract(Source()));

    /// <summary>$trustedSids e as funções acima, na ordem, sem aspas duplas.</summary>
    public static string Text => text.Value;

    internal static string Source()
    {
        using var stream = typeof(ProtectedFolderScript).Assembly.GetManifestResourceStream("Nexo.NetworkTransaction.ps1") ?? throw new IOException("Executor de rede ausente.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Cada função vai da linha "function Nome" até a primeira linha que é só "}" (todas no script de rede são assim).
    /// Sem encontrar o trecho, falha: uma mudança no script de rede não pode tirar a proteção do Desfazer em silêncio.
    /// </summary>
    internal static string Extract(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var output = new StringBuilder();
        var sids = lines.Where(line => line.StartsWith("$trustedSids = @(", StringComparison.Ordinal)).ToArray();
        if (sids.Length != 1) throw new InvalidOperationException("NetworkTransaction.ps1 sem a lista $trustedSids.");
        output.AppendLine(sids[0]);
        foreach (var name in Functions)
        {
            var start = Array.FindIndex(lines, line => line.StartsWith("function " + name + " ", StringComparison.Ordinal) || line.StartsWith("function " + name + "(", StringComparison.Ordinal));
            var end = start < 0 ? -1 : Array.FindIndex(lines, start + 1, line => line.TrimEnd() == "}");
            if (start < 0 || end < 0) throw new InvalidOperationException("NetworkTransaction.ps1 sem a função " + name + ".");
            for (var i = start; i <= end; i++) output.AppendLine(lines[i]);
        }
        return output.ToString().Replace("\"", "''");
    }
}
