# Prévia atualizada e auditoria da integração — 25/11/2026

## Intenção

Reconciliar os PRs e issues recentes do Nexo com `development`, gerar um instalador novo para o código atual e preparar a integração em `main` sem declarar concluída uma validação que não ocorreu.

## Estado verificado

Em 25/11/2026, a API do GitHub mostra zero PRs abertos; os PRs recentes, inclusive #149, #151, #152 e #153, estão mesclados em `development`. A `main` permanece na 1.6.3. A prévia Oracle aponta para um pacote 1.7.0 gerado antes desses merges. O changelog ainda lista validações pendentes. As issues abertas são #8, #10, #12, #19, #21, #22 e #139.

## Entrega

- Publicar uma nova **prévia** do aplicativo da `development`, em caminho imutável distinto do 1.7.0, com instalador que ainda baixe e confira Nexo, Ollama e `qwen3:8b`, crie atalhos, guarde logs e inclua desinstalador.
- Corrigir o script de empacotamento de prévia para publicar o aplicativo sem extração de DLLs, conforme a mudança #142 já integrada no build estável.
- Remover o número 1.7.0 fixo da interface do instalador e manter versão, documentação e SHA-256 coerentes.
- Executar testes e instalação/checagens apenas neste PC. Nenhuma VM será baixada ou executada.
- Registrar evidências existentes das issues. Fechar somente as que cumpriram seus critérios; manter explícitos os bloqueios restantes.
- Preparar um PR para `main` se a integração puder ser revisada, sem substituir o canal estável enquanto os critérios de release estiverem abertos.

## Limites

O usuário proibiu VM nesta tarefa. A #8 e os ensaios da #12 não podem ser concluídos pelo protocolo atual. Não há evidência de certificado de assinatura de código confiável para #22; assinatura do manifesto é separada. A nova publicação será identificada como prévia sem assinatura Authenticode. A pasta local `E:\TCC\23_11_2026` fica preservada fora do Git.

## Abordagens

1. Substituir a distribuição estável agora: produziria um link único, mas violaria os critérios de validação e assinatura documentados.
2. Publicar uma nova prévia imutável e abrir PR de integração com bloqueios visíveis: permite testar os merges recentes e revisar `main` sem afirmar prontidão estável. **Escolhida** por respeitar o estado atual do projeto e a restrição de não usar VM.
