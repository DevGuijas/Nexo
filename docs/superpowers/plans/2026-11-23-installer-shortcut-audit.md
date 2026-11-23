# Installer Shortcut and Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a desktop shortcut, a local structured installation record and an uninstaller inside the Nexo application folder.

**Architecture:** A focused audit class writes one JSON event per line and a GeoIP parser limits retained fields. `Installer.RunAsync` records every material stage and creates the desktop shortcut after activating Nexo. The WPF page discloses the optional HTTPS lookup and log path.

**Tech Stack:** .NET 8, WPF, Windows Shell COM, PowerShell 7, Oracle Object Storage.

**Spec:** `docs/superpowers/specs/2026-11-23-installer-shortcut-audit.md`

## Global Constraints

- No VM download or execution. Test on this PC.
- Do not write raw IP, coordinates, postal code or private signing keys to logs.
- Keep the stable installer and both existing signed manifests unchanged.
- A GeoIP failure must not block installation; an audit-file creation failure must block before mutation.

## Review Focus

- A redirected Desktop folder still receives the shortcut.
- Re-running an installation produces a distinct log with a stable device identifier.
- A failed download or signature check produces a terminal failure event.
- An invalid GeoIP response does not leak raw IP or abort installation.
- A cache hit is distinguishable from a fresh download.
- The uninstaller removes only shortcuts targeting this Nexo installation and preserves Ollama, model and logs.

---

### Task 1: Shortcut and audit units

**Files:** `Nexo.Setup/Program.cs`, `Nexo.Setup/InstallAudit.cs`, `Nexo.Setup.Checks/Program.cs`

- [x] Add failing checks for a real `.lnk` in a temporary Desktop directory, JSONL schema, distinct sessions, stable device ID and filtered GeoIP parsing.
- [x] Run setup checks and observe the expected failure.
- [x] Implement the shortcut helper, audit writer and GeoIP parser; rerun setup checks.

### Task 2: Integrate the installation flow

**Files:** `Nexo.Setup/Program.cs`, `Nexo.Setup/SetupWindow.cs`, `Nexo.Setup.Checks/Program.cs`

- [x] Add a failing check for phase logging and terminal status on both success and failure.
- [x] Instrument manifest, package, Ollama, model, activation and shortcut steps; create the Desktop shortcut.
- [x] Add the GeoIP choice and log path to the WPF review page; verify setup checks.

### Task 3: Add the application uninstaller

**Files:** `Nexo.Setup/Uninstall.cs`, `Nexo.Setup/Program.cs`, `Nexo.Setup.Checks/Program.cs`

- [x] Add failing checks for installing `NexoUninstall.exe` beside `Nexo.exe` and for removing a temporary fake application, cache and owned shortcuts while preserving logs and shared Ollama files.
- [x] Run setup checks and observe the expected failure.
- [x] Implement a confirmation window, a temporary cleanup worker, safe target checks, removal and audit events; rerun setup checks.

### Task 4: Publish and document

**Files:** `Nexo.Setup/Nexo.Setup.csproj`, `README.md`, `docs/INSTALADOR-NUVEM.md`

- [x] Bump the preview installer to 1.2.0 and publish a single-file Windows x64 binary without replacing prior objects.
- [x] Run the installer on this PC and inspect the `.lnk`, JSONL schema, GeoIP status, `NexoUninstall.exe`, and Nexo/Ollama/model readiness. Test uninstallation against a temporary fake application only.
- [x] Verify SHA-256 locally and from Oracle, `--check`, Defender result and existing suites; document exact path and limits.
