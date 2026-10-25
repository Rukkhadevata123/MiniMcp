using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Services;

internal static class RecoveryLoadingChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("recovery loading preserves mixed original bytes and permits read-only inspection across runs", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("sample.md", "\ufeffuncommitted\r\nnext\n");
            fixture.Put("delete.md", "");
            fixture.Put("move.md", "before\n");
            var prepared = await fixture.Prepare("*** Update File: sample.md\n@@\n-uncommitted\n+changed\n*** Delete File: delete.md\n" +
                "*** Update File: move.md\n*** Move to: moved.md\n@@\n-before\n+after\n*** Add File: nested/new.md\n+added");
            fixture.Put("sample.md", "external edit\n");
            Directory.CreateDirectory(Path.Combine(fixture.Storage, "broken-neighbor"));
            File.WriteAllText(Path.Combine(fixture.Storage, "broken-neighbor", RecoveryStore.ManifestFileName), "invalid unrelated record");
            var before = fixture.Snapshot();
            var loaded = await fixture.Store(ExecutionMode.ReadOnly).LoadAsync(prepared.Manifest.ChangeId);
            Assert(loaded.Manifest.RunId == prepared.Manifest.RunId && loaded.Manifest.State == RecoveryRecordState.Prepared, "Historical run and state preserved");
            Assert(RecoveryManifestJson.Serialize(loaded.Manifest).SequenceEqual(RecoveryManifestJson.Serialize(prepared.Manifest)), "Complete metadata preserved");
            Assert(loaded.Backups.Length == 3 && loaded.Backups[0].Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("\ufeffuncommitted\r\nnext\n")) &&
                loaded.Backups[1].Bytes.IsEmpty && loaded.Backups[2].Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("before\n")), "Raw bytes, BOM, line endings and empty original preserved");
            Assert(before.SequenceEqual(fixture.Snapshot()), "Loading changes no workspace, record or marker bytes");
            File.WriteAllText(Path.Combine(prepared.DirectoryPath, loaded.Backups[0].FileName), "later disk change");
            Assert(loaded.Backups[0].Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("\ufeffuncommitted\r\nnext\n")), "Result owns immutable copies");
            Assert((object)loaded is not RecoveryStore.PreparedRecord, "A loaded record is not a live preparation handle");
        });

        await check("recovery loading supports add-only records and the exact file and operation limits", async () =>
        {
            using var fixture = new Fixture();
            var added = await fixture.Prepare("*** Add File: empty.md");
            Assert((await fixture.Store().LoadAsync(added.Manifest.ChangeId)).Backups.IsEmpty, "Absent originals require no backup");
            var bytes = Encoding.UTF8.GetBytes("\ufeff" + new string('a', ProjectWorkspace.MaxFileBytes - 3));
            File.WriteAllBytes(Path.Combine(fixture.Root, "large.md"), bytes);
            var large = await fixture.Prepare("*** Delete File: large.md");
            Assert((await fixture.Store().LoadAsync(large.Manifest.ChangeId)).Backups.Single().Bytes.AsSpan().SequenceEqual(bytes), "Exactly 256 KiB including BOM accepted");
            var operations = new StringBuilder();
            for (var i = 0; i < 64; i++) { fixture.Put($"f{i}.md", "x"); operations.Append($"*** Delete File: f{i}.md\n"); }
            var full = await fixture.Prepare(operations.ToString().TrimEnd('\n'));
            Assert((await fixture.Store().LoadAsync(full.Manifest.ChangeId)).Backups.Length == 64, "All 64 required backups accepted");
        });

        await check("recovery loading invalid IDs, missing storage, missing markers and pre-cancellation create nothing", async () =>
        {
            using var fixture = new Fixture();
            var store = fixture.Store(ExecutionMode.ReadOnly);
            await RejectAsync<ArgumentException>(() => store.LoadAsync(Guid.Empty));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await RejectAsync<OperationCanceledException>(() => store.LoadAsync(Guid.NewGuid(), cancelled.Token));
            await RejectAsync<IOException>(() => store.LoadAsync(Guid.NewGuid()));
            Assert(!Directory.Exists(fixture.Storage), "No storage was created");
            Directory.CreateDirectory(fixture.Storage);
            var before = fixture.Snapshot();
            await RejectAsync<IOException>(() => store.LoadAsync(Guid.NewGuid()));
            Assert(before.SequenceEqual(fixture.Snapshot()), "Missing lock marker was not created");
            var prepared = await fixture.Prepare();
            await RejectAsync<IOException>(() => store.LoadAsync(Guid.NewGuid()));
            Assert(Directory.Exists(prepared.DirectoryPath), "Missing selected record leaves others intact");
        });

        await check("recovery loading bounds manifest bytes and rejects malformed, truncated and invalid UTF-8 manifests", async () =>
        {
            using var fixture = new Fixture();
            var prepared = await fixture.Prepare();
            var path = Path.Combine(prepared.DirectoryPath, RecoveryStore.ManifestFileName);
            var valid = File.ReadAllBytes(path);
            var atLimit = new byte[RecoveryManifestJson.MaxManifestBytes];
            atLimit.AsSpan().Fill((byte)' ');
            valid.CopyTo(atLimit, 0);
            File.WriteAllBytes(path, atLimit);
            _ = await fixture.Store().LoadAsync(prepared.Manifest.ChangeId);
            foreach (var bad in new byte[][] { [], valid[..^5], [0xff], [0xef, 0xbb, 0xbf, .. valid],
                [.. atLimit, (byte)' '], Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("\"Prepared\"", "\"Unknown\"", StringComparison.Ordinal)),
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal)) })
            {
                File.WriteAllBytes(path, bad);
                await RejectInvalidRecord(() => fixture.Store().LoadAsync(prepared.Manifest.ChangeId));
                Assert(File.ReadAllBytes(path).SequenceEqual(bad), "Bad manifest preserved for local inspection");
            }
        });

        await check("recovery loading binds the selected ID and workspace and validates paths before opening backups", async () =>
        {
            using var fixture = new Fixture();
            var prepared = await fixture.Prepare();
            var original = prepared.Manifest;
            foreach (var manifest in new[] { original with { ChangeId = Guid.NewGuid() },
                original with { ProjectRoot = Path.Combine(fixture.DirectoryPath, "other") },
                original with { Paths = [original.Paths[0] with { BackupFile = "../outside.bin" }] },
                original with { Paths = [original.Paths[0] with { Path = "../outside.md" }] } })
            {
                fixture.SaveManifest(prepared, manifest);
                await RejectAsync<JsonException>(() => fixture.Store().LoadAsync(original.ChangeId));
            }
            fixture.SaveManifest(prepared, original);
            _ = await fixture.Store().LoadAsync(original.ChangeId);
        });

        await check("recovery loading rejects missing, extra, nested and noncanonical record entries", async () =>
        {
            foreach (var kind in new[] { "manifest", "backup", "extra", "directory", "case" })
            {
                using var fixture = new Fixture();
                var prepared = await fixture.Prepare();
                var backup = Path.Combine(prepared.DirectoryPath, "before-0000.bin");
                switch (kind)
                {
                    case "manifest": File.Delete(Path.Combine(prepared.DirectoryPath, RecoveryStore.ManifestFileName)); break;
                    case "backup": File.Delete(backup); break;
                    case "extra": File.WriteAllText(Path.Combine(prepared.DirectoryPath, "before-0063.bin"), "extra"); break;
                    case "directory": File.Delete(backup); Directory.CreateDirectory(backup); break;
                    case "case":
                        var bytes = File.ReadAllBytes(backup);
                        File.Delete(backup);
                        File.WriteAllBytes(Path.Combine(prepared.DirectoryPath, "Before-0000.bin"), bytes);
                        break;
                }
                var before = fixture.Snapshot();
                await RejectInvalidRecord(() => fixture.Store().LoadAsync(prepared.Manifest.ChangeId));
                Assert(before.SequenceEqual(fixture.Snapshot()), "Loading never repairs or deletes partial record data");
            }
        });

        await check("recovery loading rejects truncated, oversized and same-length corrupt backups", async () =>
        {
            using var fixture = new Fixture();
            var prepared = await fixture.Prepare();
            var backup = Path.Combine(prepared.DirectoryPath, "before-0000.bin");
            foreach (var bad in new[] { Encoding.UTF8.GetBytes("bad\n"), Encoding.UTF8.GetBytes("old"), new byte[ProjectWorkspace.MaxFileBytes + 1] })
            {
                File.WriteAllBytes(backup, bad);
                var before = fixture.Snapshot();
                await RejectAsync<InvalidDataException>(() => fixture.Store().LoadAsync(prepared.Manifest.ChangeId));
                Assert(before.SequenceEqual(fixture.Snapshot()), "Invalid backup bytes remain untouched");
            }
        });

        await check("recovery loading rejects NUL and invalid UTF-8 even with matching declared length and hash", async () =>
        {
            using var fixture = new Fixture();
            var prepared = await fixture.Prepare();
            foreach (var bytes in new byte[][] { [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xe2, 0x82], [(byte)'a', 0, (byte)'b'] })
            {
                File.WriteAllBytes(Path.Combine(prepared.DirectoryPath, "before-0000.bin"), bytes);
                var state = prepared.Manifest.Paths[0];
                fixture.SaveManifest(prepared, prepared.Manifest with { Paths = [state with { Before = state.Before! with {
                    ByteLength = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } }] });
                await RejectAsync<InvalidDataException>(() => fixture.Store().LoadAsync(prepared.Manifest.ChangeId));
            }
        });

        await check("recovery loading preserves every record state and refuses incomplete bytes without assuming a commit", async () =>
        {
            using var fixture = new Fixture();
            var prepared = await fixture.Prepare();
            foreach (var state in Enum.GetValues<RecoveryRecordState>())
            {
                fixture.SaveManifest(prepared, prepared.Manifest with { State = state });
                Assert((await fixture.Store().LoadAsync(prepared.Manifest.ChangeId)).Manifest.State == state, "State is retained, not inferred or promoted");
            }
            File.Delete(Path.Combine(prepared.DirectoryPath, "before-0000.bin"));
            await RejectAsync<InvalidDataException>(() => fixture.Store().LoadAsync(prepared.Manifest.ChangeId));
            Assert(File.ReadAllText(Path.Combine(fixture.Root, "sample.md")) == "old\n", "No state triggers workspace writes");
        });

        await check("recovery loading needs only read access and refuses busy or invalid store locks without modifying them", async () =>
        {
            using var fixture = new Fixture();
            var prepared = await fixture.Prepare();
            var files = Directory.EnumerateFiles(prepared.DirectoryPath).Append(Path.Combine(fixture.Storage, RecoveryStorageLimits.LockFileName)).ToArray();
            try
            {
                foreach (var file in files)
                    if (OperatingSystem.IsWindows()) File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
                    else File.SetUnixFileMode(file, UnixFileMode.UserRead);
                _ = await fixture.Store(ExecutionMode.ReadOnly).LoadAsync(prepared.Manifest.ChangeId);
                using (var held = RecoveryFileSystem.OpenRead(files[^1]))
                {
                    RecoveryFileSystem.Lock(held);
                    await RejectAsync<IOException>(() => fixture.Store().LoadAsync(prepared.Manifest.ChangeId));
                }
            }
            finally
            {
                foreach (var file in files)
                    if (OperatingSystem.IsWindows()) File.SetAttributes(file, FileAttributes.Normal);
                    else File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.WriteAllText(files[^1], "invalid marker");
            await RejectAsync<InvalidDataException>(() => fixture.Store().LoadAsync(prepared.Manifest.ChangeId));
            Assert(File.ReadAllText(files[^1]) == "invalid marker", "Bad marker is never truncated");
        });

        await check("recovery loading cancellation releases locks and preserves all record bytes at every checkpoint", async () =>
        {
            foreach (var stage in Enum.GetValues<RecoveryLoadingStage>())
            {
                using var fixture = new Fixture();
                var prepared = await fixture.Prepare();
                using var cancelled = new CancellationTokenSource();
                var before = fixture.Snapshot();
                var store = fixture.Store(hook: current => { if (stage == current) cancelled.Cancel(); return Task.CompletedTask; });
                await RejectAsync<OperationCanceledException>(() => store.LoadAsync(prepared.Manifest.ChangeId, cancelled.Token));
                Assert(before.SequenceEqual(fixture.Snapshot()), "Cancelled inspection is read-only");
                _ = await fixture.Store().LoadAsync(prepared.Manifest.ChangeId);
            }
        });

        await check("recovery loading detects replaced files, late same-file corruption and newly added entries", async () =>
        {
            foreach (var kind in new[] { "replace", "corrupt", "manifest", "extra", "directory" })
            {
                using var fixture = new Fixture();
                var prepared = await fixture.Prepare();
                var store = fixture.Store(hook: stage =>
                {
                    if (stage != RecoveryLoadingStage.BeforeReturn) return Task.CompletedTask;
                    var path = Path.Combine(prepared.DirectoryPath, "before-0000.bin");
                    if (kind == "replace") { File.Move(path, Path.Combine(fixture.DirectoryPath, "detached.bin")); File.WriteAllText(path, "old\n"); }
                    else if (kind == "corrupt") File.WriteAllText(path, "bad\n");
                    else if (kind == "manifest") fixture.SaveManifest(prepared, prepared.Manifest with { State = RecoveryRecordState.Committed });
                    else if (kind == "extra") File.WriteAllText(Path.Combine(prepared.DirectoryPath, "extra.bin"), "unowned");
                    else { Directory.Move(prepared.DirectoryPath, prepared.DirectoryPath + ".detached"); Directory.CreateDirectory(prepared.DirectoryPath); }
                    return Task.CompletedTask;
                });
                await RejectInvalidRecord(() => store.LoadAsync(prepared.Manifest.ChangeId));
                Assert(File.ReadAllText(Path.Combine(fixture.Root, "sample.md")) == "old\n", "Races never mutate workspace");
                Assert(Directory.Exists(prepared.DirectoryPath), "No cleanup is attempted by readers");
            }
        });

        await check("cancelled recovery readers leave an active writer and its record unchanged", async () =>
        {
            using var fixture = new Fixture();
            var prepared = await fixture.Prepare();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var activeStore = fixture.Store();
            // The same store gate protects inspection and preparation; keep a real writer inside it.
            var writer = fixture.Prepare(hook: async (stage, _) =>
            {
                if (stage == RecoveryPreparationStage.BackupWritten) { entered.TrySetResult(); await release.Task; }
            });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using var cancelled = new CancellationTokenSource();
                var reader = activeStore.LoadAsync(prepared.Manifest.ChangeId, cancelled.Token);
                cancelled.Cancel();
                await RejectAsync<OperationCanceledException>(() => reader);
                Assert(!writer.IsCompleted, "Cancelling an inspector does not interrupt the active writer");
                release.TrySetResult();
                var written = await writer;
                _ = await activeStore.LoadAsync(prepared.Manifest.ChangeId);
                _ = await activeStore.LoadAsync(written.Manifest.ChangeId);
            }
            finally { release.TrySetResult(); await writer; }
        });

        await check("recovery loading rejects directory, ancestor, backup and manifest links including dangling links", async () =>
        {
            using var fixture = new Fixture();
            var probe = Path.Combine(fixture.DirectoryPath, "probe");
            try { Directory.CreateSymbolicLink(probe, fixture.Root); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP recovery loading link check: symbolic link permission unavailable."); return; }
            Directory.Delete(probe);
            foreach (var kind in new[] { "record", "root", "ancestor", "backup", "manifest", "dangling" })
            {
                var prepared = await fixture.Prepare();
                var store = fixture.Store();
                var path = kind switch { "record" => prepared.DirectoryPath, "root" => fixture.Storage,
                    "ancestor" => Path.GetDirectoryName(fixture.Storage)!, "manifest" => Path.Combine(prepared.DirectoryPath, RecoveryStore.ManifestFileName),
                    _ => Path.Combine(prepared.DirectoryPath, "before-0000.bin") };
                var directory = Directory.Exists(path);
                var detached = Path.Combine(fixture.DirectoryPath, "detached-" + kind);
                if (directory) Directory.Move(path, detached); else File.Move(path, detached);
                var before = File.ReadAllText(Path.Combine(fixture.Root, "sample.md"));
                try
                {
                    var target = kind == "dangling" ? detached + ".missing" : detached;
                    if (directory) Directory.CreateSymbolicLink(path, target); else File.CreateSymbolicLink(path, target);
                    await RejectInvalidRecord(() => store.LoadAsync(prepared.Manifest.ChangeId));
                    Assert(File.ReadAllText(Path.Combine(fixture.Root, "sample.md")) == before, "Link target and workspace stay untouched");
                }
                finally
                {
                    if (directory) { Directory.Delete(path); Directory.Move(detached, path); }
                    else { File.Delete(path); File.Move(detached, path); }
                }
            }
        });

        await check("recovery loading refuses hard-linked backups and Linux FIFOs before content reads", async () =>
        {
            using var fixture = new Fixture();
            var prepared = await fixture.Prepare();
            var backup = Path.Combine(prepared.DirectoryPath, "before-0000.bin");
            var alias = Path.Combine(fixture.DirectoryPath, "backup-alias");
            var linked = OperatingSystem.IsWindows() ? CreateHardLink(alias, backup, IntPtr.Zero) : Link(backup, alias) == 0;
            Assert(linked, "Test hard link created");
            await RejectAsync<IOException>(() => fixture.Store().LoadAsync(prepared.Manifest.ChangeId));
            File.Delete(alias);
            _ = await fixture.Store().LoadAsync(prepared.Manifest.ChangeId);
            if (!OperatingSystem.IsLinux()) return;
            File.Delete(backup);
            Assert(MakeFifo(backup, 0x180) == 0, "Test FIFO created");
            await RejectAsync<IOException>(() => fixture.Store().LoadAsync(prepared.Manifest.ChangeId).WaitAsync(TimeSpan.FromSeconds(3)));
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Recovery loading: " + message); }
    private static async Task RejectInvalidRecord(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected invalid recovery record to be rejected.");
    }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string name, string existing, IntPtr attributes);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-recovery-loading-{Guid.NewGuid():N}");
        public string Root { get; }
        public string Storage => Path.Combine(DirectoryPath, "archive", "recovery");
        private readonly ProjectWorkspace _workspace;
        private readonly PatchService _patches;
        private readonly ConfigurationPaths _paths;
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
        public RecoveryStore Store(ExecutionMode mode = ExecutionMode.FullAccess, Func<RecoveryLoadingStage, Task>? hook = null,
            Func<RecoveryPreparationStage, RecoveryDraft, Task>? writeHook = null) =>
            new(Guid.NewGuid(), new ConfigurationValidator(_paths).Validate(new AppSettings { ProjectRoot = Root,
                BashPath = mode == ExecutionMode.ReadOnly ? "" : _bash, Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } },
                Path.Combine(DirectoryPath, "settings.json"), new ExecutionPolicy { Mode = mode, RecoveryDirectory = Storage }), writeHook, hook);
        public async Task<RecoveryStore.PreparedRecord> Prepare(string body = "*** Update File: sample.md\n@@\n-old\n+new",
            Func<RecoveryPreparationStage, RecoveryDraft, Task>? hook = null) =>
            await Store(writeHook: hook).PrepareAsync(await _patches.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch"));
        public void SaveManifest(RecoveryStore.PreparedRecord record, RecoveryManifest manifest) =>
            File.WriteAllBytes(Path.Combine(record.DirectoryPath, RecoveryStore.ManifestFileName), RecoveryManifestJson.Serialize(manifest));
        public void Put(string name, string value) => File.WriteAllText(Path.Combine(Root, name), value, new UTF8Encoding(false));
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(DirectoryPath, "*", SearchOption.AllDirectories).Order()
            .Select(path => Path.GetRelativePath(DirectoryPath, path) + ":" + (Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() { _workspace.Mutations.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }
}
