param(
    [string]$OllamaInstallerPath,
    [string]$OllamaExpectedSha256,
    [string]$OutputDirectory = 'artifacts/cloud-release',
    [string]$ModelRoot = "$env:USERPROFILE\.ollama\models",
    [string]$SigningKeyPath = "$env:USERPROFILE\.nexo-release-signing.pem",
    [string]$CodeSigningThumbprint,
    [string]$TimestampServer,
    [string]$InstallerPath,
    [switch]$ReuseArchives,
    [switch]$ReusePublishedDependencies
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
$publicationDate = [datetime]::MinValue
if (-not [datetime]::TryParseExact(($heading -replace ('^## \[' + [regex]::Escape($version) + '\] — '), '').Trim(), 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$publicationDate)) { throw "A versão $version está marcada como não publicada no CHANGELOG.md. Conclua os itens de 'Antes de publicar' e registre a data de publicação." }
. (Join-Path $PSScriptRoot 'Release-Guard.ps1')
$publicKey = Join-Path $projectRoot 'Nexo.Setup/release-public.pem'
$published = Assert-NexoNewRelease -Version $version -PublicKeyPath $publicKey
Write-Host "Versão publicada: $($published.Version); gerando a $version."
$ollama = $null
if ($OllamaInstallerPath) {
    $ollama = (Resolve-Path -LiteralPath $OllamaInstallerPath).Path
    if ($OllamaExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileHash -LiteralPath $ollama -Algorithm SHA256).Hash -ne $OllamaExpectedSha256) { throw 'SHA-256 do Ollama não corresponde ao pacote oficial escolhido.' }
}
if (!(Test-Path -LiteralPath $SigningKeyPath)) { throw 'Chave privada de publicação ausente.' }
if (-not $CodeSigningThumbprint) { throw 'Informe -CodeSigningThumbprint para assinar e conferir Nexo.exe antes de publicar.' }
if (-not $InstallerPath) { throw 'Informe -InstallerPath para conferir NexoSetup.exe antes de publicar.' }
$installer = Get-Item -LiteralPath $InstallerPath -ErrorAction Stop
if ($installer.Name -ne 'NexoSetup.exe') { throw 'O instalador deve ser NexoSetup.exe.' }
& (Join-Path $PSScriptRoot 'Sign-NexoArtifacts.ps1') -Paths $installer.FullName -Thumbprint $CodeSigningThumbprint -VerifyOnly
$releaseRoot = Join-Path $projectRoot $OutputDirectory
$releaseDirectory = Join-Path $releaseRoot "releases/$version"
$appStage = Join-Path $releaseRoot 'app-stage'
New-Item -ItemType Directory -Force -Path $releaseDirectory, $appStage | Out-Null
$appArchive = Join-Path $releaseDirectory 'nexo-app.zip'
$modelArchive = Join-Path $releaseDirectory 'qwen3-8b-model.zip'
# DLLs nativas do WPF que o single-file sem extração deixa ao lado do Nexo.exe (#142, B1).
$nativeBesideExe = @('wpfgfx_cor3.dll', 'PresentationNative_cor3.dll')
if (-not $ReuseArchives) {
# A pasta de preparo começa vazia: o ZIP leva a pasta inteira, e nada de uma publicação anterior pode entrar nele.
if (Test-Path -LiteralPath $appStage) { Remove-Item -LiteralPath $appStage -Recurse -Force }
New-Item -ItemType Directory -Path $appStage | Out-Null
# Single-file sem IncludeNativeLibrariesForSelfExtract, como o comando do README (#142, B1): nada é extraído ao abrir. Com a
# extração, o host gravaria as DLLs nativas no %TEMP% antes do Main (para a tarefa SYSTEM do leitor de sensores, em
# C:\Windows\Temp\.net, uma pasta que o usuário controla), e o --sensor-loop recusaria esse Nexo.exe com o código 2.
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $appStage
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o aplicativo.' }
foreach ($native in $nativeBesideExe) {
    if (!(Test-Path -LiteralPath (Join-Path $appStage $native) -PathType Leaf)) { throw "A publicação não deixou $native ao lado do Nexo.exe; o pacote não seria aceito pelo leitor de sensores." }
}
# Só o Nexo.exe recebe a assinatura da equipe. As DLLs nativas ao lado dele vêm do runtime da Microsoft, já assinadas por ela.
& (Join-Path $PSScriptRoot 'Sign-NexoArtifacts.ps1') -Paths (Join-Path $appStage 'Nexo.exe') -Thumbprint $CodeSigningThumbprint -TimestampServer $TimestampServer
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

# Uma tentativa reaproveitada só pode publicar um ZIP cujo executável já esteja assinado.
$signatureCheck = Join-Path ([System.IO.Path]::GetTempPath()) ('NexoSignatureCheck-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $signatureCheck | Out-Null
try {
    $archiveToCheck = [System.IO.Compression.ZipFile]::OpenRead($appArchive)
    try {
        $entryToCheck = $archiveToCheck.GetEntry('Nexo.exe')
        if (-not $entryToCheck) { throw 'O pacote do aplicativo não contém Nexo.exe.' }
        # Um ZIP reaproveitado de uma publicação com extração não tem as nativas ao lado do Nexo.exe (#142, B1).
        foreach ($native in $nativeBesideExe) {
            if (-not $archiveToCheck.GetEntry($native)) { throw "O pacote do aplicativo não contém $native ao lado do Nexo.exe; gere-o de novo sem extração (sem -ReuseArchives)." }
        }
        $executableToCheck = Join-Path $signatureCheck 'Nexo.exe'
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entryToCheck, $executableToCheck)
        & (Join-Path $PSScriptRoot 'Sign-NexoArtifacts.ps1') -Paths $executableToCheck -Thumbprint $CodeSigningThumbprint -VerifyOnly
    } finally { $archiveToCheck.Dispose() }
} finally { Remove-Item -LiteralPath $signatureCheck -Recurse -Force }

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
    # Manifesto publicado já lido e com a assinatura conferida por Assert-NexoNewRelease.
    $previous = $published.Manifest
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
# SHA256SUMS.txt: o Nexo.exe primeiro, depois cada outro arquivo do ZIP (as DLLs nativas ao lado dele, ThirdParty…) pelo caminho
# dentro do pacote, e por último o próprio ZIP.
$sums = [System.Collections.Generic.List[string]]::new()
$archive = [System.IO.Compression.ZipFile]::OpenRead($appArchive)
try {
    $executable = $archive.GetEntry('Nexo.exe')
    if (-not $executable) { throw 'O pacote do aplicativo não contém Nexo.exe.' }
    $stream = $executable.Open()
    try { $executableHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
    $executableBytes = $executable.Length
    $sums.Add("$executableHash  Nexo.exe")
    foreach ($entry in @($archive.Entries | Where-Object { $_.FullName -ne 'Nexo.exe' -and -not $_.FullName.EndsWith('/') } | Sort-Object FullName)) {
        $stream = $entry.Open()
        try { $sums.Add([Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)) + '  ' + $entry.FullName) } finally { $stream.Dispose() }
    }
} finally { $archive.Dispose() }
$appHash = (Get-FileHash -LiteralPath $appArchive -Algorithm SHA256).Hash
$appBytes = (Get-Item -LiteralPath $appArchive).Length
$sums.Add("$appHash  nexo-app.zip")
Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Value $sums -Encoding ascii
Write-Host "Versão pronta: $latestPath"
Write-Host "Registre no CHANGELOG.md: Nexo.exe $executableBytes bytes, SHA-256 $executableHash; releases/$version/nexo-app.zip $appBytes bytes, SHA-256 $appHash; commit $(git -C $projectRoot rev-parse --short HEAD)."
Write-Host 'Envie a pasta releases, latest.sig e depois latest.json ao armazenamento HTTPS da Oracle; publicar latest.json por último ativa a nova versão.'
