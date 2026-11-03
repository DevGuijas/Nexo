# Nexo Clean UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Aplicar uma identidade visual clean e consistente a todas as telas WPF do Nexo.

**Architecture:** Centralizar paleta e estilos em recursos WPF, criar ícones vetoriais internos e adaptar shell e componentes gerados em `MainWindow`. A lógica de suporte continua nos mesmos métodos.

**Tech Stack:** .NET 8, WPF, XAML, C#; sem pacote visual externo.

**Spec:** `docs/superpowers/specs/2026-11-03-nexo-clean-ui-design.md`

## Global Constraints

- Sem copiar marcas ou assets dos exemplos locais.
- Suporte offline e operações do Windows permanecem idênticos.
- Verificar 1380×900 e 1060×720 e navegação por teclado.
- PR para `development`, executável atualizado na Área de Trabalho.

## Review Focus

- Nome comprido de usuário, título de atendimento e texto de resultado não devem cortar ações.
- Conteúdo em 1060×720 deve manter navegação, compositor e progresso acessíveis.
- Falha/aviso/sucesso precisam de texto além de cor.
- Alterações de tema não devem afetar os fluxos simulados de confirmação e conversa.
- Ícones devem escalar sem rasterização ou caracteres ausentes.

---

### Task 1: Recursos e ícones

**Files:** `Nexo.Support/App.xaml`, `Nexo.Support/UiIcons.cs`, `Nexo.Support/MainWindow.xaml.cs`

**Interfaces:** `UiIcons.Create(string name, double size)` retorna `Viewbox`; paleta usa chaves de recursos em `App.xaml`.

- [x] Adicionar uma verificação de prévia que exija recursos de paleta, estilos reutilizáveis e ícones vetoriais; executar e observar a falha esperada.
- [x] Criar recursos e ícones para marca, navegação, categorias e ações; trocar os helpers de cor por chaves semânticas.
- [x] Compilar e executar a prévia até a verificação passar.

### Task 2: Shell e assistente

**Files:** `Nexo.Support/MainWindow.xaml`, `Nexo.Support/MainWindow.xaml.cs`

**Interfaces:** navegação mantém `Tag`, eventos e nomes de automação; cartões e ações usam estilos compartilhados.

- [x] Ampliar a prévia para conferir layout nas duas resoluções e presença dos seis atalhos com ícones; observar falha antes da mudança.
- [x] Redesenhar barra lateral, cabeçalho do chat, saudação, atalhos, mensagens, progresso, respostas e compositor.
- [x] Renderizar capturas de início, conversa e progresso; corrigir cortes, alinhamento e foco.

### Task 3: Páginas e diálogo

**Files:** `Nexo.Support/MainWindow.xaml.cs`, `Nexo.Support/App.xaml`

**Interfaces:** manter `Heading`, `Card`, `Action`, `Field`, `Secret`, `Check` como pontos únicos de construção visual.

- [x] Adicionar capturas isoladas de saúde, histórico, encaminhamentos, configurações, auditoria e conta à prévia; verificar que os arquivos ainda não existem.
- [x] Aplicar tipografia, estados, cartões e formulários consistentes a todas as páginas e ao diálogo de autorização.
- [x] Inspecionar as capturas, inclusive textos longos e estado vazio; ajustar visualmente.

### Task 4: Regressão e entrega

**Files:** `docs/superpowers/specs/2026-11-03-nexo-clean-ui-design.md`, `docs/superpowers/plans/2026-11-03-nexo-clean-ui.md`; artefatos ignorados pelo Git.

- [x] Executar `dotnet run --project Nexo.Checks -c Release` e `dotnet run --project Nexo.Setup.Checks -c Release`.
- [x] Publicar `Nexo.Support` para win-x64 self-contained single-file, executar `--screenshot` e inspecionar todas as capturas.
- [x] Confirmar diff sem binários/segredos, criar commits em português, enviar branch e abrir PR para `development` vinculado à #24.
- [x] Substituir o executável da Área de Trabalho, comparar SHA-256 e verificar assinatura/status; fechar a #24 após a entrega.
