# Validação de interface, hardware e drivers — 21/10/2026

## Escopo

O início do aplicativo abre o atendimento sem login. Foram removidos o indicador fixo do modelo, a faixa superior, o título redundante da conversa, o texto vazio de acompanhamento e o cartão informativo da lateral. “Novo atendimento” fica na barra lateral; resolução e encaminhamento ficam alinhados acima da caixa de mensagem. O acompanhamento aparece durante uma operação e os resultados permanecem no chat.

O executor inventaria dispositivos PnP presentes com IDs de hardware, código de problema, fabricante e versão/provedor do driver. A busca online usa a fonte de atualizações configurada no Windows e só retorna ofertas cujo `HardwareID` corresponde exatamente a um único dispositivo observado. Instalação exige confirmação no aplicativo, UAC e nova conferência do alvo e da oferta. O resultado é classificado como versão observada, reinicialização pendente ou não confirmado; somente o teste funcional do usuário confirma que o problema foi resolvido.

Na revisão 1.6.1, pedidos explícitos de driver receberam um fluxo limitado independente da escolha de próximas ferramentas pelo modelo. Isso corrige o caso em que a IA fazia o inventário e a busca, mas encerrava com “não conseguiu selecionar a próxima etapa”. O chat agora apresenta cada chamada como linha expansível, resposta final destacada e duração real da verificação. Esses elementos mostram ações e resultados, não raciocínio interno do modelo.

**Falha corrigida (alta):** no teste do usuário, “Atualize os drivers da minha placa de vídeo” executava inventário e busca, mas terminava sem conclusão útil. Em outra consulta real de leitura, a resposta do modelo dizia que o driver estava “atualizado” mesmo com zero ofertas; a busca só prova que a fonte configurada não ofereceu uma versão compatível naquele momento. O fluxo 1.6.1 agora usa essa formulação limitada e informa explicitamente que nenhum driver foi alterado.

## Resultado obtido

| Verificação | Resultado | Evidência |
|---|---|---|
| Compilação Release do aplicativo | Aprovada: 0 erros e 0 avisos | `dotnet build Nexo.Support/Nexo.Support.csproj -c Release -o artifacts/Nexo-1.6.1-driver-chat` |
| Regressão determinística | Aprovada: 166 verificações | `dotnet run --project Nexo.Checks -c Release` |
| Pedido de driver em hardware real, somente leitura | Aprovado: 168 verificações totais; 1 dispositivo gráfico, 0 ofertas compatíveis; resposta informa a versão observada e nenhuma alteração | `dotnet run --project Nexo.Checks -c Release -- --hardware-live` |
| Prévia WPF em 1380×900 e 1060×720 | Aprovada: abertura direta, navegação, conversa, atalhos, rolagem, novo atendimento na lateral, botões de ação e apresentação compacta das etapas/resultados | `Nexo.exe --screenshot`, arquivo `ui-checks.txt` no diretório executável de teste |
| Instalação e funcionamento de novo driver em hardware real | Não executada | A consulta não retornou oferta compatível; instalação exige máquina/VM de laboratório com restauração |

## Limites observados

- Nenhuma atualização de driver foi oferecida para o dispositivo gráfico observado neste computador. Isso não prova que não existam drivers em outras fontes ou para outro hardware.
- As regressões da instalação verificam bloqueio de ID inexistente, correspondência exata, ambiguidade e resultado não confirmado. Elas não substituem teste elevado em hardware real.
- O Nexo não baixa instaladores de sites arbitrários, não força a troca de driver quando a fonte configurada não oferece uma atualização e não reinicia o computador automaticamente.
- Se o Windows solicitar reinício ou a versão do driver não puder ser confirmada, o atendimento permanece pendente de verificação funcional.

## Revisão 1.6.2 — fonte oficial e apresentação

O caso de zero ofertas para uma única GPU NVIDIA observada agora pode baixar o **NVIDIA App** da página oficial da fabricante, após autorização explícita. O endereço do instalador precisa pertencer a `us.download.nvidia.com`, usar HTTPS e seguir o padrão de versão esperado; o executável baixado precisa ter assinatura Authenticode válida da NVIDIA. O Nexo informa o caminho do instalador e deixa claro que **o driver ainda não foi instalado**. O usuário escolhe e instala o driver no NVIDIA App. Para outras marcas e dispositivos, a ausência de oferta no Windows ainda termina com encaminhamento, sem inventar um download. Este é um limite funcional conhecido, não uma atualização de driver concluída.

O detalhe expansível de execução apresenta identificação de hardware, ofertas encontradas, autorizações e resultado em português. Não exibe mais o JSON bruto ao usuário. Uma busca com zero ofertas informa somente que a fonte consultada não apresentou um pacote compatível; não afirma que o driver está atualizado.

| Verificação | Resultado | Evidência |
|---|---|---|
| Regressão do agente após a revisão | Aprovada: 172 verificações | `dotnet run --project Nexo.Checks -c Release` em 21/10/2026 |
| Consulta de hardware real sem alteração | Aprovada: 174 verificações, uma NVIDIA GeForce RTX 5060 Ti observada, nenhuma oferta compatível na fonte Windows | `dotnet run --project Nexo.Checks -c Release -- --hardware-live` em 22/10/2026; resposta informa o limite e não declara instalação |
| URL oficial, assinatura exigida e resumo sem JSON | Aprovados em testes determinísticos | Casos `Download NVIDIA aceita apenas domínio oficial`, `Detalhe do hardware explica o resultado` e `Busca sem oferta mostra fonte` em `Nexo.Checks` |
| Pacote NVIDIA disponível para o cenário observado | Download oficial e assinatura `Valid`, editor `NVIDIA Corporation`; nenhuma instalação executada | `https://www.nvidia.com/en-us/software/nvidia-app/` → `https://us.download.nvidia.com/nvapp/client/11.0.9.565/NVIDIA_app_v11.0.9.565.exe` |
| Instalação e teste de driver NVIDIA em hardware real | **Bloqueado:** não executados | Requer seleção do driver no NVIDIA App, autorização de alteração e validação após reinício se solicitado |

## Revisão 1.6.3 — pacote de driver NVIDIA

O fluxo para uma única GPU NVIDIA agora consulta também o catálogo oficial do fabricante quando a fonte Windows não retorna oferta. A consulta localiza o produto pelo nome observado, confere Windows 10/11, versão WHQL superior à instalada e URL restrita a `us.download.nvidia.com`. O executor pede confirmação com aceite da licença, baixa o pacote completo, valida a assinatura Authenticode da NVIDIA, solicita UAC, instala o componente de vídeo e lê novamente a versão do dispositivo. Recusa, alvo alterado, resposta malformada, assinatura inválida, erro ou versão não confirmada impedem declarar instalação concluída. Um resultado confirmado ainda pede teste funcional ao usuário.

| Verificação | Resultado | Evidência |
|---|---|---|
| Regressão do agente | Aprovada: 174 verificações, sem alteração de hardware | `dotnet run --project Nexo.Checks -c Release`, 22/10/2026 |
| Instalador gráfico e manifesto | Aprovada: 15 verificações, sem instalação real | `dotnet run --project Nexo.Setup.Checks -c Release`, 22/10/2026 |
| Catálogo para RTX 5060 Ti/Windows 11 | Oferta oficial 621.01 localizada em consulta HTTPS | API de busca e detalhes da NVIDIA, ID 282412, 22/10/2026 |
| Download completo e instalação real do driver | Pendente de laboratório | Requer autorização no Nexo e UAC; pode afetar vídeo e exigir reinicialização |
| Pacotes novos publicados na Oracle | Aprovado: SHA-256 público igual ao local | `nexo-app.zip` `A5D025DD163E3E2EA45DAFC69B3576C53C3A0B0304C9A547D8B3567BDE467038`; `NexoSetup.exe` `463F8C26C35253ED5691763F20E223595E3DAF4FB6B3A8A1CB58494471DD3748` |

O instalador da 1.6.3 é uma janela guiada por etapas. A opção para todos os usuários está desabilitada, pois o Ollama e os blobs do modelo desta distribuição são instalados por perfil. A versão publicada pode reutilizar os pacotes imutáveis de modelo e runtime da 1.6.2; o novo ZIP do aplicativo e seus hashes ficam no manifesto assinado.
