# Histórico de versões do Nexo

Cada versão publicada é imutável: os arquivos de `releases/<versão>/` no armazenamento da Oracle não são substituídos. Uma correção depois da publicação ganha uma versão nova. O aplicativo (`Nexo.Support`) e o instalador (`Nexo.Setup`) têm versões independentes; o instalador só muda de versão quando o próprio código dele muda, porque ele sempre baixa a versão indicada no `latest.json` assinado.

A data no título de uma versão é o dia em que o `latest.json` dela foi publicado. Enquanto o título disser "não publicada", `scripts/Build-CloudRelease.ps1` se recusa a gerar os pacotes.

## [1.7.0] — não publicada

Aplicativo 1.7.0. O instalador continua na 1.0.0, porque o código dele não mudou desde a 1.6.3.

### Antes de publicar

- [x] **Prioridade alta:** o executor de rede elevado deixa de gravar resultados na pasta do usuário (`%LOCALAPPDATA%`) e passa a gravar só na pasta protegida em `%ProgramData%` (#95, PR #96, validado no PC com UAC real em 10/11/2026).
- [ ] Investigar com o log do Ollama ativo as etapas expiradas na 1ª chamada ao modelo, antes de mudar o limite de 3 minutos (#93, item 4).
- [ ] Validar as sete falhas controladas em VM Windows (#8).
- [ ] Autenticar a decisão de manter uma mudança de rede elevada (#11) e alinhar o prazo da instalação NVIDIA ao supervisor (#12).
- [x] Validar no PC o patamar estável de memória com o NexoMemTeste-toca (#71): "Persiste", "Estável em patamar alto" depois de 31 min, volta a "Persiste" com +15% e "Resolvida" com o processo encerrado, na 76d9028 em 10/11/2026.
- [ ] Validar no PC: a recuperação e o prazo da rede com ACL e UAC reais (#9, #10); o custo da coleta de sensores, com e sem o Libre Hardware Monitor, e os discos atrás do AMD-RAID (#19); de novo, depois dos ajustes da validação de 10/11, o disco e a memória na resposta (#88: loop do disco, "pouca espaço", linha dos MB e alerta do volume removido) e as respostas da #93 (letra de unidade perguntada como "o que é o D:?", descrição de processo sem evidência); e o patamar estável na resposta (#71: não culpar o processo pela lentidão).
- [ ] Trocar "não publicada" pela data de publicação, gerar os pacotes e registrar abaixo o commit e os hashes impressos pelo script.

### Saúde do computador

- Página **Saúde do PC** com CPU, memória, volumes, serviços automáticos, adaptadores e eventos do Windows coletados enquanto a janela está aberta.
- Histórico operacional em SQLite (`operations.db`), separado das conversas, com retenção configurável.
- Ocorrências persistentes com estado atual: persiste, resolvida ou desconhecida. Resolver exige leituras seguidas sem a condição. O alerta de pouco espaço de um volume que deixou de existir (um VHDX desmontado) é resolvido quando as leituras listam os outros volumes sem falha e não o encontram ("Resolvida: o volume não está mais presente…"); um volume fixo que não está pronto (BitLocker bloqueado) não conta como removido nem deixa a coleta parcial (#88).
- Crescimento de memória por processo, identificado por PID e horário de início. Um processo que se estabiliza num patamar alto por 30 minutos passa a "Estável em patamar alto", informativo; volta a "Persiste" se subir 10% sobre a referência gravada na ocorrência (#71).
- Sondagens opcionais de gateway, DNS e destino HTTP(S), desligadas por padrão.
- Serviços automáticos que o Windows deixa parados de propósito (início atrasado ou por gatilho com código 0) não geram alerta.
- Temperatura da CPU pelo Libre Hardware Monitor (sensor de pacote ou die) e estado dos discos físicos pelo Windows Storage, com origem e horário da leitura; sem fonte confiável, o painel diz "indisponível" e o motivo (#19, PR #79).
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

### Recuperação e drivers

- Ações elevadas supervisionadas: sem resultado no prazo, a operação fica em estado desconhecido, nunca como concluída.
- Ponto de restauração confirmado antes de instalar driver; sem ele, a instalação para.
- Retomada salva quando o driver pede reinício.
- Instalação NVIDIA preserva o áudio HDMI, reconhece GeForce sem o prefixo NVIDIA e confere a versão instalada com a oferta.
- A pergunta de qual dispositivo do fluxo de driver passa a aceitar resposta, e lista só dispositivos com driver próprio, sem os endpoints de áudio (#74, #84).
- O pedido de aprovação mostra o driver atual do dispositivo ("Driver: …") e, no reinício de serviço, o estado atual dele ("Estado atual: parado (início automático).") (#84, #88).

### Impressão

- Spooler parado é lido e informado na consulta de impressoras, sem falhar a consulta (#84).

### Rede

- DNS fixo com DHCP apontado, teste TCP por IP e preferência por DNS automático.
- Transação de rede interrompida (executor elevado encerrado no meio) é detectada na hora ou na próxima abertura e, se a mudança ficou pendente, o Nexo oferece restaurar a configuração anterior, com autorização e UAC. O backup confiável fica numa cópia protegida em `%ProgramData%`, e o que vem da pasta do usuário não é aplicado (#10, PR #76).
- Prazo total de 5 minutos para a transação de rede, por fase; esgotado o prazo, o estado fica desconhecido e a resposta diz que nenhuma restauração foi confirmada, sem encerrar nem disputar com o executor ainda ativo (#9, PR #78).
- O executor de rede elevado grava o estado aplicado e o resultado só na pasta protegida; nada é gravado com elevação em `%LOCALAPPDATA%`, e a troca de IP/DNS não trava mais antes do UAC (#95, PR #96).
- Restaurar na janela "Manter a configuração de rede?" aparece como escolha do usuário: a resposta diz que a configuração anterior foi restaurada a pedido e conferida, o cartão mostra "Restaurado a seu pedido" e a linha de fatos lista a mudança em "Desfeitas a seu pedido", em vez de "Etapa não concluída… confira o erro no cartão". Prazo esgotado, cancelamento e restauração que falha continuam como antes (#98).

### Interface e repositório

- Navegação, formulários e confirmações redesenhados, com tema claro e ícones vetoriais.
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
