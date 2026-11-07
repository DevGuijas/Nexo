# Tutorial de compilação, execução e evolução da IA

Este documento explica como preparar o ambiente, compilar, testar e publicar o Nexo a partir da branch `development`. O aplicativo está na versão 1.6.3 e o instalador na 1.0.0, conforme o `<Version>` de cada projeto. A segunda parte mostra como o modelo de IA é usado pelo projeto e como organizar um experimento de fine-tuning sem confundir treinamento com correções no código do aplicativo.

## 1. Visão geral do projeto

O repositório contém quatro projetos .NET, sem arquivo `.sln`:

| Projeto | Tipo | Finalidade |
|---|---|---|
| `Nexo.Support/Nexo.Support.csproj` | WPF, `net8.0-windows` | Aplicativo gráfico. O executável gerado se chama `Nexo.exe`. |
| `Nexo.Checks/Nexo.Checks.csproj` | Console, `net8.0-windows` | Regressões, avaliações da IA e verificações opcionais do Windows real. |
| `Nexo.Setup/Nexo.Setup.csproj` | WPF, `net8.0-windows` | Instalador gráfico. O executável gerado se chama `NexoSetup.exe`. |
| `Nexo.Setup.Checks/Nexo.Setup.Checks.csproj` | Console, `net8.0-windows` | Regressões do instalador. |

O Nexo é exclusivo para Windows porque usa WPF e ferramentas do próprio sistema operacional. Os dados operacionais usam SQLite, então `Nexo.Support` e `Nexo.Checks` dependem do pacote NuGet `Microsoft.Data.Sqlite` 8.0.33: a primeira restauração precisa de acesso ao NuGet ou de um cache local. Os scripts PowerShell necessários são incorporados ao assembly durante a compilação.

Além do atendimento, o aplicativo contém os módulos de monitoramento e recuperação:

| Arquivos em `Nexo.Support` | Papel |
|---|---|
| `MonitoringEngine.cs`, `WindowsHealthSampler.cs`, `ConnectivityProbe.cs`, `WindowsEventCollector.cs` | Coletam métricas, sondagens de rede opcionais e eventos enquanto a janela está aberta. |
| `HealthRules.cs`, `DiagnosticEngine.cs`, `HealthReportPresentation.cs` | Transformam as leituras em achados e no relatório de saúde. |
| `OperationalStore.cs` | Guarda o histórico em `operations.db`, separado das conversas. |
| `RestorePointProtection.cs` | Exige um ponto de restauração confirmado antes de instalar um driver. |

O modelo não é compilado dentro de `Nexo.exe`. O aplicativo conversa com o Ollama em `http://127.0.0.1:11434` e, por padrão, solicita o modelo `qwen3:8b`.

### O que cada forma de distribuição inclui

| Forma | O que contém | O que ainda falta na máquina |
|---|---|---|
| Publicação dependente de framework (seção 7, opção A) | Só o Nexo | .NET Desktop Runtime 8, Ollama e modelo |
| Publicação independente, chamada aqui de portátil (seção 7, opção B) | Nexo e o runtime .NET | Ollama e modelo |
| Ollama e modelo | Motor local e pesos do `qwen3:8b`, instalados à parte | Sem eles, o aplicativo abre, mas o atendimento guiado pela IA não funciona |
| `NexoSetup.exe`, instalador em nuvem | Assistente que baixa e valida o Nexo, o Ollama e o modelo por manifesto assinado | Nada, além de acesso à publicação; consulte [Instalador e publicação na Oracle](INSTALADOR-NUVEM.md) |

## 2. Pré-requisitos

### Obrigatórios para desenvolver

- Windows 10 ou 11, preferencialmente x64.
- [.NET 8 SDK para Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). Instale o **SDK**, não apenas o Runtime. O SDK contém o compilador e também os runtimes necessários para executar durante o desenvolvimento.
- PowerShell, já incluído no Windows.
- [Git](https://git-scm.com/download/win), para clonar o repositório.
- Acesso ao NuGet na primeira restauração.

Depois da instalação, feche e abra novamente o terminal e confira:

```powershell
dotnet --info
dotnet --list-sdks
```

Deve aparecer algum SDK `8.0.x`. Um SDK mais novo pode conseguir compilar o alvo .NET 8, mas instalar o SDK 8 torna o ambiente mais previsível.

O SDK também pode ser instalado sem administrador, só no perfil do usuário, com o script oficial `dotnet-install.ps1`. Nesse caso, `dotnet build` e `dotnet run` funcionam, mas abrir `Nexo.exe` diretamente falha e pode abrir a página de download do .NET: veja [O executável pede o Runtime](#o-executável-pede-o-runtime).

### Opção com Visual Studio

Também é possível usar o Visual Studio 2022 17.8 ou posterior. No Visual Studio Installer, marque a carga de trabalho **Desenvolvimento para desktop com .NET**. Não existe um arquivo `.sln` no repositório; abra diretamente `Nexo.Support/Nexo.Support.csproj` ou crie uma solução apenas para conveniência local.

### Opcionais para usar a IA

- [Ollama para Windows](https://ollama.com/download/windows).
- Aproximadamente 6 GB livres para baixar a variante quantizada atual do `qwen3:8b`, além de espaço para o aplicativo e seus dados.
- GPU compatível melhora a velocidade, mas não é necessária para compilar o projeto.

Instale e prepare o modelo:

```powershell
ollama pull qwen3:8b
ollama list
```

O Nexo tenta iniciar o Ollama automaticamente. Para testar manualmente, execute:

```powershell
ollama run qwen3:8b
```

Digite `/bye` para sair do chat do Ollama.

## 3. Obter o código e abrir o terminal na pasta correta

```powershell
git clone https://github.com/DevGuijas/Nexo.git
Set-Location .\Nexo
git switch development
```

Todos os comandos deste documento partem da raiz do repositório. Os exemplos de fine-tuning, mais adiante, usam `G:\TCC` como caminho ilustrativo: substitua pelo caminho do seu clone.

## 4. Restaurar e compilar

Restaure os componentes do projeto principal:

```powershell
dotnet restore .\Nexo.Support\Nexo.Support.csproj
```

Compile uma versão de desenvolvimento:

```powershell
dotnet build .\Nexo.Support\Nexo.Support.csproj -c Debug
```

O resultado principal ficará em:

```text
Nexo.Support\bin\Debug\net8.0-windows\Nexo.exe
```

Para uma compilação otimizada:

```powershell
dotnet build .\Nexo.Support\Nexo.Support.csproj -c Release
```

O resultado ficará em:

```text
Nexo.Support\bin\Release\net8.0-windows\Nexo.exe
```

Compilar `Nexo.Checks` também compila os arquivos compartilhados que ele vincula do projeto principal. O instalador é compilado à parte:

```powershell
dotnet build .\Nexo.Checks\Nexo.Checks.csproj -c Release
dotnet build .\Nexo.Setup\Nexo.Setup.csproj -c Release
dotnet build .\Nexo.Setup.Checks\Nexo.Setup.Checks.csproj -c Release
```

Para remover apenas saídas geradas pelo SDK e recompilar. Sem `-c`, `dotnet clean` limpa só a configuração Debug:

```powershell
dotnet clean .\Nexo.Support\Nexo.Support.csproj -c Release
dotnet build .\Nexo.Support\Nexo.Support.csproj -c Release
```

## 5. Executar o aplicativo

Durante o desenvolvimento:

```powershell
dotnet run --project .\Nexo.Support\Nexo.Support.csproj
```

Ou execute diretamente o resultado de Release, se o .NET estiver instalado para toda a máquina ou `DOTNET_ROOT` estiver definido:

```powershell
.\Nexo.Support\bin\Release\net8.0-windows\Nexo.exe
```

O Nexo abre diretamente com a identidade da sessão atual do Windows. Não é necessário criar uma conta nem informar senha no aplicativo. Correções continuam sujeitas à confirmação explícita; operações que precisam de elevação usam o UAC do Windows.

Para testar a IA:

1. Confirme que `ollama list` mostra `qwen3:8b`.
2. Abra o Nexo.
3. Acesse **Configurações** pela navegação lateral.
4. Marque **Usar agente IA para conduzir os atendimentos** e mantenha `qwen3:8b` em **Modelo com suporte a ferramentas**.
5. Use **Verificar modelo local**.

Sem Ollama ou sem o modelo, o aplicativo ainda pode ser compilado e as verificações determinísticas funcionam, mas os atendimentos guiados pela IA e as avaliações que usam o Qwen não funcionarão.

## 6. Executar os testes

### Suíte básica

```powershell
dotnet run --project .\Nexo.Checks\Nexo.Checks.csproj -c Release
dotnet run --project .\Nexo.Setup.Checks\Nexo.Setup.Checks.csproj -c Release
```

As duas usam pastas temporárias para os dados e executam regressões do código. Ao terminar, cada uma mostra a quantidade de verificações aprovadas. O CI do repositório roda em `windows-latest`, em todo PR para `development`, os builds de `Nexo.Support` e `Nexo.Setup`, estas duas suítes e os dois scripts de rede simulados.

### Scripts de rede simulados

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Nexo.Checks\NetworkScriptChecks.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Nexo.Checks\NetworkAdapterChecks.ps1
dotnet run --project .\Nexo.Checks\Nexo.Checks.csproj -- --network-evals
```

Os scripts substituem comandos de rede por simuladores. `--network-evals` exercita o fluxo determinístico de rede com um executor simulado e não depende do Qwen. Eles não devem reconfigurar a rede real.

### Avaliações que usam o Qwen local

Com o Ollama e `qwen3:8b` disponíveis:

```powershell
dotnet run --project .\Nexo.Checks\Nexo.Checks.csproj -- --component-evals
dotnet run --project .\Nexo.Checks\Nexo.Checks.csproj -- --adapter-evals
dotnet run --project .\Nexo.Checks\Nexo.Checks.csproj -- --interaction-evals
```

Essas avaliações usam ferramentas simuladas para verificar seleção de ações, tratamento de recusa, continuidade e conclusão. Elas não comprovam que uma correção funciona em todo hardware real.

### Verificações no Windows real

Os comandos abaixo são opcionais e não alteram o Windows. `--diagnostics` só consulta o sistema. `--interface-probe-live` também tenta conexões TCP 443 a `1.1.1.1` e `8.8.8.8` por cada interface, então envia tráfego externo. O último também usa a IA instalada; ele permite que o modelo proponha uma correção de teste, mas o executor a recusa antes de qualquer alteração:

```powershell
dotnet run --project .\Nexo.Checks\Nexo.Checks.csproj -- --diagnostics
dotnet run --project .\Nexo.Checks\Nexo.Checks.csproj -- --interface-probe-live
dotnet run --project .\Nexo.Checks\Nexo.Checks.csproj -- --diagnostics --agent-live
```

`--hardware-live` é outra verificação opcional: inventaria o hardware gráfico e consulta ofertas online em modo somente leitura. Depende de internet e do estado do computador, e pode falhar com o Windows Update desativado. Definir `NEXO_LIVE_SAMPLE=1` faz `Nexo.Checks` usar o amostrador real e imprimir um resumo da leitura.

`dotnet run --project .\Nexo.Setup.Checks\Nexo.Setup.Checks.csproj -c Release -- --remote` baixa o manifesto e a assinatura da publicação na Oracle e confere se os pacotes existem, sem baixá-los. Depende dessa infraestrutura.

### Prévia da interface

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-UiPreview.ps1
```

O script compila o aplicativo e o abre com `--screenshot`. A prévia usa dados de exemplo em `.preview-data`, sem tocar nos dados reais, e grava as imagens e `ui-checks.txt` em `Nexo.Support\bin\Release\net8.0-windows`. O comando `dotnet` precisa encontrar o SDK e, como o script abre `Nexo.exe` diretamente, também é preciso o runtime global ou `DOTNET_ROOT`.

Não execute `Nexo.Checks/DhcpLiveCheck.ps1` no computador de uso diário. Esse teste altera temporariamente a configuração IPv4, exige administrador e foi criado para uma máquina ou VM de laboratório com recuperação preparada.

## 7. Publicar para distribuição

Há duas formas principais de publicar só o aplicativo, sem o Ollama e sem o modelo. Defina a versão uma vez, igual ao `<Version>` de `Nexo.Support.csproj`:

```powershell
$nexoVersion = "1.6.3"
```

A primeira publicação com `-r win-x64` baixa pacotes de runtime do NuGet: em um teste, o cache `~/.nuget/packages` cresceu cerca de 290 MB.

### Opção A: dependente do .NET Desktop Runtime

Gera uma pasta menor, de cerca de 3 MB. O computador de destino precisa ter o **.NET Desktop Runtime 8 x64**:

```powershell
$nexoPublishDir = ".\artifacts\Nexo-$nexoVersion-local"
dotnet publish .\Nexo.Support\Nexo.Support.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o $nexoPublishDir
```

Execute o resultado:

```powershell
& "$nexoPublishDir\Nexo.exe"
```

### Opção B: independente do Runtime

Inclui o runtime na publicação e gera uma pasta de cerca de 160 MB, mas o usuário não precisa instalar o .NET 8 separadamente:

```powershell
$nexoPublishDir = ".\artifacts\Nexo-$nexoVersion-self-contained"
dotnet publish .\Nexo.Support\Nexo.Support.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o $nexoPublishDir
```

O Ollama e o modelo continuam separados nos dois casos. Uma publicação self-contained inclui o runtime .NET, não inclui os aproximadamente 5–6 GB do Qwen.

### Criar o ZIP

Depois de conferir que o executável abre:

```powershell
$nexoZip = ".\artifacts\Nexo-$nexoVersion-local-Windows.zip"
Compress-Archive -Path "$nexoPublishDir\*" -DestinationPath $nexoZip
```

Use nomes novos para não sobrescrever por acidente um pacote já validado. Distribua o ZIP inteiro; não copie somente `Nexo.exe`.

### Instalador em nuvem

Para distribuir também o Ollama e o modelo, use o instalador descrito em [Instalador e publicação na Oracle](INSTALADOR-NUVEM.md). O executável de instalação publicado está disponível em [NexoSetup.exe](https://objectstorage.sa-saopaulo-1.oraclecloud.com/n/grlqtjwmlv51/b/nexo-releases/o/NexoSetup.exe).

Compilar `NexoSetup.exe` só exige o SDK:

```powershell
dotnet publish .\Nexo.Setup\Nexo.Setup.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ".\artifacts\NexoSetup-$nexoVersion"
```

Gerar uma versão completa depende de infraestrutura externa: `scripts/Build-CloudRelease.ps1` exige a chave privada de publicação, o instalador oficial do Ollama e o modelo local, e o envio usa o bucket da Oracle. Sem esses itens, esse passo não pode ser reproduzido.

## 8. Problemas comuns de compilação

### `dotnet` não é reconhecido

O SDK não está instalado ou o terminal foi aberto antes da instalação. Instale o .NET 8 SDK, abra um PowerShell novo e repita `dotnet --info`.

### A restauração falha ao baixar pacotes

`Microsoft.Data.Sqlite` vem do NuGet. Confira o acesso a `api.nuget.org` ou a origem configurada em `dotnet nuget list source` e rode `dotnet restore` novamente.

### `No .NET SDKs were found`

O comando `dotnet` encontrou uma instalação sem SDK. Isso acontece, por exemplo, quando o SDK está só no perfil do usuário e depois o .NET Desktop Runtime é instalado para toda a máquina: `C:\Program Files\dotnet` entra primeiro no PATH e não contém o SDK. Confira com:

```powershell
where.exe dotnet
dotnet --list-sdks
```

A solução permanente é instalar o .NET 8 SDK completo para a máquina, o que pede UAC. Para o terminal atual, sem instalar nada:

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
```

### `NETSDK1045` ou alvo `net8.0-windows` não suportado

Instale o .NET 8 SDK e confirme `dotnet --list-sdks`. Para Visual Studio, confirme a carga de trabalho de desktop e uma versão compatível com .NET 8.

### O executável pede o Runtime

`Nexo.exe` não encontrou o .NET. O Windows pode mostrar um aviso que abre a página de download, e o processo termina com o código `0x80008083`. Isso acontece em dois casos:

- **Publicação dependente de framework:** instale o .NET Desktop Runtime 8 x64 ou publique novamente com `--self-contained true`.
- **SDK instalado só no usuário:** use `dotnet run`, ou defina `DOTNET_ROOT` apenas para o terminal atual, sem instalar nada:

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
.\Nexo.Support\bin\Release\net8.0-windows\Nexo.exe
```

### O arquivo está em uso durante a compilação

Feche `Nexo.exe` e compile novamente. O Windows pode impedir que o SDK substitua um executável que ainda está aberto.

### O modelo não responde

Confira:

```powershell
ollama list
ollama run qwen3:8b
```

Verifique também se outro processo bloqueia a porta local `11434`. O nome configurado no Nexo deve ser idêntico ao nome mostrado por `ollama list`.

### Uma correção pede UAC

Isso é esperado para operações elevadas. Compilar ou iniciar o aplicativo não exige executar todo o Nexo como administrador; ações específicas pedem elevação quando necessário.

## 9. Como a IA funciona hoje

O fluxo atual é:

```text
Usuário -> Nexo.exe -> prompt + catálogo de ferramentas -> Ollama/Qwen
                    <- chamada estruturada de ferramenta <-
         executor valida permissão, argumentos e alvo no Windows
                    -> resultado real/simulado -> Qwen -> resposta
```

Os pontos principais estão em:

- `Nexo.Support/AgentService.cs`: modelo padrão, prompt de sistema, histórico e chamada à API `/api/chat` do Ollama.
- `Nexo.Support/AgentService.cs`, classe `ToolCatalog`: esquemas das ferramentas entregues ao modelo.
- `Nexo.Support/AgentProtocol.cs`: validações de protocolo e bloqueio de comportamentos indevidos.
- `Nexo.Support/WindowsTools.cs`: execução das ferramentas no Windows.
- `Nexo.Checks/*Evals.cs`: cenários de avaliação.

O modelo escolhe entre ferramentas permitidas, mas não executa PowerShell arbitrário. A autorização, a validação de parâmetros e a confirmação do resultado continuam no código C#.

## 10. O que significa “treinar” neste projeto

Baixar `qwen3:8b` não é treinamento. Compilar o Nexo também não altera os pesos do modelo. Há quatro formas diferentes de melhorar o sistema:

1. **Código e ferramentas:** necessário quando falta observar ou executar alguma ação no Windows.
2. **Prompt e regras do controlador:** útil para instruções, sequência e limites de segurança.
3. **Base de conhecimento/RAG:** útil para procedimentos e documentação que mudam; ainda precisa ser implementada no projeto.
4. **Fine-tuning:** altera o comportamento estatístico do modelo com exemplos revisados. É útil quando permanecem padrões de erro de interpretação, seleção de ferramenta ou formato, mesmo com ferramentas e regras corretas.

Fine-tuning não cria uma ferramenta ausente, não corrige um script defeituoso e não garante que o hardware foi reparado. Para o Nexo, o primeiro alvo deve ser confiabilidade mensurável no uso de ferramentas, não apenas respostas com aparência convincente.

## 11. Estratégia recomendada para fine-tuning

### Etapa 1: criar uma linha de base

Antes de treinar, rode as avaliações existentes e guarde os resultados. Defina métricas como:

- escolheu a ferramenta e o alvo corretos;
- não inventou dispositivo, IP ou sucesso;
- respeitou recusa e modo somente leitura;
- verificou o estado depois da alteração;
- pediu teste funcional quando necessário;
- concluiu sem repetição.

Separe cenários de teste que nunca serão usados no treinamento. Caso contrário, a medição apenas mostrará memorização.

### Etapa 2: preparar dados revisados

Cada exemplo deve conter:

- pedido do usuário;
- esquema das ferramentas disponíveis;
- chamada correta e argumentos;
- resultado observado, inclusive falha ou recusa;
- próxima chamada ou resposta final fiel.

Inclua casos negativos: alvo ausente, múltiplos alvos, ação recusada, ferramenta com erro, correção sem efeito e necessidade de encaminhamento.

Não use diretamente `%LOCALAPPDATA%\NexoSupport\data.json`, logs ou chats brutos. Eles podem conter nomes, e-mails, IPs, GUIDs, nomes de máquinas e também respostas erradas do agente. Remova dados pessoais, substitua identificadores por valores fictícios consistentes e faça revisão humana.

O `ms-swift` aceita JSON/JSONL no formato de mensagens e normaliza chamadas no estilo OpenAI. Um exemplo conceitual é:

```json
{
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "diagnose_device",
        "description": "Consulta o estado de um componente.",
        "parameters": {
          "type": "object",
          "properties": { "topic": { "type": "string" } },
          "required": ["topic"]
        }
      }
    }
  ],
  "messages": [
    { "role": "user", "content": "Minha câmera não aparece." },
    {
      "role": "assistant",
      "content": null,
      "tool_calls": [
        {
          "type": "function",
          "function": {
            "name": "diagnose_device",
            "arguments": "{\"topic\":\"camera\"}"
          }
        }
      ]
    },
    {
      "role": "tool",
      "content": "{\"success\":true,\"devices\":[]}"
    },
    {
      "role": "assistant",
      "content": "Nenhuma câmera foi identificada. Ela é integrada ao computador ou conectada por USB?"
    }
  ]
}
```

No arquivo `.jsonl`, cada exemplo completo ocupa uma única linha. Use os esquemas reais do `ToolCatalog` e resultados representativos do executor, não somente o exemplo resumido acima.

Uma divisão inicial pode ser 80% treino, 10% validação e 10% teste, mas a separação deve ser por incidente ou família de incidente. Variações quase iguais do mesmo caso não podem aparecer em treino e teste.

### Etapa 3: escolher o método

Para uma GPU com 16 GB de VRAM, não é realista começar com fine-tuning completo do Qwen3 8B. Mesmo o exemplo oficial de LoRA para Qwen3 8B cita aproximadamente 22 GB. O primeiro experimento deve usar uma destas opções:

- **QLoRA em 4 bits**, lote 1, sequências menores e gradient checkpointing;
- um modelo Qwen menor para validar todo o pipeline;
- uma GPU em nuvem com 24 GB ou mais para LoRA do modelo de 8 bilhões.

O consumo real depende do comprimento dos exemplos, versão das bibliotecas, atenção, otimizador e quantidade de camadas adaptadas. Comece com poucos exemplos e monitore a VRAM antes de executar um treinamento longo.

### Etapa 4: preparar um ambiente de treinamento

O caminho mais previsível no Windows é usar WSL2 com Ubuntu e o suporte CUDA da GPU. No ambiente Linux:

```bash
python3 -m venv .venv
source .venv/bin/activate
python -m pip install --upgrade pip
```

Instale primeiro o PyTorch indicado para a versão CUDA do ambiente usando o seletor oficial em <https://pytorch.org/get-started/locally/>. Em seguida, instale o restante:

```bash
python -m pip install --upgrade ms-swift transformers bitsandbytes
```

Confirme que a GPU está visível:

```bash
python -c "import torch; print(torch.cuda.is_available()); print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'sem CUDA')"
```

Não prossiga se o primeiro valor for `False`, a menos que a intenção seja treinar em CPU, o que pode ser impraticavelmente lento para esse modelo.

### Etapa 5: executar um experimento QLoRA

O comando abaixo é um ponto de partida, não uma configuração universal:

```bash
CUDA_VISIBLE_DEVICES=0 swift sft \
  --model Qwen/Qwen3-8B \
  --tuner_type lora \
  --dataset /mnt/g/TCC/ia/dados/nexo-treino.jsonl \
  --split_dataset_ratio 0.1 \
  --torch_dtype bfloat16 \
  --quant_method bnb \
  --quant_bits 4 \
  --num_train_epochs 1 \
  --per_device_train_batch_size 1 \
  --per_device_eval_batch_size 1 \
  --gradient_accumulation_steps 16 \
  --learning_rate 1e-4 \
  --lora_rank 8 \
  --lora_alpha 32 \
  --target_modules all-linear \
  --gradient_checkpointing true \
  --max_length 1024 \
  --eval_steps 50 \
  --save_steps 50 \
  --logging_steps 5 \
  --output_dir /mnt/g/TCC/ia/saida/nexo-qwen3-8b
```

Se faltar memória:

- reduza `--max_length`;
- mantenha o lote por dispositivo em 1;
- use um Qwen menor para provar o pipeline;
- não aumente rank, contexto ou lote até obter uma execução estável.

Não escolha épocas e taxa de aprendizado apenas pela queda da perda de treino. Compare a validação e, principalmente, as avaliações de tarefas mantidas fora dos dados.

### Etapa 6: testar o adaptador

Localize o checkpoint criado e rode inferência antes de exportar:

```bash
CUDA_VISIBLE_DEVICES=0 swift infer \
  --adapters /mnt/g/TCC/ia/saida/nexo-qwen3-8b/checkpoint-XXX \
  --temperature 0 \
  --max_new_tokens 1400
```

Teste exemplos aprovados, recusas, falhas de ferramenta e exemplos completamente novos. Uma resposta textual boa não basta: valide também as chamadas estruturadas.

### Etapa 7: mesclar e importar no Ollama

Faça a mesclagem do adaptador com a mesma base usada no treinamento:

```bash
swift export \
  --adapters /mnt/g/TCC/ia/saida/nexo-qwen3-8b/checkpoint-XXX \
  --merge_lora true \
  --output_dir /mnt/g/TCC/ia/saida/nexo-qwen3-8b-merged
```

A documentação atual do Ollama permite importar um diretório de pesos Safetensors. Crie, por exemplo, `G:\TCC\ia\Modelfile`:

```text
FROM G:/TCC/ia/saida/nexo-qwen3-8b-merged
PARAMETER num_ctx 12288
```

No PowerShell do Windows:

```powershell
ollama create --quantize q4_K_M nexo-qwen3-8b -f .\ia\Modelfile
ollama run nexo-qwen3-8b
```

O argumento `--quantize q4_K_M` reduz o modelo durante a criação para torná-lo mais próximo do consumo da variante usada atualmente. Se a importação direta não aceitar a arquitetura, converta o modelo mesclado para GGUF e quantize com `llama.cpp`, depois aponte `FROM` para o arquivo `.gguf`. Não aplique um adaptador a uma variante base diferente: isso produz resultados imprevisíveis.

Por fim, em **Configurações** do Nexo, mude o nome do modelo para `nexo-qwen3-8b` e use **Verificar modelo local**. Não altere `DefaultModel` no código apenas para testar; a configuração já aceita um nome de modelo instalado.

### Etapa 8: aprovar ou rejeitar o modelo

Execute o mesmo benchmark usado na linha de base. Para liberar um modelo, exija pelo menos:

- nenhuma ação sem autorização;
- nenhuma alegação de sucesso sem resultado correspondente;
- melhora mensurável nos cenários mantidos fora do treino;
- ausência de regressão relevante em saudações, recusas e modo somente leitura;
- validação em VM ou máquina de laboratório antes de ações reais.

Se o fine-tuning piorar segurança ou generalização, não o publique. Corrija os dados, reduza a intensidade do ajuste ou mantenha o modelo original.

## 12. Referências

- [Instalação do .NET no Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [Download do .NET 8 SDK e Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- [Qwen3: uso, ferramentas, fine-tuning e licença](https://github.com/QwenLM/Qwen3)
- [Qwen3 com ms-swift](https://github.com/QwenLM/Qwen3/blob/main/docs/source/training/ms_swift.md)
- [Formato de datasets personalizados do ms-swift](https://swift.readthedocs.io/en/latest/Customization/Custom-dataset.html)
- [Chamadas de ferramentas no Ollama](https://docs.ollama.com/capabilities/tool-calling)
- [Importação de modelos no Ollama](https://docs.ollama.com/import)
- [Plano e avaliações já existentes no Nexo](AVALIACAO-E-TREINAMENTO.md)
