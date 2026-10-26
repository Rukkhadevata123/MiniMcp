using MiniMcp.Services.Patching;
using MiniMcp.Services.Workspace;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

internal static class PatchDiffChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("complete patch previews cover mixed operations and count changed content rather than context", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("delete.txt", "gone\n");
            fixture.Put("move.txt", "unchanged\n");
            var plan = await fixture.Prepare("*** Add File: new.txt\n+你好\n*** Update File: sample.txt\n@@\n-old\n+new\n" +
                "*** Delete File: delete.txt\n*** Update File: move.txt\n*** Move to: renamed.txt");
            var before = fixture.Snapshot();
            var preview = PatchDiffBuilder.Create(plan);
            Assert(ReferenceEquals(preview.Plan, plan) && preview.Files.Length == 4);
            Assert(preview.Statistics == new PatchStatistics(2, 2, Encoding.UTF8.GetByteCount("你好\nnew\n"), Encoding.UTF8.GetByteCount("old\ngone\n")));
            Assert(preview.Files[3].Statistics == new PatchStatistics(0, 0, 0, 0));
            Assert(preview.Diff.Contains("--- \"/dev/null\"\n+++ \"b/new.txt\""));
            Assert(preview.Diff.Contains("--- \"a/move.txt\"\n+++ \"b/renamed.txt\""));
            Assert(preview.Diff.Contains("+\"你好\\n\"") && preview.Diff.Contains("-\"gone\\n\""));
            Assert(before.SequenceEqual(fixture.Snapshot()));
        });

        await check("diff hunks preserve separated edits, repeated lines and exact source/target ranges", () =>
        {
            using var fixture = new Fixture();
            var oldText = string.Concat(Enumerable.Range(1, 25).Select(line => $"line{line}\n"));
            var newText = oldText.Replace("line2\n", "changed2\n").Replace("line24\n", "changed24\n");
            var preview = PatchDiffBuilder.Create(fixture.Images(oldText, newText));
            Assert(preview.Diff.Split('\n').Count(line => line.StartsWith("@@ ", StringComparison.Ordinal)) == 2);
            Assert(!preview.Diff.Contains("\"line12\\n\""));
            Assert(preview.Statistics == new PatchStatistics(2, 2, Encoding.UTF8.GetByteCount("changed2\nchanged24\n"), Encoding.UTF8.GetByteCount("line2\nline24\n")));
            Assert(ApplyReview(oldText, preview.Diff) == newText);
            foreach (var (before, after) in new[]
            {
                ("a\nb\na\nc\n", "a\na\nb\nc\n"), ("a\nb\n", "new\na\nb\nlast\n"),
                ("one\ntwo\nthree\n", "two\n"), ("", "new\n"), ("old\n", "")
            }) Assert(ApplyReview(before, PatchDiffBuilder.Create(fixture.Images(before, after)).Diff) == after);
            return Task.CompletedTask;
        });

        await check("empty-file additions and deletions remain visible without invented line changes", () =>
        {
            using var fixture = new Fixture();
            var add = PatchDiffBuilder.Create(fixture.Images(null, ""));
            var delete = PatchDiffBuilder.Create(fixture.Images("", null));
            Assert(add.Diff.Contains("# action: add") && add.Diff.Contains("# before: absent"));
            Assert(delete.Diff.Contains("# action: delete") && delete.Diff.Contains("# after: absent"));
            Assert(!add.Diff.Contains("@@ ") && !delete.Diff.Contains("@@ "));
            Assert(add.Statistics == new PatchStatistics(0, 0, 0, 0) && delete.Statistics == add.Statistics);
            return Task.CompletedTask;
        });

        await check("BOM, mixed line endings, missing final newline and retained Unix modes are explicit", () =>
        {
            using var fixture = new Fixture();
            var bom = PatchDiffBuilder.Create(fixture.Images("same\n", "same\n", afterBom: true));
            Assert(bom.Diff.Contains("# before: utf8-bom=no") && bom.Diff.Contains("# after: utf8-bom=yes"));
            Assert(bom.Statistics == new PatchStatistics(0, 0, 3, 0));
            var before = "first\r\nsecond\nlast";
            var after = "first\nsecond\r\nlast\n";
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            var formats = PatchDiffBuilder.Create(fixture.Images(before, after, mode: mode));
            Assert(formats.Diff.Contains("eol=mixed; eof-newline=no; mode=0744"));
            Assert(formats.Diff.Contains("eol=mixed; eof-newline=yes; mode=0744"));
            Assert(formats.Diff.Contains("-\"first\\r\\n\"") && formats.Diff.Contains("+\"first\\n\""));
            Assert(formats.Statistics == new PatchStatistics(3, 3, Encoding.UTF8.GetByteCount(after), Encoding.UTF8.GetByteCount(before)));
            Assert(ApplyReview(before, formats.Diff) == after);
            var noNewline = PatchDiffBuilder.Create(fixture.Images("old", "new"));
            Assert(noNewline.Diff.Contains("-\"old\"\n+\"new\"\n"));
            Assert(ApplyReview("old", noNewline.Diff) == "new");
            return Task.CompletedTask;
        });

        await check("preview escaping makes controls and Unicode formatting visible while preserving raw statistics", () =>
        {
            using var fixture = new Fixture();
            const string text = "中👋\t\u001b[31m\u202e\u2028\U000e0001\\u001b\"\n";
            var preview = PatchDiffBuilder.Create(fixture.Images(null, text, path: "name\u202e\u2028.txt"));
            Assert(!preview.Diff.Contains('\u001b') && !preview.Diff.Contains('\t') && !preview.Diff.Contains('\r'));
            Assert(!preview.Diff.Contains('\u202e') && !preview.Diff.Contains('\u2028') && !preview.Diff.Contains("\U000e0001"));
            Assert(preview.Diff.Contains("\\u001b[31m") && preview.Diff.Contains("\\u202e") && preview.Diff.Contains("\\udb40\\udc01"));
            Assert(preview.Diff.Contains("\\\\u001b") && preview.Diff.Contains("中👋"));
            Assert(preview.Statistics.AddedBytes == Encoding.UTF8.GetByteCount(text) && preview.Statistics.AddedLines == 1);
            Assert(ApplyReview("", preview.Diff) == text);
            return Task.CompletedTask;
        });

        await check("preview generation uses its immutable snapshot and never rereads or changes workspace content", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Prepare("*** Update File: sample.txt\n@@\n-old\n+new");
            var expected = PatchDiffBuilder.Create(plan);
            fixture.Put("sample.txt", "external\n");
            var before = fixture.Snapshot();
            var actual = PatchDiffBuilder.Create(plan);
            Assert(actual.Diff == expected.Diff && actual.Statistics == expected.Statistics);
            Assert(actual.Diff.Contains("-\"old\\n\"\n+\"new\\n\""));
            Assert(before.SequenceEqual(fixture.Snapshot()));
        });

        await check("diff UTF-8 display limit accepts its exact boundary and rejects the complete oversized preview", () =>
        {
            using var fixture = new Fixture();
            var initialLength = PatchDiffBuilder.MaxDiffUtf8Bytes - 1024;
            var initial = PatchDiffBuilder.Create(fixture.Images(null, new string('a', initialLength) + "\n"));
            var exactLength = initialLength + PatchDiffBuilder.MaxDiffUtf8Bytes - Encoding.UTF8.GetByteCount(initial.Diff);
            var exact = PatchDiffBuilder.Create(fixture.Images(null, new string('a', exactLength) + "\n"));
            Assert(Encoding.UTF8.GetByteCount(exact.Diff) == PatchDiffBuilder.MaxDiffUtf8Bytes);
            Reject(() => PatchDiffBuilder.Create(fixture.Images(null, new string('a', exactLength + 1) + "\n")), "display limit");
            Reject(() => PatchDiffBuilder.Create(fixture.Images(null, new string('\u001b', 50_000) + "\n")), "display limit");
            var first = fixture.Images(null, new string('a', 140_000) + "\n", path: "one.txt");
            var second = fixture.Images(null, new string('b', 140_000) + "\n", path: "two.txt");
            Assert(PatchDiffBuilder.Create(first).Files.Length == 1);
            Reject(() => PatchDiffBuilder.Create(fixture.Combine(first, second)), "display limit");
            Assert(!Directory.EnumerateFiles(fixture.Root).Any(path => Path.GetFileName(path) != "sample.txt"));
            return Task.CompletedTask;
        });

        await check("diff matrix limit covers exact allocation boundaries and is shared across all files", () =>
        {
            using var fixture = new Fixture();
            static string Lines(string prefix, int count) => string.Concat(Enumerable.Range(0, count).Select(i => $"{prefix}{i}\n"));
            var exact = PatchDiffBuilder.Create(fixture.Images(Lines("old", 999), Lines("new", 999)));
            Assert(exact.Statistics.AddedLines == 999 && exact.Statistics.RemovedLines == 999);
            Reject(() => PatchDiffBuilder.Create(fixture.Images(Lines("old", 1000), Lines("new", 1000))), "computation limit");
            var first = fixture.Images(Lines("old", 750), Lines("new", 750), path: "one.txt");
            var second = fixture.Images(Lines("old", 750), Lines("new", 750), path: "two.txt");
            Assert(PatchDiffBuilder.Create(first).Files.Length == 1);
            Reject(() => PatchDiffBuilder.Create(fixture.Combine(first, second)), "computation limit");
            return Task.CompletedTask;
        });

        await check("diff comparison work is bounded even when many large moves produce almost no display", () =>
        {
            using var fixture = new Fixture();
            var text = new string('a', 120_000) + "\n";
            var moves = Enumerable.Range(0, 64).Select(i => fixture.Images(text, text, path: $"old{i}.txt", moveTo: $"new{i}.txt")).ToArray();
            Assert(PatchDiffBuilder.Create(moves[0]).Statistics == new PatchStatistics(0, 0, 0, 0));
            Reject(() => PatchDiffBuilder.Create(fixture.Combine(moves)), "computation limit");
            Assert(PatchDiffBuilder.Create(moves[0]).Files.Length == 1); // Budgets belong to one preview, not the process.
            return Task.CompletedTask;
        });

        await check("cancelled diff generation returns no preview or filesystem effects", () =>
        {
            using var fixture = new Fixture();
            var plan = fixture.Images("old\n", "new\n");
            var before = fixture.Snapshot();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { PatchDiffBuilder.Create(plan, cancellation.Token); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Assert(before.SequenceEqual(fixture.Snapshot()));
            Assert(PatchDiffBuilder.Create(plan).Files.Length == 1);
            return Task.CompletedTask;
        });
    }

    // Independently interpret the review hunks: check source rows/ranges and reconstruct the target text.
    private static string ApplyReview(string original, string diff)
    {
        var source = ProjectWorkspace.SplitLines(original).Select(line => line.Text + line.Ending).ToArray();
        var result = new StringBuilder();
        int cursor = 0, targetLines = 0, oldRows = 0, newRows = 0;
        int? expectedOld = null, expectedNew = null;
        void Finish() { if (expectedOld is not null) Assert(oldRows == expectedOld && newRows == expectedNew); }
        foreach (var row in diff.Split('\n'))
        {
            if (row.StartsWith("@@ ", StringComparison.Ordinal))
            {
                Finish();
                var fields = row.Split(' ');
                var oldRange = fields[1][1..].Split(',').Select(int.Parse).ToArray();
                var newRange = fields[2][1..].Split(',').Select(int.Parse).ToArray();
                var start = oldRange[1] == 0 ? oldRange[0] : oldRange[0] - 1;
                Assert(start >= cursor && start <= source.Length);
                while (cursor < start) { result.Append(source[cursor++]); targetLines++; }
                Assert(newRange[0] == (newRange[1] == 0 ? targetLines : targetLines + 1));
                expectedOld = oldRange[1]; expectedNew = newRange[1]; oldRows = 0; newRows = 0;
            }
            else if (expectedOld is not null && row.Length > 1 && row[0] is ' ' or '+' or '-')
            {
                var line = JsonSerializer.Deserialize<string>(row[1..])!;
                if (row[0] != '+') { Assert(cursor < source.Length && source[cursor++] == line); oldRows++; }
                if (row[0] != '-') { result.Append(line); newRows++; targetLines++; }
            }
        }
        Finish();
        while (cursor < source.Length) result.Append(source[cursor++]);
        return result.ToString();
    }

    private static void Reject(Action action, string message)
    {
        try { action(); } catch (WorkspaceException error) { Assert(error.Message.Contains(message) && error.Message.Contains("split")); return; }
        throw new Exception("Expected complete-preview rejection.");
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Patch diff assertion failed."); }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"minimcp-diff-{Guid.NewGuid():N}");
        public string Root { get; }
        private readonly ProjectWorkspace _workspace;
        private readonly PatchService _service;
        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(_directory, "project")).FullName;
            Put("sample.txt", "old\n");
            _workspace = new ProjectWorkspace(Root);
            _service = new PatchService(_workspace);
        }
        public void Put(string path, string text) => File.WriteAllText(Path.Combine(Root, path), text, new UTF8Encoding(false));
        public Task<PatchPlan> Prepare(string body) => _service.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch");
        public PatchPlan Images(string? before, string? after, string path = "sample.txt", string? moveTo = null,
            bool beforeBom = false, bool afterBom = false, UnixFileMode? mode = null)
        {
            var oldBytes = before is null ? (ImmutableArray<byte>?)null : ProjectWorkspace.Encode(before, beforeBom).ToImmutableArray();
            var newBytes = after is null ? (ImmutableArray<byte>?)null : ProjectWorkspace.Encode(after, afterBom).ToImmutableArray();
            var snapshot = before is null ? null : new TextFile(oldBytes!.Value, before, beforeBom, mode);
            var summary = new PatchChange(before is null ? "add" : after is null ? "delete" : moveTo is null ? "update" : "move",
                path, moveTo, oldBytes?.Length ?? 0, newBytes?.Length ?? 0);
            return new PatchPlan(_service, [new PlannedPatchChange(Path.Combine(Root, path), Path.Combine(Root, moveTo ?? path), snapshot, newBytes, summary)]);
        }
        public PatchPlan Combine(params PatchPlan[] plans) => new(_service, plans.SelectMany(plan => plan.Changes).ToImmutableArray());
        public string[] Snapshot() => Directory.EnumerateFileSystemEntries(_directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(_directory, path) + (Directory.Exists(path) ? "/" : ":" + Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() { _workspace.Mutations.Dispose(); Directory.Delete(_directory, recursive: true); }
    }
}
