$ErrorActionPreference='Stop'
# All networking cmdlets are replaced here. This suite never invokes real network mutations.
function Get-NetAdapter { param($InterfaceIndex) [pscustomobject]@{Name=$script:adapterName;InterfaceDescription=$script:adapterDescription;InterfaceGuid='{00000000-0000-0000-0000-000000000001}';Virtual=$script:virtual;HardwareInterface=(-not $script:virtual)} }
function Get-NetIPInterface { param($InterfaceIndex,$AddressFamily) [pscustomobject]@{Dhcp=$script:state.Dhcp} }
function Get-CimInstance { param($ClassName,$Filter,$ErrorAction) if($ClassName -ne 'Win32_NetworkAdapterConfiguration' -or $Filter -ne 'InterfaceIndex=10'){throw 'Wrong DHCP target'}; [pscustomobject]@{SettingID='{00000000-0000-0000-0000-000000000001}'} }
function Invoke-CimMethod { param($InputObject,$MethodName,$OperationTimeoutSec,$ErrorAction) if($MethodName -ne 'RenewDHCPLease'){throw 'Wrong method'}; $script:renewed=$true; if($script:renewOnly){$script:state.Addresses=@([pscustomobject]@{IPAddress='10.0.0.25';PrefixLength=24;PrefixOrigin=3;AddressState=4})}; [pscustomobject]@{ReturnValue=0} }
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
        if ($script:failOnce) {$script:failOnce=$false; throw 'Simulated apply failure'}
        if($ResetServerAddresses){$script:state.Dns=@('10.0.0.1');$script:state.StaticDns=''}
        else{$script:state.Dns=@($ServerAddresses);$script:state.StaticDns=@($ServerAddresses) -join ','}
    }
}
function Set-NetIPInterface { param($InterfaceIndex,$AddressFamily,$Dhcp) $script:state.Dhcp=$Dhcp; if($Dhcp -eq 'Enabled'){if($script:noLease -or $script:renewOnly -or $script:leaseDelay -gt 0){$script:state.Addresses=@()}else{$script:state.Addresses=@([pscustomobject]@{IPAddress='10.0.0.25';PrefixLength=24;PrefixOrigin=3;AddressState=4})}} }
function Remove-NetRoute { param([Parameter(ValueFromPipeline=$true)]$InputObject,$Confirm) process {$script:state.Routes=@()} }
function Remove-NetIPAddress { param([Parameter(ValueFromPipeline=$true)]$InputObject,$Confirm) process {$script:state.Addresses=@($script:state.Addresses | Where-Object {$_.IPAddress -ne $InputObject.IPAddress})} }
function New-NetIPAddress { param($InterfaceIndex,$IPAddress,$PrefixLength,$DefaultGateway) $script:state.Addresses+= [pscustomobject]@{IPAddress=$IPAddress;PrefixLength=$PrefixLength;PrefixOrigin=1;AddressState=4}; if($DefaultGateway){$script:state.Routes=@([pscustomobject]@{DestinationPrefix='0.0.0.0/0';NextHop=$DefaultGateway;RouteMetric=256})} }
function New-NetRoute { param($InterfaceIndex,$DestinationPrefix,$NextHop,$RouteMetric) $script:state.Routes+=[pscustomobject]@{DestinationPrefix=$DestinationPrefix;NextHop=$NextHop;RouteMetric=$RouteMetric} }
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('NexoNetworkTests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Nexo.Support\NetworkTransaction.ps1') -Raw
$scriptBlock=[scriptblock]::Create($source)
function Check($name,$action,$mode,$decision,$failure=$false,$variant='normal') {
    $script:state=@{Dhcp='Disabled';StaticDns='10.0.0.2';Dns=@('10.0.0.2');Addresses=@([pscustomobject]@{IPAddress='10.0.0.10';PrefixLength=24;PrefixOrigin=1;AddressState=4});Routes=@([pscustomobject]@{DestinationPrefix='0.0.0.0/0';NextHop='10.0.0.1';RouteMetric=256})}
    $script:failOnce=$failure
    $script:noLease=$variant -eq 'no-lease'; $script:leaseDelay=$(if($variant -eq 'delayed-lease'){3}else{0})
    $script:renewOnly=$variant -eq 'renew-required'; $script:renewed=$false
    $script:virtual=$variant -in 'hyperv','default-switch','vpn'
    $script:adapterName=$(if($variant -eq 'default-switch'){'vEthernet (Default Switch)'}else{'Adapter-Test'})
    $script:adapterDescription=$(if($variant -in 'hyperv','default-switch'){'Hyper-V Virtual Ethernet Adapter #2'}elseif($variant -eq 'vpn'){'VPN Adapter'}else{'Realtek Ethernet'})
    $operationDir=Join-Path $testRoot $name; New-Item -ItemType Directory -Path $operationDir | Out-Null
    if($decision -ne 'timeout'){Set-Content -LiteralPath (Join-Path $operationDir 'decision.txt') -Value $decision}
    $requestJson=@{Action=$action;Index=10;ExpectedGuid=$(if($variant -eq 'identity-changed'){'00000000-0000-0000-0000-000000000099'}else{'00000000-0000-0000-0000-000000000001'});Mode=$mode;Servers=@('1.1.1.1');Address='10.0.0.20';Prefix=24;Gateway='10.0.0.1'} | ConvertTo-Json
    $testSource=$source.Replace('.AddSeconds(60)', '.AddMilliseconds(500)').Replace('Start-Sleep -Milliseconds 500','Start-Sleep -Milliseconds 10').Replace('Start-Sleep -Seconds 3','Start-Sleep -Milliseconds 1')
    & ([scriptblock]::Create($testSource))
    $result=Get-Content -LiteralPath (Join-Path $operationDir 'result.json') -Raw | ConvertFrom-Json
    if($variant -in 'default-switch','vpn','identity-changed') {
        if($result.success -or (Test-Path (Join-Path $operationDir 'before.json')) -or $script:state.Addresses[0].IPAddress -ne '10.0.0.10'){throw 'Disallowed target was changed'}
    } elseif($decision -eq 'keep' -and -not $failure -and -not $script:noLease) {
        if(-not $result.success){throw ($name+': '+($result | ConvertTo-Json -Depth 5))}
        if($action -eq 'set_dns' -and $script:state.Dns[0] -ne '1.1.1.1'){throw 'DNS was not applied'}
        if($action -eq 'set_ipv4' -and $mode -eq 'static' -and $script:state.Addresses[0].IPAddress -ne '10.0.0.20'){throw 'Static IP was not applied'}
        if($action -eq 'set_ipv4' -and $mode -eq 'dhcp' -and ($script:state.Addresses[0].IPAddress -ne '10.0.0.25' -or $script:state.Dns[0] -ne '10.0.0.2')){throw 'DHCP lease or preserved DNS not verified'}
    } else {
        if($result.success -or -not $result.reverted){throw ($name+': rollback not reported: '+($result | ConvertTo-Json -Depth 5))}
        if($script:state.Dns[0] -ne '10.0.0.2' -or $script:state.Addresses[0].IPAddress -ne '10.0.0.10'){throw ($name+': prior state not restored')}
    }
    Write-Output ('PASS: simulated network transaction '+$name)
    if($variant -eq 'renew-required' -and -not $script:renewed){throw 'DHCP renewal was not requested'}
}
try {
    Check 'dns-keep' 'set_dns' 'manual' 'keep'
    Check 'dns-rollback' 'set_dns' 'manual' 'rollback'
    Check 'dns-error-rollback' 'set_dns' 'manual' 'keep' $true
    Check 'static-keep' 'set_ipv4' 'static' 'keep'
    Check 'static-rollback' 'set_ipv4' 'static' 'rollback'
    Check 'dhcp-rollback' 'set_ipv4' 'dhcp' 'rollback'
    Check 'timeout-rollback' 'set_dns' 'manual' 'timeout'
    Check 'dhcp-empty-then-lease' 'set_ipv4' 'dhcp' 'keep' $false 'delayed-lease'
    Check 'dhcp-renewal-required' 'set_ipv4' 'dhcp' 'keep' $false 'renew-required'
    Check 'dhcp-unavailable-restores-from-empty' 'set_ipv4' 'dhcp' 'keep' $false 'no-lease'
    Check 'hyperv-dhcp-keep' 'set_ipv4' 'dhcp' 'keep' $false 'hyperv'
    Check 'default-switch-blocked' 'set_ipv4' 'dhcp' 'keep' $false 'default-switch'
    Check 'vpn-blocked' 'set_ipv4' 'dhcp' 'keep' $false 'vpn'
    Check 'identity-changed-blocked' 'set_ipv4' 'dhcp' 'keep' $false 'identity-changed'
} finally {
    $resolved=[IO.Path]::GetFullPath($testRoot)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) -and (Split-Path $resolved -Leaf) -like 'NexoNetworkTests-*'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
