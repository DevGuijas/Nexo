# Travas de publicação do Nexo (#21), usadas por Build-CloudRelease.ps1 antes de compilar ou empacotar.
# A origem é fixa: é a mesma do Nexo.Setup (DefaultManifestUrl) e não pode ser trocada por parâmetro, para que
# nenhuma opção de linha de comando desligue a comparação com a versão publicada.
# Requer PowerShell 7 (RSA.ImportFromPem).
$NexoPublishedOrigin = 'https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/'

function ConvertTo-NexoBytes($content) {
    if ($content -is [byte[]]) { return , $content }
    return , [System.Text.Encoding]::UTF8.GetBytes([string]$content)
}

# Lê um endereço da origem. Erro de rede vira falha explícita; o código HTTP volta para quem chamou decidir.
function Invoke-NexoPublishedRequest([scriptblock]$Fetch, [string]$Uri, [string]$Method) {
    try { $response = & $Fetch $Uri $Method }
    catch { throw "Não foi possível consultar a versão publicada em ${Uri}: $($_.Exception.Message)" }
    if ($null -eq $response) { throw "Não foi possível consultar a versão publicada em ${Uri}: sem resposta." }
    return $response
}

# Recusa a geração dos pacotes quando:
#  - o latest.json publicado não pode ser lido, ou a assinatura dele (latest.sig) não confere com a chave pública;
#  - a versão do projeto não é MAIOR que a publicada (comparação numérica: 1.10.0 > 1.9.0);
#  - releases/<versão>/nexo-app.zip já existe na origem (versões publicadas são imutáveis).
# Devolve o manifesto publicado já verificado, para quem precisar reutilizar os pacotes dele.
function Assert-NexoNewRelease {
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$PublicKeyPath,
        [scriptblock]$Fetch = {
            param($Uri, $Method)
            $r = Invoke-WebRequest -Uri $Uri -Method $Method -UseBasicParsing -SkipHttpErrorCheck
            @{ Status = [int]$r.StatusCode; Content = $r.Content }
        }
    )
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versão do projeto inválida: $Version." }
    $manifestUri = $NexoPublishedOrigin + 'latest.json'
    $signatureUri = $NexoPublishedOrigin + 'latest.sig'
    $manifest = Invoke-NexoPublishedRequest $Fetch $manifestUri 'Get'
    if ($manifest.Status -ne 200) { throw "Não foi possível ler o latest.json publicado (HTTP $($manifest.Status)); sem ele não há como confirmar que a versão $Version é nova." }
    $signature = Invoke-NexoPublishedRequest $Fetch $signatureUri 'Get'
    if ($signature.Status -ne 200) { throw "Não foi possível ler o latest.sig publicado (HTTP $($signature.Status)); sem ele não há como confirmar que a versão $Version é nova." }
    $manifestBytes = ConvertTo-NexoBytes $manifest.Content
    $signatureText = [System.Text.Encoding]::UTF8.GetString((ConvertTo-NexoBytes $signature.Content)).Trim()
    $verifier = [System.Security.Cryptography.RSA]::Create()
    try {
        $verifier.ImportFromPem([System.IO.File]::ReadAllText($PublicKeyPath))
        $valid = $false
        try { $valid = $verifier.VerifyData($manifestBytes, [Convert]::FromBase64String($signatureText), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1) }
        catch [System.FormatException] { $valid = $false }
        if (-not $valid) { throw 'O latest.json publicado não tem assinatura válida; a publicação foi interrompida.' }
    } finally { $verifier.Dispose() }
    $published = [System.Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
    if ([string]$published.version -notmatch '^\d+\.\d+\.\d+$') { throw "O latest.json publicado traz uma versão inválida: $($published.version)." }
    if ([version]$Version -le [version]$published.version) {
        throw "A versão $Version não é maior que a publicada ($($published.version)). Versões publicadas são imutáveis; aumente a versão em Nexo.Support.csproj e no CHANGELOG.md."
    }
    $appUri = $NexoPublishedOrigin + "releases/$Version/nexo-app.zip"
    $app = Invoke-NexoPublishedRequest $Fetch $appUri 'Head'
    if ($app.Status -eq 200) { throw "A versão $Version já está publicada em $appUri. Versões publicadas são imutáveis; aumente a versão em Nexo.Support.csproj e no CHANGELOG.md." }
    if ($app.Status -ne 404) { throw "Não foi possível confirmar se a versão $Version já foi publicada em $appUri (HTTP $($app.Status))." }
    return [pscustomobject]@{ Version = [string]$published.version; Manifest = $published }
}
