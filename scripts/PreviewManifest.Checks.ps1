$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PreviewManifest.ps1')
if ((Get-NexoContentLength -HeaderValues @('1657983656')) -ne 1657983656L) {
    throw 'Content-Length da Oracle não foi convertido.'
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('nexo-preview-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $app = Join-Path $temporary 'nexo-app.zip'
    [IO.File]::WriteAllBytes($app, (New-Object byte[] 2048))
    $hash = 'a' * 64
    $model = @{ path = 'releases/1.6.2/qwen3-8b-model.zip'; sha256 = $hash; bytes = 5225390118 }
    $ollama = @{ path = 'releases/1.6.2/OllamaSetup.exe'; sha256 = $hash; bytes = 1657983656 }
    $manifest = New-NexoPreviewManifest -Version '1.7.0' -AppPath $app -ModelArchive $model -Ollama $ollama
    if ($manifest.schema -ne 1 -or $manifest.version -ne '1.7.0' -or $manifest.model -ne 'qwen3:8b' -or
        $manifest.app.path -ne 'releases/1.7.0/nexo-app.zip' -or $manifest.app.bytes -ne 2048 -or
        $manifest.modelArchive.path -ne $model.path -or $manifest.ollama.path -ne $ollama.path) {
        throw 'Manifesto não mantém os caminhos de pacote esperados na raiz do bucket.'
    }
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $rsa.KeySize = 3072
        $public = Join-Path $temporary 'public.pem'
        [IO.File]::WriteAllText($public, $rsa.ExportSubjectPublicKeyInfoPem())
        $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 5))
        $signature = [Convert]::ToBase64String($rsa.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1))
        if (-not (Test-NexoPreviewSignature -ManifestBytes $bytes -SignatureText $signature -PublicKeyPath $public)) { throw 'Assinatura válida recusada.' }
        $bytes[0] = $bytes[0] -bxor 1
        if (Test-NexoPreviewSignature -ManifestBytes $bytes -SignatureText $signature -PublicKeyPath $public) { throw 'Manifesto alterado aceito.' }
    } finally { $rsa.Dispose() }
    'PreviewManifest checks passed.'
} finally { Remove-Item -LiteralPath $temporary -Recurse -Force }
