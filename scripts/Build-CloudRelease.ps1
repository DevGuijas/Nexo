param(
    [string]$OllamaInstallerPath,
    [string]$OllamaExpectedSha256,
    [string]$OutputDirectory = 'artifacts/cloud-release',
    [string]$ModelRoot = "$env:USERPROFILE\.ollama\models",
    [string]$SigningKeyPath = "$env:USERPROFILE\.nexo-release-signing.pem",
    [switch]$ReuseArchives
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'Nexo.Support/Nexo.Support.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Versão do projeto inválida.' }
$ollama = $null
if ($OllamaInstallerPath) {
    $ollama = (Resolve-Path -LiteralPath $OllamaInstallerPath).Path
    if ($OllamaExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash -LiteralPath $ollama -Algorithm SHA256).Hash -ne $OllamaExpectedSha256) { throw 'SHA-256 do Ollama não corresponde ao pacote oficial escolhido.' }
}
if (!(Test-Path -LiteralPath $SigningKeyPath)) { throw 'Chave privada de publicação ausente.' }
$releaseRoot = Join-Path $projectRoot $OutputDirectory
$releaseDirectory = Join-Path $releaseRoot "releases/$version"
$appStage = Join-Path $releaseRoot 'app-stage'
New-Item -ItemType Directory -Force -Path $releaseDirectory, $appStage | Out-Null
$appArchive = Join-Path $releaseDirectory 'nexo-app.zip'
$modelArchive = Join-Path $releaseDirectory 'qwen3-8b-model.zip'
if (-not $ReuseArchives) {
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $appStage
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o aplicativo.' }
if (Test-Path -LiteralPath $appArchive) { Remove-Item -LiteralPath $appArchive -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($appStage, $appArchive, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$modelManifest = Join-Path $ModelRoot 'manifests/registry.ollama.ai/library/qwen3/8b'
if (!(Test-Path -LiteralPath $modelManifest)) { throw 'Modelo qwen3:8b não está disponível no Ollama local.' }
$modelData = Get-Content -LiteralPath $modelManifest -Raw | ConvertFrom-Json
$digests = @($modelData.config.digest) + @($modelData.layers | ForEach-Object { $_.digest })
if (Test-Path -LiteralPath $modelArchive) { Remove-Item -LiteralPath $modelArchive -Force }
$stream = [System.IO.File]::Open($modelArchive, [System.IO.FileMode]::CreateNew)
try {
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $entry = $zip.CreateEntry('manifests/registry.ollama.ai/library/qwen3/8b', [System.IO.Compression.CompressionLevel]::Optimal)
        $source = [System.IO.File]::OpenRead($modelManifest)
        try { $destination = $entry.Open(); try { $source.CopyTo($destination) } finally { $destination.Dispose() } } finally { $source.Dispose() }
        foreach ($digest in $digests) {
            if ($digest -notmatch '^sha256:[0-9a-f]{64}$') { throw 'Digest do modelo inválido.' }
            $name = $digest.Replace(':','-')
            $blob = Join-Path $ModelRoot "blobs/$name"
            if (!(Test-Path -LiteralPath $blob)) { throw "Blob ausente: $name" }
            Write-Host "Empacotando $name"
            $entry = $zip.CreateEntry("blobs/$name", [System.IO.Compression.CompressionLevel]::NoCompression)
            $source = [System.IO.File]::OpenRead($blob)
            try { $destination = $entry.Open(); try { $source.CopyTo($destination) } finally { $destination.Dispose() } } finally { $source.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
} else {
    if (!(Test-Path -LiteralPath $appArchive) -or !(Test-Path -LiteralPath $modelArchive)) { throw 'Arquivos existentes não foram encontrados para reutilização.' }
}

$ollamaTarget = $null
if ($ollama) {
    $ollamaTarget = Join-Path $releaseDirectory 'OllamaSetup.exe'
    Copy-Item -LiteralPath $ollama -Destination $ollamaTarget -Force
}
function Package([string]$path) {
    $item = Get-Item -LiteralPath $path
    return @{ path = "releases/$version/$($item.Name)"; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $item.Length }
}
$latest = [ordered]@{ schema = 1; version = $version; model = 'qwen3:8b'; app = (Package $appArchive); modelArchive = (Package $modelArchive) }
if ($ollamaTarget) { $latest.ollama = (Package $ollamaTarget) }
$latestPath = Join-Path $releaseRoot 'latest.json'
$latest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $latestPath -Encoding utf8NoBOM
$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([System.IO.File]::ReadAllText($SigningKeyPath))
    $signature = $rsa.SignData([System.IO.File]::ReadAllBytes($latestPath), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [System.IO.File]::WriteAllText((Join-Path $releaseRoot 'latest.sig'), [Convert]::ToBase64String($signature))
} finally { $rsa.Dispose() }
Write-Host "Versão pronta: $latestPath"
Write-Host 'Envie a pasta releases, latest.sig e depois latest.json ao armazenamento HTTPS da Oracle; publicar latest.json por último ativa a nova versão.'
