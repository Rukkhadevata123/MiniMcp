# First-batch Windows checks baseline

Captured on Windows with .NET SDK 10.0.403 on 2026-11-03, using independently filtered group runs. Times are observed wall-clock within each checks executable; they exclude dotnet build/startup and should not be added to claim a full-suite runtime.

| Runtime group | Pass | Fail | Skip | Selected | Elapsed (s) |
|---|---:|---:|---:|---:|---:|
| cli.basic | 8 | 0 | 0 | 8 | 1.16 |
| cli.check-command | 15 | 0 | 0 | 15 | 0.75 |
| cli.process | 3 | 0 | 0 | 3 | 3.30 |
| cli.run | 8 | 0 | 0 | 8 | 21.23 |
| configuration.dependencies | 8 | 0 | 0 | 8 | 0.27 |
| configuration.external-programs | 6 | 0 | 0 | 6 | 0.15 |
| configuration.json | 8 | 0 | 0 | 8 | 0.15 |
| configuration.patch-limits | 8 | 0 | 0 | 8 | 0.25 |
| configuration.policy-save | 8 | 0 | 0 | 8 | 0.33 |
| configuration.serialization | 4 | 0 | 0 | 4 | 0.13 |
| configuration.settings-save | 8 | 0 | 0 | 8 | 0.26 |
| configuration.store | 8 | 0 | 0 | 8 | 0.19 |
| configuration.validator | 8 | 0 | 0 | 8 | 0.13 |
| configuration.values | 8 | 0 | 0 | 8 | 0.12 |
| host | 13 | 0 | 0 | 13 | 0.55 |
| host.calls | 4 | 0 | 0 | 4 | 11.70 |
| host.configuration | 9 | 0 | 0 | 9 | 0.23 |
| host.diagnostics | 16 | 0 | 0 | 16 | 0.65 |
| host.process-output | 6 | 0 | 0 | 6 | 0.35 |
| host.session | 7 | 0 | 0 | 7 | 0.49 |
| host.snapshot | 5 | 0 | 0 | 5 | 0.56 |
| host.tunnel | 19 | 0 | 0 | 19 | 18.67 |
| http.mcp | 13 | 0 | 0 | 13 | 5.02 |
| http.recorded-tools | 7 | 0 | 0 | 7 | 1.89 |
| http.security | 8 | 0 | 0 | 8 | 1.48 |
| infrastructure.runner | 4 | 0 | 0 | 4 | 0.12 |
| recovery.applying | 10 | 0 | 0 | 10 | 0.91 |
| recovery.completion | 11 | 0 | 1 | 12 | 1.03 |
| recovery.finalization | 11 | 0 | 0 | 11 | 1.84 |
| recovery.loading | 15 | 0 | 0 | 15 | 1.32 |
| recovery.local-entry | 6 | 0 | 0 | 6 | 1.47 |
| recovery.manifest-replacement | 15 | 0 | 0 | 15 | 1.60 |
| recovery.patch-commit | 13 | 0 | 0 | 13 | 1.21 |
| recovery.patch-concurrency | 6 | 0 | 0 | 6 | 0.81 |
| recovery.patch-fault | 17 | 0 | 0 | 17 | 11.85 |
| recovery.preparation | 9 | 0 | 0 | 9 | 0.82 |
| recovery.preview | 16 | 0 | 0 | 16 | 5.64 |
| recovery.preview-lifetime | 6 | 0 | 0 | 6 | 1.72 |
| recovery.restore | 8 | 0 | 0 | 8 | 2.28 |
| recovery.restore-fault | 12 | 0 | 0 | 12 | 18.73 |
| recovery.restore-lifetime | 7 | 0 | 0 | 7 | 14.05 |
| recovery.storage-guard | 8 | 0 | 0 | 8 | 0.79 |
| recovery.store | 10 | 0 | 0 | 10 | 1.24 |
| recovery.tool-execution | 12 | 0 | 0 | 12 | 1.81 |
| recovery.tool-lifetime | 3 | 0 | 0 | 3 | 11.20 |
| tools.authentication | 2 | 0 | 0 | 2 | 0.11 |
| tools.execution | 1 | 0 | 0 | 1 | 0.24 |
| tools.git-query | 19 | 0 | 0 | 19 | 42.87 |
| tools.query-output | 7 | 0 | 0 | 7 | 0.82 |
| tools.workspace-query | 15 | 0 | 0 | 15 | 23.78 |
| **Total across groups** | **458** | **0** | **1** | **459** | — |

Other executable runs on the same Windows environment:

| Executable | Pass | Fail | Skip | Selected | Approximate suite time |
|---|---:|---:|---:|---:|---:|
| MiniMcp.Checks | 94 | 0 | 4 | 98 | 5.35 s |
| MiniMcp.ProtocolChecks | 3 | 0 | 0 | 3 | 3.46 s |
| MiniMcp.DesktopChecks (default, no live UI) | 20 | 0 | 0 | 20 | 2.85 s |

The protocol checks previously printed 24 internal PASS lines (eight per mode). The runner now counts three **actual mode-level cases**, while retaining eight STEP diagnostics per mode. Counts represent check callbacks, **not** all loop iterations: the fault matrices still include their existing boundaries and state combinations. Explicit Linux-only scenarios are now counted as skipped on Windows rather than omitted or counted as passing.

Transient diagnostic history: a core fixture teardown failed once on a Windows .BAK.tmp sharing violation (93 pass, 1 cleanup failure), followed by a passing complete core run. A recorded-patch mixed-fault matrix once observed a nonempty newly created directory and then passed on rerun. Neither event was silently counted as a pass. The responsible locking process has not been verified; focused bounded teardown retries apply only to native Windows sharing/lock violations, not product-operation assertions.

Full default Runtime launch was interrupted by a tool execution failure after 316 PASS lines. The table above records successful separate execution of every registered Runtime group instead; it is not presented as a successful uninterrupted default run.
