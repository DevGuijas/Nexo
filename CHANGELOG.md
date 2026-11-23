# Histórico de versões do Nexo

Cada versão publicada é imutável: os arquivos de `releases/<versão>/` no armazenamento da Oracle não são substituídos. Uma correção depois da publicação ganha uma versão nova. O aplicativo (`Nexo.Support`) e o instalador (`Nexo.Setup`) têm versões independentes; o instalador só muda de versão quando o próprio código dele muda, porque ele sempre baixa a versão indicada no `latest.json` assinado.

A data no título de uma versão é o dia em que o `latest.json` dela foi publicado. Enquanto o título disser "não publicada", `scripts/Build-CloudRelease.ps1` se recusa a gerar os pacotes.

## [1.7.0] — não publicada

Aplicativo 1.7.0. O instalador continua na 1.0.0, porque o código dele não mudou desde a 1.6.3.

### Antes de publicar

- [x] **Prioridade alta:** o executor de rede elevado deixa de gravar resultados na pasta do usuário (`%LOCALAPPDATA%`) e passa a gravar só na pasta protegida em `%ProgramData%` (#95, PR #96, validado no PC com UAC real em 10/11/2026).
- [x] **Prioridade alta:** os scripts elevados restringem o `PSModulePath` aos módulos do Windows antes de qualquer comando, e o lançamento elevado recusa o que não fizer isso; um módulo plantado na pasta Documentos do usuário não roda mais com admin (#131).
- [x] **Prioridade alta:** o executor de rede elevado não compila mais código para conferir o PID do pipe da decisão; o `Add-Type` gravava o código e a DLL no TEMP do usuário antes de carregá-los com admin (#133). Falta confirmar no PC, com UAC, a troca de DNS mantida pelo pipe.
- [x] Investigar com o log do Ollama ativo as etapas expiradas na 1ª chamada ao modelo, antes de mudar o limite de 3 minutos (#93, item 4). No PC, em 22/11/2026 (e7a69a2), com a RAM entre 80,1% e 81,6% e `OLLAMA_DEBUG=1`, nenhuma etapa expirou: a maior chamada levou 26,7 s (27,3 s com a RAM em 39%), todas com HTTP 200, e o modelo ficou na GPU. O limite de 3 minutos fica como está.
- [ ] Validar as sete falhas controladas em VM Windows (#8). O cenário 5 de memória foi ensaiado com alerta, PID e resolução no [relatório da VM](docs/ENSAIO-VM-MEMORIA-2026-11-16.md); faltam os demais cenários e controles.
- [ ] Validar em VM descartável a limpeza de temporários, a manutenção de disco e o cancelamento da otimização (#118); os testes automatizados usam pastas e comandos simulados.
- [ ] Obter certificado de assinatura de código confiável, assinar `Nexo.exe` e `NexoSetup.exe`, conferir em Windows limpo e registrar emissor, validade e hashes antes da distribuição (#22).
- [x] Validar em VM o canal autenticado da rede: um `keep` plantado antes do UAC não venceu a escolha explícita de restaurar; DNS e IP foram conferidos depois (#11, 15/11/2026).
- [ ] Validar a instalação NVIDIA real com o prazo alinhado ao supervisor num laboratório com GPU acessível ao Windows convidado (#12); a VM VirtualBox atual só expõe vídeo virtual.
- [x] Validar no PC o patamar estável de memória com o NexoMemTeste-toca (#71): "Persiste", "Estável em patamar alto" depois de 31 min, volta a "Persiste" com +15% e "Resolvida" com o processo encerrado, na 76d9028 em 10/11/2026.
- [x] Conferir prazo e estado desconhecido após travamento no PC (12/11/2026) e, na VM, a abertura seguinte após queda do executor: ofereceu restauração, ignorou resultado local forjado e mostrou estado confirmado depois de recuperar o DNS (#9, 15/11/2026). O registro protegido permitiu a recuperação com a pasta local ausente (#106).
- [ ] Completar as quedas em cada fase da transação de rede na VM (#10). O caso depois de `applied.json` foi comprovado; os demais ainda faltam.
- [ ] Repetir os ensaios com sensores e discos atrás de AMD-RAID (#19). No PC de desenvolvimento, o sensor AMD retornou 63,8 °C com Nexo elevado; sem elevação, o valor zero foi descartado. O hardware disponível não tem AMD-RAID.
- [x] Repetir no PC os passos que falharam na validação de 22/11/2026 (e7a69a2) depois dos ajustes do PR #125 (#88, #93). Na revalidação da 84d764d, no mesmo dia, passaram: o disco T: com o Armazenamento nos 3 turnos e sem "A IA não conseguiu"; o D: como USB, sem falar do conteúdo; a linha dos MB e a resposta de lentidão com contexto; a função inventada do NexoMemTeste recusada; e o "é vírus?" terminando com a orientação da Segurança do Windows. O modelo não chegou a oferecer aumentar o VHDX, ver o que há dentro do D: nem fechar o processo, e essas travas ficam cobertas pelos testes do PR #125. Na rodada da e7a69a2 já tinham passado o volume removido como "Resolvida", o patamar estável e o Spooler com "Estado atual: parado". A #88 e a #93 foram fechadas.
- [ ] Conferir a mensagem do chat após escolher restaurar o DNS (#98). No PC, em 16/11/2026 (e7a69a2), o cartão mostrou "Restaurado a seu pedido" sem "Etapa não concluída"; falta fechar a issue.
- [x] Validar no PC o patamar estável na resposta, sem culpar o processo pela lentidão quando a memória está abaixo de 80% (#71).
- [ ] Validar no PC as métricas ao vivo contra o Gerenciador de Tarefas e medir o custo de CPU do Nexo (#141).
- [ ] Validar no PC os temas da #143: `scripts/Check-UiPreview.ps1` com `-Theme onyx` e `-Theme glacial` sem falha, as capturas dos dois temas conferidas a olho e a troca em Configurações > Aparência mantida depois de fechar e abrir o Nexo.
- [ ] Validar no PC a janela e a Início da #143 (etapas 2 e 3): `scripts/Check-UiPreview.ps1 -Theme onyx` e `-Theme glacial` sem falha, snap layouts ao passar sobre o maximizar no Windows 11, maximizar sem cortar a borda e a janela em 100%, 125% e 150% de escala.
- [ ] Trocar "não publicada" pela data de publicação, gerar os pacotes e registrar abaixo o commit e os hashes impressos pelo script.

### Saúde do computador

- Página **Saúde do PC** com CPU, memória, volumes, serviços automáticos, adaptadores e eventos do Windows coletados enquanto a janela está aberta.
- Histórico operacional em SQLite (`operations.db`), separado das conversas, com retenção configurável.
- Cartão **Agora** na Saúde do PC com CPU, memória, disco (tempo ativo do disco mais ocupado, leitura e gravação de todos) e rede (recebido e enviado do adaptador mais ativo, sem somar adaptadores que repetem o mesmo tráfego, como a vEthernet do Hyper-V e a placa física) atualizados a cada segundo, com o gráfico do último minuto e o uso de CPU do próprio Nexo (#141). A leitura usa APIs do Windows sem driver nem administrador (`GetSystemTimes`, `GlobalMemoryStatusEx`, contadores de disco pelo PDH com nomes em inglês e bytes por adaptador), roda só com a tela aberta e a janela visível, pausa ao minimizar ou esconder e não grava nada no `operations.db`; o monitor de saúde e as ocorrências continuam com o próprio intervalo. Uma fonte indisponível aparece como "indisponível" sem afetar as outras.
- Ocorrências persistentes com estado atual: persiste, resolvida ou desconhecida. Resolver exige leituras seguidas sem a condição. O alerta de pouco espaço de um volume que deixou de existir (um VHDX desmontado) é resolvido quando as leituras listam os outros volumes sem falha e não o encontram ("Resolvida: o volume não está mais presente…"); um volume fixo que não está pronto (BitLocker bloqueado) não conta como removido nem deixa a coleta parcial (#88).
- Crescimento de memória por processo, identificado por PID e horário de início. Um processo que se estabiliza num patamar alto por 30 minutos passa a "Estável em patamar alto", informativo; volta a "Persiste" se subir 10% sobre a referência gravada na ocorrência (#71).
- Sondagens opcionais de gateway, DNS e destino HTTP(S), desligadas por padrão.
- Serviços automáticos que o Windows deixa parados de propósito (início atrasado ou por gatilho com código 0) não geram alerta.
- Temperatura da CPU pelo LibreHardwareMonitorLib incorporado ao Nexo (sensor de pacote ou die) e estado dos discos físicos pelo Windows Storage, com origem e horário da leitura; sem fonte confiável, o painel diz "indisponível" e o motivo (#19, PR #79). O driver PawnIO não é instalado pelo Nexo. Em 16/11/2026, uma leitura elevada neste PC retornou 63,8 °C do sensor AMD `Core (Tctl/Tdie)`; cinco leituras sem elevação descartaram valor zero como indisponível. AMD-RAID segue sem ensaio real.
- Serviço automático parado com saída normal só gera alerta se o Nexo o viu em execução estável nos 10 minutos anteriores; os demais, como os GoogleUpdater, ficam num cartão informativo e não vão ao modelo como problema (#57). Saída com erro continua gerando alerta.
- Limiares digitados com ponto não são mais multiplicados por dez.

### Diagnóstico e assistente

- Relatório de saúde disponível ao assistente (`get_health_report`) e sem o modelo, com todas as ocorrências das últimas 24 horas.
- Coleta incremental de eventos System, Application e Setup, sem perder rajadas entre consultas e reconhecendo reinício do log.
- INF e versão do driver associados ao dispositivo com erro.
- Eventos do serviço consultados antes de propor o reinício.
- Triagem reconhece falta de espaço, lentidão e sites que não abrem.
- Resposta final cita a evidência trazida pelas ferramentas.
- Com mais de um alvo possível, só o nome sozinho escolhe o dispositivo, a impressora ou a oferta de driver.
- Toda resposta termina com a linha de fatos do turno ("Alterações feitas pelo Nexo neste turno: …"), e uma alteração recusada ou não confirmada nunca aparece como feita (#73).
- Recusa de ferramenta devolve ao modelo o motivo e o que é aceito; o turno não termina mais em erro por argumento inválido (#83).
- O alvo nomeado pelo usuário é consultado antes e chega ao pedido de aprovação (#70).
- Ocorrência encerrada (resolvida ou histórica) não é apresentada como problema atual; ocorrências estáveis e informativas vão ao modelo marcadas como tal (#88).
- Os MB do processo que segura memória e, com alerta de pouco espaço ativo, a indicação de Configurações > Sistema > Armazenamento entram na resposta pelo próprio Nexo; a oferta de achar os maiores arquivos é recusada, e um alerta de disco já encerrado não vira pedido para liberar espaço (#88, PR #94).
- Rótulos de progresso para todas as ferramentas, e "ferramenta inexistente pedida pelo modelo" quando ele pede uma que não existe (#84, #88).
- Ajustes da validação de 10/11 (#88, #71, #93): "pouca", "pouquíssimo", "quase sem" e "sem espaço" também contam como falta de espaço num alerta de disco encerrado, e "caso necessário" não isenta a frase; a promessa de liberar espaço ou remover arquivos é recusada com a instrução de que o Nexo não tem essa ferramenta, e o turno que esgota as tentativas sem evidência num incidente do sistema consulta o relatório de saúde e termina com ele, em vez de "A IA não conseguiu selecionar a próxima etapa"; a linha dos MB entra também quando o processo é citado por um começo do nome e, no patamar estável, diz que é informativo; a resposta que apresenta um processo em patamar estável como causa da lentidão, com a memória do PC abaixo de 80%, é recusada.
- Na 2ª recusa da resposta pelo mesmo motivo, o Nexo diz ao modelo qual frase remover e, quando houver, qual processo não citar; a resposta que sugere que um processo é malicioso sem evidência é recusada; e o inventário de armazenamento liga cada letra de unidade ao disco que a contém, com o VHD/VHDX marcado como disco virtual (#93).
- Ajustes da validação no PC de 22/11 (#88, #93): a oferta de ver "o que há dentro" de uma unidade é recusada como a de verificar o conteúdo; a função inventada de um processo ("é um teste de memória", "que verifica…", "usado para identificar…") é recusada também sem consulta no turno, pelo nome citado no pedido ou na linha do monitor de uma resposta anterior; a 3ª resposta recusada com texto e motivo idênticos encerra o turno com o relatório por qualquer motivo, e a recusa sobre um processo consulta o relatório de saúde antes de encerrar, com a orientação da Segurança do Windows quando o usuário perguntou sobre vírus; a oferta de fechar programas ou de aumentar um disco, volume ou VHD/VHDX é recusada com o caminho manual (Gerenciador de Tarefas, Gerenciamento de Disco); e a resposta pobre depois de uma recusa ("Posso ajudar?") vem depois do que as consultas mostraram.
- Ajustes da validação da #19 no PC com AMD-RAID (#135): o disco com barramento RAID diz que o estado é o do volume informado pelo driver do controlador e que o dos discos-membro não é visível pelo Windows Storage; o pedido sobre a temperatura ou a saúde dos discos consulta o relatório de saúde, e a resposta traz a temperatura (ou o motivo da indisponibilidade, como o driver PawnIO ausente) e a linha "Discos físicos", também no relatório de fim de turno; mandar o usuário ao Gerenciador de Tarefas para ver a temperatura da CPU é recusado; e "verificar se há arquivos… ocupando espaço" num disco com alerta encerrado é recusado pelo motivo do disco encerrado, não mais pelo do conteúdo de unidade. Na revalidação no PC (afeefb7), a resposta que resumia os discos com "sem alertas informados" perdia o limite do RAID: agora ele entra sozinho quando a resposta não o cita, sem repetir a linha do estado, e o resumo genérico não conta como citação quando há disco em alerta; sugerir verificar o uso do disco ou revisar o conteúdo dos volumes com a ocorrência de disco encerrada é recusado como o pedido de liberar espaço; e a oferta de verificar, instalar ou procurar o driver PawnIO é recusada, porque nenhuma ferramenta faz isso. Na repetição no PC (5b48461), a oferta de "verificar o uso do disco ou identificar quais processos estão utilizando mais espaço" passou: agora ela é recusada como a de achar os maiores arquivos, também no subjuntivo ("quer que eu verifique…"); a linha do monitor entra antes da oferta que precede a pergunta final, para "Deseja que eu faça isso?" não parecer falar da linha; e, num pedido só de temperatura, o relatório de fim de turno traz só a linha da temperatura, sem as ocorrências e o espaço livre.
- Ajustes da 3ª rodada da #135 no PC (#139): uma ocorrência encerrada não aparece mais como algo que evolui agora ("o espaço do T: está normalizando" é recusado mesmo com "já foi resolvida" na frase, e "normalizando" não isenta como "normal"); com o crescimento de memória só em ocorrência encerrada, a frase que o apresenta como atual, com ou sem o nome do processo, e a recomendação de verificar ou encerrar "esses processos" são recusadas.

### Recuperação e drivers

- Ações elevadas supervisionadas: sem resultado no prazo, a operação fica em estado desconhecido, nunca como concluída.
- Todo script elevado (rede, serviço, impressora, adaptador, dispositivo, driver, ponto de restauração, otimização e Office) começa restringindo o `PSModulePath` aos módulos do Windows (`$PSHOME\Modules`), e o lançamento elevado recusa antes do UAC o script que não fizer isso. Um módulo plantado em `Documentos\WindowsPowerShell\Modules` com o nome de um módulo do Windows (`DnsClient`, `NetAdapter`…) não é mais autocarregado com admin. As leituras sem elevação usam a mesma restrição, inclusive a conferência da assinatura do driver baixado (#131).
- O lançamento elevado aceita só o formato que o Nexo gera (`-NoProfile -NonInteractive` e o script em `-EncodedCommand` ou `-Command`); abreviações como `-c` e `-enc`, `-File`, `-PSConsoleFile`, argumentos a mais e a falta de `-NoProfile` são recusados antes do UAC. O `netsh.exe` do executor de rede e o `pnputil.exe` das ações de dispositivo, que rodam com admin, e o `wevtutil.exe` e o módulo de diagnóstico da coleta de eventos deixam de vir de `$env:WINDIR`/`$env:SystemRoot`, que o usuário define em `HKCU\Environment`, e passam a vir do System32 por `[Environment]::SystemDirectory` e do `$PSHOME` (#131).
- Ponto de restauração confirmado antes de instalar driver; sem ele, a instalação para.
- Retomada salva quando o driver pede reinício.
- Instalação NVIDIA preserva o áudio HDMI, reconhece GeForce sem o prefixo NVIDIA e confere a versão instalada com a oferta.
- A pergunta de qual dispositivo do fluxo de driver passa a aceitar resposta, e lista só dispositivos com driver próprio, sem os endpoints de áudio (#74, #84).
- O pedido de aprovação mostra o driver atual do dispositivo ("Driver: …") e, no reinício de serviço, o estado atual dele ("Estado atual: parado (início automático).") (#84, #88).

### Impressão

- Spooler parado é lido e informado na consulta de impressoras, sem falhar a consulta (#84).

### Rede

- A confirmação para manter IP/DNS passa por um pipe local de uso único: aplicativo e executor elevado conferem os PIDs um do outro. Um `keep` plantado em `decision.txt` não confirma a mudança nem encerra a oferta de restauração após queda; a escolha aceita fica no registro protegido (#11). A VM confirmou que a escolha explícita de restaurar vence um `keep` plantado antes do UAC e devolve o DNS anterior.
- A recuperação procura transações pendentes no registro protegido, mesmo com a pasta local apagada, bloqueada ou com resultados falsos. Uma recusa guardada só na pasta local volta a ser perguntada na próxima abertura (#106). A VM confirmou a descoberta com resultado local forjado ou pasta local ausente, a oferta visual na abertura seguinte e a restauração do DNS a partir do backup protegido.
- Ajustes da validação no PC das quedas do executor de rede (#128): depois de "Manter configuração", o executor que termina sem resultado não gera mais "A restauração automática não foi confirmada"; fechar a janela de restauração ou o Nexo com ela aberta vale "agora não", sem gravar recusa; o resultado da recuperação feita na abertura seguinte vai também para o atendimento de origem, com a linha de fatos; com o estado atual já igual ao backup protegido, a recuperação registra isso sem janela nem UAC (a leitura usa só os módulos do Windows; se a rede mudar depois, até a limpeza de 7 dias, a oferta volta avisando que restaurar desfaria essa mudança posterior); e set_dns, set_ipv4 ou enable_network_adapter com um índice que não veio da consulta de rede do turno são recusados antes do executor, com as interfaces consultadas.
- O executor de rede elevado confere o PID do servidor do pipe da decisão (#11) com um P/Invoke definido por `System.Reflection.Emit` num assembly dinâmico só em memória, em vez do `Add-Type`. No Windows PowerShell 5.1, o `Add-Type` compila com o csc.exe e grava o `.cs` e a `.dll` no TEMP do processo elevado, que vem de `HKCU\Environment` e o usuário grava sem elevação: um programa da sessão poderia trocar a DLL antes de ela ser carregada com admin. Sem o tipo, a decisão é restaurar (#133).
- O carregador compactado do executor de rede elevado vai em `-Command`, não mais em `-EncodedCommand`, que o Microsoft Defender bloqueava de vez em quando como `!#SLF:CMD_HSTR:General.ML.B` e fazia o `powershell.exe` elevado falhar com "Acesso negado" (#10).

- DNS fixo com DHCP apontado, teste TCP por IP e preferência por DNS automático.
- Transação de rede interrompida (executor elevado encerrado no meio) é detectada na hora ou na próxima abertura e, se a mudança ficou pendente, o Nexo oferece restaurar a configuração anterior, com autorização e UAC. O backup confiável fica numa cópia protegida em `%ProgramData%`, e o que vem da pasta do usuário não é aplicado (#10, PR #76).
- Prazo total de 5 minutos para a transação de rede, por fase; esgotado o prazo, o estado fica desconhecido e a resposta diz que nenhuma restauração foi confirmada, sem encerrar nem disputar com o executor ainda ativo (#9, PR #78).
- O executor de rede elevado grava o estado aplicado e o resultado só na pasta protegida; nada é gravado com elevação em `%LOCALAPPDATA%`, e a troca de IP/DNS não trava mais antes do UAC (#95, PR #96).
- Quando o executor de rede sai sem resultado, o Nexo confere a cadeia `%ProgramData%\NexoSupport`. Se há dono ou permissões inesperados, link ou arquivo no lugar de pasta, a resposta informa o problema e orienta a conferir a rede antes de repetir; não atribui a causa nem afirma que a rede ficou intacta sem resultado do executor. O README explica a recuperação da pasta conforme o tipo de entrada (#97).
- Restaurar na janela "Manter a configuração de rede?" aparece como escolha do usuário: a resposta diz que a configuração anterior foi restaurada a pedido e conferida, o cartão mostra "Restaurado a seu pedido" e a linha de fatos lista a mudança em "Desfeitas a seu pedido", em vez de "Etapa não concluída… confira o erro no cartão". Prazo esgotado, cancelamento e restauração que falha continuam como antes (#98).

### Interface e repositório

- Navegação, formulários e confirmações redesenhados, com tema claro e ícones vetoriais.
- Novo layout, etapa 1 (#143): temas **Ônix** (escuro, padrão) e **Glacial** (claro), escolhidos em **Configurações > Aparência** e aplicados na hora, inclusive ao texto já aberto; a escolha fica no `data.json` do perfil. Cores com contraste AA (4,5:1) conferido nos testes; no Glacial, o acento é o azul da logo escurecido para passar no AA. Fontes IBM Plex Sans, Barlow Condensed e IBM Plex Mono embutidas (OFL 1.1, avisos em `ThirdParty/Fonts`), sem texto abaixo de 11 px, e logo nova em vetor na lateral e nas mensagens. O ícone do executável e da bandeja ainda é o anterior.
- Novo layout, etapa 2 (#143): barra de título própria (arrastar, duplo clique, minimizar, maximizar/restaurar, fechar, redimensionar pelas bordas e snap layouts do Windows 11 sobre o maximizar), barra lateral de ícones que expande e recolhe com a escolha salva no `data.json`, e paleta **Ctrl+K** para abrir as telas, começar um atendimento, trocar o tema e recolher a lateral, toda pelo teclado (setas, Enter, Esc). Foco visível, nomes para leitor de tela e animação desligada quando o Windows pede menos movimento.
- Novo layout, etapa 3 (#143): tela **Início**, a de abertura, com o estado do computador pelo monitor de saúde, o cartão Agora da #141, seis atalhos "Resolver rápido" numerados (cliques ou teclas 1 a 6) que escrevem o pedido no Assistente sem enviar, as ocorrências de hoje e os atendimentos recentes. Só dados do Nexo, com estados de carregando, vazio e indisponível.
- A opção de monitoramento em segundo plano mantém o Nexo na bandeja quando escolhida; fechar a janela sem essa opção encerra o aplicativo (#111).
- Em **Ferramentas**, a instalação do Office LTSC Professional Plus 2024 (64 bits, pt-BR) pede confirmação e UAC, valida o instalador oficial da Microsoft e confere a instalação no Windows. Não inclui licença nem ativação (#117).
- Em **Ferramentas**, **Otimizar PC** remove arquivos antigos apenas das pastas temporárias permitidas e solicita ao Windows a manutenção de discos elegíveis, sem prometer ganho de desempenho. Registra cada etapa e trata acesso negado ou resultado não confirmado como parcial ou desconhecido (#118).
- Título da janela lido da versão do aplicativo, sem número fixo no código.
- CI na `development` com compilação e regressões.
- Testes que dependiam da data do dia passam a usar relógio injetado.
- `scripts/Build-CloudRelease.ps1` só gera os pacotes se o `latest.json` publicado tiver assinatura válida e a versão do projeto for maior que a publicada; a origem da Oracle é fixa (#21).

### Artefatos publicados

Preencher na publicação.

| Arquivo | Tamanho (bytes) | SHA-256 |
| --- | --- | --- |
| `Nexo.exe` | | |
| `releases/1.7.0/nexo-app.zip` | | |

Commit publicado: a registrar.

## [1.6.3] — 2026-10-22

Aplicativo 1.6.3 e instalador 1.0.0. Publicação atual do `latest.json`, conferida em 08/11/2026.

- Instalação direta do driver NVIDIA pelo catálogo oficial quando o Windows não oferece atualização para uma única placa, com licença, autorização explícita, validação da assinatura e conferência da versão.
- Instalador gráfico com etapas, progresso e instalação por usuário.

### Artefatos publicados

| Arquivo | Tamanho (bytes) | SHA-256 |
| --- | --- | --- |
| `Nexo.exe` (dentro de `nexo-app.zip`) | 162098152 | `6132E99C4B15634B28E48EE442D6BC8CA4C7D6BED2D679CFD5435D1E99A90F78` |
| `releases/1.6.3/nexo-app.zip` | 67853086 | `A5D025DD163E3E2EA45DAFC69B3576C53C3A0B0304C9A547D8B3567BDE467038` |
| `releases/1.6.2/qwen3-8b-model.zip` (reutilizado) | 5225390118 | `385C1011D168E474E498FE3FA8C306A157F7A914F4B82F4FBA7F3465A394A7F3` |
| `releases/1.6.2/OllamaSetup.exe` (reutilizado) | 1657983656 | `6394E61DAEDC2DE736C9F76CBC58F30F9241BA1EB4C2F79EF75E833168DB8A60` |
| `NexoSetup.exe` 1.0.0 | 161680390 | `463F8C26C35253ED5691763F20E223595E3DAF4FB6B3A8A1CB58494471DD3748` |

Os hashes do ZIP, do modelo e do Ollama são os do manifesto assinado. O do `Nexo.exe` foi calculado em 08/11/2026 sobre o ZIP baixado da Oracle, cujo hash coincidiu com o manifesto. O do `NexoSetup.exe` foi conferido no mesmo dia sobre o arquivo público. Commit da versão: `834d02b`.

## [1.6.2]

- Detalhes das ferramentas legíveis em português, sem JSON no chat.
- Distribuição por manifesto assinado na Oracle e download do NVIDIA App.

Os pacotes de modelo e Ollama desta versão continuam publicados e foram reutilizados na 1.6.3. O hash do aplicativo 1.6.2 não foi registrado antes de o manifesto ser substituído.

## Versões anteriores

As novidades da 1.6.1, da 1.6.0 e da correção 1.5.2 estão descritas no [README](README.md). Os artefatos delas não foram registrados.
