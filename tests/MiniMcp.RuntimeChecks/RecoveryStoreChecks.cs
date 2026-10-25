using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Services;

internal static class RecoveryStoreChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("recovery preparation persists complete original bytes and manifest without changing the workspace", async () =>
        {
            using var fixture = new Fixture();
            fixture.PutProject("sample.md", "\ufeffuncommitted\r\nnext\n");
            fixture.PutProject("deleted.md", "gone\n");
            fixture.PutProject("moving.md", "before\n");
            var plan = await fixture.Plan("*** Update File: sample.md\n@@\n-uncommitted\n+changed\n*** Delete File: deleted.md\n" +
                "*** Update File: moving.md\n*** Move to: moved.md\n@@\n-before\n+after\n*** Add File: nested/empty.md");
            fixture.PutProject("sample.md", "external edit after planning\n");
            var before = fixture.ProjectSnapshot();
            var store = fixture.Store();
            var prepared = await store.PrepareAsync(plan);
            store.ValidateOwnership(prepared, plan);
            Reject<InvalidOperationException>(() => fixture.Store().ValidateOwnership(prepared, plan));
            Reject<InvalidOperationException>(() => store.ValidateOwnership(prepared, new PatchPlan(plan.Owner, plan.Changes)));
            var manifest = RecoveryManifestJson.Parse(await File.ReadAllBytesAsync(Path.Combine(prepared.DirectoryPath, RecoveryStore.ManifestFileName)));
            RecoveryManifestValidator.Validate(manifest, fixture.Root, prepared.Manifest.ChangeId);
            Assert(manifest.RunId == fixture.RunId && manifest.State == RecoveryRecordState.Prepared && manifest.Operations.Length == 4, "Stored manifest identity/state");
            Assert(File.ReadAllBytes(Path.Combine(prepared.DirectoryPath, "before-0000.bin")).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("\ufeffuncommitted\r\nnext\n")), "BOM, line endings and uncommitted original preserved");
            Assert(File.ReadAllText(Path.Combine(prepared.DirectoryPath, "before-0001.bin")) == "gone\n" &&
                File.ReadAllText(Path.Combine(prepared.DirectoryPath, "before-0002.bin")) == "before\n", "Delete and move backups");
            Assert(Directory.EnumerateFiles(prepared.DirectoryPath).Count() == 4 && before.SequenceEqual(fixture.ProjectSnapshot()), "Workspace and absent destinations unchanged");
            if (OperatingSystem.IsLinux())
            {
                Assert(File.GetUnixFileMode(prepared.DirectoryPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Private record directory");
                Assert(File.GetUnixFileMode(Path.Combine(prepared.DirectoryPath, "before-0000.bin")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Private backup permissions");
            }
        });

        await check("recovery preparation denies read-only, wrong-workspace and pre-cancelled requests before storage creation", async () =>
        {
            using var fixture = new Fixture();
            using var other = new Fixture();
            var plan = await fixture.Plan();
            var before = fixture.ProjectSnapshot();
            await RejectAsync<WorkspaceException>(() => fixture.Store(mode: ExecutionMode.ReadOnly).PrepareAsync(plan));
            var otherPlan = await other.Plan();
            await RejectAsync<System.Text.Json.JsonException>(() => fixture.Store().PrepareAsync(otherPlan));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await RejectAsync<OperationCanceledException>(() => fixture.Store().PrepareAsync(plan, cancelled.Token));
            Assert(!Directory.Exists(fixture.Storage) && before.SequenceEqual(fixture.ProjectSnapshot()), "Rejected requests have no preparation artifacts");
            if (OperatingSystem.IsLinux())
            {
                Directory.CreateDirectory(fixture.Storage);
                var marker = Path.Combine(fixture.Storage, RecoveryStorageLimits.LockFileName);
                if (MakeFifo(marker, 0x180) != 0) throw new Exception("Cannot create FIFO fixture.");
                try { await RejectAsync<IOException>(() => fixture.Store().PrepareAsync(plan)).WaitAsync(TimeSpan.FromSeconds(3)); }
                finally { File.Delete(marker); }
                Assert(!fixture.RecordDirectories().Any(), "Non-regular marker rejected without a blocking open or a record");
            }
        });

        await check("cancelling any recovery preparation phase removes only its new record and leaves existing records intact", async () =>
        {
            foreach (var stage in Enum.GetValues<RecoveryPreparationStage>())
            {
                using var fixture = new Fixture();
                fixture.PutStorage("old-record/keep", "old data");
                using var cancelled = new CancellationTokenSource();
                var store = fixture.Store((current, _) => { if (current == stage) cancelled.Cancel(); return Task.CompletedTask; });
                var plan = await fixture.Plan();
                await RejectAsync<OperationCanceledException>(() => store.PrepareAsync(plan, cancelled.Token));
                Assert(fixture.RecordDirectories().SequenceEqual(new[] { "old-record" }) && File.ReadAllText(Path.Combine(fixture.Storage, "old-record", "keep")) == "old data", "Cancellation cleanup preserves old data: " + stage);
                Assert(File.ReadAllText(Path.Combine(fixture.Root, "sample.md")) == "old\n", "No workspace changes");
            }
        });

        await check("failed recovery writes release storage locks and clean successfully created backups", async () =>
        {
            using var fixture = new Fixture();
            fixture.PutStorage("old-record/keep", "old data");
            var store = fixture.Store((stage, _) => stage == RecoveryPreparationStage.BackupWritten
                ? Task.FromException(new IOException("Injected backup write failure.")) : Task.CompletedTask);
            var plan = await fixture.Plan();
            await RejectAsync<IOException>(() => store.PrepareAsync(plan));
            Assert(fixture.RecordDirectories().SequenceEqual(new[] { "old-record" }), "No partial record after successful cleanup");
            var prepared = await fixture.Store().PrepareAsync(plan);
            Assert(Directory.Exists(prepared.DirectoryPath) && File.ReadAllText(Path.Combine(fixture.Storage, "old-record", "keep")) == "old data", "Lock released for retry, old data retained");
        });

        await check("recovery preparation never overwrites colliding record directories or foreign manifest files", async () =>
        {
            foreach (var collision in new[] { RecoveryPreparationStage.BeforeDirectory, RecoveryPreparationStage.BeforeManifest })
            {
                using var fixture = new Fixture();
                string? occupied = null;
                var store = fixture.Store((stage, draft) =>
                {
                    if (stage != collision) return Task.CompletedTask;
                    occupied = Path.Combine(fixture.Storage, draft.Manifest.ChangeId.ToString("N"));
                    Directory.CreateDirectory(occupied);
                    File.WriteAllText(Path.Combine(occupied, RecoveryStore.ManifestFileName), "foreign record data");
                    return Task.CompletedTask;
                });
                var plan = await fixture.Plan();
                if (collision == RecoveryPreparationStage.BeforeDirectory) await RejectAsync<IOException>(() => store.PrepareAsync(plan));
                else await RejectAsync<AggregateException>(() => store.PrepareAsync(plan));
                Assert(File.ReadAllText(Path.Combine(occupied!, RecoveryStore.ManifestFileName)) == "foreign record data" &&
                    Directory.EnumerateFiles(occupied!).Count() == 1, "Foreign data preserved; only owned backup removed");
            }
        });

        await check("recovery cleanup refuses replaced files and preserves unexpected entries in partial records", async () =>
        {
            foreach (var replace in new[] { false, true })
            {
                using var fixture = new Fixture();
                string? directory = null;
                var store = fixture.Store((stage, draft) =>
                {
                    if (stage != RecoveryPreparationStage.BeforeReturn) return Task.CompletedTask;
                    directory = Path.Combine(fixture.Storage, draft.Manifest.ChangeId.ToString("N"));
                    if (replace)
                    {
                        var backup = Path.Combine(directory, "before-0000.bin");
                        File.Move(backup, backup + ".original");
                        File.WriteAllText(backup, "replacement");
                    }
                    else File.WriteAllText(Path.Combine(directory, "unexpected"), "preserve me");
                    return Task.CompletedTask;
                });
                var plan = await fixture.Plan();
                await RejectAsync<AggregateException>(() => store.PrepareAsync(plan));
                Assert(directory is not null && Directory.Exists(directory), "Partial record remains visible");
                Assert(replace ? File.ReadAllText(Path.Combine(directory!, "before-0000.bin")) == "replacement" :
                    File.ReadAllText(Path.Combine(directory!, "unexpected")) == "preserve me", "Unowned entries are not deleted");
            }
        });

        await check("recovery cleanup refuses replaced record-directory links and leaves link targets untouched", async () =>
        {
            using var fixture = new Fixture();
            var outside = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "before-0000.bin"), "outside data");
            var probe = Path.Combine(fixture.DirectoryPath, "link-probe");
            try { Directory.CreateSymbolicLink(probe, outside); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP recovery cleanup link check: symbolic link permission unavailable."); return; }
            Directory.Delete(probe);
            string? link = null;
            var store = fixture.Store((stage, draft) =>
            {
                if (stage != RecoveryPreparationStage.BackupWritten) return Task.CompletedTask;
                link = Path.Combine(fixture.Storage, draft.Manifest.ChangeId.ToString("N"));
                Directory.Move(link, Path.Combine(fixture.DirectoryPath, "detached-record"));
                Directory.CreateSymbolicLink(link, outside);
                return Task.FromException(new IOException("Injected directory replacement."));
            });
            try
            {
                var plan = await fixture.Plan();
                await RejectAsync<AggregateException>(() => store.PrepareAsync(plan));
                Assert(File.ReadAllText(Path.Combine(outside, "before-0000.bin")) == "outside data", "Cleanup never follows replaced directory links");
            }
            finally { if (link is not null) Directory.Delete(link); }
        });

        await check("recovery preparation readback detects same-file content corruption before returning a prepared handle", async () =>
        {
            using var fixture = new Fixture();
            var store = fixture.Store((stage, draft) =>
            {
                if (stage == RecoveryPreparationStage.BeforeReturn)
                    File.WriteAllText(Path.Combine(fixture.Storage, draft.Manifest.ChangeId.ToString("N"), "before-0000.bin"), "bad\n");
                return Task.CompletedTask;
            });
            var plan = await fixture.Plan();
            await RejectAsync<IOException>(() => store.PrepareAsync(plan));
            Assert(!fixture.RecordDirectories().Any() && File.ReadAllText(Path.Combine(fixture.Root, "sample.md")) == "old\n", "Corrupt owned data cleaned, workspace unchanged");
        });

        await check("concurrent recovery stores honor the final slot boundary and queued cancellation does not interrupt the active writer", async () =>
        {
            using var fixture = new Fixture();
            for (var i = 0; i < 999; i++) Directory.CreateDirectory(Path.Combine(fixture.Storage, $"old-{i}"));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstStore = fixture.Store(async (stage, _) => { if (stage == RecoveryPreparationStage.BackupWritten) { entered.TrySetResult(); await release.Task; } });
            var plan = await fixture.Plan();
            var first = firstStore.PrepareAsync(plan);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using var cancelled = new CancellationTokenSource();
                var queued = fixture.Store().PrepareAsync(plan, cancelled.Token);
                cancelled.Cancel();
                await RejectAsync<OperationCanceledException>(() => queued);
                Assert(!first.IsCompleted, "Cancelling another waiter leaves active writer running");
                release.TrySetResult();
                _ = await first;
                await RejectAsync<IOException>(() => fixture.Store().PrepareAsync(plan));
                Assert(fixture.RecordDirectories().Count() == 1000, "Exactly one new record fits the last slot");
            }
            finally { release.TrySetResult(); await first; }
        });

        await check("a separate process holding the recovery lock prevents preparation and releases it on exit", async () =>
        {
            using var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Storage);
            var marker = Path.Combine(fixture.DirectoryPath, "lock-child.pid");
            var executable = Environment.ProcessPath ?? throw new Exception("Cannot locate recovery test executable.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(RecoveryStoreChecks).Assembly.Location);
            start.ArgumentList.Add("--recovery-lock-child");
            start.ArgumentList.Add(Path.Combine(fixture.Storage, RecoveryStorageLimits.LockFileName));
            start.ArgumentList.Add(marker);
            using var child = Process.Start(start) ?? throw new Exception("Could not start recovery lock helper.");
            try
            {
                for (var i = 0; i < 500 && !File.Exists(marker); i++) { if (child.HasExited) throw new Exception("Recovery lock helper exited early."); await Task.Delay(10); }
                Assert(File.Exists(marker), "Child owns the actual filesystem lock");
                var plan = await fixture.Plan();
                await RejectAsync<IOException>(() => fixture.Store().PrepareAsync(plan));
                Assert(!fixture.RecordDirectories().Any(), "Busy store creates no record");
            }
            finally { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            Assert(Directory.Exists((await fixture.Store().PrepareAsync(await fixture.Plan())).DirectoryPath), "Lock released after the owning process exited");
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Recovery store: " + message); }
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static async Task<T> RejectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-recovery-store-{Guid.NewGuid():N}");
        public string Root { get; }
        public string Storage => Path.Combine(DirectoryPath, "archive", "recovery");
        public Guid RunId { get; } = Guid.NewGuid();
        private readonly ConfigurationPaths _paths;
        private readonly ProjectWorkspace _workspace;
        private readonly PatchService _patches;
        private readonly string _bash;
        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(DirectoryPath, "project")).FullName;
            PutProject("sample.md", "old\n");
            _paths = new ConfigurationPaths(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"));
            _bash = Path.Combine(DirectoryPath, "bash-fixture");
            File.WriteAllText(_bash, "Dependency fixture; never execute.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_bash, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            _workspace = new ProjectWorkspace(Root);
            _patches = new PatchService(_workspace);
        }
        public RecoveryStore Store(Func<RecoveryPreparationStage, RecoveryDraft, Task>? hook = null, ExecutionMode mode = ExecutionMode.FullAccess) =>
            new(RunId, new ConfigurationValidator(_paths).Validate(new AppSettings { ProjectRoot = Root, BashPath = mode == ExecutionMode.ReadOnly ? "" : _bash,
                Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } }, Path.Combine(DirectoryPath, "settings.json"),
                new ExecutionPolicy { Mode = mode, RecoveryDirectory = Storage }), hook);
        public Task<PatchPlan> Plan(string body = "*** Update File: sample.md\n@@\n-old\n+new") => _patches.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch");
        public void PutProject(string name, string value) => File.WriteAllText(Path.Combine(Root, name), value, new System.Text.UTF8Encoding(false));
        public void PutStorage(string name, string value) { var path = Path.Combine(Storage, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
        public string[] ProjectSnapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Order().Select(path => Path.GetRelativePath(Root, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
        public IEnumerable<string> RecordDirectories() => Directory.Exists(Storage) ? Directory.EnumerateDirectories(Storage).Select(Path.GetFileName).Select(name => name!) : [];
        public void Dispose() { _workspace.Mutations.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }
}
