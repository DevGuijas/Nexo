# Read adapters independently of the IP stack. Disabled adapters may have no MSFT_NetIPInterface.
$adapters = @(Get-NetAdapter -ErrorAction Stop)
$issues = [System.Collections.Generic.List[string]]::new()
function Read-Optional($label, [scriptblock]$read) {
    try { & $read } catch { $issues.Add($label + ': ' + $_.Exception.Message) }
}
$interfaces = @(Read-Optional 'IPv4' { Get-NetIPInterface -AddressFamily IPv4 -ErrorAction Stop })
$addresses = @(Read-Optional 'Endereços' { Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop })
$routes = @(Read-Optional 'Rotas' { Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop })
$dns = @(Read-Optional 'DNS' { Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction Stop })
$profiles = @(Read-Optional 'Perfis' { Get-NetConnectionProfile -ErrorAction Stop })
$rows = @($adapters | ForEach-Object {
    $adapter = $_; $index = [int]$adapter.ifIndex
    $ip = @($interfaces | Where-Object {$_.InterfaceIndex -eq $index}) | Select-Object -First 1
    $disabled = [string]$adapter.Status -eq 'Disabled'
    [pscustomobject]@{
        Index = $index; Guid = [string]$adapter.InterfaceGuid; Name = $adapter.Name
        Description = $adapter.InterfaceDescription; Status = [string]$adapter.Status
        InterfaceType = [int]$adapter.InterfaceType
        AdminStatus = [string]$adapter.AdminStatus; MediaState = [string]$adapter.MediaConnectionState
        Disabled = $disabled; CanEnable = $disabled; Virtual = [bool]$adapter.Virtual; Hardware = [bool]$adapter.HardwareInterface
        IpStackAvailable = $null -ne $ip; Dhcp = $(if($ip){[string]$ip.Dhcp}else{$null})
        InterfaceMetric = $(if($ip){$ip.InterfaceMetric}else{$null})
        Addresses = @($addresses | Where-Object {$_.InterfaceIndex -eq $index} | Select-Object IPAddress,PrefixLength,@{N='PrefixOrigin';E={[string]$_.PrefixOrigin}},@{N='AddressState';E={[string]$_.AddressState}})
        HasSplitDefaultRoute = @($routes | Where-Object {$_.InterfaceIndex -eq $index -and $_.DestinationPrefix -in '0.0.0.0/1','128.0.0.0/1'}).Count -gt 0
        Gateways = @($routes | Where-Object {$_.InterfaceIndex -eq $index -and $_.DestinationPrefix -eq '0.0.0.0/0' -and $_.NextHop -ne '0.0.0.0'} | Select-Object -ExpandProperty NextHop -Unique)
        Dns = @($dns | Where-Object {$_.InterfaceIndex -eq $index} | ForEach-Object {$_.ServerAddresses})
        Profile = @($profiles | Where-Object {$_.InterfaceIndex -eq $index} | ForEach-Object {[string]$_.NetworkCategory})
    }
})
[pscustomobject]@{
    Adapters = $rows; Warnings = @($issues.ToArray()); Partial = $issues.Count -gt 0
    Note = 'Disabled significa desativado administrativamente: use enable_network_adapter para uma interface identificada, inclusive vEthernet. Disconnected não significa desativado. Ausência de IP em interface desativada não impede sua ativação. Habilitar não comprova acesso à internet.'
} | ConvertTo-Json -Depth 8 -Compress
