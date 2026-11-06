# Avaliação do monitoramento e da recuperação do Nexo

## Escopo implementado

O Nexo coleta métricas locais enquanto a janela está aberta, guarda amostras e ocorrências em SQLite no perfil do Windows, consulta eventos System/Application/Setup, calcula achados por regras e apresenta um painel de saúde. O Qwen3 8B via Ollama pode consultar `get_health_report`; o relatório determinístico permanece disponível se o modelo estiver indisponível. Intervenções continuam na lista fechada de ferramentas, com confirmação específica e UAC quando necessário. O executor persiste proposta, autorização, início e resultado relatado ou estado desconhecido, com leituras anteriores estruturadas quando disponíveis. O INF instalado pode ser consultado para um dispositivo previamente inventariado.

Antes de uma instalação de driver autorizada, o Nexo pede ao Windows que crie um ponto de restauração e verifica o número de sequência. Se a proteção não for confirmada, a instalação para. O [Checkpoint-Computer da Microsoft](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/checkpoint-computer?view=powershell-5.1) é restrito a sistemas cliente e pode recusar um segundo ponto no mesmo dia; esse resultado deve ser registrado como limitação, não como proteção existente.

O banco `operations.db` não contém conversas e não migra `data.json`. A retenção padrão é 30 dias; intervalo padrão de 30 segundos; limiares padrão de 90% de CPU e 10% de espaço livre. As preferências ficam em `health-settings.json`. Temperatura da CPU e SMART permanecem `indisponível` quando o Windows não fornece leitura confiável. O estado de conectividade do painel representa interfaces locais ativas, não acesso à Internet.

## Protocolo experimental

Use uma VM Windows isolada com snapshot e um operador presente. Anote versão do Windows, hardware virtual, horário, configuração inicial, sintomas, fonte de alteração e restauração. Abra o Nexo e aguarde leituras suficientes para cada regra. Capture o painel, o log operacional, a conversa, a autorização, a observação depois da mudança e o teste funcional. Execute cada cenário isoladamente e restaure o snapshot antes do próximo. Não aplique falhas de rede, serviços ou drivers no computador de trabalho.

| Cenário | Falha controlada | Detecção esperada | Falso positivo a verificar | Recuperação e validação |
| --- | --- | --- | --- | --- |
| 1 | Adaptador usado desativado | Adaptador `Disabled` e ausência de rede local em duas amostras; inventário de rede identifica índice exato | Adaptador intencionalmente desativado enquanto outro fornece conectividade | Ativação só após confirmação; conferir estado e teste de conexão na mesma interface |
| 2 | IP fixo e gateway incompatíveis | Análise de sub-rede identifica inconsistência, sem afirmar que todo IP fixo está errado | Interface auxiliar ou rede isolada sem rota externa | Propor DHCP somente para interface candidata, com backup e confirmação; verificar concessão, DNS/TCP e restauração se falhar |
| 3 | DNS incorreto com TCP por IP funcionando | Resumo indica conectividade parcial; falha DNS separada de TCP | Nome de teste indisponível isolado | Confirmar servidores esperados antes de alterar; testar nome e IP novamente |
| 4 | Serviço automático relevante parado | Duas leituras com `StartMode=Auto` e `Stopped` | Serviço manual parado não gera alerta | Consultar eventos e sintoma; reinício autorizado; conferir estado e função no aplicativo |
| 5 | Memória em crescimento | Três leituras crescentes, subida de 20 pontos e último uso ≥80% | Pico isolado ou lacuna entre leituras | Identificar processo antes de fechar; confirmar redução e resposta do aplicativo |
| 6 | Driver ausente/com erro | Inventário PnP observa código de erro e associa INF/versão ao ID exato quando disponível | Oferta para outro HardwareID ou múltiplos dispositivos | Fonte do Windows ou fabricante permitido, confirmação e UAC; verificar versão/código e teste funcional; reinício pode ficar pendente |
| 7 | Volume com pouco espaço | Duas leituras abaixo de 10% livre | Volume removível, leitura única ou erro de acesso | Orientar revisão de uso; não apagar arquivos automaticamente; confirmar espaço recuperado |

## Evidência nesta implementação

`Nexo.Checks` executa cenários simulados para os sete casos, incluindo exemplos negativos e estados sem validação funcional. Os três primeiros reutilizam avaliação de rede e evidência de conexão; os demais verificam regras, associação do INF e resultado de instalação não confirmado. A suíte também cobre retenção e corrupção da base, persistência de amostras, lacunas, supressão de alertas repetidos, intervalo longo de coleta, coleta parcial de eventos, relatório sem evidência, injeção em nome de provedor, recusa, timeout, cancelamento de operação elevada e bloqueio de driver sem ponto de restauração. Os testes comuns não alteram o Windows.

Foram executadas leituras reais somente de CPU, memória, discos, serviços, interfaces e eventos. Essas leituras mostram que os sensores e canais funcionam neste Windows, sem demonstrar uma falha ou recuperação real. Os sete cenários de laboratório com alteração deliberada e a avaliação de precisão/recall em várias máquinas permanecem pendentes. Não houve treinamento ou ajuste dos pesos do modelo.

## Indicadores para o TCC

Por cenário, registre tempo até detecção, verdade da falha, alertas falsos, hipótese proposta, autorização, resultado do executor, teste funcional e necessidade de encaminhamento. Calcule precisão e recall somente após rotular casos reais; a suíte simulada mede comportamento do código e não deve entrar como resultado de eficácia no hardware.
