using System.Diagnostics;
using System.Text;
using MiniMcp.Configuration;
using MiniMcp.Services;

var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-checks-{Guid.NewGuid():N}");
var root = Path.Combine(temporary, "project with spaces");
Directory.CreateDirectory(root);
var bashPath = ExternalProgramLocator.FindBash(Environment.GetEnvironmentVariable("MINIMCP_TEST_BASH_PATH") ?? "",
    Path.Combine(Environment.CurrentDirectory, "settings.json")) ?? throw new InvalidOperationException("Bash is required for core checks.");
var workspace = new ProjectWorkspace(root);
var patches = new PatchService(workspace);
var bash = new BashRunner(workspace, bashPath);
var passed = 0;
var failed = 0;

async Task<PatchResult> Patch(string body, bool dry = false, CancellationToken ct = default)
{
    var plan = await patches.PrepareAsync("*** Begin Patch\n" + body + "\n*** End Patch\n", ct);
    return dry ? plan.ToResult(dryRun: true) : await patches.CommitAsync(plan, ct);
}
void Put(string path, string text) => File.WriteAllText(Path.Combine(root, path), text, new UTF8Encoding(false));
string Get(string path) => File.ReadAllText(Path.Combine(root, path));
async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failed++; Console.WriteLine($"FAIL {name}: {exception}"); }
}

try
{
    await Check("read inclusive line ranges and mixed newlines", async () =>
    {
        Put("read.txt", "one\r\n你好 👋\ntail");
        var result = await workspace.ReadFileAsync("read.txt", 2, 3);
        Assert(result.Content == "你好 👋\ntail" && result.StartLine == 2 && result.EndLine == 3 && result.TotalLines == 3 && !result.Truncated);
        Assert((await workspace.ReadFileAsync("read.txt", 3, 100)).Content == "tail");
        await RejectAsync<WorkspaceException>(() => workspace.ReadFileAsync("read.txt", 0));
        await RejectAsync<WorkspaceException>(() => workspace.ReadFileAsync("read.txt", 4));
        await RejectAsync<WorkspaceException>(() => workspace.ReadFileAsync("read.txt", 2, 1));
    });
    await Check("empty files and trailing newlines have correct line counts", async () =>
    {
        Put("empty.txt", "");
        var empty = await workspace.ReadFileAsync("empty.txt");
        Assert(empty.Content == "" && empty.TotalLines == 0 && empty.EndLine == 0);
        Put("newline.txt", "a\n\n");
        Assert((await workspace.ReadFileAsync("newline.txt")).TotalLines == 2);
    });
    await Check("read output is bounded and does not split a surrogate pair", async () =>
    {
        Put("long.txt", new string('a', 15_999) + "👋\n");
        var result = await workspace.ReadFileAsync("long.txt");
        Assert(result.Truncated && result.Content.Length == 15_999);
        _ = ProjectWorkspace.Utf8.GetBytes(result.Content);
    });
    await Check("file byte limit, binary, invalid UTF-8 and BOM", async () =>
    {
        Put("limit.txt", new string('a', ProjectWorkspace.MaxFileBytes));
        Assert((await workspace.ReadFileAsync("limit.txt")).Truncated);
        Put("large.txt", new string('a', ProjectWorkspace.MaxFileBytes + 1));
        File.WriteAllBytes(Path.Combine(root, "binary"), [0, 1]);
        File.WriteAllBytes(Path.Combine(root, "invalid"), [0xff]);
        foreach (var path in new[] { "large.txt", "binary", "invalid" })
            await RejectAsync<WorkspaceException>(() => workspace.ReadFileAsync(path));
        File.WriteAllBytes(Path.Combine(root, "bom.txt"), [0xef, 0xbb, 0xbf, 0x61]);
        Assert((await workspace.ReadFileAsync("bom.txt")).Content == "a");
    });
    await Check("file tools reject traversal, private paths and absolute paths", async () =>
    {
        foreach (var path in new[] { "../outside", "a/../../outside", temporary, "C:/secret", "x:stream", "..\\outside",
            ".env", "src/.env.local", ".git/config", ".ssh/id_rsa", ".aws/credentials", ".codex/config", ".agents/a", ".cloudflared/a", "tunnel-token", "secret.pem", "private.key" })
        {
            Reject<WorkspaceException>(() => workspace.Resolve(path));
            await RejectAsync<WorkspaceException>(() => Patch($"*** Add File: {path}\n+x"));
        }
    });
    await Check("add Unicode and empty files; refuse overwrite", async () =>
    {
        var result = await Patch("*** Add File: src/示例.cs\n+你好 👋\n*** Add File: zero.txt");
        Assert(result.Changes.Length == 2 && result.Changes[0].AfterBytes == Encoding.UTF8.GetByteCount("你好 👋\n"));
        Assert(Get("src/示例.cs") == "你好 👋\n" && Get("zero.txt") == "");
        await RejectAsync<WorkspaceException>(() => Patch("*** Add File: src/示例.cs\n+overwrite"));
        Assert(Get("src/示例.cs") == "你好 👋\n");
    });
    await Check("dry run validates but creates no files or directories", async () =>
    {
        var result = await Patch("*** Add File: preview/deep/new.txt\n+hello", dry: true);
        Assert(result.DryRun && result.Changes[0].Action == "add" && !Directory.Exists(Path.Combine(root, "preview")));
        await RejectAsync<WorkspaceException>(() => Patch("*** Delete File: missing.txt", dry: true));
    });
    await Check("ordered chunks and anchor update exact context", async () =>
    {
        Put("update.txt", "first\na\nmiddle\na\nlast\n");
        await Patch("*** Update File: update.txt\n@@\n first\n-a\n+A\n@@ middle\n-a\n+B\n last");
        Assert(Get("update.txt") == "first\nA\nmiddle\nB\nlast\n");
    });
    await Check("ambiguous and mismatched context fails without modification", async () =>
    {
        Put("ambiguous.txt", "a\na\n");
        foreach (var body in new[] { "@@\n-a\n+b", "@@\n- a\n+b", "@@ missing\n-a\n+b" })
            await RejectAsync<WorkspaceException>(() => Patch("*** Update File: ambiguous.txt\n" + body));
        Assert(Get("ambiguous.txt") == "a\na\n");
        await Patch("*** Update File: ambiguous.txt\n@@\n-a\n+b\n*** End of File");
        Assert(Get("ambiguous.txt") == "a\nb\n");
    });
    await Check("UTF-8 BOM, CRLF, final newline and executable mode preserved", async () =>
    {
        var file = Path.Combine(root, "format.txt");
        File.WriteAllBytes(file, [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("a\r\nb")]);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, mode);
        await Patch("*** Update File: format.txt\n@@\n-a\n+A\n b");
        Assert(File.ReadAllBytes(file).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("A\r\nb"))));
        if (!OperatingSystem.IsWindows()) Assert(File.GetUnixFileMode(file) == mode);
        await Patch("*** Update File: format.txt\n@@\n+c");
        Assert(Get("format.txt") == "A\r\nb\r\nc");
    });
    await Check("mixed line endings on unchanged lines survive updates", async () =>
    {
        Put("mixed.txt", "a\r\nb\nc\r\n");
        await Patch("*** Update File: mixed.txt\n@@\n a\n-b\n+B\n c");
        Assert(Get("mixed.txt") == "a\r\nB\r\nc\r\n");
    });
    await Check("move with update, move only, delete and target collision", async () =>
    {
        Put("move.txt", "old\n");
        await RejectAsync<WorkspaceException>(() => Patch("*** Update File: move.txt\n*** Move to: read.txt"));
        await RejectAsync<WorkspaceException>(() => Patch("*** Update File: move.txt\n*** Move to: .env"));
        await Patch("*** Update File: move.txt\n*** Move to: moved/new.txt\n@@\n-old\n+new");
        Assert(!File.Exists(Path.Combine(root, "move.txt")) && Get("moved/new.txt") == "new\n");
        await Patch("*** Update File: moved/new.txt\n*** Move to: moved/final.txt");
        await Patch("*** Delete File: moved/final.txt");
        Assert(!File.Exists(Path.Combine(root, "moved/final.txt")));
    });
    await Check("whole patch validation prevents earlier operations from writing", async () =>
    {
        await RejectAsync<WorkspaceException>(() => Patch("*** Add File: atomic/new.txt\n+first\n*** Update File: read.txt\n@@\n-does not exist\n+bad"));
        Assert(!Directory.Exists(Path.Combine(root, "atomic")));
        await RejectAsync<WorkspaceException>(() => Patch("*** Delete File: read.txt\n*** Delete File: missing.txt"));
        Assert(Get("read.txt").StartsWith("one"));
    });
    await Check("duplicate paths, parent collisions and malformed patches rejected", async () =>
    {
        foreach (var body in new[] { "*** Add File: duplicate\n+x\n*** Add File: ./duplicate\n+y",
            "*** Add File: parent\n+x\n*** Add File: parent/child\n+y", "*** Update File: read.txt\n@@\n one",
            "*** Update File: read.txt\n@@ -1,1 +1,1 @@\n-one\n+x", "*** Add File: wrong\nno prefix" })
            await RejectAsync<WorkspaceException>(() => Patch(body));
        await RejectAsync<WorkspaceException>(() => patches.PrepareAsync("not a patch"));
        Assert(!File.Exists(Path.Combine(root, "duplicate")) && !File.Exists(Path.Combine(root, "parent")));
    });
    await Check("patch size, operation count, binary and resulting file byte limits", async () =>
    {
        await RejectAsync<WorkspaceException>(() => Patch("*** Add File: too-large\n+" + new string('中', 100_000)));
        await RejectAsync<WorkspaceException>(() => Patch("*** Add File: huge\n+" + new string('a', 1024 * 1024)));
        await RejectAsync<WorkspaceException>(() => Patch("*** Add File: nul\n+a\0b"));
        await RejectAsync<WorkspaceException>(() => Patch(string.Join('\n', Enumerable.Range(0, 65).Select(i => $"*** Add File: excess-{i}"))));
        await RejectAsync<WorkspaceException>(() => Patch("*** Delete File: binary"));
    });
    await Check("pre-cancelled patch leaves no files or staging artifacts", async () =>
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await RejectAsync<OperationCanceledException>(() => Patch("*** Add File: cancelled.txt\n+x", ct: cancelled.Token));
        Assert(!File.Exists(Path.Combine(root, "cancelled.txt")));
        Assert(!Directory.EnumerateFiles(root, ".minimcp-*", SearchOption.AllDirectories).Any());
    });
    if (!OperatingSystem.IsWindows())
    {
        await Check("file, directory, dangling and move-target symlinks rejected", async () =>
        {
            var outside = Path.Combine(temporary, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret"), "secret");
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
            File.CreateSymbolicLink(Path.Combine(root, "linked-file"), Path.Combine(outside, "secret"));
            File.CreateSymbolicLink(Path.Combine(root, "dangling"), Path.Combine(outside, "missing"));
            foreach (var path in new[] { "linked/secret", "linked-file", "dangling" })
            {
                await RejectAsync<WorkspaceException>(() => workspace.ReadFileAsync(path));
                await RejectAsync<WorkspaceException>(() => Patch($"*** Add File: {path}\n+x"));
                await RejectAsync<WorkspaceException>(() => Patch($"*** Update File: read.txt\n*** Move to: {path}"));
            }
            Assert(File.ReadAllText(Path.Combine(outside, "secret")) == "secret");
            Directory.CreateSymbolicLink(Path.Combine(temporary, "root-link"), root);
            Reject<WorkspaceException>(() => new ProjectWorkspace(Path.Combine(temporary, "root-link")));
        });
    }
    await Check("Bash requires an explicit existing absolute file path without fallback", () =>
    {
        foreach (var path in new[] { null, "", " ", "relative/bash", Path.Combine(temporary, "missing-bash"), root })
            Reject<InvalidOperationException>(() => new BashRunner(workspace, path!));
        return Task.CompletedTask;
    });
    await Check("real Bash supports pipelines, multiline commands, quoting and Unicode", async () =>
    {
        var result = await bash.RunAsync("printf '%s\\n' '你好 👋' | cat\nprintf '%s' 'a b; $literal'\nprintf 'diagnostic' >&2\nexit 7");
        Assert(result.ExitCode == 7 && !result.TimedOut && result.Stdout == "你好 👋\na b; $literal" && result.Stderr == "diagnostic");
    });
    await Check("workdir is validated and shell state does not persist", async () =>
    {
        Directory.CreateDirectory(Path.Combine(root, "cwd"));
        Assert((await bash.RunAsync("pwd", "cwd")).Stdout.TrimEnd().EndsWith("/cwd"));
        await bash.RunAsync("export MINIMCP_TEST_STATE=hello; cd cwd");
        var next = await bash.RunAsync("printf '%s' \"${MINIMCP_TEST_STATE-unset}\"; pwd");
        Assert(next.Stdout.StartsWith("unset") && next.Stdout.TrimEnd().EndsWith("project with spaces"));
        await RejectAsync<WorkspaceException>(() => bash.RunAsync("pwd", ".."));
        await RejectAsync<WorkspaceException>(() => bash.RunAsync("pwd", "missing"));
        await RejectAsync<WorkspaceException>(() => bash.RunAsync("pwd", timeoutSeconds: 0));
        await RejectAsync<WorkspaceException>(() => bash.RunAsync(new string('a', 65_537)));
    });
    await Check("stdin is closed and application secrets are not automatically inherited", async () =>
    {
        var previous = Environment.GetEnvironmentVariable("MINIMCP_CHECK_SECRET");
        var previousEnv = Environment.GetEnvironmentVariable("BASH_ENV");
        try
        {
            Put("startup.sh", "printf unexpected-startup");
            Environment.SetEnvironmentVariable("MINIMCP_CHECK_SECRET", "test-only-secret");
            Environment.SetEnvironmentVariable("BASH_ENV", Path.Combine(root, "startup.sh"));
            var result = await bash.RunAsync("if read -r x; then exit 9; fi; printf '%s' \"${MINIMCP_CHECK_SECRET-unset}\"");
            Assert(result.ExitCode == 0 && result.Stdout == "unset");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MINIMCP_CHECK_SECRET", previous);
            Environment.SetEnvironmentVariable("BASH_ENV", previousEnv);
        }
    });
    await Check("large stdout and stderr are drained and bounded independently", async () =>
    {
        var result = await bash.RunAsync("for ((i=0; i<3000; i++)); do printf 'abcdefghijklmnopqrstuvwxyz\\n'; printf 'abcdefghijklmnopqrstuvwxyz\\n' >&2; done");
        Assert(result.ExitCode == 0 && result.OutputTruncated && result.Stdout.Length == 16_000 && result.Stderr.Length == 16_000);
    });
    await Check("timeout returns partial output and releases the mutation lock", async () =>
    {
        var result = await bash.RunAsync("printf started; sleep 30", timeoutSeconds: 1);
        Assert(result.TimedOut && result.ExitCode is null && result.Stdout == "started");
        await Patch("*** Add File: after-timeout.txt\n+ok");
        Assert((await bash.RunAsync("exit 0")).ExitCode == 0);
    });
    await Check("client cancellation cleans up and releases the mutation lock", async () =>
    {
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await RejectAsync<OperationCanceledException>(() => bash.RunAsync("sleep 30", cancellationToken: cancelled.Token));
        Assert((await bash.RunAsync("exit 0")).ExitCode == 0);
    });
    if (OperatingSystem.IsLinux())
    {
        foreach (var normalExit in new[] { false, true })
            await Check(normalExit ? "normal shell exit cleans up background children" : "timeout kills descendants", async () =>
            {
                var result = await bash.RunAsync("sleep 30 & child=$!; printf '%s' \"$child\"; " + (normalExit ? "exit 0" : "wait"), timeoutSeconds: 1);
                Assert(normalExit ? result.ExitCode == 0 : result.TimedOut);
                await AssertStopped(int.Parse(result.Stdout));
            });
        await Check("client cancellation kills an observed child process", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            var running = bash.RunAsync("sleep 30 & printf '%s' \"$!\" > cancel-child.pid; wait", cancellationToken: cancelled.Token);
            for (var i = 0; i < 200 && !File.Exists(Path.Combine(root, "cancel-child.pid")); i++) await Task.Delay(10);
            cancelled.Cancel();
            await RejectAsync<OperationCanceledException>(() => running);
            await AssertStopped(int.Parse(Get("cancel-child.pid")));
        });
    }
    await PatchPlanChecks.RunAsync(Check);
    await PatchDiffChecks.RunAsync(Check);
    await PatchRiskChecks.RunAsync(Check);
    await PatchBatchChecks.RunAsync(Check);
    await PatchWritePolicyChecks.RunAsync(Check);
    await RecoveryDraftChecks.RunAsync(Check);
    Console.WriteLine($"\n{passed} passed, {failed} failed.");
    return failed == 0 ? 0 : 1;
}
finally { Directory.Delete(temporary, recursive: true); }

static async Task AssertStopped(int pid)
{
    for (var i = 0; i < 100; i++)
    {
        try
        {
            using var child = Process.GetProcessById(pid);
            var stat = $"/proc/{pid}/stat";
            if (child.HasExited || (File.Exists(stat) && File.ReadAllText(stat).Contains(") Z "))) return;
        }
        catch (ArgumentException) { return; }
        catch (FileNotFoundException) { return; }
        await Task.Delay(20);
    }
    throw new Exception($"Child process {pid} is still running.");
}

static void Assert(bool condition, string message = "Assertion failed.")
{
    if (!condition) throw new Exception(message);
}

static void Reject<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new Exception($"Expected {typeof(TException).Name}.");
}

static async Task RejectAsync<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new Exception($"Expected {typeof(TException).Name}.");
}
