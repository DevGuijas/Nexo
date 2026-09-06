# expectedGuid / expectedIndex are supplied only by the executor from its own latest inventory.
function Find-ObservedAdapter {
    $matches = @(Get-NetAdapter -ErrorAction Stop | Where-Object {[string]$_.InterfaceGuid -eq $expectedGuid})
    if ($matches.Count -ne 1 -or [int]$matches[0].ifIndex -ne $expectedIndex) { throw 'A identidade da interface mudou. Faça uma nova consulta antes de ativar.' }
    $matches[0]
}
$before = Find-ObservedAdapter
$beforeStatus = [string]$before.Status
if ($beforeStatus -ne 'Disabled') {
    [pscustomobject]@{Changed=$false;Name=$before.Name;Before=$beforeStatus;After=$beforeStatus;Note='A interface não está desativada. Não foi alterada; investigue o link e a configuração IP.'}
} else {
    $before | Enable-NetAdapter -Confirm:$false -ErrorAction Stop
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 500
        $after = Find-ObservedAdapter
    } while ([string]$after.Status -eq 'Disabled' -and (Get-Date) -lt $deadline)
    if ([string]$after.Status -eq 'Disabled') { throw 'O Windows ainda reporta a interface desativada após a tentativa.' }
    if ([string]$after.Status -notin 'Up','Disconnected') { throw ('Ativação não confirmada. Estado atual: '+$after.Status) }
    [pscustomobject]@{Changed=$true;Name=$after.Name;Index=[int]$after.ifIndex;Guid=[string]$after.InterfaceGuid;Before=$beforeStatus;After=[string]$after.Status;InternetVerified=$false;Note='Interface habilitada. Consulte IP, gateway e DNS novamente e teste a conectividade; isso ainda não comprova acesso à internet.'}
}
