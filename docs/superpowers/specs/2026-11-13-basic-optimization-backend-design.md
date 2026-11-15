# Basic optimization backend design — issue 113

Implements the orchestrator-delegated safe backend only. Entry point is an explicit UI action; no assistant, model, or conversation participates. UI files are outside this branch.

## Approach

Use a deterministic service with Windows handle-based temporary-file deletion and an injected privileged native-disk executor. A recursive shell cleanup was rejected because pathname validation alone cannot prevent junction replacement races; assistant-driven cleanup was rejected because this is a direct action. Native Optimize-Volume chooses the appropriate operation for its media without forced defragmentation or trim flags. Virtual disk models and virtual bus types are skipped: a VirtualBox VDI grew by roughly 24 GB during the first real lab run and exhausted the host disk before the operation returned.

## Safety and limits

Production roots come from Windows known folders: LocalApplicationData/Temp and Windows/Temp, never arbitrary environment TEMP or user-selected roots. Every ancestor directory is opened without write/delete sharing and reparse points are rejected. Descendant directories stay pinned during traversal. A file is opened without write/delete sharing, checked through its handle for age, type, attributes and hardlinks, then deleted through the same handle. Both creation and last-write time must be older than seven days. Directories are preserved, locked/inaccessible entries skipped. Protected data/cache directory names are excluded. No Downloads, Documents, browser cache, recycle bin or Windows Update cleanup is performed.

A static gate prevents simultaneous runs in this process. Cancellation stops between files and before disk execution; deleted files cannot be restored. Native execution must be awaited by the injected executor. Canceling or losing the native executor has unknown disk outcome, with no claim of rollback.

Audit persistence is synchronous and required; failure propagates and prevents further mutations. The callback is called from a worker thread during cleanup and must be thread safe. Intent is persisted before deletion and outcome afterward, allowing interrupted intents to remain explicit. Disk audit stores the aggregate observed outcome. RemovedBytes represents logical file lengths, not a measured change in free space.

The production constructor with no disk executor reports disk skipped. The caller supplies its existing elevation/UAC boundary when disk maintenance is enabled. No real host cleanup or disk optimization is part of development verification.

## Validation

Fake temporary directory trees cover age, locked files, protected names, root/nested junctions, audit failure and cancellation. A separate PowerShell process replaces every storage command with fixture functions to verify native selection and failure reporting without invoking native maintenance. Run focused checks, complete checks and WPF Release build.

Microsoft reference for default media-specific operation: https://learn.microsoft.com/powershell/module/storage/optimize-volume
## Per-stage evidence

`BasicOptimizationResult.Stages` and `OptimizationAudit.Stage` expose the same typed Cleanup/Disk summaries. Each has an optional observed start time, monotonic elapsed duration, completed/skipped item counts, logical file bytes removed, error count and local error details. Cleanup items are filesystem entries (including inaccessible roots); errors are counted separately from expected skips. Detailed cleanup errors are bounded to the first 100 while ErrorCount remains the full count. The cleanup summary is persisted before native disk work begins. Cancellation preserves completed cleanup evidence and marks unconfirmed native work Unknown.

Disk logical bytes are always null: no free-space delta is measured. Unknown/denied native execution has null volume counts. Explicit UAC refusal and access-denied executor exceptions produce Denied and overall Partial. The native script records skipped volumes and errors as well as completed volumes.

Real cleanup, UAC and native disk validation must be performed only in a disposable Windows VM. Host verification uses fake directories and shadowed PowerShell storage commands; it does not establish real disk optimization success.
