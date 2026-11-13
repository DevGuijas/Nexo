param(
    [string[]]$Paths,
    [string]$Thumbprint,
    [string]$TimestampServer,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { throw 'A assinatura Authenticode exige Windows.' }
if (-not $Paths -or $Paths.Count -eq 0) { throw 'Informe ao menos um executável em -Paths.' }

$resolved = foreach ($path in $Paths) {
    $item = Get-Item -LiteralPath $path -ErrorAction Stop
    if ($item.PSIsContainer -or $item.Extension -ine '.exe') { throw "Esperado um arquivo .exe: $path" }
    $item.FullName
}

if ($Thumbprint) {
    $Thumbprint = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($Thumbprint -notmatch '^[0-9A-F]{40}$') { throw 'Thumbprint do certificado inválido.' }
}

function Assert-Signature([string]$path, [string]$expectedThumbprint) {
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate) {
        throw "O arquivo não possui assinatura Authenticode válida: $path ($($signature.Status): $($signature.StatusMessage))."
    }
    if (-not $signature.TimeStamperCertificate) { throw "A assinatura não possui carimbo de tempo: $path" }
    if ($expectedThumbprint -and $signature.SignerCertificate.Thumbprint -ne $expectedThumbprint) {
        throw "O certificado da assinatura não corresponde ao esperado: $path"
    }
    $cert = $signature.SignerCertificate
    [pscustomobject]@{
        Path = $path
        Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        Issuer = $cert.Issuer
        Subject = $cert.Subject
        ValidFrom = $cert.NotBefore.ToString('yyyy-MM-dd')
        ValidUntil = $cert.NotAfter.ToString('yyyy-MM-dd')
        Thumbprint = $cert.Thumbprint
        TimeStamper = $signature.TimeStamperCertificate.Subject
    }
}

if ($VerifyOnly) {
    foreach ($path in $resolved) { Assert-Signature $path $Thumbprint }
    return
}

if (-not $Thumbprint) { throw 'Informe -Thumbprint do certificado de assinatura de código.' }
if ($TimestampServer -notmatch '^http://[^\s]+$') { throw 'Informe -TimestampServer com URL HTTP de uma autoridade de carimbo de tempo.' }

$certificate = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert |
    Where-Object Thumbprint -EQ $Thumbprint | Select-Object -First 1
if (-not $certificate -or -not $certificate.HasPrivateKey) {
    throw "Certificado de assinatura de código com chave privada não encontrado: $Thumbprint."
}
$now = Get-Date
if ($now -lt $certificate.NotBefore -or $now -gt $certificate.NotAfter) { throw 'O certificado de assinatura de código está fora da validade.' }
$chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
try {
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
    $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(30)
    if (-not $chain.Build($certificate)) {
        $reason = ($chain.ChainStatus | ForEach-Object { $_.StatusInformation.Trim() }) -join '; '
        throw "A cadeia do certificado de assinatura de código não é confiável: $reason"
    }
} finally { $chain.Dispose() }

foreach ($path in $resolved) {
    $signed = Set-AuthenticodeSignature -LiteralPath $path -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer -IncludeChain All
    if ($signed.Status -ne 'Valid') { throw "Falha ao assinar $path`: $($signed.StatusMessage)" }
    Assert-Signature $path $Thumbprint
}
