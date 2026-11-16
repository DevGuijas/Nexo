# Office LTSC backend implementation plan

> Execute inline with Superpowers executing-plans; scoped backend handoff from issue #112.

Goal: expose a closed Office LTSC Professional Plus 2024 deployment workflow for future UI.
Architecture: service coordinates injectable platform; Windows adapter downloads fixed official ODT, verifies Microsoft Authenticode before extraction and setup, checks registry/process conflicts. Cancellation after configure starts is Unknown and never kills Office.
Tech: .NET 8, offline console checks.
Constraints: 64-bit pt-BR ProPlus2024Volume PerpetualVL2024; explicit install/license acknowledgement; no keys, activation, automatic removals, process termination or reboot. No host installation in tests.

- [x] Write offline checks for gates, XML, signatures, cancellation and exit codes; observe failure.
- [x] Implement backend and Windows adapter; run checks.
- [x] Run full suites/build and diff check; commit locally.

Decision: conservatively block all detected Office products (including Visio/Project) rather than uninstall/migrate. ODT version is pinned to official Download Center release 16.0.20430.20146 (2026-10-19); update URL deliberately when needed.
Sources: https://learn.microsoft.com/en-us/office/ltsc/2024/deploy and https://www.microsoft.com/en-us/download/details.aspx?id=49117.

Validation: Office 16 checks; Nexo.Checks Release 2605 checks; Nexo.Setup.Checks Release 36 checks; Support Release build 0 warnings/errors. No Office installed on host. Initial missing implementation made test project fail compilation; extractor cancellation additionally verified with a behavioral failing assertion before correction.
