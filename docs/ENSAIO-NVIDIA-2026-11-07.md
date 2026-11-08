# Ensaio real de instalação de driver NVIDIA — 07/11/2026

## Escopo

Ensaio com alteração real de driver, feito no PC físico do usuário, não em VM: Windows 10 Pro 19045, NVIDIA GeForce RTX 3060 Ti, Ryzen 7 5700X e 32 GB de RAM. No Windows 10 Pro a VM não recebe a GPU, e o instalador da NVIDIA não acharia a placa. A decisão de usar o PC físico foi do usuário e está registrada na issue #8. O código usado foi a `development` em `c73ef99`.

As ferramentas reais do Nexo foram chamadas por um programa de teste que referencia o `Nexo.Checks.dll`: `get_hardware_inventory` (display), `find_official_nvidia_driver` e `install_official_nvidia_driver`. O aplicativo WPF e a conversa com o modelo ficaram de fora. O callback de aprovação só aceitava o pedido exato da 621.14. O usuário confirmou a instalação e a licença e clicou nos UACs.

## Antes do ensaio

| Consulta | Resultado | Evidência |
|---|---|---|
| Consulta real, somente leitura, com a `development` em `05c4e84` | Falhou: “A NVIDIA não identificou de forma única o modelo observado” | [Comentário no PR #52](https://github.com/DevGuijas/Nexo/pull/52#issuecomment-6714635202) |
| Consulta real, somente leitura, com o PR #52 (`42fb7db`), no mesmo PC, com o vídeo em 32.0.16.2101 | `Available=true`, `DriverId` 283968, `Version` 621.14, `InstalledVersion` 32.0.16.2101, `DownloadUrl` `https://us.download.nvidia.com/Windows/621.14/621.14-desktop-win10-win11-64bit-international-dch-whql.exe` | [Comentário no PR #52](https://github.com/DevGuijas/Nexo/pull/52#issuecomment-6714635202) |

As duas consultas usaram as ferramentas reais `get_hardware_inventory` e `find_official_nvidia_driver`, com o callback de aprovação recusando sempre. Nenhum driver foi baixado nem alterado. A falha em `05c4e84` vinha do catálogo da NVIDIA, que lista a série 30 sem o prefixo “NVIDIA” (#51); o PR #52 corrigiu a comparação.

## Linha do tempo da instalação

| Horário (-03:00) | Evento |
|---|---|
| 21:04:54 | Oferta e aprovação |
| 21:05:02 | Ponto de restauração nº 4 confirmado |
| 21:05:28 | Download de 1037274832 bytes, assinatura válida |
| 21:05:33 | Instalação com UAC |
| 21:06:22 | O setup desinstala o `nvhda` 1.4.6.6 (áudio HDMI) |
| 21:07:50 | Vídeo instalado |
| 21:08:26 | Fim |

## Resultado obtido

| Verificação | Resultado | Evidência |
|---|---|---|
| Retorno do Nexo | `success:false`, “O instalador NVIDIA terminou com código -2147024891” (0x80070005); a intervenção ficou como `failed` | [Comentário na #13](https://github.com/DevGuijas/Nexo/issues/13#issuecomment-6714920800); [comentário na #12](https://github.com/DevGuijas/Nexo/issues/12#issuecomment-6714923661) |
| Driver de vídeo depois da instalação | 32.0.16.2114 (= 621.14), Code 0, 1920×1080 a 239 Hz | [Comentário na #12](https://github.com/DevGuijas/Nexo/issues/12#issuecomment-6714923661); [comentário na #13](https://github.com/DevGuijas/Nexo/issues/13#issuecomment-6714920800) |
| Teste funcional de vídeo do usuário | “tudo normal” | Relato do usuário nos registros locais do operador |
| Áudio HDMI da GPU | Code 28 e NVHDA desativado; o endpoint de áudio do monitor ficou inativo (Status Unknown) e depois foi recriado | Code 28 e NVHDA: [comentário na #12](https://github.com/DevGuijas/Nexo/issues/12#issuecomment-6714923661) e issue #53; endpoint: registros locais do operador |
| Reinicialização | Não foi pedida | [Comentário na #12](https://github.com/DevGuijas/Nexo/issues/12#issuecomment-6714923661) |

O Nexo declarou falha numa instalação em que o vídeo ficou na versão oferecida e funcionando. No mesmo processo, o setup removeu o áudio HDMI da GPU.

## Ensaios seguintes

| Ordem | Ensaio | Resultado |
|---|---|---|
| 1 | Reinstalação manual do `nvhda` 1.4.6.6 por `pnputil` | Code 0 |
| 2 | Setup 621.14 com `-s -n Display.Driver HDAudio.Driver` | Aceitou o componente posicional e reinstalou o `nvhda`. Saiu de novo com 0x80070005. O `setupapi` mostra a falha ao apagar do DriverStore o pacote de vídeo em uso (`SetupUninstallOEMInf oem2.inf`, `FAILURE 0xE000023D`), provável origem do código |
| 3 | Setup 621.01 com os mesmos argumentos, para voltar de versão | Saiu com 0xE4000020 em 30 s sem alterar driver. Com `-s`, o único sinal foi o código de saída |
| 4 | Restauração do sistema para o ponto nº 4 | Eventos 8199 e 8202, sucesso. O vídeo voltou para 32.0.16.2101 (621.01), mas o áudio ficou no `nvhda` 1.4.0.1, que sobrou no DriverStore. A restauração não recriou o pacote 1.4.6.6 apagado pelo setup |

O monitor do usuário (Samsung Odyssey G40B) não tem alto-falante. Por isso, os testes de som pelo HDMI depois da reinstalação do áudio, que deram “sem som”, não indicam falha de driver e não servem de evidência sobre som.

## Achados e encaminhamento

1. **Falso negativo pelo código de saída.** O instalador saiu com 0x80070005 e o Nexo marcou a intervenção como falha, embora o vídeo estivesse na 621.14 com Code 0. Encaminhado na issue #13.
2. **Instalar só `Display.Driver` remove o áudio HDMI.** O setup desinstalou o `nvhda` e o áudio da GPU ficou com Code 28. Encaminhado na issue #53, no [PR #54](https://github.com/DevGuijas/Nexo/pull/54).
3. **Downgrade sem efeito.** O setup da 621.01 saiu com 0xE4000020 em 30 s sem alterar driver. Com `-s`, o único sinal foi o código de saída. O Nexo não deve contar com o instalador para voltar de versão.
4. **O ponto de restauração não recupera pacotes de driver apagados pelo setup.** A restauração voltou o vídeo, mas o áudio ficou num pacote mais antigo que já estava no DriverStore.
5. **A supervisão de 35 min não foi testada no limite.** A instalação levou cerca de 3 min.

## Limites do ensaio

- Feito em PC físico, não em VM, fora do [protocolo experimental](AVALIACAO-MONITORAMENTO-E-RECUPERACAO.md#protocolo-experimental).
- É a atualização de um driver que funcionava, não o cenário 6 do protocolo (“driver ausente/com erro”).
- Um único hardware.
- Não cobre a conversa com o modelo nem o aplicativo WPF.
- Não cumpre os critérios da #8 (VM isolada com snapshot) nem o último item da #12 (validação na VM Windows da #8), que continuam em aberto.

Os dados brutos do ensaio ficaram nos registros locais do operador, fora do repositório.
