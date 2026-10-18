# Perguntas e respostas sobre o Nexo

Este FAQ serve como guia para abrir, compilar, executar e apresentar o Nexo. As respostas descrevem a implementação registrada em 16/10/2026; resultados simulados e limites de laboratório são identificados como tais.

## Projeto e tecnologias

### 1. O que é o Nexo?

O Nexo é um aplicativo de suporte técnico para Windows. Ele conversa com o usuário, coleta informações do sistema, escolhe uma ferramenta permitida e, quando uma correção muda o Windows, pede autorização, executa a ação e observa o resultado. Quando não há evidência suficiente ou uma ferramenta adequada, deve interromper a tentativa e orientar o encaminhamento para a equipe de TI.

### 2. Qual problema o projeto pretende resolver?

O projeto busca tornar o diagnóstico de problemas comuns de Windows mais guiado e verificável. Em vez de apenas sugerir passos, o agente pode consultar dispositivos e serviços, propor algumas correções implementadas e conferir o estado depois. Ele foi pensado como protótipo local de suporte; não substitui uma equipe de TI nem promete resolver todo incidente.

### 3. Quais tecnologias foram utilizadas?

- **C# e .NET 8:** linguagem e plataforma do aplicativo e das verificações.
- **WPF:** interface gráfica para Windows.
- **Ollama e Qwen3 8B:** modelo de linguagem executado localmente, usado para interpretar a conversa e selecionar ferramentas do catálogo.
- **PowerShell e APIs do Windows:** consultas e operações específicas do sistema, encapsuladas pelo executor do aplicativo.
- **JSON local:** persistência de contas, conversas e registros técnicos.
- **PBKDF2-SHA256:** derivação de senha com salt aleatório para as contas locais do aplicativo.

O aplicativo principal não depende de pacotes NuGet externos. O modelo de IA é instalado separadamente e não é compilado dentro de `Nexo.exe`.

### 4. Como o sistema está dividido?

O repositório contém dois projetos .NET. `Nexo.Support` é o aplicativo WPF. `Nexo.Checks` é um aplicativo de console que executa regressões determinísticas, cenários simulados e algumas verificações opcionais de leitura no Windows. O projeto de verificações vincula arquivos compartilhados do aplicativo para testar o mesmo protocolo e executor.

### 5. O que acontece entre a mensagem e uma ação?

O fluxo geral é: usuário descreve o problema → controlador conversa com o Ollama local → modelo propõe uma chamada estruturada → protocolo valida a chamada → executor verifica nome, argumentos, alvo e permissões → interface pede confirmação quando necessário → executor consulta ou altera o Windows → resultado observado volta ao controlador → resposta é apresentada ao usuário. A chamada do modelo, por si só, não altera o Windows.

## Abrir, compilar e executar

### 6. Como abrir o código-fonte?

Abra a pasta raiz do repositório, `D:\Projetos e código\TCC`, no Visual Studio Code por **Arquivo → Abrir Pasta** ou no terminal com `code .`, se o comando estiver disponível. No Visual Studio 2022, abra diretamente `Nexo.Support\Nexo.Support.csproj` para trabalhar no aplicativo ou `Nexo.Checks\Nexo.Checks.csproj` para as verificações.

O repositório não contém um arquivo `.sln`. Por isso, no Visual Studio é preciso abrir o `.csproj` ou criar uma solução local por conveniência. Os arquivos principais da interface estão em `Nexo.Support\MainWindow.xaml` e `Nexo.Support\MainWindow.xaml.cs`.

### 7. O que é necessário para compilar?

Use Windows 10 ou 11 e instale o **.NET 8 SDK x64**. O SDK inclui compilador e ferramentas de desenvolvimento; somente o Runtime não basta para compilar. PowerShell já vem com o Windows. Ollama e Qwen são opcionais para compilar e necessários para usar os atendimentos guiados pela IA.

Confira a instalação em um PowerShell novo:

```powershell
dotnet --info
dotnet --list-sdks
```

Deve aparecer uma versão `8.0.x` do SDK.

### 8. Como compilar o aplicativo?

No PowerShell, vá até a raiz do repositório e execute:

```powershell
Set-Location "D:\Projetos e código\TCC"
dotnet restore .\Nexo.Support\Nexo.Support.csproj
dotnet build .\Nexo.Support\Nexo.Support.csproj -c Release
```

O executável de desenvolvimento será gerado em `Nexo.Support\bin\Release\net8.0-windows\Nexo.exe`. Para compilar em modo Debug, troque `Release` por `Debug`.

### 9. Como executar durante o desenvolvimento?

Na raiz do repositório:

```powershell
dotnet run --project .\Nexo.Support\Nexo.Support.csproj
```

Também é possível abrir diretamente o executável gerado:

```powershell
.\Nexo.Support\bin\Release\net8.0-windows\Nexo.exe
```

### 10. Como executar uma versão já empacotada?

Extraia todos os arquivos de `artifacts\Nexo-1.6.0-Windows.zip` para a mesma pasta e abra `Nexo.exe`. Essa publicação depende do **.NET Desktop Runtime 8 para Windows x64**. Não copie apenas o executável, pois os demais arquivos publicados podem ser necessários.

### 11. Como preparar a IA local?

Instale o Ollama para Windows e, no PowerShell, baixe o modelo:

```powershell
ollama pull qwen3:8b
ollama list
```

Depois abra o Nexo, entre com uma conta administradora, habilite o agente de IA em **Configurações**, mantenha `qwen3:8b` como modelo e use **Verificar modelo local**. O Nexo tenta iniciar o serviço Ollama automaticamente. O modelo ocupa aproximadamente 5–6 GB e não acompanha o ZIP do aplicativo.

### 12. Qual é a conta inicial?

O tutorial do projeto registra a conta inicial `Guisbz`, senha `1234`. No primeiro acesso, o aplicativo exige que a senha seja trocada antes de liberar as funções; o perfil permanece acessível para concluir essa etapa. Troque a senha antes de usar uma cópia compartilhada do sistema.

### 13. Como executar as verificações automatizadas?

Na raiz do repositório, a suíte básica pode ser iniciada com:

```powershell
dotnet run --project .\Nexo.Checks\Nexo.Checks.csproj -c Release
```

Para avaliações adicionais, consulte `docs\COMPILACAO-E-IA.md`. Por exemplo, `--component-evals`, `--adapter-evals`, `--interaction-evals` e `--network-evals` executam grupos distintos. Algumas avaliações usam o Qwen local; os cenários que simulam o Windows não comprovam reparo em hardware real.

### 14. Como publicar para outro computador?

O tutorial de compilação apresenta dois modos: dependente do Desktop Runtime 8, com pasta menor, e self-contained, que inclui o runtime .NET e ocupa mais espaço. Em ambos, Ollama e Qwen continuam separados. Depois de publicar, compacte a pasta completa, teste o executável e distribua o ZIP inteiro. Os comandos completos estão em `docs\COMPILACAO-E-IA.md`, na seção “Publicar para distribuição”.

## Segurança, dados e comportamento

### 15. O Qwen pode executar qualquer comando no computador?

Não. O modelo recebe um catálogo limitado de ferramentas. O executor seleciona implementações pelo nome da ferramenta e valida os argumentos antes de agir; não há ferramenta de shell genérico para o modelo. Scripts e comandos específicos fazem parte do código do aplicativo e recebem parâmetros validados.

### 16. Como o Nexo impede uma alteração sem autorização?

O executor confere permissões da conta e pede confirmação específica no aplicativo antes de alterações protegidas. Operações que exigem elevação também passam pelo UAC do Windows. Se o usuário recusar, a etapa é encerrada sem executar a alteração. A permissão de reparo do Nexo não concede, por si só, privilégios administrativos do Windows.

### 17. Como o sistema evita dizer que corrigiu algo quando não corrigiu?

O controlador exige resultados das ferramentas para sustentar afirmações sobre consultas e ações. O executor retorna sucesso ou falha observados, e o agente deve distinguir ação proposta, recusada, concluída e função confirmada. Em câmera, áudio, Bluetooth e impressão, um serviço ativo ou dispositivo habilitado não prova que a função esteja boa; após a correção, o agente pede ao usuário um teste funcional.

### 18. O que acontece se o usuário cancelar ou recusar?

Uma recusa impede aquela alteração e é devolvida ao controlador como resultado explícito; ela não deve ser descrita como executada. Cancelamentos interrompem etapas canceláveis e observações em andamento. Se uma operação elevada do Windows já começou, o aplicativo pode precisar aguardar seu resultado para relatar o estado real; isso não significa que cancelamento tenha revertido uma mudança já aplicada.

### 19. Onde os dados ficam armazenados?

Os dados locais do aplicativo ficam em `%LOCALAPPDATA%\NexoSupport\data.json`. Isso inclui contas e histórico de atendimentos com chamadas e resultados técnicos. As senhas são armazenadas como derivação PBKDF2-SHA256 com salt aleatório, mas o arquivo inteiro não tem criptografia integral. Trate o arquivo, backups e relatórios exportados como potencialmente sensíveis.

### 20. A conversa é enviada para a nuvem?

Na configuração documentada, o aplicativo envia a conversa ao serviço local do Ollama em `127.0.0.1:11434`; o modelo Qwen está no computador. Isso descreve o fluxo implementado, mas não deve ser entendido como uma auditoria de tráfego de todos os programas ou serviços instalados na máquina.

### 21. O Nexo fica monitorando o computador em segundo plano?

Não. O usuário inicia o aplicativo e conduz o atendimento pelo chat. O Nexo não é um serviço permanente e não monitora ou repara o computador em segundo plano.

## Capacidade e avaliação

### 22. Quais áreas o protótipo atende?

Há fluxos e ferramentas para rede, câmera, áudio, Bluetooth, impressoras, informações básicas do sistema e consulta de drivers oferecidos pelo Windows Update. A ação disponível varia por diagnóstico, alvo observado, permissões e confirmação. Consulte `docs\ARQUITETURA-E-LIMITES.md` para a lista e os limites de cada área.

### 23. O Nexo instala qualquer driver?

Não. A implementação procura drivers oferecidos pelo Windows Update e restringe a instalação ao item identificado e escolhido. A disponibilidade depende das fontes e políticas do Windows Update. A instalação pode pedir reinicialização, mas o Nexo não reinicia o computador automaticamente.

### 24. O agente consegue reparar qualquer periférico?

Não. Ele pode consultar estados e executar algumas correções implementadas, mas a presença de um dispositivo não prova que áudio, imagem, pareamento ou impressão funcionem. O sistema não faz reparo físico nem pareamento Bluetooth com PIN; configuração de impressoras de rede e políticas corporativas também têm cobertura limitada. O agente deve pedir esclarecimentos para alvos ambíguos ou ausentes e encaminhar quando não houver caminho seguro.

### 25. Como interpretar os resultados dos testes?

Os testes determinísticos demonstram que o código responde como esperado para entradas e resultados simulados. Avaliações com Qwen exercitam o modelo real, mas frequentemente usam um executor que simula o Windows. Diagnósticos de leitura no computador real comprovam apenas as informações consultadas naquela máquina e momento. Nenhum desses resultados, sozinho, comprova reparo físico geral.

O relatório de validação de 16/10/2026 registra uma linha de base com build sem erros ou avisos, 84 verificações básicas, 16 cenários de transação de rede simulados, 7 cenários de adaptadores e 95 verificações de diagnóstico somente de leitura. O relatório também registra validações posteriores e limitações por tarefa. Consulte [o relatório técnico](RELATORIO-TECNICO-VALIDACAO-2026-10-16.md) antes de citar números mais recentes; aprovações simuladas não são taxa de sucesso em chamados reais.

### 26. O que falta validar em laboratório?

É necessário executar, em uma máquina ou VM preparada para recuperação, operações elevadas e alterações reais autorizadas; testar UAC e permissões corporativas; e validar câmera, áudio, Bluetooth, impressão e drivers em dispositivos físicos. O relatório de validação detalha o que foi simulado, o que foi consultado em leitura e o que permanece pendente.

## Referências do repositório

- [Tutorial de compilação, execução, publicação e IA](COMPILACAO-E-IA.md)
- [Arquitetura, segurança e limitações](ARQUITETURA-E-LIMITES.md)
- [Relatório técnico de validação](RELATORIO-TECNICO-VALIDACAO-2026-10-16.md)
- [README do projeto](../README.md)
