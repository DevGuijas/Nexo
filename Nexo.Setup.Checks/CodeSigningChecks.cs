using System.Diagnostics;

internal static class CodeSigningChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var script = Path.Combine(root, "scripts", "Sign-NexoArtifacts.ps1");
        var directory = Path.Combine(Path.GetTempPath(), "NexoCodeSigningChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var unsigned = Path.Combine(directory, "unsigned.exe");
            File.WriteAllBytes(unsigned, [77, 90, 0, 0]);
            var before = File.ReadAllBytes(unsigned);

            var verify = RunPowerShell(script, "-Paths", unsigned, "-VerifyOnly");
            check(verify.ExitCode != 0 && verify.Output.Contains("assinatura Authenticode válida", StringComparison.OrdinalIgnoreCase)
                && before.SequenceEqual(File.ReadAllBytes(unsigned)),
                "Assinatura: binário sem Authenticode é recusado sem alterar o arquivo");

            var sign = RunPowerShell(script, "-Paths", unsigned, "-Thumbprint", new string('0', 40), "-TimestampServer", "http://timestamp.example.invalid");
            check(sign.ExitCode != 0 && sign.Output.Contains("certificado de assinatura de código", StringComparison.OrdinalIgnoreCase)
                && before.SequenceEqual(File.ReadAllBytes(unsigned)),
                "Assinatura: certificado ausente impede a distribuição sem tocar no binário");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static (int ExitCode, string Output) RunPowerShell(string script, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("pwsh")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-File", script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
