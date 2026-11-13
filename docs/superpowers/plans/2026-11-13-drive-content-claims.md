# Resposta sobre conteúdo de unidades (#93)

## Evidência

Na consulta real “o que é o D:?” em 13/11, o modelo identificou o SSD BIWIN SATA pelo inventário, mas afirmou que a unidade não tinha arquivos visíveis e ofereceu verificar seu conteúdo. A ferramenta só vê metadados de hardware e volume. Uma leitura independente mostrou 11 entradas na raiz e 108,1 GB usados.

## Plano

1. Reproduzir em teste a resposta sem evidência e algumas variações, mantendo válidas respostas sobre hardware, espaço e orientação ao usuário.
2. Fazer o controle de evidências recusar afirmações de conteúdo e ofertas de inspeção de arquivos que nenhuma ferramenta executa. Dar ao modelo motivo específico para reescrever.
3. Rodar testes completos e revisão. Criar PR para `development`, conferir CI e integrar. Manter #93 aberta para os cenários de validação ainda pendentes.
