using MiniMcp;
using MiniMcp.Services.Processes;
using MiniMcp.Services.Workspace;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Services;

internal static class WorkspaceQueryChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("queries: list depth, tree, hidden/generated exclusions and explicit subdirectory scope", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Put("src/deep/item.cs", "hello\n");
            fixture.Put(".hidden/note.txt", "hidden\n");
            fixture.Put("node_modules/package.txt", "dependency\n");
            System.IO.Directory.CreateDirectory(fixture.PathOf("empty"));
            var shallow = await fixture.Execution.ListFilesAsync(maxDepth: 1, format: "tree");
            Assert(shallow.Entries.Any(item => item.Path == "sample.txt") && shallow.Entries.All(item => item.Depth == 1), "Direct children listed");
            Assert(shallow.Entries.Single(item => item.Path == "src") is { Kind: "directory", Expanded: false } &&
                shallow.Entries.Single(item => item.Path == "empty") is { Kind: "directory", Expanded: false }, "Boundary directories remain unexpanded, including empty directories");
            Assert(!shallow.Complete && shallow.Truncated && shallow.Tree!.Contains("[not expanded]") &&
                shallow.Notices.Any(notice => notice.StartsWith("DepthLimit:") && notice.Contains("does not indicate a listing failure")), "Depth boundary describes coverage, not listing failure");
            Assert(!shallow.Entries.Any(item => item.Path.StartsWith('.') || item.Path.StartsWith("node_modules")), "Default scope");
            var full = await fixture.Execution.ListFilesAsync(maxDepth: 8, includeHidden: true, format: "tree");
            Assert(full.Entries.Any(item => item.Path == ".hidden/note.txt") && full.Entries.Any(item => item.Path == "src/deep/item.cs"), "Explicit hidden traversal");
            Assert((await fixture.Execution.ListFilesAsync("node_modules")).Entries.Single().Path == "node_modules/package.txt", "Explicit generated root");
            var zero = await fixture.Execution.ListFilesAsync(maxDepth: 0);
            Assert(zero.Entries.IsEmpty && !zero.Complete && zero.Truncated && zero.Notices.Any(notice => notice.StartsWith("DepthLimit:")), "Root depth zero is not expanded");
            await using var filesOnly = new Fixture();
            var complete = await filesOnly.Execution.ListFilesAsync(maxDepth: 1);
            Assert(complete.Complete && !complete.Truncated && complete.Entries.Single().Path == "sample.txt" &&
                !complete.Notices.Any(notice => notice.StartsWith("DepthLimit:")), "A root with only files is complete at depth one");
        });
        await check("queries: private paths are hard exclusions in lists and rg even under inclusive globs", async () =>
        {
            await using var fixture = new Fixture();
            foreach (var path in new[] { ".env", ".ENV.local", "keys/SECRET.PEM", "a/.aws/credentials", ".git/config", ".codex/notes", "tunnel-token" })
                fixture.Put(path, "query-secret\n");
            var list = await fixture.Execution.ListFilesAsync(maxDepth: 8, includeHidden: true);
            Assert(!JsonSerializer.Serialize(list).Contains("SECRET") && !list.Entries.Any(item => item.Path.Contains(".aws")), "No private names");
            Assert((await fixture.Execution.SearchTextAsync("query-secret", glob: "**")).Matches.IsEmpty, "Private contents excluded");
            foreach (var path in new[] { "../outside", ".env", "a/.aws", "C:/outside", "src\\file" })
            {
                await Reject<WorkspaceException>(() => fixture.Execution.ListFilesAsync(path));
                await Reject<WorkspaceException>(() => fixture.Execution.SearchTextAsync("hello", path));
            }
        });
        await check("queries: entry/output/work budgets bound list traversal and reject oversized search scope", async () =>
        {
            await using var fixture = new Fixture();
            for (var i = 0; i < 25; i++) fixture.Put($"many/{i}.txt", "hello\n");
            var limited = await fixture.Execution.ListFilesAsync("many", maxEntries: 3);
            Assert(limited.Entries.Length == 3 && limited.Truncated && limited.Notices.Contains("EntryLimit"), "Entry limit");
            Assert(JsonSerializer.SerializeToUtf8Bytes(limited).Length <= QueryText.MaxResultBytes, "Result bytes");
            for (var i = 0; i < QueryText.MaxWork + 1; i++) fixture.Put($"wide/{i}", "");
            var wide = await fixture.Execution.ListFilesAsync("wide", maxEntries: 2000);
            Assert(wide.Entries.Length <= 2000 && wide.Truncated, "Wide list remains bounded");
            var error = await Reject<WorkspaceException>(() => fixture.Execution.SearchTextAsync("hello", "wide"));
            Assert(error.Message.Contains("10000"), "Search inspection work cap");
        });
        await check("queries: real rg literal, regex, case, ignore/glob, selected root and no-match semantics", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Put("src/code.cs", "a.b\naxb\nHELLO\n你好 hello\n");
            fixture.Put("src/ignored.txt", "hello\n");
            fixture.Put(".gitignore", "src/ignored.txt\n");
            fixture.Put(".hidden", "hello\n");
            Assert((await fixture.Execution.SearchTextAsync("a.b", "src")).Matches.Length == 1, "Literal default");
            Assert((await fixture.Execution.SearchTextAsync("a.b", "src", regex: true)).Matches.Length == 2, "Regex opt-in");
            var matches = await fixture.Execution.SearchTextAsync("hello", glob: "*.cs", caseSensitive: false);
            Assert(matches.Matches.Length == 2 && matches.Matches.Any(item => item.Line == 4 && item.Preview == "你好 hello"), "Case/Unicode/1-based lines: " + JsonSerializer.Serialize(matches));
            var ignored = await fixture.Execution.SearchTextAsync("hello");
            Assert(ignored.Matches.All(item => item.Path != "src/ignored.txt" && item.Path != ".hidden"), "Project ignore and hidden defaults");
            var none = await fixture.Execution.SearchTextAsync("never-found-84739");
            Assert(none.Complete && !none.Truncated && none.Matches.IsEmpty, "No match is success");
            Assert((await Reject<WorkspaceException>(() => fixture.Execution.SearchTextAsync("[", regex: true))).Message.Contains("regular expression"), "Regex error distinct from no match/read failure");
            Assert((await Reject<WorkspaceException>(() => fixture.Execution.SearchTextAsync("hello", glob: "["))).Message.Contains("glob"), "Glob diagnostic");
            Assert((await fixture.Execution.SearchTextAsync("hello", "sample.txt")).Matches.Single().Path == "sample.txt", "Explicit file");
        });
        await check("queries: rg limits snippets/results and skips oversized, binary and invalid UTF8 data", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Put("many.txt", string.Concat(Enumerable.Repeat("needle\n", 150)));
            var limited = await fixture.Execution.SearchTextAsync("needle", maxResults: 3);
            Assert(limited.Matches.Length == 3 && limited.Truncated && !limited.Complete, "Result limit: " + JsonSerializer.Serialize(limited));
            fixture.Put("long.txt", "needle" + new string('x', 20_000) + "\u001b[31m\n");
            var longLine = await fixture.Execution.SearchTextAsync("needle", "long.txt");
            Assert(longLine.Matches.Single().PreviewTruncated && longLine.Matches.Single().Preview.Length <= 2000, "Snippet bound");
            fixture.Put("controls.txt", "needle\t\u001b[31m\n");
            Assert((await fixture.Execution.SearchTextAsync("needle", "controls.txt")).Matches.Single().Preview.Contains("\\u001b", StringComparison.OrdinalIgnoreCase), "Terminal controls escaped");
            File.WriteAllBytes(fixture.PathOf("invalid.txt"), [.. Encoding.UTF8.GetBytes("needle"), 0xff, 10]);
            File.WriteAllBytes(fixture.PathOf("binary.txt"), [0, .. Encoding.UTF8.GetBytes("needle\n")]);
            fixture.Put("large.txt", "needle" + new string('x', ProjectWorkspace.MaxFileBytes));
            var invalid = await fixture.Execution.SearchTextAsync("needle", "invalid.txt");
            Assert(invalid.Matches.IsEmpty && !invalid.Complete && invalid.Notices.Contains("NonUtf8DataSkipped"), "No base64 bytes escape");
            Assert((await fixture.Execution.SearchTextAsync("needle", "binary.txt")).Matches.IsEmpty, "Binary skipped");
            Assert((await fixture.Execution.SearchTextAsync("needle", "large.txt")).Matches.IsEmpty, "Oversized skipped");
            Assert(JsonSerializer.SerializeToUtf8Bytes(limited).Length <= QueryText.MaxResultBytes, "Serialized result bound");
        });
        await check("queries: bounded parameters, malicious pattern/glob and rg config never invoke a shell or preprocessor", async () =>
        {
            await using var fixture = new Fixture();
            var old = Environment.GetEnvironmentVariable("RIPGREP_CONFIG_PATH");
            var config = Path.Combine(fixture.Directory, "evil-rg-config");
            File.WriteAllText(config, "--pre=definitely-not-a-valid-command\n--text\n");
            Environment.SetEnvironmentVariable("RIPGREP_CONFIG_PATH", config);
            try
            {
                fixture.Put("--pre=attack.txt", "--pre=attack\n");
                var match = await fixture.Execution.SearchTextAsync("--pre=attack", glob: "--pre=attack.txt");
                Assert(match.Matches.Length == 1, "Values are separate arguments, no option injection: " + JsonSerializer.Serialize(match));
                Assert((await fixture.Execution.SearchTextAsync("$(touch marker); `attack` \"quoted\"")).Matches.IsEmpty, "No shell expansion");
                Assert((await fixture.Execution.SearchTextAsync("hello")).Matches.Length == 1, "External config ignored");
                foreach (var depth in new[] { -1, 33 }) await Reject<WorkspaceException>(() => fixture.Execution.SearchTextAsync("x", maxDepth: depth));
                foreach (var count in new[] { 0, 1001 }) await Reject<WorkspaceException>(() => fixture.Execution.SearchTextAsync("x", maxResults: count));
                await Reject<WorkspaceException>(() => fixture.Execution.SearchTextAsync(new string('a', 4097)));
                await Reject<WorkspaceException>(() => fixture.Execution.ListFilesAsync(maxDepth: 9));
                await Reject<WorkspaceException>(() => fixture.Execution.ListFilesAsync(format: "shell"));
            }
            finally { Environment.SetEnvironmentVariable("RIPGREP_CONFIG_PATH", old); }
        });
        await check("queries: dependency is optional, rg path is normalized, and retired policy field is rejected", async () =>
        {
            await using var fixture = new Fixture(rgPath: "missing-rg");
            Assert(fixture.Configuration.Settings.RgPath == Path.Combine(fixture.Directory, "missing-rg"), "Normalize optional path");
            Assert((await fixture.Execution.ListFilesAsync()).Entries.Length == 1, "No rg does not disable listing");
            await Reject<WorkspaceException>(() => fixture.Execution.SearchTextAsync("hello"));
            try { ConfigurationJson.ParsePolicy("""{"patchApprovalLimits":{}}"""); throw new Exception("Legacy alias accepted"); }
            catch (JsonException) { }
            Assert(ConfigurationJson.ParseSettings("""{"rgPath":"rg"}""").RgPath == "rg", "Configuration field");
            await File.WriteAllTextAsync(fixture.Configuration.SettingsFilePath, ConfigurationJson.SerializeSettings(fixture.Configuration.Settings));
            var store = new ConfigurationStore(new ConfigurationPaths(Path.Combine(fixture.Directory, "user"), Path.Combine(fixture.Directory, "state")));
            var before = fixture.Snapshot();
            var diagnostic = await store.CheckAsync(fixture.Configuration.SettingsFilePath, fixture.Directory);
            Assert(diagnostic.RipgrepPath is null && diagnostic.RipgrepError is not null && before.SequenceEqual(fixture.Snapshot()), "Optional dependency diagnostic without executing programs or blocking startup");
            await using var runtime = new McpRuntime(0);
            await runtime.StartAsync(fixture.Configuration);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            await using var client = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
            Assert((await client.ListToolsAsync()).Count == 8, "Missing rg keeps the fixed catalog");
            Assert((await client.CallToolAsync("search_text", new Dictionary<string, object?> { ["pattern"] = "hello" })).IsError == true, "Missing dependency is a tool error");
            Assert((await client.CallToolAsync("list_files", new Dictionary<string, object?>())).IsError != true && runtime.Snapshot.State == RuntimeState.Running,
                "Missing rg does not disrupt the Host or other tools");
        });
        await check("queries: links, linked ignore files, special names and search depth stay within scope", async () =>
        {
            await using var fixture = new Fixture();
            var outside = Path.Combine(fixture.Directory, "outside");
            System.IO.Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside-secret\n");
            var link = fixture.PathOf("linked");
            var links = true;
            try { System.IO.Directory.CreateSymbolicLink(link, outside); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            { links = false; Console.WriteLine("SKIP query symlink checks: Windows Developer Mode/permission required."); }
            if (links)
            {
                try
                {
                    Assert(!(await fixture.Execution.ListFilesAsync(includeHidden: true)).Entries.Any(item => item.Path == "linked"), "No link names or targets");
                    Assert((await fixture.Execution.SearchTextAsync("outside-secret", glob: "**")).Matches.IsEmpty, "No link traversal");
                    await Reject<WorkspaceException>(() => fixture.Execution.SearchTextAsync("outside-secret", "linked"));
                    File.CreateSymbolicLink(fixture.PathOf(".rgignore"), Path.Combine(outside, "secret.txt"));
                    try { await Reject<WorkspaceException>(() => fixture.Execution.SearchTextAsync("hello")); }
                    finally { File.Delete(fixture.PathOf(".rgignore")); }
                }
                finally { System.IO.Directory.Delete(link); }
            }
            fixture.Put("space [literal]/quote\".txt".Replace('"', '_'), "special-marker\n");
            fixture.Put("a/b/c.txt", "deep-marker\n");
            Assert((await fixture.Execution.SearchTextAsync("deep-marker", maxDepth: 1)).Matches.IsEmpty, "Depth excludes deeper files");
            Assert((await fixture.Execution.SearchTextAsync("deep-marker", maxDepth: 3)).Matches.Single().Path == "a/b/c.txt", "Depth includes boundary files");
            Assert((await fixture.Execution.SearchTextAsync("special-marker")).Matches.Single().Path.Contains("[literal]"), "Literal argument paths");
            File.WriteAllText(Path.Combine(fixture.Directory, ".ignore"), "sample.txt\n");
            Assert((await fixture.Execution.SearchTextAsync("hello")).Matches.Single().Path == "sample.txt", "External parent ignore disabled");
        });
        await check("queries: large serialized output is capped without silently calling it complete", async () =>
        {
            await using var fixture = new Fixture();
            var line = "needle" + new string('中', 1500) + "\n";
            fixture.Put("large-results.txt", string.Concat(Enumerable.Repeat(line, 50)));
            var result = await fixture.Execution.SearchTextAsync("needle", maxResults: 1000);
            Assert(result.Truncated && !result.Complete && result.Matches.Length > 0 && result.Matches.Length < 50, "Byte budget reached before count budget");
            Assert(JsonSerializer.SerializeToUtf8Bytes(result).Length <= QueryText.MaxResultBytes, "Escaped Unicode counts toward serialized budget");
        });
        await check("queries: query admission has two active slots, eight cancellable waiters and no abandoned work", async () =>
        {
            using var runner = new ReadOnlyQueryRunner();
            using var cancel = new CancellationTokenSource();
            var entered = 0;
            var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 10).Select(_ => runner.ExecuteAsync(async token =>
            {
                if (Interlocked.Increment(ref entered) == 2) active.TrySetResult();
                await release.Task.WaitAsync(token);
                return 0;
            }, cancel.Token)).ToArray();
            await active.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(entered == 2, "Only two queries active");
            await Reject<WorkspaceException>(() => runner.ExecuteAsync(_ => Task.FromResult(0), CancellationToken.None));
            cancel.Cancel();
            foreach (var task in tasks) await Reject<OperationCanceledException>(() => task);
            Assert(await runner.ExecuteAsync(_ => Task.FromResult(7), CancellationToken.None) == 7, "All slots reclaimed");
        });
        await check("queries: three modes expose identical real MCP catalogs and readonly results cause no project/record writes", async () =>
        {
            string? catalog = null;
            foreach (var mode in Enum.GetValues<ExecutionMode>())
            {
                await using var fixture = new Fixture(mode);
                await using var runtime = new McpRuntime(0);
                await runtime.StartAsync(fixture.Configuration);
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                await using var client = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
                var tools = await client.ListToolsAsync();
                var current = JsonSerializer.Serialize(tools.OrderBy(tool => tool.Name).Select(tool => tool.ProtocolTool));
                catalog ??= current;
                Assert(catalog == current && tools.Count == 8, "Stable schemas/descriptions/annotations in all modes");
                foreach (var tool in tools.Where(tool => tool.Name is "list_files" or "search_text"))
                    Assert(tool.ProtocolTool.Annotations is { ReadOnlyHint: true, DestructiveHint: false, OpenWorldHint: false }, "Read-only hints");
                var before = fixture.Snapshot();
                foreach (var (name, arguments) in new[]
                {
                    ("list_files", new Dictionary<string, object?> { ["format"] = "tree" }),
                    ("search_text", new Dictionary<string, object?> { ["pattern"] = "hello" })
                })
                {
                    var result = await client.CallToolAsync(name, arguments);
                    Assert(result.IsError != true && result.StructuredContent is not null, "Real MCP query success");
                }
                Assert(before.SequenceEqual(fixture.Snapshot()) && !System.IO.Directory.Exists(fixture.Recovery), "No query state writes");
                if (mode != ExecutionMode.FullAccess)
                    Assert((await client.CallToolAsync("bash", new Dictionary<string, object?> { ["command"] = "exit 0", ["approved"] = true })).IsError == true, "Client approval cannot enable Bash");
            }
        });
        await check("queries: caller cancellation and session stop terminate actual query descendants before releasing calls", async () =>
        {
            foreach (var sessionStop in new[] { false, true })
            {
                await using var fixture = new Fixture(rgPath: Environment.ProcessPath!);
                fixture.Put(".query-control", sessionStop ? "hold" : "partial");
                using var cancel = new CancellationTokenSource();
                var query = fixture.Execution.SearchTextAsync("hello", cancellationToken: cancel.Token);
                using var child = await RuntimeCallChecks.ObserveChildAsync(fixture.PathOf("query-child.pid"), query);
                try
                {
                    if (sessionStop) await fixture.Execution.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    else cancel.Cancel();
                    await Reject<OperationCanceledException>(() => query).WaitAsync(TimeSpan.FromSeconds(5));
                    await RuntimeCallChecks.AssertStoppedAsync(child);
                    Assert(fixture.Execution.ActiveCalls == 0, "Tracking released only after child cleanup");
                }
                finally { cancel.Cancel(); RuntimeCallChecks.KillIfRunning(child); }
            }
        });
        await check("queries: query timeout is distinct from cancellation and cleans actual child process", async () =>
        {
            await using var fixture = new Fixture(rgPath: Environment.ProcessPath!);
            fixture.Put(".query-control", "hold");
            var query = fixture.Execution.SearchTextAsync("hello");
            using var child = await RuntimeCallChecks.ObserveChildAsync(fixture.PathOf("query-child.pid"), query);
            try
            {
                var failure = await Reject<WorkspaceException>(() => query).WaitAsync(TimeSpan.FromSeconds(15));
                Assert(failure.Message.Contains("10 second"), "Explicit timeout result");
                await RuntimeCallChecks.AssertStoppedAsync(child);
                Assert(fixture.Execution.ActiveCalls == 0, "Timeout drained");
            }
            finally { RuntimeCallChecks.KillIfRunning(child); }
        });
        await check("queries: overlong and malformed child output stops the actual process and drains both streams", async () =>
        {
            foreach (var mode in new[] { "flood", "malformed" })
            {
                await using var fixture = new Fixture(rgPath: Environment.ProcessPath!);
                fixture.Put(".query-control", mode);
                var query = fixture.Execution.SearchTextAsync("hello");
                using var child = await RuntimeCallChecks.ObserveChildAsync(fixture.PathOf("query-child.pid"), query);
                try
                {
                    if (mode == "flood") Assert((await query.WaitAsync(TimeSpan.FromSeconds(5))).Truncated, "Protocol output cap");
                    else await Reject<WorkspaceException>(() => query).WaitAsync(TimeSpan.FromSeconds(5));
                    await RuntimeCallChecks.AssertStoppedAsync(child);
                    Assert(fixture.Execution.ActiveCalls == 0, "Fault/limit releases only after cleanup");
                }
                finally { RuntimeCallChecks.KillIfRunning(child); }
            }
        });
    }

    // Only lifecycle/fault checks use this controllable real child; functional matching checks use real rg.
    internal static async Task<int> ChildAsync(string[] arguments)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (arguments.Contains("--files")) { Console.Write("./sample.txt\0"); return 0; }
        var marker = Path.Combine(Environment.CurrentDirectory, "query-child.pid");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--execution-child");
        start.ArgumentList.Add(marker);
        using var child = Process.Start(start)!;
        while (!File.Exists(marker)) await Task.Delay(10);
        var mode = await File.ReadAllTextAsync(Path.Combine(Environment.CurrentDirectory, ".query-control"));
        if (mode == "malformed") { Console.WriteLine("invalid-json"); await Console.Out.FlushAsync(); }
        if (mode == "partial") { Console.Write("{\"type\":\"match\",\"data\":"); await Console.Out.FlushAsync(); }
        if (mode == "flood")
        {
            while (true)
            {
                Console.Write(new string('x', 4096));
                Console.Error.Write(new string('e', 4096));
            }
        }
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Query check: " + message); }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "minimcp-query-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public string Recovery { get; }
        public RuntimeConfiguration Configuration { get; }
        public ToolExecutionService Execution { get; }
        public Fixture(ExecutionMode mode = ExecutionMode.ReadOnly, string? rgPath = null)
        {
            Root = Path.Combine(Directory, "project");
            System.IO.Directory.CreateDirectory(Root);
            var paths = new ConfigurationPaths(Path.Combine(Directory, "user"), Path.Combine(Directory, "state"));
            Recovery = paths.RecoveryDirectory;
            var source = Path.Combine(Directory, "settings.json");
            Configuration = new ConfigurationValidator(paths).Validate(new AppSettings
            {
                ProjectRoot = Root,
                BashPath = mode == ExecutionMode.FullAccess ? ExternalProgramLocator.FindBash("", source)! : "absent-bash",
                RgPath = rgPath ?? ExternalProgramLocator.FindRipgrep("", source) ?? throw new Exception("Real rg required for query checks"),
                Authentication = new() { Mode = AuthenticationMode.Local }
            }, source, new() { Mode = mode });
            Put("sample.txt", "hello\n");
            Execution = new ToolExecutionService(Guid.NewGuid(), Configuration);
        }
        public string PathOf(string path) => Path.Combine(Root, path);
        public void Put(string path, string content)
        {
            var file = PathOf(path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content, new UTF8Encoding(false));
        }
        public string[] Snapshot() => System.IO.Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Order()
            .Select(path => Path.GetRelativePath(Root, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
        public async ValueTask DisposeAsync()
        {
            await Execution.DisposeAsync();
            var resolved = Path.GetFullPath(Directory);
            if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(resolved).StartsWith("minimcp-query-", StringComparison.Ordinal)) throw new Exception("Unexpected fixture root");
            System.IO.Directory.Delete(resolved, true);
        }
    }
}
