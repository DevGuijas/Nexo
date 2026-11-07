# Limpeza dos diretórios do teste de eventos legados

## Intenção

A suíte `Nexo.Checks` deve terminar sem deixar diretórios `NexoEventsLegacy-*` no `%TEMP%`. Uma falha de limpeza precisa aparecer como falha da suíte.

## Escopo e limites

O ajuste fica em `Nexo.Checks/EventCollectionChecks.cs`. A migração do banco legado e o código do aplicativo não mudam. A confirmação precisa ocorrer no Windows, onde um arquivo SQLite aberto bloqueia a exclusão.

## Abordagens

- **Escolhida:** desativar pooling na única conexão SQLite criada pelo teste legado e deixar `Directory.Delete` relatar a falha. Mantém a conexão restrita ao teste e mostra um bloqueio restante.
- Limpar todos os pools antes de excluir: afeta outras conexões da suíte e esconderia qual teste reteve o arquivo.
- Repetir a exclusão e ignorar o erro final: reduz a evidência do defeito e permite novo acúmulo silencioso.

## Verificação

Uma execução na `development` passou com 527 verificações, mas aumentou a contagem de diretórios legados de 52 para 53. A correção deve falhar antes da mudança, passar depois e não criar nova pasta em duas execuções seguidas no Windows. O CI deve continuar verde.
