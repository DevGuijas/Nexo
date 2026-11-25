param(
    [string]$OutputDirectory = 'artifacts/preview-release',
    [string]$SigningKeyPath = (Join-Path $env:USERPROFILE '.nexo-preview-signing.pem')
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use o PowerShell 7.' }
. (Join-Path $PSScriptRoot 'PreviewManifest.ps1')

$projectRoot = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Nexo.Support/Nexo.Support.csproj') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Versão do aplicativo inválida.' }
$setupProject = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Nexo.Setup/Nexo.Setup.csproj') -Raw)
$installerVersion = $setupProject.SelectSingleNode("//Version[contains(@Condition, 'NexoPreview')]").InnerText
if ($installerVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Versão do instalador de prévia inválida.' }
$publicKey = Join-Path $projectRoot 'Nexo.Setup/preview-public.pem'
if (!(Test-Path -LiteralPath $SigningKeyPath) -or !(Test-Path -LiteralPath $publicKey)) { throw 'Par de chaves da prévia ausente.' }

$origin = 'https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/'
$stableResponse = Invoke-WebRequest -Uri ($origin + 'latest.json') -MaximumRedirection 0
$stableBytes = [Text.Encoding]::UTF8.GetBytes([string]$stableResponse.Content)
$stableSignature = [string](Invoke-WebRequest -Uri ($origin + 'latest.sig') -MaximumRedirection 0).Content
if (-not (Test-NexoPreviewSignature -ManifestBytes $stableBytes -SignatureText $stableSignature -PublicKeyPath (Join-Path $projectRoot 'Nexo.Setup/release-public.pem'))) {
    throw 'Manifesto estável publicado não possui assinatura válida.'
}
$stable = [Text.Encoding]::UTF8.GetString($stableBytes) | ConvertFrom-Json
if ([version]$version -le [version]$stable.version -or $stable.model -ne 'qwen3:8b' -or !$stable.modelArchive -or !$stable.ollama) {
    throw 'A versão da prévia deve ser nova e reutilizar dependências verificadas da versão estável.'
}

$appRemote = Invoke-WebRequest -Uri ($origin + "releases/$version/nexo-app.zip") -Method Head -SkipHttpErrorCheck -MaximumRedirection 0
if ([int]$appRemote.StatusCode -ne 404) { throw "O caminho releases/$version/nexo-app.zip não está livre (HTTP $($appRemote.StatusCode))." }
foreach ($package in @($stable.modelArchive, $stable.ollama)) {
    $local = Join-Path $projectRoot ('artifacts/cloud-release/' + $package.path)
    if (!(Test-Path -LiteralPath $local)) { throw "Dependência local ausente: $($package.path)" }
    $item = Get-Item -LiteralPath $local
    if ($item.Length -ne $package.bytes -or (Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash -ne $package.sha256) {
        throw "Dependência local diferente do manifesto estável: $($package.path)"
    }
    $remote = Invoke-WebRequest -Uri ($origin + $package.path) -Method Head -MaximumRedirection 0
    if ((Get-NexoContentLength -HeaderValues $remote.Headers.'Content-Length') -ne [long]$package.bytes) { throw "Tamanho remoto diferente: $($package.path)" }
}

$releaseRoot = Join-Path $projectRoot $OutputDirectory
$packageDirectory = Join-Path $releaseRoot "releases/$version"
$manifestDirectory = Join-Path $releaseRoot 'preview'
New-Item -ItemType Directory -Force -Path $packageDirectory, $manifestDirectory | Out-Null
$appArchive = Join-Path $packageDirectory 'nexo-app.zip'
if (Test-Path -LiteralPath $appArchive) { throw 'Pacote local já existe; uma versão publicada deve ser imutável.' }
$stage = Join-Path $releaseRoot ('stage-' + [guid]::NewGuid().ToString('N'))
$appStage = Join-Path $stage 'app'
$setupStage = Join-Path $stage 'setup'
$dotnet = Get-Command dotnet -ErrorAction Stop

& $dotnet.Source publish (Join-Path $projectRoot 'Nexo.Support/Nexo.Support.csproj') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' -o $appStage
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o aplicativo.' }
if (!(Test-Path -LiteralPath (Join-Path $appStage 'Nexo.exe'))) { throw 'Nexo.exe ausente do build.' }
foreach ($native in @('wpfgfx_cor3.dll', 'PresentationNative_cor3.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $appStage $native) -PathType Leaf)) { throw "DLL nativa ausente ao lado do Nexo.exe: $native" }
}
[IO.Compression.ZipFile]::CreateFromDirectory($appStage, $appArchive, [IO.Compression.CompressionLevel]::Optimal, $false)

& $dotnet.Source publish (Join-Path $projectRoot 'Nexo.Setup/Nexo.Setup.csproj') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:NexoPreview=true' -o $setupStage
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o instalador de prévia.' }
$installerSource = Join-Path $setupStage 'NexoSetup.exe'
if (!(Test-Path -LiteralPath $installerSource)) { throw 'NexoSetup.exe ausente do build.' }
$installerTarget = Join-Path $releaseRoot "NexoSetup-Preview-$installerVersion.exe"
if (Test-Path -LiteralPath $installerTarget) { throw 'Instalador local já existe; use uma nova versão para não substituir uma publicação.' }
Copy-Item -LiteralPath $installerSource -Destination $installerTarget -ErrorAction Stop

$manifest = New-NexoPreviewManifest -Version $version -AppPath $appArchive -ModelArchive $stable.modelArchive -Ollama $stable.ollama
$manifestPath = Join-Path $manifestDirectory 'latest.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$bytes = [IO.File]::ReadAllBytes($manifestPath)
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText($SigningKeyPath))
    $signature = [Convert]::ToBase64String($rsa.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1))
} finally { $rsa.Dispose() }
if (-not (Test-NexoPreviewSignature -ManifestBytes $bytes -SignatureText $signature -PublicKeyPath $publicKey)) {
    throw 'A chave privada não corresponde à chave pública embutida no instalador.'
}
$signaturePath = Join-Path $manifestDirectory 'latest.sig'
[IO.File]::WriteAllText($signaturePath, $signature)
Write-Host "Prévia $version pronta. Envie o ZIP, o instalador e latest.sig antes de latest.json."
Write-Host "App: $appArchive $($manifest.app.bytes) bytes SHA-256 $($manifest.app.sha256)"
Write-Host "Instalador: $installerTarget $((Get-Item $installerTarget).Length) bytes SHA-256 $((Get-FileHash $installerTarget -Algorithm SHA256).Hash)"
Write-Host "Manifesto: $manifestPath"
