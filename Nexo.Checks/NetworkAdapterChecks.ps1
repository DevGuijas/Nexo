$ErrorActionPreference='Stop'
# Simulated Windows cmdlets: these tests never change a real adapter.
$script:status='Disabled'; $script:enableCount=0; $script:dnsFailure=$false; $script:enableFails=$false
function Get-NetAdapter {
    param($ErrorAction)
    [pscustomobject]@{ifIndex=8;InterfaceGuid='{00000000-0000-0000-0000-000000000008}';Name='Ethernet';InterfaceDescription='Test physical';Status='Up';AdminStatus='Up';MediaConnectionState='Connected';Virtual=$false;HardwareInterface=$true}
    [pscustomobject]@{ifIndex=3;InterfaceGuid='{00000000-0000-0000-0000-000000000003}';Name='vEthernet (Rede de teste)';InterfaceDescription='Test Hyper-V';Status=$script:status;AdminStatus=$(if($script:status -eq 'Disabled'){'Down'}else{'Up'});MediaConnectionState='Disconnected';Virtual=$true;HardwareInterface=$false}
}
function Get-NetIPInterface {param($AddressFamily,$InterfaceIndex,$ErrorAction) if($InterfaceIndex -eq 3){throw 'No MSFT_NetIPInterface InterfaceIndex=3'}; [pscustomobject]@{InterfaceIndex=8;Dhcp='Enabled'} }
function Get-NetIPAddress {param($AddressFamily,$ErrorAction) [pscustomobject]@{InterfaceIndex=8;IPAddress='192.0.2.10';PrefixLength=24} }
function Get-NetRoute {param($AddressFamily,$ErrorAction) [pscustomobject]@{InterfaceIndex=8;DestinationPrefix='0.0.0.0/0';NextHop='0.0.0.0';RouteMetric=256} }
function Get-DnsClientServerAddress {param($AddressFamily,$InterfaceIndex,$ErrorAction) if($script:dnsFailure -or $InterfaceIndex -eq 3){throw 'DNS unavailable'}; [pscustomobject]@{InterfaceIndex=8;ServerAddresses=@('192.0.2.53')} }
function Get-NetConnectionProfile {param($ErrorAction) [pscustomobject]@{InterfaceIndex=8;NetworkCategory='Public'} }
function Enable-NetAdapter {param([Parameter(ValueFromPipeline=$true)]$InputObject,$Confirm) process {if($InputObject.ifIndex -ne 3){throw 'Wrong adapter selected'};$script:enableCount++;if(-not $script:enableFails){$script:status='Disconnected'}} }
function Assert($condition,$label) {if(-not $condition){throw ('FAIL: '+$label)};Write-Output ('PASS: '+$label)}
$inventory=[scriptblock]::Create((Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Nexo.Support\NetworkInventory.ps1') -Raw))
$enableSource=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Nexo.Support\EnableNetworkAdapter.ps1') -Raw
$enable=[scriptblock]::Create($enableSource)
$result=(& $inventory) | ConvertFrom-Json
$target=$result.Adapters | Where-Object {$_.Index -eq 3}
Assert ($result.Adapters.Count -eq 2 -and $target.Disabled -and -not $target.IpStackAvailable) 'disabled virtual adapter remains visible without an IP interface'
Assert ($target.Dns.Count -eq 0 -and $result.Adapters[0].Gateways.Count -eq 0) 'missing DNS and on-link route are not fabricated as internet configuration'
$script:dnsFailure=$true
$partial=(& $inventory) | ConvertFrom-Json
Assert ($partial.Partial -and $partial.Warnings.Count -eq 1 -and $partial.Adapters.Count -eq 2) 'failed optional DNS query preserves adapter inventory with a warning'
$script:dnsFailure=$false
$expectedGuid='{00000000-0000-0000-0000-000000000003}';$expectedIndex=3
$enabled=& $enable
Assert ($script:enableCount -eq 1 -and $enabled.Changed -and $enabled.After -eq 'Disconnected' -and -not $enabled.InternetVerified) 'exact virtual adapter enabled without claiming internet access'
$again=& $enable
Assert ($script:enableCount -eq 1 -and -not $again.Changed) 'disconnected adapter is not enabled again'
$script:status='Disabled';$expectedIndex=99
$rejected=$false;try {& $enable | Out-Null}catch {$rejected=$true}
Assert ($rejected -and $script:enableCount -eq 1) 'reused index or changed identity rejected before activation'
$expectedIndex=3;$script:enableFails=$true
$rejected=$false;try {& ([scriptblock]::Create($enableSource.Replace('.AddSeconds(15)', '.AddMilliseconds(1)'))) | Out-Null}catch {$rejected=$true}
Assert ($rejected -and $script:status -eq 'Disabled') 'unsuccessful enable operation cannot report success'
