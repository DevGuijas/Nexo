#Requires -RunAsAdministrator
param([Parameter(Mandatory=$true)][int]$InterfaceIndex,
      [Parameter(Mandatory=$true)][guid]$ExpectedGuid)
# Opt-in integration test. Uses the CURRENT leased address as the temporary static
# address, then the production transaction to return to DHCP. Never run as a unit test.
$ErrorActionPreference='Stop'
$adapter=Get-NetAdapter -InterfaceIndex $InterfaceIndex
if([guid]$adapter.InterfaceGuid -ne $ExpectedGuid -or -not $adapter.HardwareInterface -or $adapter.Virtual){throw 'Expected physical adapter was not found'}
$ipInterface=Get-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4
if([string]$ipInterface.Dhcp -ne 'Enabled'){throw 'Test requires a working DHCP configuration initially'}
$lease=@(Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 | Where-Object {[string]$_.PrefixOrigin -eq 'Dhcp' -and [string]$_.AddressState -eq 'Preferred' -and $_.IPAddress -notlike '169.254.*'})
$routes=@(Get-NetRoute -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 | Where-Object {$_.DestinationPrefix -eq '0.0.0.0/0'})
if($lease.Count -ne 1 -or $routes.Count -ne 1){throw 'Expected one DHCP lease and one default gateway'}
$registry='HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\'+$adapter.InterfaceGuid
$staticDns=[string](Get-ItemProperty -LiteralPath $registry).NameServer
$testRoot=Join-Path $PSScriptRoot ('..\artifacts\dhcp-live-'+(Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$testRoot=(Resolve-Path -LiteralPath $testRoot).Path
@{Index=$InterfaceIndex;Guid=[string]$ExpectedGuid;Dhcp='Enabled';Address=$lease[0].IPAddress;Prefix=$lease[0].PrefixLength;Gateway=$routes[0].NextHop;StaticDns=$staticDns} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'initial.json') -Encoding UTF8
$finished=Join-Path $testRoot 'finished.txt'
# Independent watchdog survives a closed test process. It only restores DHCP on
# this exact GUID/index, whose original mode was checked above; DNS is untouched.
$marker=$finished.Replace("'","''")
$watchdog="Start-Sleep -Seconds 180; if(-not(Test-Path -LiteralPath '$marker')){`$a=Get-NetAdapter -InterfaceIndex $InterfaceIndex -ErrorAction SilentlyContinue; if(`$a -and [guid]`$a.InterfaceGuid -eq [guid]'$ExpectedGuid'){& `"`$env:WINDIR\System32\netsh.exe`" interface ipv4 set address name=$InterfaceIndex source=dhcp store=persistent}}"
Start-Process -FilePath "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($watchdog))) -WindowStyle Hidden | Out-Null
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Nexo.Support\NetworkTransaction.ps1') -Raw
try {
    foreach($mode in @('static','dhcp')) {
        # The executor requires a GUID "N" operation id and writes its protected copy under %ProgramData% (#10).
        $operationId=[guid]::NewGuid().ToString('N')
        $operationDir=Join-Path $testRoot $operationId; New-Item -ItemType Directory -Path $operationDir | Out-Null
        Set-Content -LiteralPath (Join-Path $operationDir 'mode.txt') -Value $mode
        $requestJson=@{Action='set_ipv4';Index=$InterfaceIndex;ExpectedGuid=[string]$ExpectedGuid;Mode=$mode;Address=$(if($mode -eq 'static'){$lease[0].IPAddress}else{''});Prefix=$(if($mode -eq 'static'){$lease[0].PrefixLength}else{0});Gateway=$(if($mode -eq 'static'){$routes[0].NextHop}else{''});Servers=@()} | ConvertTo-Json
        $decisionPipeName='nexo-network-'+$operationId
        $appProcessId=$PID
        $pipe=New-Object System.IO.Pipes.NamedPipeServerStream($decisionPipeName, [System.IO.Pipes.PipeDirection]::Out, 1, [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous)
        $job=$null
        try {
            # The worker runs the actual elevated transaction; this process supplies the one-shot confirmation.
            $job=Start-Job -ScriptBlock {
                param($source,$requestJson,$operationId,$operationDir,$decisionPipeName,$appProcessId)
                $ErrorActionPreference='Stop'
                & ([scriptblock]::Create($source))
            } -ArgumentList $source,$requestJson,$operationId,$operationDir,$decisionPipeName,$appProcessId
            $connection=$pipe.WaitForConnectionAsync()
            if(-not $connection.Wait(85000)){throw ($mode+': executor did not request confirmation within 85 seconds')}
            $writer=New-Object System.IO.StreamWriter($pipe)
            $writer.AutoFlush=$true
            $writer.WriteLine('keep')
            if(-not (Wait-Job -Job $job -Timeout 30)){throw ($mode+': executor did not finish after confirmation')}
            Receive-Job -Job $job -ErrorAction Stop | Out-Null
        } finally {
            $pipe.Dispose()
            if($job){if($job.State -eq 'Running'){Stop-Job -Job $job};Remove-Job -Job $job -Force}
        }
        $protectedDir=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('NexoSupport\operations\'+$operationId)
        $result=Get-Content -LiteralPath (Join-Path $protectedDir 'result.json') -Raw | ConvertFrom-Json
        if(-not $result.success){throw ($mode+': '+$result.error)}
    }
} finally {
    # Restore original mode even if static application or the tested transition fails.
    $current=Get-NetAdapter -InterfaceIndex $InterfaceIndex
    if([guid]$current.InterfaceGuid -ne $ExpectedGuid){throw 'Identity changed; automatic restoration blocked'}
    if([string](Get-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4).Dhcp -ne 'Enabled') {
        & "$env:WINDIR\System32\netsh.exe" interface ipv4 set address "name=$InterfaceIndex" source=dhcp store=persistent | Out-Null
        if($LASTEXITCODE -ne 0){throw 'Emergency DHCP restoration failed; watchdog remains active'}
    }
    if([string]::IsNullOrWhiteSpace($staticDns)){Set-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -ResetServerAddresses}
    else{Set-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -ServerAddresses @($staticDns -split '[,; ]+' | Where-Object {$_})}
    if([string](Get-NetIPInterface -InterfaceIndex $InterfaceIndex -AddressFamily IPv4).Dhcp -ne 'Enabled'){throw 'DHCP restoration not confirmed'}
    $deadline=(Get-Date).AddSeconds(20)
    do {
        $restoredLease=@(Get-NetIPAddress -AddressFamily IPv4 | Where-Object {$_.InterfaceIndex -eq $InterfaceIndex -and [string]$_.PrefixOrigin -eq 'Dhcp' -and [string]$_.AddressState -eq 'Preferred' -and $_.IPAddress -notlike '169.254.*'})
        if($restoredLease.Count){break}
        Start-Sleep -Milliseconds 500
    } while((Get-Date) -lt $deadline)
    if(-not $restoredLease.Count){throw 'Original DHCP mode enabled but lease unconfirmed; watchdog remains active'}
    Set-Content -LiteralPath $finished -Value 'Original DHCP mode restored'
}
Write-Output "PASS: production static-to-DHCP transition. Evidence: $testRoot"
