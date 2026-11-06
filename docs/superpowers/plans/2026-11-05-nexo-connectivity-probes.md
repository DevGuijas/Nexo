# Nexo Connectivity Probes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record gateway, DNS and external reachability separately from local interface state, with opt-in checks and temporal alerts.

**Architecture:** Keep `WindowsHealthSampler` as the collection entry point and put the bounded, injectable network checks in `ConnectivityProbe.cs`. Store the observations in `HealthSnapshot`; `HealthRules` consumes their explicit states. Settings persist in `health-settings.json` and are exposed on the existing WPF settings page.

**Tech Stack:** C#/.NET 8, WPF, `System.Net.NetworkInformation`, `System.Net.Dns`, `HttpClient`, the existing `Nexo.Checks` console suite.

**Spec:** `docs/superpowers/specs/2026-11-05-nexo-connectivity-probes-design.md`

## Global Constraints

- No active probe by default; no network or hardware mutation.
- Independent bounded checks; no automatic HTTP redirect or TLS bypass.
- Results describe the configured target and keep uncertainty explicit.
- Keep existing `Connectivity=local|none|unknown` behavior and persisted older snapshots readable.

## Review Focus

- Offline with all checks disabled: zero outbound probe calls and all three states `disabled` (Task 2).
- Active interface without a gateway: gateway `unavailable`, not a network-failure alert (Task 2).
- Captive portal or proxy response: external `unexpected`, never `ok` (Task 2).
- Cancellation during a probe: collection stops promptly without converting cancellation into a failure (Task 2).
- Isolated network or one failed request: no persistent external alert (Task 3).

---

### Task 1: Settings and observation model

**Files:** Modify `Nexo.Support/HealthSettings.cs`, `Nexo.Support/HealthModels.cs`, `Nexo.Checks/ScenarioChecks.cs`.

**Interfaces:** `HealthSettings` adds `CheckGateway`, `CheckDns`, `CheckExternal`, `DnsProbeHost`, `HttpProbeUrl`, `HttpExpectedText`; `HealthSnapshot` adds `GatewayProbe`, `DnsProbe`, `ExternalProbe` of type `NetworkProbeReading` with `State`, `Target`, `Detail`.

- [x] Add failing tests: defaults disable all three checks; valid settings round-trip; invalid URL, DNS name and empty expected text are rejected; older settings load with probes disabled.
- [x] Run `dotnet run --project Nexo.Checks -c Release` and confirm failure from missing model/settings behavior.
- [x] Add the model and validation with defaults documented in the spec.
- [x] Run the suite to green and commit `feat(monitoramento): configura sondagens de rede opcionais`.

### Task 2: Bounded read-only probes

**Files:** Create `Nexo.Support/ConnectivityProbe.cs`, `Nexo.Checks/ConnectivityProbeChecks.cs`; modify `Nexo.Checks/Nexo.Checks.csproj`, `Nexo.Checks/Program.cs`, `Nexo.Support/WindowsHealthSampler.cs`.

**Interfaces:** `ConnectivityProbe.Run(HealthSettings settings, bool localActive, IReadOnlyList<IPAddress> gateways, CancellationToken token)` returns three `NetworkProbeReading` values; injectable ping, DNS and HTTP delegates allow deterministic checks without live requests. `WindowsHealthSampler(HealthSettings? settings = null, ConnectivityProbe? probe = null)` keeps existing call sites valid.

- [x] Add failing tests for disabled checks, no gateway, ICMP silence, DNS failure, matching HTTP body, redirect/wrong body, cancellation and bounded requests.
- [x] Run the suite and observe the failures.
- [x] Implement the smallest probe and sampler integration; cap work per sample and preserve local collection if a probe fails.
- [x] Run the suite to green and commit `feat(monitoramento): coleta gateway DNS e destino externo`.

### Task 3: Temporal rule, report and UI

**Files:** Modify `Nexo.Support/HealthRules.cs`, `Nexo.Support/HealthReportPresentation.cs`, `Nexo.Support/DiagnosticEngine.cs`, `Nexo.Support/MainWindow.xaml.cs`, `Nexo.Checks/MonitoringChecks.cs`.

**Interfaces:** `HealthRules.Evaluate` emits `network-external-unconfirmed` only from three consecutive failing external checks while local connectivity is active. Settings checkboxes and fields feed `HealthSettings`; panel and report label each probe independently.

- [x] Add failing tests for a lone failure, three continuous failures, a successful recovery, network isolation, an unavailable target and report text.
- [x] Run the suite and observe the failures.
- [x] Implement rule and presentation, then wire the settings and sampler.
- [x] Run the suite and WPF preview to green; commit `feat(interface): mostra sondagens e alertas de conectividade`.

### Task 4: Evidence and delivery

**Files:** Modify `README.md`, `docs/AVALIACAO-MONITORAMENTO-E-RECUPERACAO.md`, this plan.

- [x] Document opt-in behavior, target-specific interpretation and remaining real-network limits.
- [ ] Run builds, both .NET suites, simulated PowerShell network suites, `scripts/Check-UiPreview.ps1` and `git diff --check`.
- [ ] Request a read-only branch review; address concrete findings with red-green tests.
- [ ] Push the feature branch, open a PR to `development`, wait for Actions and record evidence on #17.
- [ ] Merge only after all acceptance checks pass; close #17 with the checklist completed.
