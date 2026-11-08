# Histórico de versões do Nexo

Cada versão publicada é imutável: os arquivos de `releases/<versão>/` no armazenamento da Oracle não são substituídos. Uma correção depois da publicação ganha uma versão nova. O aplicativo (`Nexo.Support`) e o instalador (`Nexo.Setup`) têm versões independentes; o instalador só muda de versão quando o próprio código dele muda, porque ele sempre baixa a versão indicada no `latest.json` assinado.

A data no título de uma versão é o dia em que o `latest.json` dela foi publicado. Enquanto o título disser "não publicada", `scripts/Build-CloudRelease.ps1` se recusa a gerar os pacotes.

## [1.7.0] — não publicada

Aplicativo 1.7.0. O instalador continua na 1.0.0, porque o código dele não mudou desde a 1.6.3.

### Antes de publicar

- [ ] Validar as sete falhas controladas em VM Windows (#8).
- [ ] Concluir as correções críticas da recuperação: prazo e saída segura para transações de rede travadas (#9), recuperação após encerramento abrupto do executor de rede (#10), autenticação da decisão de manter uma mudança de rede (#11) e prazo da instalação NVIDIA alinhado ao supervisor (#12).
- [ ] Trocar "não publicada" pela data de publicação, gerar os pacotes e registrar abaixo o commit e os hashes impressos pelo script.

### Saúde do computador

- Página **Saúde do PC** com CPU, memória, volumes, serviços automáticos, adaptadores e eventos do Windows coletados enquanto a janela está aberta.
- Histórico operacional em SQLite (`operations.db`), separado das conversas, com retenção configurável.
- Ocorrências persistentes com estado atual: persiste, resolvida ou desconhecida. Resolver exige leituras seguidas sem a condição.
- Crescimento de memória por processo, identificado por PID e horário de início.
- Sondagens opcionais de gateway, DNS e destino HTTP(S), desligadas por padrão.
- Serviços automáticos que o Windows deixa parados de propósito (início atrasado ou por gatilho com código 0) não geram alerta.
- Limiares digitados com ponto não são mais multiplicados por dez.

### Diagnóstico e assistente

- Relatório de saúde disponível ao assistente (`get_health_report`) e sem o modelo, com todas as ocorrências das últimas 24 horas.
- Coleta incremental de eventos System, Application e Setup, sem perder rajadas entre consultas e reconhecendo reinício do log.
- INF e versão do driver associados ao dispositivo com erro.
- Eventos do serviço consultados antes de propor o reinício.
- Triagem reconhece falta de espaço, lentidão e sites que não abrem.
- Resposta final cita a evidência trazida pelas ferramentas.
- Com mais de um alvo possível, só o nome sozinho escolhe o dispositivo, a impressora ou a oferta de driver.

### Recuperação e drivers

- Ações elevadas supervisionadas: sem resultado no prazo, a operação fica em estado desconhecido, nunca como concluída.
- Ponto de restauração confirmado antes de instalar driver; sem ele, a instalação para.
- Retomada salva quando o driver pede reinício.
- Instalação NVIDIA preserva o áudio HDMI, reconhece GeForce sem o prefixo NVIDIA e confere a versão instalada com a oferta.

### Rede

- DNS fixo com DHCP apontado, teste TCP por IP e preferência por DNS automático.

### Interface e repositório

- Navegação, formulários e confirmações redesenhados, com tema claro e ícones vetoriais.
- Título da janela lido da versão do aplicativo, sem número fixo no código.
- CI na `development` com compilação e regressões.

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
