# Standalone checks

Each executable uses the same small runner, without a test-framework package. `tests/CheckInfrastructure` is linked into all four projects: `CheckRunner` (selection, tiers, result reporting), `CheckAssertions` (`Assert` and `Reject<T>`, imported everywhere via `global using static`; `Assert` reports the asserted expression text on failure) and `TemporaryTestDirectory` (`Reserve(tag)` / `Delete(path)` for every fixture's `%TEMP%\minimcp-<tag>-<guid>` directory).

~~~powershell
dotnet run --project tests/MiniMcp.Checks -- --list
dotnet run --project tests/MiniMcp.RuntimeChecks -- --list
dotnet run --project tests/MiniMcp.RuntimeChecks -- --group configuration.settings-save
dotnet run --project tests/MiniMcp.RuntimeChecks -- --id runtime/configuration.settings-save/invalid-and-oversized-settings-are-rejected-before-creating-storage-or-changing-existing-files
dotnet run --project tests/MiniMcp.RuntimeChecks -- --filter "policy saves"
dotnet run --project tests/MiniMcp.ProtocolChecks
dotnet run --project tests/MiniMcp.DesktopChecks
~~~

--list, --group NAME, --id SUITE/GROUP/SLUG, --filter TEXT, and --tier fast|integration|slow|ui work in all four check runners. Selection flags can be combined; --id and --group are exact (case-insensitive), --filter is a case-insensitive substring of the name or ID, and --tier selects one explicitly assigned category. A selection that matches no case exits with code 1; listing does not execute selected check actions. IDs are deterministic within the current suite; renaming a case changes its ID. Duplicate IDs fail registration.

Results distinguish **PASS**, **FAIL**, **SKIP**, and **FAIL CLEANUP**, with per-case and suite elapsed times. An explicit skip has a reason and is never counted as a pass. A fixture can report a cleanup failure separately from its original assertion failure, including both exception stacks. Fault-matrix failures include the applicable stage/state/index dimensions where annotated. A selected check can contain several internal matrix iterations, so the number of checks is **not** the number of independent subscenarios.

For normal development, run the applicable logic/configuration groups and groups affected by a change. Before merging, run the complete default executables and the relevant integration/slow checks; on Windows run DesktopChecks as well. Real window interaction is separate: dotnet run --project tests/MiniMcp.DesktopChecks -- --ui. Screenshot generation is a development aid, not a test pass. MiniMcp.ProtocolChecks treats each of ReadOnly, ProjectEdit, and FullAccess as one complete protocol scenario; its STEP lines are internal progress, not extra passes.

Dependencies are resolved where they are used: pure core patch/format checks need no Bash; actual shell, Git, ripgrep, processes, and UI checks still need their platform-specific dependencies. Missing required dependencies fail the relevant selected check with the original diagnostic. Platform-inapplicable tests and insufficient symbolic-link permissions explicitly skip where applicable.

## Windows cleanup diagnostics

The patch fixtures check for no staging/backup leftovers as part of the **product assertion**. Separately, their teardown deletes only the fixture's temporary directory. The runner reports teardown errors by path, never drops a primary failure, and never ignores a persistent deletion failure.

An intermittent Windows sharing violation on .minimcp-*.bak.tmp was reproduced during test-directory teardown. Once observed, the file disappeared on later inspection; the locking process has **not** been identified. The reported names are uppercased copies with an extra `.tmp` suffix (for example `.MINIMCP-<GUID>.BAK.tmp`, `POLICY.JSON~RF….TMP.tmp`), which the product never creates, so an external file watcher is shadowing freshly written files. `TemporaryTestDirectory.Delete`, used by every fixture, therefore allows at most three short retries **only** for native Windows sharing/lock violation codes; every other error fails immediately, and an unresolved share violation still fails. CLEANUP RETRY logs disclose each retry. This retry does not change the product's patch/rollback behavior.

Do not treat an injected rollback failure, an unexpected file, an unchanged-source mismatch, or a nonempty directory as a successful teardown. Preserve the path, assertion, and exception details to investigate when one recurs.

See [first-batch baseline](first-batch-baseline.md) for the initial Windows grouped timing results. Group elapsed times reflect one machine and one run; they are not stable benchmarks.

The [second-batch coverage map](second-batch-coverage.md) identifies each cross-layer reduction, its retained assertions and the responsible layer.

## Test tiers

~~~powershell
dotnet run --project tests/MiniMcp.Checks -- --tier fast
dotnet run --project tests/MiniMcp.ProtocolChecks -- --tier fast
dotnet run --project tests/MiniMcp.RuntimeChecks -- --tier fast
dotnet run --project tests/MiniMcp.RuntimeChecks -- --tier integration
dotnet run --project tests/MiniMcp.RuntimeChecks -- --tier slow
dotnet run --project tests/MiniMcp.RuntimeChecks -- --list --tier slow
dotnet run --project tests/MiniMcp.RuntimeChecks -- --group recovery.restore-fault --tier integration
dotnet run --project tests/MiniMcp.DesktopChecks -- --tier ui
~~~

Each check has exactly one tier; a tier is a filter, not a flag that changes assertions or replaces real test infrastructure. **fast** covers core parsing, risk rules, configuration values, quick recovery validation and in-memory SDK protocol. The SDK protocol still uses real tools (including Bash on applicable modes), so fast does not imply zero filesystem/process dependencies. **integration** covers filesystem, real Git/rg/Bash, HTTP and representative recovery/runtime/CLI behavior. **slow** isolates the full recorded fault matrices and actual default stop/start budgets; currently it contains RuntimeChecks tests, so requesting slow in another executable with no such cases correctly returns a no-match error. **ui/acceptance** is the real Windows desktop UI, invoked via DesktopChecks --tier ui (equivalent to --ui); default DesktopChecks remains non-window integration. Screenshot generation remains separate, not automatic proof of UI correctness.

For Windows daily work run fast plus affected integration groups; before merge run all integration groups and slow on the affected platform; for release run the full suite and manual UI acceptance where supported. Using --list --tier CATEGORY exposes ownership, while --list without a filter inventories all check IDs. In-memory ProtocolChecks is fast despite its relatively small number of expensive SDK operations; timings are observations rather than stable guarantees.

See [third-batch matrix and results](third-batch-results.md) for the explicit rollback/finalization ownership, before/after subscenario counts, timings and outstanding environment limitations.
