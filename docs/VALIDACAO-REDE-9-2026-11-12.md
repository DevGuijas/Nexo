# Validação no PC do prazo e da saída segura da transação de rede (#9)

**Data:** 12/11/2026
**Código validado:** `development` em `25a06a0` (inclui o PR #78), compilado em Release.
**Ambiente:** Windows 11, interface Wi-Fi (`ifIndex` 6) com DNS automático (DHCP), DNS original `192.168.101.1`. UAC com confirmação (`ConsentPromptBehaviorAdmin = 5`).
**Executado por:** Rafael Castro.

## Como foi feito

O PC de teste não tem o Ollama, e a mudança de DNS só é disparada pelo agente. Por isso os testes **não passaram pela janela do Nexo**. Um programa de teste (fora do repositório) chama `NetworkChange.Apply` da `development` com o executor elevado real, o mesmo código que o agente usa, e registra cada etapa com horário. O pedido foi `set_dns` manual `1.1.1.1` na interface 6.

Os resultados abaixo são o relato de quem executou os testes. O programa de teste e os registros com horário não foram incluídos neste PR, então os resultados no PC não podem ser reproduzidos apenas com os arquivos versionados.

- Teste 2: terminal comum, UAC real.
- Testes 1 e 3: terminal elevado. O programa congela (`NtSuspendProcess`) ou encerra (`Kill`) o processo do executor. Como o terminal já era administrador, **o UAC não foi exibido** nesses dois.

## Resultados

| # | Critério da issue | Resultado | Evidência |
|---|---|---|---|
| 1 | Transação travada respeita o prazo e termina como estado desconhecido, sem restauração concorrente | **Passou** | Executor congelado logo após iniciar. Aos **150 s**: `state: "unknown"`, `timedOut: true`, `processRunning: true`, `phase: "applying"`, `reverted: false`. Mensagem: "não chegou à confirmação em 150 segundos e ainda está em execução... nenhuma restauração foi confirmada". **Nenhuma pergunta de restauração** e nenhum segundo UAC. |
| 2 | Recusar o UAC não deixa alerta falso nem transação pendente | **Passou, em parte** (alerta da interface não testado) | "Não" no UAC. Em **2 s**: `Win32Exception: A operação foi cancelada pelo usuário`. DNS inalterado. A pasta da operação ficou com `recovery-result.json` (`started: false`, `unchanged: true`, "Nenhuma alteração de rede foi feita") e **não** houve pasta protegida para essa operação. |
| 3 | Executor encerrado à força: restauração oferecida | **Passou, em parte** (ver limite abaixo) | Executor encerrado aos 7 s, com a pergunta "Manter a configuração de rede?" aberta. Sem resposta, aos 67 s (prazo de 60 s do executor elevado; a janela do aplicativo usa 45 s) o Nexo gravou o rollback, informou "O executor de rede terminou sem resultado. Oferecendo a restauração..." e fez a pergunta de restauração. Aceita, aos 71 s: `reverted: true`, DNS de volta ao automático (`192.168.101.1`, `StaticDns` vazio). |

Nos Testes 2 e 3, foi confirmado que o DNS terminou em `192.168.101.1`. No Teste 1, o executor ainda estava ativo aos 150 s e a transação permanecia em estado desconhecido; este relatório não comprova o estado final do DNS nessa rodada.

Antes do Teste 2 houve uma rodada em que o UAC foi aceito por engano. Ela exercitou o caminho normal: aplicou `1.1.1.1`, mostrou o estado e o aviso de 45 s, e restaurou ao responder "restaurar", em 14 s.

## O que esta validação não cobre

- **Transação numa VM Windows com snapshot.** Prevista no critério original da #9 e na #8; não foi feita (ver o fim deste relatório).
- **UAC nos Testes 1 e 3.** O terminal elevado dispensou o prompt. A recusa do UAC foi coberta separadamente no Teste 2.
- **Janela do Nexo e agente.** Os textos mostrados ao usuário nas janelas não foram exercitados, só as respostas e as perguntas do `NetworkChange.Apply`.
- **Suíte `Nexo.Checks` completa.** Neste PC ela parou com `Win32Exception (5): Acesso negado` ao iniciar o `powershell.exe` (em `NetworkRecoveryChecks.RoundTrip` ou `CompactRuns`), depois de 2.393 `PASS` e antes dos cenários simulados de prazo. A suíte do executor (`NetworkScriptChecks.ps1`) passou. A causa do acesso negado não foi apurada.

## Complemento de 13/11/2026: abertura seguinte na janela do Nexo

Mesmo PC e mesma interface (Wi-Fi, `ifIndex` 6, DNS original `192.168.101.1`). O Nexo (`development`, compilado em Release) foi aberto depois de cada simulação. O disparo da mudança continuou sendo o programa de teste, não o agente, mas o que apareceu na abertura é a varredura do próprio Nexo (`NetworkRecovery.Scan`).

| # | Critério | Resultado | Evidência |
|---|---|---|---|
| 3 (abertura seguinte) | Executor encerrado à força: restauração oferecida na próxima abertura | **Passou** | Programa de teste no modo `cair` em terminal elevado, com o DNS preso em `1.1.1.1`. O Nexo foi aberto às 19:00:29 e mostrou o diálogo "Restaurar configuração de rede anterior?", com o horário da alteração interrompida (13/11/2026 18:59), a configuração anterior salva ("Wi-Fi #6, DNS: automático"), a explicação de que a interface é conferida antes e o aviso de que a restauração pede permissão de administrador. Autorizado, com UAC confirmado, às 19:01:18. Às 19:01:21 o `recovery-result.json` registrou `restored: true`, `success: true` ("Configuração anterior restaurada e conferida.") e DNS `192.168.101.1`, sem DNS fixo. |
| 2 (abertura seguinte) | Recusar o UAC não deixa alerta falso | **Passou** | Programa de teste em terminal comum, "Não" no UAC. Operação das 19:05:01 gravada com `started: false`, `unchanged: true` ("A operação foi cancelada pelo usuário. Nenhuma alteração de rede foi feita."); DNS `192.168.101.1`. Ao abrir o Nexo em seguida, a tela inicial apareceu sem nenhum aviso de rede interrompida. |

Na primeira tentativa do dia, o Nexo foi aberto sem nenhuma queda simulada antes (nenhuma operação nova) e a tela inicial apareceu normalmente; essa abertura não conta como teste.

## Observação: início do executor negado de forma intermitente

Na primeira rodada do Teste 3, o executor falhou ao iniciar com `Win32Exception: ... Acesso negado` (aos 0 s). Um segundo depois o Teste 1 iniciou o executor normalmente, e a repetição do Teste 3 também. Nada foi alterado na rede (DNS inalterado). Vale investigar por que o Windows negou a criação do processo naquela vez, e se a mensagem mostrada ao usuário nesse caso é clara, já que não é o "cancelado pelo usuário" (Win32 1223).

## Para fechar a #9

Os três critérios da validação no PC (prazo, recusa do UAC e executor encerrado com restauração na abertura seguinte) passaram. Resta apenas a transação numa VM Windows com snapshot, prevista no critério original da #9 e já acompanhada na #8; ela não depende de código novo da #9.
