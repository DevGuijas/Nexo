# Componentes do Windows para programas (#150)

Cartão 04 de **Ferramentas**. Ele verifica, só lendo o registro e os arquivos, o Visual C++ Redistributable (2005 a 2015–2022, x86 e x64), as DLLs do DirectX End-User Runtime de junho de 2010 e a versão do .NET Framework. Depois instala só o que o usuário marcar, sempre pelo instalador oficial da Microsoft.

A ação existe só no menu. O assistente não oferece nem executa a instalação pelo chat: a trava `AgentProtocol.RuntimeInstallReason` recusa a oferta, e o prompt manda indicar o cartão.

O Nexo não mexe em antivírus, Defender, anticheat nem serviços do Windows. Não usa repacks, espelhos, pacotes "all in one" nem o DirectX SDK.

## Decisões

1. **Hash fixado ou versão atual:**
   - Os instaladores de URL fixa e conteúdo imutável (VC++ 2005, 2008, 2010, 2012 e 2013 e o `dxwebsetup.exe`) têm URL, tamanho e SHA-256 fixados em `Nexo.Support/RuntimeCatalog.cs`.
   - O VC++ v14 (2015–2022 e posteriores) usa o link permanente da Microsoft, que muda de versão. O arquivo é aceito com a assinatura Authenticode `Valid` do assinante `CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US`, com a cadeia terminando na raiz `Microsoft Root Certificate Authority 2011` e com tamanho até 64 MB.
2. **VC++ antigos (2005 a 2013):** só ganham caixa de seleção quando faltam, e vêm desmarcados. O v14 que falta vem marcado. O DirectX que falta também vem desmarcado: o pedido da #150 não define o padrão dele, e o cartão instala só o que o usuário marca.
3. **DirectX:** o instalador web oficial `dxwebsetup.exe` (9.29.1974.1), com `/Q`. Ele tem 288 KB e baixa o resto da própria Microsoft.
4. **Um UAC por item:** cada instalador roda por `runas`, um de cada vez. Um executor elevado único para vários itens exigiria um modo elevado do Nexo recebendo caminhos de arquivo. Isso abre a troca do arquivo entre a conferência e a execução, por isso ficou de fora.

## Fontes oficiais

- [Latest supported Visual C++ Redistributable downloads](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist): v14, 2013, 2012, 2010 e 2008.
- [Microsoft Visual C++ 2005 Service Pack 1 Redistributable Package MFC Security Update](https://www.microsoft.com/download/details.aspx?id=26347): 2005. A página da Microsoft acima aponta para ela.
- [DirectX End-User Runtime Web Installer](https://www.microsoft.com/download/details.aspx?id=35): `dxwebsetup.exe`.
- [Redistributing Visual C++ files, Command-line options](https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files#command-line-options-for-the-redistributable-packages): `/install`, `/passive`, `/quiet` e `/norestart`.

## Instaladores conferidos em 23/11/2026

A conferência foi feita no Mac, com `curl -L`, que segue os redirecionamentos. Ela anotou o host final, o tamanho e o SHA-256, e leu os certificados da assinatura com `openssl pkcs7`. O estado `Valid` do Authenticode só pode ser conferido no Windows; isso fica para a validação no PC.

| Item | URL no Nexo | Host final | Bytes | SHA-256 | Fixado |
|---|---|---|---|---|---|
| VC++ v14 x64 | `https://aka.ms/vc14/vc_redist.x64.exe` | download.visualstudio.microsoft.com (via aka.ms/vs/18/release) | 18863624 (no dia) | 9c02c9ed120f671b900e6b9471d890e3b2414117a7e53f4ae2e855466fa2b7c9 (no dia) | não |
| VC++ v14 x86 | `https://aka.ms/vc14/vc_redist.x86.exe` | download.visualstudio.microsoft.com (via aka.ms/vs/18/release) | 6919464 (no dia) | 7b0117e6e20e9e349029c000a8a2715a52cf37c15b6bb697b2908e8bc7066cdf (no dia) | não |
| VC++ 2013 x64 | `https://download.visualstudio.microsoft.com/download/pr/10912041/cee5d6bca2ddbcd039da727bf4acb48a/vcredist_x64.exe` | download.visualstudio.microsoft.com | 7200744 | a4bba7701e355ae29c403431f871a537897c363e215cafe706615e270984f17c | sim |
| VC++ 2013 x86 | `https://download.visualstudio.microsoft.com/download/pr/10912113/5da66ddebb0ad32ebd4b922fd82e8e25/vcredist_x86.exe` | download.visualstudio.microsoft.com | 6510136 | 53b605d1100ab0a88b867447bbf9274b5938125024ba01f5105a9e178a3dcdbd | sim |
| VC++ 2012 x64 | `https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe` | download.microsoft.com | 7186992 | 681be3e5ba9fd3da02c09d7e565adfa078640ed66a0d58583efad2c1e3cc4064 | sim |
| VC++ 2012 x86 | `https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x86.exe` | download.microsoft.com | 6554576 | b924ad8062eaf4e70437c8be50fa612162795ff0839479546ce907ffa8d6e386 | sim |
| VC++ 2010 x64 | `https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe` | download.microsoft.com | 10277328 | f3b7a76d84d23f91957aa18456a14b4e90609e4ce8194c5653384ed38dada6f3 | sim |
| VC++ 2010 x86 | `https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x86.exe` | download.microsoft.com | 8993744 | 99dce3c841cc6028560830f7866c9ce2928c98cf3256892ef8e6cf755147b0d8 | sim |
| VC++ 2008 x64 | `https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x64.exe` | download.microsoft.com | 5211080 | c5e273a4a16ab4d5471e91c7477719a2f45ddadb76c7f98a38fa5074a6838654 | sim |
| VC++ 2008 x86 | `https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x86.exe` | download.microsoft.com | 4483040 | 8742bcbf24ef328a72d2a27b693cc7071e38d3bb4b9b44dec42aa3d2c8d61d92 | sim |
| VC++ 2005 x64 | `https://download.microsoft.com/download/8/b/4/8b42259f-5d70-43f4-ac2e-4b208fd8d66a/vcredist_x64.EXE` | download.microsoft.com | 3179000 | 4487570bd86e2e1aac29db2a1d0a91eb63361fcaac570808eb327cd4e0e2240d | sim |
| VC++ 2005 x86 | `https://download.microsoft.com/download/8/b/4/8b42259f-5d70-43f4-ac2e-4b208fd8d66a/vcredist_x86.EXE` | download.microsoft.com | 2710520 | 8648c5fc29c44b9112fe52f9a33f80e7fc42d10f3b5b42b2121542a13e44adfd | sim |
| DirectX (web) | `https://download.microsoft.com/download/1/7/1/1718ccc4-6315-4d8e-9543-8e28a4e18c4c/dxwebsetup.exe` | download.microsoft.com | 295320 | 2cf71d098c608c56e07f4655855a886c3102553f648df88458df616b26fd612f | sim |

Observações:

- **VC++ v14:** a página da Microsoft lista hoje o link `aka.ms/vc14/...`, da linha do Visual Studio 2026, que só suporta Windows 10 e 11. O link `aka.ms/vs/17/release/...`, da linha do VS 2022, também responde. Em 23/11/2026 ele entregava outra build, com 25635768 bytes no x64 (SHA-256 cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b) e 13953392 no x86 (0c09f2611660441084ce0df425c51c11e147e6447963c3690f97e0b25c55ed64). O Nexo usa o link da página.
- **VC++ 2013:** a página da Microsoft aponta `aka.ms/highdpimfc2013x64enu` e `aka.ms/highdpimfc2013x86enu`, que redirecionam para as URLs acima. O Nexo fixa a URL final.
- **Assinantes lidos no Mac:** os VC++ 2005, 2008 e 2010, o v14 e o `dxwebsetup.exe` são assinados por `CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US`. O 2012 e o 2013 são assinados por `CN=Microsoft Corporation, OU=MOPR, O=Microsoft Corporation, L=Redmond, S=Washington, C=US`, numa cadeia de 2013/2014. Cada item do catálogo tem o assinante exato que espera; o MOPR só é aceito no 2012 e no 2013, que têm o SHA-256 fixado.

## Raízes da cadeia

Só o Subject e o `Valid` não bastam: uma raiz extra instalada no PC, por adware ou por um proxy TLS, emitiria um certificado com o mesmo Subject, e o Windows o daria como `Valid`. Por isso, na mesma chamada do PowerShell do `Get-AuthenticodeSignature`, o Nexo monta a cadeia do `SignerCertificate` com `X509Chain` e exige que o certificado raiz tenha a impressão digital SHA-1 esperada para o item (campo `Root` do catálogo). A comparação é exata, sem diferenciar maiúsculas de minúsculas. Se a raiz for outra, o item para em "Assinatura não confiável", antes do UAC.

- A cadeia usa `RevocationMode` `NoCheck`, para não depender de rede. O `Valid` do Authenticode já vem da conferência do Windows, e nenhum outro ponto do Nexo consulta revogação no PC do usuário (o `Online` existe só no script de assinatura do release).
- A cadeia usa `VerificationFlags` `IgnoreNotTimeValid`, porque a raiz de 2001 e as folhas do 2012 e do 2013 estão vencidas. O resultado do `Build` e cada `ChainStatus` vão na saída do PowerShell, e o Nexo exige `Build` verdadeiro. Ele só tolera os estados `NotTimeValid`, `RevocationStatusUnknown` e `OfflineRevocation`. Qualquer outro, como `PartialChain`, `NotSignatureValid` ou `UntrustedRoot`, descarta a raiz, e o item para em "Assinatura não confiável", com o estado na mensagem, antes do UAC. A falta do `Build` ou do `ChainStatus` na saída também recusa o item.
- Os certificados embutidos no arquivo entram como auxiliares (`ExtraStore`). A raiz precisa vir do repositório do Windows e ter a impressão digital fixada.

| Raiz | SHA-1 | Itens |
|---|---|---|
| Microsoft Root Certificate Authority 2011 | `8F43288AD272F3103B6FB1428485EA3014C0BCFE` | v14, 2010, 2008, 2005 e `dxwebsetup.exe` |
| Microsoft Root Certificate Authority (2001) | `CDD4EEAE6000AC7F40C3802C171E30148030C072` | 2012 e 2013 |

Conferência em 23/11/2026, no Mac, com os arquivos da tabela acima. Nenhum deles traz a raiz embutida: só a folha e o intermediário (o 2012 e o 2013 também trazem a cadeia do carimbo de tempo).

1. `openssl pkcs7 -print_certs` na assinatura de cada arquivo mostra o intermediário: `Microsoft Code Signing PCA 2024` no v14, `Microsoft Code Signing PCA 2011` no 2005, 2008, 2010 e DirectX, e `Microsoft Code Signing PCA` no 2012 e 2013.
2. O "authority key identifier" desses intermediários é `72:2D:3A:02:...:89:34` (PCA 2011 e 2024) e `0E:AC:82:60:...:E4:A4` (PCA). Eles são o "subject key identifier" das duas raízes.
3. As raízes foram baixadas das URLs AIA dos próprios intermediários, no repositório da Microsoft: `http://www.microsoft.com/pki/certs/MicRooCerAut2011_2011_03_22.crt` e `http://www.microsoft.com/pki/certs/MicrosoftRootCert.crt`. O SHA-1 delas é o da tabela.
4. `openssl verify -no_check_time` confirma a cadeia inteira: folha → intermediário → raiz, nos 13 arquivos.

A raiz de 2001 venceu em 2021, mas o Windows a mantém no repositório de raízes para assinaturas com carimbo de tempo. Os instaladores de 2012 e 2013 dependem disso.

## Argumentos silenciosos

| Instalador | Argumentos | Fonte |
|---|---|---|
| VC++ v14, 2012 e 2013 (pacotes Burn) | `/install /passive /norestart` | Página "Redistributing Visual C++ files"; os de 2012 e 2013 trazem as mesmas opções no `/?` |
| VC++ 2010 | `/passive /norestart` | Opções do próprio instalador (`/?`) |
| VC++ 2005 e 2008 (pacotes IExpress) | `/q` | Texto do próprio pacote: "/Q -- Quiet modes for package" |
| `dxwebsetup.exe` | `/Q` | Texto do próprio pacote: "/Q -- Quiet modes for package" |

Códigos de saída:

- 0 é sucesso, conferido depois pela verificação;
- 3010 vira "Reinício pendente", e o Nexo não reinicia o PC;
- 1641 também vira "Reinício pendente", mas o texto diz que o instalador da Microsoft já iniciou o reinício do Windows;
- 1638, versão igual ou mais nova já instalada, vira "Já instalado" só se a verificação depois confirmar o componente. Se ele não aparecer, vira "Falha (1638)";
- qualquer outro código vira "Falha", com o código.

## Como funciona

- **Verificar este PC (só leitura, sem admin):**
  - VC++ v14: `Installed=1` em `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{x64|x86}`, nas duas visões do registro (o instalador grava na de 32 bits, `WOW6432Node`), com a versão. Se a chave faltar, vale o nome nas chaves de desinstalação.
  - VC++ 2012 e 2013: a chave `Runtimes` da versão (11.0, 12.0) ou o nome nas chaves de desinstalação.
  - VC++ 2005 a 2010: o nome nas chaves de desinstalação (`HKLM\...\Uninstall`, nas duas visões), por ano e arquitetura. A busca é aproximada para o lado do "instalado": qualquer build daquele ano conta, e o 2005 sem arquitetura no nome é o x86. Assim não aparece um falso "falta".
  - DirectX: `d3dx9_43.dll`, `d3dx10_43.dll`, `d3dx11_43.dll`, `xinput1_3.dll`, `XAudio2_7.dll` e `xactengine3_7.dll` em `System32` e, no Windows de 64 bits, em `SysWOW64`. A falta de qualquer uma marca o item como faltando, com os arquivos no detalhe.
  - .NET Framework: o `Release` de `NDP\v4\Full`, convertido pela tabela da Microsoft. O .NET Desktop Runtime vem das chaves `dotnet\Setup\InstalledVersions`. Os dois são só informativos.
  - No Windows de 32 bits, os itens x64 não se aplicam.
- **Instalar selecionados:**
  1. A confirmação lista item a item o que vai ser instalado e de onde vem.
  2. Cada item é baixado só de `aka.ms`, `download.microsoft.com` ou `download.visualstudio.microsoft.com`, por HTTPS na porta 443, sem redirecionamento automático. Cada salto é conferido antes do pedido, com no máximo 5 redirecionamentos.
  3. O tamanho é limitado durante o download: o fixado, ou 64 MB no v14.
  4. O Nexo cria a pasta `%TEMP%\Nexo\Runtimes\<guid>` e, nela, o arquivo do instalador vazio, já aberto para escrita sem compartilhamento.
  5. Logo em seguida, antes do download, a pasta ganha uma DACL protegida, sem herança, aplicada pelo próprio Nexo, sem admin (veja "Pasta do instalador"). Se ela não puder ser aplicada, nada é baixado.
  6. O download grava direto no arquivo aberto. Depois, o Nexo o reabre só para leitura, sem compartilhar escrita nem exclusão, até o instalador terminar. Um link ou junção no caminho é recusado.
  7. O Nexo confere o SHA-256 (quando fixado), o Authenticode `Valid` com o assinante esperado, a cadeia montada e a raiz da cadeia.
  8. Com o arquivo ainda aberto, o Nexo confere que a pasta contém só o instalador, e só então pede o UAC.
  9. Depois do instalador, a verificação confere o componente.
  10. Um UAC cancelado encerra a fila: os itens seguintes ficam "Não iniciado". O cancelamento do cartão vale entre itens; um instalador já iniciado termina antes.
- **Resultado por item:** Ok, Já instalado, Reinício pendente, Falha (com código), UAC cancelado, Integridade, Assinatura, Assinatura não confiável, Endereço recusado, Sem download, Link no caminho, Pasta do instalador alterada ou Não iniciado. Ele vai:
  - para o cartão;
  - para `runtimes-audit.ndjson` na pasta do Nexo, com a URL final, os bytes, o SHA-256 calculado, o assinante, o código de saída e, se sobrou, a pasta temporária (`Leftover`);
  - para o histórico operacional (`operations.db`, intervenção "Instalar componentes do Windows (Ferramentas)");
  - para o log da conta.

## Pasta do instalador

A pasta `%TEMP%\Nexo\Runtimes\<guid>` herdaria a ACL do `%TEMP%`. Um processo da mesma conta poderia gravar uma DLL ali antes do UAC, e os instaladores antigos (2005 e 2008, pacotes IExpress) carregam DLLs da própria pasta, já como administrador. Por isso, logo depois de criar a pasta e o instalador vazio, e antes do download, o Nexo aplica à pasta uma DACL protegida, sem herança:

- `OWNER RIGHTS` (`S-1-3-4`) com `ReadAndExecute`, `Delete` e `DeleteSubdirectoriesAndFiles`. Isso tira do dono o `WRITE_DAC` e o `WRITE_OWNER` implícitos e impede criar arquivos e pastas;
- `SYSTEM` e Administradores com controle total;
- nenhuma ACE para o usuário atual.

O dono ainda pode definir a DACL nesse momento; depois dela, perde o `WRITE_DAC`. As ACEs são herdáveis, e o Windows as propaga ao instalador já criado. O handle de escrita do Nexo foi aberto antes da DACL, e o Windows confere o acesso na abertura: o download continua gravando nele. Terminado o download, o Nexo fecha esse handle e reabre o arquivo só para leitura com `FileShare.Read`. O `Get-AuthenticodeSignature` e o `runas` leem o arquivo com esse compartilhamento, o que um handle de escrita impediria. Se outro processo tiver o arquivo aberto para escrita, a reabertura falha; se algo o alterou entre o fechamento e a reabertura, o SHA-256 ou a assinatura acusam. Com o arquivo ainda aberto e travado, o Nexo lista a pasta: arquivos e subpastas, inclusive ocultos e de sistema, e os fluxos alternativos (ADS) da pasta e do instalador. Se houver qualquer coisa além do instalador, o item para em "Pasta do instalador alterada" e nada é executado.

No fim, o Nexo apaga o arquivo e a pasta com a ACE de `Delete` do `OWNER RIGHTS`. Se não conseguir, por exemplo quando sobrou um arquivo plantado, a pasta fica e o caminho vai para `runtimes-audit.ndjson`, sem erro para o usuário.

Riscos residuais:

- Entre o `CreateDirectory` e a DACL ainda há uma janela. Ela só contém a criação do instalador vazio, sem download, mas não tem duração garantida. Um processo da mesma conta que abra um handle na pasta nessa janela, com `FILE_ADD_FILE` ou `WRITE_DAC`, mantém esse acesso depois da troca da DACL, porque o Windows confere o acesso na abertura. Com ele, poderia gravar um arquivo na pasta depois da listagem e antes do `runas`. O nome da pasta é um GUID novo, e o instalador já nasce aberto sem compartilhamento, então esse processo não consegue abrir o próprio instalador para gravar. A listagem logo antes do UAC pega o que tiver sido gravado até ali.
- Um processo da mesma conta já consegue injetar código no próprio Nexo, que roda com o mesmo usuário. A DACL não protege contra isso; ela fecha só o caminho da DLL plantada ao lado do instalador elevado.
- Depois do UAC, o instalador elevado extrai os próprios arquivos no `%TEMP%`. Esse risco é o mesmo de rodar o instalador da Microsoft a partir da pasta Downloads, e fica fora do alcance do Nexo.

## Como atualizar quando a Microsoft trocar um arquivo

Um instalador fixado que mudou para na etapa de integridade e não é executado: o cartão mostra "Integridade" e o SHA-256 recebido vai para `runtimes-audit.ndjson`. Para atualizar:

1. Abra a fonte oficial da tabela acima e confirme a URL nova na própria página da Microsoft. Não use espelhos.
2. Baixe numa pasta vazia com `curl -sL -o arquivo.exe -w "%{url_effective} %{size_download}\n" <url>` e confirme que o host final está na lista.
3. Calcule o SHA-256 (`shasum -a 256` ou `Get-FileHash`). No Windows, confira `Get-AuthenticodeSignature` com `Status` igual a `Valid`, o `SignerCertificate.Subject` e a raiz da cadeia (seção "Raízes da cadeia"). No Mac, siga os 4 passos daquela seção.
4. Troque URL, tamanho, SHA-256 e, se mudaram, o assinante e a raiz (`Root`) em `Nexo.Support/RuntimeCatalog.cs`. Atualize os valores conferidos em `Nexo.Checks/RuntimeChecks.cs` e a tabela e a data deste documento.
5. Rode `dotnet run --project Nexo.Checks -- --runtimes`.

O v14 não precisa de atualização enquanto o link permanente continuar entregando um arquivo assinado pela Microsoft com até 64 MB. Se a Microsoft trocar o assinante, a raiz ou passar desse limite, o item para em "Assinatura", "Assinatura não confiável" ou "Integridade", e a atualização segue os mesmos passos.

## Testes

`dotnet run --project Nexo.Checks -- --runtimes` (também roda na suíte completa) simula registro, arquivos, download, assinatura e processo. Ele cobre:

- a detecção com a chave presente, ausente e só no x86;
- os nomes das chaves de desinstalação e as DLLs do DirectX faltando;
- a verificação sem alterar nada;
- o hash diferente, os tamanhos, os hosts fora da lista, os laços de redirecionamento e o assinante diferente;
- a raiz da cadeia por item, com a saída simulada do PowerShell e o script real num pwsh com `Get-AuthenticodeSignature` falso;
- a cadeia com a saída simulada: `Build` verdadeiro com `NotTimeValid` ou revogação desconhecida é aceito; `PartialChain`, `NotSignatureValid`, `UntrustedRoot`, `Build` falso ou ausente e `ChainStatus` ausente são recusados;
- a ordem instalador vazio → DACL → download → hash → assinatura → listar → runas, a DACL recusada sem download e a recusa com arquivo, subpasta ou ADS a mais na pasta;
- no Windows, a DACL real: o handle aberto antes dela continua gravando; o dono não cria arquivo, pasta nem fluxo (na pasta e no instalador), não troca a DACL e não grava no instalador fechado; e o Nexo apaga tudo no fim. Num processo elevado, como o runner do GitHub, os Administradores têm controle total na DACL, e essas recusas aparecem como `IGNORADO`; a listagem e o resto rodam sempre;
- os códigos 3010, 1641, 1638 com e sem o componente depois, de erro e 0 sem o componente;
- o UAC cancelado, os itens antigos desmarcados e a trava do modelo.

A validação no PC fica no roteiro da #150.
