# Autenticação da decisão de rede (#11)

## Objetivo

Um processo comum da mesma sessão não pode manter uma alteração de rede apenas criando ou modificando `decision.txt`. O Nexo só deve manter a alteração depois da confirmação explícita exibida ao usuário. Sem confirmação autenticada, o executor elevado restaura a configuração anterior.

## Limite de confiança

O executor elevado confia no PID do aplicativo que o iniciou e o aplicativo confia no PID do executor que iniciou. Esses PIDs são conferidos pelo Windows nas duas pontas de um pipe local. O nome do pipe e os PIDs não são segredos. Arquivos na pasta do usuário podem sinalizar restauração ou registrar andamento, mas nunca autorizar `keep`. O modelo não tenta defender uma máquina em que outro processo já consiga injetar código no Nexo ou no executor elevado.

## Fluxo

1. O aplicativo cria um nome de pipe exclusivo para a operação e passa nome e próprio PID ao executor, junto com o pedido de rede.
2. Após aplicar e conferir a configuração, o executor grava `applied.json` na pasta protegida e espera uma conexão ao pipe por no máximo 60 segundos. Ele verifica que o servidor do pipe pertence ao PID do aplicativo. Resposta ausente, inválida ou conexão de outro servidor significa restauração.
3. O aplicativo só abre o servidor depois que a confirmação na janela terminar. Ele verifica que o cliente conectado pertence ao PID do executor elevado, envia uma única linha `keep` ou `rollback` e encerra o pipe. Conexões de outros processos são recusadas; falha de entrega resulta em restauração ou estado incerto, nunca sucesso presumido.
4. `decision.txt` continua podendo registrar `rollback` para cancelamento durante a aplicação e para diagnóstico, mas o script nunca usa seu conteúdo para autorizar `keep`.

## Evidência necessária

- Teste de `keep` forjado em `decision.txt` antes de `applied.json`: nenhum sucesso e nenhuma confirmação pulada.
- Testes de conexão de processo estranho ao pipe e servidor de PID errado: nenhum `keep` aceito.
- Testes de confirmação legítima, restauração, cancelamento, prazo e encerramento do aplicativo.
- Compilação do app e instalador, `Nexo.Checks`, `Nexo.Setup.Checks`, `NetworkScriptChecks.ps1` e CI.
- Validação de UAC e transação real na VM Windows da #8 antes de fechar a issue.

## Limites

Uma pasta gravável pelo usuário ainda permite negar serviço ao gravar `rollback` antecipadamente. Isso só acelera a restauração, sem manter uma alteração. Sem VM com snapshot, os testes locais não comprovam a transação elevada completa.
