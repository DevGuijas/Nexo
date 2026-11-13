# Issue 113 backend implementation plan

Scope delegated by orchestrator: deterministic basic optimization without assistant or UI changes. Isolated from updated origin/development. Never execute host cleanup during development.

1. Add service contract and fake-directory checks. Verify an old fixture is initially retained (red).
2. Implement cleanup using Windows file handles: pin directory ancestors against replacement, reject every reparse point, retain recent files, skip protected paths and locked files. Delete files only, never recursively delete directories.
3. Add fixed native disk script using healthy fixed local volumes and Optimize-Volume with default media-specific operation. Inject existing elevated execution boundary; never invoke it in checks. Report unsupported/failed disks separately.
4. Test cancellation, audit failures, protected paths, locks, junctions and concurrent starts using sandbox fixtures; run full checks and WPF build. Commit locally without push.

API: BasicOptimizationService.RunAsync(progress, cancellation) returns completed/canceled/partial status, removed bytes/files, skipped files and disk outcome. Audit callback receives start, per-file deletion and terminal entries and must be persisted by caller. No assistant dependency. Default retention: seven days. Disk cancellation is cooperative; native operation may outlive process interruption and must never be reported complete without evidence.
