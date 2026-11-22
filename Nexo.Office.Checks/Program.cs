using Nexo.Support;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
int count=0;
void Check(bool ok,string name) { if(!ok) throw new Exception(name); Console.WriteLine("PASS: "+name); count++; }
var xml=XDocument.Parse(OfficeDeploymentService.Configuration);
Check(xml.Descendants("Product").Single().Attribute("ID")!.Value=="ProPlus2024Volume" && xml.Descendants("Language").Single().Attribute("ID")!.Value=="pt-br" && xml.Descendants("Add").Single().Attribute("Channel")!.Value=="PerpetualVL2024" && xml.Descendants("Add").Single().Attribute("OfficeClientEdition")!.Value=="64", "fixed edition/channel/locale/architecture");
Check(xml.Descendants("Display").Single().Attribute("Level")!.Value=="None", "no Office wizard clicks");
Check(!OfficeDeploymentService.Configuration.Contains("PIDKEY") && !OfficeDeploymentService.Configuration.Contains("Remove") && !OfficeDeploymentService.Configuration.Contains("FORCEAPPSHUTDOWN"), "no key/removal/forced closure");
foreach(var uri in new[]{"https://download.microsoft.com.evil/a.exe","http://download.microsoft.com/a.exe","https://user@download.microsoft.com/a.exe"}) Check(!WindowsOfficeDeployment.AllowedDownload(new(uri)),"reject "+uri);
var staging=Path.Combine(Path.GetTempPath(),"OfficeBootstrapChecks-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
try {
 var source=Path.Combine(staging,"odt.exe"); await File.WriteAllTextAsync(source,"fake ODT bytes");
 var expectedHash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
 var exited=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
 var entered=new TaskCompletionSource<ProcessStartInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
 var windows=new WindowsOfficeDeployment((start,token)=> { if(!start.UseShellExecute)return Task.FromResult(0); entered.SetResult(start); return exited.Task; });
 using var cancellation=new CancellationTokenSource();
 var deployment=windows.RunAsync(source,["/deploy-office"],cancellation.Token);
 var start=await entered.Task;
 var script=start.ArgumentList.Last();
 Check(start.FileName==Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe") && start.UseShellExecute && start.Verb=="runas" && start.ArgumentList.Contains("-Command"),"only protected OS PowerShell is elevated with inline fixed bootstrap");
 Check(script.StartsWith(PowerShellModulePath.Guard,StringComparison.Ordinal) && WindowsOfficeDeployment.BuildBootstrap(source,expectedHash).StartsWith(PowerShellModulePath.Guard,StringComparison.Ordinal),"#131: elevated bootstrap starts by restricting PSModulePath to Windows modules");
 Check(script.Contains(expectedHash) && script.Contains("Get-FileHash -LiteralPath $odt") && script.Contains("Get-AuthenticodeSignature -LiteralPath $odt") && script.Contains("Get-AuthenticodeSignature -LiteralPath $setup"),"post-UAC copies require pinned hash and Microsoft signatures");
 Check(script.Contains("[Guid]::NewGuid()") && script.Contains("CreateDirectory($candidate,$acl)") && script.Contains("SetAccessRuleProtection($true,$false)") && script.Contains("S-1-5-32-544") && script.Contains("S-1-5-18") && script.Contains("Pasta de preparação preexistente") && script.Contains("ACL da preparação inválida"),"helper creates random admin/SYSTEM staging atomically and rejects hostile ACL/path");
 Check(script.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(OfficeDeploymentService.Configuration))) && !script.Contains("ReadAllText") && !start.ArgumentList.Contains("-File"),"recipe embedded in command; no mutable script/XML read from user temp");
 Check(windows.InstallationInProgress && new WindowsOfficeDeployment().InstallationInProgress,"in-flight state shared across adapter instances");
 cancellation.Cancel(); try {await deployment;}catch(OperationCanceledException){}
 Check(windows.InstallationInProgress,"UI cancellation leaves helper in-flight");
 bool duplicateRejected=false; try {await new WindowsOfficeDeployment().RunAsync(source,["/deploy-office"],default);}catch(InvalidOperationException){duplicateRejected=true;}
 Check(duplicateRejected,"second installer cannot start while cancelled helper still runs");
 exited.SetResult(0);
 for(int i=0;i<100 && windows.InstallationInProgress;i++) await Task.Delay(10);
 Check(!windows.InstallationInProgress,"actual helper exit clears in-flight gate");
 // Parse, never execute, the exact bootstrap using Windows PowerShell 5.1.
 var parser=new ProcessStartInfo(start.FileName){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
 var payload=Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
 foreach(var arg in new[]{"-NoProfile","-NonInteractive","-Command","$t=$null;$e=$null;[Management.Automation.Language.Parser]::ParseInput([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('"+payload+"')),[ref]$t,[ref]$e)|Out-Null;if($e.Count){$e|Out-String|Write-Output;exit 1};exit 0"}) parser.ArgumentList.Add(arg);
 using var parserProcess=Process.Start(parser)!; var stdout=parserProcess.StandardOutput.ReadToEndAsync(); var stderr=parserProcess.StandardError.ReadToEndAsync(); await parserProcess.WaitForExitAsync(); if(parserProcess.ExitCode!=0) Console.WriteLine($"Parser exit {parserProcess.ExitCode}: {await stdout} {await stderr}"); Check(parserProcess.ExitCode==0,"exact elevated bootstrap parses in PowerShell 5.1 without executing it");
 // Exercise hostile-directory guards without elevation or a real ODT: use a user-owned fixture,
 // remove only the administrator precondition and force the randomly generated candidate to it.
 var hostile=Path.Combine(staging,"hostile");Directory.CreateDirectory(hostile);var marker=Path.Combine(hostile,"marker");File.WriteAllText(marker,"untouched");
 var probe=System.Text.RegularExpressions.Regex.Replace(script,@"(?m)^.*if\(-not \(New-Object Security.Principal.WindowsPrincipal.*$","");
 probe=System.Text.RegularExpressions.Regex.Replace(probe,@"(?m)^\s*\$base=\[Environment\].*$", "$base='"+staging.Replace("'","''")+"'");
 probe=System.Text.RegularExpressions.Regex.Replace(probe,@"(?m)^\s*\$candidate=\[IO.Path\]::Combine.*$", "$candidate='"+hostile.Replace("'","''")+"'");
 probe=probe.Replace("catch { $result=if($installerLaunched){60002}else{1603} }","catch { Write-Output $_.Exception.Message; $result=1603 }");
 async Task<(int Code,string Output)> RunProbe(string text) {
  var info=new ProcessStartInfo(start.FileName){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  foreach(var arg in new[]{"-NoProfile","-NonInteractive","-Command",text})info.ArgumentList.Add(arg);
  using var process=Process.Start(info)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();return(process.ExitCode,(await output)+(await error));
 }
 var existing=await RunProbe(probe);
 if(existing.Code!=1603 || !existing.Output.Contains("preexistente"))Console.WriteLine($"Existing probe {existing.Code}: {existing.Output}");
 Check(existing.Code==1603 && existing.Output.Contains("preexistente") && File.ReadAllText(marker)=="untouched","precreated hostile candidate fails closed without deleting or executing its contents");
 var aclProbe=probe.Replace("if(Test-Path -LiteralPath $candidate){throw 'Pasta de preparação preexistente'}","");
 var badAcl=await RunProbe(aclProbe);
 if(badAcl.Code!=1603 || !badAcl.Output.Contains("ACL da"))Console.WriteLine($"ACL probe {badAcl.Code}: {badAcl.Output}");
 Check(badAcl.Code==1603 && badAcl.Output.Contains("ACL da") && File.ReadAllText(marker)=="untouched","atomic-create race returning existing user-owned directory fails ACL validation");
} finally {Directory.Delete(staging,true);}
var fake=new Fake();var service=new OfficeDeploymentService(fake);
var expectedEvidence=new OfficeInstallationEvidence("ProPlus2024Volume","x64","pt-br","16.0.17932.20114",true,true);
Check(OfficeDeploymentValidation.Verify(expectedEvidence).Verified,"matching Click-to-Run evidence confirms installation");
foreach(var evidence in new OfficeInstallationEvidence?[]{null,expectedEvidence with {ProductReleaseIds="O365ProPlusRetail"},expectedEvidence with {Platform="x86"},expectedEvidence with {ClientCulture="en-us"},expectedEvidence with {VersionToReport="16.0.10000.1"},expectedEvidence with {ApplicationFilesPresent=false}})
 Check(!OfficeDeploymentValidation.Verify(evidence).Verified,"missing or mismatched Click-to-Run evidence never confirms success");
Check(OfficeDeploymentValidation.Verify(expectedEvidence with {StreamingFinished=false}).Verified,"ODT exit code zero plus registry and app files can confirm Office without undocumented StreamingFinished value");
Check(OfficeDeploymentValidation.CanResume("ProPlus2024Volume","x64","pt-br","") &&
      !OfficeDeploymentValidation.CanResume("ProPlus2024Volume","x64","pt-br","True") &&
      !OfficeDeploymentValidation.CanResume("ProPlus2024Volume,VisioPro2024Volume","x64","pt-br","") &&
      !OfficeDeploymentValidation.CanResume("O365ProPlusRetail","x64","pt-br","") &&
      !OfficeDeploymentValidation.CanResume("ProPlus2024Volume","x86","pt-br","") &&
      !OfficeDeploymentValidation.CanResume("ProPlus2024Volume","x64","en-us",""),"resume only the matching unfinished Office edition");
Check(OfficeDeploymentValidation.ResourceBlockers(0,0,false).Count==3,"space and network requirements block before download");
Check(OfficeDeploymentValidation.ResourceBlockers(OfficeDeploymentValidation.RequiredInstallationBytes,OfficeDeploymentValidation.RequiredTemporaryBytes,true).Count==0,"resource thresholds accept sufficient space and active network");
Check(OfficeDeploymentValidation.Conflict("O365ProPlusRetail","x86").Contains("x86") && OfficeDeploymentValidation.Conflict("O365ProPlusRetail","x86").Contains("O365ProPlusRetail"),"conflict identifies existing edition and architecture");
Check((await service.InstallAsync(false)).State==OfficeDeploymentState.Declined && fake.Calls.Count==0,"consent before IO");
fake.InstallationInProgress=true;
Check((await service.InstallAsync(true)).State==OfficeDeploymentState.Blocked && fake.Calls.Count==0,"running installer blocks service retry");fake.InstallationInProgress=false;
fake.Preflight=new(false,["Office existente"]);
Check((await service.InstallAsync(true)).State==OfficeDeploymentState.Blocked && fake.Calls.Count==0,"conflict before download");fake.Preflight=new(true,[]);
fake.Signature=false;
Check((await service.InstallAsync(true)).State==OfficeDeploymentState.Failed && !fake.Calls.Contains("deploy"),"unsigned download never elevated");fake.Signature=true;
fake.BlockOnRecheck=true;fake.Checks=0;fake.Calls.Clear();
Check((await service.InstallAsync(true)).State==OfficeDeploymentState.Blocked && !fake.Calls.Contains("deploy"),"conflict rechecked immediately before elevation");fake.BlockOnRecheck=false;fake.Calls.Clear();
Check((await service.InstallAsync(true)).State==OfficeDeploymentState.Completed && string.Join(",",fake.Calls)=="download,signature,deploy","single protected bootstrap owns extraction and configure");
Check(!Directory.Exists(fake.Folder),"known success cleans caller staging");
fake.Verification=new(false,"Click-to-Run ausente");
Check((await service.InstallAsync(true)).State==OfficeDeploymentState.Unknown,"installer exit zero without Windows confirmation is unknown");fake.Verification=new(true,"Office confirmado");
fake.VerificationError=new OperationCanceledException();Check((await service.InstallAsync(true)).State==OfficeDeploymentState.Unknown,"cancelled post-install check never becomes pre-install cancellation");fake.VerificationError=new UnauthorizedAccessException();Check((await service.InstallAsync(true)).State==OfficeDeploymentState.Unknown,"unreadable Windows installation evidence never claims success");fake.VerificationError=null;
foreach(var failure in new (Exception Error,string Message)[]{(new System.Net.Http.HttpRequestException("offline"),"rede"),(new UnauthorizedAccessException(),"Permissão"),(new IOException("full",unchecked((int)0x80070070)),"Espaço")}){
 fake.DownloadError=failure.Error;var failed=await service.InstallAsync(true);Check(failed.State==OfficeDeploymentState.Failed && failed.Message.Contains(failure.Message),"preparation error explains "+failure.Message);
}fake.DownloadError=null;
fake.Exit=3010;Check((await service.InstallAsync(true)).State==OfficeDeploymentState.RestartRequired && !Directory.Exists(fake.Folder),"known restart result cleans staging without reboot");
fake.Exit=1603;Check((await service.InstallAsync(true)).State==OfficeDeploymentState.Failed && !Directory.Exists(fake.Folder),"known helper failure cleans staging");
fake.Exit=60002;Check((await service.InstallAsync(true)).State==OfficeDeploymentState.Unknown,"helper without installer exit proof never reports completion");fake.Exit=0;
fake.Refuse=true;Check((await service.InstallAsync(true)).State==OfficeDeploymentState.Declined && !Directory.Exists(fake.Folder),"refused UAC cleans staging and never claims installation");fake.Refuse=false;
fake.Cancel=true;Check((await service.InstallAsync(true)).State==OfficeDeploymentState.Unknown && Directory.Exists(fake.Folder),"cancel after dispatch is unknown; preserve source while helper may need it");fake.Cancel=false;
using var cancelled=new CancellationTokenSource();cancelled.Cancel();fake.Calls.Clear();
Check((await service.InstallAsync(true,cancellationToken:cancelled.Token)).State==OfficeDeploymentState.Cancelled && fake.Calls.Count==0,"cancel before dispatch has no effects");
Console.WriteLine($"{count} Office checks passed");
sealed class Fake:IOfficeDeploymentPlatform {
 public bool InstallationInProgress{get;set;} public OfficePreflight Preflight=new(true,[]);public bool Signature=true,BlockOnRecheck,Refuse,Cancel;public int Checks,Exit;public string Folder="";public List<string> Calls=[];
 public OfficeInstallationVerification Verification=new(true,"Office confirmado");
 public Exception? VerificationError,DownloadError;
 public Task<OfficeInstallationVerification> VerifyInstallationAsync(CancellationToken token)=>VerificationError==null?Task.FromResult(Verification):Task.FromException<OfficeInstallationVerification>(VerificationError);
 public Task<OfficePreflight> CheckAsync(CancellationToken t)=>Task.FromResult(BlockOnRecheck && ++Checks>1?new OfficePreflight(false,["Office aberto"]):Preflight);
 public Task DownloadAsync(string p,CancellationToken t){Folder=Path.GetDirectoryName(p)!;Calls.Add("download");return DownloadError==null?Task.CompletedTask:Task.FromException(DownloadError);}
 public Task<bool> IsMicrosoftSignedAsync(string p,CancellationToken t){Calls.Add("signature");return Task.FromResult(Signature);}
 public Task<int> RunAsync(string p,IReadOnlyList<string>a,CancellationToken t){Calls.Add("deploy");if(a.Count!=1||a[0]!="/deploy-office")throw new Exception("Unclosed recipe");if(Refuse)throw new System.ComponentModel.Win32Exception(1223);if(Cancel)throw new OperationCanceledException();return Task.FromResult(Exit);}
}
