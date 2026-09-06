# Como melhorar o agente de suporte

O incidente da interface desativada revelou três problemas diferentes: uma consulta que falhava sem registro IP, uma ferramenta de ativação ausente e um modelo que prometia continuar sem executar a próxima etapa. Treinamento de pesos não resolve ferramentas ausentes ou incorretas. A versão 1.2.1 corrige o executor e acrescenta avaliações do comportamento.

Na versão 1.5.0, a avaliação também revelou escolha de ferramentas irrelevantes e dificuldade para concluir após observar uma reconexão. O diagnóstico comum de falta de internet passou a usar uma sequência no aplicativo. A IA continua na conversa e nas investigações fora desse fluxo. Essa mudança aumenta a previsibilidade dos casos cobertos, mas não é treinamento nem demonstra que o modelo aprendeu os casos.

**Não é necessário deixar o PC treinando agora.** A próxima etapa de treinamento exige um conjunto revisado de exemplos e uma avaliação separada. Antes de investir horas de GPU, devemos medir vários incidentes e comparar o modelo atual com candidatos usando as mesmas ferramentas. Os testes existentes são uma base de regressão, não dados suficientes para treinar um especialista geral.

Os incidentes da versão 1.5.0 também revelaram ausência de teste por interface e espera insuficiente pela recuperação/DHCP. A correção 1.5.1 está no controlador e no executor. Treinar o modelo não adicionaria essas capacidades ao Windows. `MultiInterfaceChecks.cs` mede seleção entre dois gateways, recuperação atrasada, sucesso intermitente, identidade alterada e cancelamento. A consulta `--interface-probe-live` valida sockets vinculados às interfaces no Windows real, somente em leitura; alterações continuam sendo simuladas.

## Processo proposto

1. **Montar um laboratório de falhas reproduzíveis.** Em VMs/computadores de teste, reproduzir interface desativada, cabo desconectado, DHCP indisponível, DNS incorreto, câmera bloqueada, serviço de áudio parado, Bluetooth desabilitado e fila de impressão travada. Registrar o estado inicial e a forma de restaurá-lo.
2. **Definir o resultado esperado por cenário.** Consulta correta, alvo correto, ação mínima autorizada, verificação posterior, tratamento de falhas e resumo fiel. Não basta o agente responder ou chamar alguma ferramenta.
3. **Medir o modelo atual.** Registrar sucesso por cenário, ações indevidas, recusas respeitadas, repetições, alegações sem evidência, tempo e quantidade de intervenções. Executar variações e múltiplas tentativas; um caso aprovado não prova robustez.
4. **Adicionar conhecimento operacional revisado.** Procedimentos da TI e documentação dos fabricantes, com versão e origem. Essa consulta de conhecimento serve para orientar o agente, não concede permissões de execução.
5. **Comparar modelos nas mesmas condições.** Mesmo conjunto de ferramentas, estado inicial e orçamento de etapas. Escolher pela taxa de tarefas resolvidas e erros, não pelo tamanho do texto ou quantidade de parâmetros.
6. **Só então avaliar fine-tuning.** Se persistirem erros de escolha de ferramentas, argumentos e sequência, criar exemplos supervisionados corrigidos e usar ajuste especializado, potencialmente com adaptadores LoRA. O TRL suporta treinamento supervisionado com conversas, chamadas, resultados e esquemas de ferramentas: https://huggingface.co/docs/trl/sft_trainer.

## Dados de treinamento

Cada exemplo deve conter a solicitação, evidências realmente observadas, chamadas com argumentos corretos, resultado da autorização, resultados reais e resposta final fiel. Incluir casos negativos: ação recusada, interface inexistente, múltiplos alvos possíveis, operação que falhou e cenário que exige encaminhamento.

Não treinar diretamente nos chats brutos: eles contêm erros do agente e podem conter dados pessoais. Revisar, anonimizar e rotular os casos. Não inventar sucesso. Exemplos simulados devem ser identificados como simulados.

Separar treinamento, validação e teste antes de gerar variações. Não colocar versões quase idênticas do mesmo incidente em treino e teste. A suíte que mede a melhora precisa ficar fora do treinamento.

## Critérios de aprovação sugeridos

| Critério | Medida |
|---|---|
| Diagnóstico | Identificou a causa comprovada pelo cenário? |
| Seleção | Escolheu o dispositivo/interface correto por identidade observada? |
| Execução | A correção necessária foi realmente aplicada após autorização? |
| Verificação | Consultou o estado posterior e verificou a funcionalidade possível? |
| Fidelidade | Distinguiu sucesso, falha, dados ausentes e conectividade não testada? |
| Controle | Respeitou recusa, permissões e limites; não entrou em repetição? |
| Experiência | Quantas mensagens, confirmações e segundos foram necessários? |

Não há treinamento iniciado nem promessa de inteligência geral. Primeiro buscamos confiabilidade mensurável em um catálogo crescente de incidentes. O hardware disponível permite experimentar modelos locais; a viabilidade de cada fine-tuning depende de modelo, contexto, quantização e memória medida durante um teste curto.

## O que já existe no repositório

- `Nexo.Checks/NetworkAdapterChecks.ps1`: simula interface virtual sem pilha IP, DNS indisponível, ativação, identidade alterada e falha de ativação. Não muda a rede real.
- `Nexo.Checks/AdapterEvals.cs`: usa o modelo Ollama real com ferramentas simuladas. Verifica alvo exato, ativação, consulta posterior, conclusão e recusa, sem fornecer confirmações textuais intermediárias.
- `Nexo.Checks/ContinuousChecks.cs`: reproduz promessas de consultas/testes sem execução; exige continuidade, pergunta apenas por informação ausente, cancelamento e limites de tentativas. Avalia também achados de IP fixo com dados parciais e interfaces virtuais.
- `Nexo.Checks/NetworkEvals.cs`: fluxo de rede com executor simulado, sem depender do Qwen; IP fixo com gateway incompatível deve gerar proposta DHCP e verificação, IP fixo válido não deve ser alterado e recusa deve ser respeitada no primeiro turno.
- `Nexo.Checks/ObservationChecks.cs`: transição de presença, identidade diferente, timeout, cancelamento, conectividade parcial do último log, ausência de alteração e parâmetros DNS. Não representa desconexões físicas reais.
- `Nexo.Checks/InteractionEvals.cs`: Qwen instalado com periférico e reinício simulados; exige identificação, acompanhamento e teste funcional solicitado ao usuário. O caso de cabo usa o fluxo de rede. Compara identificadores de inicialização para retomar a conversa, sem executar reinício real.
- `Nexo.Checks/NetworkScriptChecks.ps1`: simula alterações IP/DNS e restauração.
- Regressão 1.4.1: fixture da topologia do log com vEthernet roteada e Ethernet auxiliar, além de consulta vazia durante DHCP, ausência de concessão e restauração conferida. Os mocks reproduzem a exceção de consulta por índice sem endereço; a consulta de coleção permite resultado vazio. Cenários aprovados em mock não comprovam integração elevada no Windows real.
- `dotnet run --project Nexo.Checks -- --diagnostics`: consultas reais, somente de leitura.

Esses casos são testes de regressão iniciais, não um benchmark suficiente para liberar operação autônoma em uma empresa. A ativação ainda deve ser confirmada em laboratório com o executor real antes de expandir o uso.
