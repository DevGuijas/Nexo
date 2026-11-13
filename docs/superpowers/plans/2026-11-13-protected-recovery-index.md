# Recuperação de rede a partir da cópia protegida (#106)

## Resultado esperado

Ao reabrir o Nexo, uma transação elevada sem resultado deve ser descoberta pelo registro confiável em `%ProgramData%`, mesmo que a pasta em `%LOCALAPPDATA%` tenha sumido ou contenha resultados plantados. A recusa local do usuário pode valer durante a sessão atual, mas a próxima abertura pede a decisão novamente enquanto o backup protegido estiver pendente.

## Limites

- Nenhuma troca real de IP/DNS neste PC. A validação elevada com snapshot continua na #8.
- Resultado `result.json`/`recovery-result.json` protegido e confiável encerra a transação.
- Pasta protegida com ACL inesperada nunca serve como fonte de restauração.
- O identificador continua sendo GUID no formato `N`; caminhos locais com link não são seguidos.

## Passos

- [x] Acrescentar regressões para pasta local ausente, resultado local forjado, resultado protegido e recusa legítima.
- [x] Fazer `Scan` incluir operações protegidas confiáveis e fazer `Inspect` usar o registro protegido para identidade, fase e PID.
- [x] Ignorar marcadores de conclusão apenas locais quando existe backup protegido pendente; restaurar a pasta local de trabalho com checagem de link.
- [x] Preservar a oferta quando o caminho local for substituído por arquivo/junção ou a listagem for negada; usar pasta alternativa com o mesmo id e limpá-la após `Recover`.
- [ ] Executar testes focados, suíte completa, build do app e instalador, scripts de rede e revisão independente.
- [ ] Abrir PR para `development`, acompanhar CI, integrar e manter a #106 aberta até a VM.
