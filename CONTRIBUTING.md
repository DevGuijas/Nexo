# Como contribuir com o Nexo

## Branches e entregas

- `main` representa a versão estável. O trabalho em andamento acontece em `development`.
- Mudanças grandes podem usar uma branch curta criada a partir de `development`. Integre nela depois de revisão e testes.
- Uma issue descreve o resultado esperado e seus critérios de aceite. Ao concluir, registre na issue o commit, os testes e as limitações antes de fechá-la.
- Não publique instaladores, altere drivers ou faça mudanças reais de rede como parte dos testes comuns.

## Commits claros

Use `tipo(escopo): descrição` em português. Prefira um verbo concreto e o efeito da mudança para quem usa o aplicativo. Cada commit deve tratar uma unidade de trabalho revisável.

Tipos usuais: `feat`, `fix`, `docs`, `test`, `refactor` e `chore`. O escopo pode ser `dados`, `monitoramento`, `diagnostico`, `assistente`, `recuperacao`, `interface` ou `setup`.

Exemplos:

```text
feat(monitoramento): avisa quando a CPU permanece sobrecarregada
fix(recuperacao): mantém resultado desconhecido após timeout do UAC
docs(avaliacao): descreve o cenário de DNS com conectividade parcial
```

No corpo do commit, explique o motivo, o comportamento resultante e como foi verificado. Inclua `Refs #123` quando houver issue. Evite mensagens genéricas como “ajustes”, “update” ou “fix stuff”.

## Organização do projeto

- `Nexo.Support`: aplicativo WPF, coletores de leitura e executor fechado de ações do Windows.
- `Nexo.Checks`: testes e cenários simulados; nenhuma correção real de hardware ou rede na suíte comum.
- `Nexo.Setup` e `Nexo.Setup.Checks`: instalação e verificações do instalador.
- `docs`: arquitetura, protocolos de avaliação e limites dos resultados.

Dados operacionais ficam no perfil do Windows. `data.json` guarda conversas; `operations.db` guarda métricas, eventos e intervenções. Sensores indisponíveis e hipóteses devem aparecer como tal. O modelo local explica evidências; ações continuam restritas ao executor e à confirmação específica do usuário.

## Antes de enviar

1. Rode `dotnet build Nexo.Support -c Release`.
2. Rode `dotnet run --project Nexo.Checks -c Release` e `dotnet run --project Nexo.Setup.Checks -c Release`.
3. Para mudanças de rede, rode `Nexo.Checks/NetworkScriptChecks.ps1` e `Nexo.Checks/NetworkAdapterChecks.ps1` com PowerShell.
4. Revise `git status` e `git diff --check`. Envie somente arquivos necessários ao projeto.

Não versione `.env`, chaves, certificados privados, `node_modules`, `modules`, ambientes virtuais, `bin`, `obj`, bancos locais, logs ou prévias geradas. Forneça valores de exemplo em `.env.example`, sem credenciais reais. Se um segredo entrar no histórico, remova-o e faça a rotação da credencial; adicioná-lo ao `.gitignore` depois não o apaga dos commits anteriores.
