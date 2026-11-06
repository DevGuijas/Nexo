# Nexo: monitoramento e recuperação locais — desenho incremental

## Intenção

Evoluir o Nexo 1.6.3 de atendimento reativo para uma central local de saúde,
diagnóstico e recuperação assistida. A pessoa deve entender o problema,
enxergar a evidência e autorizar separadamente qualquer mudança. O resultado
de uma ferramenta não equivale à resolução funcional.

## Escolha de arquitetura

Foram considerados: (A) reescrever a aplicação com um serviço Windows e nova
interface; (B) ampliar os arquivos atuais diretamente; (C) acrescentar
componentes pequenos à aplicação WPF existente e manter o executor fechado.
Escolha: C. A preserva menos código e eleva o risco; B amplia o acoplamento
dos arquivos já extensos. C mantém os fluxos, a identidade visual, os testes
e o Qwen3 8B no Ollama local. A coleta contínua funciona enquanto o Nexo
estiver aberto. Um serviço persistente após o fechamento é uma entrega
separada, pois exige instalação, privilégio, privacidade e recuperação próprios.

## Objetivos e limites

- Executar as seis frentes aprovadas: base de segurança/dados, monitoramento,
  eventos/diagnóstico, integração com IA local, recuperação/drivers e interface/
  avaliação.
- Diagnóstico local e apresentação de histórico devem funcionar sem internet.
  Consulta de ofertas de drivers continua opcional e dependente de internet.
- O modelo é dado não confiável: só o código escolhe e executa uma ação de uma
  lista fechada. Toda mudança exige consentimento específico; elevação exige UAC.
- Métricas têm unidades, horário, origem e estado de disponibilidade. Sensor
  inexistente produz `indisponível`, nunca zero inventado.
- Alertas dependem de limiar e duração configuráveis; recorrência depende de
  amostras/eventos suficientes. Uma falha de coleta não é falha do computador.
- Correção exige comparação antes/depois e, quando necessário, teste funcional
  do usuário. Falha, recusa e estado incerto são resultados distintos.
- Dados permanecem no perfil Windows, com retenção configurável. O `data.json`
  legado continua legível. Nenhum dado é enviado a um servidor de IA.
- Não instalar drivers reais nem alterar a máquina durante a suíte comum.

## Componentes e contratos

1. `HealthSnapshot` representa uma coleta: CPU, RAM, volumes, processos,
   adaptadores, serviços e sensores opcionais, cada um com estado de coleta.
2. `HealthSampler` consulta o Windows em leitura e `MonitoringEngine` agenda
   amostras com cancelamento, sem sobrepor execuções. `HealthRules` avalia
   duração, limites e supressão de alertas duplicados.
3. `EventCollector` lê System, Application e Setup com limites de horário e
   quantidade; `EventAnalyzer` agrupa recorrências por provedor e ID.
4. `DiagnosticReport` reúne achados, evidências, hipótese, gravidade, impacto,
   recomendação e confiança calculada por regras, com indicação de limites.
5. `OperationalStore` armazena séries resumidas, ocorrências e intervenções em
   SQLite local. Faz migração versionada sem descartar `data.json` e mantém a
   persistência atual das conversas até migração específica posterior.
6. `AgentService` recebe resumos estruturados para explicar achados, mas o
   `WindowsTools` continua único executor. Se Ollama falhar, o diagnóstico
   determinístico continua disponível.
7. `RecoveryEngine` padroniza a máquina de estados de uma ação: proposta,
   autorização, execução, validação e resultado. O processo elevado recebe
   supervisão e nunca é tratado como revertido só porque houve cancelamento.
   Drivers ganham leitura do Driver Store e tentativa de ponto de recuperação
   quando o Windows oferecer; falta dessa proteção é exibida antes de instalar.
8. O WPF oferece dashboard, ocorrências, eventos, recuperação e drivers sobre
   dados reais. Mantém atendimentos, encaminhamentos e registro de ações.

## Dados, privacidade e desempenho

SQLite é a única dependência nova prevista (`Microsoft.Data.Sqlite`), necessária
para consulta temporal e gravação incremental. Os registros de monitoramento
guardam totais e identificadores mínimos; comandos/argumentos sensíveis não
são copiados indiscriminadamente. Amostragem padrão: 30 segundos com janela
curta em memória, intervalos configuráveis de 10–300 segundos e retenção padrão
de 30 dias. A UI não bloqueia durante coleta. Temperatura e SMART são opcionais.

## Avaliação

Os sete cenários exigidos são: adaptador de rede desativado, IP incorreto, DNS
problemático, serviço Windows interrompido, consumo excessivo de memória,
falha de driver/dispositivo sem driver e disco com pouco espaço. Cada um recebe
estado inicial, oráculo, permissão, ação autorizada, estado posterior e
restauração. Testes simulados medem contrato e segurança nesta entrega. A
execução que altera um Windows real requer VM/laboratório preparada e não faz
parte da suíte comum nem deste ambiente de desenvolvimento. Entregar o roteiro
reproduzível para essa execução, marcando seus resultados como pendentes.
Registrar detecção, acerto, falso positivo, tempo e verificação sem inventar
taxa de sucesso antes da execução. A documentação identifica sempre qual
ambiente produziu cada resultado.

## Decisões deixadas à implementação

Escolher entre APIs .NET e PowerShell para cada sensor conforme disponibilidade
no Windows e custo de coleta. Manter as telas atuais e acrescentar páginas
gradualmente. Priorizar nomes e mensagens em português, sem modificar a
autorização já estabelecida para as ações antigas.
