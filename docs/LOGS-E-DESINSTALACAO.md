# Logs de instalação e desinstalação da prévia 1.2.0 e posteriores

## Localização

Cada execução do instalador, inclusive `--check`, e cada desinstalação gera um arquivo separado em `%LOCALAPPDATA%\Nexo\InstallerLogs` (`C:\Users\<usuário>\AppData\Local\Nexo\InstallerLogs`). O nome é `install-<data UTC>-<id da sessão>.jsonl`. O aplicativo fica em `%LOCALAPPDATA%\Programs\Nexo`; o desinstalador é `%LOCALAPPDATA%\Programs\Nexo\NexoUninstall.exe`.

O arquivo é JSON Lines: **uma linha JSON por evento**. Os campos de nível superior são `schemaVersion` (atualmente `1`), `sessionId`, `timestampUtc`, `event`, `status` e `data`. Uma sessão começa com `run_started` e termina com `run_finished` (`success`, `failure` ou `interrupted`). A duração total, em milissegundos, está no evento final. Falha na criação do arquivo impede a instalação antes de qualquer alteração. Os arquivos ficam somente no computador; esta versão não os envia para Oracle, domínio ou serviço de estatísticas. Proteja seu acesso e defina uma política de retenção antes de coletá-los de usuários.

## Eventos e uso estatístico

| Evento | Informação útil |
| --- | --- |
| `run_started` | Modo (`install`, `check`, `uninstall`), versão do instalador, nome do computador, ID persistente do dispositivo, Windows e arquitetura, CPU lógica, memória física, idioma, fuso, espaço livre/total do disco e presença anterior do Nexo e Ollama. |
| `geoip` | `success` com código do país, país, região e cidade; `skipped` se a opção não foi marcada ou se é `--check`; `unavailable` se a consulta falhar. |
| `manifest` | Início da consulta e versão/modelo após verificação da assinatura. |
| `package` | Nome, tamanho, SHA-256, origem cache/download e tempo de obtenção; em `--check`, disponibilidade remota. |
| `application`, `uninstaller` | Preparação, ativação, versão e tempo; presença do desinstalador na pasta do aplicativo. |
| `ollama`, `model` | Instalação ou uso do Ollama existente, validação da assinatura, importação do modelo e tempos. |
| `shortcut_desktop`, `shortcut_start_menu` | Criação, ausência, remoção ou falha dos atalhos. |
| `installer_cache`, `application_stage`, `uninstall` | Etapas e resultado da desinstalação. |
| `error`, `run_finished` | Tipo e mensagem do erro, resultado final e duração. |

O `deviceId` é um SHA-256 derivado do identificador local do Windows com um prefixo próprio do Nexo; o identificador original não é gravado. Ele permite relacionar instalações sucessivas no mesmo Windows, mas pode mudar após reformatação. O nome do computador e esse ID são dados identificáveis: compartilhe os logs apenas quando houver uma finalidade e autorização adequadas. Mensagens de erro podem conter caminhos do perfil; revise ou pseudonimize antes de uma análise centralizada.

O assistente mostra uma opção para consultar a localização aproximada por GeoIP. Se marcada, ele faz uma requisição HTTPS a [ipwho.is](https://ipwho.is/); o serviço recebe o IP público para responder. O log retém somente país, região e cidade, sem IP bruto, coordenadas ou código postal. A localização pode apontar para VPN, proxy ou provedor de internet, e uma falha da consulta não impede a instalação. A consulta não ocorre em `--check` nem durante a desinstalação.

Para contar resultados por modo e status em PowerShell:

```powershell
$logFolder = Join-Path $env:LOCALAPPDATA 'Nexo\InstallerLogs'
Get-ChildItem -LiteralPath $logFolder -Filter '*.jsonl' -File |
  ForEach-Object { Get-Content -LiteralPath $_.FullName | ConvertFrom-Json |
    Where-Object event -eq 'run_finished' |
    Select-Object sessionId, status, @{ Name='version'; Expression={ $_.data.result.version } } } |
  Group-Object status, version | Select-Object Name, Count
```

Para distinguir instalações de verificações e desinstalações, una `run_started.data.mode` e `run_finished` pelo `sessionId`. Para estatísticas de localidade, use somente eventos `geoip` com `status=success`; conte `skipped` e `unavailable` separadamente para evitar distorções. Não interprete um evento `application.activated` isolado como instalação concluída: use o `run_finished` de sucesso.

## Desinstalação

Abra `NexoUninstall.exe` dentro da pasta do aplicativo e confirme na janela. Feche o Nexo antes. O desinstalador remove a pasta do aplicativo, suas pastas de preparação/backup, atalhos que apontam para esta instalação e o cache de instalação do Nexo. Preserva Ollama, `qwen3:8b` e os logs locais, pois o runtime/modelo podem servir a outros usos e os registros são necessários para auditoria. A remoção também gera um log.

Para remover o executável instalado enquanto ele está aberto, o desinstalador inicia uma cópia de trabalho em `%TEMP%\Nexo-Uninstall`, espera a janela fechar e faz a limpeza. Uma cópia temporária desse auxiliar pode permanecer nessa pasta após o processo terminar; ela pode ser apagada manualmente mais tarde. A desinstalação real não foi executada no PC de desenvolvimento; a lógica foi testada com uma instalação fictícia temporária.
