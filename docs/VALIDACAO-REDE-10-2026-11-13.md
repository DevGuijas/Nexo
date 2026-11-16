# Validação no PC da recuperação da rede (#10)

**Data:** 13/11/2026
**Código:** `development` em `3caf53a`, compilado em Release, e a correção do carregador do executor descrita abaixo.
**Ambiente:** Windows 11, interface Wi-Fi (`ifIndex` 6) com DNS automático (DHCP), DNS original `192.168.101.1`. UAC com confirmação (`ConsentPromptBehaviorAdmin = 5`).
**Executado por:** Rafael Castro.

## Como foi feito

O PC de teste não tem modelo no Ollama, e a mudança de DNS só é disparada pelo agente. Um programa de teste (fora do repositório) chama `NetworkChange.Apply` da `development` com o executor elevado real, o mesmo código que o agente usa. As telas do Nexo (varredura de abertura, oferta e restauração) foram usadas de verdade. O pedido foi `set_dns` manual na interface 6.

## Resultados

| # | Critério da issue | Resultado | Evidência |
|---|---|---|---|
| 1 | `icacls` na pasta protegida durante o `set_dns`: herança desligada, dono Administradores, Usuários só com leitura | **Passou** | Terminal comum, UAC real, com a pergunta "Manter a configuração de rede?" aberta e a pasta da operação criada. `icacls` e `GetAccessControl` na pasta da operação, em `%ProgramData%\NexoSupport\operations`, em `%ProgramData%\NexoSupport` e nos arquivos `applied.json`, `before.json` e `transaction.json`: dono `BUILTIN\Administradores`, herança desligada em todos. Pastas: `SISTEMA (OI)(CI)(F)`, `Administradores (OI)(CI)(F)`, `Usuários (OI)(CI)(RX)`. Arquivos: `SISTEMA (F)`, `Administradores (F)`, `Usuários (RX)`. Repetido depois da correção do item 5, com o mesmo resultado. |
| 2 | Transação forjada em `%LOCALAPPDATA%` (DNS `203.0.113.66`) não gera oferta nem UAC | **Passou** | Pasta de operação criada à mão em `%LOCALAPPDATA%\NexoSupport\operations`, com `transaction.json` de uma hora antes e `before.json` com `StaticDns` e `Dns` `203.0.113.66`. Ao abrir o Nexo, a tela mostrou só o aviso "Etapa não concluída… A transação de rede foi interrompida antes de salvar o estado anterior… Nenhuma restauração é necessária.", sem pergunta de restauração. DNS continuou `192.168.101.1` e nenhuma pasta protegida foi criada. |
| 3 | Pasta `%ProgramData%\NexoSupport` criada antes por outro processo é recusada | **Passou** | A pasta verdadeira foi renomeada por um instante, e uma pasta `NexoSupport` foi criada sem elevação (dono `RAFAEL\rafae`, herança ligada). Troca de DNS com UAC aceito: em 5 s, `success: false`, `protectedFolderProblem: true`, "a cadeia %ProgramData%\NexoSupport… tem uma condição que bloqueia esta operação (C:\ProgramData\NexoSupport: dono não confiável…)" com as instruções de liberação. DNS inalterado; nada foi gravado na pasta hostil. A pasta verdadeira foi devolvida (dono Administradores, 7 operações). |
| 4 | Executor encerrado à força: restauração oferecida e aplicada | **Passou, depois da correção do item 5** | Executor encerrado com a pergunta aberta e o programa de teste derrubado, DNS preso em `1.1.1.1`. Ao abrir o Nexo, apareceu "Restaurar configuração de rede anterior?" com a configuração salva e o aviso de permissão de administrador. Autorizado com UAC, às 19:48:15 o `recovery-result.json` registrou `restored: true` ("Configuração anterior restaurada e conferida.") e DNS `192.168.101.1`, sem DNS fixo. Antes da correção, duas tentativas de restauração falharam com "Acesso negado" ao iniciar o `powershell.exe` (ver abaixo); o Nexo não alterou nada e ofereceu de novo. Uma execução anterior no mesmo dia (18:59, `development` em `25a06a0`) já tinha restaurado com sucesso. |
| 5 | Carregador GZip funciona sob UAC no Windows PowerShell 5.1 | **Funciona, mas o Defender o bloqueava; corrigido** | O executor compactado iniciou, aplicou o DNS e restaurou sob UAC real nos itens 1, 3 e 4. O problema está na próxima seção. |

## Achado: o Defender bloqueia o carregador do executor (causa do "Acesso negado")

O "Acesso negado" intermitente ao iniciar o `powershell.exe` elevado, visto desde 11/11 e que também derrubava a suíte `Nexo.Checks` neste PC, vem do Microsoft Defender. O log `Microsoft-Windows-Windows Defender/Operational` (eventos 1116 e 1117) registra a ameaça `!#SLF:CMD_HSTR:General.ML.B` (cavalo de Troia, detecção por heurística de linha de comando) sobre a linha `powershell.exe -NoProfile -NonInteractive -EncodedCommand …` com o carregador GZip. Houve 10 detecções entre 11 e 13/11, e os horários batem com as falhas: 12/11 às 21:20:25 e 21:20:40, e 13/11 às 19:31:02 e 19:32:42, quando o Nexo não conseguiu iniciar a restauração.

Medição sem UAC, iniciando o `powershell.exe` 20 a 40 vezes por variante com o script real do executor, que sai logo no início:

| Variante da linha de comando | Inícios | Bloqueados |
|---|---|---|
| Carregador GZip em `-EncodedCommand` (como estava) | 50 | 7 |
| O mesmo carregador GZip em `-Command`, em texto | 40 | 0 |
| Script lido de arquivo e conferido por SHA-256, em `-Command` | 30 | 0 |

Correção: o carregador GZip do executor de rede agora vai em `-Command`, sem `-EncodedCommand` (`NetworkRecovery.ElevatedArguments`). O conteúdo, o limite de linha de comando (agora cerca de 10,6 mil caracteres) e o fato de nada ser gravado em disco antes da elevação não mudam.

Depois da correção:

- `Nexo.Checks` completa neste PC: 2.590 verificações aprovadas, nenhuma falha. Antes, parava com `Win32Exception (5)` ao iniciar o `powershell.exe`.
- O programa de teste e a restauração pelo Nexo (itens 1 e 4) rodaram sob UAC real sem nenhuma detecção do Defender na janela.

A amostra depois da correção é pequena, e a heurística do Defender pode mudar com o tempo.

## O que esta validação não cobre

- **Agente e Ollama.** A mudança de DNS foi disparada pelo programa de teste, não pelo agente.
- **Mensagem para o usuário da pasta hostil.** O texto mostrado na janela do Nexo nesse caso é o assunto da #97; aqui foi conferido o resultado devolvido por `NetworkChange.Apply`.
- **VM Windows com snapshot.** Segue na #8.
- **`Nexo.exe` compilado.** O Smart App Control bloqueou o `Nexo.exe` recompilado, sem assinatura; o Nexo foi aberto com `dotnet Nexo.dll`, a partir do mesmo código.

## Complemento: interrupções por fase na VM (16/11/2026)

O ensaio usou a VM `NexoLab-Win11` (Windows 11, NAT, snapshot `baseline-clean-win11`) e o executor elevado real. O DNS inicial da interface de teste era `8.8.8.8,1.1.1.1` e o IP era `10.0.2.15`. Um programa de teste fora do repositório injetou `Environment.Exit(137)` em pontos definidos do script, sem alterar o código de produção. O usuário aprovou os UACs da VM. O fluxo de rede usado na VM corresponde ao código atual: não há diferença entre `d93d437` e `b4318fc` em `NetworkChange.cs`, `NetworkRecovery.cs`, `NetworkTransaction.ps1` ou `MainWindow.xaml.cs`.

| Ponto da interrupção | Estado observado e comportamento ao reabrir o Nexo |
| --- | --- |
| Antes de gravar a transação e o backup protegidos | A pasta protegida da operação ficou vazia. O DNS e o IP não mudaram. O Nexo não ofereceu uma restauração sem backup nem tentou alterar a rede. |
| Depois do backup, antes do comando DNS | O backup e a transação protegidos existiam, mas a rede continuava no estado inicial. O Nexo ofereceu a recuperação; após a autorização, conferiu o estado e informou que nenhuma mudança era necessária (`alreadyRestored=true`). |
| Depois do comando DNS, antes de `applied.json` | O DNS ficou em `9.9.9.9`. O Nexo detectou a pendência, mesmo com um `result.json` local falso marcado como sucesso, e restaurou o DNS inicial. O resultado protegido registrou `restored=true` e `success=true`; a leitura independente do Windows confirmou DNS e IP finais. |
| Depois de `applied.json`, antes do resultado protegido | O Nexo ofereceu a restauração na abertura e recuperou o DNS inicial. Esse cenário já foi registrado no relatório de ensaio anterior na Área de Trabalho. |

O aplicativo retornou estado desconhecido quando o executor parou sem resultado; não declarou restauração antes de conferi-la. Os logs, hashes dos programas de teste e capturas da VM estão em `Nexo-Teste-Issue-10-Fases-VM.md`, na Área de Trabalho do operador. Nenhuma configuração de rede do anfitrião foi alterada.

**A #10 continua aberta.** Faltam interrupções isoladas durante a confirmação e depois do comando de restauração, antes do resultado protegido. A revisão automática de segurança desta sessão rejeitou o envio do próximo programa de teste à VM; o ensaio foi interrompido nesse ponto, sem contornar o bloqueio.
