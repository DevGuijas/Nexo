using System.IO;
using System.Security.Principal;
using System.Text.Json;

namespace Nexo.Support;

internal static class OptimizationDiskExecutor
{
    public static async Task<string> RunAsync(string diskScript, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The elevated helper creates this directory below a protected parent. The
        // unelevated process can read and delete its result, but cannot replace it.
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NexoOptimize-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(folder, "result.json");
        var userSid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Usuário Windows indisponível.");
        var script = BuildScript(diskScript, folder, output, userSid);
        var started = false;
        var finished = false;
        try
        {
            using var process = WindowsTools.StartElevated(["-NoProfile", "-NonInteractive", "-Command", script]);
            started = true;
            // A operação nativa não é revertida pelo cancelamento. Aguarde a saída real antes
            // de permitir outra execução ou declarar o resultado.
            await process.WaitForExitAsync(CancellationToken.None);
            finished = true;
            if (process.ExitCode != 0 || !File.Exists(output))
                return JsonSerializer.Serialize(new { success = false, error = "Resultado da manutenção de disco indisponível." });
            return await File.ReadAllTextAsync(output, CancellationToken.None);
        }
        finally
        {
            if (!started || finished)
            {
                try { if (File.Exists(output)) File.Delete(output); if (Directory.Exists(folder)) Directory.Delete(folder); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static string BuildScript(string diskScript, string folder, string output, string userSid)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(userSid, @"^S-1-5(?:-\d+)+$"))
            throw new ArgumentException("SID de usuário inválido.", nameof(userSid));
        return $$$"""
            $ErrorActionPreference='Stop'
            $env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules')
            $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
            if(-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Elevação necessária'}
            $base=[Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
            if((Get-Item -LiteralPath $base -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Base protegida redirecionada'}
            $folder='{{{folder.Replace("'", "''")}}}'
            $output='{{{output.Replace("'", "''")}}}'
            if([IO.Path]::GetDirectoryName($folder) -ne $base -or [IO.Path]::GetDirectoryName($output) -ne $folder){throw 'Destino inválido'}
            $admins=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
            $system=New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
            $reader=New-Object Security.Principal.SecurityIdentifier('{{{userSid}}}')
            $acl=New-Object Security.AccessControl.DirectorySecurity
            $acl.SetOwner($admins)
            $acl.SetAccessRuleProtection($true,$false)
            foreach($sid in @($admins,$system)){
              $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
            }
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($reader,'ReadAndExecute,Delete','ContainerInherit,ObjectInherit','None','Allow')))
            if(Test-Path -LiteralPath $folder){throw 'Pasta de resultado preexistente'}
            [IO.Directory]::CreateDirectory($folder,$acl) | Out-Null
            $actual=Get-Acl -LiteralPath $folder
            if(-not $actual.AreAccessRulesProtected -or $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $admins.Value -or
               ((Get-Item -LiteralPath $folder -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Pasta de resultado insegura'}
            try {
              $data=& { {{{diskScript}}} }
              $result=@{success=$true;data=$data}
            } catch { $result=@{success=$false;error=$_.Exception.Message} }
            $json=$result|ConvertTo-Json -Depth 8 -Compress
            [IO.File]::WriteAllText($output,$json,(New-Object Text.UTF8Encoding($false)))
            """;
    }
}
