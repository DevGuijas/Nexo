# Nexo — agente de suporte para Windows

Aplicativo C# / WPF com IA local que escolhe ferramentas do Windows, executa ações autorizadas, recebe os resultados e continua a investigação. O aplicativo abre direto no atendimento e usa a sessão atual do Windows.

O desenvolvimento acontece na branch `development`. Convenções de commits, testes, issues e arquivos que não devem ser enviados estão em [CONTRIBUTING.md](CONTRIBUTING.md).

## Novidade 1.7.0 — saúde do computador (não publicada)

A página **Saúde do PC** mostra leituras locais, ocorrências persistentes, eventos do Windows e intervenções registradas enquanto o aplicativo está aberto. **Configurações** permite ajustar intervalo, limiares de CPU, disco e crescimento de memória por processo, e retenção. O crescimento sustentado aponta o processo por PID e horário de início, como hipótese a confirmar. O banco operacional SQLite fica separado das conversas. O agente local pode consultar `get_health_report`; sem Ollama, o aplicativo apresenta o relatório determinístico disponível. Um processo elevado sem resultado no prazo fica em estado **desconhecido**, nunca como correção concluída. O INF instalado pode ser consultado após identificar o dispositivo, sem busca online quando o pedido se limita ao INF. Antes de instalar um driver, o Nexo exige um ponto de restauração confirmado; se o Windows não puder criá-lo, a instalação é interrompida. Consulte o [protocolo de avaliação](docs/AVALIACAO-MONITORAMENTO-E-RECUPERACAO.md) para os sete cenários e os limites da validação.

O monitor mostra separadamente a interface local, o gateway, a resolução DNS e um destino HTTP(S). As três sondagens de rede ficam desligadas por padrão e podem ser ativadas separadamente em **Configurações → Monitoramento local**; nome DNS, URL e texto esperado são editáveis. Ativá-las envia consultas aos destinos configurados. Três falhas consecutivas do mesmo teste externo, dentro do intervalo de coleta com tolerância curta, podem gerar um aviso. Mudar a URL ou o texto esperado reinicia essa contagem. O resultado descreve apenas aquele teste: bloqueio de ICMP, portal cativo, proxy, VPN ou indisponibilidade do próprio destino podem alterar as leituras. O Nexo não muda a rede ao coletar esses dados e continua a registrar CPU, disco e eventos sem acesso externo.

## Abrir

Para executar a compilação local, rode `dotnet run --project Nexo.Support -c Release`. Para instalar o aplicativo com o modelo local, baixe [NexoSetup.exe](https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/NexoSetup.exe). Ele consulta a versão publicada na Oracle ao ser executado; veja [o procedimento de instalação e publicação](docs/INSTALADOR-NUVEM.md). A versão publicada atual é a 1.6.3; a 1.7.0 desta branch ainda não foi publicada. Histórico, pré-requisitos de publicação e hashes dos arquivos publicados estão em [CHANGELOG.md](CHANGELOG.md).

### Novidade 1.6.3 — driver NVIDIA e instalador gráfico

Se o Windows não oferece atualização para uma única placa NVIDIA detectada, o Nexo consulta o catálogo oficial do fabricante para o modelo e o Windows identificados. Se houver versão WHQL mais recente, apresenta a licença e pede autorização explícita; baixa o pacote de `us.download.nvidia.com`, valida a assinatura NVIDIA, instala o driver de vídeo e confere a versão observada pelo Windows. Reinicialização ou falta de confirmação deixam a correção pendente, e o usuário ainda precisa testar a função. O instalador agora abre um assistente gráfico com etapas, progresso e opção de instalação por usuário. A opção para todos os usuários fica indisponível enquanto o runtime e o modelo forem instalados por perfil.

### Novidade 1.6.2 — detalhes legíveis e distribuição

Os detalhes expandidos das ferramentas mostram dispositivo, versão, origem e resultado em português, sem despejar JSON no chat. A versão 1.6.2 introduziu a distribuição por manifesto assinado na Oracle e o download do NVIDIA App; o fluxo de instalação direta de driver veio na 1.6.3.

### Novidade 1.6.1 — drivers e acompanhamento

Pedidos explícitos para consultar ou atualizar o driver de um componente identificado seguem um fluxo limitado: inventário PnP, busca online de ofertas com ID exato, autorização nativa quando há uma oferta única e verificação do resultado. Sem oferta, o Nexo informa o dispositivo e a versão observada, registra que nada foi alterado e encerra a busca. O chat mostra as etapas como linhas expansíveis e destaca o resultado com a duração da verificação; não expõe raciocínio interno do modelo.

### Novidade 1.6.0 — conversa e investigação de componentes

- Saudações, agradecimentos e dúvidas podem receber resposta natural do modelo, sem exigir chamada de ferramenta nem gerar o relatório de falha de investigação. Perguntas como “Como posso ajudar?” são aceitas; prometer uma consulta ainda exige executá-la. Uma saudação acompanhada de sintoma inicia a investigação.
- Sintomas reconhecidos de câmera, áudio, Bluetooth e impressão começam por uma consulta pertinente do Windows. Essa triagem seleciona apenas a primeira observação: o modelo recebe os dispositivos/serviços reais e escolhe a próxima etapa dentro do catálogo permitido. Dispositivo ausente leva a esclarecimento sobre periférico integrado/externo, sem inventar um alvo para habilitar.
- Antes de reiniciar um serviço de áudio, Bluetooth, impressão ou Wi-Fi, o Nexo lê os eventos desse serviço nas últimas 24 h (parada inesperada, término com erro e mudança de estado), só leitura e sem elevação. Se o modelo não fez essa consulta, o controlador faz. O pedido de aprovação e a resposta dizem o último evento de falha, ou que não há nenhum (ou que a leitura foi parcial, quando ela para no limite de eventos); se a leitura falhar, dizem que os eventos não puderam ser consultados, e o reinício continua disponível.
- Após habilitar/reiniciar um dispositivo, ajustar a permissão da câmera, reiniciar serviços de áudio/Bluetooth/impressão ou limpar uma fila, o executor confere o estado. O controlador pede um teste funcional ao usuário antes de novas correções. “Serviço em execução” não comprova som, nem “dispositivo habilitado” comprova imagem.
- Pedidos de acompanhar reconexão mantêm o foco em identificar o alvo, observar sua desconexão/retorno e verificar a função. Não expandem a investigação para busca de drivers nessa etapa. O histórico é mantido para receber o resultado do teste e continuar a conversa.
- O fluxo de rede da 1.5.2 foi preservado. As confirmações específicas e permissões existentes continuam no executor.

Validação: `dotnet run --project Nexo.Checks -- --component-evals` usa o Qwen local com Windows simulado para saudações, agradecimento após reparo, câmera desabilitada, serviço de áudio parado, Bluetooth desabilitado, fila de impressão, câmera ausente e recusa. As avaliações de reconexão/retomada e regressões de rede ficam disponíveis pelas opções `--interaction-evals --adapter-evals --network-evals`. Isso não comprova reparos físicos nem cobertura de qualquer falha desses componentes. Não houve treinamento de pesos. O plano de expansão e seus limites estão em [docs/AVALIACAO-E-TREINAMENTO.md](docs/AVALIACAO-E-TREINAMENTO.md).

### Correção 1.5.2 — transição de IP fixo para automático

O registro real da falha de 1.5.1 trouxe `RenewDHCPLease.ReturnValue=84`: IP não habilitado no adaptador. O executor removia o último endereço manual, ativava DHCP e tentava renovar por WMI; o erro do método era registrado, mas o resumo o tratava como simples falta de concessão.

- A transição agora usa `netsh interface ipv4 set address name=<índice observado> source=dhcp store=persistent`, após conferir o GUID. Essa operação do Windows habilita DHCP e remove endereços estáticos e gateways anteriores; o executor não esvazia o endereço antes de chamá-la. DNS continua preservado.
- O código de saída é conferido imediatamente, seguido de leitura do modo DHCP e espera pela concessão utilizável. Comando aceito não equivale a endereço recebido nem a internet confirmada.
- Falhas guardam `dhcpTransition` (método, código e saída) e `failureState` (estado antes da restauração). O resumo não atribui automaticamente a falha ao cabo/roteador. A confirmação e a restauração continuam obrigatórias.
- A suíte simulada inclui o cenário em que remover o último IP antes da transição é rejeitado, comando nativo recusado, comando aceito sem mudança de modo, concessão ausente e restauração. `Nexo.Checks/DhcpLiveCheck.ps1` é um teste **opcional e explicitamente autorizado** no PC real; exige administrador, usa o endereço DHCP atual como estático temporário, depois retorna a DHCP com o executor de produção. Guarda backup e inicia recuperação independente para o mesmo GUID. Não é executado pela suíte comum.

Referências: [Microsoft — código 84](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/enabledhcp-method-in-class-win32-networkadapterconfiguration) e [Microsoft — netsh interface](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-interface).

### Correção 1.5.1 — múltiplas interfaces e recuperação demorada

- O diagnóstico de falta de internet compara as interfaces automaticamente, sem perguntar nomes/índices ao usuário. O protocolo de conversa também rejeita essa pergunta técnica do modelo. Perguntas sobre sintomas observáveis continuam disponíveis.
- Nova consulta `probe_network_interfaces`: inventaria todas as interfaces e testa as saídas IPv4 utilizáveis com endereço de origem e índice de saída vinculados ao socket (`IP_UNICAST_IF`). Testa dois destinos TCP; não confunde resposta de uma interface com a de outra. Interfaces sem gateway/endereço ou sem link recebem indicação de teste externo indisponível. DNS global continua separado. Isso não testa todas as funções de VPN, IPv6, proxy ou redes internas.
- A seleção considera resultados individuais, identidade e métricas das rotas. Uma interface que respondeu não é candidata a DHCP; interfaces gerenciadas continuam protegidas. Havendo candidatas com falha e IP fixo, apresenta uma proposta por vez, com autorização e restauração. Falhar em destinos externos não prova que uma rede isolada esteja mal configurada.
- Após ativação, reconexão ou alteração IPv4, atualiza o inventário e acompanha a recuperação em uma janela limitada, com intervalos de cinco segundos. Exige duas verificações DNS/TCP positivas consecutivas para concluir recuperação estável. Um êxito isolado ou o estado “habilitada” não bastam. Cancelamento continua disponível.
- DHCP agora solicita renovação explicitamente para o GUID/índice correto e aguarda até 60 segundos por concessão utilizável. Registra o retorno da renovação; a confirmação depende do endereço realmente observado. Sem concessão, restaura e explica a falha. Isso não cria um servidor DHCP nem garante obter endereço em toda rede.

O registro dos prints mostrou ativação terminando em `Disconnected`, seguida de investigação cedo demais; na tentativa IPv4, houve mudança e reversão após expirar a espera anterior de 20 segundos. A versão nova trata esses dois caminhos. Foram adicionadas regressões para dois gateways, ativação demorada, sucesso intermitente, identidade alterada e cancelamento; a sondagem por interface também foi executada em leitura no PC real. Aplicação DHCP e falhas de recuperação continuam validadas em simulação, sem reconfigurar a rede real neste desenvolvimento.

Referências técnicas: [Microsoft — IP_UNICAST_IF](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options) e [Microsoft — RenewDHCPLease](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/renewdhcplease-method-in-class-win32-networkadapterconfiguration).

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

O Nexo abre diretamente para o usuário atual do Windows. Não há tela de login nem senha inicial do aplicativo. Atendimentos antigos salvos neste perfil são associados à sessão local para continuar acessíveis. Alterações no Windows ainda pedem autorização específica e ações elevadas usam o UAC.

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
| Hardware e drivers | Dispositivos PnP presentes, códigos de erro, fabricante, IDs e versão/provedor do driver; ofertas online da fonte de atualizações configurada no Windows | Baixar e instalar somente uma oferta com ID de hardware exato para um único dispositivo observado, mediante confirmação e UAC, sem reinício automático |
| Acompanhamento | Estado de link Ethernet físico, presença de periférico USB/Bluetooth identificado, inicialização do Windows | Orientar reconexão e observar transição; preparar retomada após reinício manual |

Experimente: “Meu Bluetooth não funciona”, “A câmera está desabilitada, pode corrigir”, “A impressora está travada”, “Verifique meu áudio”. Para IPv4/DNS manual, informe os valores da sua rede quando solicitado. O modo básico permanece disponível quando a IA está desativada nas configurações.

## Rede e recuperação

Antes de alterar IP/DNS, o processo elevado salva o backup (`before.json`) e o registro da transação em `%ProgramData%\NexoSupport\operations\<operação>`. Cada pasta dessa cadeia nasce já com o descritor final (herança desligada, dono Administradores, controle total só para SYSTEM e Administradores, leitura para Usuários). Uma pasta que já existe nunca é reparada nem adotada: só é usada se já estiver assim, sem link. Se não estiver, nada é alterado na rede. Os arquivos da pasta protegida também nascem com o descritor explícito (dono Administradores, sem herança), num temporário que substitui o destino; o dono não depende do padrão do processo, que num administrador com o UAC desligado é o próprio usuário. Sem conseguir esse dono, a gravação falha e nada é alterado.

Uma pasta `NexoSupport` criada antes por outro programa, com outro dono ou outras permissões, só impede as mudanças de IP/DNS pelo Nexo; ela nunca é usada para restaurar. Para removê-la, abra um prompt como administrador e rode `takeown /F "%ProgramData%\NexoSupport" /A /R /D S` (em Windows em inglês, `/D Y`), `icacls "%ProgramData%\NexoSupport" /reset /T` e `rmdir /S /Q "%ProgramData%\NexoSupport"`. Se ela for um link (junção), use só `rmdir "%ProgramData%\NexoSupport"`, sem `/S`, para não apagar o destino.

O executor vai compactado para o processo elevado, porque o script inteiro passa do limite de 32 767 caracteres da linha de comando do Windows (sem compactar, cerca de 55 mil). Compactada, a linha de comando da aplicação tem cerca de 23 100 caracteres e a da restauração cerca de 23 050, uma margem de uns 9 600 (29%); a suíte falha se ela passar do limite. As demais operações elevadas vão sem compactar e são recusadas antes do UAC, com mensagem clara, se passarem do limite; a maior delas, a instalação do driver NVIDIA, tem cerca de 14 100. Após aplicar, consulta a interface e pede confirmação para manter a configuração. A janela tem 45 segundos; o processo elevado usa prazo independente de 60 segundos para restaurar se o aplicativo encerrar ou não confirmar. O texto da confirmação cita os 45 segundos porque é a janela que decide: ao fim deles, o aplicativo pede a restauração. Os 60 segundos só valem quando o aplicativo não responde. Falhas também acionam tentativa de restauração.

Se o executor terminar sem resultado, a abertura seguinte do Nexo oferece restaurar a configuração anterior. O que é mostrado e restaurado vem só da cópia protegida, conferida pelo aplicativo e de novo pelo processo elevado (dono, permissões e links); os arquivos de `%LOCALAPPDATA%` podem ser alterados por qualquer programa do usuário e nunca definem o que é aplicado. Sem cópia protegida confiável, ou com a transação iniciada há mais de 7 dias, a restauração não é oferecida e o Nexo pede para conferir a rede. Um UAC recusado não deixa transação pendente. O resultado da restauração elevada é gravado só na pasta protegida e copiado pelo aplicativo, sem elevação, para `%LOCALAPPDATA%`. A recusa da oferta fica registrada em `%LOCALAPPDATA%`; se esse registro for apagado, a oferta pode reaparecer enquanto a cópia protegida tiver menos de 7 dias.

O processo elevado não grava nada em `%LOCALAPPDATA%`: o estado aplicado (`applied.json`) e o resultado (`result.json`) também ficam na pasta protegida. O aplicativo só os lê depois de conferir o dono e as permissões do próprio arquivo e copia o resultado, sem elevação, para `%LOCALAPPDATA%`; um resultado em pasta não confiável é ignorado e o estado fica desconhecido. A pasta protegida de cada transação é apagada por uma transação seguinte depois de 7 dias. A decisão de manter ou restaurar (`decision.txt`) continua em `%LOCALAPPDATA%` e é lida pelo processo elevado; autenticá-la é a #11. A mudança foi validada em simulação e falta conferir no Windows com UAC real (#95).

A espera do aplicativo pelo processo elevado tem prazo total de 5 minutos: até 150 segundos para aplicar e chegar à confirmação (incluindo a espera por concessão DHCP), até 60 segundos pela resposta da janela e até 90 segundos para o executor registrar o resultado depois da decisão. O cancelamento grava a restauração para o executor e continua contando o prazo; se for antes da aplicação, o prazo inclui também a fase de conclusão, e com a janela de confirmação aberta ele encerra a espera por ela. Se o prazo acabar com o processo elevado ainda ativo, o Nexo não o encerra nem inicia uma restauração concorrente: grava `unconfirmed.json`, informa que o estado da rede não pôde ser confirmado e não anuncia restauração. Quando o executor terminar sem resultado, a restauração é oferecida na próxima abertura do Nexo.

A restauração pode falhar por indisponibilidade do dispositivo ou políticas do Windows; o erro é registrado junto ao backup. Consultar a configuração ou receber DHCP não comprova conectividade com todos os serviços. Teste a conexão antes de manter a mudança. A reconfiguração de IP/DNS permite interfaces físicas e vEthernet do host identificadas; VPN e Default Switch permanecem bloqueados. A ativação de uma interface virtual desativada é permitida com confirmação, sem recriar switches.

## Acesso local e dados

Dados em `%LOCALAPPDATA%\NexoSupport\data.json`, por substituição de arquivo temporário. O histórico estruturado contém chamadas e resultados; contexto antigo é removido em turnos completos. A IA recebe dados apenas no serviço local `127.0.0.1:11434`.

O acesso do aplicativo acompanha a sessão Windows que abriu o Nexo; o arquivo depende da proteção desse perfil, sem criptografia integral nem auditoria inviolável. A sessão direta pode diagnosticar e propor correções, mas não recebe administração de contas. Correções continuam sujeitas à confirmação e não concedem privilégios do Windows; ações elevadas passam pelo UAC. Faça backup com o aplicativo fechado.

## Limites

O agente opera pelas ferramentas implementadas, sem executar PowerShell livre gerado pelo modelo. Não é controle irrestrito de qualquer software, não manipula arbitrariamente interfaces de aplicativos, não faz pareamento Bluetooth com PIN nem altera automaticamente o dispositivo de áudio padrão.

Drivers dependem da disponibilidade e das políticas da fonte de atualizações configurada no Windows (Windows Update ou servidor gerenciado). Quando a fonte do Windows não oferece atualização para uma única placa NVIDIA, o Nexo pode baixar do catálogo oficial o driver WHQL do modelo observado e instalá-lo, somente com autorização explícita e assinatura válida; também pode baixar o NVIDIA App oficial, sem instalá-lo. O Nexo não aceita URLs indicadas pelo modelo. Após uma instalação via Windows Update, compara a versão observada e o estado PnP; reinicialização pendente ou resultado não confirmado impedem declarar a correção concluída. Não há promessa de resolver qualquer incidente N3. Quando faltar uma ferramenta ou a correção falhar, o agente deve informar a limitação. Relatórios são exportáveis para a TI; ainda não há envio automático a GLPI/Jira ou outro sistema externo.

## Desenvolvimento e testes

Tutorial completo: [compilação, execução, publicação e evolução da IA](docs/COMPILACAO-E-IA.md).

Perguntas detalhadas sobre tecnologias, abertura do código, compilação, execução, segurança e limites: [perguntas e respostas sobre o Nexo](docs/PERGUNTAS-E-RESPOSTAS.md).

Visão do código, fronteiras de autorização e limitações: [arquitetura e limites](docs/ARQUITETURA-E-LIMITES.md). Resultados de validação: [revisão do núcleo em 16/10/2026](docs/RELATORIO-TECNICO-VALIDACAO-2026-10-16.md), [interface/hardware em 21/10/2026](docs/VALIDACAO-HARDWARE-2026-10-21.md) e [ensaio real de instalação NVIDIA em 07/11/2026](docs/ENSAIO-NVIDIA-2026-11-07.md).

```powershell
dotnet build Nexo.Support/Nexo.Support.csproj -c Release
dotnet run --project Nexo.Checks
dotnet run --project Nexo.Checks -- --diagnostics --agent-live
powershell -NoProfile -ExecutionPolicy Bypass -File Nexo.Checks/NetworkScriptChecks.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Nexo.Checks/NetworkAdapterChecks.ps1
dotnet run --project Nexo.Checks -- --adapter-evals
dotnet run --project Nexo.Checks -- --network-evals
dotnet run --project Nexo.Checks -- --interaction-evals
dotnet run --project Nexo.Checks -- --interface-probe-live
dotnet run --project Nexo.Checks -- --hardware-live
dotnet publish Nexo.Support/Nexo.Support.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/Nexo-1.7.0
dotnet run --project Nexo.Setup.Checks -c Release
```

`--diagnostics` consulta o Windows real. `--agent-live` usa o modelo instalado e verifica chamadas reais e recusa de correções, sem autorizar alterações. A suíte PowerShell substitui comandos de rede por simuladores e testa aplicação, falha, cancelamento e timeout com restauração.

`--hardware-live` inventaria hardware gráfico presente e consulta ofertas online compatíveis em modo somente leitura. **Operações que alteram hardware, serviços ou drivers ainda precisam de validação em máquina/VM de teste.** Neste PC, os testes de integração foram de leitura; não foram modificadas configurações, filas ou drivers. `Nexo.exe --screenshot` gera prévias WPF com dados isolados.

Referências: [Ollama — ciclo de ferramentas](https://docs.ollama.com/capabilities/tool-calling), [Qwen3 8B](https://ollama.com/library/qwen3:8b), [Microsoft — IPv4](https://learn.microsoft.com/en-us/powershell/module/nettcpip/new-netipaddress), [Microsoft — DNS](https://learn.microsoft.com/en-us/powershell/module/dnsclient/set-dnsclientserveraddress), [Microsoft — PnPUtil](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax).

Próximos passos para melhorar o agente: [avaliação e treinamento](docs/AVALIACAO-E-TREINAMENTO.md).
