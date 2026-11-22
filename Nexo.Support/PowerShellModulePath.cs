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
    /// Caminho de um executável do System32 como expressão do PowerShell, por GetSystemDirectory ([Environment]::SystemDirectory).
    /// Nunca por $env:WINDIR ou $env:SystemRoot: o usuário define essas variáveis em HKCU\Environment e o processo elevado as
    /// recebe, o que trocaria o netsh.exe ou o pnputil.exe por um executável dele, rodando com admin (#131).
    /// </summary>
    public static string SystemExecutable(string fileName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^[A-Za-z0-9_-]+\.exe$")) throw new ArgumentException("Nome de executável inválido.", nameof(fileName));
        return "([IO.Path]::Combine([Environment]::SystemDirectory,'" + fileName + "'))";
    }

    /// <summary>
    /// Confere, antes do UAC, o único formato de lançamento que o Nexo usa: exatamente -NoProfile, -NonInteractive e o
    /// script em -EncodedCommand ou -Command, que precisa começar com a fixação. Qualquer outro formato é recusado, em vez
    /// de interpretado: o powershell.exe aceita abreviações (-c, -e, -ec, -enc, -f) e outros parâmetros que carregam código
    /// (-File, -PSConsoleFile), junta ao -Command os argumentos que vierem depois e, sem -NoProfile, roda os perfis do
    /// usuário antes do script. Script ausente, ilegível ou com a fixação depois de outro comando também é recusado.
    /// </summary>
    public static void Require(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 4 || !string.Equals(arguments[0], "-NoProfile", StringComparison.Ordinal) || !string.Equals(arguments[1], "-NonInteractive", StringComparison.Ordinal))
            throw new InvalidOperationException(MissingGuardMessage);
        string script;
        if (string.Equals(arguments[2], "-EncodedCommand", StringComparison.Ordinal))
        {
            try { script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[3])); }
            catch (FormatException) { throw new InvalidOperationException(MissingGuardMessage); }
        }
        else if (string.Equals(arguments[2], "-Command", StringComparison.Ordinal)) script = arguments[3];
        else throw new InvalidOperationException(MissingGuardMessage);
        if (!script.StartsWith(Guard, StringComparison.Ordinal)) throw new InvalidOperationException(MissingGuardMessage);
    }
}
