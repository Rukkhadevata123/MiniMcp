using System.Text;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Services;

internal static class RecoveryFinalizationChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("finalization persists a verified mixed commit and publishes its exact final manifest and identity", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("sample.md", "\ufeffold\r\n");
            fixture.Put("delete.md", "");
            fixture.Put("move.md", "move\n");
            var plan = await fixture.Plan("*** Update File: sample.md\n@@\n-old\n+new\n*** Delete File: delete.md\n" +
                "*** Update File: move.md\n*** Move to: nested/moved.md\n*** Add File: nested/new.md\n+added");
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var applying = record.Manifest;
            var identity = record.FileIdentities[RecoveryStore.ManifestFileName];
            var backups = fixture.Backups(record);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            var project = fixture.ProjectSnapshot();
            await store.CompleteAsync(record, plan, outcome);
            Assert(record.Manifest.State == RecoveryRecordState.Committed && !record.IsInvalidated && applying.State == RecoveryRecordState.Applying &&
                record.FileIdentities[RecoveryStore.ManifestFileName] != identity, "Final revision published without mutating earlier snapshots");
            Assert(record.Manifest.Paths.All(path => path.After?.UnixModeIsCreateRequest != true) &&
                RecoveryManifestJson.Serialize(record.Manifest).SequenceEqual(File.ReadAllBytes(fixture.Manifest(record))), "Final observed modes and exact persisted bytes");
            Assert((await store.LoadAsync(record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Committed &&
                backups.SequenceEqual(fixture.Backups(record)) && project.SequenceEqual(fixture.ProjectSnapshot()), "Original backups and installed workspace remain intact");
            await store.RevalidatePreparedAsync(record, plan);
        });

        await check("finalization records complete and incomplete rollback from actual bound engine outcomes", async () =>
        {
            foreach (var incomplete in new[] { false, true })
            {
                Fixture? fixtureRef = null;
                using var fixture = new Fixture((stage, index) =>
                {
                    if (stage != PatchCommitStage.Installed || index != 0) return Task.CompletedTask;
                    if (incomplete) { File.Delete(Path.Combine(fixtureRef!.Root, "sample.md")); Directory.CreateDirectory(Path.Combine(fixtureRef.Root, "sample.md")); }
                    return Task.FromException(new IOException("Injected installation failure."));
                });
                fixtureRef = fixture;
                var plan = await fixture.Plan("*** Update File: sample.md\n@@\n-old\n+new\n*** Add File: nested/new.md\n+new");
                var store = fixture.Store();
                var record = await store.PrepareAsync(plan);
                await store.BeginApplyingAsync(record, plan);
                var backups = fixture.Backups(record);
                var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
                var project = fixture.ProjectSnapshot();
                await store.CompleteAsync(record, plan, outcome);
                var expected = incomplete ? RecoveryRecordState.Incomplete : RecoveryRecordState.RolledBack;
                Assert(record.Manifest.State == expected && (await store.LoadAsync(record.Manifest.ChangeId)).Manifest.State == expected && !record.IsInvalidated,
                    "Persisted and live state follow the observed rollback result");
                Assert(project.SequenceEqual(fixture.ProjectSnapshot()) && backups.SequenceEqual(fixture.Backups(record)), "Finalization does not attempt another rollback");
                await store.RevalidatePreparedAsync(record, plan);
            }
        });

        await check("failed post-commit verification can only be finalized as Incomplete while preserving actual engine facts", async () =>
        {
            Fixture? fixtureRef = null;
            using var fixture = new Fixture((stage, _) => { if (stage == PatchCommitStage.BeforeVerification) fixtureRef!.Put("sample.md", "external\n"); return Task.CompletedTask; });
            fixtureRef = fixture;
            var plan = await fixture.Plan();
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            Assert(outcome.State == PatchCommitState.Committed && outcome.VerifiedState == PatchCommitState.Incomplete, "Raw installation and verification facts remain separate");
            await store.CompleteAsync(record, plan, outcome);
            Assert(record.Manifest.State == RecoveryRecordState.Incomplete && File.ReadAllText(Path.Combine(fixture.Root, "sample.md")) == "external\n", "No false Committed record or overwrite of external data");
        });

        await check("finalization rejects wrong state, ownership, outcome and duplicate transitions without storage changes", async () =>
        {
            using var fixture = new Fixture();
            var store = fixture.Store();
            var other = fixture.Store();
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var peer = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(peer, plan);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            var before = fixture.JournalSnapshot();
            await Reject<ArgumentNullException>(() => store.CompleteAsync(null!, plan, outcome));
            await Reject<ArgumentNullException>(() => store.CompleteAsync(record, null!, outcome));
            await Reject<ArgumentNullException>(() => store.CompleteAsync(record, plan, null!));
            await Reject<InvalidOperationException>(() => other.CompleteAsync(record, plan, outcome));
            await Reject<InvalidOperationException>(() => store.CompleteAsync(record, new PatchPlan(plan.Owner, plan.Changes), outcome));
            await Reject<InvalidOperationException>(() => store.CompleteAsync(peer, plan, outcome));
            var prepared = await store.PrepareAsync(plan);
            before = fixture.JournalSnapshot();
            await Reject<InvalidOperationException>(() => store.CompleteAsync(prepared, plan, outcome));
            Assert(before.SequenceEqual(fixture.JournalSnapshot()), "All invalid requests leave every record intact");
            await store.CompleteAsync(record, plan, outcome);
            before = fixture.JournalSnapshot();
            await Reject<InvalidOperationException>(() => store.CompleteAsync(record, plan, outcome));
            await Reject<InvalidOperationException>(() => store.BeginApplyingAsync(record, plan));
            Assert(before.SequenceEqual(fixture.JournalSnapshot()) && !record.IsInvalidated, "Terminal records cannot repeat or go backwards");
        });

        await check("pre-rename finalization failure keeps Applying retryable and carries the committed engine outcome", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.BeforeTemporaryFile, RecoveryManifestReplacementStage.TemporaryWritten, RecoveryManifestReplacementStage.BeforeReplace })
            {
                using var fixture = new Fixture();
                var armed = false;
                var failOnce = true;
                var failure = new IOException("Injected final metadata write failure.");
                var store = fixture.Store((stage, _) =>
                {
                    if (!armed || !failOnce || stage != boundary) return Task.CompletedTask;
                    failOnce = false;
                    return Task.FromException(failure);
                });
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                await store.BeginApplyingAsync(record, plan);
                var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
                var project = fixture.ProjectSnapshot();
                var journal = fixture.JournalSnapshot();
                armed = true;
                var error = await Reject<RecoveryFinalizationException>(() => store.CompleteAsync(record, plan, outcome));
                Assert(ReferenceEquals(error.Outcome, outcome) && ReferenceEquals(error.InnerException, failure) && outcome.State == PatchCommitState.Committed &&
                    record.Manifest.State == RecoveryRecordState.Applying && !record.IsInvalidated, "Engine success is not replaced with a cancellation/rollback claim");
                Assert(project.SequenceEqual(fixture.ProjectSnapshot()) && journal.SequenceEqual(fixture.JournalSnapshot()), "Only owned temporary metadata was cleaned");
                await store.RevalidatePreparedAsync(record, plan);
                await store.CompleteAsync(record, plan, outcome);
                Assert(record.Manifest.State == RecoveryRecordState.Committed, "Same verified outcome can retry metadata without replaying the engine");
            }
        });

        await check("post-rename finalization failures preserve actual stored state and invalidate the live handle", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.Replaced, RecoveryManifestReplacementStage.BeforeReturn })
            {
                using var fixture = new Fixture();
                var armed = false;
                Exception failure = boundary == RecoveryManifestReplacementStage.Replaced ? new IOException("Injected final flush failure.") : new OperationCanceledException("Injected late metadata cancellation error.");
                var store = fixture.Store((stage, _) => armed && stage == boundary ? Task.FromException(failure) : Task.CompletedTask);
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                await store.BeginApplyingAsync(record, plan);
                var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
                var backups = fixture.Backups(record);
                var project = fixture.ProjectSnapshot();
                armed = true;
                var error = await Reject<RecoveryFinalizationException>(() => store.CompleteAsync(record, plan, outcome));
                Assert(ReferenceEquals(error.Outcome, outcome) && error.InnerException is RecoveryManifestReplacementException { ReplacementAttempted: true } replaced && ReferenceEquals(replaced.InnerException, failure),
                    "Original execution and uncertain replacement errors retained together");
                Assert(record.IsInvalidated && record.Manifest.State == RecoveryRecordState.Applying &&
                    (await store.LoadAsync(record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Committed, "No unverified live promotion or automatic manifest rollback");
                Assert(backups.SequenceEqual(fixture.Backups(record)) && project.SequenceEqual(fixture.ProjectSnapshot()), "Original backup and installed files preserved");
                await Reject<InvalidOperationException>(() => store.CompleteAsync(record, plan, outcome));
            }
        });

        await check("incomplete finalization cleanup preserves foreign data and permanently disables the old live record", async () =>
        {
            using var fixture = new Fixture();
            var armed = false;
            string? temporary = null;
            var store = fixture.Store((stage, path) =>
            {
                if (!armed || stage != RecoveryManifestReplacementStage.BeforeReplace) return Task.CompletedTask;
                temporary = path;
                var bytes = File.ReadAllBytes(path);
                File.Move(path, Path.Combine(fixture.DirectoryPath, "detached-final"));
                File.WriteAllBytes(path, bytes);
                return Task.CompletedTask;
            });
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            armed = true;
            var error = await Reject<RecoveryFinalizationException>(() => store.CompleteAsync(record, plan, outcome));
            Assert(record.IsInvalidated && error.InnerException is RecoveryManifestReplacementException { ReplacementAttempted: false } failed && !failed.CleanupErrors.IsEmpty && File.Exists(temporary),
                "Unowned replacement is never removed by cleanup");
            File.Delete(temporary!); // Test operator removes its own foreign fixture, not runtime repair.
            Assert((await store.LoadAsync(record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Applying && File.ReadAllText(Path.Combine(fixture.Root, "sample.md")) == "new\n",
                "Original journal state and actual commit remain inspectable");
            await Reject<InvalidOperationException>(() => store.CompleteAsync(record, plan, outcome));
        });

        await check("cancelling a UI wait does not abandon finalization and queued duplicates observe its terminal revision", async () =>
        {
            using var fixture = new Fixture();
            using var cancelled = new CancellationTokenSource();
            var armed = false;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writes = 0;
            var store = fixture.Store(async (stage, _) =>
            {
                if (!armed || stage != RecoveryManifestReplacementStage.TemporaryWritten) return;
                writes++;
                cancelled.Cancel();
                entered.TrySetResult();
                await release.Task;
            });
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            armed = true;
            var finishing = store.CompleteAsync(record, plan, outcome);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var duplicate = store.CompleteAsync(record, plan, outcome);
                await Reject<OperationCanceledException>(() => finishing.WaitAsync(cancelled.Token));
                Assert(!finishing.IsCompleted && !duplicate.IsCompleted && record.Manifest.State == RecoveryRecordState.Applying, "Underlying write and its locks remain owned");
                release.TrySetResult();
                await finishing;
                await Reject<InvalidOperationException>(() => duplicate);
                Assert(writes == 1 && record.Manifest.State == RecoveryRecordState.Committed && !record.IsInvalidated, "Only one final update and no false cancellation");
                await store.RevalidatePreparedAsync(record, plan);
            }
            finally { release.TrySetResult(); await finishing; }
        });

        await check("busy storage locks report finalization failure without losing engine results and permit metadata retry", async () =>
        {
            using var fixture = new Fixture();
            var store = fixture.Store();
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            var before = fixture.JournalSnapshot();
            using (var held = RecoveryFileSystem.OpenRead(Path.Combine(fixture.Storage, RecoveryStorageLimits.LockFileName)))
            {
                RecoveryFileSystem.Lock(held);
                var error = await Reject<RecoveryFinalizationException>(() => store.CompleteAsync(record, plan, outcome));
                Assert(ReferenceEquals(error.Outcome, outcome) && record.Manifest.State == RecoveryRecordState.Applying && !record.IsInvalidated, "Busy lock does not change persistent or live state");
            }
            Assert(before.SequenceEqual(fixture.JournalSnapshot()), "No temporary writes before lock acquisition");
            await store.CompleteAsync(record, plan, outcome);
        });

        await check("finalization rechecks storage quota and preserves foreign records while reporting an already committed patch", async () =>
        {
            using var fixture = new Fixture();
            var store = fixture.Store();
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            for (var i = 0; i < 1000; i++) Directory.CreateDirectory(Path.Combine(fixture.Storage, $"foreign-{i}"));
            var before = fixture.JournalSnapshot();
            var error = await Reject<RecoveryFinalizationException>(() => store.CompleteAsync(record, plan, outcome));
            Assert(ReferenceEquals(error.Outcome, outcome) && !record.IsInvalidated && record.Manifest.State == RecoveryRecordState.Applying && before.SequenceEqual(fixture.JournalSnapshot()),
                "Quota failure never relabels or replays the committed engine operation");
            Directory.Delete(Path.Combine(fixture.Storage, "foreign-0")); // Remove one owned empty test fixture slot.
            await store.CompleteAsync(record, plan, outcome);
            Assert(Directory.EnumerateDirectories(fixture.Storage).Count() == 1000 && record.Manifest.State == RecoveryRecordState.Committed, "Existing thousandth record still updates without evicting other data");
        });

        await check("finalization refuses rewritten recovery metadata without overwriting it or undoing the workspace", async () =>
        {
            using var fixture = new Fixture();
            var store = fixture.Store();
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            await store.BeginApplyingAsync(record, plan);
            var outcome = await fixture.Service.CommitWithContextAsync(plan, record.ExecutionContext);
            var changed = record.Manifest with { RunId = Guid.NewGuid() };
            File.WriteAllBytes(fixture.Manifest(record), RecoveryManifestJson.Serialize(changed));
            var before = fixture.Snapshot();
            var error = await Reject<RecoveryFinalizationException>(() => store.CompleteAsync(record, plan, outcome));
            Assert(ReferenceEquals(error.Outcome, outcome) && before.SequenceEqual(fixture.Snapshot()) && record.Manifest.State == RecoveryRecordState.Applying,
                "Foreign data preserved and no unverified live state advance");
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Recovery finalization: " + message); }
    private static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-finalization-{Guid.NewGuid():N}");
        public string Root { get; }
        public string Storage => Path.Combine(DirectoryPath, "archive", "recovery");
        public ProjectWorkspace Workspace { get; }
        public PatchService Service { get; }
        private readonly ConfigurationPaths _paths;
        private readonly string _bash;
        public Fixture(Func<PatchCommitStage, int, Task>? checkpoint = null)
        {
            Root = Directory.CreateDirectory(Path.Combine(DirectoryPath, "project")).FullName;
            Put("sample.md", "old\n");
            Workspace = new ProjectWorkspace(Root);
            Service = new PatchService(Workspace, checkpoint);
            _paths = new ConfigurationPaths(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"));
            _bash = Path.Combine(DirectoryPath, "bash-fixture");
            File.WriteAllText(_bash, "Dependency fixture; never execute.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_bash, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        public RecoveryStore Store(Func<RecoveryManifestReplacementStage, string, Task>? hook = null) => new(Guid.NewGuid(), new ConfigurationValidator(_paths).Validate(
            new AppSettings { ProjectRoot = Root, BashPath = _bash, Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } },
            Path.Combine(DirectoryPath, "settings.json"), new ExecutionPolicy { Mode = ExecutionMode.FullAccess, RecoveryDirectory = Storage }), null, null, hook);
        public Task<PatchPlan> Plan(string body = "*** Update File: sample.md\n@@\n-old\n+new") => Service.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch");
        public void Put(string path, string text) => File.WriteAllText(Path.Combine(Root, path), text, new UTF8Encoding(false));
        public string Manifest(RecoveryStore.PreparedRecord record) => Path.Combine(record.DirectoryPath, RecoveryStore.ManifestFileName);
        public string[] Backups(RecoveryStore.PreparedRecord record) => Directory.EnumerateFiles(record.DirectoryPath, "*").Where(path => Path.GetFileName(path).StartsWith("before-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
        public string[] JournalSnapshot() => Directory.EnumerateFiles(Storage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Storage, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(DirectoryPath, path) + ":" + (Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public string[] ProjectSnapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Root, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
        public void Dispose() { Workspace.Mutations.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }
}
