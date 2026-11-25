# Preview Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Audit the latest merges and blockers, then publish an immutable preview installer for current `development`.

**Architecture:** Keep stable `latest.json` and `NexoSetup.exe` untouched. Build a new app ZIP and setup EXE, sign the preview manifest with the existing preview key, publish files in activation order and verify the downloaded result.

**Tech Stack:** .NET 8 WPF, PowerShell 7, Oracle Object Storage, GitHub.

**Spec:** `docs/superpowers/specs/2026-11-25-preview-integration-design.md`

## Global Constraints

- No VM download or execution; use this PC for tests.
- Preserve `E:\TCC\23_11_2026` and stable Oracle objects.
- Never commit or upload private signing keys, user logs or test artifacts.
- Do not claim stable readiness or merge `main` while release criteria remain unmet.

## Review Focus

- Preview packaging must leave native WPF DLLs next to `Nexo.exe` for the system sensor reader.
- The setup window must not claim an old fixed app version while the manifest points to a new one.
- Package and installer paths must be new and immutable.
- A partial upload must not activate a preview manifest.
- Issue closure must be supported by criteria and actual evidence.

---

### Task 1: Audit and baseline

**Files:** `CHANGELOG.md`, GitHub PRs/issues, `docs/superpowers/specs/2026-11-25-preview-integration-design.md`

- [x] Fetch GitHub state and identify merged/open PRs, open issues and release blockers.
- [ ] Run baseline Nexo, setup and Office suites in an isolated worktree.
- [ ] Reconcile which issues can be closed from evidence and which remain blocked.

### Task 2: Preview packaging and version

**Files:** `scripts/Build-PreviewRelease.ps1`, `Nexo.Setup/SetupWindow.cs`, `Nexo.Support/Nexo.Support.csproj`, `Nexo.Setup/Nexo.Setup.csproj`, `Nexo.Setup.Checks/Program.cs`, `CHANGELOG.md`

- [ ] Write a failing check for no extraction in preview app packaging and for the setup window not hardcoding `1.7.0`; observe failure.
- [ ] Publish preview app without native self-extraction; make setup version text read the manifest or generic preview wording; bump immutable preview app/setup versions.
- [ ] Run setup checks, Nexo checks, Office checks and preview manifest checks.

### Task 3: Build and publish

**Files:** `README.md`, `docs/INSTALADOR-NUVEM.md`, `docs/RECUPERACAO-2026-11-22.md`, Oracle objects.

- [ ] Build app ZIP, setup EXE and signed preview manifest with `Build-PreviewRelease.ps1`.
- [ ] Verify ZIP contents, signatures, hashes and `--check` locally.
- [ ] Publish app ZIP and setup EXE, then `preview/latest.sig`, then `preview/latest.json`.
- [ ] Download the public installer and confirm SHA-256; install on this PC and inspect shortcut, log, app and uninstaller.
- [ ] Update documentation with exact version, URL, hash and known limits.

### Task 4: Integration handoff

**Files:** GitHub PR and issues.

- [ ] Rerun final checks and review diff.
- [ ] Commit and push the preview branch; prepare a draft PR toward `main` with blockers.
- [ ] Close only issues with completed criteria and summarize the remaining work for stable release.
