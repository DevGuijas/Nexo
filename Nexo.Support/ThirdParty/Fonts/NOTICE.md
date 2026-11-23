# Fontes da interface

O Nexo embute, sem modificação, estas fontes como recurso do aplicativo (`Nexo.Support/Fonts`). Todas são distribuídas sob a SIL Open Font License 1.1; o texto da licença de cada família acompanha o Nexo nesta pasta. As fontes não são vendidas separadamente nem renomeadas.

| Arquivo | Família | Origem | SHA-256 |
|---|---|---|---|
| `BarlowCondensed-SemiBold.ttf` | Barlow Condensed (600) | [google/fonts `06bfe0d`](https://github.com/google/fonts/tree/06bfe0d62a98375cb09c942fa18e68bd32cff057/ofl/barlowcondensed) | `7b619d14bc2327509a9ef32b0890f709626f7ecc9ff61191c2a4314c5499d2d9` |
| `BarlowCondensed-Bold.ttf` | Barlow Condensed (700) | idem | `e476562ec9c1e16cf16475895b511f08c804f438cc9a9f80a44ea50a0eeb5b65` |
| `IBMPlexMono-Regular.ttf` | IBM Plex Mono (400) | [google/fonts `06bfe0d`](https://github.com/google/fonts/tree/06bfe0d62a98375cb09c942fa18e68bd32cff057/ofl/ibmplexmono) | `6a3412f058c7d8dfd9170c41e85ade48e5156ecb89356110ca57a0a27734af46` |
| `IBMPlexMono-Medium.ttf` | IBM Plex Mono (500) | idem | `a9b4c49bb299e05b5f6c481e7fb5e78943d2793249a0c8874ab574a2d1ea6755` |
| `IBMPlexSans-Regular.ttf` | IBM Plex Sans (400) | [IBM/plex `2e90fe3`](https://github.com/IBM/plex/tree/2e90fe366fdc27c12682db13e5aa20ded3d90908/packages/plex-sans/fonts/complete/ttf) | `b47ebd72b5ce53b60f909033ff84d6fc12358cc147e2981f8c321ad3f36246b6` |
| `IBMPlexSans-Medium.ttf` | IBM Plex Sans (500) | idem | `a494d5a69fa094f7071a9ffbc98e86c85337d1a29f3fc1ff7c2e1c46161ddf27` |
| `IBMPlexSans-SemiBold.ttf` | IBM Plex Sans (600) | idem | `c51119b25f4a296da2b66edf3201b9aa1735091ed7efc1fca0313346f3851a4d` |

O IBM Plex Sans vem do repositório oficial da IBM porque o `google/fonts` só publica a versão variável (`IBMPlexSans[wdth,wght].ttf`), e o WPF não seleciona os pesos de uma fonte variável. Cada arquivo foi conferido contra o blob do GitHub (`git hash-object`) no commit indicado.

Licenças: `BarlowCondensed-OFL.txt` (Copyright 2017 The Barlow Project Authors), `IBMPlexSans-OFL.txt` e `IBMPlexMono-OFL.txt` (Copyright © 2017 IBM Corp., nome reservado "Plex"). Mantenha estes arquivos ao redistribuir o Nexo.
