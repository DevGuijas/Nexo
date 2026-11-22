using System.Text;

namespace Nexo.Support;

/// <summary>
/// Restringe o PSModulePath aos módulos do próprio Windows PowerShell antes de qualquer comando (#131).
/// Num processo elevado do mesmo usuário, o PSModulePath padrão inclui Documentos\WindowsPowerShell\Modules, que o
/// usuário grava sem elevação: um módulo plantado ali com o nome de um módulo do Windows (DnsClient, NetAdapter…) seria
/// autocarregado com admin. O -NoProfile não impede o autocarregamento.
/// O caminho vem de $PSHOME, a pasta do powershell.exe que o Nexo inicia (System32, por GetSystemDirectory, e não por
/// variável de ambiente). Variáveis como SystemRoot e windir não servem: o usuário consegue defini-las no próprio
/// ambiente (HKCU\Environment), e um processo elevado as recebe.
/// </summary>
public static class PowerShellModulePath
{
    public const string Guard = "$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules');";
    public const string MissingGuardMessage = "O script do PowerShell não restringe os módulos aos do Windows ($PSHOME\\Modules). A operação não foi iniciada e nada foi alterado.";

    /// <summary>O script com a fixação como primeiro comando.</summary>
    public static string Prefix(string script) => Guard + " " + script;

    /// <summary>
    /// Confere, antes do UAC, que o script entregue ao powershell.exe (em -EncodedCommand ou -Command) começa com a
    /// fixação. Script por arquivo (-File), ausente, ilegível ou com a fixação depois de outro comando é recusado.
    /// </summary>
    public static void Require(IReadOnlyList<string> arguments)
    {
        string? script = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.Equals("-File", StringComparison.OrdinalIgnoreCase) || argument.Equals("-f", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(MissingGuardMessage);
            if (script != null) continue;
            if (argument.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Count)
            {
                try { script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[i + 1])); }
                catch (FormatException) { throw new InvalidOperationException(MissingGuardMessage); }
            }
            else if (argument.Equals("-Command", StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Count)
                script = arguments[i + 1];
        }
        if (script == null || !script.StartsWith(Guard, StringComparison.Ordinal)) throw new InvalidOperationException(MissingGuardMessage);
    }
}
