using MiniMcp.Services.Patching;
using MiniMcp.Services.Workspace;
using System.Collections.Immutable;
using System.Text;
using MiniMcp.Configuration;

internal static class PatchBatchChecks
{
    private const PatchRiskReason BatchReasons = PatchRiskReason.PathLimitExceeded |
        PatchRiskReason.ChangedLineLimitExceeded | PatchRiskReason.ChangedByteLimitExceeded;

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("default patch batch limits accept exact boundaries and identify each exceeded dimension", async () =>
        {
            using var fixture = new Fixture();
            foreach (var count in new[] { 10, 11 })
            {
                var preview = await fixture.Preview(string.Join('\n', Enumerable.Range(0, count).Select(i => $"*** Add File: file{i}.md")));
                var risk = fixture.Classify(preview);
                Assert(risk.Size == new PatchBatchSize(count, 0, 0), "Distinct empty-file paths");
                Assert(risk.Reasons == (count == 10 ? PatchRiskReason.None : PatchRiskReason.PathLimitExceeded), "Default path boundary");
            }
            foreach (var count in new[] { 500, 501 })
            {
                var preview = await fixture.Preview("*** Add File: lines.md\n" + string.Join('\n', Enumerable.Range(0, count).Select(i => $"+line{i}")));
                var risk = fixture.Classify(preview);
                Assert(risk.Size.ChangedLines == count && risk.Size.Paths == 1, "Added lines");
                Assert(risk.Reasons == (count == 500 ? PatchRiskReason.None : PatchRiskReason.ChangedLineLimitExceeded), "Default line boundary");
            }
            foreach (var count in new[] { 65536, 65537 })
            {
                var risk = fixture.Classify(await fixture.Preview("*** Add File: bytes.md\n+" + new string('a', count - 1)));
                Assert(risk.Size == new PatchBatchSize(1, 1, count), "Real added bytes");
                Assert(risk.Reasons == (count == 65536 ? PatchRiskReason.None : PatchRiskReason.ChangedByteLimitExceeded), "Default byte boundary");
            }
        });

        await check("mixed patch batches sum additions and removals and retain every file and operation review reason", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("update.cs", "head\nold\ntail\n");
            fixture.Put("delete.md", "gone\r\n");
            fixture.Put("move.cs", "same\r\n");
            var before = fixture.Snapshot();
            var preview = await fixture.Preview("*** Add File: add.md\n+你好👋\n*** Update File: update.cs\n@@\n-old\n+new\n" +
                "*** Delete File: delete.md\n*** Update File: move.cs\n*** Move to: renamed.cs\n" +
                "*** Add File: Directory.Build.cs\n+b\n*** Add File: data.json\n+u");
            var bytes = Encoding.UTF8.GetByteCount("你好👋\nnew\nold\ngone\r\nb\nu\n");
            var limits = new ProjectEditLimits { MaxPaths = 6, MaxChangedLines = 5, MaxChangedBytes = bytes - 1 };
            var risk = fixture.Classify(preview, limits);
            Assert(risk.Size == new PatchBatchSize(7, 6, bytes), "Whole-batch totals rather than net changes");
            Assert(risk.Reasons == (BatchReasons | PatchRiskReason.CriticalFile | PatchRiskReason.UnknownFileType |
                PatchRiskReason.Delete | PatchRiskReason.Move), "All reasons preserved");
            Assert(risk.RestrictedInProjectEdit && risk.Changes[0].Reasons == PatchRiskReason.None, "Ordinary member does not clear whole-batch review");
            Assert(ReferenceEquals(risk.Preview, preview) && ReferenceEquals(risk.Plan, preview.Plan) && ReferenceEquals(risk.Limits, limits), "Preview and policy binding");
            Assert(before.SequenceEqual(fixture.Snapshot()), "No partial execution");
        });

        await check("pure moves count both paths and no changed content regardless of the moved file sizes", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("first.cs", new string('x', 4096) + "\n");
            fixture.Put("second.md", new string('y', 4096) + "\n");
            var limits = new ProjectEditLimits { MaxPaths = 2, MaxChangedLines = 0, MaxChangedBytes = 0 };
            var one = await fixture.Preview("*** Update File: first.cs\n*** Move to: moved.cs");
            var risk = fixture.Classify(one, limits);
            Assert(risk.Size == new PatchBatchSize(2, 0, 0) && risk.Reasons == PatchRiskReason.Move, "Exact two-path boundary still retains move review");
            var two = await fixture.Preview("*** Update File: first.cs\n*** Move to: moved.cs\n*** Update File: second.md\n*** Move to: moved.md");
            risk = fixture.Classify(two, limits with { MaxPaths = 3 });
            Assert(risk.Size == new PatchBatchSize(4, 0, 0) && risk.Reasons == (PatchRiskReason.Move | PatchRiskReason.PathLimitExceeded), "Multiple move paths");
        });

        await check("batch byte totals include BOM and exact newline changes from the complete diff", () =>
        {
            using var fixture = new Fixture();
            foreach (var (before, after, beforeBom, afterBom, lines, bytes) in new[]
            {
                ("same\n", "same\n", false, true, 0L, 3), ("same\n", "same\n", true, false, 0L, 3),
                ("x\r\n", "x\n", false, false, 2L, 5), ("tail\n", "tail", false, false, 2L, 9)
            })
            {
                var preview = PatchDiffBuilder.Create(fixture.Images(before, after, beforeBom, afterBom));
                var limits = new ProjectEditLimits { MaxPaths = 1, MaxChangedLines = (int)lines, MaxChangedBytes = bytes };
                var exact = fixture.Classify(preview, limits);
                Assert(exact.Size == new PatchBatchSize(1, lines, bytes) && exact.Reasons == PatchRiskReason.None, "Format boundary");
                Assert(fixture.Classify(preview, limits with { MaxChangedBytes = bytes - 1 }).Reasons == PatchRiskReason.ChangedByteLimitExceeded, "Format byte overage");
            }
            return Task.CompletedTask;
        });

        await check("unchanged context and review escapes do not inflate changed-line or byte thresholds", async () =>
        {
            using var fixture = new Fixture();
            var context = string.Concat(Enumerable.Range(0, 500).Select(i => $"unchanged{i}\n"));
            fixture.Put("context.md", context + "old\n" + context);
            var preview = await fixture.Preview("*** Update File: context.md\n@@\n-old\n+new");
            var limits = new ProjectEditLimits { MaxPaths = 1, MaxChangedLines = 2, MaxChangedBytes = 8 };
            var risk = fixture.Classify(preview, limits);
            Assert(risk.Size == new PatchBatchSize(1, 2, 8) && !risk.RestrictedInProjectEdit, "Context excluded");
            const string text = "中👋\t\u001b";
            preview = await fixture.Preview("*** Add File: controls.md\n+" + text);
            var bytes = Encoding.UTF8.GetByteCount(text + "\n");
            risk = fixture.Classify(preview, limits with { MaxChangedBytes = bytes });
            Assert(risk.Size == new PatchBatchSize(1, 1, bytes) && !risk.RestrictedInProjectEdit, "Raw UTF-8, not escaped display");
            Assert(Encoding.UTF8.GetByteCount(preview.Diff) > bytes && preview.Diff.Contains("\\u001b"), "Display actually escaped");
            Assert(fixture.Classify(preview, limits with { MaxChangedBytes = bytes - 1 }).Reasons == PatchRiskReason.ChangedByteLimitExceeded, "Raw byte overage");
        });

        await check("zero thresholds review only nonzero dimensions and an empty new file still counts as a path", async () =>
        {
            using var fixture = new Fixture();
            var limits = new ProjectEditLimits { MaxPaths = 1, MaxChangedLines = 0, MaxChangedBytes = 0 };
            var empty = await fixture.Preview("*** Add File: empty.md");
            var risk = fixture.Classify(empty, limits);
            Assert(risk.Size == new PatchBatchSize(1, 0, 0) && !risk.RestrictedInProjectEdit, "Zero changed-content boundary");
            Assert(fixture.Classify(empty, limits with { MaxPaths = 0 }).Reasons == PatchRiskReason.PathLimitExceeded, "No automatic file creation with zero path limit");
            var blank = await fixture.Preview("*** Add File: blank.md\n+");
            risk = fixture.Classify(blank, limits);
            Assert(risk.Size == new PatchBatchSize(1, 1, 1) && risk.Reasons ==
                (PatchRiskReason.ChangedLineLimitExceeded | PatchRiskReason.ChangedByteLimitExceeded), "Empty line has content effects");
        });

        await check("batch sums widen before addition and arithmetic overflow cannot produce a permissive assessment", async () =>
        {
            using var fixture = new Fixture();
            var preview = await fixture.Preview("*** Add File: empty.md");
            var limits = new ProjectEditLimits { MaxPaths = int.MaxValue, MaxChangedLines = int.MaxValue, MaxChangedBytes = int.MaxValue };
            // Synthetic statistics exercise numeric boundaries without weakening real diff/file budgets.
            var large = preview with { Statistics = new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue) };
            var risk = fixture.Classify(large, limits);
            Assert(risk.Size == new PatchBatchSize(1, 2L * int.MaxValue, 2L * int.MaxValue), "Wide totals");
            Assert(risk.Reasons == (PatchRiskReason.ChangedLineLimitExceeded | PatchRiskReason.ChangedByteLimitExceeded), "No Int32 wraparound");
            try { fixture.Classify(preview with { Statistics = new(0, 0, long.MaxValue, 1) }, limits); throw new Exception("Expected overflow rejection"); }
            catch (OverflowException) { }
            Assert(!Directory.EnumerateFiles(fixture.Root).Any(), "No writes");
        });

        await check("batch path counting follows normalized native identities and retains base duplicate-path rejection", async () =>
        {
            using var fixture = new Fixture();
            var one = await fixture.Preview("*** Add File: src/../one.md\n+new");
            Assert(fixture.Classify(one).Size.Paths == 1 && one.Plan.Changes[0].Summary.Path == "one.md", "Same source/destination counted once");
            const string differentCase = "*** Add File: file.md\n+first\n*** Add File: FILE.md\n+second";
            fixture.Put("move.md", "same\n");
            const string caseMove = "*** Update File: move.md\n*** Move to: MOVE.md";
            if (OperatingSystem.IsWindows())
            {
                await RejectAsync<WorkspaceException>(() => fixture.Preview(differentCase));
                await RejectAsync<WorkspaceException>(() => fixture.Preview(caseMove));
            }
            else
            {
                Assert(fixture.Classify(await fixture.Preview(differentCase)).Size.Paths == 2, "Case-sensitive distinct files");
                Assert(fixture.Classify(await fixture.Preview(caseMove)).Size.Paths == 2, "Case-sensitive move endpoints");
            }
        });

        await check("batch assessment remains bound to its preview without filesystem I/O and observes cancellation", async () =>
        {
            using var fixture = new Fixture();
            var preview = await fixture.Preview("*** Add File: nested/new.cs\n+new");
            var limits = new ProjectEditLimits { MaxPaths = 0 };
            var expected = fixture.Classify(preview, limits);
            Directory.Delete(fixture.Root, recursive: true);
            var actual = fixture.Classify(preview, limits);
            Assert(actual.Size == expected.Size && actual.Reasons == expected.Reasons && actual.Changes.SequenceEqual(expected.Changes), "Immutable assessment");
            Assert(ReferenceEquals(actual.Preview, preview) && !Directory.Exists(fixture.Root), "No reread or directory creation");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { PatchRiskClassifier.Classify(preview, fixture.SettingsPath, limits, cancelled.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Assert(!Directory.Exists(fixture.Root), "Cancellation has no effects");
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Patch batch: " + message); }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"minimcp-batch-{Guid.NewGuid():N}");
        public string Root { get; }
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        private readonly ProjectWorkspace _workspace;
        private readonly PatchService _service;
        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(_directory, "project")).FullName;
            _workspace = new ProjectWorkspace(Root);
            _service = new PatchService(_workspace);
        }
        public void Put(string path, string text) => File.WriteAllText(Path.Combine(Root, path), text, new UTF8Encoding(false));
        public async Task<PatchPreview> Preview(string body) => PatchDiffBuilder.Create(await _service.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch"));
        public PatchRiskAssessment Classify(PatchPreview preview, ProjectEditLimits? limits = null) =>
            PatchRiskClassifier.Classify(preview, SettingsPath, limits ?? new());
        public PatchPlan Images(string before, string after, bool beforeBom, bool afterBom)
        {
            var oldBytes = ProjectWorkspace.Encode(before, beforeBom).ToImmutableArray();
            var newBytes = ProjectWorkspace.Encode(after, afterBom).ToImmutableArray();
            var source = Path.Combine(Root, "format.md");
            return new(_service, [new(source, source, new(oldBytes, before, beforeBom, null), newBytes,
                new("update", "format.md", null, oldBytes.Length, newBytes.Length))]);
        }
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(_directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(_directory, path) + (Directory.Exists(path) ? "/" : ":" + Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() { _workspace.Mutations.Dispose(); Directory.Delete(_directory, recursive: true); }
    }
}
