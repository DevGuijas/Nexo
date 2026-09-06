# Nexo — agente de suporte para Windows

Aplicativo C# / WPF com IA local que escolhe ferramentas do Windows, executa ações, recebe os resultados e continua a investigação. A interface e as contas da primeira versão foram preservadas.

## Abrir

Execute `artifacts/Nexo-1.5.0/Nexo.exe`. A barra da janela deve mostrar **Nexo 1.5.0**. Feche a versão anterior antes de abrir. Requer **.NET Desktop Runtime 8 para Windows x64**. Para distribuir, extraia todos os arquivos de `artifacts/Nexo-1.5.0-Windows.zip` na mesma pasta.

### Novidade 1.5.0 — observação, verificação e retomada

- **Rede com sequência verificável:** pedidos como “estou sem internet”, “sem conexão” e “cabo desconectado” usam um fluxo limitado no aplicativo, sem depender da escolha de etapas pelo modelo nem do Ollama disponível. Consulta os adaptadores, trata interface desativada ou cabo desconectado, testa nome/TCP e IP/TCP, propõe DHCP quando cabível com autorização e verifica novamente. Com várias interfaces candidatas, pede identificação. IP fixo com teste funcionando é preservado. O fluxo usa a rota do Windows; não comprova por qual interface o tráfego passou.
- **Resumo baseado em resultados:** conexão por IP funcionando e acesso por nome falhando são conectividade parcial. O agente não deve atribuir uma recuperação ao Nexo sem operação concluída. Testes idênticos sem novas evidências deixam de repetir indefinidamente; uma mudança ou reconexão permite nova verificação. O erro de DNS automático sem a lista `servers` foi corrigido; valores de DNS manual continuam obrigatórios.
- **Cabo e periférico:** pode orientar uma desconexão/reconexão de um alvo previamente identificado e acompanhar seu estado por até dois minutos, com cancelamento. Para cabo, somente Ethernet física habilitada; vEthernet não tem cabo próprio. Para periféricos USB/Bluetooth reconhecidos, observa presença PnP da mesma identidade. Essa presença não comprova som, imagem, impressão ou conexão Bluetooth funcional. Reconexões rápidas podem escapar da amostragem; trocar a porta USB pode mudar a identidade. O resultado pede teste no aplicativo.
- **Reinício manual:** pode registrar o motivo e a inicialização atual no atendimento. Salve seus arquivos, reinicie manualmente e, ao voltar, abra a conversa em **Atendimentos** e envie uma mensagem. O agente compara a inicialização e retoma a investigação. Não reinicia o computador automaticamente e não considera o reinício prova de resolução.
- **Interação:** instruções de reconexão aparecem no chat durante a espera. Consultas seguem sem “Ok”; perguntas de funcionamento podem encerrar a etapa para receber o teste do usuário. Mudanças reais continuam passando pela autorização específica do executor; pedidos apenas de acompanhamento não autorizam ajustes de configuração.

Validação: regressões automatizadas com executores simulados, incluindo o último caso de TCP funcionando/DNS falhando, preservação de IP válido, recusa, reconexão da mesma identidade, ausência de transição, cancelamento e reinício. Avaliação com Qwen real para USB e retomada; o fluxo comum de rede é testado separadamente, sem atribuir seu resultado ao modelo. As transições físicas e alterações elevadas ainda precisam de teste em máquina/VM de laboratório. Não houve treinamento de pesos nem comprovação de resolução geral N1/N2/N3.

No atendimento relatado em 05/09, 21h43–21h51, o registro consultado indicou TCP com `8.8.8.8:53` funcionando e falha de DNS para `google.com`. Não havia alteração autorizada/concluída registrada naquele intervalo. Portanto, o log comprova conectividade parcial, mas não comprova que o Nexo tenha causado a recuperação percebida.

### Correção 1.4.1 — topologia Hyper-V, DHCP e interface

- O log relatado continha uma vEthernet com gateway padrão e uma Ethernet física auxiliar sem gateway. O diagnóstico agora destaca a interface com rota externa, em vez de propor DHCP na auxiliar só por não ter gateway. A topologia do log está reproduzida em um teste com Qwen local e executor simulado.
- Configuração IPv4/DNS permitida em adaptadores vEthernet do host Hyper-V identificados, com confirmação e conferência do GUID antes de mudar. Default Switch e adaptadores VPN permanecem bloqueados. A operação não recria switches nem modifica VMs.
- Consultas de endereços e rotas toleram interface temporariamente vazia durante DHCP e restauração. Após ativar DHCP, aguarda até 20 segundos por endereço DHCP utilizável; sem concessão, tenta restaurar. A restauração confere modo, endereços estáticos, gateways e DNS anterior. Falha de transação encerra com erro e estado de restauração explícitos, sem repetir a correção.
- **Shift + Enter envia**; **Enter quebra linha**. O atalho usa PreviewKeyDown para preceder o tratamento do TextBox. Os atalhos de resposta continuam disponíveis.
- Etapas com rolagem independente; cancelar, resolver e encaminhar continuam acessíveis. Em janelas baixas, o cartão explicativo é recolhido para dar espaço às etapas. Ícones e textos da navegação usam colunas de largura consistente; navegação também pode rolar.

Validação: 13 cenários PowerShell simulados, incluindo IPv4 temporariamente vazio, DHCP sem concessão, restauração, vEthernet, VPN, Default Switch e troca de identidade; avaliação do Qwen com a topologia do log; prévia WPF com 70 etapas, rolagem e envio multilinha em janela normal e mínima. Não foram realizadas alterações na rede real durante o desenvolvimento. A integração elevada ainda precisa ser confirmada no cenário de teste do usuário/VM.

### Novidade 1.4.0 — diagnóstico contínuo e IP fixo

- O controlador exige conclusão ou pergunta explícita. Texto como “vamos consultar” não encerra mais o turno: o agente recebe instrução de continuar, sem fabricar um “Ok” do usuário. Consultas e testes seguem até conclusão, informação indispensável, cancelamento ou limite de execução.
- Correções continuam passando pela confirmação concreta do executor. Recusar uma ação interrompe a etapa e impede novas ações no mesmo lote.
- Pedidos explícitos como “apenas consulte” e “não mude a rede” restringem o catálogo e a execução a ferramentas de leitura naquele turno; a permissão de correção da conta também é respeitada.
- O inventário inclui origem/estado do endereço, DHCP e uma análise calculada fora da IA: IP fixo, gateway ausente/fora da sub-rede, APIPA e endereço duplicado. IP fixo coerente não é tratado como defeito. Inventário parcial não comprova ausência de gateway.
- Para uma interface física candidata, o agente pode propor **Obter um endereço IP automaticamente**, com confirmação, DNS preservado e restauração existente. DHCP disponível na rede não é presumido. Interfaces ambíguas exigem identificar o alvo.
- Depois de alterar IP/DNS com sucesso, o executor consulta novamente a configuração; o agente recebe esse resultado e pode testar a conexão no mesmo turno. Testes DNS/TCP usam a rota do Windows, sem comprovar conectividade de uma interface específica.

Validação: testes de controle com as frases da falha relatada; Qwen local com rede simulada (IP fixo incompatível, IP fixo válido, DHCP recusado e adaptador desativado) sem mensagens intermediárias de “Ok”; testes PowerShell de inventário e restauração. Não houve treinamento de pesos. Falhas fora dos cenários avaliados ainda podem ocorrer; limites de repetição/cancelamento permanecem ativos.

### Novidade 1.3.0 — respostas rápidas

Perguntas de confirmação reconhecidas no chat recebem os botões **Sim, por favor** e **Não**. Perguntas de resultado, como “Funcionou?”, recebem **Sim, funcionou** e **Ainda não**. Um clique envia o texto e continua o atendimento pelo mesmo fluxo do envio manual. O rascunho digitado é preservado.

Os botões só aparecem na pergunta mais recente de um atendimento em andamento. Ficam indisponíveis durante a execução e desaparecem após a resposta; cliques antigos não enviam mensagens duplicadas. Perguntas abertas, com alternativas ou múltiplas perguntas não recebem sugestões genéricas. A detecção usa padrões de texto, sem chamada adicional ao modelo; formulações não reconhecidas continuam com resposta livre. As autorizações específicas das ferramentas e do Windows permanecem no fluxo normal.

Validação na interface WPF: envio pelos dois botões, rascunho preservado, bloqueio de clique antigo, pergunta aberta sem atalhos e atalhos de confirmação de resultado.

### Correção 1.2.1 — interface desativada

- O inventário descobre adaptadores independentemente da pilha IP. A ausência de MSFT_NetIPInterface ou DNS em uma interface desativada não derruba o diagnóstico de todas as interfaces.
- Falhas de consultas opcionais aparecem como avisos e dados parciais; não são interpretadas como inexistência do adaptador.
- Nova ferramenta `enable_network_adapter`, inclusive para vEthernet/Hyper-V. Seleção por índice observado e GUID estável; conferência de identidade imediatamente antes de ativar.
- A ativação consulta o estado e a configuração novamente. `Disconnected` não é confundido com `Disabled`; interface habilitada não significa internet confirmada.
- O executor acompanha a conversa entre mensagens; chamadas idênticas que falharam ou foram recusadas não se repetem indefinidamente no mesmo turno.
- Resultados de ferramentas têm indicação de conclusão/falha e JSON legível, sem sequências de escape na mensagem.

Validação original: consultas reais somente de leitura, testes simulados do executor e avaliação do Qwen3 instalado com uma interface virtual sem IP, índice diferente e ação recusada. A versão 1.4.0 também exige que esses cenários avancem sem confirmação intermediária no chat. Não há garantia de resolução geral baseada nesses poucos cenários.

As contas existentes continuam válidas. Em instalação nova: **Guisbz / 1234**, administrador. O primeiro acesso solicita troca de senha antes do chat. A atualização ativa `qwen3:8b` uma única vez, preservando perfis, senhas e históricos.

## IA instalada

Foi baixado **Qwen3 8B Q4_K_M**, aproximadamente 5,2 GB, pelo Ollama já instalado. O PC utilizado tem 32 GB de RAM e RTX 5060 Ti com 16 GB de VRAM. O modelo foi testado com chamadas reais de ferramentas. Não foi realizado treinamento ou fine-tuning.

O Nexo tenta iniciar o Ollama automaticamente. O modelo fica no armazenamento de modelos do Ollama e **não está no ZIP**. Em outro PC, instale o [Ollama](https://ollama.com) e execute `ollama pull qwen3:8b`. Em Configurações, “Verificar modelo local” testa disponibilidade e suporte a ferramentas.

Com o agente ativo, os pedidos comuns de falta de conexão passam primeiro pelo fluxo de rede acima. Os demais pedidos usam o modelo para escolher ferramentas com argumentos estruturados; o executor valida permissões e parâmetros. Consultas são executadas diretamente. Alterações exibem uma autorização concreta e podem solicitar UAC. O resultado volta ao controlador e à IA para verificação e conclusão. O painel mostra eventos de execução, sem expor raciocínio interno. Resultados completos aparecem em cartões expansíveis no chat.

## Ferramentas

| Área | Consultas | Ações |
|---|---|---|
| Rede | Adaptadores, estado administrativo, IPv4, prefixo, gateway, DNS, DHCP, resolução e conexão TCP | Habilitar interface desativada (incluindo virtual), definir DNS manual/automático, IPv4 estático/DHCP, limpar cache DNS |
| Câmera | Dispositivos, códigos de problema, preferência de privacidade | Habilitar/reiniciar dispositivo, permitir câmera no perfil atual |
| Áudio | Dispositivos e serviços | Habilitar/reiniciar dispositivo, reiniciar serviço de áudio |
| Bluetooth | Dispositivos e serviço | Habilitar/reiniciar dispositivo, reiniciar serviço Bluetooth |
| Impressoras | Impressoras, drivers, portas e filas | Reiniciar spooler, cancelar trabalhos da impressora selecionada, habilitar/reiniciar dispositivo |
| Sistema | Memória, discos, processos com maior uso de memória, erros recentes | Correções conforme causa e ferramentas disponíveis |
| Drivers | Drivers oferecidos pelo Windows Update | Baixar e instalar um driver específico consultado, sem reinício automático |
| Acompanhamento | Estado de link Ethernet físico, presença de periférico USB/Bluetooth identificado, inicialização do Windows | Orientar reconexão e observar transição; preparar retomada após reinício manual |

Experimente: “Meu Bluetooth não funciona”, “A câmera está desabilitada, pode corrigir”, “A impressora está travada”, “Verifique meu áudio”. Para IPv4/DNS manual, informe os valores da sua rede quando solicitado. O modo básico anterior permanece disponível se o administrador desativar a IA.

## Rede e recuperação

Antes de alterar IP/DNS, o executor salva `%LOCALAPPDATA%\NexoSupport\operations\<operação>\before.json`. Após aplicar, consulta a interface e pede confirmação para manter a configuração. A janela tem 45 segundos; o processo elevado usa prazo independente de 60 segundos para restaurar se o aplicativo encerrar ou não confirmar. Falhas também acionam tentativa de restauração.

A restauração pode falhar por indisponibilidade do dispositivo ou políticas do Windows; o erro é registrado junto ao backup. Consultar a configuração ou receber DHCP não comprova conectividade com todos os serviços. Teste a conexão antes de manter a mudança. A reconfiguração de IP/DNS permite interfaces físicas e vEthernet do host identificadas; VPN e Default Switch permanecem bloqueados. A ativação de uma interface virtual desativada é permitida com confirmação, sem recriar switches.

## Contas e dados

Perfis com foto, nome, e-mail e senha. Administradores gerenciam contas, redefinem senhas, desativam usuários e atribuem permissões. Senhas usam PBKDF2-SHA256, salt aleatório e 210 mil iterações. Há proteção do último administrador ativo e bloqueio temporário após falhas.

Dados em `%LOCALAPPDATA%\NexoSupport\data.json`, por substituição de arquivo temporário. O histórico estruturado contém chamadas e resultados; contexto antigo é removido em turnos completos. A IA recebe dados apenas no serviço local `127.0.0.1:11434`.

As contas pertencem ao aplicativo dentro de um perfil do Windows, não ao domínio. O arquivo depende da proteção desse perfil, sem criptografia integral nem auditoria inviolável. A permissão de correção do Nexo não concede privilégios do Windows: ações elevadas passam pelo UAC. Faça backup com o aplicativo fechado.

## Limites

O agente opera pelas ferramentas implementadas, sem executar PowerShell livre gerado pelo modelo. Não é controle irrestrito de qualquer software, não manipula arbitrariamente interfaces de aplicativos, não faz pareamento Bluetooth com PIN nem altera automaticamente o dispositivo de áudio padrão.

Drivers dependem da disponibilidade e políticas do Windows Update. Não há promessa de resolver qualquer incidente N3. Quando faltar uma ferramenta ou a correção falhar, o agente deve informar a limitação. Relatórios são exportáveis para a TI; ainda não há envio automático a GLPI/Jira ou outro sistema externo.

## Desenvolvimento e testes

```powershell
dotnet build Nexo.Support/Nexo.Support.csproj -c Release
dotnet run --project Nexo.Checks
dotnet run --project Nexo.Checks -- --diagnostics --agent-live
powershell -NoProfile -ExecutionPolicy Bypass -File Nexo.Checks/NetworkScriptChecks.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Nexo.Checks/NetworkAdapterChecks.ps1
dotnet run --project Nexo.Checks -- --adapter-evals
dotnet run --project Nexo.Checks -- --network-evals
dotnet run --project Nexo.Checks -- --interaction-evals
dotnet publish Nexo.Support/Nexo.Support.csproj -c Release -o artifacts/Nexo-1.5.0
```

`--diagnostics` consulta o Windows real. `--agent-live` usa o modelo instalado e verifica chamadas reais e recusa de correções, sem autorizar alterações. A suíte PowerShell substitui comandos de rede por simuladores e testa aplicação, falha, cancelamento e timeout com restauração.

**Operações que alteram hardware, serviços ou drivers ainda precisam de validação em máquina/VM de teste.** Neste PC, os testes de integração foram de leitura; não foram modificadas configurações, filas ou drivers. `Nexo.exe --screenshot` gera prévias WPF com dados isolados.

Referências: [Ollama — ciclo de ferramentas](https://docs.ollama.com/capabilities/tool-calling), [Qwen3 8B](https://ollama.com/library/qwen3:8b), [Microsoft — IPv4](https://learn.microsoft.com/en-us/powershell/module/nettcpip/new-netipaddress), [Microsoft — DNS](https://learn.microsoft.com/en-us/powershell/module/dnsclient/set-dnsclientserveraddress), [Microsoft — PnPUtil](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax).

Próximos passos para melhorar o agente: [avaliação e treinamento](docs/AVALIACAO-E-TREINAMENTO.md).
