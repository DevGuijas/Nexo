# Event Test Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remover as pastas temporárias criadas pela verificação de eventos legados.

**Architecture:** A conexão SQLite do teste legado usa `Pooling=False`. A limpeza dos diretórios de teste propaga erros em vez de ignorá-los. Nenhum código do aplicativo muda.

**Tech Stack:** C#/.NET 8, Microsoft.Data.Sqlite, Windows.

**Spec:** `docs/superpowers/specs/2026-11-07-event-test-cleanup-design.md`

## Global Constraints

- Preservar a verificação de migração do banco legado.
- Não apagar diretórios preexistentes do `%TEMP%`; medir apenas os criados pela execução.
- Trabalhar em branch isolada e abrir PR para `development`.

## Review Focus

- Uma conexão ainda aberta deve fazer a suíte falhar com erro visível.
- A conexão legada deve ser descartada antes da exclusão.
- Duas execuções seguidas não devem criar diretórios novos.
- As demais verificações devem permanecer aprovadas.
- O CI Windows deve aprovar o commit final.

### Task 1: Fixar a regressão e corrigir a conexão

**Files:**
- Modify: `Nexo.Checks/EventCollectionChecks.cs`

**Interfaces:**
- Consumes: `SqliteConnectionStringBuilder`, `OperationalStore`, `Directory.Delete`.
- Produces: verificação de migração sem pasta residual.

- [x] Acrescentar assertiva de que o diretório legado não existe após a limpeza.
- [x] Rodar `dotnet run --project Nexo.Checks -c Release` e confirmar a falha.
- [x] Definir `Pooling = false` na conexão legada; remover os `catch` vazios da limpeza.
- [x] Rodar a suíte duas vezes, confirmar contagem estável de pastas e `git diff --check`.
- [ ] Solicitar revisão, criar commit e PR para `development`, aguardar o CI e mesclar se tudo passar.
