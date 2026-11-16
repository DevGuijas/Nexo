# Ensaio parcial na VM: memória em crescimento

Data: 16/11/2026. Cenário 5 do [protocolo de avaliação](AVALIACAO-MONITORAMENTO-E-RECUPERACAO.md), ligado à issue #8.

## Ambiente e método

- VM `NexoLab-Win11`: Windows 11 Enterprise Evaluation pt-BR, 4 vCPU, 8 GiB de RAM, adaptador NAT. O snapshot `baseline-clean-win11` estava disponível. A rede do computador anfitrião não foi alterada.
- Nexo 1.7.0 executado na VM, com monitoramento local em leituras periódicas. Um processo PowerShell de teste alocou e tocou páginas de memória em etapas; a carga era conhecida e legítima, não um vazamento comprovado. Nenhuma ação corretiva foi autorizada ou executada.
- Comparei o log do processo com amostras e achados gravados em `operations.db`, a medição de memória do Windows e o painel **Saúde do PC**. Os horários abaixo são da VM (`-03:00`).

## Alerta de memória do computador

A rampa rápida alocou quatro blocos de 896 MiB no processo PID 9276. O processo começou às 07:37:37. Uma rampa mais lenta, interrompida antes desta, serviu para calibrar a carga e não gerou alerta de memória do computador.

| Horário | Memória usada observada pelo Nexo | Resultado |
| --- | ---: | --- |
| 07:37:25 | 40,4% | Leitura anterior à rampa rápida |
| 07:37:56 | 51,8% | Primeiro bloco visível |
| 07:38:38 | 63,2% | Crescimento em andamento |
| 07:39:23 | 74,5% | Crescimento em andamento |
| 07:40:06 | 83,8% | Achado criado: **“Memória em crescimento persistente”**, evidência “De 63% para 84% em três leituras” |
| 07:40:40 | 84,2% | Patamar alto ainda observado |
| 07:41:05 em diante | Cerca de 41–42% | O Windows voltou a informar memória disponível; o Nexo não atribuiu causa ao processo sem cumprir a regra de identidade |

O alerta surgiu **2 min 26 s** depois do início da rampa. O painel mostrou 84% e a ocorrência preventiva. O processo terminou naturalmente às 07:43:04; após três leituras seguidas sem a condição, o painel mostrou **“Resolvida: 3 leituras seguidas sem a condição”**. Capturas: [uso em 84%](evidencias/vm-memoria-84.png) e [ocorrência resolvida](evidencias/vm-memoria-resolvida.png).

Há uma limitação importante: por volta de 07:41, o Windows passou a informar cerca de 4,7 GiB disponíveis embora o PID ainda apresentasse aproximadamente 3,6 GiB de working set. A causa dessa divergência não foi determinada neste ensaio. Por isso a resolução descreve somente as leituras do Windows; ela não prova que o processo liberou a alocação naquele instante.

## Identificação do processo

Numa segunda rampa do mesmo cenário, o PID 5584 começou às 07:43:48 e aguardou antes de alocar memória, para que o Nexo não confundisse a inicialização do processo com crescimento persistente. Quatro leituras do mesmo PID e horário de início registraram 600, 1.112, 1.625 e 2.137 MB entre 07:45:48 e 07:47:37. O Nexo criou **“Processo com memória em crescimento persistente”**, identificando `powershell` PID 5584 e a subida de **600 para 2.137 MB em quatro leituras e 19 pontos da RAM**. Às 07:49, a interface mostrava **“Persiste”** e 2.650 MB atuais. O texto avisava que crescimento observado não distingue vazamento de carga legítima. [Captura do alerta por processo](evidencias/vm-memoria-processo.png).

O processo terminou naturalmente às 07:50:29. As leituras de 07:50:32, 07:51:05 e 07:51:29 deixaram de encontrá-lo; o painel mudou a ocorrência para **“Resolvida: 3 leituras seguidas sem a condição”**, sem afirmar que algum aplicativo voltou a funcionar. [Captura da resolução do processo](evidencias/vm-memoria-processo-resolvido.png).

## Alcance da evidência

O ensaio confirma detecção e registro visual de crescimento real de memória e identificação do processo nesta VM. A carga foi criada para o teste; não houve medição de resposta de outro aplicativo nem estimativa de precisão ou recall. Os demais seis cenários da #8, os controles negativos e a repetição em outras máquinas continuam pendentes. O resultado do executor sozinho não seria suficiente para declarar uma recuperação funcional.
