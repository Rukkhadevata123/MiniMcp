using MiniMcp.Services.Patching;
using MiniMcp.Services.Workspace;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;

internal static class RecoveryManifestReplacementChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("manifest replacement preserves mixed backups and changes only verified storage metadata", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("sample.md", "\ufeffold\r\n");
            fixture.Put("empty.md", "");
            fixture.Put("move.md", "move\n");
            var plan = await fixture.Plan("*** Update File: sample.md\n@@\n-old\n+new\n*** Delete File: empty.md\n" +
                "*** Update File: move.md\n*** Move to: moved.md\n*** Add File: nested/new.md\n+added");
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            var before = fixture.ProjectSnapshot();
            var backups = fixture.Backups(record);
            var next = record.Manifest with { State = RecoveryRecordState.Applying };
            var result = await store.ReplaceManifestAsync(record, plan, next);
            Assert(result.Manifest == next && result.Identity != record.FileIdentities[RecoveryStore.ManifestFileName], "A new verified file identity is returned");
            Assert((await store.LoadAsync(next.ChangeId)).Manifest.State == RecoveryRecordState.Applying &&
                File.ReadAllBytes(fixture.Manifest(record)).SequenceEqual(RecoveryManifestJson.Serialize(next)), "Complete replacement bytes loaded");
            Assert(before.SequenceEqual(fixture.ProjectSnapshot()) && backups.SequenceEqual(fixture.Backups(record)) && !fixture.Temporaries(record).Any(), "Workspace and all original backups preserved");
            Assert(record.Manifest.State == RecoveryRecordState.Prepared, "Storage primitive does not advance live state or claim a workspace commit");
            await Reject<IOException>(() => store.RevalidatePreparedAsync(record, plan));
            await Reject<IOException>(() => store.ReplaceManifestAsync(record, plan, next));
        });

        await check("manifest replacement rejects foreign ownership, invalid and changed payloads before creating a temporary file", async () =>
        {
            using var fixture = new Fixture();
            var calls = 0;
            var store = fixture.Store((_, _) => { calls++; return Task.CompletedTask; });
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            var next = record.Manifest with { State = RecoveryRecordState.Applying };
            var other = fixture.Store();
            var before = fixture.Snapshot();
            await Reject<ArgumentNullException>(() => store.ReplaceManifestAsync(null!, plan, next));
            await Reject<ArgumentNullException>(() => store.ReplaceManifestAsync(record, null!, next));
            await Reject<ArgumentNullException>(() => store.ReplaceManifestAsync(record, plan, null!));
            await Reject<InvalidOperationException>(() => other.ReplaceManifestAsync(record, plan, next));
            await Reject<InvalidOperationException>(() => store.ReplaceManifestAsync(record, new PatchPlan(plan.Owner, plan.Changes), next));
            foreach (var bad in new[] { next with { RunId = Guid.NewGuid() }, next with { ChangeId = Guid.NewGuid() },
                next with { CreatedAtUtc = next.CreatedAtUtc.AddSeconds(1) }, next with { State = (RecoveryRecordState)99 },
                next with { Paths = [next.Paths[0] with { BackupFile = "../secret" }] },
                next with { Paths = [next.Paths[0] with { After = next.Paths[0].After! with { ByteLength = 5 } }] } })
                await RejectInvalid(() => store.ReplaceManifestAsync(record, plan, bad));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Reject<OperationCanceledException>(() => store.ReplaceManifestAsync(record, plan, next, cancelled.Token));
            Assert(calls == 0 && before.SequenceEqual(fixture.Snapshot()), "No write checkpoint or filesystem change");
            if (OperatingSystem.IsLinux())
            {
                var addPlan = await fixture.Plan("*** Add File: added.md\n+new");
                var added = await store.PrepareAsync(addPlan);
                var path = added.Manifest.Paths[0];
                var changedRequest = added.Manifest with { State = RecoveryRecordState.Applying,
                    Paths = [path with { After = path.After! with { UnixMode = path.After!.UnixMode!.Value | UnixFileMode.UserExecute } }] };
                await Reject<InvalidDataException>(() => store.ReplaceManifestAsync(added, addPlan, changedRequest));
                Assert(calls == 0 && !fixture.Temporaries(added).Any(), "Only actual observations may replace a creation request, never a different request");
            }
        });

        await check("manifest replacement capacity bounds both staged peak and final record sizes without requiring a new slot", () =>
        {
            var usage = new RecoveryStorageUsage(RecoveryStorageLimits.MaxStoreBytes - 100, 1000);
            usage.EnsureCanReplaceManifest(500, 100, 100);
            RejectSync<IOException>(() => usage.EnsureCanReplaceManifest(500, 100, 101));
            new RecoveryStorageUsage(RecoveryStorageLimits.MaxRecordBytes, 1).EnsureCanReplaceManifest(RecoveryStorageLimits.MaxRecordBytes, 100, 100);
            RejectSync<IOException>(() => new RecoveryStorageUsage(RecoveryStorageLimits.MaxRecordBytes, 1).EnsureCanReplaceManifest(RecoveryStorageLimits.MaxRecordBytes, 100, 101));
            foreach (var invalid in new[] { new RecoveryStorageUsage(long.MaxValue, 1), new RecoveryStorageUsage(10, 0),
                new RecoveryStorageUsage(10, 1001), new RecoveryStorageUsage(4, 1) })
                RejectSync<InvalidDataException>(() => invalid.EnsureCanReplaceManifest(10, 5, 5));
            foreach (var sizes in new[] { (0L, 1, 1), (10L, 0, 1), (10L, 1, 0), (10L, 1, RecoveryManifestJson.MaxManifestBytes + 1) })
                RejectSync<ArgumentOutOfRangeException>(() => new RecoveryStorageUsage(100, 1).EnsureCanReplaceManifest(sizes.Item1, sizes.Item2, sizes.Item3));
            return Task.CompletedTask;
        });

        await check("manifest replacement still works in an existing thousandth record slot", async () =>
        {
            using var fixture = new Fixture();
            var store = fixture.Store();
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            for (var i = 0; i < 999; i++) Directory.CreateDirectory(Path.Combine(fixture.Storage, $"old-{i}"));
            _ = await store.ReplaceManifestAsync(record, plan, record.Manifest with { State = RecoveryRecordState.Applying });
            Assert(Directory.EnumerateDirectories(fixture.Storage).Count() == 1000 && !fixture.Temporaries(record).Any(), "No extra slot or old-record deletion");
        });

        await check("cancelling before the replace attempt preserves the original manifest and safely removes owned temporary files", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.BeforeTemporaryFile, RecoveryManifestReplacementStage.TemporaryWritten, RecoveryManifestReplacementStage.BeforeReplace })
            {
                using var fixture = new Fixture();
                using var cancelled = new CancellationTokenSource();
                var store = fixture.Store((stage, _) => { if (stage == boundary) cancelled.Cancel(); return Task.CompletedTask; });
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                var before = fixture.Snapshot();
                await Reject<OperationCanceledException>(() => store.ReplaceManifestAsync(record, plan, record.Manifest with { State = RecoveryRecordState.Applying }, cancelled.Token));
                Assert(before.SequenceEqual(fixture.Snapshot()), "Cancellation restores the pre-replacement file layout");
                await store.RevalidatePreparedAsync(record, plan);
            }
        });

        await check("late cancellation completes manifest replacement and its readback instead of reporting cancellation", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.Replaced, RecoveryManifestReplacementStage.BeforeReturn })
            {
                using var fixture = new Fixture();
                using var cancelled = new CancellationTokenSource();
                var store = fixture.Store((stage, _) => { if (stage == boundary) cancelled.Cancel(); return Task.CompletedTask; });
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                var result = await store.ReplaceManifestAsync(record, plan, record.Manifest with { State = RecoveryRecordState.Applying }, cancelled.Token);
                Assert(cancelled.IsCancellationRequested && result.Manifest.State == RecoveryRecordState.Applying &&
                    (await store.LoadAsync(record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Applying, "Late cancellation does not hide completed metadata writes");
            }
        });

        await check("injected pre-replacement IO failures preserve the original error, backups and retryable preparation", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.BeforeTemporaryFile, RecoveryManifestReplacementStage.TemporaryWritten, RecoveryManifestReplacementStage.BeforeReplace })
            {
                using var fixture = new Fixture();
                var failure = new IOException("Injected write/flush failure.");
                var store = fixture.Store((stage, _) => stage == boundary ? Task.FromException(failure) : Task.CompletedTask);
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                var before = fixture.Snapshot();
                Assert(ReferenceEquals(await Reject<IOException>(() => store.ReplaceManifestAsync(record, plan, record.Manifest with { State = RecoveryRecordState.Applying })), failure), "Original IO error retained");
                Assert(before.SequenceEqual(fixture.Snapshot()), "No manifest or backup changes");
                await store.RevalidatePreparedAsync(record, plan);
            }
        });

        await check("manifest replacement never overwrites a colliding temporary file", async () =>
        {
            using var fixture = new Fixture();
            string? collision = null;
            var store = fixture.Store((stage, path) =>
            {
                if (stage == RecoveryManifestReplacementStage.BeforeTemporaryFile) { collision = path; File.WriteAllText(path, "foreign data"); }
                return Task.CompletedTask;
            });
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            var manifest = File.ReadAllBytes(fixture.Manifest(record));
            var backups = fixture.Backups(record);
            await Reject<IOException>(() => store.ReplaceManifestAsync(record, plan, record.Manifest with { State = RecoveryRecordState.Applying }));
            Assert(File.ReadAllText(collision!) == "foreign data" && manifest.SequenceEqual(File.ReadAllBytes(fixture.Manifest(record))) &&
                backups.SequenceEqual(fixture.Backups(record)), "Unowned collision and original data preserved");
        });

        await check("manifest replacement rechecks old metadata, backups, owned temporary bytes and file identities before renaming", async () =>
        {
            foreach (var kind in new[] { "manifest", "backup", "temporary", "temporary-replaced", "extra" })
            {
                using var fixture = new Fixture();
                RecoveryStore.PreparedRecord? record = null;
                var store = fixture.Store((stage, path) =>
                {
                    if (stage != RecoveryManifestReplacementStage.BeforeReplace) return Task.CompletedTask;
                    if (kind == "manifest") File.WriteAllText(fixture.Manifest(record!), "external manifest");
                    else if (kind == "backup") File.WriteAllText(Path.Combine(record!.DirectoryPath, "before-0000.bin"), "bad\n");
                    else if (kind == "temporary") File.WriteAllText(path, "corrupt temporary");
                    else if (kind == "temporary-replaced") { var bytes = File.ReadAllBytes(path); File.Move(path, Path.Combine(fixture.DirectoryPath, "detached")); File.WriteAllBytes(path, bytes); }
                    else File.WriteAllText(Path.Combine(record!.DirectoryPath, "foreign"), "preserve");
                    return Task.CompletedTask;
                });
                var plan = await fixture.Plan();
                record = await store.PrepareAsync(plan);
                var original = File.ReadAllBytes(fixture.Manifest(record));
                var error = await RejectInvalid(() => store.ReplaceManifestAsync(record, plan, record.Manifest with { State = RecoveryRecordState.Applying }));
                Assert(error is not RecoveryManifestReplacementException { ReplacementAttempted: true }, "No replace call after failed revalidation");
                Assert(kind == "manifest" ? File.ReadAllText(fixture.Manifest(record)) == "external manifest" : original.SequenceEqual(File.ReadAllBytes(fixture.Manifest(record))), "Existing manifest never overwritten");
                if (kind == "temporary-replaced")
                    Assert(error is RecoveryManifestReplacementException { ReplacementAttempted: false } failed && !failed.CleanupErrors.IsEmpty && fixture.Temporaries(record).Any(), "Cleanup refuses a different file at the temporary path");
            }
        });

        await check("manifest replacement cleanup refuses directory links and preserves both detached files and link targets", async () =>
        {
            using var fixture = new Fixture();
            var outside = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "sentinel"), "outside data");
            var probe = Path.Combine(fixture.DirectoryPath, "probe");
            try { Directory.CreateSymbolicLink(probe, outside); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP manifest replacement link check: symbolic link permission unavailable."); return; }
            Directory.Delete(probe);
            RecoveryStore.PreparedRecord? record = null;
            var detached = Path.Combine(fixture.DirectoryPath, "detached-record");
            var store = fixture.Store((stage, _) =>
            {
                if (stage == RecoveryManifestReplacementStage.TemporaryWritten) { Directory.Move(record!.DirectoryPath, detached); Directory.CreateSymbolicLink(record.DirectoryPath, outside); }
                return Task.CompletedTask;
            });
            var plan = await fixture.Plan();
            record = await store.PrepareAsync(plan);
            try
            {
                var error = await Reject<RecoveryManifestReplacementException>(() => store.ReplaceManifestAsync(record, plan, record.Manifest with { State = RecoveryRecordState.Applying }));
                Assert(!error.ReplacementAttempted && !error.CleanupErrors.IsEmpty && File.ReadAllText(Path.Combine(outside, "sentinel")) == "outside data", "Link target untouched");
                Assert(Directory.EnumerateFiles(detached).Count() == 3, "Original manifest, backup and tracked temporary preserved");
            }
            finally { Directory.Delete(record.DirectoryPath); Directory.Move(detached, record.DirectoryPath); }
        });

        if (OperatingSystem.IsWindows())
            await check("a real native replacement error retains the original manifest and staged evidence and releases the lock", async () =>
            {
                using var fixture = new Fixture();
                RecoveryStore.PreparedRecord? record = null;
                var store = fixture.Store((stage, _) =>
                {
                    if (stage == RecoveryManifestReplacementStage.BeforeReplace) File.SetAttributes(fixture.Manifest(record!), FileAttributes.ReadOnly);
                    return Task.CompletedTask;
                });
                var plan = await fixture.Plan();
                record = await store.PrepareAsync(plan);
                var original = File.ReadAllBytes(fixture.Manifest(record));
                try
                {
                    var error = await Reject<RecoveryManifestReplacementException>(() => store.ReplaceManifestAsync(record, plan, record.Manifest with { State = RecoveryRecordState.Applying }));
                    Assert(error.ReplacementAttempted && error.InnerException is IOException && fixture.Temporaries(record).Single() == error.TemporaryPath &&
                        original.SequenceEqual(File.ReadAllBytes(fixture.Manifest(record))), "Attempted rename is reported conservatively and staging evidence remains");
                    using var held = RecoveryFileSystem.OpenRead(Path.Combine(fixture.Storage, RecoveryStorageLimits.LockFileName));
                    RecoveryFileSystem.Lock(held);
                }
                finally { File.SetAttributes(fixture.Manifest(record), FileAttributes.Normal); }
            });

        await check("post-replacement faults report uncertainty and never restore or delete an already changed manifest", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.Replaced, RecoveryManifestReplacementStage.BeforeReturn })
            {
                using var fixture = new Fixture();
                Exception failure = boundary == RecoveryManifestReplacementStage.Replaced
                    ? new IOException("Injected post-replace flush/readback failure.")
                    : new OperationCanceledException("Injected cancellation error after replacement.");
                var store = fixture.Store((stage, _) => stage == boundary ? Task.FromException(failure) : Task.CompletedTask);
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                var backups = fixture.Backups(record);
                var error = await Reject<RecoveryManifestReplacementException>(() => store.ReplaceManifestAsync(record, plan, record.Manifest with { State = RecoveryRecordState.Applying }));
                Assert(error.ReplacementAttempted && ReferenceEquals(error.InnerException, failure) && backups.SequenceEqual(fixture.Backups(record)) &&
                    (await store.LoadAsync(record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Applying && !fixture.Temporaries(record).Any(), "Changed persistent state is preserved and inspectable");
                Assert(record.Manifest.State == RecoveryRecordState.Prepared, "Failure never advances the live descriptor");
            }
        });

        await check("post-replacement readback detects late manifest corruption and identical-byte backup replacements", async () =>
        {
            foreach (var backup in new[] { false, true })
            {
                using var fixture = new Fixture();
                RecoveryStore.PreparedRecord? record = null;
                var store = fixture.Store((stage, _) =>
                {
                    if (stage != RecoveryManifestReplacementStage.BeforeReturn) return Task.CompletedTask;
                    var path = backup ? Path.Combine(record!.DirectoryPath, "before-0000.bin") : fixture.Manifest(record!);
                    if (backup) { var bytes = File.ReadAllBytes(path); File.Move(path, Path.Combine(fixture.DirectoryPath, "old-backup")); File.WriteAllBytes(path, bytes); }
                    else File.WriteAllText(path, "corrupt after rename");
                    return Task.CompletedTask;
                });
                var plan = await fixture.Plan();
                record = await store.PrepareAsync(plan);
                var error = await Reject<RecoveryManifestReplacementException>(() => store.ReplaceManifestAsync(record, plan, record.Manifest with { State = RecoveryRecordState.Applying }));
                Assert(error.ReplacementAttempted && (backup ? File.ReadAllText(Path.Combine(record.DirectoryPath, "before-0000.bin")) == "old\n" :
                    File.ReadAllText(fixture.Manifest(record)) == "corrupt after rename"), "No automatic rollback or data deletion after uncertainty");
            }
        });

        await check("queued manifest cancellation does not interrupt an active replacement or reuse its stale preparation", async () =>
        {
            using var fixture = new Fixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var store = fixture.Store(async (stage, _) => { if (stage == RecoveryManifestReplacementStage.TemporaryWritten) { entered.TrySetResult(); await release.Task; } });
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            var next = record.Manifest with { State = RecoveryRecordState.Applying };
            var active = store.ReplaceManifestAsync(record, plan, next);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using var cancelled = new CancellationTokenSource();
                var queued = store.ReplaceManifestAsync(record, plan, next, cancelled.Token);
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => queued);
                Assert(!active.IsCompleted, "Another waiter cannot interrupt the lock owner");
                release.TrySetResult();
                _ = await active;
                await Reject<IOException>(() => store.ReplaceManifestAsync(record, plan, next));
                Assert(!fixture.Temporaries(record).Any(), "No second staging attempt from stale preparation");
            }
            finally { release.TrySetResult(); await active; }
        });

        await check("manifest replacement observes unexpected storage usage before and after the rename without deleting foreign data", async () =>
        {
            foreach (var boundary in new[] { RecoveryManifestReplacementStage.BeforeReplace, RecoveryManifestReplacementStage.BeforeReturn })
            {
                using var fixture = new Fixture();
                var foreign = Path.Combine(fixture.Storage, "foreign");
                var store = fixture.Store((stage, _) => { if (stage == boundary) File.WriteAllText(foreign, "preserve"); return Task.CompletedTask; });
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                var error = await Reject<IOException>(() => store.ReplaceManifestAsync(record, plan, record.Manifest with { State = RecoveryRecordState.Applying }));
                Assert((error is RecoveryManifestReplacementException { ReplacementAttempted: true }) == (boundary == RecoveryManifestReplacementStage.BeforeReturn), "Correctly distinguishes pre-rename failure and later uncertainty");
                Assert(File.ReadAllText(foreign) == "preserve" && !fixture.Temporaries(record).Any(), "Unrelated data preserved");
                Assert((await store.LoadAsync(record.Manifest.ChangeId)).Manifest.State == (boundary == RecoveryManifestReplacementStage.BeforeReturn ? RecoveryRecordState.Applying : RecoveryRecordState.Prepared), "State reflects the actual rename boundary");
            }
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Manifest replacement: " + message); }
    private static void RejectSync<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static async Task<Exception> RejectInvalid(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return error; }
        throw new Exception("Expected invalid manifest replacement.");
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-manifest-replacement-{Guid.NewGuid():N}");
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
        public string[] Backups(RecoveryStore.PreparedRecord record) => Directory.EnumerateFiles(record.DirectoryPath, "before-*.bin").Order(StringComparer.Ordinal)
            .Select(path => Path.GetFileName(path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
        public IEnumerable<string> Temporaries(RecoveryStore.PreparedRecord record) => Directory.EnumerateFiles(record.DirectoryPath, ".manifest-*.tmp");
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(DirectoryPath, path) + ":" + (Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public string[] ProjectSnapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Root, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
        public void Dispose() { _workspace.Mutations.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }
}
