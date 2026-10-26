using MiniMcp.Services.Patching;
using MiniMcp.Services.Workspace;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;

internal static class RecoveryPreparationValidationChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("live recovery revalidation checks complete mixed records without writing or validating current project contents", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("sample.md", "\ufeffold\r\n");
            fixture.Put("empty.md", "");
            fixture.Put("move.md", "move\n");
            var plan = await fixture.Plan("*** Update File: sample.md\n@@\n-old\n+new\n*** Delete File: empty.md\n" +
                "*** Update File: move.md\n*** Move to: moved.md\n*** Add File: nested/new.md\n+added");
            var store = fixture.Store();
            var record = await store.PrepareAsync(plan);
            fixture.Put("sample.md", "later project edit\n");
            var before = fixture.Snapshot();
            await store.RevalidatePreparedAsync(record, plan);
            await store.RevalidatePreparedAsync(record, plan);
            Assert(record.FileIdentities.Count == 4 && record.Manifest.State == RecoveryRecordState.Prepared && before.SequenceEqual(fixture.Snapshot()), "Repeated validation preserves all data and the original state");
            Assert((await store.LoadAsync(record.Manifest.ChangeId)).Backups.Length == 3 && !Directory.Exists(Path.Combine(fixture.Root, "nested")), "No installation or new workspace directories");
            var added = await fixture.Plan("*** Add File: only-add.md\n+new");
            var addRecord = await store.PrepareAsync(added);
            await store.RevalidatePreparedAsync(addRecord, added);
            Assert(addRecord.FileIdentities.Count == 1, "Add-only preparation pins the manifest without fictitious backups");
        });

        await check("live recovery revalidation rejects foreign stores, cloned plans and invalid arguments before opening storage", async () =>
        {
            using var fixture = new Fixture();
            var store = fixture.Store();
            var other = fixture.Store();
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            File.Delete(Path.Combine(fixture.Storage, RecoveryStorageLimits.LockFileName));
            var before = fixture.Snapshot();
            await Reject<ArgumentNullException>(() => store.RevalidatePreparedAsync(null!, plan));
            await Reject<ArgumentNullException>(() => store.RevalidatePreparedAsync(record, null!));
            await Reject<InvalidOperationException>(() => other.RevalidatePreparedAsync(record, plan));
            await Reject<InvalidOperationException>(() => store.RevalidatePreparedAsync(record, new PatchPlan(plan.Owner, plan.Changes)));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Reject<OperationCanceledException>(() => store.RevalidatePreparedAsync(record, plan, cancelled.Token));
            await Reject<IOException>(() => store.RevalidatePreparedAsync(record, plan));
            Assert(before.SequenceEqual(fixture.Snapshot()), "No missing marker recreation or record changes");
        });

        await check("live recovery revalidation rejects identical-byte file replacements that remain independently inspectable", async () =>
        {
            foreach (var name in new[] { RecoveryStore.ManifestFileName, "before-0000.bin" })
            {
                using var fixture = new Fixture();
                var store = fixture.Store();
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                var path = Path.Combine(record.DirectoryPath, name);
                var bytes = File.ReadAllBytes(path);
                File.Move(path, Path.Combine(fixture.DirectoryPath, "detached-file"));
                File.WriteAllBytes(path, bytes);
                _ = await store.LoadAsync(record.Manifest.ChangeId);
                var before = fixture.Snapshot();
                await Reject<IOException>(() => store.RevalidatePreparedAsync(record, plan));
                Assert(before.SequenceEqual(fixture.Snapshot()), "A valid replacement is preserved, not promoted to live preparation");
            }
        });

        await check("live recovery revalidation pins both storage and record directory identities even when every original file survives", async () =>
        {
            foreach (var replaceRoot in new[] { false, true })
            {
                using var fixture = new Fixture();
                var store = fixture.Store();
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                var path = replaceRoot ? fixture.Storage : record.DirectoryPath;
                var detached = Path.Combine(fixture.DirectoryPath, "detached-directory");
                Directory.Move(path, detached);
                Directory.CreateDirectory(path);
                foreach (var entry in Directory.EnumerateFileSystemEntries(detached))
                {
                    var destination = Path.Combine(path, Path.GetFileName(entry));
                    if (Directory.Exists(entry)) Directory.Move(entry, destination); else File.Move(entry, destination);
                }
                _ = await store.LoadAsync(record.Manifest.ChangeId);
                var before = fixture.Snapshot();
                await Reject<IOException>(() => store.RevalidatePreparedAsync(record, plan));
                Assert(before.SequenceEqual(fixture.Snapshot()), "Same original files in a different directory do not reuse live preparation");
            }
        });

        await check("live recovery revalidation rejects self-consistent manifest and backup rewrites and format-only edits", async () =>
        {
            using var fixture = new Fixture();
            var store = fixture.Store();
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            var manifestPath = Path.Combine(record.DirectoryPath, RecoveryStore.ManifestFileName);
            var original = RecoveryManifestJson.Serialize(record.Manifest);
            foreach (var changed in new[] { record.Manifest with { State = RecoveryRecordState.Applying },
                record.Manifest with { RunId = Guid.NewGuid() }, record.Manifest with { CreatedAtUtc = record.Manifest.CreatedAtUtc.AddSeconds(1) },
                record.Manifest with { Paths = [record.Manifest.Paths[0] with { After = record.Manifest.Paths[0].After! with { Sha256 = Hash("alt\n") } }] } })
            {
                File.WriteAllBytes(manifestPath, RecoveryManifestJson.Serialize(changed));
                _ = await store.LoadAsync(record.Manifest.ChangeId);
                await Reject<InvalidDataException>(() => store.RevalidatePreparedAsync(record, plan));
            }
            File.WriteAllBytes(manifestPath, [.. original, (byte)' ']);
            _ = await store.LoadAsync(record.Manifest.ChangeId);
            await Reject<InvalidDataException>(() => store.RevalidatePreparedAsync(record, plan));
            var state = record.Manifest.Paths[0];
            File.WriteAllText(Path.Combine(record.DirectoryPath, "before-0000.bin"), "alt\n", new UTF8Encoding(false));
            File.WriteAllBytes(manifestPath, RecoveryManifestJson.Serialize(record.Manifest with {
                Paths = [state with { Before = state.Before! with { Sha256 = Hash("alt\n") } }] }));
            _ = await store.LoadAsync(record.Manifest.ChangeId);
            var before = fixture.Snapshot();
            await Reject<InvalidDataException>(() => store.RevalidatePreparedAsync(record, plan));
            Assert(before.SequenceEqual(fixture.Snapshot()), "Even mutually consistent replacement metadata and bytes remain unmodified");
        });

        await check("live recovery revalidation rejects damaged or partial records without repairing or deleting them", async () =>
        {
            foreach (var kind in new[] { "missing", "extra", "hash", "manifest" })
            {
                using var fixture = new Fixture();
                var store = fixture.Store();
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                var backup = Path.Combine(record.DirectoryPath, "before-0000.bin");
                if (kind == "missing") File.Delete(backup);
                else if (kind == "extra") File.WriteAllText(Path.Combine(record.DirectoryPath, "foreign"), "preserve");
                else if (kind == "hash") File.WriteAllText(backup, "bad\n");
                else File.WriteAllText(Path.Combine(record.DirectoryPath, RecoveryStore.ManifestFileName), "{\"state\":");
                var before = fixture.Snapshot();
                await RejectInvalid(() => store.RevalidatePreparedAsync(record, plan));
                Assert(before.SequenceEqual(fixture.Snapshot()), "Invalid data stays available for inspection");
            }
        });

        await check("live recovery revalidation cancellation releases locks at every loading stage and preserves original bytes", async () =>
        {
            foreach (var stage in Enum.GetValues<RecoveryLoadingStage>())
            {
                using var fixture = new Fixture();
                using var cancelled = new CancellationTokenSource();
                var store = fixture.Store(current => { if (stage == current) cancelled.Cancel(); return Task.CompletedTask; });
                var plan = await fixture.Plan();
                var record = await store.PrepareAsync(plan);
                var before = fixture.Snapshot();
                await Reject<OperationCanceledException>(() => store.RevalidatePreparedAsync(record, plan, cancelled.Token));
                Assert(before.SequenceEqual(fixture.Snapshot()), "Cancellation is entirely read-only");
                await store.RevalidatePreparedAsync(record, plan);
            }
        });

        await check("live recovery revalidation supports read-only files and releases its process gate after a busy file lock", async () =>
        {
            using var fixture = new Fixture();
            var store = fixture.Store();
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            var marker = Path.Combine(fixture.Storage, RecoveryStorageLimits.LockFileName);
            var files = Directory.EnumerateFiles(record.DirectoryPath).Append(marker).ToArray();
            try
            {
                foreach (var file in files)
                    if (OperatingSystem.IsWindows()) File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
                    else File.SetUnixFileMode(file, UnixFileMode.UserRead);
                await store.RevalidatePreparedAsync(record, plan);
                using (var held = RecoveryFileSystem.OpenRead(marker))
                {
                    RecoveryFileSystem.Lock(held);
                    await Reject<IOException>(() => store.RevalidatePreparedAsync(record, plan));
                }
                await store.RevalidatePreparedAsync(record, plan);
            }
            finally
            {
                foreach (var file in files)
                    if (OperatingSystem.IsWindows()) File.SetAttributes(file, FileAttributes.Normal);
                    else File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        });

        await check("queued recovery revalidation cancellation does not interrupt the inspector holding the store lock", async () =>
        {
            using var fixture = new Fixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var store = fixture.Store(async stage => { if (stage == RecoveryLoadingStage.BeforeReturn) { entered.TrySetResult(); await release.Task; } });
            var plan = await fixture.Plan();
            var record = await store.PrepareAsync(plan);
            var first = store.RevalidatePreparedAsync(record, plan);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using var cancelled = new CancellationTokenSource();
                var queued = store.RevalidatePreparedAsync(record, plan, cancelled.Token);
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => queued);
                Assert(!first.IsCompleted, "Active inspection retains ownership");
                release.TrySetResult();
                await first;
                await store.RevalidatePreparedAsync(record, plan);
            }
            finally { release.TrySetResult(); await first; }
        });
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Prepared recovery validation: " + message); }
    private static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static async Task RejectInvalid(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected invalid prepared record.");
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-preparation-validation-{Guid.NewGuid():N}");
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
        public RecoveryStore Store(Func<RecoveryLoadingStage, Task>? hook = null) => new(Guid.NewGuid(), new ConfigurationValidator(_paths).Validate(
            new AppSettings { ProjectRoot = Root, BashPath = _bash, Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } },
            Path.Combine(DirectoryPath, "settings.json"), new ExecutionPolicy { Mode = ExecutionMode.FullAccess, RecoveryDirectory = Storage }), null, hook);
        public Task<PatchPlan> Plan(string body = "*** Update File: sample.md\n@@\n-old\n+new") =>
            _patches.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch");
        public void Put(string name, string text) => File.WriteAllText(Path.Combine(Root, name), text, new UTF8Encoding(false));
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(DirectoryPath, path) + ":" + (Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() { _workspace.Mutations.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }
}
