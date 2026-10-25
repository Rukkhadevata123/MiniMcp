using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Services;

internal static class RecoveryApplyingStateChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("begin-applying persists a mixed record and advances the complete live revision without workspace writes", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("sample.md", "\ufeffold\r\n");
            fixture.Put("empty.md", "");
            fixture.Put("move.md", "move\n");
            var plan = await fixture.Plan("*** Update File: sample.md\n@@\n-old\n+new\n*** Delete File: empty.md\n" +
                "*** Update File: move.md\n*** Move to: moved.md\n*** Add File: nested/new.md\n+added");
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            var original = record.Manifest;
            var originalIdentities = record.FileIdentities;
            var project = fixture.ProjectSnapshot();
            var backups = fixture.Backups(record);
            await store.BeginApplyingAsync(record, plan);
            Assert(record.Manifest.State == RecoveryRecordState.Applying && !record.IsInvalidated && original.State == RecoveryRecordState.Prepared, "New live manifest with immutable earlier snapshot");
            Assert(record.FileIdentities[RecoveryStore.ManifestFileName] != originalIdentities[RecoveryStore.ManifestFileName], "Manifest identity advanced");
            foreach (var identity in originalIdentities.Where(pair => pair.Key != RecoveryStore.ManifestFileName))
                Assert(record.FileIdentities[identity.Key] == identity.Value, "Original backup identities retained");
            Assert(RecoveryManifestJson.Serialize(record.Manifest).SequenceEqual(File.ReadAllBytes(fixture.Manifest(record))), "Complete live and persistent metadata agree");
            await store.RevalidatePreparedAsync(record, plan);
            Assert((await store.LoadAsync(record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Applying && backups.SequenceEqual(fixture.Backups(record)) &&
                project.SequenceEqual(fixture.ProjectSnapshot()) && !Directory.Exists(Path.Combine(fixture.Root, "nested")), "Record ready for later engine integration, workspace unchanged");
        });

        await check("begin-applying rejects foreign plans, nulls, cancellation and duplicate transitions before temporary writes", async () =>
        {
            using var fixture = new Fixture();
            var checkpoints = 0;
            var store = fixture.Store((_, _) => { checkpoints++; return Task.CompletedTask; });
            var other = fixture.Store();
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            var before = fixture.Snapshot();
            await Reject<ArgumentNullException>(() => store.BeginApplyingAsync(null!, plan));
            await Reject<ArgumentNullException>(() => store.BeginApplyingAsync(record, null!));
            await Reject<InvalidOperationException>(() => other.BeginApplyingAsync(record, plan));
            await Reject<InvalidOperationException>(() => store.BeginApplyingAsync(record, new PatchPlan(plan.Owner, plan.Changes)));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Reject<OperationCanceledException>(() => store.BeginApplyingAsync(record, plan, cancelled.Token));
            Assert(before.SequenceEqual(fixture.Snapshot()) && checkpoints == 0, "Invalid requests create no temporary files");
            await store.BeginApplyingAsync(record, plan);
            var count = checkpoints;
            before = fixture.Snapshot();
            await Reject<InvalidOperationException>(() => store.BeginApplyingAsync(record, plan));
            Assert(checkpoints == count && before.SequenceEqual(fixture.Snapshot()) && !record.IsInvalidated, "Duplicate transition preserves usable Applying record");
            await store.RevalidatePreparedAsync(record, plan);
        });

        await check("pre-replace failure or cancellation keeps the Prepared live revision and permits a verified retry", async () =>
        {
            foreach (var cancel in new[] { false, true })
                foreach (var boundary in new[] { RecoveryManifestReplacementStage.BeforeTemporaryFile, RecoveryManifestReplacementStage.TemporaryWritten, RecoveryManifestReplacementStage.BeforeReplace })
                {
                    using var fixture = new Fixture();
                    using var cancelled = new CancellationTokenSource();
                    var failOnce = true;
                    var failure = new IOException("Injected before applying was persisted.");
                    var store = fixture.Store((stage, _) =>
                    {
                        if (stage != boundary || !failOnce) return Task.CompletedTask;
                        failOnce = false;
                        if (cancel) { cancelled.Cancel(); return Task.CompletedTask; }
                        return Task.FromException(failure);
                    });
                    var plan = await fixture.Plan();
                    var record = await store.PrepareAsync(plan);
                    var original = record.Manifest;
                    var identities = record.FileIdentities;
                    var before = fixture.Snapshot();
                    if (cancel) await Reject<OperationCanceledException>(() => store.BeginApplyingAsync(record, plan, cancelled.Token));
                    else Assert(ReferenceEquals(await Reject<IOException>(() => store.BeginApplyingAsync(record, plan)), failure), "Original error retained");
                    Assert(!record.IsInvalidated && ReferenceEquals(record.Manifest, original) && ReferenceEquals(record.FileIdentities, identities) && before.SequenceEqual(fixture.Snapshot()), "No premature in-memory state update");
                    await store.RevalidatePreparedAsync(record, plan);
                    await store.BeginApplyingAsync(record, plan);
                    Assert(record.Manifest.State == RecoveryRecordState.Applying && !record.IsInvalidated, "Retry advances a fully revalidated record");
                }
        });

        await check("late cancellation cannot split persisted Applying state from its live manifest and identity", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.Replaced, RecoveryManifestReplacementStage.BeforeReturn })
            {
                using var fixture = new Fixture();
                using var cancelled = new CancellationTokenSource();
                var store = fixture.Store((stage, _) => { if (stage == boundary) cancelled.Cancel(); return Task.CompletedTask; });
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                await store.BeginApplyingAsync(record, plan, cancelled.Token);
                Assert(cancelled.IsCancellationRequested && record.Manifest.State == RecoveryRecordState.Applying && !record.IsInvalidated, "Verified persistent success reported despite late cancellation");
                await store.RevalidatePreparedAsync(record, plan);
            }
        });

        await check("uncertain applying writes invalidate the live handle without promoting or rolling back persistent state", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.Replaced, RecoveryManifestReplacementStage.BeforeReturn })
            {
                using var fixture = new Fixture();
                Exception failure = boundary == RecoveryManifestReplacementStage.Replaced ? new IOException("Injected post-rename IO failure.") : new OperationCanceledException("Injected late cancellation failure.");
                var store = fixture.Store((stage, _) => stage == boundary ? Task.FromException(failure) : Task.CompletedTask);
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                var original = record.Manifest;
                var error = await Reject<RecoveryManifestReplacementException>(() => store.BeginApplyingAsync(record, plan));
                Assert(error.ReplacementAttempted && ReferenceEquals(error.InnerException, failure) && record.IsInvalidated && ReferenceEquals(record.Manifest, original), "Uncertain handle is disabled; no unverified live state advance");
                Assert((await store.LoadAsync(record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Applying, "Actual stored state remains inspectable");
                File.Delete(Path.Combine(fixture.Storage, RecoveryStorageLimits.LockFileName));
                await Reject<InvalidOperationException>(() => store.RevalidatePreparedAsync(record, plan));
                await Reject<InvalidOperationException>(() => store.BeginApplyingAsync(record, plan));
                Assert(!File.Exists(Path.Combine(fixture.Storage, RecoveryStorageLimits.LockFileName)), "Invalidated handle rejected before storage access");
            }
        });

        await check("incomplete temporary cleanup invalidates an applying attempt even before a rename was attempted", async () =>
        {
            using var fixture = new Fixture();
            string? temporary = null;
            var store = fixture.Store((stage, path) =>
            {
                if (stage != RecoveryManifestReplacementStage.BeforeReplace) return Task.CompletedTask;
                temporary = path;
                var bytes = File.ReadAllBytes(path);
                File.Move(path, Path.Combine(fixture.DirectoryPath, "detached-temporary"));
                File.WriteAllBytes(path, bytes);
                return Task.CompletedTask;
            });
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            var error = await Reject<RecoveryManifestReplacementException>(() => store.BeginApplyingAsync(record, plan));
            Assert(!error.ReplacementAttempted && !error.CleanupErrors.IsEmpty && record.IsInvalidated && record.Manifest.State == RecoveryRecordState.Prepared,
                "Incomplete cleanup cannot leave a reusable live handle");
            Assert(File.Exists(temporary), "Foreign replacement preserved");
            File.Delete(temporary!); // Test operator removes only its own replacement, not runtime cleanup.
            Assert((await store.LoadAsync(record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Prepared, "Original metadata still present");
            await Reject<InvalidOperationException>(() => store.BeginApplyingAsync(record, plan));
        });

        await check("queued duplicate begin-applying calls observe the completed revision and queued cancellation leaves the owner intact", async () =>
        {
            using var fixture = new Fixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var store = fixture.Store(async (stage, _) =>
            {
                if (stage != RecoveryManifestReplacementStage.TemporaryWritten) return;
                calls++;
                entered.TrySetResult();
                await release.Task;
            });
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            var first = store.BeginApplyingAsync(record, plan);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert(record.Manifest.State == RecoveryRecordState.Prepared, "No advance while replacement is unfinished");
                var duplicate = store.BeginApplyingAsync(record, plan);
                using var cancelled = new CancellationTokenSource();
                var queued = store.BeginApplyingAsync(record, plan, cancelled.Token);
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => queued);
                Assert(!first.IsCompleted && !duplicate.IsCompleted, "Active writer remains owned");
                release.TrySetResult();
                await first;
                await Reject<InvalidOperationException>(() => duplicate);
                Assert(calls == 1 && record.Manifest.State == RecoveryRecordState.Applying && !record.IsInvalidated, "Only one transition reached the writer");
                await store.RevalidatePreparedAsync(record, plan);
            }
            finally { release.TrySetResult(); await first; }
        });

        await check("begin-applying cannot promote a self-consistent state rewritten directly on disk", async () =>
        {
            using var fixture = new Fixture();
            var calls = 0;
            var store = fixture.Store((_, _) => { calls++; return Task.CompletedTask; });
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            foreach (var state in new[] { RecoveryRecordState.Applying, RecoveryRecordState.Committed, RecoveryRecordState.Incomplete })
            {
                File.WriteAllBytes(fixture.Manifest(record), RecoveryManifestJson.Serialize(record.Manifest with { State = state }));
                var before = fixture.Snapshot();
                await RejectInvalid(() => store.BeginApplyingAsync(record, plan));
                Assert(calls == 0 && record.Manifest.State == RecoveryRecordState.Prepared && before.SequenceEqual(fixture.Snapshot()), "Disk state is not a live transition capability");
            }
        });

        await check("applying manifest budget bounds every terminal state and new-file permission representation without IO", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Plan("*** Update File: sample.md\n@@\n-old\n+new\n*** Add File: empty.md");
            var applying = RecoveryDraftBuilder.Create(Guid.NewGuid(), plan).Manifest with { State = RecoveryRecordState.Applying };
            var maximum = RecoveryManifestBudget.MaximumFinalBytes(applying);
            foreach (var state in new[] { RecoveryRecordState.Committed, RecoveryRecordState.RolledBack, RecoveryRecordState.Incomplete })
                foreach (var mode in new[] { UnixFileMode.None, UnixFileMode.UserRead, (UnixFileMode)0xfff })
                {
                    var observed = applying.Paths.Select(path => path.After is { UnixModeIsCreateRequest: true } image
                        ? path with { After = image with { UnixMode = mode, UnixModeIsCreateRequest = false } } : path).ToImmutableArray();
                    var terminal = applying with { State = state, Paths = state == RecoveryRecordState.RolledBack ? applying.Paths : observed };
                    RecoveryManifestValidator.Validate(terminal, fixture.Root, applying.ChangeId);
                    Assert(RecoveryManifestJson.Serialize(terminal).Length <= maximum, "Maximum covers actual terminal encoding");
                }
            var before = fixture.Snapshot();
            _ = RecoveryManifestBudget.MaximumFinalBytes(applying);
            Assert(before.SequenceEqual(fixture.Snapshot()) && !Directory.Exists(fixture.Storage), "Sizing creates no recovery storage");
            RejectSync<InvalidOperationException>(() => RecoveryManifestBudget.MaximumFinalBytes(applying with { State = RecoveryRecordState.Prepared }));
            // Synthetic format boundary: storage semantics are validated separately before sizing.
            var oversized = applying with { Paths = [applying.Paths[0] with { Path = new string('a', RecoveryManifestJson.MaxManifestBytes) }] };
            RejectSync<JsonException>(() => RecoveryManifestBudget.MaximumFinalBytes(oversized));
        });

        await check("applying capacity preflight checks later replacement peak rather than only the immediate Applying file", () =>
        {
            var usage = new RecoveryStorageUsage(RecoveryStorageLimits.MaxStoreBytes - 1000, 1000);
            usage.EnsureCanReplaceManifest(2000, 499, 500);
            RejectSync<IOException>(() => usage.EnsureCanBeginApplying(2000, 499, 500, 1000));
            usage.EnsureCanBeginApplying(2000, 499, 500, 999);
            var fullRecord = new RecoveryStorageUsage(RecoveryStorageLimits.MaxRecordBytes, 1);
            fullRecord.EnsureCanReplaceManifest(RecoveryStorageLimits.MaxRecordBytes, 500, 500);
            RejectSync<IOException>(() => fullRecord.EnsureCanBeginApplying(RecoveryStorageLimits.MaxRecordBytes, 500, 500, 501));
            fullRecord.EnsureCanBeginApplying(RecoveryStorageLimits.MaxRecordBytes, 500, 500, 500);
            return Task.CompletedTask;
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Applying recovery state: " + message); }
    private static void RejectSync<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static async Task RejectInvalid(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected an invalid live recovery record.");
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-applying-state-{Guid.NewGuid():N}");
        public string Root { get; }
        public string Storage => Path.Combine(DirectoryPath, "archive", "recovery");
        private readonly ConfigurationPaths _paths;
        private readonly ProjectWorkspace _workspace;
        private readonly PatchService _patches;
        private readonly string _bash;
        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(DirectoryPath, "project")).FullName;
            Put("sample.md", "old\n");
            _workspace = new ProjectWorkspace(Root);
            _patches = new PatchService(_workspace);
            _paths = new ConfigurationPaths(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"));
            _bash = Path.Combine(DirectoryPath, "bash-fixture");
            File.WriteAllText(_bash, "Dependency fixture; never execute.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_bash, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        public RecoveryStore Store(Func<RecoveryManifestReplacementStage, string, Task>? hook = null) => new(Guid.NewGuid(), new ConfigurationValidator(_paths).Validate(
            new AppSettings { ProjectRoot = Root, BashPath = _bash, Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } },
            Path.Combine(DirectoryPath, "settings.json"), new ExecutionPolicy { Mode = ExecutionMode.FullAccess, RecoveryDirectory = Storage }), null, null, hook);
        public Task<PatchPlan> Plan(string body = "*** Update File: sample.md\n@@\n-old\n+new") => _patches.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch");
        public void Put(string path, string text) => File.WriteAllText(Path.Combine(Root, path), text, new UTF8Encoding(false));
        public string Manifest(RecoveryStore.PreparedRecord record) => Path.Combine(record.DirectoryPath, RecoveryStore.ManifestFileName);
        public string[] Backups(RecoveryStore.PreparedRecord record) => Directory.EnumerateFiles(record.DirectoryPath, "*").Where(path => Path.GetFileName(path).StartsWith("before-", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(path => Path.GetFileName(path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(DirectoryPath, path) + ":" + (Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public string[] ProjectSnapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Root, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
        public void Dispose() { _workspace.Mutations.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }
}
