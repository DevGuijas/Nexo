$ErrorActionPreference='Stop'
# All networking cmdlets are replaced here. This suite never invokes real network mutations.
function Get-NetAdapter { param($InterfaceIndex) [pscustomobject]@{Name=$script:adapterName;InterfaceDescription=$script:adapterDescription;InterfaceGuid='{00000000-0000-0000-0000-000000000001}';Virtual=$script:virtual;HardwareInterface=(-not $script:virtual)} }
function Get-NetIPInterface { param($InterfaceIndex,$AddressFamily) [pscustomobject]@{Dhcp=$script:state.Dhcp} }
function Get-CimInstance { param($ClassName,$Filter,$ErrorAction) if($ClassName -ne 'Win32_NetworkAdapterConfiguration' -or $Filter -ne 'InterfaceIndex=10'){throw 'Wrong DHCP target'}; [pscustomobject]@{SettingID='{00000000-0000-0000-0000-000000000001}'} }
function Invoke-CimMethod { throw 'Legacy renewal must not run after removing the last IP (Windows error 84)' }
# Every simulated mutation requires the protected copy to exist first (#10): no network change before it.
function Assert-Backed { if($script:currentProtected -and -not (Test-Path -LiteralPath (Join-Path $script:currentProtected 'before.json'))){throw 'Network changed before the protected backup was written'} }
# Simulated protected store: the real ACL functions are renamed in the code under test.
function Protect-Directory($path) { if(Test-Path -LiteralPath $path){ if((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'link'} } else { New-Item -ItemType Directory -Path $path -Force | Out-Null } }
function Assert-Trusted($path) { if(-not (Test-Path -LiteralPath $path)){throw ('Arquivo protegido ausente: '+$path)}; if($script:untrusted){throw 'Simulated untrusted protected copy. Automatic restore refused.'} }
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
$protectedTestRoot=Join-Path $testRoot 'programdata\NexoSupport\operations'
$productionRoot="Join-Path `$env:ProgramData 'NexoSupport\operations'"
function Isolated($text) {
    foreach($piece in @($productionRoot,'function Protect-Directory(','function Assert-Trusted(')){ if(-not $text.Contains($piece)){throw ('Protected-store hook not found: '+$piece)} }
    $text.Replace($productionRoot,"'"+$protectedTestRoot.Replace("'","''")+"'").Replace('function Protect-Directory(','function Protect-Directory-Windows(').Replace('function Assert-Trusted(','function Assert-Trusted-Windows(')
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
    $script:currentProtected=Join-Path $protectedTestRoot $operationId; $script:untrusted=$false
    if($variant -eq 'protected-exists'){New-Item -ItemType Directory -Path $script:currentProtected -Force | Out-Null; Set-Content -LiteralPath (Join-Path $script:currentProtected 'before.json') -Value '{"forjado":true}'}
    if($decision -ne 'timeout'){Set-Content -LiteralPath (Join-Path $operationDir 'decision.txt') -Value $decision}
    $requestJson=@{Action=$action;Index=10;ExpectedGuid=$(if($variant -eq 'identity-changed'){'00000000-0000-0000-0000-000000000099'}else{'00000000-0000-0000-0000-000000000001'});Mode=$mode;Servers=@('1.1.1.1');Address='10.0.0.20';Prefix=24;Gateway='10.0.0.1'} | ConvertTo-Json
    $testSource=(Isolated $source).Replace('"$env:WINDIR\System32\netsh.exe"','Invoke-TestNetsh').Replace('.AddSeconds(60)', '.AddMilliseconds(500)').Replace('Start-Sleep -Milliseconds 500','Start-Sleep -Milliseconds 10').Replace('Start-Sleep -Seconds 3','Start-Sleep -Milliseconds 1')
    if($testSource.Contains('netsh.exe')){throw 'Native command was not isolated by the simulator'}
    & ([scriptblock]::Create($testSource))
    $result=Get-Content -LiteralPath (Join-Path $operationDir 'result.json') -Raw | ConvertFrom-Json
    if($variant -in 'default-switch','vpn','identity-changed','protected-exists','bad-id') {
        if($result.success -or (Test-Path (Join-Path $operationDir 'before.json')) -or $script:state.Addresses[0].IPAddress -ne '10.0.0.10' -or $script:state.Dns[0] -ne '10.0.0.2'){throw ($name+': disallowed target was changed')}
        if($variant -eq 'protected-exists' -and ((Get-Content -LiteralPath (Join-Path $script:currentProtected 'before.json') -Raw) -notmatch 'forjado' -or $result.error -notmatch 'existia')){throw ($name+': pre-existing protected folder was used or removed')}
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
    if($variant -notin 'default-switch','vpn','identity-changed','protected-exists','bad-id' -and (Test-Path -LiteralPath $script:currentProtected)){throw ($name+': protected copy was left behind after the result')}
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
    $script:currentProtected=Join-Path $protectedTestRoot $operationId; $script:untrusted=$variant -eq 'untrusted'
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
    $result=Get-Content -LiteralPath (Join-Path $operationDir 'recovery-result.json') -Raw | ConvertFrom-Json
    switch($expect) {
        'restored' { if(-not $result.success -or -not $result.restored -or -not $result.found -or -not $result.after){throw ($name+': restore not reported: '+($result | ConvertTo-Json -Depth 5))} }
        'already' { if(-not $result.success -or $result.restored -or -not $result.alreadyRestored){throw ($name+': matching state was not detected')} }
        'failed' { if($result.success -or $result.restored -or -not $result.error -or -not $result.state){throw ($name+': failure or uncertainty not recorded')} }
    }
    if($variant -eq 'dhcp-before') { if($script:state.Dhcp -ne 'Enabled' -or -not $script:transitionCalled){throw ($name+': DHCP was not restored')} }
    elseif($variant -in 'identity-changed','untrusted','request-mismatch','missing-protected') { if($script:state.Addresses[0].IPAddress -ne '10.0.0.20' -or $script:state.Dns[0] -ne '1.1.1.1'){throw ($name+': network was changed without a trusted protected backup')} }
    elseif($expect -ne 'failed') { if($script:state.Addresses[0].IPAddress -ne '10.0.0.10' -or $script:state.Dns[0] -ne '10.0.0.2'){throw ($name+': prior state not present after recovery')} }
    if(@($script:state.Dns) -contains '203.0.113.66'){throw ($name+': user-writable backup was applied')}
    if($variant -eq 'untrusted' -and -not (Test-Path -LiteralPath (Join-Path $script:currentProtected 'before.json'))){throw ($name+': untrusted protected folder was deleted with elevation')}
    if($variant -notin 'untrusted','missing-protected' -and (Test-Path -LiteralPath $script:currentProtected)){throw ($name+': protected copy was left behind after recovery')}
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
    Recover 'recovery-untrusted-copy' 'set_ipv4' (StaticState '10.0.0.20' '1.1.1.1') 'failed' 'untrusted'
    Recover 'recovery-request-mismatch' 'set_ipv4' (StaticState '10.0.0.20' '1.1.1.1') 'failed' 'request-mismatch'
    Recover 'recovery-missing-protected' 'set_ipv4' (StaticState '10.0.0.20' '1.1.1.1') 'failed' 'missing-protected'
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
    Check 'invalid-operation-id' 'set_dns' 'manual' 'keep' $false 'bad-id'
    # Real ACL of the protected folder: Windows as administrator only (GitHub CI runs elevated).
    $onWindows=$PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows
    if($onWindows -and ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $ast=[System.Management.Automation.Language.Parser]::ParseInput($source,[ref]$null,[ref]$null)
        $definitions=@(($source -split "`r?`n") | Where-Object {$_ -match '^\$trustedSids = '})
        foreach($function in 'Protect-Directory','Assert-Trusted'){ $definitions+=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $function},$true).Extent.Text }
        $real=[scriptblock]::Create(($definitions -join "`n")+"`n"+@'
$dir=Join-Path $args[0] 'acl-real'
Protect-Directory $dir
$acl=Get-Acl -LiteralPath $dir
if(-not $acl.AreAccessRulesProtected){throw 'Real ACL: inheritance was not disabled'}
if($acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544'){throw 'Real ACL: owner is not Administrators'}
foreach($rule in $acl.GetAccessRules($true,$true,[System.Security.Principal.SecurityIdentifier])){ if($rule.IdentityReference.Value -eq 'S-1-5-32-545' -and ([int64]$rule.FileSystemRights -band 0x500D0156)){throw 'Real ACL: Users can write to the protected folder'} }
$file=Join-Path $dir 'before.json'; Set-Content -LiteralPath $file -Value '{}'
Assert-Trusted $dir; Assert-Trusted $file
$open=Get-Acl -LiteralPath $file
$open.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule([System.Security.Principal.SecurityIdentifier]'S-1-5-32-545','Modify','Allow')))
Set-Acl -LiteralPath $file -AclObject $open
$refused=$false; try { Assert-Trusted $file } catch { $refused=$true }
if(-not $refused){throw 'Real ACL: a file writable by Users was accepted'}
'@)
        & $real $testRoot
        Write-Output 'PASS: real ACL of the protected backup folder (administrator)'
    } else { Write-Output 'SKIP: real ACL of the protected backup folder needs Windows as administrator' }
} finally {
    $resolved=[IO.Path]::GetFullPath($testRoot)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) -and (Split-Path $resolved -Leaf) -like 'NexoNetworkTests-*'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
