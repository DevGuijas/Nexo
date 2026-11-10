$ErrorActionPreference = 'Stop'
$request = $requestJson | ConvertFrom-Json
$interfaceIndex = [int]$request.Index
$before = $null
$changed = $false
$dhcpTransition = @{}
$failureState = $null
# Cópia protegida (#10): o backup e o registro também ficam em %ProgramData%, numa pasta que só SYSTEM e
# Administradores podem alterar. A restauração lê só de lá; os arquivos em %LOCALAPPDATA% podem ser forjados por
# qualquer processo do usuário e nunca definem o que é aplicado com elevação.
$protectedRoot = Join-Path $env:ProgramData 'NexoSupport\operations'
$protectedDir = Join-Path $protectedRoot ([string]$operationId)
$protectedState = @{Opened=$false}
$trustedSids = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
function Assert-OperationId {
    if ([string]$operationId -cnotmatch '^[0-9a-f]{32}$' -or (Split-Path -Leaf $operationDir) -cne [string]$operationId) { throw 'Identificador de operação inválido. Nenhuma alteração foi feita.' }
}
function Protect-Directory($path) {
    if (Test-Path -LiteralPath $path) {
        if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A pasta protegida do Nexo é um link. Nenhuma alteração foi feita.' }
    } else { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetOwner([System.Security.Principal.SecurityIdentifier]'S-1-5-32-544')
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach ($entry in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'), @('S-1-5-32-545', 'ReadAndExecute'))) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule([System.Security.Principal.SecurityIdentifier]$entry[0], [System.Security.AccessControl.FileSystemRights]$entry[1], $inherit, [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Allow)))
    }
    Set-Acl -LiteralPath $path -AclObject $acl
}
function Assert-Trusted($path) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'O backup protegido está em um link. A restauração automática foi recusada.' }
    $acl = Get-Acl -LiteralPath $path
    if ($acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value -notin $trustedSids) { throw 'O backup protegido não pertence ao sistema nem aos administradores. A restauração automática foi recusada.' }
    # Escrita, criação, exclusão, troca de permissão ou de dono, e os direitos genéricos de escrita.
    $write = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
    foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow' -or ($rule.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly)) { continue }
        if (([int64]$rule.FileSystemRights -band $write) -and $rule.IdentityReference.Value -notin $trustedSids) { throw 'O backup protegido pode ser alterado por outros usuários. A restauração automática foi recusada.' }
    }
}
function Open-ProtectedDir {
    Protect-Directory (Split-Path -Parent $protectedRoot)
    Protect-Directory $protectedRoot
    Remove-StaleProtected
    # O id é um GUID novo do aplicativo: uma pasta que já existe não foi criada por esta operação.
    if (Test-Path -LiteralPath $protectedDir) { throw 'A pasta protegida desta operação já existia. Nenhuma alteração foi feita.' }
    Protect-Directory $protectedDir
    $protectedState.Opened = $true
}
function Save-Protected($filename, $value) {
    $destination = Join-Path $protectedDir $filename
    $temporary = $destination + '.tmp'
    $value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $destination -Force
}
# Remove só os arquivos conhecidos e a pasta vazia: nunca apaga recursivamente com elevação (um link poderia apontar
# para outro lugar).
function Remove-ProtectedFolder($path) {
    foreach ($name in 'before.json', 'transaction.json') { $file = Join-Path $path $name; if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } }
    [IO.Directory]::Delete($path, $false)
}
function Remove-Protected { if ($protectedState.Opened) { try { Remove-ProtectedFolder $protectedDir } catch { } } }
function Remove-StaleProtected {
    try {
        $limit = (Get-Date).ToUniversalTime().AddDays(-7)
        foreach ($folder in @(Get-ChildItem -LiteralPath $protectedRoot -Directory -Force)) {
            if ($folder.Name -cmatch '^[0-9a-f]{32}$' -and -not ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $folder.LastWriteTimeUtc -lt $limit) { try { Remove-ProtectedFolder $folder.FullName } catch { } }
        }
    } catch { }
}
function Set-AutomaticIP {
    $current = Get-NetAdapter -InterfaceIndex $interfaceIndex -ErrorAction Stop
    if ([guid]$current.InterfaceGuid -ne [guid]$before.Guid) { throw 'A identidade da interface mudou antes da transição DHCP.' }
    # Let Windows perform the complete static-to-DHCP transition. Removing the last
    # address first can leave Win32_NetworkAdapterConfiguration.IPEnabled false (84).
    $output = @(& "$env:WINDIR\System32\netsh.exe" interface ipv4 set address "name=$interfaceIndex" source=dhcp store=persistent 2>&1)
    $code = $LASTEXITCODE
    $dhcpTransition.method='netsh interface ipv4 set address source=dhcp'; $dhcpTransition.exitCode=$code; $dhcpTransition.output=($output -join "`n")
    if ($code -ne 0) { throw ('O Windows recusou a mudança para IP automático (código '+$code+'): '+($output -join ' ')) }
}
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
            Set-AutomaticIP
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
    Assert-Restored (Snapshot)
}
function Assert-Restored($restored) {
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
if ($recoveryMode) {
    $found = $null
    try {
        Assert-OperationId
        foreach ($path in @((Split-Path -Parent $protectedRoot), $protectedRoot, $protectedDir, (Join-Path $protectedDir 'transaction.json'), (Join-Path $protectedDir 'before.json'))) { Assert-Trusted $path }
        $protectedState.Opened = $true
        $record = Get-Content -LiteralPath (Join-Path $protectedDir 'transaction.json') -Raw | ConvertFrom-Json
        $before = Get-Content -LiteralPath (Join-Path $protectedDir 'before.json') -Raw | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($request.ExpectedGuid) -or [string]$record.Action -ne [string]$request.Action -or [int]$record.Index -ne $interfaceIndex -or [guid]$record.ExpectedGuid -ne [guid]$request.ExpectedGuid -or [int]$before.Index -ne $interfaceIndex -or [guid]$before.Guid -ne [guid]$record.ExpectedGuid) { throw 'O pedido de restauração não corresponde ao registro protegido. Nenhuma alteração foi feita.' }
        $request = [pscustomobject]@{Action=[string]$record.Action;Index=[int]$record.Index;ExpectedGuid=[string]$record.ExpectedGuid}
        $adapter = Get-NetAdapter -InterfaceIndex $interfaceIndex -ErrorAction Stop
        if ([guid]$adapter.InterfaceGuid -ne [guid]$before.Guid -or [guid]$adapter.InterfaceGuid -ne [guid]$request.ExpectedGuid) { throw 'A interface neste índice não é a mesma da transação interrompida. Nenhuma alteração foi feita.' }
        $found = Snapshot
        $alreadyRestored = $true
        try { Assert-Restored $found } catch { $alreadyRestored = $false }
        if ($alreadyRestored) {
            Save-Result 'recovery-result.json' @{success=$true;restored=$false;alreadyRestored=$true;message='A configuração atual já corresponde ao estado anterior salvo. Nenhuma alteração foi feita.';found=$found}
        } else {
            Restore-Network
            Save-Result 'recovery-result.json' @{success=$true;restored=$true;alreadyRestored=$false;message='Configuração anterior restaurada e conferida.';found=$found;after=(Snapshot)}
        }
    } catch {
        $failure = $_.Exception.Message
        try { $state = Snapshot } catch { $state = @{error=$_.Exception.Message} }
        Save-Result 'recovery-result.json' @{success=$false;restored=$false;alreadyRestored=$false;error=$failure;found=$found;state=$state}
    }
    Remove-Protected
    return
}
try {
    Assert-OperationId
    $adapter = Get-NetAdapter -InterfaceIndex $interfaceIndex
    if ([string]::IsNullOrWhiteSpace($request.ExpectedGuid) -or [guid]$adapter.InterfaceGuid -ne [guid]$request.ExpectedGuid) { throw 'A identidade da interface mudou desde a consulta. Consulte a rede novamente.' }
    $hostHyperV = [string]$adapter.InterfaceDescription -match '^Hyper-V Virtual Ethernet Adapter( #\d+)?$'
    $managedSwitch = [string]$adapter.Name -match '(Default Switch|Comutador Padrão|Switch Padrão)'
    if ($managedSwitch -or (($adapter.Virtual -or -not $adapter.HardwareInterface) -and -not $hostHyperV)) { throw 'Esta interface é gerenciada pelo Windows ou por VPN. Reconfiguração de IP/DNS não permitida.' }
    $before = Snapshot
    Open-ProtectedDir
    Save-Protected 'transaction.json' @{Action=[string]$request.Action;Index=$interfaceIndex;ExpectedGuid=[string]$request.ExpectedGuid;Created=(Get-Date).ToUniversalTime().ToString('o')}
    Save-Protected 'before.json' $before
    Save-Result 'before.json' $before
    $changed = $true
    if ($request.Action -eq 'set_dns') {
        if ($request.Mode -eq 'automatic') {
            Get-DnsClientServerAddress -InterfaceIndex $interfaceIndex -AddressFamily IPv4 | Set-DnsClientServerAddress -ResetServerAddresses
        } else {
            Get-DnsClientServerAddress -InterfaceIndex $interfaceIndex -AddressFamily IPv4 | Set-DnsClientServerAddress -ServerAddresses @($request.Servers)
        }
    } elseif ($request.Action -eq 'set_ipv4') {
        if ($request.Mode -eq 'dhcp') {
            Set-AutomaticIP
        } else {
            Clear-ManualIP
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
        # Verify the mode before waiting for a lease. A successful command exit alone
        # is not confirmation, and an API rejection is not proof of a DHCP outage.
        if ($after.Dhcp -ne 'Enabled') { throw 'O comando terminou, mas o Windows não confirmou IP automático. Não foi uma falha comprovada do servidor DHCP.' }
        $leaseDeadline = (Get-Date).AddSeconds(60)
        while (-not @($after.Addresses | Where-Object { [string]$_.PrefixOrigin -in 'Dhcp','3' -and [string]$_.AddressState -in 'Preferred','4' -and $_.IPAddress -notlike '169.254.*' }).Count) {
            if ((Test-Path -LiteralPath (Join-Path $operationDir 'decision.txt')) -and (Get-Content -LiteralPath (Join-Path $operationDir 'decision.txt') -Raw).Trim() -eq 'rollback') { throw 'Aquisição DHCP cancelada. Restaurando a configuração anterior.' }
            if ((Get-Date) -ge $leaseDeadline) { throw 'O Windows confirmou IP automático, mas não foi observada concessão IPv4 utilizável no prazo. A causa ainda não foi comprovada; a configuração anterior será restaurada.' }
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
        Save-Result 'result.json' @{success=$true;message='Configuração aplicada, estado consultado e permanência confirmada pelo usuário.';before=$before;after=(Snapshot);dhcpTransition=$dhcpTransition}
    } else {
        Restore-Network
        Save-Result 'result.json' @{success=$false;reverted=$true;message='Configuração anterior restaurada por cancelamento ou ausência de confirmação.';after=(Snapshot)}
    }
} catch {
    $failure = $_.Exception.Message
    try { $failureState = Snapshot } catch { $failureState = @{error=$_.Exception.Message} }
    $reverted = $false
    $rollbackError = $null
    if ($changed -and $before) { try { Restore-Network; $reverted=$true } catch { $rollbackError=$_.Exception.Message } }
    Save-Result 'result.json' @{success=$false;error=$failure;changed=$changed;reverted=$reverted;rollbackError=$rollbackError;dhcpTransition=$dhcpTransition;failureState=$failureState;backup=(Join-Path $operationDir 'before.json')}
}
Remove-Protected
