# Network Decision Authentication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `keep` valid only when the Nexo confirmation reaches the elevated network executor through a mutually PID-verified local pipe.

**Architecture:** The WPF process hosts a one-shot named pipe after the confirmation callback, verifies the connected elevated process PID, and sends one decision. The PowerShell 5.1 executor verifies the server PID and treats all other outcomes as rollback. The user-writable decision file may request rollback but cannot authorize keep.

**Tech Stack:** .NET 8 WPF/C#, Windows PowerShell 5.1, Win32 named-pipe PID APIs.

**Spec:** `docs/superpowers/specs/2026-11-13-nexo-network-decision-auth-design.md`

## Global Constraints

- No network, service or driver mutation on the work PC for #8.
- Preserve the 45-second UI and 60-second executor confirmation limits.
- Keep `before.json`, `applied.json` and results under the protected `%ProgramData%` store.
- Every uncertain outcome is reported as uncertain; `keep` must never be inferred from a user-writable file.
- Issue #11 remains open until Windows VM validation.

## Review Focus

- A forged `decision.txt` containing `keep` before application must not suppress the UI prompt or authorize keep (Task 2).
- Another process racing to create or connect to the pipe must not receive or send the decision (Task 1 and Task 2).
- A missing client/server PID must fail closed rather than treat the connection as authenticated (Task 1).
- Cancellation during DHCP acquisition must still request rollback promptly (Task 2).
- App termination after network application must end with executor rollback by deadline (Task 2).

---

### Task 1: Mutually verified decision channel

**Files:**
- Create: `Nexo.Support/NetworkDecisionChannel.cs`
- Modify: `Nexo.Checks/Nexo.Checks.csproj`
- Test: `Nexo.Checks/NetworkDecisionChecks.cs`
- Modify: `Nexo.Checks/Program.cs`

**Interfaces:**
- Produces: `NetworkDecisionChannel.PipeName(string operationId)` and `SendAsync(string pipeName, int expectedClientPid, string decision, TimeSpan timeout, CancellationToken token)`.
- Produces: `NetworkDecisionChannel.ServerProcessId(SafePipeHandle)` for the elevated PowerShell side to mirror through Win32.

- [ ] Write failing checks for genuine client PID, foreign client PID, absent PID and one-shot delivery.
- [ ] Run `dotnet run --project Nexo.Checks -c Release`; confirm the new checks fail for the missing channel.
- [ ] Implement the named-pipe server and kernel PID verification; bound every wait and close after one message.
- [ ] Rerun `Nexo.Checks` and confirm the new checks pass.
- [ ] Commit as `fix(rede): autentica o canal de decisão elevado`.

### Task 2: Remove file-based keep authority

**Files:**
- Modify: `Nexo.Support/NetworkChange.cs`
- Modify: `Nexo.Support/NetworkTransaction.ps1`
- Modify: `Nexo.Checks/NetworkRecoveryChecks.cs`
- Modify: `Nexo.Checks/NetworkScriptChecks.ps1`
- Modify: `README.md`

**Interfaces:**
- Consumes: `NetworkDecisionChannel.SendAsync`, the operation-specific pipe name and both process PIDs.
- Produces: a decision after a trusted `applied.json`, with rollback on missing or invalid peer.

- [ ] Add failing tests for early forged `keep`, replayed/foreign pipe connections, legitimate keep, explicit restore, cancellation and timeout.
- [ ] Run focused C# and script checks; confirm the security regression fails on current code.
- [ ] Pass the pipe name/app PID into the elevated script, verify the pipe server PID in PowerShell 5.1, and remove `decision.txt` as keep authority.
- [ ] Update C# to start the pipe only after the confirmation callback and verify the elevated client PID; keep rollback signaling and uncertain-state reporting.
- [ ] Rerun focused checks, full `Nexo.Checks`, `Nexo.Setup.Checks`, `NetworkScriptChecks.ps1`, app/setup builds and `git diff --check`.
- [ ] Commit as `fix(rede): recusa keep forjado em arquivo local`.

### Task 3: Integration and review

**Files:**
- Modify: `CHANGELOG.md`
- Review: all files above.

- [ ] Review the whole diff for privilege boundaries, process lifetime, pipe races and altered user messages.
- [ ] Request an independent code review and resolve all critical or important findings.
- [ ] Run the full verification again on the final commit.
- [ ] Open a PR to `development`, wait for CI, merge only if checks pass and no important finding remains.
- [ ] Leave #11 open with the exact VM acceptance steps and link the PR.
