# Recuperação do TCC Nexo — 22/11/2026

## Origem recuperada

- O checkout recebido estava em `main` na versão 1.6.3. A branch pública `development` contém a versão 1.7.0 e alterações mais recentes. O trabalho desta recuperação está na branch local `codex/recovery-2026-11-22`, criada a partir de `origin/development`.
- O arquivo `artifacts/backup-preformatacao/git/Nexo-todas-as-branches.bundle` conserva as branches e o histórico anteriores à formatação.
- `artifacts/cloud-release/releases/1.6.2` contém os pacotes locais do Ollama e do modelo `qwen3:8b`; a 1.6.3 usa os mesmos pacotes no bucket público `nexo-releases`.
- Os relatórios em `Documentos Nexo e relatórios` e as evidências de `artifacts/entregas` registram os ensaios anteriores. Dados operacionais do aplicativo devem permanecer locais e fora do release.

## Plano de organização

1. Preservar o código atual, o bundle Git, relatórios, evidências, dados operacionais e um conjunto verificável dos pacotes já publicados.
2. Remover os dois discos de VM salvos, cópias de worktrees, caches de instalação, compilações antigas e dependências temporárias depois de conferir os caminhos. Nenhuma VM será baixada ou executada.
3. Manter um instalador público separado da versão 1.7.0: o instalador atual e seu manifesto assinado antigo continuam válidos na raiz do bucket. O novo manifesto terá outra chave e ficará em um canal próprio, porque a chave privada antiga não foi recuperada.
4. Compilar no PC, rodar as suítes automatizadas, publicar apenas os artefatos necessários e validar o manifesto e os downloads por HTTPS. Testes que reconfiguram rede, instalam drivers ou exigem hardware indisponível não serão usados como prova de funcionamento geral.

## Limites da entrega

- O executável sem assinatura Authenticode pode receber um aviso do Windows SmartScreen ou ser bloqueado por políticas do dispositivo; nenhum processo de empacotamento garante ausência de alertas de antivírus.
- O instalador exige confirmação na própria interface e valida assinatura do manifesto, tamanhos e SHA-256 dos pacotes. As ações de correção do Nexo continuam pedindo autorização específica.
- A 1.7.0 ainda tem pendências documentadas no `CHANGELOG.md`. A nova distribuição deve ser identificada como prévia até que os ensaios restantes sejam concluídos.

## Resultado

- O código em `codex/recovery-2026-11-22` veio de `origin/development` (1.7.0). A modificação anterior do `.gitignore` foi guardada no stash `pre-recovery gitignore from backup`. O bundle Git e os worktrees de validação permanecem em `artifacts/backup-preformatacao`; os `bin`, `obj` e `publish` dos worktrees foram descartados.
- `artifacts/backup-preformatacao` conserva o histórico Codex antigo, a primeira versão, os dados operacionais, as referências e os worktrees que podem conter edições únicas. `Documentos Nexo e relatórios` e `artifacts/evidence` conservam relatórios e evidências. Os pacotes locais conferidos da versão estável estão em `artifacts/cloud-release`.
- Discos de VM antigos, cache de Ollama, cópias duplicadas do aplicativo e modelo, builds e ambientes temporários foram enviados à Lixeira do Windows após checagem dos caminhos; nenhuma VM foi baixada ou executada. A exclusão permanente foi recusada pela revisão automática, portanto o espaço físico desses itens só será liberado quando a Lixeira for esvaziada pelo proprietário. As pastas ativas de `artifacts` somam aproximadamente 8 GiB, dos quais 6,69 GiB são os pacotes estáveis preservados.
- A prévia 1.7.0 está em `releases/1.7.0/nexo-app.zip` e `preview/latest.json`/`preview/latest.sig` no bucket público Oracle. O manifesto da prévia usa a nova chave RSA 3072, embutida como chave pública em `Nexo.Setup/preview-public.pem`. A chave privada está somente em `%USERPROFILE%\.nexo-preview-signing.pem`, com acesso restrito ao usuário atual; faça backup fora deste computador antes de outra formatação. A versão estável 1.6.3 na raiz do bucket não foi alterada.
- O instalador atual é [NexoSetup-Preview-1.1.1.exe](https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/NexoSetup-Preview-1.1.1.exe), 161.762.771 bytes, SHA-256 `2A1027D775103E7A064CEE42F483BD1698CF534F81D6A9B15A84D6516F03EF39`. O objeto publicado respondeu HTTP 200, tamanho correto e, ao ser baixado para comparação, hash idêntico ao binário local. O instalador local retornou código 0 em `--check` contra o manifesto público.
- A primeira tentativa de instalação revelou dois defeitos reais: o módulo de Authenticode do Windows PowerShell não carregava quando herdava `PSModulePath` do PowerShell 7; durante o início do Ollama, um blob pequeno do modelo desapareceu antes da ativação do manifesto. O instalador 1.1.1 fixa o caminho dos módulos nativos e restaura blobs ausentes do ZIP verificado. O objeto antigo `NexoSetup-Preview.exe` foi substituído e não deve ser distribuído; ele ainda existe no bucket sem listagem pública.
- No PC atual, o instalador baixou e verificou Nexo, Ollama e `qwen3:8b`, instalou o aplicativo e criou o atalho. `ollama list` mostrou `qwen3:8b`, a API local respondeu `OK` com `done=true` e a janela `Nexo 1.7.0 • Suporte inteligente` abriu. As verificações finais passaram: 2.753 do Nexo, 41 do instalador, 49 do Office e os testes do manifesto de prévia. O Defender local não registrou detecção para o instalador 1.1.1. Isso não substitui os cenários de hardware e intervenção ainda pendentes no `CHANGELOG.md`.
