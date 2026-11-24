function Get-NexoContentLength {
    param([Parameter(Mandatory)][string[]]$HeaderValues)
    if ($HeaderValues.Count -ne 1 -or $HeaderValues[0] -notmatch '^\d+$') { throw 'Content-Length remoto inválido.' }
    return [long]::Parse($HeaderValues[0], [Globalization.CultureInfo]::InvariantCulture)
}

function New-NexoPreviewManifest {
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$AppPath,
        [Parameter(Mandatory)]$ModelArchive,
        [Parameter(Mandatory)]$Ollama
    )
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Versão inválida.' }
    $file = Get-Item -LiteralPath $AppPath -ErrorAction Stop
    if ($file.Length -lt 1000 -or $file.Length -gt 8000000000) { throw 'Tamanho do aplicativo inválido.' }
    foreach ($package in @($ModelArchive, $Ollama)) {
        if ($package.path -notmatch '^releases/\d+\.\d+\.\d+/[A-Za-z0-9_.-]+$' -or
            $package.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $package.bytes -lt 1000 -or $package.bytes -gt 8000000000) { throw 'Dependência publicada inválida.' }
    }
    return [ordered]@{
        schema = 1
        version = $Version
        model = 'qwen3:8b'
        app = [ordered]@{
            path = "releases/$Version/nexo-app.zip"
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes = $file.Length
        }
        modelArchive = $ModelArchive
        ollama = $Ollama
    }
}

function Test-NexoPreviewSignature {
    param(
        [Parameter(Mandatory)][byte[]]$ManifestBytes,
        [Parameter(Mandatory)][string]$SignatureText,
        [Parameter(Mandatory)][string]$PublicKeyPath
    )
    try { $signature = [Convert]::FromBase64String($SignatureText.Trim()) }
    catch [FormatException] { return $false }
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem([IO.File]::ReadAllText($PublicKeyPath))
        return $rsa.VerifyData($ManifestBytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    } finally { $rsa.Dispose() }
}
