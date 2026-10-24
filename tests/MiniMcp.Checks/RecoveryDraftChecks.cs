using System.Security.Cryptography;
using System.Text;
using MiniMcp.Recovery;
using MiniMcp.Services;

internal static class RecoveryDraftChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("recovery drafts capture exact original bytes and both path states for mixed patch operations", async () =>
        {
            using var fixture = new Fixture();
            const string original = "\ufeff用户本地修改\r\nsame\n尾";
            fixture.Put("edit.cs", original);
            fixture.Put("delete.md", "gone\r\n");
            fixture.Put("move.cs", "before\n");
            var before = fixture.Snapshot();
            var plan = await fixture.Prepare("*** Add File: nested/new.md\n+新增\n*** Add File: empty.md\n" +
                "*** Update File: edit.cs\n@@\n-用户本地修改\n+用户修改后\n same\n 尾\n*** Delete File: delete.md\n" +
                "*** Update File: move.cs\n*** Move to: moved/result.cs\n@@\n-before\n+after");
            var run = Guid.NewGuid();
            var draft = RecoveryDraftBuilder.Create(run, plan);
            Assert(ReferenceEquals(draft.Plan, plan) && draft.Manifest.RunId == run && draft.Manifest.ProjectRoot == fixture.Root, "Plan and workspace ownership");
            Assert(draft.Manifest.SchemaVersion == 1 && draft.Manifest.State == RecoveryRecordState.Prepared, "Initial manifest");
            Assert(draft.Manifest.Operations.Select(operation => operation.Action).SequenceEqual(new[] { "add", "add", "update", "delete", "move" }), "Exact operation order");
            Assert(draft.Manifest.Paths.Length == 6 && draft.Backups.Length == 3, "Move has two path states and one source backup");
            var added = PathState(draft, "nested/new.md");
            Assert(added.Before is null && added.BackupFile is null, "New file originally absent");
            CheckImage(added.After, Encoding.UTF8.GetBytes("新增\n"));
            var edited = PathState(draft, "edit.cs");
            CheckImage(edited.Before, Encoding.UTF8.GetBytes(original));
            CheckImage(edited.After, Encoding.UTF8.GetBytes("\ufeff用户修改后\r\nsame\n尾"));
            CheckBackup(draft, edited, Encoding.UTF8.GetBytes(original));
            var deleted = PathState(draft, "delete.md");
            Assert(deleted.After is null, "Deleted file expected absent");
            CheckBackup(draft, deleted, Encoding.UTF8.GetBytes("gone\r\n"));
            var source = PathState(draft, "move.cs");
            var destination = PathState(draft, "moved/result.cs");
            Assert(source.Before is not null && source.After is null && destination.Before is null && destination.BackupFile is null, "Move absence facts");
            CheckBackup(draft, source, Encoding.UTF8.GetBytes("before\n"));
            CheckImage(destination.After, Encoding.UTF8.GetBytes("after\n"));
            Assert(draft.BackupBytes == Encoding.UTF8.GetByteCount(original + "gone\r\nbefore\n"), "Only complete original bytes count toward backups");
            Assert(before.SequenceEqual(fixture.Snapshot()), "No workspace, backup or directory writes");
        });

        await check("recovery drafts distinguish missing paths from existing empty files including zero-byte backups", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("existing.md", "");
            fixture.Put("deleted.md", "");
            var draft = RecoveryDraftBuilder.Create(Guid.NewGuid(), await fixture.Prepare(
                "*** Update File: existing.md\n@@\n+new\n*** Add File: new-empty.md\n*** Delete File: deleted.md"));
            var existing = PathState(draft, "existing.md");
            var added = PathState(draft, "new-empty.md");
            var deleted = PathState(draft, "deleted.md");
            CheckImage(existing.Before, []);
            CheckImage(added.After, []);
            CheckImage(deleted.Before, []);
            Assert(added.Before is null && added.BackupFile is null && deleted.After is null, "Absence uses a null image");
            Assert(existing.Before!.Sha256 == "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", "Known empty-file SHA-256");
            Assert(draft.Backups.Length == 2 && draft.Backups.All(backup => backup.Bytes.Length == 0) && draft.BackupBytes == 0, "Empty originals still have backup entries");
            Assert(existing.BackupFile != deleted.BackupFile && existing.BackupFile is not null && deleted.BackupFile is not null, "Separate original files");
        });

        await check("recovery images preserve original Unix modes and distinguish new-file creation requests from actual permissions", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Prepare("*** Update File: sample.md\n*** Move to: moved.md\n@@\n-old\n+new");
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
            var change = plan.Changes[0];
            // Interpret a captured Unix mode on every test platform without claiming real Linux filesystem coverage.
            var snapshot = new PatchPlan(plan.Owner, [change with { Before = change.Before! with { Mode = mode } }]);
            var moved = RecoveryDraftBuilder.Create(Guid.NewGuid(), snapshot);
            Assert(PathState(moved, "sample.md").Before!.UnixMode == mode && !PathState(moved, "sample.md").Before!.UnixModeIsCreateRequest, "Observed original mode");
            Assert(PathState(moved, "moved.md").After!.UnixMode == mode && !PathState(moved, "moved.md").After!.UnixModeIsCreateRequest, "Move retains the source mode");
            var addPlan = await fixture.Prepare("*** Add File: new.md\n+new");
            var added = PathState(RecoveryDraftBuilder.Create(Guid.NewGuid(), addPlan), "new.md").After!;
            Assert(added.UnixMode == addPlan.NewFileCreateMode && added.UnixModeIsCreateRequest == (addPlan.NewFileCreateMode is not null), "Creation mode is a request subject to umask");
            if (OperatingSystem.IsWindows()) Assert(added.UnixMode is null, "Windows mode is not tracked");
        });

        await check("recovery drafts use prepared snapshots after external edits and even when the workspace has disappeared", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Prepare("*** Update File: sample.md\n@@\n-old\n+new");
            fixture.Put("sample.md", "external edit\n");
            var before = fixture.Snapshot();
            var first = RecoveryDraftBuilder.Create(Guid.NewGuid(), plan);
            CheckBackup(first, PathState(first, "sample.md"), Encoding.UTF8.GetBytes("old\n"));
            Assert(before.SequenceEqual(fixture.Snapshot()), "No reread or overwrite of external changes");
            Directory.Delete(fixture.Root, recursive: true);
            var second = RecoveryDraftBuilder.Create(Guid.NewGuid(), plan);
            CheckBackup(second, PathState(second, "sample.md"), Encoding.UTF8.GetBytes("old\n"));
            CheckImage(PathState(second, "sample.md").After, Encoding.UTF8.GetBytes("new\n"));
            Assert(!Directory.Exists(fixture.Root) && second.Manifest.ProjectRoot == fixture.Root, "In-memory capture neither validates current commit readiness nor creates directories");
        });

        await check("recovery record IDs are locally generated, workspaces stay isolated and copied buffers cannot mutate originals", async () =>
        {
            using var first = new Fixture();
            using var second = new Fixture();
            var run = Guid.NewGuid();
            var plan = await first.Prepare("*** Delete File: sample.md");
            var a = RecoveryDraftBuilder.Create(run, plan);
            var b = RecoveryDraftBuilder.Create(run, plan);
            var c = RecoveryDraftBuilder.Create(run, await second.Prepare("*** Delete File: sample.md"));
            Assert(a.Manifest.ChangeId != Guid.Empty && a.Manifest.ChangeId != b.Manifest.ChangeId && b.Manifest.ChangeId != c.Manifest.ChangeId, "Unique change IDs independent of run ID");
            Assert(a.Manifest.RunId == run && b.Manifest.RunId == run && c.Manifest.RunId == run, "Run association");
            Assert(a.Manifest.CreatedAtUtc.Offset == TimeSpan.Zero && a.Manifest.ProjectRoot != c.Manifest.ProjectRoot && c.Manifest.ProjectRoot == second.Root, "UTC and owner workspace");
            Assert(a.Manifest.Paths.SequenceEqual(b.Manifest.Paths) && a.Manifest.Operations.SequenceEqual(b.Manifest.Operations), "Stable snapshots across new record IDs");
            var copied = a.Backups[0].Bytes.ToArray();
            copied[0] = (byte)'X';
            CheckBackup(a, PathState(a, "sample.md"), Encoding.UTF8.GetBytes("old\n"));
            Assert(a.Backups[0].FileName == "before-0000.bin" && !a.Backups[0].FileName.Contains("sample"), "Generated backup names do not embed project paths");
        });

        await check("cancelled or invalid recovery draft requests produce no files or stored-record capability", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Prepare("*** Add File: pending/new.md\n+new");
            var before = fixture.Snapshot();
            Reject<ArgumentException>(() => RecoveryDraftBuilder.Create(Guid.Empty, plan));
            Reject<ArgumentNullException>(() => RecoveryDraftBuilder.Create(Guid.NewGuid(), null!));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Reject<OperationCanceledException>(() => RecoveryDraftBuilder.Create(Guid.NewGuid(), plan, cancelled.Token));
            var draft = RecoveryDraftBuilder.Create(Guid.NewGuid(), plan);
            Assert(draft.Backups.Length == 0 && PathState(draft, "pending/new.md").Before is null, "Following capture still succeeds");
            Assert(before.SequenceEqual(fixture.Snapshot()) && !Directory.Exists(Path.Combine(fixture.Root, "pending")), "Nothing persisted or executed");
        });
    }

    private static RecoveryPathState PathState(RecoveryDraft draft, string path) => draft.Manifest.Paths.Single(state => state.Path == path);
    private static void CheckImage(RecoveryFileImage? image, byte[] bytes)
    {
        var value = image ?? throw new Exception("Expected a file image.");
        Assert(value.ByteLength == bytes.Length && value.Sha256 == Convert.ToHexString(SHA256.HashData(bytes)), "Exact byte length and hash");
    }
    private static void CheckBackup(RecoveryDraft draft, RecoveryPathState state, byte[] bytes)
    {
        CheckImage(state.Before, bytes);
        Assert(state.BackupFile is not null && draft.Backups.Single(backup => backup.FileName == state.BackupFile).Bytes.AsSpan().SequenceEqual(bytes), "Complete immutable original backup");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Recovery draft: " + message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"minimcp-recovery-draft-{Guid.NewGuid():N}");
        public string Root { get; }
        private readonly ProjectWorkspace _workspace;
        private readonly PatchService _service;
        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(_directory, "project")).FullName;
            Put("sample.md", "old\n");
            _workspace = new ProjectWorkspace(Root);
            _service = new PatchService(_workspace);
        }
        public void Put(string path, string text) => File.WriteAllText(Path.Combine(Root, path), text, new UTF8Encoding(false));
        public Task<PatchPlan> Prepare(string body) => _service.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch");
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(_directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(_directory, path) + (Directory.Exists(path) ? "/" : ":" + Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() { _workspace.Mutations.Dispose(); Directory.Delete(_directory, recursive: true); }
    }
}
