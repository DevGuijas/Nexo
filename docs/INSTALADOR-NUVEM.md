# Instalador e publicação na Oracle

## O que é entregue

`NexoSetup.exe` é um instalador Windows x64 independente do .NET instalado no computador. Ao abrir, ele lê `latest.json` no bucket Oracle `nexo-releases` (região São Paulo), exige `latest.sig` válido e baixa os três arquivos da versão indicada: aplicativo, runtime Ollama e modelo `qwen3:8b`. Cada arquivo precisa ter exatamente o tamanho e o SHA-256 registrados no manifesto assinado. O instalador recusa redirecionamentos, caminhos fora da versão e arquivos ZIP que escapem da pasta de instalação.

O aplicativo é instalado em `%LOCALAPPDATA%\Programs\Nexo` e ganha atalho no menu Iniciar. O Ollama é instalado somente se ainda não estiver disponível. Os blobs do modelo vão para `%USERPROFILE%\.ollama\models`; cada blob é validado pelo digest e o manifesto do modelo só é ativado por último. A instalação anterior do aplicativo é preservada em `Nexo.previous` para recuperação manual. Reexecutar o mesmo instalador consulta o manifesto atual: **novas instalações obtêm a versão mais recente publicada**. Instalações existentes são atualizadas ao reexecutá-lo; não há serviço de atualização automática em segundo plano.

URL do manifesto: `https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/latest.json`.

Download público do instalador: [NexoSetup.exe](https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/NexoSetup.exe). SHA-256 da versão publicada em 22/10/2026: `B6A40BA7D82E9924214B334FC61D76EFE0BD1996B24EC2C1A435271A18D3CDA1`. O arquivo remoto foi baixado de volta e seu hash coincidiu com o arquivo local. Esse executável consulta `latest.json` em cada execução.

## Gerar uma versão

1. Altere `<Version>` em `Nexo.Support/Nexo.Support.csproj` e o título da janela. Não substitua os ZIPs de uma versão já publicada.
2. Confirme que `qwen3:8b` está instalado no Ollama local e obtenha `OllamaSetup.exe` do site oficial ou do pacote `Ollama.Ollama` do WinGet. Verifique a assinatura do fornecedor e o SHA-256 antes de publicar.
3. Execute a suíte e crie os pacotes:

```powershell
dotnet run --project Nexo.Checks -c Release
dotnet run --project Nexo.Setup.Checks -c Release
./scripts/Build-CloudRelease.ps1 `
  -OllamaInstallerPath artifacts/ollama-winget/Ollama_0.40.7_User_X64_inno_en-US.exe `
  -OllamaExpectedSha256 6394E61DAEDC2DE736C9F76CBC58F30F9241BA1EB4C2F79EF75E833168DB8A60
dotnet publish Nexo.Setup/Nexo.Setup.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/NexoSetup-1.6.2
```

Os arquivos ficam em `artifacts/cloud-release/releases/<versão>/`, `latest.sig` e `latest.json`. Envie os arquivos de `releases/<versão>/` à Oracle e confira seus tamanhos. Envie `latest.sig` e **por último** `latest.json`; este último passo ativa a versão para novos instaladores. Depois execute `NexoSetup.exe --check` e confira se o manifesto assinado e cada arquivo respondem por HTTPS com o tamanho esperado.

O par de chaves de publicação é RSA 3072. A chave pública fica embutida no instalador em `Nexo.Setup/release-public.pem`; a privada deste ambiente fica em `%USERPROFILE%\.nexo-release-signing.pem` e **não deve entrar no Git nem no bucket**. Guarde backup seguro da chave privada. Caso ela seja perdida ou comprometida, publique um novo instalador com outra chave pública. O manifesto assinado evita que apenas alterar arquivos no bucket faça o instalador executar um pacote não autorizado.

## Infraestrutura e limites

O bucket foi criado inicialmente como privado e, com autorização do proprietário, configurado como **público sem listagem de objetos**. Qualquer pessoa com a URL pode baixar os arquivos, mas não listar o conteúdo nem modificá-lo. Armazene nele apenas pacotes destinados à distribuição. A cota gratuita observada na conta é de 20 GiB; esta versão usa cerca de 6,5 GiB. Consulte a cota antes de publicar versões adicionais. O domínio `gs-tecnologia.top` continua apontando para o estacionamento do Superdomínios; o link de distribuição usa o HTTPS da Oracle diretamente.

O `NexoSetup.exe` e o `Nexo.exe` ainda não possuem assinatura de código da equipe. O instalador valida o manifesto e os pacotes baixados, e o `OllamaSetup.exe` oficial desta versão tem assinatura válida da Ollama Inc.; isso não elimina avisos do Windows sobre o executável Nexo sem certificado de editor. A instalação do driver NVIDIA é um fluxo separado dentro do aplicativo, com autorização própria.
