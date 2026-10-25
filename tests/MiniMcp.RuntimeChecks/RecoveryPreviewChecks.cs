using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Services;
using static PatchExecutionFixture;

internal static class RecoveryPreviewChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("recovery preview reverses mixed operations with exact original bytes, empty-file existence, format and permissions without writes", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            fixture.Put("sample.md", "\ufeffold\r\nlocal\nlast");
            fixture.Put("empty-delete.md", "");
            Directory.CreateDirectory(fixture.PathOf("gone"));
            fixture.Put("gone/bom.md", "\ufeffgone\r\n");
            fixture.Put("move.md", "\ufeffmove\r\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fixture.PathOf("gone/bom.md"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.UserExecute);
            await using var execution = fixture.Execution();
            var committed = await execution.ApplyPatchAsync(Patch("*** Add File: nested/new.md\n+added\n*** Add File: empty-add.md\n" +
                "*** Update File: sample.md\n@@\n-old\n+new\n*** Delete File: empty-delete.md\n*** Delete File: gone/bom.md\n" +
                "*** Update File: move.md\n*** Move to: nested/moved.md\n@@\n-move\n+moved"));
            Directory.Delete(fixture.PathOf("gone"));
            var project = fixture.ProjectSnapshot();
            var journal = StoreSnapshot(fixture);
            var result = await execution.PreviewRecoveryAsync(committed.Commit!.ChangeId);
            Assert(!result.HasConflicts && result.Blockers.IsEmpty && result.RequiresConfirmation && result.RunId == execution.RunId &&
                result.RecordRunId == committed.Commit.RunId && result.RecordState == "Committed" && result.Changes.Length == 6, "One complete read-only preview is bound to the source record and current session");
            var summaries = result.Changes.Select(change => change.Summary).ToDictionary(change => change.Path);
            Assert(summaries["nested/moved.md"].Action == "move" && summaries["nested/moved.md"].MoveTo == "move.md" &&
                summaries["sample.md"].Action == "update" && summaries["gone/bom.md"].Action == "add" &&
                summaries["empty-delete.md"] is { Action: "add", BeforeBytes: 0, AfterBytes: 0 } &&
                summaries["empty-add.md"] is { Action: "delete", BeforeBytes: 0, AfterBytes: 0 } && summaries["nested/new.md"].Action == "delete", "Inverse actions retain movement and distinguish empty files from absence");
            Assert(result.Diff!.Contains("+\"old\\r\\n\"") && result.Diff.Contains("-\"moved\\r\\n\"") && result.Diff.Contains("+\"move\\r\\n\"") &&
                result.Diff.Contains("utf8-bom=yes; eol=mixed; eof-newline=no") && result.Diff.Contains("# before: absent") &&
                !result.Diff.Contains("creation request"), "Full inverse content and exact restoration metadata, not a default creation mode request");
            if (!OperatingSystem.IsWindows()) Assert(result.Diff.Contains("mode=0740"), "Deleted file's actual original mode is the restoration target");
            Assert(result.Statistics == new PatchStatistics(committed.Statistics.RemovedLines, committed.Statistics.AddedLines,
                committed.Statistics.RemovedBytes, committed.Statistics.AddedBytes), "Inverse statistics count real original bytes including BOM");
            Assert(project.SequenceEqual(fixture.ProjectSnapshot()) && journal.SequenceEqual(StoreSnapshot(fixture)) && !Directory.Exists(fixture.PathOf("gone")), "No restore directories, staging, backups, state changes or new records");
        });

        await check("ReadOnly sessions can preview historical committed records while retaining a write blocker and distinct current run identity", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            PatchApplyResult committed;
            await using (var writer = fixture.Execution()) committed = await writer.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var currentRun = Guid.NewGuid();
            await using var reader = new ToolExecutionService(currentRun, fixture.ConfigurationFor(ExecutionMode.ReadOnly));
            var before = StoreSnapshot(fixture);
            var result = await reader.PreviewRecoveryAsync(committed.Commit!.ChangeId);
            Assert(result.Diff is not null && !result.HasConflicts && result.Blockers.SequenceEqual(["ReadOnlyMode"]) && result.Mode == "ReadOnly" &&
                result.RunId == currentRun && result.RecordRunId == committed.Commit.RunId && result.RunId != result.RecordRunId &&
                before.SequenceEqual(StoreSnapshot(fixture)), "Inspection crosses runs without changing policy or granting restoration authority");
        });

        await check("non-Committed recovery states produce explicit blockers and no executable or partial reverse preview", async () =>
        {
            foreach (var state in new[] { RecoveryRecordState.Prepared, RecoveryRecordState.Applying, RecoveryRecordState.RolledBack, RecoveryRecordState.Incomplete })
            {
                using var fixture = new PatchExecutionFixture();
                Guid id;
                if (state is RecoveryRecordState.Prepared or RecoveryRecordState.Applying)
                {
                    var workspace = new ProjectWorkspace(fixture.Root);
                    try
                    {
                        var plan = await new PatchService(workspace).PrepareAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
                        var store = new RecoveryStore(fixture.RunId, fixture.Configuration);
                        var prepared = await store.PrepareAsync(plan);
                        if (state == RecoveryRecordState.Applying) await store.BeginApplyingAsync(prepared, plan);
                        id = prepared.Manifest.ChangeId;
                    }
                    finally { workspace.Mutations.Dispose(); }
                }
                else
                {
                    await using var writer = fixture.Execution((stage, _) =>
                    {
                        if (stage != PatchCommitStage.Installed) return Task.CompletedTask;
                        if (state == RecoveryRecordState.Incomplete) { File.Delete(fixture.PathOf("sample.md")); Directory.CreateDirectory(fixture.PathOf("sample.md")); }
                        return Task.FromException(new IOException("Injected installation failure."));
                    });
                    var failure = await Reject<RecordedPatchExecutionException>(() => writer.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new")));
                    id = failure.Commit.Record.Manifest.ChangeId;
                }
                var pathReads = 0;
                await using var reader = fixture.Execution(preview: (stage, _) => { if (stage == RecoveryPreviewStage.PathRead) pathReads++; return Task.CompletedTask; });
                var journal = StoreSnapshot(fixture);
                var result = await reader.PreviewRecoveryAsync(id);
                Assert(result.RecordState == state.ToString() && result.Blockers.Contains("RecordNotCommitted") && result.Diff is null &&
                    result.Changes.IsEmpty && result.Statistics is null && pathReads == 0 && journal.SequenceEqual(StoreSnapshot(fixture)), "Uncertain, prepared or rolled-back records cannot masquerade as an installed result");
            }
        });

        await check("recovery preview diagnoses byte, format, existence, entry-type and occupied-path conflicts for the whole record", async () =>
        {
            foreach (var scenario in new[] { "same-length", "bom", "ending", "missing", "directory", "nul", "invalid-utf8", "oversized", "parent", "absent-parent", "deleted-occupied", "move-source-occupied" })
            {
                using var fixture = new PatchExecutionFixture();
                var target = "sample.md";
                var body = "*** Update File: sample.md\n@@\n-old\n+new";
                if (scenario is "parent" or "absent-parent")
                {
                    Directory.CreateDirectory(fixture.PathOf("nested")); fixture.Put("nested/item.md", "old\n"); target = "nested/item.md";
                    body = scenario == "parent" ? "*** Update File: nested/item.md\n@@\n-old\n+new" : "*** Delete File: nested/item.md";
                }
                else if (scenario == "deleted-occupied") body = "*** Delete File: sample.md";
                else if (scenario == "move-source-occupied") body = "*** Update File: sample.md\n*** Move to: nested/moved.md";
                await using var execution = fixture.Execution();
                var committed = await execution.ApplyPatchAsync(Patch(body));
                var reason = scenario switch
                {
                    "same-length" or "bom" or "ending" => "ContentChanged", "missing" => "FileMissing", "directory" => "NotRegularFile",
                    "parent" or "absent-parent" => "ParentNotDirectory", "deleted-occupied" or "move-source-occupied" => "PathOccupied",
                    _ => "UnsafeOrUnsupportedFile"
                };
                switch (scenario)
                {
                    case "same-length": fixture.Put(target, "xxx\n"); break;
                    case "bom": fixture.Put(target, "\ufeffnew\n"); break;
                    case "ending": fixture.Put(target, "new\r\n"); break;
                    case "missing": File.Delete(fixture.PathOf(target)); break;
                    case "directory": File.Delete(fixture.PathOf(target)); Directory.CreateDirectory(fixture.PathOf(target)); break;
                    case "nul": File.WriteAllBytes(fixture.PathOf(target), [(byte)'n', 0, (byte)'w', 10]); break;
                    case "invalid-utf8": File.WriteAllBytes(fixture.PathOf(target), [0xff, 0xfe]); break;
                    case "oversized": fixture.Put(target, new string('x', ProjectWorkspace.MaxFileBytes + 1)); break;
                    case "parent": File.Delete(fixture.PathOf(target)); goto case "absent-parent";
                    case "absent-parent": Directory.Delete(fixture.PathOf("nested")); fixture.Put("nested", "foreign parent\n"); break;
                    default: fixture.Put(target, "foreign occupant\n"); break;
                }
                var before = fixture.ProjectSnapshot(); var journal = StoreSnapshot(fixture);
                var result = await execution.PreviewRecoveryAsync(committed.Commit!.ChangeId);
                Assert(result.HasConflicts && result.Blockers.Contains("Conflicts") && result.Conflicts.Any(conflict => conflict.Path == target && conflict.Reason == reason) &&
                    result.Diff is null && result.Changes.IsEmpty && result.Statistics is null, "Specific current-state conflict blocks the entire reverse diff: " + scenario);
                Assert(before.SequenceEqual(fixture.ProjectSnapshot()) && journal.SequenceEqual(StoreSnapshot(fixture)), "Conflicting entries, files and records preserved: " + scenario);
            }
            if (OperatingSystem.IsLinux())
            {
                using var fixture = new PatchExecutionFixture();
                await using var execution = fixture.Execution();
                var committed = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
                File.SetUnixFileMode(fixture.PathOf("sample.md"), UnixFileMode.UserRead);
                var result = await execution.PreviewRecoveryAsync(committed.Commit!.ChangeId);
                Assert(result.Conflicts.Any(conflict => conflict.Reason == "PermissionsChanged") && File.GetUnixFileMode(fixture.PathOf("sample.md")) == UnixFileMode.UserRead,
                    "Actual Unix mode changes are conflicts, never silently repaired");
            }
        });

        await check("recovery preview rejects symlinks, hard links and applicable nonblocking special files while preserving their target", async () =>
        {
            foreach (var kind in OperatingSystem.IsLinux() ? new[] { "symlink", "hardlink", "fifo" } : ["symlink", "hardlink"])
            {
                using var fixture = new PatchExecutionFixture();
                await using var execution = fixture.Execution();
                var committed = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
                var outside = Path.Combine(fixture.DirectoryPath, "outside.md");
                File.WriteAllText(outside, "new\n"); File.Delete(fixture.PathOf("sample.md"));
                if (kind == "symlink") File.CreateSymbolicLink(fixture.PathOf("sample.md"), outside);
                else if (kind == "fifo") Assert(MakeFifo(fixture.PathOf("sample.md"), 0x180) == 0, "FIFO fixture creation");
                else Assert(OperatingSystem.IsWindows() ? CreateHardLinkWindows(fixture.PathOf("sample.md"), outside, IntPtr.Zero) :
                    Link(outside, fixture.PathOf("sample.md")) == 0, "Hard-link fixture creation");
                try
                {
                    var journal = StoreSnapshot(fixture);
                    var result = await execution.PreviewRecoveryAsync(committed.Commit!.ChangeId).WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(result.HasConflicts && result.Diff is null && result.Changes.IsEmpty && File.ReadAllText(outside) == "new\n" &&
                        journal.SequenceEqual(StoreSnapshot(fixture)), "No link following, special-file blocking or target writes: " + kind);
                }
                finally { File.Delete(fixture.PathOf("sample.md")); }
            }
        });

        await check("a missing project produces a recovery conflict without recreating its directory", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var execution = fixture.Execution();
            var committed = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            File.Delete(fixture.PathOf("sample.md")); Directory.Delete(fixture.Root);
            var result = await execution.PreviewRecoveryAsync(committed.Commit!.ChangeId);
            Assert(result.Conflicts.Any(conflict => conflict.Reason == "ProjectMissing") && result.Diff is null && !Directory.Exists(fixture.Root), "Missing project is observed, never recreated by preview");
        });

        await check("corrupt, incomplete, foreign-workspace and unsafe recovery data fail preview without cleanup, repair or partial output", async () =>
        {
            foreach (var damage in new[] { "missing-backup", "bad-backup", "unknown-field", "bad-path", "foreign-root", "extra-entry" })
            {
                using var fixture = new PatchExecutionFixture();
                await using var execution = fixture.Execution();
                var committed = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
                var directory = Path.Combine(fixture.RecoveryDirectory, committed.Commit!.ChangeId.ToString("N"));
                var manifestPath = Path.Combine(directory, RecoveryStore.ManifestFileName);
                var manifest = fixture.ReadManifest();
                switch (damage)
                {
                    case "missing-backup": File.Delete(Path.Combine(directory, "before-0000.bin")); break;
                    case "bad-backup": File.WriteAllText(Path.Combine(directory, "before-0000.bin"), "bad\n"); break;
                    case "unknown-field": File.WriteAllText(manifestPath, Encoding.UTF8.GetString(RecoveryManifestJson.Serialize(manifest)).Replace("\"schemaVersion\": 1", "\"approved\": true, \"schemaVersion\": 1")); break;
                    case "bad-path": File.WriteAllBytes(manifestPath, RecoveryManifestJson.Serialize(manifest with { Paths = [manifest.Paths[0] with { Path = "../outside.md" }] })); break;
                    case "foreign-root": File.WriteAllBytes(manifestPath, RecoveryManifestJson.Serialize(manifest with { ProjectRoot = fixture.DirectoryPath })); break;
                    default: File.WriteAllText(Path.Combine(directory, "foreign.bin"), "keep"); break;
                }
                var before = fixture.ProjectSnapshot(); var journal = StoreSnapshot(fixture);
                await RejectRecordRead(() => execution.PreviewRecoveryAsync(committed.Commit.ChangeId));
                Assert(before.SequenceEqual(fixture.ProjectSnapshot()) && journal.SequenceEqual(StoreSnapshot(fixture)) && execution.ActiveCalls == 0, "Malformed source data is not trusted, repaired or removed: " + damage);
            }
        });

        await check("recovery preview rechecks previously observed paths after all diff computation and collects multiple conflicts", async () =>
        {
            using var fixture = new PatchExecutionFixture(); fixture.Put("second.md", "old\n");
            var changed = false;
            await using var execution = fixture.Execution(preview: (stage, path) =>
            {
                if (!changed && stage == RecoveryPreviewStage.PathRead && path == "sample.md") { changed = true; fixture.Put("sample.md", "late\n"); }
                if (stage == RecoveryPreviewStage.BeforeReturn) fixture.Put("second.md", "external\n");
                return Task.CompletedTask;
            });
            var committed = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new\n*** Update File: second.md\n@@\n-old\n+new"));
            var journal = StoreSnapshot(fixture);
            var result = await execution.PreviewRecoveryAsync(committed.Commit!.ChangeId);
            Assert(result.HasConflicts && result.Conflicts.Where(conflict => conflict.Reason == "ContentChanged").Select(conflict => conflict.Path).Order()
                .SequenceEqual(new[] { "sample.md", "second.md" }) && result.Diff is null && result.Changes.IsEmpty && journal.SequenceEqual(StoreSnapshot(fixture)), "Late changes on any member invalidate the whole previously computed preview");
        });

        await check("source record changes during preview invalidate its diff and backup corruption is rechecked before return", async () =>
        {
            foreach (var corruptBackup in new[] { false, true })
            {
                using var fixture = new PatchExecutionFixture();
                await using var execution = fixture.Execution(preview: (stage, _) =>
                {
                    if (stage != RecoveryPreviewStage.BeforeReturn) return Task.CompletedTask;
                    if (corruptBackup) File.WriteAllText(Path.Combine(fixture.RecoveryDirectory, fixture.RecordIds().Single().ToString("N"), "before-0000.bin"), "bad\n");
                    else
                    {
                        var manifest = fixture.ReadManifest() with { RunId = Guid.NewGuid() };
                        File.WriteAllBytes(Path.Combine(fixture.RecoveryDirectory, manifest.ChangeId.ToString("N"), RecoveryStore.ManifestFileName), RecoveryManifestJson.Serialize(manifest));
                    }
                    return Task.CompletedTask;
                });
                var committed = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
                if (corruptBackup) await RejectRecordRead(() => execution.PreviewRecoveryAsync(committed.Commit!.ChangeId));
                else
                {
                    var result = await execution.PreviewRecoveryAsync(committed.Commit!.ChangeId);
                    Assert(result.RecordRunId == committed.Commit.RunId && result.Conflicts.Any(conflict => conflict.Reason == "RecordChanged") && result.Diff is null,
                        "Full metadata revision is rechecked, not merely state and change ID");
                }
                Assert(fixture.Get("sample.md") == "new\n" && fixture.RecordIds().Length == 1, "No workspace repair or replacement record after source changes");
            }
        });

        await check("recovery previews retain immutable historical output and expose only bounded local display metadata", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var execution = fixture.Execution();
            var committed = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var preview = await execution.PreviewRecoveryAsync(committed.Commit!.ChangeId);
            var json = JsonSerializer.Serialize(preview, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            fixture.Put("sample.md", "external after preview\n");
            using var parsed = JsonDocument.Parse(json);
            Assert(JsonSerializer.Serialize(preview, new JsonSerializerOptions(JsonSerializerDefaults.Web)) == json && preview.Diff!.Contains("+\"old\\n\"") &&
                parsed.RootElement.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[]
                { "blockers", "changeId", "changes", "conflicts", "diff", "hasConflicts", "mode", "recordRunId", "recordState", "requiresConfirmation", "runId", "statistics" }) &&
                !json.Contains("before-0000.bin") && !json.Contains(fixture.Root.Replace("\\", "\\\\")) && !json.Contains("executionContext", StringComparison.OrdinalIgnoreCase), "A display snapshot is immutable and contains no plan, backing capability, storage paths or raw snapshot objects");
            var current = await execution.PreviewRecoveryAsync(committed.Commit.ChangeId);
            Assert(current.HasConflicts && current.Diff is null && preview.Conflicts.IsEmpty && fixture.RecordIds().Length == 1, "A prior clean preview cannot authorize or hide a later conflicting state");
        });

        await check("recovery preview reads complete large original content and visibly escapes controls without file-read output truncation", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            var original = "\ufeff" + new string('x', 100_000) + "TAIL中👋\t\u001b\n";
            fixture.Put("long.md", original);
            await using var execution = fixture.Execution();
            var committed = await execution.ApplyPatchAsync(Patch("*** Delete File: long.md"));
            var result = await execution.PreviewRecoveryAsync(committed.Commit!.ChangeId);
            Assert(result.Diff!.Length > ProjectWorkspace.MaxOutputCharacters && result.Diff.Contains("TAIL中👋\\t\\u001b\\n\"") &&
                !result.Diff.Contains('\u001b') && result.Statistics!.AddedBytes == Encoding.UTF8.GetByteCount(original) && !File.Exists(fixture.PathOf("long.md")), "Full backup bytes, BOM and escaped line content are shown with no write or arbitrary truncation");
        });

        await check("recovery preview enforces complete display and computation budgets without returning partial diffs or writing state", async () =>
        {
            foreach (var computation in new[] { false, true })
            {
                using var fixture = new RecordedPatchCommitChecks.Fixture();
                var body = "*** Add File: large.md\n+" + new string('\u001b', 50_000);
                if (computation)
                {
                    fixture.Put("sample.md", string.Concat(Enumerable.Range(0, 1000).Select(index => $"old{index}\n")));
                    body = "*** Update File: sample.md\n@@\n" + string.Join('\n', Enumerable.Range(0, 1000).Select(index => $"-old{index}")) + "\n" +
                        string.Join('\n', Enumerable.Range(0, 1000).Select(index => $"+new{index}"));
                }
                var store = fixture.Store();
                var committed = await fixture.Service.CommitWithRecoveryAsync(await fixture.Plan(body), store);
                var before = fixture.ProjectSnapshot();
                var service = new RecoveryPreviewService(fixture.Workspace, store);
                var error = await Reject<WorkspaceException>(() => service.PreviewAsync(committed.Record.Manifest.ChangeId));
                Assert(error.Message.Contains("Complete recovery preview") && error.Message.Contains(computation ? "computation limit" : "display limit") &&
                    before.SequenceEqual(fixture.ProjectSnapshot()) && (await store.LoadAsync(committed.Record.Manifest.ChangeId)).Manifest.State == RecoveryRecordState.Committed,
                    "Unrenderable history fails closed with an intact record and workspace");
            }
        });

        await check("recovery preview covers the full 64-move and 128-path record boundary without omitted files", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            for (var index = 0; index < 64; index++) fixture.Put($"source{index}.md", "");
            await using var execution = fixture.Execution();
            var body = string.Join('\n', Enumerable.Range(0, 64).Select(index => $"*** Update File: source{index}.md\n*** Move to: moved/file{index}.md"));
            var committed = await execution.ApplyPatchAsync(Patch(body));
            var result = await execution.PreviewRecoveryAsync(committed.Commit!.ChangeId);
            Assert(result.Changes.Length == 64 && !result.HasConflicts && result.Changes.All(change => change.Summary.Action == "move") &&
                result.Changes.Select(change => change.Summary.MoveTo).ToHashSet().SetEquals(Enumerable.Range(0, 64).Select(index => $"source{index}.md")) &&
                result.Statistics == new PatchStatistics(0, 0, 0, 0) && !File.Exists(fixture.PathOf("source0.md")), "All inverse move ends and empty-file existence remain explicit");
        });

        await check("preview uses read-only record access and shared-read workspace observation without changing read-only storage attributes", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var execution = fixture.Execution();
            var committed = await execution.ApplyPatchAsync(Patch("*** Update File: sample.md\n@@\n-old\n+new"));
            var files = Directory.EnumerateFiles(fixture.RecoveryDirectory, "*", SearchOption.AllDirectories).ToArray();
            try
            {
                foreach (var file in files) File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
                using var shared = new FileStream(fixture.PathOf("sample.md"), FileMode.Open, FileAccess.Read, FileShare.Read);
                var preview = await execution.PreviewRecoveryAsync(committed.Commit!.ChangeId);
                Assert(!preview.HasConflicts && preview.Diff is not null && files.All(file => (File.GetAttributes(file) & FileAttributes.ReadOnly) != 0), "Preview never upgrades record access or excludes another read handle");
            }
            finally { foreach (var file in files) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly); }
        });

        await check("empty IDs, missing records, missing storage and pre-cancelled local previews create no directories or retained calls", async () =>
        {
            using var fixture = new PatchExecutionFixture(ExecutionMode.ReadOnly);
            await using var execution = fixture.Execution();
            var before = fixture.ProjectSnapshot();
            await Reject<ArgumentException>(() => execution.PreviewRecoveryAsync(Guid.Empty));
            await RejectRecordRead(() => execution.PreviewRecoveryAsync(Guid.NewGuid()));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Reject<OperationCanceledException>(() => execution.PreviewRecoveryAsync(Guid.NewGuid(), cancelled.Token));
            Assert(!Directory.Exists(fixture.RecoveryDirectory) && before.SequenceEqual(fixture.ProjectSnapshot()) && execution.ActiveCalls == 0, "Read failure and cancellation do not manufacture a recovery root, lock marker or record");
        });

        await check("preview rejects a store attached to another project before loading any metadata", () =>
        {
            using var first = new PatchExecutionFixture(); using var second = new PatchExecutionFixture();
            var workspace = new ProjectWorkspace(first.Root);
            try
            {
                try { _ = new RecoveryPreviewService(workspace, new RecoveryStore(second.RunId, second.Configuration)); }
                catch (ArgumentException) { Assert(!Directory.Exists(first.RecoveryDirectory) && !Directory.Exists(second.RecoveryDirectory), "Mismatched construction performs no storage IO"); return Task.CompletedTask; }
                throw new Exception("Expected project/store mismatch rejection.");
            }
            finally { workspace.Mutations.Dispose(); }
        });
    }

    internal static string[] StoreSnapshot(PatchExecutionFixture fixture) => Directory.Exists(fixture.RecoveryDirectory)
        ? Directory.EnumerateFileSystemEntries(fixture.RecoveryDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(fixture.RecoveryDirectory, path) + ":" + (Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray() : [];
    private static async Task RejectRecordRead(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or WorkspaceException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected record read/validation failure without a preview.");
    }
    private static void Assert(bool condition, string message) => PatchExecutionFixture.Assert(condition, message);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkWindows(string link, string target, IntPtr security);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string target, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)] private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
