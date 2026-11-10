$ErrorActionPreference='Stop'
# All networking cmdlets are replaced here. This suite never invokes real network mutations.
function Get-NetAdapter { param($InterfaceIndex) [pscustomobject]@{Name=$script:adapterName;InterfaceDescription=$script:adapterDescription;InterfaceGuid='{00000000-0000-0000-0000-000000000001}';Virtual=$script:virtual;HardwareInterface=(-not $script:virtual)} }
function Get-NetIPInterface { param($InterfaceIndex,$AddressFamily) [pscustomobject]@{Dhcp=$script:state.Dhcp} }
function Get-CimInstance { param($ClassName,$Filter,$ErrorAction) if($ClassName -ne 'Win32_NetworkAdapterConfiguration' -or $Filter -ne 'InterfaceIndex=10'){throw 'Wrong DHCP target'}; [pscustomobject]@{SettingID='{00000000-0000-0000-0000-000000000001}'} }
function Invoke-CimMethod { throw 'Legacy renewal must not run after removing the last IP (Windows error 84)' }
# Every simulated mutation requires the protected copy to exist first (#10): no network change before it.
function Assert-Backed { if($script:currentProtected -and -not (Test-Path -LiteralPath (Join-Path $script:currentProtected 'before.json'))){throw 'Network changed before the protected backup was written'}; Note-Phase }
# Phase in the protected record (#10), noted at every network mutation and every read of decision.txt.
function Note-Phase {
    if(-not $script:currentProtected){return}
    $record=Join-Path $script:currentProtected 'transaction.json'
    if(-not (Test-Path -LiteralPath $record)){return}
    $phase=[string](Microsoft.PowerShell.Management\Get-Content -LiteralPath $record -Raw | ConvertFrom-Json).Phase
    if($phase -and ($script:phases.Count -eq 0 -or $script:phases[-1] -ne $phase)){$script:phases+=@($phase)}
}
function Get-Content { param([string]$LiteralPath,[switch]$Raw) if($LiteralPath -like '*decision.txt'){Note-Phase}; Microsoft.PowerShell.Management\Get-Content -LiteralPath $LiteralPath -Raw:$Raw }
# Simulated protected store: the real ACL primitives are renamed in the code under test; Use-ProtectedDirectory and
# Open-ProtectedDir keep their real logic. Set-Acl must never be called by the executor.
function New-ProtectedDirectory($path) { if(Test-Path -LiteralPath $path){throw ('Created over an existing folder: '+$path)}; New-Item -ItemType Directory -Path $path | Out-Null; $script:created+=@($path) }
# The simulated Assert-Trusted runs the executor's real decision (Get-UntrustedReason) on a simulated descriptor: by
# default the protected one; a scenario can give a path a Users:Modify ACE or another owner.
function Assert-Trusted($path) {
    if(-not (Test-Path -LiteralPath $path)){throw ('Protected item missing: '+$path)}
    $descriptor=$(if($script:fakeAcl.ContainsKey($path)){$script:fakeAcl[$path]}elseif($script:untrusted){UsersModify}else{ProtectedDescriptor})
    $reason=Get-UntrustedReason $path (Test-Path -LiteralPath $path -PathType Container) $descriptor.Owner $descriptor.Protected $descriptor.Rules
    if($reason){throw ('Simulated protected copy not used: '+$reason)}
}
function ProtectedDescriptor { @{Owner='S-1-5-32-544';Protected=$true;Rules=@([pscustomobject]@{Sid='S-1-5-18';Rights=0x1F01FF;Type='Allow';Propagation=0},[pscustomobject]@{Sid='S-1-5-32-544';Rights=0x1F01FF;Type='Allow';Propagation=0},[pscustomobject]@{Sid='S-1-5-32-545';Rights=0x1200A9;Type='Allow';Propagation=0})} }
function UsersModify { $d=ProtectedDescriptor; $d.Rules+=@([pscustomobject]@{Sid='S-1-5-32-545';Rights=0x1301BF;Type='Allow';Propagation=0}); $d }
function Set-Acl { $script:aclWrites++ }
function Write-ProtectedFile($path, $text) { Set-Content -LiteralPath $path -Value $text; $script:protectedWrites+=@(Split-Path -Leaf $path); if($script:leaveTmp){Set-Content -LiteralPath ($path+'.tmp') -Value 'interrupted'} }
function Invoke-TestNetsh {
    Assert-Backed
    if (($args -join ' ') -ne 'interface ipv4 set address name=10 source=dhcp store=persistent') { throw 'Unexpected native arguments' }
    if ($script:transition84 -and $script:state.Addresses.Count -eq 0) { throw 'Last IP was removed before DHCP transition' }
    $script:transitionCalled=$true
    if($script:nativeError){$global:LASTEXITCODE=5; 'Access denied';return}
    $global:LASTEXITCODE=0
    if($script:modeUnchanged){return}
    $script:state.Dhcp='Enabled'; $script:state.Routes=@()
    $script:state.Addresses=$(if($script:noLease -or $script:leaseDelay -gt 0){@()}else{@([pscustomobject]@{IPAddress='10.0.0.25';PrefixLength=24;PrefixOrigin=3;AddressState=4})})
}
function Get-NetIPAddress {
    param($InterfaceIndex,$AddressFamily,$ErrorAction)
    if($script:state.Dhcp -eq 'Enabled' -and $script:leaseDelay -gt 0){$script:leaseDelay--; if($script:leaseDelay -eq 0){$script:state.Addresses=@([pscustomobject]@{IPAddress='10.0.0.25';PrefixLength=24;PrefixOrigin='Dhcp';AddressState='Preferred'})}}
    if($InterfaceIndex -and -not $script:state.Addresses.Count){throw 'Nenhum objeto MSFT_NetIPAddress correspondente encontrado'}
    foreach($ip in $script:state.Addresses){$ip | Add-Member -NotePropertyName InterfaceIndex -NotePropertyValue 10 -Force; $ip}
}
function Get-NetRoute { param($InterfaceIndex,$AddressFamily,$ErrorAction) foreach($route in $script:state.Routes){$route | Add-Member -NotePropertyName InterfaceIndex -NotePropertyValue 10 -Force; $route} }
function Get-ItemProperty { param($LiteralPath,$ErrorAction) [pscustomobject]@{NameServer=$script:state.StaticDns} }
function Get-DnsClientServerAddress { param($InterfaceIndex,$AddressFamily) [pscustomobject]@{ServerAddresses=$script:state.Dns} }
function Set-DnsClientServerAddress {
    param([Parameter(ValueFromPipeline=$true)]$InputObject,$ServerAddresses,[switch]$ResetServerAddresses)
    process {
        Assert-Backed
        if ($script:failOnce) {$script:failOnce=$false; throw 'Simulated apply failure'}
        if($ResetServerAddresses){$script:state.Dns=@('10.0.0.1');$script:state.StaticDns=''}
        else{$script:state.Dns=@($ServerAddresses);$script:state.StaticDns=@($ServerAddresses) -join ','}
    }
}
function Set-NetIPInterface { param($InterfaceIndex,$AddressFamily,$Dhcp) Assert-Backed; $script:state.Dhcp=$Dhcp; if($Dhcp -eq 'Enabled'){if($script:noLease -or $script:renewOnly -or $script:leaseDelay -gt 0){$script:state.Addresses=@()}else{$script:state.Addresses=@([pscustomobject]@{IPAddress='10.0.0.25';PrefixLength=24;PrefixOrigin=3;AddressState=4})}} }
function Remove-NetRoute { param([Parameter(ValueFromPipeline=$true)]$InputObject,$Confirm) process {Assert-Backed; $script:state.Routes=@()} }
function Remove-NetIPAddress { param([Parameter(ValueFromPipeline=$true)]$InputObject,$Confirm) process {Assert-Backed; $script:state.Addresses=@($script:state.Addresses | Where-Object {$_.IPAddress -ne $InputObject.IPAddress})} }
function New-NetIPAddress { param($InterfaceIndex,$IPAddress,$PrefixLength,$DefaultGateway) Assert-Backed; $script:state.Addresses+= [pscustomobject]@{IPAddress=$IPAddress;PrefixLength=$PrefixLength;PrefixOrigin=1;AddressState=4}; if($DefaultGateway){$script:state.Routes=@([pscustomobject]@{DestinationPrefix='0.0.0.0/0';NextHop=$DefaultGateway;RouteMetric=256})} }
function New-NetRoute { param($InterfaceIndex,$DestinationPrefix,$NextHop,$RouteMetric) $script:state.Routes+=[pscustomobject]@{DestinationPrefix=$DestinationPrefix;NextHop=$NextHop;RouteMetric=$RouteMetric} }
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('NexoNetworkTests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Nexo.Support\NetworkTransaction.ps1') -Raw
$scriptBlock=[scriptblock]::Create($source)
$productionRoot="([Environment]::GetFolderPath('CommonApplicationData'))"
# The protected root comes from the Windows API, never from an environment variable the caller could change, and the
# executor never rewrites an ACL.
# The executor's ACE decision, loaded once so the simulated Assert-Trusted and the unit cases below use the real code.
$decisionAst=[System.Management.Automation.Language.Parser]::ParseInput($source,[ref]$null,[ref]$null)
. ([scriptblock]::Create((@(($source -split "`r?`n") | Where-Object {$_ -match '^\$trustedSids = '}) + $decisionAst.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-UntrustedReason'},$true).Extent.Text) -join "`n"))
function Rule($sid,$rights,$type='Allow',$propagation=0) { [pscustomobject]@{Sid=$sid;Rights=[int64]$rights;Type=$type;Propagation=$propagation} }
$base=(ProtectedDescriptor).Rules
$cases=@(
    @('protected file',$false,'S-1-5-32-544',$false,$base,$false),
    @('protected folder',$true,'S-1-5-32-544',$true,$base,$false),
    @('Users Modify on a file',$false,'S-1-5-32-544',$false,($base+@(Rule 'S-1-5-32-545' 0x1301BF)),$true),
    @('Users FullControl on a folder',$true,'S-1-5-32-544',$true,($base+@(Rule 'S-1-5-32-545' 0x1F01FF)),$true),
    @('Everyone GENERIC_WRITE',$false,'S-1-5-32-544',$false,($base+@(Rule 'S-1-1-0' 0x40000000)),$true),
    @('Authenticated Users Delete',$false,'S-1-5-32-544',$false,($base+@(Rule 'S-1-5-11' 0x10000)),$true),
    @('Users ChangePermissions',$false,'S-1-5-32-544',$false,($base+@(Rule 'S-1-5-32-545' 0x40000)),$true),
    @('Users AppendData only',$false,'S-1-5-32-544',$false,($base+@(Rule 'S-1-5-32-545' 0x4)),$true),
    @('Users Modify inherit-only',$true,'S-1-5-32-544',$true,($base+@(Rule 'S-1-5-32-545' 0x1301BF 'Allow' 2)),$false),
    @('Users Modify denied',$false,'S-1-5-32-544',$false,($base+@(Rule 'S-1-5-32-545' 0x1301BF 'Deny')),$false),
    @('TrustedInstaller FullControl',$false,'S-1-5-32-544',$false,($base+@(Rule 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464' 0x1F01FF)),$false),
    @('user as owner',$false,'S-1-5-21-1-2-3-1001',$false,$base,$true),
    @('inheriting folder',$true,'S-1-5-32-544',$false,$base,$true)
)
foreach($case in $cases){
    $reason=Get-UntrustedReason ('C:\case\'+$case[0]) $case[1] $case[2] $case[3] $case[4]
    if([bool]$reason -ne $case[5]){throw ('ACE decision wrong for '+$case[0]+': '+$reason)}
    if($case[5] -and $case[0] -like '*Modify on a file' -and ($reason -notmatch 'S-1-5-32-545' -or $reason -notmatch '0x1301BF')){throw ('Refusal reason lacks SID or rights: '+$reason)}
}
Write-Output 'PASS: executor ACE decision refuses write rights for non-admin SIDs on files and folders, with SID and rights in the reason'
foreach($forbidden in '$env:ProgramData','Set-Acl'){ if($source.Contains($forbidden)){throw ('NetworkTransaction.ps1 must not use '+$forbidden)} }
Write-Output 'PASS: executor uses the Windows API for the protected root and never calls Set-Acl'
# Protected files are born with the explicit descriptor: only Save-Result (the %LOCALAPPDATA% folder) uses Set-Content.
$parsed=[System.Management.Automation.Language.Parser]::ParseInput($source,[ref]$null,[ref]$null)
function FunctionText($name) { $parsed.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true).Extent.Text }
$saveProtected=FunctionText 'Save-Protected'; $writeProtected=FunctionText 'Write-ProtectedFile'; $saveResult=FunctionText 'Save-Result'
$setContent=[regex]::Matches($source,'Set-Content|Out-File|WriteAllText|WriteAllBytes').Count
if(-not $saveProtected.Contains('Write-ProtectedFile') -or $saveProtected -match 'Set-Content|Out-File|WriteAll' -or -not $writeProtected.Contains('FileSecurity') -or -not $writeProtected.Contains('FileStream($temporary, [IO.FileMode]::CreateNew, $rights, [IO.FileShare]::None, 4096, [IO.FileOptions]::None, $security)') -or -not $writeProtected.Contains('Create((New-Object System.IO.FileInfo($temporary)), [IO.FileMode]::CreateNew, $rights, [IO.FileShare]::None, 4096, [IO.FileOptions]::None, $security)') -or $writeProtected -match 'Set-Content|Out-File|WriteAll' -or $setContent -ne [regex]::Matches($saveResult,'Set-Content').Count){throw 'Protected files must be created only by Write-ProtectedFile with an explicit FileSecurity'}
Write-Output 'PASS: protected files are created with an explicit descriptor, never with Set-Content'
# Each scenario gets its own simulated %ProgramData%.
function UseProgramData($name) { $script:programData=Join-Path $testRoot ('pd-'+$name); $script:protectedTestRoot=Join-Path $script:programData 'NexoSupport\operations'; $script:created=@(); $script:untrustedPaths=@(); $script:aclWrites=0; $script:phases=@(); $script:protectedWrites=@(); $script:fakeAcl=@{} }
function Isolated($text) {
    foreach($piece in @($productionRoot,'function New-ProtectedDirectory(','function Assert-Trusted(','function Write-ProtectedFile(')){ if(-not $text.Contains($piece)){throw ('Protected-store hook not found: '+$piece)} }
    $text.Replace($productionRoot,"'"+$script:programData.Replace("'","''")+"'").Replace('function New-ProtectedDirectory(','function New-ProtectedDirectory-Windows(').Replace('function Assert-Trusted(','function Assert-Trusted-Windows(').Replace('function Write-ProtectedFile(','function Write-ProtectedFile-Windows(')
}
function Check($name,$action,$mode,$decision,$failure=$false,$variant='normal') {
    $script:state=@{Dhcp='Disabled';StaticDns='10.0.0.2';Dns=@('10.0.0.2');Addresses=@([pscustomobject]@{IPAddress='10.0.0.10';PrefixLength=24;PrefixOrigin=1;AddressState=4});Routes=@([pscustomobject]@{DestinationPrefix='0.0.0.0/0';NextHop='10.0.0.1';RouteMetric=256})}
    $script:failOnce=$failure
    $script:noLease=$variant -eq 'no-lease'; $script:leaseDelay=$(if($variant -eq 'delayed-lease'){3}else{0})
    $script:renewOnly=$false; $script:transition84=$variant -eq 'error-84'; $script:transitionCalled=$false
    $script:nativeError=$variant -eq 'native-error'; $script:modeUnchanged=$variant -eq 'mode-unchanged'
    $script:virtual=$variant -in 'hyperv','default-switch','vpn'
    $script:adapterName=$(if($variant -eq 'default-switch'){'vEthernet (Default Switch)'}else{'Adapter-Test'})
    $script:adapterDescription=$(if($variant -in 'hyperv','default-switch'){'Hyper-V Virtual Ethernet Adapter #2'}elseif($variant -eq 'vpn'){'VPN Adapter'}else{'Realtek Ethernet'})
    $operationId=$(if($variant -eq 'bad-id'){'NAO-E-GUID'}else{[guid]::NewGuid().ToString('N')})
    $operationDir=Join-Path $testRoot $(if($variant -eq 'bad-id'){'NAO-E-GUID'}else{$operationId}); New-Item -ItemType Directory -Path $operationDir | Out-Null
    UseProgramData $name
    $script:currentProtected=Join-Path $script:protectedTestRoot $operationId; $script:untrusted=$false
    # A write interrupted before the rename leaves a .tmp: the closed removal list still empties the folder.
    $script:leaveTmp=$variant -eq 'leftover-tmp'
    $nexoRoot=Split-Path -Parent $script:protectedTestRoot
    # A folder that already exists is used only if it is already protected; it is never repaired or adopted.
    if($variant -eq 'protected-exists'){New-Item -ItemType Directory -Path $script:currentProtected -Force | Out-Null; Set-Content -LiteralPath (Join-Path $script:currentProtected 'before.json') -Value '{"forjado":true}'; $script:fakeAcl[$script:currentProtected]=UsersModify}
    if($variant -eq 'existing-untrusted-root'){New-Item -ItemType Directory -Path $nexoRoot -Force | Out-Null; $d=ProtectedDescriptor; $d.Owner='S-1-5-21-1-2-3-1001'; $script:fakeAcl[$nexoRoot]=$d}
    if($variant -eq 'existing-trusted-root'){New-Item -ItemType Directory -Path $script:protectedTestRoot -Force | Out-Null}
    if($decision -ne 'timeout'){Set-Content -LiteralPath (Join-Path $operationDir 'decision.txt') -Value $decision}
    $requestJson=@{Action=$action;Index=10;ExpectedGuid=$(if($variant -eq 'identity-changed'){'00000000-0000-0000-0000-000000000099'}else{'00000000-0000-0000-0000-000000000001'});Mode=$mode;Servers=@('1.1.1.1');Address='10.0.0.20';Prefix=24;Gateway='10.0.0.1'} | ConvertTo-Json
    $testSource=(Isolated $source).Replace('"$env:WINDIR\System32\netsh.exe"','Invoke-TestNetsh').Replace('.AddSeconds(60)', '.AddMilliseconds(500)').Replace('Start-Sleep -Milliseconds 500','Start-Sleep -Milliseconds 10').Replace('Start-Sleep -Seconds 3','Start-Sleep -Milliseconds 1')
    if($testSource.Contains('netsh.exe')){throw 'Native command was not isolated by the simulator'}
    & ([scriptblock]::Create($testSource))
    $result=Get-Content -LiteralPath (Join-Path $operationDir 'result.json') -Raw | ConvertFrom-Json
    if($script:aclWrites -ne 0){throw ($name+': the executor rewrote an ACL')}
    # Confirmation, cancellation and failure with restore: the protected phase follows each step.
    $expectedPhases=@{'dns-keep'='applying,confirming';'static-keep'='applying,confirming';'dns-rollback'='applying,confirming,restoring';'static-rollback'='applying,confirming,restoring';'dns-error-rollback'='applying,restoring'}
    if($expectedPhases.ContainsKey($name) -and (@($script:phases) -join ',') -ne $expectedPhases[$name]){throw ($name+': protected phases were '+(@($script:phases) -join ','))}
    if($variant -in 'default-switch','vpn','identity-changed','protected-exists','bad-id','existing-untrusted-root') {
        if($result.success -or (Test-Path (Join-Path $operationDir 'before.json')) -or $script:state.Addresses[0].IPAddress -ne '10.0.0.10' -or $script:state.Dns[0] -ne '10.0.0.2'){throw ($name+': disallowed target was changed')}
        if($variant -eq 'protected-exists' -and ((Get-Content -LiteralPath (Join-Path $script:currentProtected 'before.json') -Raw) -notmatch 'forjado' -or $result.error -notmatch 'not used')){throw ($name+': untrusted pre-existing protected folder was used or removed')}
        if($variant -eq 'existing-untrusted-root' -and ($result.error -notmatch 'not used' -or $script:created.Count -ne 0 -or (Test-Path -LiteralPath $script:protectedTestRoot))){throw ($name+': untrusted pre-existing NexoSupport folder was adopted')}
        if($variant -eq 'bad-id' -and $result.error -notmatch 'Identificador'){throw ($name+': invalid operation id was accepted')}
    } elseif($decision -eq 'keep' -and -not $failure -and -not $script:noLease -and -not $script:nativeError -and -not $script:modeUnchanged) {
        if(-not $result.success){throw ($name+': '+($result | ConvertTo-Json -Depth 5))}
        if($action -eq 'set_dns' -and $script:state.Dns[0] -ne '1.1.1.1'){throw 'DNS was not applied'}
        if($action -eq 'set_ipv4' -and $mode -eq 'static' -and $script:state.Addresses[0].IPAddress -ne '10.0.0.20'){throw 'Static IP was not applied'}
        if($action -eq 'set_ipv4' -and $mode -eq 'dhcp' -and ($script:state.Addresses[0].IPAddress -ne '10.0.0.25' -or $script:state.Dns[0] -ne '10.0.0.2')){throw 'DHCP lease or preserved DNS not verified'}
    } else {
        if($result.success -or -not $result.reverted){throw ($name+': rollback not reported: '+($result | ConvertTo-Json -Depth 5))}
        if($script:state.Dns[0] -ne '10.0.0.2' -or $script:state.Addresses[0].IPAddress -ne '10.0.0.10'){throw ($name+': prior state not restored')}
    }
    if($variant -notin 'default-switch','vpn','identity-changed','protected-exists','bad-id','existing-untrusted-root') {
        if(Test-Path -LiteralPath $script:currentProtected){throw ($name+': protected copy was left behind after the result')}
        $expected=$(if($variant -eq 'existing-trusted-root'){@($script:currentProtected)}else{@($nexoRoot,$script:protectedTestRoot,$script:currentProtected)})
        if((@($script:created) -join '|') -ne ($expected -join '|')){throw ($name+': protected folders created: '+(@($script:created) -join ', '))}
        if(@($script:protectedWrites) -notcontains 'transaction.json' -or @($script:protectedWrites) -notcontains 'before.json'){throw ($name+': protected files not written by Write-ProtectedFile: '+(@($script:protectedWrites) -join ', '))}
    }
    Write-Output ('PASS: simulated network transaction '+$name)
    if($variant -eq 'error-84' -and -not $script:transitionCalled){throw 'Static-to-DHCP transition was not requested'}
    if($variant -in 'native-error','mode-unchanged','no-lease' -and -not $result.failureState){throw 'Pre-rollback evidence was lost'}
    if($variant -eq 'native-error' -and $result.dhcpTransition.exitCode -ne 5){throw 'Native error code was lost'}
}
function StaticState($ip,$dns) { @{Dhcp='Disabled';StaticDns=$dns;Dns=@($dns);Addresses=@([pscustomobject]@{IPAddress=$ip;PrefixLength=24;PrefixOrigin=1;AddressState=4});Routes=@([pscustomobject]@{DestinationPrefix='0.0.0.0/0';NextHop='10.0.0.1';RouteMetric=256})} }
function Recover($name,$action,$current,$expect,$variant='normal') {
    $script:state=$current
    $script:failOnce=$variant -eq 'restore-fails'
    $script:noLease=$false; $script:leaseDelay=0; $script:renewOnly=$false; $script:transition84=$false; $script:transitionCalled=$false
    $script:nativeError=$false; $script:modeUnchanged=$false; $script:virtual=$false; $script:adapterName='Adapter-Test'; $script:adapterDescription='Realtek Ethernet'
    $guid=$(if($variant -eq 'identity-changed'){'{00000000-0000-0000-0000-000000000099}'}else{'{00000000-0000-0000-0000-000000000001}'})
    $dhcpBefore=$variant -eq 'dhcp-before'
    $beforeJson=[pscustomobject]@{Index=10;Name='Adapter-Test';Guid=$guid;Dhcp=$(if($dhcpBefore){'Enabled'}else{'Disabled'});StaticDns=$(if($dhcpBefore){''}else{'10.0.0.2'});Dns=@($(if($dhcpBefore){'10.0.0.1'}else{'10.0.0.2'}));Addresses=$(if($dhcpBefore){@([pscustomobject]@{IPAddress='10.0.0.25';PrefixLength=24;PrefixOrigin='3';AddressState='4'})}else{@([pscustomobject]@{IPAddress='10.0.0.10';PrefixLength=24;PrefixOrigin='1';AddressState='4'})});Routes=$(if($dhcpBefore){@()}else{@([pscustomobject]@{NextHop='10.0.0.1';RouteMetric=256})})} | ConvertTo-Json -Depth 5
    $operationId=[guid]::NewGuid().ToString('N'); $recoveryMode=$true
    $operationDir=Join-Path $testRoot $operationId; New-Item -ItemType Directory -Path $operationDir | Out-Null
    UseProgramData $name
    $script:currentProtected=Join-Path $script:protectedTestRoot $operationId; $script:untrusted=$variant -eq 'untrusted'
    if($variant -ne 'missing-protected'){
        New-Item -ItemType Directory -Path $script:currentProtected -Force | Out-Null
        @{Action=$action;Index=10;ExpectedGuid=$guid.Trim('{}');Created=(Get-Date).ToUniversalTime().ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $script:currentProtected 'transaction.json')
        $beforeJson | Set-Content -LiteralPath (Join-Path $script:currentProtected 'before.json')
    }
    # The %LOCALAPPDATA% backup is ignored: forged content there must never be applied.
    ($beforeJson -replace '10\.0\.0\.2','203.0.113.66') | Set-Content -LiteralPath (Join-Path $operationDir 'before.json')
    $requestAction=$(if($variant -eq 'request-mismatch'){if($action -eq 'set_dns'){'set_ipv4'}else{'set_dns'}}else{$action})
    $requestJson=@{Action=$requestAction;Index=10;ExpectedGuid=$guid.Trim('{}');Mode='';Servers=@();Address='';Prefix=0;Gateway=''} | ConvertTo-Json
    $testSource=(Isolated $source).Replace('"$env:WINDIR\System32\netsh.exe"','Invoke-TestNetsh')
    & ([scriptblock]::Create($testSource))
    if(Test-Path (Join-Path $operationDir 'result.json')){throw ($name+': recovery ran the normal transaction')}
    # The elevated recovery writes its result only to the protected folder; the app copies it without elevation.
    if(Test-Path (Join-Path $operationDir 'recovery-result.json')){throw ($name+': elevated recovery wrote to the user folder')}
    $resultPath=Join-Path $script:currentProtected 'recovery-result.json'
    if($variant -in 'untrusted','missing-protected') {
        if(Test-Path -LiteralPath $resultPath){throw ($name+': result written without a trusted protected copy')}
        $result=$null; $expect='silent'
    } else {
        $result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if(@($script:protectedWrites) -notcontains 'recovery-result.json'){throw ($name+': recovery result not written by Write-ProtectedFile')}
    }
    switch($expect) {
        'restored' { if(-not $result.success -or -not $result.restored -or -not $result.found -or -not $result.after){throw ($name+': restore not reported: '+($result | ConvertTo-Json -Depth 5))} }
        'already' { if(-not $result.success -or $result.restored -or -not $result.alreadyRestored){throw ($name+': matching state was not detected')} }
        'failed' { if($result.success -or $result.restored -or -not $result.error -or -not $result.state){throw ($name+': failure or uncertainty not recorded')} }
    }
    if($variant -eq 'dhcp-before') { if($script:state.Dhcp -ne 'Enabled' -or -not $script:transitionCalled){throw ($name+': DHCP was not restored')} }
    elseif($variant -in 'identity-changed','untrusted','request-mismatch','missing-protected') { if($script:state.Addresses[0].IPAddress -ne '10.0.0.20' -or $script:state.Dns[0] -ne '1.1.1.1'){throw ($name+': network was changed without a trusted protected backup')} }
    elseif($expect -notin 'failed','silent') { if($script:state.Addresses[0].IPAddress -ne '10.0.0.10' -or $script:state.Dns[0] -ne '10.0.0.2'){throw ($name+': prior state not present after recovery')} }
    if(@($script:state.Dns) -contains '203.0.113.66'){throw ($name+': user-writable backup was applied')}
    if($variant -eq 'untrusted' -and -not (Test-Path -LiteralPath (Join-Path $script:currentProtected 'before.json'))){throw ($name+': untrusted protected folder was deleted with elevation')}
    if($script:aclWrites -ne 0){throw ($name+': the executor rewrote an ACL')}
    $recoveryMode=$false
    Write-Output ('PASS: simulated network recovery '+$name)
}
try {
    Recover 'recovery-static-after-crash' 'set_ipv4' (StaticState '10.0.0.20' '1.1.1.1') 'restored'
    Recover 'recovery-dns-after-crash' 'set_dns' (StaticState '10.0.0.10' '1.1.1.1') 'restored'
    Recover 'recovery-already-matching' 'set_ipv4' (StaticState '10.0.0.10' '10.0.0.2') 'already' 'restore-fails'
    Recover 'recovery-dhcp-after-crash' 'set_ipv4' (StaticState '10.0.0.20' '1.1.1.1') 'restored' 'dhcp-before'
    Recover 'recovery-identity-changed' 'set_ipv4' (StaticState '10.0.0.20' '1.1.1.1') 'failed' 'identity-changed'
    Recover 'recovery-restore-fails' 'set_dns' (StaticState '10.0.0.10' '1.1.1.1') 'failed' 'restore-fails'
    Recover 'recovery-untrusted-copy' 'set_ipv4' (StaticState '10.0.0.20' '1.1.1.1') 'silent' 'untrusted'
    Recover 'recovery-request-mismatch' 'set_ipv4' (StaticState '10.0.0.20' '1.1.1.1') 'failed' 'request-mismatch'
    Recover 'recovery-missing-protected' 'set_ipv4' (StaticState '10.0.0.20' '1.1.1.1') 'silent' 'missing-protected'
    Check 'dns-keep' 'set_dns' 'manual' 'keep'
    Check 'dns-rollback' 'set_dns' 'manual' 'rollback'
    Check 'dns-error-rollback' 'set_dns' 'manual' 'keep' $true
    Check 'static-keep' 'set_ipv4' 'static' 'keep'
    Check 'static-rollback' 'set_ipv4' 'static' 'rollback'
    Check 'dhcp-rollback' 'set_ipv4' 'dhcp' 'rollback'
    Check 'timeout-rollback' 'set_dns' 'manual' 'timeout'
    Check 'dhcp-empty-then-lease' 'set_ipv4' 'dhcp' 'keep' $false 'delayed-lease'
    Check 'dhcp-last-address-error84' 'set_ipv4' 'dhcp' 'keep' $false 'error-84'
    Check 'dhcp-native-command-rejected' 'set_ipv4' 'dhcp' 'keep' $false 'native-error'
    Check 'dhcp-command-success-mode-unchanged' 'set_ipv4' 'dhcp' 'keep' $false 'mode-unchanged'
    Check 'dhcp-unavailable-restores-from-empty' 'set_ipv4' 'dhcp' 'keep' $false 'no-lease'
    Check 'hyperv-dhcp-keep' 'set_ipv4' 'dhcp' 'keep' $false 'hyperv'
    Check 'default-switch-blocked' 'set_ipv4' 'dhcp' 'keep' $false 'default-switch'
    Check 'vpn-blocked' 'set_ipv4' 'dhcp' 'keep' $false 'vpn'
    Check 'identity-changed-blocked' 'set_ipv4' 'dhcp' 'keep' $false 'identity-changed'
    Check 'protected-folder-already-exists' 'set_dns' 'manual' 'keep' $false 'protected-exists'
    Check 'existing-untrusted-nexo-folder' 'set_dns' 'manual' 'keep' $false 'existing-untrusted-root'
    Check 'existing-trusted-operations-folder' 'set_dns' 'manual' 'keep' $false 'existing-trusted-root'
    Check 'leftover-temporary-files' 'set_dns' 'manual' 'keep' $false 'leftover-tmp'
    Check 'invalid-operation-id' 'set_dns' 'manual' 'keep' $false 'bad-id'
    # Real ACL of the protected folder: Windows as administrator only. In GitHub Actions a skip is a failure.
    $onWindows=$PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows
    if($onWindows -and ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $ast=[System.Management.Automation.Language.Parser]::ParseInput($source,[ref]$null,[ref]$null)
        $definitions=@(($source -split "`r?`n") | Where-Object {$_ -match '^\$trustedSids = '})
        foreach($function in 'Get-UntrustedReason','New-ProtectedSecurity','New-ProtectedDirectory','Assert-Trusted','Use-ProtectedDirectory','New-ProtectedFileSecurity','Write-ProtectedFile'){ $definitions+=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $function},$true).Extent.Text }
        $real=[scriptblock]::Create(($definitions -join "`n")+"`n"+@'
$dir=Join-Path $args[0] 'acl-real'
Use-ProtectedDirectory $dir
$acl=Get-Acl -LiteralPath $dir
if(-not $acl.AreAccessRulesProtected){throw 'Real ACL: the new folder inherits permissions'}
if($acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544'){throw 'Real ACL: owner is not Administrators'}
foreach($rule in $acl.GetAccessRules($true,$true,[System.Security.Principal.SecurityIdentifier])){ if($rule.IdentityReference.Value -eq 'S-1-5-32-545' -and ([int64]$rule.FileSystemRights -band 0x500D0156)){throw 'Real ACL: Users can write to the protected folder'} }
Use-ProtectedDirectory $dir
$file=Join-Path $dir 'before.json'; Write-ProtectedFile $file '{}'; Write-ProtectedFile $file '{"again":true}'
$fileAcl=Get-Acl -LiteralPath $file
if($fileAcl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544' -or -not $fileAcl.AreAccessRulesProtected){throw ('Real ACL: protected file owner/inheritance is wrong: '+$fileAcl.Sddl)}
if((Get-Content -LiteralPath $file -Raw) -notmatch 'again'){throw 'Real ACL: protected file was not replaced'}
Assert-Trusted $dir; Assert-Trusted $file
$open=Get-Acl -LiteralPath $file
$open.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule([System.Security.Principal.SecurityIdentifier]'S-1-5-32-545','Modify','Allow')))
# The suite mocks Set-Acl for the simulations above; the real cmdlet is called by its module name.
Microsoft.PowerShell.Security\Set-Acl -LiteralPath $file -AclObject $open
if(-not @((Get-Acl -LiteralPath $file).GetAccessRules($true,$true,[System.Security.Principal.SecurityIdentifier]) | Where-Object {$_.IdentityReference.Value -eq 'S-1-5-32-545' -and ([int64]$_.FileSystemRights -band 0x2)}).Count){throw 'Real ACL: the test could not apply Users:Modify to the file'}
$refused=$false; try { Assert-Trusted $file } catch { $refused=$true }
if(-not $refused){throw 'Real ACL: a file writable by Users was accepted'}
$plain=Join-Path $args[0] 'acl-preexisting'
New-Item -ItemType Directory -Path $plain | Out-Null
$sddl=(Get-Acl -LiteralPath $plain).Sddl
$refused=$false; try { Use-ProtectedDirectory $plain } catch { $refused=$true }
if(-not $refused){throw 'Real ACL: an inheriting pre-existing folder was adopted'}
if((Get-Acl -LiteralPath $plain).Sddl -ne $sddl){throw 'Real ACL: the pre-existing folder ACL was changed'}
'@)
        & $real $testRoot
        Write-Output 'PASS: real ACL of the protected backup folder (administrator)'
    } elseif($env:GITHUB_ACTIONS -eq 'true') { throw 'Real ACL of the protected backup folder was skipped in CI: the job must run on Windows as administrator' }
    else { Write-Output 'SKIP: real ACL of the protected backup folder needs Windows as administrator' }
} finally {
    $resolved=[IO.Path]::GetFullPath($testRoot)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) -and (Split-Path $resolved -Leaf) -like 'NexoNetworkTests-*'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
