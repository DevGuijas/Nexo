param(
    [string]$OllamaInstallerPath,
    [string]$OllamaExpectedSha256,
    [string]$OutputDirectory = 'artifacts/cloud-release',
    [string]$ModelRoot = "$env:USERPROFILE\.ollama\models",
    [string]$SigningKeyPath = "$env:USERPROFILE\.nexo-release-signing.pem",
    [switch]$ReuseArchives,
    [switch]$ReusePublishedDependencies,
    [string]$PublishedOrigin = 'https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/'
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use o PowerShell 7 (pwsh) para gerar a versão.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'Nexo.Support/Nexo.Support.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Versão do projeto inválida.' }
$changelog = Get-Content -LiteralPath (Join-Path $projectRoot 'CHANGELOG.md') -Encoding utf8
$heading = $changelog | Where-Object { $_ -match ('^## \[' + [regex]::Escape($version) + '\] — (.+)$') } | Select-Object -First 1
if (-not $heading) { throw "O CHANGELOG.md não tem a versão $version." }
if (($heading -replace ('^## \[' + [regex]::Escape($version) + '\] — '), '').Trim() -notmatch '^\d{4}-\d{2}-\d{2}$') { throw "A versão $version está marcada como não publicada no CHANGELOG.md. Conclua os itens de 'Antes de publicar' e registre a data de publicação." }
$publishedApp = $PublishedOrigin + "releases/$version/nexo-app.zip"
$alreadyPublished = $false
try { Invoke-WebRequest -Uri $publishedApp -Method Head -UseBasicParsing | Out-Null; $alreadyPublished = $true }
catch {
    if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { $alreadyPublished = $false }
    else { throw "Não foi possível confirmar se a versão $version já foi publicada: $($_.Exception.Message)" }
}
if ($alreadyPublished) { throw "A versão $version já está publicada em $publishedApp. Versões publicadas são imutáveis; aumente a versão em Nexo.Support.csproj e no CHANGELOG.md." }
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

if (-not $ReusePublishedDependencies) {
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
}
} else {
    if (!(Test-Path -LiteralPath $appArchive) -or (-not $ReusePublishedDependencies -and !(Test-Path -LiteralPath $modelArchive))) { throw 'Arquivos existentes não foram encontrados para reutilização.' }
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
$latest = [ordered]@{ schema = 1; version = $version; model = 'qwen3:8b'; app = (Package $appArchive) }
if ($ReusePublishedDependencies) {
    $origin = $PublishedOrigin
    $previousContent = (Invoke-WebRequest -Uri ($origin + 'latest.json') -UseBasicParsing).Content
    $previousBytes = if ($previousContent -is [byte[]]) { $previousContent } else { [System.Text.Encoding]::UTF8.GetBytes([string]$previousContent) }
    $signatureContent = (Invoke-WebRequest -Uri ($origin + 'latest.sig') -UseBasicParsing).Content
    $previousSignature = if ($signatureContent -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($signatureContent).Trim() } else { ([string]$signatureContent).Trim() }
    $publicKey = Join-Path $projectRoot 'Nexo.Setup/release-public.pem'
    $verifier = [System.Security.Cryptography.RSA]::Create()
    try {
        $verifier.ImportFromPem([System.IO.File]::ReadAllText($publicKey))
        if (-not $verifier.VerifyData($previousBytes, [Convert]::FromBase64String($previousSignature), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'Manifesto anterior não possui assinatura válida.' }
    } finally { $verifier.Dispose() }
    $previous = [System.Text.Encoding]::UTF8.GetString($previousBytes) | ConvertFrom-Json
    if ($previous.model -ne 'qwen3:8b' -or !$previous.modelArchive -or !$previous.ollama) { throw 'Pacotes publicados não podem ser reutilizados.' }
    $latest.modelArchive = $previous.modelArchive
    $latest.ollama = $previous.ollama
} else {
    $latest.modelArchive = Package $modelArchive
    if ($ollamaTarget) { $latest.ollama = Package $ollamaTarget }
}
$latestPath = Join-Path $releaseRoot 'latest.json'
$latest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $latestPath -Encoding utf8NoBOM
$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([System.IO.File]::ReadAllText($SigningKeyPath))
    $signature = $rsa.SignData([System.IO.File]::ReadAllBytes($latestPath), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [System.IO.File]::WriteAllText((Join-Path $releaseRoot 'latest.sig'), [Convert]::ToBase64String($signature))
} finally { $rsa.Dispose() }
$archive = [System.IO.Compression.ZipFile]::OpenRead($appArchive)
try {
    $executable = $archive.GetEntry('Nexo.exe')
    if (-not $executable) { throw 'O pacote do aplicativo não contém Nexo.exe.' }
    $stream = $executable.Open()
    try { $executableHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
    $executableBytes = $executable.Length
} finally { $archive.Dispose() }
$appHash = (Get-FileHash -LiteralPath $appArchive -Algorithm SHA256).Hash
$appBytes = (Get-Item -LiteralPath $appArchive).Length
Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Value @("$executableHash  Nexo.exe", "$appHash  nexo-app.zip") -Encoding ascii
Write-Host "Versão pronta: $latestPath"
Write-Host "Registre no CHANGELOG.md: Nexo.exe $executableBytes bytes, SHA-256 $executableHash; releases/$version/nexo-app.zip $appBytes bytes, SHA-256 $appHash; commit $(git -C $projectRoot rev-parse --short HEAD)."
Write-Host 'Envie a pasta releases, latest.sig e depois latest.json ao armazenamento HTTPS da Oracle; publicar latest.json por último ativa a nova versão.'
