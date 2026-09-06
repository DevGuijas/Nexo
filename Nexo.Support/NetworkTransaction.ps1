$ErrorActionPreference = 'Stop'
$request = $requestJson | ConvertFrom-Json
$interfaceIndex = [int]$request.Index
$before = $null
$changed = $false
function Save-Result($filename, $value) {
    $destination = Join-Path $operationDir $filename
    $temporary = $destination + '.tmp'
    $value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $destination -Force
}
function Snapshot {
    $adapter = Get-NetAdapter -InterfaceIndex $interfaceIndex -ErrorAction Stop
    $ip = Get-NetIPInterface -InterfaceIndex $interfaceIndex -AddressFamily IPv4
    $registry = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\' + $adapter.InterfaceGuid
    $staticDns = (Get-ItemProperty -LiteralPath $registry -ErrorAction SilentlyContinue).NameServer
    [pscustomobject]@{
        Index = $interfaceIndex; Name = $adapter.Name; Guid = [string]$adapter.InterfaceGuid
        Dhcp = [string]$ip.Dhcp; StaticDns = [string]$staticDns
        Dns = @((Get-DnsClientServerAddress -InterfaceIndex $interfaceIndex -AddressFamily IPv4).ServerAddresses)
        Addresses = @(Read-Addresses | Select-Object IPAddress,PrefixLength,PrefixOrigin,AddressState)
        Routes = @(Read-Routes | Where-Object {$_.DestinationPrefix -eq '0.0.0.0/0'} | Select-Object NextHop,RouteMetric)
    }
}
function Read-Addresses {
    # Filtering an enumeration tolerates a temporarily empty interface. A filtered CIM
    # query throws ObjectNotFound during DHCP acquisition and also breaks rollback.
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object {$_.InterfaceIndex -eq $interfaceIndex}
}
function Read-Routes {
    Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop | Where-Object {$_.InterfaceIndex -eq $interfaceIndex}
}
function Restore-Dns {
    if ([string]::IsNullOrWhiteSpace($before.StaticDns)) {
        Get-DnsClientServerAddress -InterfaceIndex $interfaceIndex -AddressFamily IPv4 | Set-DnsClientServerAddress -ResetServerAddresses
    } else {
        Get-DnsClientServerAddress -InterfaceIndex $interfaceIndex -AddressFamily IPv4 | Set-DnsClientServerAddress -ServerAddresses @($before.StaticDns -split '[,; ]+' | Where-Object {$_})
    }
}
function Clear-ManualIP {
    Read-Routes | Where-Object {$_.DestinationPrefix -eq '0.0.0.0/0'} | Remove-NetRoute -Confirm:$false
    Read-Addresses | Where-Object {[string]$_.PrefixOrigin -in 'Manual','1'} | Remove-NetIPAddress -Confirm:$false
}
function Restore-Network {
    $current = Get-NetAdapter -InterfaceIndex $interfaceIndex
    if ([string]$current.InterfaceGuid -ne $before.Guid) { throw 'A interface mudou de identidade; restauração automática interrompida.' }
    if ($request.Action -eq 'set_ipv4') {
        Clear-ManualIP
        if ($before.Dhcp -eq 'Enabled') {
            Set-NetIPInterface -InterfaceIndex $interfaceIndex -AddressFamily IPv4 -Dhcp Enabled
        } else {
            Set-NetIPInterface -InterfaceIndex $interfaceIndex -AddressFamily IPv4 -Dhcp Disabled
            Read-Addresses | Remove-NetIPAddress -Confirm:$false
            foreach ($address in $before.Addresses) {
                if ([string]$address.PrefixOrigin -in 'Manual','1') {
                    New-NetIPAddress -InterfaceIndex $interfaceIndex -IPAddress $address.IPAddress -PrefixLength $address.PrefixLength | Out-Null
                }
            }
            foreach ($route in $before.Routes) {
                New-NetRoute -InterfaceIndex $interfaceIndex -DestinationPrefix '0.0.0.0/0' -NextHop $route.NextHop -RouteMetric $route.RouteMetric | Out-Null
            }
        }
    }
    Restore-Dns
    $restored = Snapshot
    if ($request.Action -eq 'set_ipv4') {
        if ($restored.Dhcp -ne $before.Dhcp) { throw 'O modo DHCP anterior não foi restaurado.' }
        if ($before.Dhcp -eq 'Disabled') {
            foreach ($address in $before.Addresses | Where-Object {[string]$_.PrefixOrigin -in 'Manual','1'}) {
                if (-not @($restored.Addresses | Where-Object {$_.IPAddress -eq $address.IPAddress -and $_.PrefixLength -eq $address.PrefixLength}).Count) { throw 'O endereço IPv4 anterior não foi confirmado após a restauração.' }
            }
            foreach ($route in $before.Routes) {
                if (-not @($restored.Routes | Where-Object {$_.NextHop -eq $route.NextHop}).Count) { throw 'O gateway anterior não foi confirmado após a restauração.' }
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($before.StaticDns)) {
        if (-not [string]::IsNullOrWhiteSpace($restored.StaticDns)) { throw 'DNS automático anterior não restaurado.' }
    } elseif ((@($restored.Dns) -join ',') -ne (@($before.Dns) -join ',')) { throw 'Os servidores DNS anteriores não foram restaurados.' }
}
try {
    $adapter = Get-NetAdapter -InterfaceIndex $interfaceIndex
    if ([string]::IsNullOrWhiteSpace($request.ExpectedGuid) -or [guid]$adapter.InterfaceGuid -ne [guid]$request.ExpectedGuid) { throw 'A identidade da interface mudou desde a consulta. Consulte a rede novamente.' }
    $hostHyperV = [string]$adapter.InterfaceDescription -match '^Hyper-V Virtual Ethernet Adapter( #\d+)?$'
    $managedSwitch = [string]$adapter.Name -match '(Default Switch|Comutador Padrão|Switch Padrão)'
    if ($managedSwitch -or (($adapter.Virtual -or -not $adapter.HardwareInterface) -and -not $hostHyperV)) { throw 'Esta interface é gerenciada pelo Windows ou por VPN. Reconfiguração de IP/DNS não permitida.' }
    $before = Snapshot
    Save-Result 'before.json' $before
    $changed = $true
    if ($request.Action -eq 'set_dns') {
        if ($request.Mode -eq 'automatic') {
            Get-DnsClientServerAddress -InterfaceIndex $interfaceIndex -AddressFamily IPv4 | Set-DnsClientServerAddress -ResetServerAddresses
        } else {
            Get-DnsClientServerAddress -InterfaceIndex $interfaceIndex -AddressFamily IPv4 | Set-DnsClientServerAddress -ServerAddresses @($request.Servers)
        }
    } elseif ($request.Action -eq 'set_ipv4') {
        Clear-ManualIP
        if ($request.Mode -eq 'dhcp') {
            Set-NetIPInterface -InterfaceIndex $interfaceIndex -AddressFamily IPv4 -Dhcp Enabled
        } else {
            Set-NetIPInterface -InterfaceIndex $interfaceIndex -AddressFamily IPv4 -Dhcp Disabled
            # A previous lease can remain briefly when disabling DHCP.
            Read-Addresses | Remove-NetIPAddress -Confirm:$false
            $parameters = @{InterfaceIndex=$interfaceIndex;IPAddress=[string]$request.Address;PrefixLength=[byte]$request.Prefix}
            if ($request.Gateway) { $parameters.DefaultGateway = [string]$request.Gateway }
            New-NetIPAddress @parameters | Out-Null
        }
        Restore-Dns
    } else { throw 'Ação de rede inválida.' }
    Start-Sleep -Seconds 3
    $after = Snapshot
    if ($request.Action -eq 'set_ipv4' -and $request.Mode -eq 'dhcp') {
        $leaseDeadline = (Get-Date).AddSeconds(20)
        while (-not @($after.Addresses | Where-Object { [string]$_.PrefixOrigin -in 'Dhcp','3' -and [string]$_.AddressState -in 'Preferred','4' -and $_.IPAddress -notlike '169.254.*' }).Count) {
            if ((Get-Date) -ge $leaseDeadline) { throw 'DHCP foi habilitado, mas não forneceu um IPv4 utilizável no prazo. Restaurando a configuração anterior; isso não confirma conexão.' }
            Start-Sleep -Milliseconds 500
            $after = Snapshot
        }
    }
    if ($request.Action -eq 'set_dns' -and $request.Mode -eq 'manual' -and (@($after.Dns) -join ',') -ne (@($request.Servers) -join ',')) { throw 'DNS aplicado não corresponde ao solicitado.' }
    if ($request.Action -eq 'set_dns' -and $request.Mode -eq 'automatic' -and -not [string]::IsNullOrWhiteSpace($after.StaticDns)) { throw 'DNS automático não confirmado.' }
    if ($request.Action -eq 'set_ipv4' -and $request.Mode -eq 'dhcp' -and $after.Dhcp -ne 'Enabled') { throw 'DHCP não confirmado.' }
    if ($request.Action -eq 'set_ipv4' -and $request.Mode -eq 'static') {
        $match = @($after.Addresses | Where-Object {$_.IPAddress -eq $request.Address -and $_.PrefixLength -eq $request.Prefix})
        if ($match.Count -ne 1 -or [string]$match[0].AddressState -in 'Duplicate','2') { throw 'IP estático não confirmado ou duplicado.' }
    }
    Save-Result 'applied.json' $after
    $deadline = (Get-Date).AddSeconds(60)
    $decisionPath = Join-Path $operationDir 'decision.txt'
    while (-not (Test-Path -LiteralPath $decisionPath) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    $keep = (Get-Date) -lt $deadline -and (Test-Path -LiteralPath $decisionPath) -and (Get-Content -LiteralPath $decisionPath -Raw).Trim() -eq 'keep'
    if ($keep) {
        Save-Result 'result.json' @{success=$true;message='Configuração aplicada, estado consultado e permanência confirmada pelo usuário.';before=$before;after=(Snapshot)}
    } else {
        Restore-Network
        Save-Result 'result.json' @{success=$false;reverted=$true;message='Configuração anterior restaurada por cancelamento ou ausência de confirmação.';after=(Snapshot)}
    }
} catch {
    $failure = $_.Exception.Message
    $reverted = $false
    $rollbackError = $null
    if ($changed -and $before) { try { Restore-Network; $reverted=$true } catch { $rollbackError=$_.Exception.Message } }
    Save-Result 'result.json' @{success=$false;error=$failure;changed=$changed;reverted=$reverted;rollbackError=$rollbackError;backup=(Join-Path $operationDir 'before.json')}
}
