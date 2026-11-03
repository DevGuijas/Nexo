## O que mudou

Descreva o problema resolvido e o comportamento entregue. Referencie a issue (`Refs #...` ou `Closes #...`).

## Verificação

- [ ] `dotnet build Nexo.Support -c Release`
- [ ] `dotnet run --project Nexo.Checks -c Release`
- [ ] `dotnet run --project Nexo.Setup.Checks -c Release`, se aplicável
- [ ] Interface ou scripts específicos verificados, se aplicável

## Segurança e limites

Descreva autorização exigida para mudanças no Windows, dados locais afetados, testes somente simulados e validações pendentes em laboratório. Confirme que nenhum segredo, `.env`, módulo de dependência ou artefato gerado entrou no commit.
