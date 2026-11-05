# Nexo clean UI — design

## Intenção

Dar ao Nexo uma interface desktop clara, calma e coerente em todas as telas, mantendo os fluxos de diagnóstico, autorização e registro. Os exemplos locais em `C:\Users\Aether\Downloads\AplicaçõesEstilizaçãoExample` orientam a hierarquia: o tema claro do Seu Imposto usa superfícies quentes, bordas discretas e tipografia editorial; a página de resultados do TSE favorece leitura rápida de dados e estados. Marcas, textos e arquivos desses sites não serão copiados.

## Abordagens consideradas

1. **Recolorir o tema atual:** baixo custo, mas mantém símbolos tipográficos inconsistentes e a hierarquia pequena das páginas.
2. **Design system WPF nativo e refatoração completa das telas (escolhida):** recursos compartilhados para cores e controles, ícones vetoriais locais, shell e conteúdo redesenhados. Preserva tecnologia, funcionamento offline e lógica atual.
3. **Trocar a interface por WebView:** daria acesso a CSS, mas acrescentaria runtime e reescreveria a interface sem necessidade.

O pedido já autoriza executar a refatoração e publicar o PR; a direção visual acima é uma decisão de implementação, sujeita à revisão no PR.

## Linguagem visual

- Fundo marfim `#F4F5F2`, superfícies brancas, barra lateral `#FAFBF9`, texto grafite `#192B2B`, separadores `#E1E7E3`.
- Ação principal em teal profundo `#185B58`, superfícies ativas em `#E4F0ED`. Verde, âmbar e vermelho apenas para estados com significado.
- Tipografia nativa Segoe UI Variable/Segoe UI para controles e corpo; Georgia só no título acolhedor do início. Títulos de página em sans com peso semibold.
- Grade de 8 px, cartões de 14 px de raio, botões de 10 px e foco visível. Ícones feitos com geometrias WPF de traço uniforme e escala vetorial; sem fontes de símbolos ou dependência remota.
- Texto e dados técnicos permanecem legíveis. Alertas não dependem apenas da cor, conservam rótulos. Estados desabilitados e foco por teclado continuam distinguíveis.

## Superfícies

- **Shell:** logotipo discreto, navegação alinhada com ícones, destaque de seção atual, botão de novo atendimento e rodapé de privacidade/sessão.
- **Assistente:** cabeçalho simples, saudação com maior hierarquia, seis atalhos em grade, conversa com cartões separados por papel/estado, detalhes de ferramenta, progresso, respostas rápidas e compositor.
- **Demais páginas:** cabeçalho comum, cartões e controles consistentes para saúde, histórico, encaminhamentos, configurações, registro de ações e telas de conta; modal de autorização segue o mesmo tema.
- **Tamanhos:** verificar 1380×900 e 1060×720, sem corte de ações, navegação ou compositor. Rolagem preserva acesso a conteúdo longo.

## Limites e aceitação

- Nenhum fluxo de diagnóstico, comando, permissão, armazenamento ou instalação muda por esta entrega.
- Recursos visuais ficam no repositório, sem material de terceiros e sem dependência de rede.
- A prévia isolada captura as telas principais e verifica navegação, posição das ações, controles e teclado. Regressões existentes e build Release devem passar; capturas são inspecionadas visualmente.
- Publicar branch de feature e PR com base `development`; atualizar `Nexo-Development.exe` na Área de Trabalho após verificar o binário novo. A issue #24 fecha após entrega, mantendo o PR para revisão/merge.
