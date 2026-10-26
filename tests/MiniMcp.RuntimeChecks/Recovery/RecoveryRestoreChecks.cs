using MiniMcp.Services.Patching;
using MiniMcp.Services.Workspace;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Services;
using static PatchExecutionFixture;

internal static class RecoveryRestoreChecks
{
    internal const string MixedBody = "*** Add File: nested/added.md\n+added\n*** Add File: empty-added.md\n" +
        "*** Update File: sample.md\n@@\n-old\n+new\n*** Delete File: empty-deleted.md\n*** Delete File: gone/script.cs\n" +
        "*** Update File: move.md\n*** Move to: moved/target.md\n@@\n-before\n+after";

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        foreach (var mode in new[] { ExecutionMode.ProjectEdit, ExecutionMode.FullAccess })
            await check($"internal recovery {mode} restores mixed raw content and records its own before-state, including reversal of the restore", async () =>
            {
                using var fixture = new PatchExecutionFixture();
                Seed(fixture);
                var original = Files(fixture);
                PatchApplyResult source;
                await using (var writer = fixture.Execution()) source = await writer.ApplyPatchAsync(Patch(MixedBody));
                Directory.Delete(fixture.PathOf("gone"));
                var afterSource = Files(fixture);
                var originalRecord = RecordFiles(fixture, source.Commit!.ChangeId);
                await using var execution = new ToolExecutionService(Guid.NewGuid(), fixture.ConfigurationFor(mode));
                var preparation = await execution.PrepareRecoveryAsync(source.Commit.ChangeId);
                Assert(preparation.Preview.Diff is not null && preparation.Preview.RequiresConfirmation && preparation.Preview.Blockers.IsEmpty &&
                    preparation.Preview.RunId == execution.RunId && preparation.Preview.RecordRunId == source.Commit.RunId &&
                    afterSource.SequenceEqual(Files(fixture)) && fixture.RecordIds().Length == 1, "Preflight captures the full local review and does not reserve/write a new record");
                var result = await execution.CommitRecoveryAsync(preparation);
                await VerifyCommit(fixture, result, source.Commit.ChangeId, execution.RunId);
                Assert(original.SequenceEqual(Files(fixture)) && originalRecord.SequenceEqual(RecordFiles(fixture, source.Commit.ChangeId)) &&
                    result.Commit.Record.Manifest.ChangeId != source.Commit.ChangeId && fixture.RecordIds().Length == 2, "Restore reproduces exact original files and preserves the source record");
                if (!OperatingSystem.IsWindows())
                {
                    Assert(File.GetUnixFileMode(fixture.PathOf("gone/script.cs")) == (UnixFileMode)0x1ed &&
                        result.Commit.Record.Manifest.Paths.Single(path => path.Path == "gone/script.cs").After is { UnixMode: (UnixFileMode)0x1ed, UnixModeIsCreateRequest: false },
                        "Restored additions get the exact original 0755 mode and a finalized observed mode, rather than a default 0600/umask subset");
                }
                var restoreRecord = await fixture.Load(result.Commit.Record.Manifest.ChangeId);
                Assert(restoreRecord.PathsBeforeBytes("nested/added.md").SequenceEqual(Encoding.UTF8.GetBytes("added\n")) &&
                    restoreRecord.PathsBeforeBytes("empty-added.md").Length == 0 &&
                    restoreRecord.Manifest.Paths.Single(path => path.Path == "gone/script.cs").Before is null, "Deleting prior additions and recreating prior deletions both get truthful recovery before-images");
                var undo = await execution.CommitRecoveryAsync(await execution.PrepareRecoveryAsync(result.Commit.Record.Manifest.ChangeId));
                await VerifyCommit(fixture, undo, result.Commit.Record.Manifest.ChangeId, execution.RunId);
                Assert(afterSource.SequenceEqual(Files(fixture)) && fixture.RecordIds().Length == 3, "The restore's ordinary recovery record can itself be reversed without rewriting history");
            });

        await check("recovery writes reject ReadOnly, invalid IDs and non-Committed or conflicting records before creating a new record", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var writer = fixture.Execution();
            var source = await writer.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var preparation = await writer.PrepareRecoveryAsync(source.Commit!.ChangeId);
            await using var readOnly = new ToolExecutionService(Guid.NewGuid(), fixture.ConfigurationFor(ExecutionMode.ReadOnly));
            var before = Files(fixture); var journal = RecordFiles(fixture, source.Commit.ChangeId);
            await Reject<WorkspaceException>(() => readOnly.PrepareRecoveryAsync(source.Commit.ChangeId));
            await Reject<WorkspaceException>(() => readOnly.CommitRecoveryAsync(preparation));
            await Reject<ArgumentException>(() => writer.PrepareRecoveryAsync(Guid.Empty));
            await Reject<ArgumentNullException>(() => writer.CommitRecoveryAsync(null!));
            foreach (var state in new[] { RecoveryRecordState.Prepared, RecoveryRecordState.Applying, RecoveryRecordState.RolledBack, RecoveryRecordState.Incomplete })
            {
                var manifest = await fixture.Load(source.Commit.ChangeId);
                File.WriteAllBytes(ManifestPath(fixture, source.Commit.ChangeId), RecoveryManifestJson.Serialize(manifest.Manifest with { State = state }));
                await Reject<WorkspaceException>(() => writer.PrepareRecoveryAsync(source.Commit.ChangeId));
            }
            File.WriteAllBytes(ManifestPath(fixture, source.Commit.ChangeId), Convert.FromBase64String(journal.Single(line => line.StartsWith("manifest.json:", StringComparison.Ordinal))["manifest.json:".Length..]));
            fixture.Put("sample.md", "external\n");
            await Reject<WorkspaceException>(() => writer.PrepareRecoveryAsync(source.Commit.ChangeId));
            Assert(fixture.RecordIds().Length == 1 && (await fixture.Load(source.Commit.ChangeId)).Manifest.State == RecoveryRecordState.Committed &&
                fixture.Get("sample.md") == "external\n" && readOnly.ActiveCalls == 0 && writer.ActiveCalls == 0, "No rejected request can create a restore record, change local policy or overwrite external content");
        });

        await check("restoration preflight is one-time session-bound evidence and cannot be cloned, deserialized or issued by another coordinator", async () =>
        {
            using var fixture = new RecordedPatchCommitChecks.Fixture();
            var store = fixture.Store();
            var source = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(), store);
            var previews = new RecoveryPreviewService(fixture.Workspace, store);
            var service = new RecoveryRestoreService(fixture.Workspace, fixture.Service, store, previews);
            var peer = new RecoveryRestoreService(fixture.Workspace, fixture.Service, store, previews);
            var plan = await service.PrepareAsync(source.Record.Manifest.ChangeId);
            var fake = new RecoveryRestorePlan(service, new object(), new RecoveryInspection(plan.Preview, plan.Source, []), plan.Plan);
            var before = fixture.ProjectSnapshot();
            await Reject<InvalidOperationException>(() => peer.CommitAsync(plan));
            await Reject<InvalidOperationException>(() => service.CommitAsync(fake));
            Assert(JsonSerializer.Serialize(plan) == "{}", "No private preparation state is serialized as approval or a commit ticket");
            try { _ = JsonSerializer.Deserialize<RecoveryRestorePlan>("{}"); throw new Exception("Expected non-public constructor rejection."); }
            catch (NotSupportedException) { }
            Assert(before.SequenceEqual(fixture.ProjectSnapshot()), "Invalid plans perform zero workspace writes");
            var committed = await service.CommitAsync(plan);
            Assert(committed.Commit.Outcome.VerifiedState == PatchCommitState.Committed, "Only the real issuer can consume its original plan");
            before = fixture.ProjectSnapshot();
            await Reject<InvalidOperationException>(() => service.CommitAsync(plan));
            Assert(before.SequenceEqual(fixture.ProjectSnapshot()), "Consumed evidence cannot replay the engine");
        });

        await check("a restore preflight does not hold modification locks while awaiting a future local decision and stale files reject the whole restore", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var execution = fixture.Execution();
            var source = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new\n*** Add File: nested/added.md\n+added"));
            var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
            await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-new\n+later"));
            var current = Files(fixture);
            await Reject<WorkspaceException>(() => execution.CommitRecoveryAsync(plan));
            Assert(current.SequenceEqual(Files(fixture)) && fixture.RecordIds().Length == 2 && fixture.Get("nested/added.md") == "added\n",
                "Waiting for confirmation reserves no file; a later source change prevents even an unrelated inverse deletion");
        });

        await check("source metadata or backup changes after preflight are reloaded and rejected before restoration backup or workspace writes", async () =>
        {
            foreach (var backup in new[] { false, true })
            {
                using var fixture = new PatchExecutionFixture();
                await using var execution = fixture.Execution();
                var source = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
                var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
                if (backup) File.WriteAllText(Path.Combine(RecordDirectory(fixture, source.Commit.ChangeId), "before-0000.bin"), "bad\n");
                else RewriteRun(fixture, source.Commit.ChangeId);
                var record = RecordFiles(fixture, source.Commit.ChangeId); var current = Files(fixture);
                await RejectValidation(() => execution.CommitRecoveryAsync(plan));
                Assert(current.SequenceEqual(Files(fixture)) && record.SequenceEqual(RecordFiles(fixture, source.Commit.ChangeId)) && fixture.RecordIds().Length == 1,
                    "The bound full source record and bytes cannot be replaced by a caller-supplied ID or a rewritten snapshot");
            }
        });

        await check("a destination or parent occupied after preflight rejects restoration without deleting other reviewed paths", async () =>
        {
            foreach (var parent in new[] { false, true })
            {
                using var fixture = new PatchExecutionFixture(); Directory.CreateDirectory(fixture.PathOf("gone")); fixture.Put("gone/old.md", "old\n");
                await using var execution = fixture.Execution();
                var source = await execution.ApplyPatchAsync(Patch("*** Delete File: gone/old.md\n*** Add File: added.md\n+added"));
                Directory.Delete(fixture.PathOf("gone"));
                var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
                if (parent) fixture.Put("gone", "foreign parent\n");
                else { Directory.CreateDirectory(fixture.PathOf("gone")); fixture.Put("gone/old.md", "foreign destination\n"); }
                var current = Files(fixture);
                await Reject<WorkspaceException>(() => execution.CommitRecoveryAsync(plan));
                Assert(current.SequenceEqual(Files(fixture)) && fixture.Get("added.md") == "added\n" && fixture.RecordIds().Length == 1, "No ordinary subset or early recovery backup survives a stale target");
            }
        });

        await check("restore preparation and commit cancellation before admission perform no writes and do not consume unused preflight evidence", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var execution = fixture.Execution();
            var source = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var plan = await execution.PrepareRecoveryAsync(source.Commit!.ChangeId);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Reject<OperationCanceledException>(() => execution.PrepareRecoveryAsync(source.Commit.ChangeId, cancelled.Token));
            await Reject<OperationCanceledException>(() => execution.CommitRecoveryAsync(plan, cancelled.Token));
            Assert(fixture.Get("sample.md") == "new\n" && fixture.RecordIds().Length == 1 && execution.ActiveCalls == 0, "Pre-cancelled operations never reach preparation or installation");
            await VerifyCommit(fixture, await execution.CommitRecoveryAsync(plan), source.Commit.ChangeId, execution.RunId);
        });
    }

    internal static void Seed(PatchExecutionFixture fixture)
    {
        fixture.Put("sample.md", "\ufeffold\r\nlocal\nlast"); fixture.Put("empty-deleted.md", ""); fixture.Put("move.md", "\ufeffbefore\r\n");
        Directory.CreateDirectory(fixture.PathOf("gone")); fixture.Put("gone/script.cs", "\ufefforiginal\r\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fixture.PathOf("gone/script.cs"), (UnixFileMode)0x1ed);
    }
    internal static string[] Files(PatchExecutionFixture fixture)
    {
        // Directory enumeration is an observation, not an atomic listing. Never omit an unreadable
        // file; retry only a bounded full listing when the platform reports a stale renamed name.
        for (var attempt = 0; ; attempt++)
        {
            try { return Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(path => Path.GetRelativePath(fixture.Root, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path)) + ":" +
                    (OperatingSystem.IsWindows() ? "" : ((int)File.GetUnixFileMode(path)).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray(); }
            catch (FileNotFoundException) when (attempt < 2) { }
            catch (DirectoryNotFoundException) when (attempt < 2) { }
        }
    }
    internal static string RecordDirectory(PatchExecutionFixture fixture, Guid id) => Path.Combine(fixture.RecoveryDirectory, id.ToString("N"));
    internal static string ManifestPath(PatchExecutionFixture fixture, Guid id) => Path.Combine(RecordDirectory(fixture, id), RecoveryStore.ManifestFileName);
    internal static string[] RecordFiles(PatchExecutionFixture fixture, Guid id) => Directory.EnumerateFiles(RecordDirectory(fixture, id)).Order(StringComparer.Ordinal)
        .Select(path => Path.GetFileName(path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
    internal static void RewriteRun(PatchExecutionFixture fixture, Guid id)
    {
        var original = RecoveryManifestJson.Parse(File.ReadAllBytes(ManifestPath(fixture, id)));
        File.WriteAllBytes(ManifestPath(fixture, id), RecoveryManifestJson.Serialize(original with { RunId = Guid.NewGuid() }));
    }
    internal static async Task VerifyCommit(PatchExecutionFixture fixture, RecoveryRestoreCommit result, Guid source, Guid run)
    {
        Assert(result.SourceChangeId == source && result.Commit.Outcome.VerifiedState == PatchCommitState.Committed &&
            result.Commit.Record.Manifest.State == RecoveryRecordState.Committed && result.Commit.Record.Manifest.RunId == run, "Verified restore result identifies its historical source and real new committed record");
        await RecordedPatchFaultChecks.VerifyAsync(result.Commit.Record.Owner, result.Commit);
        Assert((await fixture.Load(result.Commit.Record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Committed, "New restore history is independently loadable");
    }
    internal static async Task RejectValidation(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is WorkspaceException or IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected record/workspace validation failure.");
    }
    private static byte[] PathsBeforeBytes(this RecoveryRecordSnapshot record, string path) => record.Backups.Single(backup =>
        backup.FileName == record.Manifest.Paths.Single(state => state.Path == path).BackupFile).Bytes.ToArray();
    private static void Assert(bool condition, string message) => PatchExecutionFixture.Assert(condition, message);
}
