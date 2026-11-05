param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\Nexo.Support\Nexo.Support.csproj'
dotnet build $project -c $Configuration --no-restore -v:q
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar a prévia ($LASTEXITCODE)." }

$output = Join-Path $PSScriptRoot "..\Nexo.Support\bin\$Configuration\net8.0-windows"
$exe = Join-Path $output 'Nexo.exe'
$report = Join-Path $output 'ui-checks.txt'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$preview = [IO.Path]::GetFullPath((Join-Path $output '.preview-data'))
if (-not $preview.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Pasta de prévia fora do worktree; limpeza bloqueada.'
}
if (Test-Path -LiteralPath $preview) {
    if ((Get-Item -LiteralPath $preview -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Pasta de prévia é um link; limpeza bloqueada.'
    }
    Remove-Item -LiteralPath $preview -Recurse -Force
}
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
$process = Start-Process -FilePath $exe -ArgumentList '--screenshot' -WindowStyle Hidden -Wait -PassThru
if (-not (Test-Path -LiteralPath $report)) { throw 'A prévia não gerou ui-checks.txt.' }
$result = Get-Content -LiteralPath $report -Raw
if ($process.ExitCode -ne 0 -or $result.StartsWith('Falha na prévia:')) { throw $result }
Write-Output $result.Trim()
