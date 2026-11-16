using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.Win32;
namespace Nexo.Support;

public sealed class WindowsOfficeDeployment : IOfficeDeploymentPlatform
{
    private static int configureInProgress;
    public bool InstallationInProgress => Volatile.Read(ref configureInProgress) != 0;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> runProcess;
    public WindowsOfficeDeployment(Func<ProcessStartInfo, CancellationToken, Task<int>>? runProcess = null) => this.runProcess = runProcess ?? RunProcessAsync;
    public const string DownloadUrl = "https://download.microsoft.com/download/6c1eeb25-cf8b-41d9-8d0d-cc1dbc032140/officedeploymenttool_20430-20146.exe";
    public static bool AllowedDownload(Uri uri) => uri.Scheme == "https" && uri.Host == "download.microsoft.com" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    public Task<OfficePreflight> CheckAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var blockers = new List<string>();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) || !Environment.Is64BitOperatingSystem) blockers.Add("Exige Windows 10/11 de 64 bits.");
        try
        {
            var installationDrive = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))!);
            var temporaryDrive = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!);
            blockers.AddRange(OfficeDeploymentValidation.ResourceBlockers(installationDrive.AvailableFreeSpace,
                temporaryDrive.AvailableFreeSpace, System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()));
            var resuming = false;
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var c2r = machine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
                if (c2r != null)
                {
                    var products = c2r.GetValue("ProductReleaseIds") as string ?? "";
                    var platform = c2r.GetValue("Platform") as string ?? "";
                    var matchingIncomplete = OfficeDeploymentValidation.CanResume(products, platform,
                        c2r.GetValue("ClientCulture") as string ?? "", c2r.GetValue("StreamingFinished")?.ToString() ?? "");
                    if (matchingIncomplete) resuming = true;
                    else blockers.Add(OfficeDeploymentValidation.Conflict(products, platform));
                }
                foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall == null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var product = uninstall.OpenSubKey(name); var display = product?.GetValue("DisplayName") as string ?? "";
                        if (new[] { "Microsoft Office", "Microsoft 365", "Microsoft Visio", "Microsoft Project" }.Any(x => display.Contains(x, StringComparison.OrdinalIgnoreCase)) &&
                            !(resuming && display.Equals("Microsoft Office LTSC Professional Plus 2024 - pt-br", StringComparison.OrdinalIgnoreCase)))
                            blockers.Add("Produto Office existente: " + display);
                    }
                }
            }
            foreach (var process in Process.GetProcesses())
            {
                using (process) if (new[] { "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "MSACCESS", "ONENOTE", "MSPUB", "VISIO", "WINPROJ", "OfficeC2RClient", "setup" }.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase) ||
                    (!resuming && process.ProcessName.Equals("OfficeClickToRun", StringComparison.OrdinalIgnoreCase)))
                    blockers.Add("Feche o aplicativo ou aguarde o instalador: " + process.ProcessName);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { blockers.Add("Não foi possível verificar conflitos: " + ex.Message); }
        return Task.FromResult(new OfficePreflight(blockers.Count == 0, blockers.Distinct().ToArray()));
    }
    public Task<OfficeInstallationVerification> VerifyInstallationAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var configuration = machine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
        if (configuration == null) return Task.FromResult(OfficeDeploymentValidation.Verify(null));
        string Value(string name) => configuration.GetValue(name) as string ?? "";
        var installation = Value("InstallationPath");
        var protectedRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var filesPresent = !string.IsNullOrWhiteSpace(installation) && Path.IsPathFullyQualified(installation)
            && Path.GetFullPath(installation).StartsWith(protectedRoot, StringComparison.OrdinalIgnoreCase)
            && new[] { "WINWORD.EXE", "EXCEL.EXE", "POWERPNT.EXE" }.All(file => File.Exists(Path.Combine(installation, "root", "Office16", file)));
        return Task.FromResult(OfficeDeploymentValidation.Verify(new(Value("ProductReleaseIds"), Value("Platform"),
            Value("ClientCulture"), Value("VersionToReport"), Value("StreamingFinished").Equals("True", StringComparison.OrdinalIgnoreCase), filesPresent)));
    }
    public async Task DownloadAsync(string destination, CancellationToken token)
    {
        var uri = new Uri(DownloadUrl);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        for (var redirect = 0; redirect < 5; redirect++)
        {
            if (!AllowedDownload(uri)) throw new InvalidDataException("Origem ODT não permitida.");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location ?? throw new InvalidDataException("Redirecionamento sem destino.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920]; long total=0; int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0) { total += read; if (total > 30_000_000) throw new InvalidDataException("ODT excedeu limite de download."); await output.WriteAsync(buffer.AsMemory(0,read),token); }
            if (total == 0) throw new InvalidDataException("Download ODT vazio."); return;
        }
        throw new InvalidDataException("Redirecionamentos ODT excedidos.");
    }
    public async Task<bool> IsMicrosoftSignedAsync(string path, CancellationToken token)
    {
        var script = "$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules');$s=Get-AuthenticodeSignature -LiteralPath '" + path.Replace("'", "''") + "'; if($s.Status -eq 'Valid' -and $s.SignerCertificate.Subject -match '(^|, )O=Microsoft Corporation(,|$)'){exit 0}; exit 1";
        return await RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), ["-NoProfile", "-NonInteractive", "-Command", script], token) == 0;
    }
    public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (arguments.Count == 1 && arguments[0] == "/deploy-office")
            return await DeployAsync(executable, token).WaitAsync(token);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return await runProcess(start, token);
    }
    private async Task<int> DeployAsync(string odt, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref configureInProgress, 1, 0) != 0) throw new InvalidOperationException("Outra instalação Office ainda está em andamento.");
        try
        {
            // Freeze bytes for the pre-UAC trust check/hash. The elevated bootstrap rechecks the
            // copied bytes against this hash; parent death or source path replacement cannot bypass it.
            using var input = new FileStream(odt, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!await IsMicrosoftSignedAsync(odt, token)) throw new InvalidDataException("Assinatura Microsoft do ODT não validada.");
            var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, token));
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", BuildBootstrap(odt, hash) }) start.ArgumentList.Add(argument);
            token.ThrowIfCancellationRequested();
            // Never cancel this wait: the elevated helper owns staging and waits for the real ODT.
            return await runProcess(start, CancellationToken.None);
        }
        finally { Volatile.Write(ref configureInProgress, 0); }
    }
    public static string BuildBootstrap(string source, string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new ArgumentException("SHA-256 inválido.", nameof(sha256));
        var xml = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(OfficeDeploymentService.Configuration));
        var quotedSource = source.Replace("'", "''");
        return $$$"""
            $ErrorActionPreference='Stop'
            $env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules')
            $staging=$null
            $installerLaunched=$false
            $installerKnown=$false
            $result=60002
            try {
              $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
              if(-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Elevação necessária'}
              $admins=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
              $system=New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
              $acl=New-Object Security.AccessControl.DirectorySecurity
              $acl.SetOwner($admins)
              $acl.SetAccessRuleProtection($true,$false)
              foreach($sid in @($admins,$system)){
                $rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
                $acl.AddAccessRule($rule)
              }
              $base=[Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
              if(-not $base -or ((Get-Item -LiteralPath $base -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Base de instalação indisponível'}
              $candidate=[IO.Path]::Combine($base,'NexoOffice-'+[Guid]::NewGuid().ToString('N'))
              if(Test-Path -LiteralPath $candidate){throw 'Pasta de preparação preexistente'}
              [IO.Directory]::CreateDirectory($candidate,$acl) | Out-Null
              $actual=Get-Acl -LiteralPath $candidate
              $rules=@($actual.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
              if(-not $actual.AreAccessRulesProtected -or $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $admins.Value -or $rules.Count -ne 2){throw 'ACL da preparação inválida'}
              foreach($rule in $rules){if($rule.IdentityReference.Value -notin @($admins.Value,$system.Value) -or $rule.AccessControlType -ne 'Allow' -or $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl){throw 'Permissões da preparação inválidas'}}
              if((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Pasta de preparação redirecionada'}
              $staging=$candidate
              $odt=[IO.Path]::Combine($staging,'odt.exe')
              [IO.File]::Copy('{{{quotedSource}}}',$odt,$false)
              if((Get-FileHash -LiteralPath $odt -Algorithm SHA256).Hash -ne '{{{sha256}}}'){throw 'ODT alterado durante autorização'}
              $signature=Get-AuthenticodeSignature -LiteralPath $odt
              if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)'){throw 'Assinatura ODT inválida'}
              $extract=Start-Process -FilePath $odt -ArgumentList @('/quiet',('/extract:"'+$staging+'"')) -PassThru -Wait
              $extract.Refresh()
              if($null -eq $extract.ExitCode -or $extract.ExitCode -ne 0){throw 'Falha ao extrair ODT'}
              $setup=[IO.Path]::Combine($staging,'setup.exe')
              if((Get-Item -LiteralPath $setup -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Setup redirecionado'}
              $signature=Get-AuthenticodeSignature -LiteralPath $setup
              if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)'){throw 'Assinatura setup inválida'}
              $configuration=[IO.Path]::Combine($staging,'configuration.xml')
              [IO.File]::WriteAllBytes($configuration,[Convert]::FromBase64String('{{{xml}}}'))
              $installerLaunched=$true
              $installer=Start-Process -FilePath $setup -ArgumentList @('/configure',('"'+$configuration+'"')) -PassThru -Wait
              $installer.Refresh()
              if($null -eq $installer.ExitCode){throw 'Resultado do instalador desconhecido'}
              $result=$installer.ExitCode
              $installerKnown=$true
            } catch { $result=if($installerLaunched){60002}else{1603} }
            finally { if($staging -and (-not $installerLaunched -or $installerKnown)){Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue} }
            exit $result
            """;
    }
    private static async Task<int> RunProcessAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var process = Process.Start(start) ?? throw new IOException("Processo não iniciado.");
        // Cancelling only detaches this wait. Never kill setup, Office or user applications.
        await process.WaitForExitAsync(token); return process.ExitCode;
    }
}
