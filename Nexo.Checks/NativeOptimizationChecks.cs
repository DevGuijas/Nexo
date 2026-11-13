using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nexo.Support;
namespace Nexo.Checks;
internal static class NativeOptimizationChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        // Every storage command is replaced inside this fresh PowerShell process. No native disk operation runs.
        var fixture = """
            $script:calls=@()
            function Get-Volume {
                @(
                [pscustomobject]@{DriveLetter='C';DriveType='Fixed';HealthStatus='Healthy';OperationalStatus=@('OK');FileSystemType='NTFS'},
                [pscustomobject]@{DriveLetter='D';DriveType='Fixed';HealthStatus='Warning';OperationalStatus=@('OK');FileSystemType='NTFS'},
                [pscustomobject]@{DriveLetter='E';DriveType='Removable';HealthStatus='Healthy';OperationalStatus=@('OK');FileSystemType='NTFS'},
                [pscustomobject]@{DriveLetter='F';DriveType='Fixed';HealthStatus='Healthy';OperationalStatus=@('OK');FileSystemType='FAT32'},
                [pscustomobject]@{DriveLetter='G';DriveType='Fixed';HealthStatus='Healthy';OperationalStatus=@('OK');FileSystemType='NTFS'},
                [pscustomobject]@{DriveLetter='H';DriveType='Fixed';HealthStatus='Healthy';OperationalStatus=@('OK');FileSystemType='ReFS'})
            }
            function Get-Partition { param([char]$DriveLetter) [pscustomobject]@{Letter=$DriveLetter} }
            function Get-Disk {
                [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject)
                process { [pscustomobject]@{HealthStatus=$(if($InputObject.Letter -eq 'G'){'Warning'}else{'Healthy'});IsOffline=$false;IsReadOnly=$false;OperationalStatus=@('Online')} }
            }
            function Optimize-Volume {
                [CmdletBinding()] param([char]$DriveLetter)
                $script:calls += [string]$DriveLetter
                if($DriveLetter -eq 'H'){throw 'fixture failure'}
            }
            $result=& {
            """ + BasicOptimizationService.DiskScript + """

            }
            [pscustomobject]@{Results=@((ConvertFrom-Json $result) | ForEach-Object { $_ });Calls=@($script:calls)} | ConvertTo-Json -Depth 6 -Compress
            """;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach(var arg in new[]{"-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(fixture))}) start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;
        var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if(process.ExitCode!=0) throw new Exception(await error);
        using var result=JsonDocument.Parse(await output);
        var calls=result.RootElement.GetProperty("Calls").EnumerateArray().Select(v=>v.GetString()).ToArray();
        var outcomes=result.RootElement.GetProperty("Results").EnumerateArray().ToArray();
        check(calls.SequenceEqual(new[]{"C","H"}) && outcomes.Any(v=>v.GetProperty("Drive").GetString()=="C" && v.GetProperty("Status").GetString()=="Completed"), "Otimização nativa: somente volumes fixos saudáveis suportados recebem operação padrão Windows");
        check(outcomes.Any(v=>v.GetProperty("Drive").GetString()=="G" && v.GetProperty("Status").GetString()=="Skipped") && outcomes.Any(v=>v.GetProperty("Drive").GetString()=="H" && v.GetProperty("Status").GetString()=="Failed"), "Otimização nativa: disco não saudável é ignorado e erro nativo é registrado");
    }
}
