using MiniMcp.Services.Patching;
using MiniMcp.Services.Workspace;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using MiniMcp.Recovery;

internal static class RecoveryManifestValidationChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("recovery metadata validates mixed operations after JSON roundtrip and independently of path-state ordering", async () =>
        {
            using var fixture = new Fixture();
            var before = fixture.Snapshot();
            var manifest = await fixture.Manifest("*** Add File: nested/new.md\n+new\n*** Add File: empty.md\n" +
                "*** Update File: source.md\n@@\n-old\n+new\n*** Delete File: delete.md\n" +
                "*** Update File: move.md\n*** Move to: moved/result.md\n@@\n-move\n+changed");
            var parsed = RecoveryManifestJson.Parse(RecoveryManifestJson.Serialize(manifest));
            fixture.Validate(parsed);
            fixture.Validate(parsed with { Paths = parsed.Paths.Reverse().ToImmutableArray() });
            fixture.Validate(parsed with { RunId = Guid.NewGuid() }); // History spans runtime sessions.
            Assert(parsed.Paths.Length == 6 && before.SequenceEqual(fixture.Snapshot()), "Complete mixed graph without writes");
        });

        await check("recovery metadata binds the caller-selected workspace and record ID without trusting manifest root aliases", async () =>
        {
            using var fixture = new Fixture();
            using var other = new Fixture();
            var manifest = await fixture.Manifest();
            Assert(Reject(() => fixture.Validate(manifest with { ChangeId = Guid.NewGuid() }, manifest.ChangeId)).Path == "$.changeId", "Record directory identity");
            foreach (var root in new[] { other.Root, fixture.Root + "-sibling", Path.Combine(fixture.Root, "nested", ".."), fixture.Root + Path.DirectorySeparatorChar })
                Assert(Reject(() => fixture.Validate(manifest with { ProjectRoot = root })).Path == "$.projectRoot", "Expected canonical root");
            if (OperatingSystem.IsWindows()) fixture.Validate(manifest with { ProjectRoot = fixture.Root.ToUpperInvariant() });
            else Reject(() => fixture.Validate(manifest with { ProjectRoot = fixture.Root.ToUpperInvariant() }));
            RejectArgument(() => RecoveryManifestValidator.Validate(manifest, "relative", manifest.ChangeId));
            RejectArgument(() => RecoveryManifestValidator.Validate(manifest, fixture.Root, Guid.Empty));
        });

        await check("recovery metadata reuses private-path restrictions and rejects traversal, aliases and native path collisions", async () =>
        {
            using var fixture = new Fixture();
            var manifest = await fixture.Manifest();
            foreach (var path in new[]
            {
                "../outside.md", fixture.Root, "./source.md", "a/../source.md", "a//source.md", "source.md/", ".", " ", "a\\file.md", "x:stream", "bad\0.md", "bad\ud800.md",
                ".git/config", ".GiT/config", ".env", "src/.env.local", ".ssh/id_rsa", ".aws/credentials", ".config/settings", ".codex/config",
                ".agents/a.md", ".cloudflared/token", "tunnel-token", "secret.PEM", "folder/private.key/data.md"
            })
            {
                var rejected = false;
                try { fixture.Validate(RenameSingle(manifest, path)); } catch (JsonException) { rejected = true; }
                Assert(rejected, "Invalid project path: " + JsonSerializer.Serialize(path));
            }
            fixture.Validate(RenameSingle(manifest, "资料/示例 👋.md"));
            foreach (var path in new[] { "source.md.", "source.md " })
            {
                if (OperatingSystem.IsWindows()) Reject(() => fixture.Validate(RenameSingle(manifest, path)));
                else fixture.Validate(RenameSingle(manifest, path));
            }
            var move = await fixture.Manifest("*** Update File: source.md\n*** Move to: renamed.md");
            var caseMove = move with { Operations = [move.Operations[0] with { MoveTo = "SOURCE.MD" }],
                Paths = [move.Paths[0], move.Paths[1] with { Path = "SOURCE.MD" }] };
            if (OperatingSystem.IsWindows()) Reject(() => fixture.Validate(caseMove));
            else fixture.Validate(caseMove);
            Reject(() => fixture.Validate(manifest with { Operations = [manifest.Operations[0] with { Path = "SOURCE.MD" }] }));
        });

        await check("recovery metadata rejects duplicate, missing, unused, ancestral and shared operation paths", async () =>
        {
            using var fixture = new Fixture();
            var manifest = await fixture.Manifest();
            Reject(() => fixture.Validate(manifest with { Paths = manifest.Paths.Add(manifest.Paths[0]) }));
            Reject(() => fixture.Validate(manifest with { Operations = manifest.Operations.Add(manifest.Operations[0]) }));
            Reject(() => fixture.Validate(manifest with { Operations = [manifest.Operations[0] with { Path = "missing.md" }] }));
            Reject(() => fixture.Validate(manifest with { Paths = manifest.Paths.Add(manifest.Paths[0] with { Path = "unused.md", BackupFile = "before-0001.bin" }) }));
            var adds = await fixture.Manifest("*** Add File: one.md\n+one\n*** Add File: two.md\n+two");
            var ancestor = adds with { Operations = [adds.Operations[0] with { Path = "parent" }, adds.Operations[1] with { Path = "parent/child.md" }],
                Paths = [adds.Paths[0] with { Path = "parent" }, adds.Paths[1] with { Path = "parent/child.md" }] };
            Reject(() => fixture.Validate(ancestor));
            fixture.Validate(ancestor with { Operations = [ancestor.Operations[0], ancestor.Operations[1] with { Path = "parent-other/child.md" }],
                Paths = [ancestor.Paths[0], ancestor.Paths[1] with { Path = "parent-other/child.md" }] });
            var moves = await fixture.Manifest("*** Update File: source.md\n*** Move to: first.md\n*** Update File: move.md\n*** Move to: last.md");
            Reject(() => fixture.Validate(moves with { Operations = [moves.Operations[0], moves.Operations[1] with { Path = "first.md" }] }));
        });

        await check("recovery metadata requires action-specific existence facts and correct move endpoints", async () =>
        {
            using var fixture = new Fixture();
            var update = await fixture.Manifest();
            foreach (var action in new[] { "add", "delete", "move", "ADD", "rename", "", null })
                Reject(() => fixture.Validate(update with { Operations = [update.Operations[0] with { Action = action! }] }));
            Reject(() => fixture.Validate(update with { Operations = [update.Operations[0] with { MoveTo = "other.md" }] }));
            Reject(() => fixture.Validate(update with { Paths = [update.Paths[0] with { Before = null, BackupFile = null }] }));
            Reject(() => fixture.Validate(update with { Paths = [update.Paths[0] with { After = null }] }));
            var add = await fixture.Manifest("*** Add File: added.md\n+new");
            Reject(() => fixture.Validate(add with { Paths = [add.Paths[0] with { Before = update.Paths[0].Before, BackupFile = "before-0000.bin" }] }));
            Reject(() => fixture.Validate(add with { Paths = [add.Paths[0] with { After = null }] }));
            var delete = await fixture.Manifest("*** Delete File: source.md");
            Reject(() => fixture.Validate(delete with { Paths = [delete.Paths[0] with { Before = null, BackupFile = null }] }));
            Reject(() => fixture.Validate(delete with { Paths = [delete.Paths[0] with { After = update.Paths[0].After }] }));
            var move = await fixture.Manifest("*** Update File: move.md\n*** Move to: moved.md\n@@\n-move\n+updated");
            fixture.Validate(move); // Moving and changing content is valid; before/after hashes need not match.
            Reject(() => fixture.Validate(move with { Operations = [move.Operations[0] with { MoveTo = null }] }));
            Reject(() => fixture.Validate(move with { Paths = [move.Paths[0]] }));
            Reject(() => fixture.Validate(move with { Paths = [move.Paths[0] with { After = move.Paths[1].After }, move.Paths[1]] }));
            Reject(() => fixture.Validate(move with { Paths = [move.Paths[0], move.Paths[1] with { Before = move.Paths[0].Before, BackupFile = "before-0001.bin" }] }));
            Reject(() => fixture.Validate(move with { Paths = [move.Paths[0], move.Paths[1] with { After = null }] }));
        });

        await check("recovery backup references are unique generated filenames and absent originals cannot reference backups", async () =>
        {
            using var fixture = new Fixture();
            var manifest = await fixture.Manifest();
            foreach (var name in new string?[] { null, "", "../before-0000.bin", "/before-0000.bin", "C:/secret", "BEFORE-0000.bin", "before-0000.BIN",
                "before-0000.bin/..", "before-1.bin", "before-0064.bin", "before-9999.bin", "before-000١.bin" })
                Reject(() => fixture.Validate(manifest with { Paths = [manifest.Paths[0] with { BackupFile = name }] }));
            fixture.Validate(manifest with { Paths = [manifest.Paths[0] with { BackupFile = "before-0063.bin" }] });
            var two = await fixture.Manifest("*** Delete File: source.md\n*** Delete File: move.md");
            Reject(() => fixture.Validate(two with { Paths = [two.Paths[0], two.Paths[1] with { BackupFile = two.Paths[0].BackupFile }] }));
            var add = await fixture.Manifest("*** Add File: added.md\n+new");
            Reject(() => fixture.Validate(add with { Paths = [add.Paths[0] with { BackupFile = "before-0000.bin" }] }));
        });

        await check("recovery image semantics preserve permissions and make hash, length and record state consistent", async () =>
        {
            using var fixture = new Fixture();
            var update = await fixture.Manifest();
            var original = update.Paths[0];
            Reject(() => fixture.Validate(update with { Paths = [original with { Before = original.Before! with { ByteLength = 0 } }] }));
            const string emptyHash = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";
            Reject(() => fixture.Validate(update with { Paths = [original with { Before = original.Before! with { Sha256 = emptyHash } }] }));
            Reject(() => fixture.Validate(update with { Paths = [original with { After = original.Before! with { ByteLength = original.Before.ByteLength + 1 } }] }));
            var hostMismatch = original.Before! with { UnixMode = OperatingSystem.IsWindows() ? UnixFileMode.UserRead : null };
            Reject(() => fixture.Validate(update with { Paths = [original with { Before = hostMismatch }] }));
            Reject(() => fixture.Validate(update with { Paths = [original with { Before = original.Before! with { UnixModeIsCreateRequest = true } }] }));
            Reject(() => fixture.Validate(update with { Paths = [original with { After = original.After! with { UnixModeIsCreateRequest = true } }] }));
            if (!OperatingSystem.IsWindows())
                Reject(() => fixture.Validate(update with { Paths = [original with { After = original.After! with { UnixMode = original.Before!.UnixMode ^ UnixFileMode.UserExecute } }] }));
            var add = await fixture.Manifest("*** Add File: empty.md");
            foreach (var state in Enum.GetValues<RecoveryRecordState>())
            {
                var value = add with { State = state };
                if (state == RecoveryRecordState.Committed)
                    value = value with { Paths = [value.Paths[0] with { After = value.Paths[0].After! with { UnixModeIsCreateRequest = false } }] };
                fixture.Validate(value);
            }
            if (!OperatingSystem.IsWindows())
            {
                Reject(() => fixture.Validate(add with { State = RecoveryRecordState.Committed }));
                Reject(() => fixture.Validate(add with { Paths = [add.Paths[0] with { After = add.Paths[0].After! with { UnixModeIsCreateRequest = false } }] }));
            }
        });

        await check("recovery semantic validation is cancellable, read-only and does not claim backup or workspace verification", async () =>
        {
            using var fixture = new Fixture();
            var manifest = await fixture.Manifest();
            var bytes = RecoveryManifestJson.Serialize(manifest);
            var before = fixture.Snapshot();
            fixture.Validate(RecoveryManifestJson.Parse(bytes));
            Assert(before.SequenceEqual(fixture.Snapshot()), "No filesystem mutations");
            Directory.Delete(fixture.Root, recursive: true);
            fixture.Validate(manifest);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { RecoveryManifestValidator.Validate(manifest, fixture.Root, manifest.ChangeId, cancelled.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            fixture.Validate(manifest);
            Assert(!Directory.Exists(fixture.Root) && !Directory.EnumerateFileSystemEntries(fixture.DirectoryPath).Any(), "No workspace or storage lookups/creation");
        });
    }

    private static RecoveryManifest RenameSingle(RecoveryManifest manifest, string path) => manifest with
    { Operations = [manifest.Operations[0] with { Path = path }], Paths = [manifest.Paths[0] with { Path = path }] };
    private static JsonException Reject(Action action)
    {
        try { action(); } catch (JsonException error) { return error; }
        throw new Exception("Expected inconsistent recovery metadata rejection.");
    }
    private static void RejectArgument(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Expected invalid validation context rejection.");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Recovery metadata: " + message); }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-recovery-validation-{Guid.NewGuid():N}");
        public string Root { get; }
        private readonly ProjectWorkspace _workspace;
        private readonly PatchService _service;
        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(DirectoryPath, "project")).FullName;
            Put("source.md", "old\n"); Put("move.md", "move\n"); Put("delete.md", "gone\n");
            _workspace = new ProjectWorkspace(Root);
            _service = new PatchService(_workspace);
        }
        private void Put(string path, string text) => File.WriteAllText(Path.Combine(Root, path), text, new UTF8Encoding(false));
        public async Task<RecoveryManifest> Manifest(string body = "*** Update File: source.md\n@@\n-old\n+new") =>
            RecoveryDraftBuilder.Create(Guid.NewGuid(), await _service.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch")).Manifest;
        public void Validate(RecoveryManifest manifest, Guid? expectedId = null) => RecoveryManifestValidator.Validate(manifest, Root, expectedId ?? manifest.ChangeId);
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(DirectoryPath, path) + (Directory.Exists(path) ? "/" : ":" + Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() { _workspace.Mutations.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }
}
