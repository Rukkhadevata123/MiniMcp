using System.Text;
using MiniMcp.Services;

internal static class PatchPlanChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("patch planning snapshots a mixed batch without creating staging files or directories", async () =>
        {
            using var fixture = new Fixture();
            fixture.Put("delete.txt", "remove\n");
            fixture.Put("move.txt", "move\n");
            var before = Snapshot(fixture.Directory);
            var plan = await fixture.Prepare("*** Add File: nested/new.txt\n+你好 👋\n*** Add File: empty.txt\n" +
                "*** Update File: sample.txt\n@@\n-old\n+updated\n*** Delete File: delete.txt\n" +
                "*** Update File: move.txt\n*** Move to: moved/new.txt");
            Assert(before.SequenceEqual(Snapshot(fixture.Directory)) && plan.Changes.Length == 5);
            Assert(plan.Changes[0].Before is null && plan.Changes[0].After is { } added && added.Length == Encoding.UTF8.GetByteCount("你好 👋\n"));
            Assert(plan.Changes[1].Before is null && plan.Changes[1].After is { Length: 0 });
            Assert(plan.Changes[2].Before!.Text == "old\n" && plan.Changes[3].After is null);
            Assert(plan.Changes[4].Summary.Action == "move" && plan.Changes[4].Summary.MoveTo == "moved/new.txt");
            var result = await fixture.Service.CommitAsync(plan);
            Assert(!result.DryRun && fixture.Get("sample.txt") == "updated\n" && fixture.Get("nested/new.txt") == "你好 👋\n");
            Assert(fixture.Get("empty.txt") == "" && !File.Exists(fixture.Path("delete.txt")) && !File.Exists(fixture.Path("move.txt")));
            Assert(fixture.Get("moved/new.txt") == "move\n");
        });

        await check("patch snapshots do not change when copied buffers or result summaries are modified", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Prepare("*** Update File: sample.txt\n@@\n-old\n+new");
            var original = plan.Changes[0].Before!.Bytes.ToArray();
            var replacement = plan.Changes[0].After!.Value.ToArray();
            original[0] = (byte)'X';
            replacement[0] = (byte)'Y';
            var summary = plan.ToResult(dryRun: true);
            summary.Changes[0] = new PatchChange("delete", "unrelated.txt", null, 0, 0);
            Assert(plan.Changes[0].Before!.Text == "old\n" && plan.ToResult(dryRun: true).Changes[0].Path == "sample.txt");
            await fixture.Service.CommitAsync(plan);
            Assert(fixture.Get("sample.txt") == "new\n");
        });

        await check("stale patch content, BOM and applicable permissions reject the whole batch before staging", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Prepare("*** Add File: preview/new.txt\n+first\n*** Update File: sample.txt\n@@\n-old\n+new");
            fixture.Put("sample.txt", "external edit\n");
            await RejectedWithoutWrites(fixture, plan);
            File.WriteAllBytes(fixture.Path("sample.txt"), [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("old\n")]);
            await RejectedWithoutWrites(fixture, plan);
            if (OperatingSystem.IsLinux())
            {
                fixture.Put("sample.txt", "old\n");
                var mode = File.GetUnixFileMode(fixture.Path("sample.txt"));
                File.SetUnixFileMode(fixture.Path("sample.txt"), mode ^ UnixFileMode.UserExecute);
                await RejectedWithoutWrites(fixture, plan);
            }
            Assert(!Directory.Exists(fixture.Path("preview")));
        });

        await check("patch commit rechecks disappeared sources, occupied destinations, parents and project roots", async () =>
        {
            foreach (var scenario in new[] { "source", "file", "directory", "parent", "move", "root" })
            {
                using var fixture = new Fixture();
                var operation = scenario == "move"
                    ? "*** Update File: sample.txt\n*** Move to: target.txt" : "*** Update File: sample.txt\n@@\n-old\n+new";
                var plan = await fixture.Prepare("*** Add File: preview/new.txt\n+first\n" + operation);
                switch (scenario)
                {
                    case "source": File.Delete(fixture.Path("sample.txt")); break;
                    case "file": Directory.CreateDirectory(fixture.Path("preview")); fixture.Put("preview/new.txt", "external\n"); break;
                    case "directory": Directory.CreateDirectory(fixture.Path("preview/new.txt")); break;
                    case "parent": fixture.Put("preview", "parent file\n"); break;
                    case "move": fixture.Put("target.txt", "external target\n"); break;
                    case "root": System.IO.Directory.Delete(fixture.Root, recursive: true); break;
                }
                await RejectedWithoutWrites(fixture, plan);
            }
            if (OperatingSystem.IsLinux())
            {
                using var fixture = new Fixture();
                var plan = await fixture.Prepare("*** Add File: linked/new.txt\n+new");
                var outside = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(fixture.Directory, "outside")).FullName;
                System.IO.Directory.CreateSymbolicLink(fixture.Path("linked"), outside);
                await Reject<WorkspaceException>(() => fixture.Service.CommitAsync(plan));
                Assert(!System.IO.Directory.EnumerateFileSystemEntries(outside).Any());
            }
        });

        await check("patch plans cannot be committed through a different workspace or service instance", async () =>
        {
            using var fixture = new Fixture();
            using var other = new Fixture();
            var plan = await fixture.Prepare("*** Add File: created.txt\n+new");
            var firstBefore = Snapshot(fixture.Directory);
            var otherBefore = Snapshot(other.Directory);
            await Reject<WorkspaceException>(() => other.Service.CommitAsync(plan));
            await Reject<WorkspaceException>(() => new PatchService(fixture.Workspace).CommitAsync(plan));
            Assert(firstBefore.SequenceEqual(Snapshot(fixture.Directory)) && otherBefore.SequenceEqual(Snapshot(other.Directory)));
            await fixture.Service.CommitAsync(plan);
            Assert(fixture.Get("created.txt") == "new\n");
        });

        await check("cancelled planning or commit releases mutation waits without altering the prepared plan", async () =>
        {
            using var fixture = new Fixture();
            var plan = await fixture.Prepare("*** Add File: pending/new.txt\n+new");
            var before = Snapshot(fixture.Directory);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Reject<OperationCanceledException>(() => fixture.Service.PrepareAsync("invalid", cancelled.Token));
            await Reject<OperationCanceledException>(() => fixture.Service.CommitAsync(plan, cancelled.Token));
            foreach (var planning in new[] { true, false })
            {
                await fixture.Workspace.Mutations.WaitAsync();
                try
                {
                    using var waiting = new CancellationTokenSource();
                    Task operation = planning ? fixture.Prepare("*** Add File: unused.txt\n+unused", waiting.Token)
                        : fixture.Service.CommitAsync(plan, waiting.Token);
                    Assert(!operation.IsCompleted);
                    waiting.Cancel();
                    await Reject<OperationCanceledException>(() => operation).WaitAsync(TimeSpan.FromSeconds(3));
                }
                finally { fixture.Workspace.Mutations.Release(); }
            }
            Assert(before.SequenceEqual(Snapshot(fixture.Directory)));
            await fixture.Service.CommitAsync(plan);
            Assert(fixture.Get("pending/new.txt") == "new\n");
        });

        if (OperatingSystem.IsWindows())
            await check("failed Windows commit rolls back and retry uses fresh staging state for the same plan", async () =>
            {
                using var fixture = new Fixture();
                var plan = await fixture.Prepare("*** Add File: nested/first.txt\n+first\n*** Update File: sample.txt\n@@\n-old\n+updated");
                var before = Snapshot(fixture.Directory);
                using (var locked = new FileStream(fixture.Path("sample.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    await Reject<IOException>(() => fixture.Service.CommitAsync(plan));
                Assert(before.SequenceEqual(Snapshot(fixture.Directory)));
                await fixture.Service.CommitAsync(plan);
                Assert(fixture.Get("nested/first.txt") == "first\n" && fixture.Get("sample.txt") == "updated\n");
                Assert(!System.IO.Directory.EnumerateFiles(fixture.Root, ".minimcp-*", SearchOption.AllDirectories).Any());
            });
    }

    private static async Task RejectedWithoutWrites(Fixture fixture, PatchPlan plan)
    {
        var before = Snapshot(fixture.Directory);
        await Reject<WorkspaceException>(() => fixture.Service.CommitAsync(plan));
        Assert(before.SequenceEqual(Snapshot(fixture.Directory)));
    }

    private static string[] Snapshot(string directory) => System.IO.Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => System.IO.Path.GetRelativePath(directory, path) +
            (System.IO.Directory.Exists(path) ? "/" : ":" + Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
    private static void Assert(bool condition) { if (!condition) throw new Exception("Patch plan assertion failed."); }
    private static async Task Reject<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); } catch (TException) { return; }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"minimcp-plan-{Guid.NewGuid():N}");
        public string Root { get; }
        public ProjectWorkspace Workspace { get; }
        public PatchService Service { get; }
        public Fixture()
        {
            Root = System.IO.Path.Combine(Directory, "project");
            System.IO.Directory.CreateDirectory(Root);
            Put("sample.txt", "old\n");
            Workspace = new ProjectWorkspace(Root);
            Service = new PatchService(Workspace);
        }
        public string Path(string relative) => System.IO.Path.Combine(Root, relative);
        public void Put(string relative, string text) => File.WriteAllText(Path(relative), text, new UTF8Encoding(false));
        public string Get(string relative) => File.ReadAllText(Path(relative));
        public Task<PatchPlan> Prepare(string body, CancellationToken token = default) => Service.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch", token);
        public void Dispose() { Workspace.Mutations.Dispose(); System.IO.Directory.Delete(Directory, recursive: true); }
    }
}
