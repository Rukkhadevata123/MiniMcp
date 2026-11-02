# Third batch: recovery matrix ownership, real waits, and tiers

Windows verification on 2026-11-03; .NET 10.0.403. Test-only changes; no production timeout, rollback logic, or authorization policy was changed.

## Fault matrix ownership

| Layer / test | Before (actual inner scenarios) | After | Responsibility |
| --- | ---: | ---: | --- |
| RecordedPatchFaultChecks: mixed patch failure at staging + 5 installation positions (before/after each) | 11 | 11 | **Primary complete engine/recorded rollback matrix**; raw contents, permissions, changed directories, record state |
| RecordedPatchFaultChecks: 3 terminal outcomes x 5 manifest replacement stages | 15 | 15 | **Primary recorded final-metadata matrix**: pre-replace retryable vs post-replace uncertain/invalidated, with bytes and error identity |
| RecordedPatchFaultChecks: backup preparation stages | 5 | 5 | All preparation callbacks, before-image and storage integrity |
| RecoveryRestoreFaultChecks: backup preparation failure stages | 5 | 2 | Restore's distinct before-image and old record protection at BeforeManifest and BeforeReturn; full stages remain in recorded patch tests |
| RecoveryRestoreFaultChecks: reverse-plan installation failures (staging + 6 indices before/after) | 13 | 5 | Restoration-specific reversal, original source unchanged, new rollback record, first/middle/last installation; full engine matrix owned above |
| RecoveryRestoreFaultChecks: terminal outcomes x manifest replacement stages | 15 | 6 | Restore-only replay ban, separate before-image, original source history and retry at BeforeReplace/Replaced for all 3 terminal states |
| RecoveryApplyingStateChecks and RecoveryFinalizationChecks | Unchanged | Unchanged | Exact Prepared/Applying state transitions, 3 pre-rename and 2 post-rename finalization checkpoints, late cancel, capacity, foreign-file conditions |

The three reduced restore matrices execute **33 -> 13** distinct inner scenarios (20 fewer). The complete recorded engine/manifest matrices are unchanged. The restore reverse-plan representative boundaries are Staged(-1), BeforeInstall(0), Installed(0), Installed(3), Installed(5); each checks its independent restore record and source state. The final save retains Committed, RolledBack, Incomplete on both sides of rename. The restore cancellation matrix (7 distinct phases), source-record tamper matrix (5 phases), incomplete rollback/cleanup, quota, external edit, token replay and double restore remain intact.

The full replacement stages are BeforeTemporaryFile, TemporaryWritten, BeforeReplace, Replaced and BeforeReturn. The distinction between failed preparation, a persistent but unverified Applying state, committed installation with failing final save, and incomplete rollback is intentionally maintained.

## Default deadlines versus caller cancellation

| Group | Before group time | After group time | Change |
| --- | ---: | ---: | --- |
| recovery.restore-fault | 10.51 s | 6.58 s | Three restore submatrices reduced; case count remains 12 |
| recovery.tool-lifetime | 11.25 s | 0.73 s | After finalization checkpoint, wait for actual Stopping, then cancel caller; still verify owned unfinished task and restart |
| recovery.restore-lifetime | ~14.05 s (first-batch baseline) | 2.09 s | Same bounded caller-cancellation mechanism with independent restore evidence and one-use plans |
| host.tunnel | ~18.67 s (first-batch baseline) | 6.53 s | Cancel after tunnel release checkpoint; still verify real descendant termination and stop/restart ownership |

The true default deadline is **still tested** in runtime/host.calls/shutdown-deadline-bounds-blocking-host-stop-and-retries-share-its-unfinished-task (10.33 s) and runtime/cli.run/cli-run-one-shutdown-budget-bounds-a-blocking-host-stop-without-an-implicit-disposal-retry (9.65 s). CLI startup cancellation also retains its actual startup budget (9.56 s). Other TimeSpan values are mostly **upper bounds on waiting for signals**, not fixed delays; replacing them does not improve a successful run's wall time and can cause flakes. Real subprocess cancellation, port release, queued work ownership and final record persistence tests remain.

## Runnable test tiers

| Runtime tier | Selected | Passed | Failed | Skipped | Observed |
| --- | ---: | ---: | ---: | ---: | --- |
| fast | 33 | 33 | 0 | 0 | 0.27 s |
| integration | 418 | 417 | 0 | 1 | 45 independent group runs; not a single aggregate runtime |
| slow | 7 | 7 | 0 | 0 | 34.58 s |
| **Total** | **458** | **457** | **0** | **1** | All categories accounted for |

Core fast: 54 passed in 0.86 s. In-memory Protocol fast: 3 passed in 2.61 s. Each test is assigned to exactly one tier, with stable IDs preserved and --list showing the category. Integration includes Git/rg and process tests, which are materially slower than fast despite no default deadline. DesktopChecks --tier ui explicitly invokes the actual Windows window smoke check; it was **not** run in this environment. Linux-only FIFO is the one Runtime skip on Windows.

Final non-UI default runs: MiniMcp.Checks **101 passed / 4 skipped / 0 failed**, MiniMcp.ProtocolChecks **3 passed / 0 failed**, MiniMcp.DesktopChecks **20 passed / 0 failed**. The Core/Protocol/Runtime projects built with zero warnings and zero errors. The normal DesktopChecks build could not overwrite the Desktop application's loaded MiniMcp.Runtime.dll (the live MiniMcp.Desktop process held it); without stopping that running app, a DesktopChecks build with a separate --no-restore -p:OutputPath completed with **0 warnings / 0 errors**. The default DesktopChecks check execution also passed. A standard in-place Desktop build remains environment-blocked while the running app holds the file; this is not a reported compiler error.

The integration groups were executed as separate processes, so their independent successes do not certify one uninterrupted complete runtime launch. One RecoveryCompletionChecks fixture teardown initially failed with a Windows sharing violation on .BAK.tmp; that fixture now uses the already existing narrowly bounded test-only cleanup with separate failure reporting, and its group subsequently passed (11 pass/1 skip). The locking process has not been identified. No production retry was introduced and a persistent lock remains a failure.

Practical commands and group selection are documented in tests/README.md. On Windows, run fast during development, relevant integration groups for changes, and isolated slow cases before merging. For release readiness also run complete suites on each platform and manual desktop acceptance.
