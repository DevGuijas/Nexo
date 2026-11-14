# Embedded CPU temperature design

The requested backend removes the need to run Libre Hardware Monitor as a separate application. Nexo will use the official LibreHardwareMonitorLib 0.9.6 package with CPU access enabled, preserving package/die selection and rejection of missing or implausible readings.

Approaches considered: retaining WMI still requires another application, contrary to the request; ACPI thermal zones are not a reliable CPU package measurement; the official embedded library provides direct CPU sensor access and is the requested preferred approach. No UI changes or driver installation belong to this backend change.

Each read owns a Computer Open/Update/Close lifecycle. Failures and cancellation attempt Close when native code returns; LHM 0.9.6 does not guarantee cleanup after partial Open failure. A process-wide circuit breaker prevents another Open after initialization fails and instructs restarting Nexo for another attempt. A global gate prevents overlapping sessions after a managed 10-second timeout. Storage remains an independent 15-second PowerShell query. Missing PawnIO is reported explicitly with null Celsius. Existing package/die ordering, timestamping, storage independence, and database formats are preserved.

Redistribution includes the unmodified upstream MPL-2.0 license, third-party notices, PawnIO bytecode license, and an exact source-commit link. Driver availability and hardware permissions remain deployment requirements; no numerical reading can be promised on unsupported or restricted hardware.
