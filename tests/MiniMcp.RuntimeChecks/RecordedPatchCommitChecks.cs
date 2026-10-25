using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Services;

internal static class RecordedPatchCommitChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("recorded mixed commit retains one workspace lock from original backups through verified final metadata", async () =>
        {
            Fixture? fixtureRef = null;
            var order = new List<string>();
            using var fixture = new Fixture((stage, _) =>
            {
                Assert(fixtureRef!.Workspace.Mutations.CurrentCount == 0, "Engine uses the transaction's workspace lock");
                Assert(fixtureRef.ReadManifest().State == RecoveryRecordState.Applying, "Durable Applying precedes every engine stage");
                order.Add(stage.ToString());
                return Task.CompletedTask;
            });
            fixtureRef = fixture;
            fixture.Put("sample.md", "\ufeffold\r\nlocal edit\r\n");
            fixture.Put("delete.md", "");
            fixture.Put("move.md", "move\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fixture.Path("move.md"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var originalMode = OperatingSystem.IsWindows() ? (UnixFileMode?)null : File.GetUnixFileMode(fixture.Path("move.md"));
            var original = fixture.ProjectSnapshot();
            var plan = await fixture.Plan("*** Update File: sample.md\n@@\n-old\n+new\n*** Delete File: delete.md\n" +
                "*** Update File: move.md\n*** Move to: nested/moved.md\n*** Add File: nested/new.md\n+added\n*** Add File: empty.md");
            var store = fixture.Store(prepare: (_, _) =>
            {
                Assert(fixture.Workspace.Mutations.CurrentCount == 0 && original.SequenceEqual(fixture.ProjectSnapshot()), "Preparation holds the lock with zero workspace writes");
                order.Add("Prepare");
                return Task.CompletedTask;
            }, replacement: (stage, _) =>
            {
                Assert(fixture.Workspace.Mutations.CurrentCount == 0, "State persistence retains the same workspace lock");
                if (stage == RecoveryManifestReplacementStage.BeforeReturn)
                {
                    var state = fixture.ReadManifest().State;
                    order.Add(state.ToString());
                    if (state == RecoveryRecordState.Applying)
                        Assert(original.SequenceEqual(fixture.ProjectSnapshot()), "No staging before Applying is fully checked");
                }
                return Task.CompletedTask;
            });
            var result = await fixture.Service.CommitWithRecoveryAsync(plan, store).WaitAsync(TimeSpan.FromSeconds(15));
            Assert(result.Outcome.VerifiedState == PatchCommitState.Committed && result.Record.Manifest.State == RecoveryRecordState.Committed &&
                result.Record.Manifest.RunId == fixture.RunId && ReferenceEquals(result.Record.Plan, plan), "Exact engine, plan, record and session binding");
            Assert(order.IndexOf("Applying") < order.IndexOf("Staged") && order.IndexOf("BeforeVerification") < order.IndexOf("Committed"), "Preparation, installation, verification and finalization are ordered");
            var saved = await store.LoadAsync(result.Record.Manifest.ChangeId);
            Assert(saved.Manifest.State == RecoveryRecordState.Committed && saved.Backups.Length == 3 &&
                saved.Backups[0].Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("\ufeffold\r\nlocal edit\r\n")) && saved.Backups[1].Bytes.IsEmpty,
                "Complete original BOM, line endings, local edits and empty deleted file are durably retained");
            Assert(fixture.Get("sample.md") == "new\r\nlocal edit\r\n" && fixture.Get("nested/moved.md") == "move\n" &&
                fixture.Get("nested/new.md") == "added\n" && File.ReadAllBytes(fixture.Path("empty.md")).Length == 0 &&
                !File.Exists(fixture.Path("move.md")) && !File.Exists(fixture.Path("delete.md")), "All mixed operations installed");
            Assert(saved.Manifest.Paths.Single(path => path.Path == "nested/moved.md").After!.UnixMode == originalMode &&
                saved.Manifest.Paths.All(path => path.After?.UnixModeIsCreateRequest != true), "Final modes are observations, preserving moved permissions");
            if (!OperatingSystem.IsWindows())
                Assert(saved.Manifest.Paths.Single(path => path.Path == "nested/new.md").After!.UnixMode == File.GetUnixFileMode(fixture.Path("nested/new.md")), "Actual umask-adjusted new permissions recorded");
            Assert(!fixture.Artifacts().Any() && fixture.Workspace.Mutations.CurrentCount == 1, "Staging cleaned and lock released after finalization");
            await store.RevalidatePreparedAsync(result.Record, plan);
        });

        await check("recorded entry rejects invalid, foreign, read-only, stale and pre-cancelled requests without staging or records", async () =>
        {
            using var fixture = new Fixture();
            using var other = new Fixture();
            var plan = await fixture.Plan("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new");
            var original = fixture.ProjectSnapshot();
            var store = fixture.Store();
            await Reject<ArgumentNullException>(() => fixture.Service.CommitWithRecoveryAsync(null!, store));
            await Reject<ArgumentNullException>(() => fixture.Service.CommitWithRecoveryAsync(plan, null!));
            await Reject<WorkspaceException>(() => new PatchService(fixture.Workspace).CommitWithRecoveryAsync(plan, store));
            await Reject<WorkspaceException>(() => fixture.Service.CommitWithRecoveryAsync(plan, fixture.Store(mode: ExecutionMode.ReadOnly)));
            await Reject<JsonException>(() => fixture.Service.CommitWithRecoveryAsync(plan, other.Store()));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Reject<OperationCanceledException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store, cancelled.Token));
            Assert(original.SequenceEqual(fixture.ProjectSnapshot()) && !Directory.Exists(fixture.Storage) && !Directory.Exists(other.Storage), "Rejected requests create no workspace or recovery artifacts");
            fixture.Put("sample.md", "external\n");
            original = fixture.ProjectSnapshot();
            await Reject<WorkspaceException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
            Assert(original.SequenceEqual(fixture.ProjectSnapshot()) && !Directory.Exists(fixture.Storage) && fixture.Workspace.Mutations.CurrentCount == 1, "Stale plan rejected before preparation");
        });

        await check("ordinary ProjectEdit commits use the same internal recorded transaction", async () =>
        {
            using var fixture = new Fixture();
            var store = fixture.Store(mode: ExecutionMode.ProjectEdit);
            var result = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(), store);
            Assert(result.Record.Manifest.State == RecoveryRecordState.Committed && fixture.Get("sample.md") == "new\n" &&
                (await store.LoadAsync(result.Record.Manifest.ChangeId)).Backups.Single().Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("old\n")), "Recorded write works in the other writable mode");
        });

        await check("preparation failures never enter the patch engine or create workspace directories", async () =>
        {
            foreach (var boundary in Enum.GetValues<RecoveryPreparationStage>())
            {
                var engineCalls = 0;
                using var fixture = new Fixture((_, _) => { engineCalls++; return Task.CompletedTask; });
                var failure = new IOException("Injected preparation failure.");
                var store = fixture.Store(prepare: (stage, _) => stage == boundary ? Task.FromException(failure) : Task.CompletedTask);
                var plan = await fixture.Plan("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new");
                var before = fixture.ProjectSnapshot();
                var error = await Reject<IOException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
                Assert(ReferenceEquals(error, failure) && engineCalls == 0 && before.SequenceEqual(fixture.ProjectSnapshot()) &&
                    !Directory.Exists(fixture.Path("nested")) && !Directory.EnumerateDirectories(fixture.Storage).Any(), "Failed preparation cleaned only its owned record and left the workspace intact");
            }
        });

        await check("failed Applying persistence preserves real disk state and never stages workspace contents", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.BeforeReplace, RecoveryManifestReplacementStage.Replaced })
            {
                var engineCalls = 0;
                using var fixture = new Fixture((_, _) => { engineCalls++; return Task.CompletedTask; });
                var failure = new IOException("Injected Applying persistence failure.");
                var store = fixture.Store(replacement: (stage, _) => stage == boundary ? Task.FromException(failure) : Task.CompletedTask);
                var plan = await fixture.Plan("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new");
                var before = fixture.ProjectSnapshot();
                await Reject<IOException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
                var saved = await store.LoadAsync(fixture.ReadManifest().ChangeId);
                Assert(saved.Manifest.State == (boundary == RecoveryManifestReplacementStage.Replaced ? RecoveryRecordState.Applying : RecoveryRecordState.Prepared) &&
                    saved.Backups.Single().Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("old\n")), "Original bytes and actual last stored revision remain inspectable");
                Assert(engineCalls == 0 && before.SequenceEqual(fixture.ProjectSnapshot()) && fixture.Workspace.Mutations.CurrentCount == 1, "Unverified Applying cannot start the engine");
            }
        });

        await check("external edits during backup preparation invalidate the full plan before Applying or staging", async () =>
        {
            var engineCalls = 0;
            using var fixture = new Fixture((_, _) => { engineCalls++; return Task.CompletedTask; });
            fixture.Put("second.md", "second\n");
            var store = fixture.Store(prepare: (stage, _) =>
            {
                if (stage == RecoveryPreparationStage.BeforeReturn) fixture.Put("second.md", "external\n");
                return Task.CompletedTask;
            });
            var plan = await fixture.Plan("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new\n*** Delete File: second.md");
            await Reject<WorkspaceException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
            var saved = await store.LoadAsync(fixture.ReadManifest().ChangeId);
            Assert(saved.Manifest.State == RecoveryRecordState.Prepared && saved.Backups[1].Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("second\n")) &&
                fixture.Get("second.md") == "external\n" && fixture.Get("sample.md") == "old\n" && !Directory.Exists(fixture.Path("nested")) && engineCalls == 0,
                "Prepared backup remains historical evidence; external changes are never overwritten");
        });

        await check("a plan changed while Applying is persisted completes as Incomplete without workspace writes", async () =>
        {
            var staged = false;
            using var fixture = new Fixture((stage, _) => { staged |= stage == PatchCommitStage.Staged; return Task.CompletedTask; });
            var changed = false;
            var store = fixture.Store(replacement: (stage, _) =>
            {
                if (!changed && stage == RecoveryManifestReplacementStage.BeforeReturn)
                {
                    changed = true;
                    fixture.Put("sample.md", "external\n");
                }
                return Task.CompletedTask;
            });
            var plan = await fixture.Plan("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new");
            var result = await fixture.Service.CommitWithRecoveryAsync(plan, store);
            Assert(result.Outcome.State == PatchCommitState.RolledBack && result.Outcome.Failure is WorkspaceException &&
                result.Outcome.VerifiedState == PatchCommitState.Incomplete && result.Outcome.VerificationFailure is not null &&
                result.Record.Manifest.State == RecoveryRecordState.Incomplete, "Refused installation does not claim it restored external edits");
            Assert(!staged && fixture.Get("sample.md") == "external\n" && !Directory.Exists(fixture.Path("nested")) &&
                (await store.LoadAsync(result.Record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Incomplete, "Truthful final state saved with zero engine staging");
        });

        await check("cancellation immediately after Applying is saved still persists a verified no-install rollback", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            var staged = false;
            using var fixture = new Fixture((stage, _) => { staged |= stage == PatchCommitStage.Staged; return Task.CompletedTask; });
            var store = fixture.Store(replacement: (stage, _) =>
            {
                if (stage == RecoveryManifestReplacementStage.Replaced) cancelled.Cancel();
                return Task.CompletedTask;
            });
            var before = fixture.ProjectSnapshot();
            var result = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(), store, cancelled.Token);
            Assert(result.Outcome.Failure is OperationCanceledException && result.Outcome.VerifiedState == PatchCommitState.RolledBack &&
                result.Record.Manifest.State == RecoveryRecordState.RolledBack && before.SequenceEqual(fixture.ProjectSnapshot()) && !staged,
                "Cancellation finishes verification and final metadata rather than abandoning Applying");
            Assert((await store.LoadAsync(result.Record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.RolledBack, "Final write ignored the cancelled call token");
        });

        await check("each untouched operation is revalidated immediately before installation and external bytes survive", async () =>
        {
            Fixture? fixtureRef = null;
            using var fixture = new Fixture((stage, index) =>
            {
                if (stage == PatchCommitStage.BeforeInstall && index == 1) fixtureRef!.Put("sample.md", "external before install\n");
                return Task.CompletedTask;
            });
            fixtureRef = fixture;
            var store = fixture.Store();
            var plan = await fixture.Plan("*** Add File: nested/new.md\n+new\n*** Update File: sample.md\n@@\n-old\n+new");
            var result = await fixture.Service.CommitWithRecoveryAsync(plan, store);
            Assert(result.Outcome.State == PatchCommitState.RolledBack && result.Outcome.Failure is WorkspaceException &&
                result.Outcome.VerifiedState == PatchCommitState.Incomplete && result.Record.Manifest.State == RecoveryRecordState.Incomplete,
                "Earlier installation rolled back, external mismatch reported separately");
            Assert(fixture.Get("sample.md") == "external before install\n" && !Directory.Exists(fixture.Path("nested")) && !fixture.Artifacts().Any(),
                "Untouched changed source was never backed up over or replaced");
        });

        await check("recorded engine failures persist complete or incomplete rollback and keep original recovery bytes", async () =>
        {
            foreach (var incomplete in new[] { false, true })
            {
                Fixture? fixtureRef = null;
                var failure = new IOException("Injected installation failure.");
                using var fixture = new Fixture((stage, index) =>
                {
                    if (stage != PatchCommitStage.Installed || index != 0) return Task.CompletedTask;
                    if (incomplete) { File.Delete(fixtureRef!.Path("sample.md")); Directory.CreateDirectory(fixtureRef.Path("sample.md")); }
                    return Task.FromException(failure);
                });
                fixtureRef = fixture;
                fixture.Put("sample.md", "\ufeffold\r\n");
                var plan = await fixture.Plan("*** Update File: sample.md\n@@\n-old\n+new\n*** Add File: nested/new.md\n+new");
                var original = fixture.ProjectSnapshot();
                var store = fixture.Store();
                var result = await fixture.Service.CommitWithRecoveryAsync(plan, store);
                var expected = incomplete ? RecoveryRecordState.Incomplete : RecoveryRecordState.RolledBack;
                var saved = await store.LoadAsync(result.Record.Manifest.ChangeId);
                Assert(ReferenceEquals(result.Outcome.Failure, failure) && result.Record.Manifest.State == expected && saved.Manifest.State == expected &&
                    saved.Backups.Single().Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("\ufeffold\r\n")), "Persisted state follows real rollback, original bytes remain recoverable");
                if (incomplete) Assert(!result.Outcome.RollbackErrors.IsEmpty && fixture.Artifacts().Any(path => path.EndsWith(".bak", StringComparison.Ordinal)), "Incomplete rollback retains engine backup and error");
                else Assert(original.SequenceEqual(fixture.ProjectSnapshot()) && !fixture.Artifacts().Any(), "Complete rollback restores full original workspace");
            }
        });

        await check("late cancellation cannot abandon final metadata or release the workspace lock to another operation", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            using var fixture = new Fixture((stage, index) =>
            {
                if (stage == PatchCommitStage.Installed && index == 0) cancelled.Cancel();
                return Task.CompletedTask;
            });
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var store = fixture.Store(replacement: async (stage, path) =>
            {
                if (stage != RecoveryManifestReplacementStage.TemporaryWritten || RecoveryManifestJson.Parse(File.ReadAllBytes(path)).State != RecoveryRecordState.Committed) return;
                entered.TrySetResult();
                await release.Task;
            });
            var plan = await fixture.Plan("*** Update File: sample.md\n@@\n-old\n+new\n*** Add File: nested/new.md\n+new");
            var committing = fixture.Service.CommitWithRecoveryAsync(plan, store, cancelled.Token);
            Task<PatchPlan>? waiting = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                waiting = fixture.Plan("*** Add File: peer.md\n+peer");
                await Reject<OperationCanceledException>(() => committing.WaitAsync(cancelled.Token));
                Assert(!committing.IsCompleted && !waiting.IsCompleted && fixture.Workspace.Mutations.CurrentCount == 0 &&
                    fixture.Get("sample.md") == "new\n" && fixture.Get("nested/new.md") == "new\n" && fixture.ReadManifest().State == RecoveryRecordState.Applying,
                    "Whole patch installed; actual final task and workspace lock remain owned despite cancelled UI wait");
                release.TrySetResult();
                var result = await committing;
                await waiting;
                Assert(result.Outcome.VerifiedState == PatchCommitState.Committed && result.Record.Manifest.State == RecoveryRecordState.Committed &&
                    (await store.LoadAsync(result.Record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Committed, "Late cancellation does not replace a real committed result");
            }
            finally
            {
                release.TrySetResult();
                await committing;
                if (waiting is not null) await waiting;
            }
        });

        await check("final save errors carry the exact record and engine outcome without replay or false workspace rollback", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.BeforeReplace, RecoveryManifestReplacementStage.Replaced })
            {
                var installs = 0;
                using var fixture = new Fixture((stage, _) => { if (stage == PatchCommitStage.Installed) installs++; return Task.CompletedTask; });
                var finishing = false;
                var failOnce = true;
                var failure = new IOException("Injected final save failure.");
                var store = fixture.Store(replacement: (stage, path) =>
                {
                    if (stage == RecoveryManifestReplacementStage.TemporaryWritten)
                        finishing = RecoveryManifestJson.Parse(File.ReadAllBytes(path)).State == RecoveryRecordState.Committed;
                    if (finishing && failOnce && stage == boundary) { failOnce = false; return Task.FromException(failure); }
                    return Task.CompletedTask;
                });
                var plan = await fixture.Plan();
                var error = await Reject<RecoveryFinalizationException>(() => fixture.Service.CommitWithRecoveryAsync(plan, store));
                var saved = await store.LoadAsync(error.Record.Manifest.ChangeId);
                Assert(ReferenceEquals(error.Record.Plan, plan) && error.Outcome.VerifiedState == PatchCommitState.Committed &&
                    fixture.Get("sample.md") == "new\n" && installs == 1 && fixture.Workspace.Mutations.CurrentCount == 1 &&
                    saved.Backups.Single().Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("old\n")), "Installed result, original backup and structured failure remain available");
                if (boundary == RecoveryManifestReplacementStage.BeforeReplace)
                {
                    Assert(ReferenceEquals(error.InnerException, failure) && saved.Manifest.State == RecoveryRecordState.Applying && !error.Record.IsInvalidated, "Safe pre-replace failure remains retryable");
                    await store.CompleteAsync(error.Record, plan, error.Outcome);
                    Assert(error.Record.Manifest.State == RecoveryRecordState.Committed && installs == 1, "Only metadata retried");
                }
                else Assert(error.Record.IsInvalidated && saved.Manifest.State == RecoveryRecordState.Committed && error.Record.Manifest.State == RecoveryRecordState.Applying,
                    "Uncertain replacement never promotes the old live handle or rewinds actual disk state");
            }
        });

        await check("recorded post-install verification failure persists Incomplete without overwriting external edits", async () =>
        {
            Fixture? fixtureRef = null;
            using var fixture = new Fixture((stage, _) =>
            {
                if (stage == PatchCommitStage.BeforeVerification) fixtureRef!.Put("sample.md", "external after install\n");
                return Task.CompletedTask;
            });
            fixtureRef = fixture;
            var store = fixture.Store();
            var result = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(), store);
            Assert(result.Outcome.State == PatchCommitState.Committed && result.Outcome.VerifiedState == PatchCommitState.Incomplete &&
                result.Record.Manifest.State == RecoveryRecordState.Incomplete && (await store.LoadAsync(result.Record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Incomplete &&
                fixture.Get("sample.md") == "external after install\n", "Observed mismatch cannot be persisted as success or trigger another rollback");
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Recorded patch commit: " + message); }
    private static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }

    internal sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"minimcp-recorded-commit-{Guid.NewGuid():N}");
        public Guid RunId { get; } = Guid.NewGuid();
        public string Root { get; }
        public string Storage => System.IO.Path.Combine(DirectoryPath, "archive", "recovery");
        public ProjectWorkspace Workspace { get; }
        public PatchService Service { get; }
        private readonly ConfigurationPaths _paths;
        private readonly string _bash;
        public Fixture(Func<PatchCommitStage, int, Task>? checkpoint = null)
        {
            Root = Directory.CreateDirectory(System.IO.Path.Combine(DirectoryPath, "project")).FullName;
            Put("sample.md", "old\n");
            Workspace = new ProjectWorkspace(Root);
            Service = new PatchService(Workspace, checkpoint);
            _paths = new ConfigurationPaths(System.IO.Path.Combine(DirectoryPath, "user"), System.IO.Path.Combine(DirectoryPath, "state"));
            _bash = System.IO.Path.Combine(DirectoryPath, "bash-fixture");
            File.WriteAllText(_bash, "Dependency fixture; never execute.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_bash, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        public RecoveryStore Store(Func<RecoveryPreparationStage, RecoveryDraft, Task>? prepare = null,
            Func<RecoveryManifestReplacementStage, string, Task>? replacement = null, ExecutionMode mode = ExecutionMode.FullAccess) => new(RunId,
            new ConfigurationValidator(_paths).Validate(new AppSettings { ProjectRoot = Root, BashPath = _bash, Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } },
                System.IO.Path.Combine(DirectoryPath, "settings.json"), new ExecutionPolicy { Mode = mode, RecoveryDirectory = Storage }), prepare, null, replacement);
        public Task<PatchPlan> Plan(string body = "*** Update File: sample.md\n@@\n-old\n+new") => Service.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch");
        public string Path(string path) => System.IO.Path.Combine(Root, path);
        public void Put(string path, string text) => File.WriteAllText(Path(path), text, new UTF8Encoding(false));
        public string Get(string path) => File.ReadAllText(Path(path));
        public IEnumerable<string> RecordDirectories() => Directory.Exists(Storage) ? Directory.EnumerateDirectories(Storage)
            .Where(path => Guid.TryParseExact(System.IO.Path.GetFileName(path), "N", out _)) : [];
        public string ManifestPath => System.IO.Path.Combine(RecordDirectories().Single(), RecoveryStore.ManifestFileName);
        public RecoveryManifest ReadManifest() => RecoveryManifestJson.Parse(File.ReadAllBytes(ManifestPath));
        public IEnumerable<string> Artifacts() => Directory.EnumerateFileSystemEntries(Root, ".minimcp-*", SearchOption.AllDirectories);
        public string[] ProjectSnapshot() => Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => System.IO.Path.GetRelativePath(Root, path) + ":" + (Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)) + ":" +
                (OperatingSystem.IsWindows() ? "" : ((int)File.GetUnixFileMode(path)).ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
        public void Dispose()
        {
            Workspace.Mutations.Dispose();
            var directory = System.IO.Path.GetFullPath(DirectoryPath);
            if (!string.Equals(System.IO.Path.GetDirectoryName(directory), System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !System.IO.Path.GetFileName(directory).StartsWith("minimcp-recorded-commit-", StringComparison.Ordinal)) throw new IOException("Unexpected test cleanup path.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
