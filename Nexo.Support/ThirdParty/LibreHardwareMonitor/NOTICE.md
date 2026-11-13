# Embedded CPU temperature

Nexo uses the official LibreHardwareMonitorLib NuGet package 0.9.6 inside its own process. It enables CPU hardware only and opens, updates, and closes the computer for each measurement, including error/cancellation paths. No standalone Libre Hardware Monitor program or WMI publisher is needed.

CPU register access requires the separately installed PawnIO driver on supported Windows hardware. Nexo does not install or launch its installer. Missing PawnIO gives `no_source` and a null temperature. A present driver does not guarantee access: permissions, platform support, or security settings may leave `no_sensor`/`error`. No estimated temperature is substituted. This change does not elevate Nexo automatically.

Temperature and storage collection remain independent. Temperature has a 10-second managed wait limit. A native call cannot safely be terminated in-process; if it exceeds the wait limit or the caller cancels, cleanup happens when that call returns. A process-wide gate prevents another hardware session from starting while the first still runs. The storage PowerShell process keeps its separate 15-second limit.

The official library is distributed unmodified under MPL-2.0. Its source is available at https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701 (the commit identified by the NuGet package). License and upstream third-party notices accompany Nexo in `ThirdParty/LibreHardwareMonitor`. PawnIO bytecode license is also included; no PawnIO driver installer is bundled by Nexo. Keep these notices when redistributing binaries and retain the source availability link. Transitive package licensing remains applicable.
