# Embedded temperature implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Collect CPU package/die temperature inside Nexo without the standalone Libre Hardware Monitor application.
**Architecture:** Official LibreHardwareMonitorLib 0.9.6, CPU only; each measurement opens and closes a computer session. Storage remains an independent PowerShell query. No driver installation or UI changes.
**Tech Stack:** C#/.NET 8 Windows, LibreHardwareMonitorLib 0.9.6.
**Spec:** docs/superpowers/specs/2026-11-13-embedded-temperature-design.md

## Global constraints

No MainWindow or UI edits. No host driver installation. No fabricated readings. Local commit only.

## Review focus

Missing driver must return null Celsius. Failed Open/Update must close resources. Cancellation must propagate. Timeout must not accumulate blocked native sessions. Disk failure must preserve valid temperature.

## Task 1: Embedded collection and independent sources

Files: Nexo.Support/EmbeddedCpuTemperature.cs; HardwareSensors.cs; Nexo.Support.csproj; Nexo.Checks/EmbeddedTemperatureChecks.cs; HardwareSensorChecks.cs; Program.cs; Nexo.Checks.csproj.

- [x] Add failing check for embedded session lifecycle and observe failure.
- [x] Implement official CPU-only adapter, missing driver result, cancellation, timeout and serialization.
- [x] Switch production collection to the embedded adapter and inject legacy WMI fixtures explicitly in regression checks.
- [x] Add checks for failure, null readings, native timeout isolation and recovery.
- [x] Run full suite (2,609 checks), focused final checks (7), Release build and publish; commit locally.

## Task 2: Redistribution

Files: Nexo.Support/ThirdParty/LibreHardwareMonitor/*; docs/embedded-temperature.md.

- [x] Include upstream licenses/notices and exact source link in build and publish outputs.
- [x] Document PawnIO/access requirements and native timeout limits.
- [x] Inspect publish output for library and notices, with no driver installer.

