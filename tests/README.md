# Standalone checks

Each executable uses the same small runner, without a test-framework package:

~~~powershell
dotnet run --project tests/MiniMcp.Checks -- --list
dotnet run --project tests/MiniMcp.RuntimeChecks -- --list
dotnet run --project tests/MiniMcp.RuntimeChecks -- --group configuration.settings-save
dotnet run --project tests/MiniMcp.RuntimeChecks -- --id runtime/configuration.settings-save/invalid-and-oversized-settings-are-rejected-before-creating-storage-or-changing-existing-files
dotnet run --project tests/MiniMcp.RuntimeChecks -- --filter "policy saves"
dotnet run --project tests/MiniMcp.ProtocolChecks
dotnet run --project tests/MiniMcp.DesktopChecks
~~~

--list, --group NAME, --id SUITE/GROUP/SLUG, and --filter TEXT work in all four executables. Selection flags can be combined; --id and --group are exact (case-insensitive), --filter is a case-insensitive substring of the name or ID. A selection that matches no case exits with code 1; listing does not execute selected check actions. IDs are deterministic within the current suite; renaming a case changes its ID. Duplicate IDs fail registration.

Results distinguish **PASS**, **FAIL**, **SKIP**, and **FAIL CLEANUP**, with per-case and suite elapsed times. An explicit skip has a reason and is never counted as a pass. A fixture can report a cleanup failure separately from its original assertion failure, including both exception stacks. Fault-matrix failures include the applicable stage/state/index dimensions where annotated. A selected check can contain several internal matrix iterations, so the number of checks is **not** the number of independent subscenarios.

For normal development, run the applicable logic/configuration groups and groups affected by a change. Before merging, run the complete default executables and the relevant integration/slow checks; on Windows run DesktopChecks as well. Real window interaction is separate: dotnet run --project tests/MiniMcp.DesktopChecks -- --ui. Screenshot generation is a development aid, not a test pass. MiniMcp.ProtocolChecks treats each of ReadOnly, ProjectEdit, and FullAccess as one complete protocol scenario; its STEP lines are internal progress, not extra passes.

Dependencies are resolved where they are used: pure core patch/format checks need no Bash; actual shell, Git, ripgrep, processes, and UI checks still need their platform-specific dependencies. Missing required dependencies fail the relevant selected check with the original diagnostic. Platform-inapplicable tests and insufficient symbolic-link permissions explicitly skip where applicable.

## Windows cleanup diagnostics

The patch fixtures check for no staging/backup leftovers as part of the **product assertion**. Separately, their teardown deletes only the fixture's temporary directory. The runner reports teardown errors by path, never drops a primary failure, and never ignores a persistent deletion failure.

An intermittent Windows sharing violation on .minimcp-*.bak.tmp was reproduced during test-directory teardown. Once observed, the file disappeared on later inspection; the locking process has **not** been identified. The affected test-only fixture cleanup therefore allows at most three short retries **only** for native Windows sharing/lock violation codes; every other error fails immediately, and an unresolved share violation still fails. CLEANUP RETRY logs disclose each retry. This retry does not change the product's patch/rollback behavior.

Do not treat an injected rollback failure, an unexpected file, an unchanged-source mismatch, or a nonempty directory as a successful teardown. Preserve the path, assertion, and exception details to investigate when one recurs.

See [first-batch baseline](first-batch-baseline.md) for the initial Windows grouped timing results. Group elapsed times reflect one machine and one run; they are not stable benchmarks.

The [second-batch coverage map](second-batch-coverage.md) identifies each cross-layer reduction, its retained assertions and the responsible layer.
