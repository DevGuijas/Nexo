# Validação no PC do prazo e da saída segura da transação de rede (#9)

**Data:** 12/11/2026
**Código validado:** `development` em `25a06a0` (inclui o PR #78), compilado em Release.
**Ambiente:** Windows 11, interface Wi-Fi (`ifIndex` 6) com DNS automático (DHCP), DNS original `192.168.101.1`. UAC com confirmação (`ConsentPromptBehaviorAdmin = 5`).
**Executado por:** Rafael Castro.

## Como foi feito

O PC de teste não tem o Ollama, e a mudança de DNS só é disparada pelo agente. Por isso os testes **não passaram pela janela do Nexo**. Um programa de teste (fora do repositório) chama `NetworkChange.Apply` da `development` com o executor elevado real, o mesmo código que o agente usa, e registra cada etapa com horário. O pedido foi `set_dns` manual `1.1.1.1` na interface 6.

- Teste 2: terminal comum, UAC real.
- Testes 1 e 3: terminal elevado. O programa congela (`NtSuspendProcess`) ou encerra (`Kill`) o processo do executor. Como o terminal já era administrador, **o UAC não foi exibido** nesses dois.

## Resultados

| # | Critério da issue | Resultado | Evidência |
|---|---|---|---|
| 1 | Transação travada respeita o prazo e termina como estado desconhecido, sem restauração concorrente | **Passou** | Executor congelado logo após iniciar. Aos **150 s**: `state: "unknown"`, `timedOut: true`, `processRunning: true`, `phase: "applying"`, `reverted: false`. Mensagem: "não chegou à confirmação em 150 segundos e ainda está em execução... nenhuma restauração foi confirmada". **Nenhuma pergunta de restauração** e nenhum segundo UAC. |
| 2 | Recusar o UAC não deixa alerta falso nem transação pendente | **Passou** | "Não" no UAC. Em **2 s**: `Win32Exception: A operação foi cancelada pelo usuário`. DNS inalterado. A pasta da operação ficou com `recovery-result.json` (`started: false`, `unchanged: true`, "Nenhuma alteração de rede foi feita") e **não** houve pasta protegida para essa operação. |
| 3 | Executor encerrado à força: restauração oferecida | **Passou, em parte** (ver limite abaixo) | Executor encerrado aos 7 s, com a pergunta "Manter a configuração de rede?" aberta. Sem resposta, aos 67 s (prazo de 60 s da janela) o Nexo gravou o rollback, informou "O executor de rede terminou sem resultado. Oferecendo a restauração..." e fez a pergunta de restauração. Aceita, aos 71 s: `reverted: true`, DNS de volta ao automático (`192.168.101.1`, `StaticDns` vazio). |

Em todos os testes o DNS terminou em `192.168.101.1`.

Antes do Teste 2 houve uma rodada em que o UAC foi aceito por engano. Ela exercitou o caminho normal: aplicou `1.1.1.1`, mostrou o estado e o aviso de 45 s, e restaurou ao responder "restaurar", em 14 s.

## O que esta validação não cobre

- **Abertura seguinte do Nexo (Teste 3 e Teste 2).** O Teste 3 verificou a restauração oferecida na mesma chamada, depois que o executor morreu. Não foi aberta uma nova instância do Nexo para ver a oferta pela varredura de abertura (`NetworkRecovery.Scan`). Pelo mesmo motivo, não foi conferido na interface que, depois de recusar o UAC, a abertura seguinte não mostra alerta. Esses dois pontos seguem cobertos só pelas checagens automatizadas.
- **UAC nos Testes 1 e 3.** O terminal elevado dispensou o prompt. A recusa do UAC foi coberta separadamente no Teste 2.
- **Janela do Nexo e agente.** Os textos mostrados ao usuário nas janelas não foram exercitados, só as respostas e as perguntas do `NetworkChange.Apply`.
- **Suíte `Nexo.Checks` completa.** Neste PC ela parou com `Win32Exception (5): Acesso negado` ao iniciar o `powershell.exe` (em `NetworkRecoveryChecks.RoundTrip` ou `CompactRuns`), depois de 2.393 `PASS` e antes dos cenários simulados de prazo. A suíte do executor (`NetworkScriptChecks.ps1`) passou. A causa do acesso negado não foi apurada.

## Observação: início do executor negado de forma intermitente

Na primeira rodada do Teste 3, o executor falhou ao iniciar com `Win32Exception: ... Acesso negado` (aos 0 s). Um segundo depois o Teste 1 iniciou o executor normalmente, e a repetição do Teste 3 também. Nada foi alterado na rede (DNS inalterado). Vale investigar por que o Windows negou a criação do processo naquela vez, e se a mensagem mostrada ao usuário nesse caso é clara, já que não é o "cancelado pelo usuário" (Win32 1223).

## Para fechar a #9

Falta conferir, com a janela do Nexo, (a) a oferta de restauração na abertura seguinte depois de encerrar o executor e (b) a ausência de alerta depois de recusar o UAC. Isso exige o Ollama (ou outro caminho que dispare `set_dns` pelo Nexo) ou a validação no PC de teste do Victor.
