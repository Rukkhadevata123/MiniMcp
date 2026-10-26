using MiniMcp.Services.Patching;
using MiniMcp.Services.Workspace;
using System.Text;

internal static class PatchCommitOutcomeChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("patch outcomes report a complete mixed commit while holding the workspace lock", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("delete.md", "remove\n");
            fixture.Put("move.md", "move\n");
            var service = fixture.Service((stage, index) =>
            {
                Assert(fixture.Workspace.Mutations.CurrentCount == 0, "Lock is retained through all engine stages");
                if (stage == PatchCommitStage.Staged)
                    Assert(index == -1 && fixture.Get("sample.md") == "old\n" && !File.Exists(fixture.Path("nested/new.md")), "All contents staged before target installation");
                return Task.CompletedTask;
            });
            var plan = await fixture.Plan(service, "*** Add File: nested/new.md\n+added\n*** Update File: sample.md\n@@\n-old\n+new\n" +
                "*** Delete File: delete.md\n*** Update File: move.md\n*** Move to: nested/moved.md");
            var outcome = await service.CommitWithOutcomeAsync(plan);
            Assert(outcome.State == PatchCommitState.Committed && outcome.Failure is null && outcome.RollbackErrors.IsEmpty && outcome.CleanupErrors.IsEmpty, "Complete commit result");
            Assert(fixture.Get("sample.md") == "new\n" && fixture.Get("nested/new.md") == "added\n" && fixture.Get("nested/moved.md") == "move\n" &&
                !File.Exists(fixture.Path("delete.md")) && !File.Exists(fixture.Path("move.md")), "Actual mixed results");
            Assert(!fixture.Artifacts().Any() && fixture.Workspace.Mutations.CurrentCount == 1, "Artifacts removed and lock released");
        });

        await check("patch outcome entry rejects invalid, foreign, stale and pre-cancelled plans before staging", async () =>
        {
            using var fixture = new Fixture();
            var calls = 0;
            var service = fixture.Service((_, _) => { calls++; return Task.CompletedTask; });
            var plan = await fixture.Plan(service, "*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new");
            await Reject<ArgumentNullException>(() => service.CommitWithOutcomeAsync(null!));
            await Reject<WorkspaceException>(() => fixture.Service().CommitWithOutcomeAsync(plan));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Reject<OperationCanceledException>(() => service.CommitWithOutcomeAsync(plan, cancelled.Token));
            fixture.Put("sample.md", "external\n");
            var before = fixture.Snapshot();
            await Reject<WorkspaceException>(() => service.CommitWithOutcomeAsync(plan));
            Assert(calls == 0 && before.SequenceEqual(fixture.Snapshot()) && !Directory.Exists(fixture.Path("nested")), "No engine stage or filesystem writes");
            Assert(fixture.Workspace.Mutations.CurrentCount == 1, "Initial rejection releases the lock");
        });

        await check("cancellation after staging reports completed cleanup and preserves legacy cancellation", async () =>
        {
            foreach (var legacy in new[] { false, true })
            {
                using var fixture = new Fixture();
                using var cancelled = new CancellationTokenSource();
                var service = fixture.Service((stage, _) => { if (stage == PatchCommitStage.Staged) cancelled.Cancel(); return Task.CompletedTask; });
                var plan = await fixture.Plan(service, "*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new");
                var before = fixture.Snapshot();
                if (legacy) await Reject<OperationCanceledException>(() => service.CommitAsync(plan, cancelled.Token));
                else
                {
                    var outcome = await service.CommitWithOutcomeAsync(plan, cancelled.Token);
                    Assert(outcome.State == PatchCommitState.RolledBack && outcome.Failure is OperationCanceledException &&
                        outcome.RollbackErrors.IsEmpty && outcome.CleanupErrors.IsEmpty, "Cancellation is reported after cleanup");
                }
                Assert(before.SequenceEqual(fixture.Snapshot()) && fixture.Workspace.Mutations.CurrentCount == 1, "All staging and created directories removed");
            }
        });

        await check("mid-commit failure restores raw originals despite cancellation and retains the original exception", async () =>
        {
            foreach (var legacy in new[] { false, true })
            {
                using var fixture = new Fixture();
                fixture.Put("sample.md", "\ufeffold\r\n");
                var failure = new IOException("Misleading original error: rollback was incomplete.");
                using var cancelled = new CancellationTokenSource();
                var service = fixture.Service((stage, index) =>
                {
                    if (stage != PatchCommitStage.Installed || index != 1) return Task.CompletedTask;
                    Assert(fixture.Get("sample.md") == "new\r\n" && fixture.Get("nested/new.md") == "new\n", "Some target changes really installed");
                    cancelled.Cancel();
                    return Task.FromException(failure);
                });
                var plan = await fixture.Plan(service, "*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new");
                var before = fixture.Snapshot();
                if (legacy) Assert(ReferenceEquals(await Reject<IOException>(() => service.CommitAsync(plan, cancelled.Token)), failure), "Legacy API rethrows the exact original error");
                else
                {
                    var outcome = await service.CommitWithOutcomeAsync(plan, cancelled.Token);
                    Assert(outcome.State == PatchCommitState.RolledBack && ReferenceEquals(outcome.Failure, failure) &&
                        outcome.RollbackErrors.IsEmpty && outcome.CleanupErrors.IsEmpty, "Classification follows actual rollback, not error text");
                }
                Assert(before.SequenceEqual(fixture.Snapshot()) && fixture.Workspace.Mutations.CurrentCount == 1, "Original BOM, bytes, directories and Unix permissions restored");
            }
        });

        await check("real rollback failure is incomplete and retains original backups and each rollback error", async () =>
        {
            foreach (var legacy in new[] { false, true })
            {
                using var fixture = new Fixture();
                var failure = new IOException("Injected installation failure.");
                var service = fixture.Service((stage, index) =>
                {
                    if (stage != PatchCommitStage.Installed || index != 0) return Task.CompletedTask;
                    File.Delete(fixture.Path("sample.md"));
                    Directory.CreateDirectory(fixture.Path("sample.md")); // Occupancy makes the real rollback delete fail.
                    return Task.FromException(failure);
                });
                var plan = await fixture.Plan(service);
                if (legacy) _ = await Reject<WorkspaceException>(() => service.CommitAsync(plan));
                else
                {
                    var outcome = await service.CommitWithOutcomeAsync(plan);
                    Assert(outcome.State == PatchCommitState.Incomplete && ReferenceEquals(outcome.Failure, failure) && outcome.RollbackErrors.Length == 1, "Original and rollback errors kept separately");
                }
                var backup = fixture.Artifacts().Single(path => path.EndsWith(".bak", StringComparison.Ordinal));
                Assert(File.ReadAllText(backup) == "old\n" && Directory.Exists(fixture.Path("sample.md")) && fixture.Workspace.Mutations.CurrentCount == 1, "Backup and occupying directory preserved; lock released");
            }
        });

        await check("failed staging cleanup is incomplete even when no target was installed", async () =>
        {
            using var fixture = new Fixture();
            var failure = new IOException("Injected staging failure.");
            var service = fixture.Service((stage, _) =>
            {
                if (stage != PatchCommitStage.Staged) return Task.CompletedTask;
                var temporary = fixture.Artifacts().Single();
                File.Move(temporary, System.IO.Path.Combine(fixture.DirectoryPath, "detached-stage"));
                Directory.CreateDirectory(temporary); // Both File.Delete and parent-directory cleanup must fail.
                return Task.FromException(failure);
            });
            var plan = await fixture.Plan(service, "*** Add File: nested/new.md\n+new");
            var outcome = await service.CommitWithOutcomeAsync(plan);
            Assert(outcome.State == PatchCommitState.Incomplete && ReferenceEquals(outcome.Failure, failure) &&
                outcome.RollbackErrors.IsEmpty && !outcome.CleanupErrors.IsEmpty, "Cleanup failure cannot be reported as a complete rollback");
            Assert(!File.Exists(fixture.Path("nested/new.md")) && fixture.Get("sample.md") == "old\n" && fixture.Workspace.Mutations.CurrentCount == 1, "Targets untouched and lock released");
        });

        await check("successful installation remains committed when only old-backup cleanup fails", async () =>
        {
            foreach (var legacy in new[] { false, true })
            {
                using var fixture = new Fixture();
                var saved = System.IO.Path.Combine(fixture.DirectoryPath, "saved-original");
                var service = fixture.Service((stage, _) =>
                {
                    if (stage != PatchCommitStage.Installed) return Task.CompletedTask;
                    var backup = fixture.Artifacts().Single(path => path.EndsWith(".bak", StringComparison.Ordinal));
                    File.Move(backup, saved);
                    Directory.CreateDirectory(backup);
                    return Task.CompletedTask;
                });
                var plan = await fixture.Plan(service);
                if (legacy) Assert(!(await service.CommitAsync(plan)).DryRun, "Legacy successful result retained");
                else
                {
                    var outcome = await service.CommitWithOutcomeAsync(plan);
                    Assert(outcome.State == PatchCommitState.Committed && outcome.Failure is null && outcome.RollbackErrors.IsEmpty && outcome.CleanupErrors.Length == 1,
                        "Cleanup diagnostics do not misreport an installed patch as rolled back");
                }
                Assert(fixture.Get("sample.md") == "new\n" && File.ReadAllText(saved) == "old\n", "Installed result and saved original intact");
            }
        });

        await check("cancellation past the install boundary finishes the whole patch and returns committed", async () =>
        {
            foreach (var boundary in new[] { PatchCommitStage.BeforeInstall, PatchCommitStage.Installed })
                foreach (var legacy in new[] { false, true })
                {
                    using var fixture = new Fixture();
                    using var cancelled = new CancellationTokenSource();
                    var service = fixture.Service((stage, index) => { if (stage == boundary && index == 0) cancelled.Cancel(); return Task.CompletedTask; });
                    var plan = await fixture.Plan(service, "*** Update File: sample.md\n@@\n-old\n+new\n*** Add File: nested/new.md\n+added");
                    if (legacy) Assert(!(await service.CommitAsync(plan, cancelled.Token)).DryRun, "Late cancellation cannot replace successful return");
                    else
                    {
                        var outcome = await service.CommitWithOutcomeAsync(plan, cancelled.Token);
                        Assert(outcome.State == PatchCommitState.Committed && outcome.Failure is null && outcome.RollbackErrors.IsEmpty && outcome.CleanupErrors.IsEmpty, "Late cancellation does not interrupt installation");
                    }
                    Assert(cancelled.IsCancellationRequested && fixture.Get("sample.md") == "new\n" && fixture.Get("nested/new.md") == "added\n" &&
                        !fixture.Artifacts().Any() && fixture.Workspace.Mutations.CurrentCount == 1, "Whole patch committed and lock released");
                }
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Patch commit outcome: " + message); }
    private static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"minimcp-commit-outcome-{Guid.NewGuid():N}");
        public string Root { get; }
        public ProjectWorkspace Workspace { get; }
        public Fixture()
        {
            Root = Directory.CreateDirectory(System.IO.Path.Combine(DirectoryPath, "project")).FullName;
            Put("sample.md", "old\n");
            Workspace = new ProjectWorkspace(Root);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path("sample.md"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public PatchService Service(Func<PatchCommitStage, int, Task>? checkpoint = null) => new(Workspace, checkpoint);
        public string Path(string relative) => System.IO.Path.Combine(Root, relative);
        public void Put(string relative, string text) => File.WriteAllText(Path(relative), text, new UTF8Encoding(false));
        public string Get(string relative) => File.ReadAllText(Path(relative));
        public Task<PatchPlan> Plan(PatchService service, string body = "*** Update File: sample.md\n@@\n-old\n+new") =>
            service.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch");
        public IEnumerable<string> Artifacts() => Directory.EnumerateFiles(Root, ".minimcp-*", SearchOption.AllDirectories);
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => System.IO.Path.GetRelativePath(Root, path) + ":" + (Directory.Exists(path) ? "directory" :
                Convert.ToBase64String(File.ReadAllBytes(path)) + ":" + (OperatingSystem.IsWindows() ? "" : ((int)File.GetUnixFileMode(path)).ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
        public void Dispose() { Workspace.Mutations.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }
}
