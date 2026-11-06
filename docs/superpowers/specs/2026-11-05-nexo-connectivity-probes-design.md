# Nexo: sondagens opcionais de conectividade

## Intenção

Mostrar o que foi observado na rede local, no gateway, na resolução de nomes e no acesso a um destino externo. Uma interface ativa, um ping sem resposta e uma página de teste indisponível são evidências diferentes. O Nexo não deve converter uma delas em diagnóstico definitivo da outra.

## Estado atual

`WindowsHealthSampler` usa a presença de uma interface ativa para marcar `Connectivity=local`. `HealthRules` só alerta após amostras recorrentes sem interface. O painel e o relatório exibem apenas esse estado. As configurações de saúde já são guardadas em `health-settings.json`; o monitor roda enquanto a janela está aberta.

## Abordagens

1. Ler apenas o estado NCSI do Windows: reduz requisições próprias, mas não separa gateway, DNS e o destino externo configurado.
2. Fazer sondagens curtas e independentes no monitor existente: oferece evidências separadas, funciona com testes simulados e mantém a coleta local mesmo sem Internet. **Escolhida.**
3. Usar um serviço remoto: criaria dependência externa e transmitiria dados mesmo quando o app está só em diagnóstico local.

## Comportamento

- Três opções independentes, desligadas por padrão: testar o gateway configurado na interface ativa, resolver um nome DNS e consultar uma URL HTTP(S). O nome, a URL e o texto esperado da resposta são configuráveis. O padrão editável usa o teste de conectividade documentado pela Microsoft.
- Cada leitura registra `disabled`, `ok`, `no_response`, `unexpected` ou `unavailable`, junto do alvo e de uma descrição curta. `no_response` e `unexpected` descrevem o teste; não provam defeito geral na rede. Falta de gateway fica `unavailable`. A ausência de interface local continua distinta.
- O ping é limitado a dois segundos por gateway e a quatro endereços; DNS e HTTP têm prazo de quatro segundos. Redirecionamentos HTTP não são seguidos. Uma resposta diferente do texto esperado é `unexpected`, inclusive em portal cativo ou proxy. A coleta respeita cancelamento e limita o corpo lido.
- Um achado de acesso externo só aparece após três amostras consecutivas com interface local ativa e resposta `no_response` ou `unexpected`, dentro do intervalo configurado. Uma leitura bem-sucedida, desativada ou indisponível encerra a condição. A mensagem cita possibilidades, sem atribuir uma causa específica.
- Painel, relatório local e documentação mostram os quatro estados separadamente. A configuração informa que habilitar DNS/HTTP envia consultas aos destinos escolhidos. A coleta de CPU, disco, serviços e histórico segue funcionando offline.

## Limites e referências

O resultado vale para os alvos configurados, não para toda a Internet. ICMP pode ser bloqueado por firewall; proxy, VPN, portal cativo e falha do próprio destino podem alterar a resposta. O Windows também trata essas situações como limites das sondagens: [visão geral do NCSI](https://learn.microsoft.com/en-us/windows-server/networking/ncsi/ncsi-overview), [FAQ do NCSI](https://learn.microsoft.com/en-us/windows-server/networking/ncsi/ncsi-frequently-asked-questions). O .NET permite [ping com prazo e cancelamento](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.ping?view=net-8.0) e [desabilitar redirecionamentos](https://learn.microsoft.com/dotnet/api/system.net.http.httpclienthandler.allowautoredirect).
