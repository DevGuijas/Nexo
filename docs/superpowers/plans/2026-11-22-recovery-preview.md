# Nexo Recovery Preview Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Recover the latest Nexo source, remove redundant backup material, and distribute a self-contained installer for the 1.7.0 preview from Oracle without changing the existing stable channel.

**Architecture:** Keep `origin/development` as the source of 1.7.0. Build a second installer variant with a separate RSA public key and `/preview/latest.json`; its signed manifest references immutable application, Ollama and model packages at the bucket root. The old `/latest.json`, signature and installer stay valid.

**Tech Stack:** C#/.NET 8 WPF, PowerShell 7, Git, OCI Object Storage.

**Spec:** `docs/RECUPERACAO-2026-11-22.md`

## Global Constraints

- Build and test on this Windows PC. Do not download or run a VM.
- The installer must visibly request installation and verify the manifest signature, file sizes and SHA-256 hashes.
- Publish 1.7.0 as a preview; preserve the stable 1.6.3 manifest and installer.
- The private signing key must remain outside Git and the public bucket.

## Review Focus

- A preview manifest must resolve package paths to the bucket root so it can reuse the existing 6.5 GiB of dependencies.
- An invalid signature or mismatched hash must stop installation before executing a package.
- Re-running the installer must preserve the prior application if staging fails.
- Unsupported or absent Ollama must report a clear error instead of claiming success.
- Cloud objects must be checked by HTTP after upload, not inferred from upload completion alone.

---

### Task 1: Recover and organize

**Files:** `docs/RECUPERACAO-2026-11-22.md`, `.gitignore`

- [x] Recover the latest `development` branch into `codex/recovery-2026-11-22`.
- [x] Build the application and installer, then run the three baseline suites.
- [x] Recycle verified duplicate VM disks, caches, old builds and test outputs; preserve reports, Git bundle and legacy operational data.
- [x] Record retained and removed locations in the recovery document.

### Task 2: Add a preview installer variant

**Files:** `Nexo.Setup/Program.cs`, `Nexo.Setup/SetupWindow.cs`, `Nexo.Setup/Nexo.Setup.csproj`, `Nexo.Setup.Checks/Program.cs`, `Nexo.Setup/preview-public.pem`

- [x] Add a failing check that `/preview/latest.json` uses the bucket root for package URLs and that the normal manifest keeps its current base.
- [x] Implement package base URL resolution, the preview default manifest URL and a build property selecting its embedded public key.
- [x] Generate a new RSA 3072 preview key outside the repository; keep only the public key in source.
- [x] Run setup checks and publish the preview installer as a single Windows x64 executable.

### Task 3: Prepare the signed preview manifest

**Files:** `scripts/Build-PreviewRelease.ps1`, `docs/RECUPERACAO-2026-11-22.md`

- [x] Verify the public stable manifest and dependency package hashes, and reject overwriting an existing preview app package.
- [x] Publish Nexo 1.7.0 as a self-contained executable, package it, create the preview manifest and sign it with the separate key.
- [x] Verify the signature locally and inspect the exact package paths and hashes.

### Task 4: Publish and verify on this PC

**Files:** OCI objects under `releases/1.7.0/`, `preview/`, and `NexoSetup-Preview-1.1.1.exe`

- [x] Upload the app ZIP and installer, then `preview/latest.sig` and `preview/latest.json` last.
- [x] Run the preview installer's `--check` against the public bucket and validate object lengths and hashes.
- [x] Scan the executable with local Windows security tools and run a local installation test if the system permits it.
- [x] Record remaining validation and unsigned executable limitations in the recovery document.
