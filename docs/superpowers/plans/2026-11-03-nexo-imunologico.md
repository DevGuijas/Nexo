# Nexo Imunológico Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Add local, evidence-based monitoring, diagnosis, prevention and assisted recovery to the existing Nexo.

**Architecture:** Keep WPF, Ollama and the closed Windows executor. New focused components collect and persist structured observations; deterministic rules create findings; the model explains them; approved actions remain in the executor and are verified.

**Tech Stack:** C#/.NET 8 Windows, WPF, PowerShell/Windows APIs, Ollama Qwen3 8B, Microsoft.Data.Sqlite 8.0.33.

**Spec:** `docs/superpowers/specs/2026-11-03-nexo-imunologico-design.md`

## Global Constraints

- Preserve existing working flows and visual identity; no rewrite or external AI API.
- No unattended mutation; each Windows change requires specific authorization and UAC if elevated.
- Local diagnostics and history work offline; online driver sources remain optional.
- Unavailable sensors are marked unavailable; observations and hypotheses are not presented as confirmed facts.
- Normal tests never mutate real network, devices, services or drivers.
- Existing `data.json` conversations remain readable; no destructive migration.

## Review Focus

- Corrupt or inaccessible SQLite: existing chat remains usable and the UI reports monitoring storage failure rather than losing `data.json`; Task 1 test.
- Repeated high CPU or low disk with a missing sample: alerts require sufficient consecutive evidence and do not claim continuity across gaps; Task 2 test.
- Inaccessible or malformed event channel: collector keeps other channels and records a warning instead of reporting no errors; Task 3 test.
- Ollama unavailable: the deterministic report remains visible and no model-only success is claimed; Task 4 test.
- Elevated process not terminating: operation state becomes unknown, is recorded and cannot be counted as success; Task 5 test.

---

### Task 1: Structured models and local operational database

**Files:** Create `Nexo.Support/HealthModels.cs`, `Nexo.Support/OperationalStore.cs`, `Nexo.Checks/HealthDataChecks.cs`; modify `Nexo.Support/Nexo.Support.csproj`, `Nexo.Checks/Nexo.Checks.csproj`, `Nexo.Checks/Program.cs`.

**Interfaces:** Produce `HealthSnapshot`, `HealthFinding`, `SystemEvent`, `InterventionRecord`; `OperationalStore(string root)` with `SaveSnapshot`, `SaveFindings`, `SaveEvents`, `SaveIntervention`, `RecentSnapshots`, `RecentFindings`, `RecentEvents`, `Prune(TimeSpan)`.

- [x] Write `HealthDataChecks` with a temporary profile: round trip snapshots/findings/events/interventions; retention deletes only old operational rows; legacy `data.json` stays byte-for-byte intact; unavailable/corrupt database returns a typed error.
- [x] Run `dotnet run --project Nexo.Checks -c Release`; expected RED because the operational types/behavior do not exist.
- [x] Implement SQLite schema/versioning and parameterized operations, no raw chat text in metric rows.
- [x] Run `dotnet run --project Nexo.Checks -c Release`; expected full suite green.
- [x] Commit Task 1.

### Task 2: Continuous sampler and preventive rules

**Files:** Create `Nexo.Support/WindowsHealthSampler.cs`, `Nexo.Support/MonitoringEngine.cs`, `Nexo.Support/HealthRules.cs`, `Nexo.Checks/MonitoringChecks.cs`; modify Checks project/program.

**Interfaces:** Consume `HealthSnapshot`/`OperationalStore`; produce `IHealthSampler.Sample(CancellationToken)`, `MonitoringEngine.Start/Stop`, `HealthRules.Evaluate(IReadOnlyList<HealthSnapshot>, HealthThresholds)`.

- [x] Write tests for sustained CPU, memory growth, low disk, stopped automatic service, network failures, missing samples, duplicate suppression and cancellation. Use a fake sampler only at the Windows boundary.
- [x] Run Checks; expected RED for missing monitoring behavior.
- [x] Implement bounded Windows read-only collection and serialized periodic scheduling. Sensor readings not available from the OS remain unavailable.
- [x] Run Checks and a read-only live sample; expected green, bounded output and no mutation.
- [x] Commit Task 2.

### Task 3: Structured Windows events and deterministic report

**Files:** Create `Nexo.Support/WindowsEventCollector.cs`, `Nexo.Support/EventAnalyzer.cs`, `Nexo.Support/DiagnosticEngine.cs`, `Nexo.Checks/DiagnosticChecks.cs`; modify Checks project/program.

**Interfaces:** Consume snapshots/events/findings; produce `WindowsEventCollector.Collect(maxAge,maxPerLog,token)`, `EventAnalyzer.Analyze`, `DiagnosticEngine.BuildReport` with evidence, cause hypothesis, severity, impact, recommendation and bounded confidence.

- [x] Write tests for recurrence, mixed log sources, partial failures, event timestamp, unsupported sensor and no-evidence conclusions.
- [x] Run Checks; expected RED.
- [x] Implement bounded System/Application/Setup collection; retain metadata rather than full event messages; generate typed reports through deterministic rules.
- [x] Run Checks and optional read-only live event query; expected green.
- [x] Commit Task 3.

### Task 4: Integrate reports with local AI and offline fallback

**Files:** Modify `Nexo.Support/AgentService.cs`, `Nexo.Support/WindowsTools.cs`, `Nexo.Support/MainWindow.xaml.cs`; create `Nexo.Support/HealthReportPresentation.cs`, `Nexo.Checks/HealthAgentChecks.cs`.

**Interfaces:** Consume `DiagnosticReport`; expose `get_health_report` as read-only tool; model receives structured, size-limited evidence; deterministic report is available if Ollama fails.

- [x] Write tests for a report tool, malformed/missing report, read-only scope, prompt injection in event/provider names and unavailable Ollama fallback.
- [x] Run Checks; expected RED.
- [x] Implement controller/executor integration without a generic command pathway.
- [x] Run Checks and existing agent evaluations; expected green and no new mutation.
- [x] Commit Task 4.

### Task 5: Supervised recovery and driver store visibility

**Files:** Create `Nexo.Support/OperationSupervisor.cs`, `Nexo.Support/DriverStoreInventory.cs`, `Nexo.Checks/RecoveryChecks.cs`; modify `WindowsTools.cs`, `DriverWorkflow.cs` and Checks project/program.

**Interfaces:** Consume `OperationalStore`; produce bounded `OperationSupervisor.Wait` state `completed/failed/unknown`, action ledger with before/after/authorization; read-only driver package inventory tied to observed device. Existing action whitelist and confirmations remain.

- [x] Write tests for timed-out elevated operation, cancellation after launch, refusal, observed device mismatch, offline driver inventory and result not confirmed.
- [x] Run Checks; expected RED.
- [x] Integrate supervision and intervention records; expose installed driver package/INF details and report when recovery protection is unavailable. Do not run real driver installation in tests.
- [x] Run Checks plus network/driver regressions; expected green.
- [x] Commit Task 5.

### Task 6: Dashboard, scenario harness and TCC evidence

**Files:** Modify `MainWindow.xaml`, `MainWindow.xaml.cs`; create `Nexo.Checks/ScenarioChecks.cs`, `docs/AVALIACAO-SISTEMA-IMUNOLOGICO.md`; update `README.md` and Checks project/program.

**Interfaces:** Consume `OperationalStore`, monitoring and diagnostic report; produce live health dashboard, occurrences/events/recovery/driver sections, controls for interval/threshold/retention, seven controlled scenario fixtures and an experimental protocol.

- [x] Write scenario tests for the seven required faults with expected detection, false positive and validation status; create UI state tests around view-model/presentation data rather than source text.
- [x] Run Checks; expected RED.
- [x] Implement WPF navigation/views with the existing visual system and real data. Document lab instructions and mark unexecuted real results pending.
- [x] Run build, Checks, setup checks, simulated PowerShell regressions, offline local smoke test and WPF preview; expected green with limitations recorded.
- [x] Commit Task 6.

## Completion gate

Review the branch against this spec, fix important findings with failing tests first, rerun the full suite, and report what was verified in simulation versus on Windows hardware. Do not publish, install drivers or alter this computer's network as part of completion.
