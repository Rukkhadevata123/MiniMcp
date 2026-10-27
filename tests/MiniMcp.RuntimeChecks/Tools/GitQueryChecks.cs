using MiniMcp;
using MiniMcp.Services.Workspace;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Services;
using MiniMcp.Cli;

internal static class GitQueryChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("git queries: real clean/dirty/staged/untracked state and index/refs/config remain unchanged", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Put("sample.txt", "base\n");
            await fixture.Commit("initial");
            Assert((await fixture.Execution.GitStatusAsync()).Entries.IsEmpty, "Clean status");
            fixture.Put("sample.txt", "staged\n");
            await fixture.Git("add", "--", "sample.txt");
            fixture.Put("sample.txt", "working\n");
            fixture.Put("untracked.txt", "untracked-body\n");
            var before = fixture.Snapshot();
            var status = await fixture.Execution.GitStatusAsync();
            Assert(status.Entries.Any(entry => entry.Path == "sample.txt" && entry.IndexStatus == "M" && entry.WorktreeStatus == "M"), "Two comparisons");
            Assert(status.Entries.Any(entry => entry.Path == "untracked.txt" && entry.IndexStatus == "?"), "Untracked status");
            var working = await fixture.Execution.GitDiffAsync();
            var staged = await fixture.Execution.GitDiffAsync(staged: true);
            Assert(working.Files.Single().Lines.Contains("-staged") && working.Files.Single().Lines.Contains("+working"), "Worktree versus index");
            Assert(staged.Files.Single().Lines.Contains("-base") && staged.Files.Single().Lines.Contains("+staged"), "Index versus HEAD");
            Assert(!JsonSerializer.Serialize(working).Contains("untracked-body"), "No untracked bodies");
            Assert((await fixture.Execution.GitLogAsync()).Commits.Single().Subject == "initial", "HEAD summaries");
            Assert(before.SequenceEqual(fixture.Snapshot()) && !Directory.Exists(fixture.Recovery), "Byte/mode/mtime facts unchanged, no recovery records");
        });
        await check("git queries: unborn HEAD is explicit and staged additions can be inspected", async () =>
        {
            await using var fixture = new Fixture();
            var log = await fixture.Execution.GitLogAsync();
            Assert(log.Head is null && log.Commits.IsEmpty && log.Complete && log.Notices.Any(notice => notice.StartsWith("NoHeadCommit")), "Unborn state");
            fixture.Put("first.txt", "first\n");
            await fixture.Git("add", ".");
            var diff = await fixture.Execution.GitDiffAsync(staged: true);
            Assert(diff.Files.Single().Path == "first.txt" && diff.Files.Single().Lines.Contains("+first"), "Unborn cached diff");
        });
        await check("git queries: private tracked/untracked paths and private-only history are excluded before output", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Put("public.txt", "public-base\n");
            foreach (var path in new[] { ".env", "a/.ENV.local", "nested/.aws/credentials", "keys/SECRET.PEM", "tunnel-token" })
                fixture.Put(path, "private-base\n");
            await fixture.Commit("initial-public");
            fixture.Put(".env", "private-update\n");
            await fixture.Commit("private-only");
            fixture.Put("public.txt", "public-change\n");
            foreach (var path in new[] { ".env", "a/.ENV.local", "nested/.aws/credentials", "keys/SECRET.PEM", "new.key" })
                fixture.Put(path, "forbidden-marker\n");
            var before = fixture.Snapshot();
            var status = JsonSerializer.Serialize(await fixture.Execution.GitStatusAsync());
            var diff = JsonSerializer.Serialize(await fixture.Execution.GitDiffAsync());
            Assert(!status.Contains(".env", StringComparison.OrdinalIgnoreCase) && !status.Contains("SECRET.PEM") && !status.Contains("new.key"), "Names filtered");
            Assert(!diff.Contains("forbidden-marker") && !diff.Contains("private-base"), "Bodies filtered");
            Assert(!(await fixture.Execution.GitLogAsync()).Commits.Any(entry => entry.Subject == "private-only"), "Private-only commits outside scoped log");
            foreach (var path in new[] { ".env", "nested/.aws", "../outside", "C:/outside", "nested\\file" })
            {
                await Reject<WorkspaceException>(() => fixture.Execution.GitStatusAsync(path));
                await Reject<WorkspaceException>(() => fixture.Execution.GitDiffAsync(path));
                await Reject<WorkspaceException>(() => fixture.Execution.GitLogAsync(path));
            }
            Assert(before.SequenceEqual(fixture.Snapshot()), "Private filtering is read-only");
        });
        await check("git queries: literal special names and deletion paths cannot turn into pathspecs or revisions", async () =>
        {
            await using var fixture = new Fixture();
            var names = new List<string> { "space [literal]/中.txt", "-dash.txt", "glob*.txt" };
            if (OperatingSystem.IsWindows()) names.Remove("glob*.txt");
            else names.Add("line\nfile.txt");
            foreach (var name in names) fixture.Put(name, "old\n");
            fixture.Put("other.txt", "other\n");
            await fixture.Commit("special files");
            foreach (var name in names) fixture.Put(name, "new\n");
            foreach (var name in names)
            {
                Assert((await fixture.Execution.GitStatusAsync(name)).Entries.Single().Path == name, "Literal status path");
                Assert((await fixture.Execution.GitDiffAsync(name)).Files.Single().Path == name, "Literal diff path");
                Assert((await fixture.Execution.GitLogAsync(name)).Commits.Length == 1, "Literal history path");
            }
            File.Delete(fixture.PathOf(names[0]));
            Assert((await fixture.Execution.GitDiffAsync(names[0])).Files.Single().Status == "D", "Deleted path remains queryable");
            await Reject<WorkspaceException>(() => fixture.Execution.GitStatusAsync(":(glob)**"));
        });
        await check("git queries: entry/file/line/result budgets and binary or oversized changes are explicit", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Put("base.txt", "base\n");
            await fixture.Commit("base");
            fixture.Put("base.txt", string.Concat(Enumerable.Repeat("new line\n", 30)));
            fixture.Put("second.txt", "second\n");
            fixture.Put("third.txt", "third\n");
            await fixture.Git("add", "--", "second.txt", "third.txt");
            for (var i = 0; i < 10; i++) fixture.Put($"untracked/{i}.txt", "x\n");
            Assert((await fixture.Execution.GitStatusAsync(maxEntries: 3)) is { Entries.Length: 3, Truncated: true, Complete: false }, "Entry budget");
            Assert((await fixture.Execution.GitDiffAsync(maxLines: 2)).Truncated, "Line budget");
            var limited = await fixture.Execution.GitDiffAsync(staged: true, maxFiles: 1);
            Assert(limited.Files.Length == 1 && limited.Truncated, "File budget");
            Assert(JsonSerializer.SerializeToUtf8Bytes(limited).Length <= QueryText.MaxResultBytes, "Byte budget");
            fixture.Put("base.txt", "needle" + new string('中', 3000) + "\n");
            Assert((await fixture.Execution.GitDiffAsync()).Notices.Contains("LinePreviewsClipped"), "Long lines clipped explicitly");
            File.WriteAllBytes(fixture.PathOf("base.txt"), [0, 1, 2]);
            Assert((await fixture.Execution.GitDiffAsync()).Notices.Contains("BinaryOrNonUtf8FilesSkipped"), "Binary current image skipped");
            fixture.Put("base.txt", new string('x', ProjectWorkspace.MaxFileBytes + 1));
            Assert((await fixture.Execution.GitDiffAsync()).Notices.Contains("OversizedFilesSkipped"), "Large worktree image skipped");
            await fixture.Git("add", "--", "base.txt");
            fixture.Put("base.txt", "small\n");
            Assert((await fixture.Execution.GitDiffAsync()).Notices.Contains("OversizedObjectsSkipped"), "Large index blob skipped");
            foreach (var count in new[] { 0, 101 }) await Reject<WorkspaceException>(() => fixture.Execution.GitLogAsync(maxCount: count));
            await Reject<WorkspaceException>(() => fixture.Execution.GitDiffAsync(maxFiles: 101));
            await Reject<WorkspaceException>(() => fixture.Execution.GitDiffAsync(maxLines: 4001));
        });
        await check("git queries: HEAD log is bounded and escapes control characters without exposing full messages", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Put("a.txt", "one\n"); await fixture.Commit("first");
            fixture.Put("a.txt", "two\n"); await fixture.Commit("second\t中\n\nbody-not-exported");
            var log = await fixture.Execution.GitLogAsync(maxCount: 1);
            Assert(log.Commits.Length == 1 && log.Truncated && log.Commits[0].Subject.Contains("\\t") && !JsonSerializer.Serialize(log).Contains("body-not-exported"), "Bounded subject only");
        });
        await check("git queries: serialized Unicode diff output stops at the byte budget", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Put("a.txt", "base\n"); await fixture.Commit("base");
            fixture.Put("a.txt", string.Concat(Enumerable.Repeat("needle" + new string('中', 1500) + "\n", 50)));
            var diff = await fixture.Execution.GitDiffAsync(maxLines: 4000);
            Assert(diff.Truncated && !diff.Complete && diff.Files.Length == 1, "Byte cap reaches before line cap");
            Assert(JsonSerializer.SerializeToUtf8Bytes(diff).Length <= QueryText.MaxResultBytes, "Serialized byte cap includes escapes");
        });
        await check("git queries: renames cannot expose private endpoints and indexed links never expose targets", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Put("source.txt", "ordinary-source\n"); await fixture.Commit("base");
            File.Move(fixture.PathOf("source.txt"), fixture.PathOf(".env.moved"));
            await fixture.Git("add", "-A");
            var status = await fixture.Execution.GitStatusAsync();
            var diff = await fixture.Execution.GitDiffAsync(staged: true);
            Assert(status.Entries.All(entry => !entry.Path.StartsWith(".env")) && diff.Files.Single().Path == "source.txt" && diff.Files.Single().Status == "D", "Separate deletion, no private rename endpoint");
            fixture.Put("target-source", "outside-target-must-not-be-returned\n");
            var hash = (await fixture.Git("hash-object", "-w", "--", "target-source")).Trim();
            await fixture.Git("update-index", "--add", "--cacheinfo", "120000", hash, "link.txt");
            var indexed = await fixture.Execution.GitDiffAsync("link.txt", staged: true);
            Assert(indexed.Files.IsEmpty && !indexed.Complete && !JsonSerializer.Serialize(indexed).Contains("outside-target"), "No indexed link body");
            Assert((await fixture.Execution.GitStatusAsync("link.txt")).Entries.IsEmpty, "No indexed link status entry");
        });
        await check("git queries: nonrepositories, damaged HEAD and bounded repository metadata give explicit errors", async () =>
        {
            await using var fixture = new Fixture();
            var config = File.ReadAllBytes(fixture.ConfigFile);
            File.WriteAllText(fixture.ConfigFile, new string('x', 65537));
            await Reject<WorkspaceException>(() => fixture.Execution.GitStatusAsync());
            File.WriteAllBytes(fixture.ConfigFile, config);
            var head = Path.Combine(fixture.Root, ".git", "HEAD");
            File.WriteAllText(head, "invalid HEAD metadata");
            await Reject<WorkspaceException>(() => fixture.Execution.GitLogAsync());
            var plain = Path.Combine(fixture.DirectoryPath, "plain"); Directory.CreateDirectory(plain);
            await using var noRepository = fixture.CreateExecution(plain);
            await Reject<WorkspaceException>(() => noRepository.GitStatusAsync());
        });
        await check("git queries: common local settings and included configuration work in every mode", async () =>
        {
            foreach (var mode in Enum.GetValues<ExecutionMode>())
            {
                await using var fixture = new Fixture(mode);
                fixture.Put("a.txt", "one\n"); await fixture.Commit("initial"); fixture.Put("a.txt", "two\n");
                var included = Path.Combine(fixture.DirectoryPath, "included-config");
                File.WriteAllText(included, "[pull]\nrebase = true\n");
                await fixture.Git("config", "--local", "include.path", included);
                await fixture.Git("config", "--local", "branch.main.vscode-merge-base", "origin/main");
                await fixture.Git("config", "--local", "branch.feature/topic.vscode-merge-base", "origin/feature/topic");
                await fixture.Git("config", "--local", "commit.gpgsign", "true");
                Assert((await fixture.Git("config", "--local", "--includes", "--get", "pull.rebase")).Trim() == "true", "Git loads the included configuration");
                var before = fixture.Snapshot();
                Assert((await fixture.Execution.GitStatusAsync()).Entries.Any(entry => entry.Path == "a.txt"), "Status with common config in " + mode);
                Assert((await fixture.Execution.GitDiffAsync()).Files.Single().Lines.Contains("+two"), "Diff with common config in " + mode);
                Assert((await fixture.Execution.GitLogAsync()).Commits.Single().Subject == "initial", "Log with common config in " + mode);
                Assert(before.SequenceEqual(fixture.Snapshot()) && !Directory.Exists(fixture.Recovery), "Ordinary query leaves repository unchanged in " + mode);
            }
        });
        await check("git queries: local clean filters can execute outside the project in every mode", async () =>
        {
            foreach (var mode in Enum.GetValues<ExecutionMode>())
            {
                await using var fixture = new Fixture(mode);
                fixture.Put(".gitattributes", "*.txt filter=demo\n");
                fixture.Put("a.txt", "one\n"); await fixture.Commit("initial"); fixture.Put("a.txt", "two\n");
                var marker = Path.Combine(fixture.DirectoryPath, "filter-ran");
                await fixture.Git("config", "--local", "filter.demo.clean", HelperCommand(marker));
                Assert(!File.Exists(marker), "No marker before querying");
                _ = await fixture.Execution.GitDiffAsync();
                Assert(File.Exists(marker) && File.ReadAllText(marker) == "helper executed", "Git invoked the configured helper outside the project in " + mode);
                Assert(!Directory.Exists(fixture.Recovery), "Filter effects do not create patch recovery records");
                File.Delete(marker);
                await using var runtime = new McpRuntime(0);
                await runtime.StartAsync(fixture.Configuration);
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                await using var client = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
                var result = await client.CallToolAsync("git_diff", new Dictionary<string, object?>());
                Assert(result.IsError != true && result.StructuredContent is not null && File.Exists(marker), "Real MCP git_diff invokes the configured helper in " + mode);
                Assert(!Directory.Exists(fixture.Recovery), "MCP filter effects do not create patch recovery records");
            }
        });
        await check("git queries: fixed options still disable local external diff, textconv and fsmonitor", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Put(".gitattributes", "*.txt diff=demo\n");
            fixture.Put("a.txt", "one\n"); await fixture.Commit("initial"); fixture.Put("a.txt", "two\n");
            var marker = Path.Combine(fixture.DirectoryPath, "disabled-helper-ran");
            var helper = HelperCommand(marker);
            await fixture.Git("config", "--local", "core.fsmonitor", helper);
            await fixture.Git("config", "--local", "diff.external", helper);
            await fixture.Git("config", "--local", "diff.demo.textconv", helper);
            var before = fixture.Snapshot();
            foreach (var query in fixture.Queries()) await query();
            Assert(!File.Exists(marker) && before.SequenceEqual(fixture.Snapshot()), "Explicitly disabled helpers do not run or write the repository");
        });
        await check("git queries: inherited config/helpers/paths are ignored and cannot redirect a query", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Put("a.txt", "one\n"); await fixture.Commit("base");
            fixture.Put("a.txt", "two\n");
            fixture.Put(".gitattributes", "*.txt filter=attack diff=attack\n");
            var marker = Path.Combine(fixture.DirectoryPath, "ambient-helper-ran");
            var global = Path.Combine(fixture.DirectoryPath, "global-config");
            File.WriteAllText(global, "[filter \"attack\"]\nclean = " + HelperCommand(marker) + "\n[diff]\nexternal = " + HelperCommand(marker) + "\n");
            var values = new Dictionary<string, string>
            {
                ["GIT_CONFIG_GLOBAL"] = global, ["GIT_CONFIG_SYSTEM"] = global, ["GIT_DIR"] = fixture.DirectoryPath,
                ["GIT_INDEX_FILE"] = Path.Combine(fixture.DirectoryPath, "external-index"), ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = "core.fsmonitor", ["GIT_CONFIG_VALUE_0"] = HelperCommand(marker),
                ["GIT_EXTERNAL_DIFF"] = HelperCommand(marker), ["GIT_OBJECT_DIRECTORY"] = fixture.DirectoryPath,
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = fixture.DirectoryPath, ["GIT_PAGER"] = HelperCommand(marker)
            };
            var previous = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var (key, value) in values) Environment.SetEnvironmentVariable(key, value);
                var before = fixture.Snapshot();
                foreach (var query in fixture.Queries()) await query();
                Assert(!File.Exists(marker) && !File.Exists(values["GIT_INDEX_FILE"]) && before.SequenceEqual(fixture.Snapshot()), "Ambient inputs isolated and no index writes");
            }
            finally { foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value); }
        });
        await check("git queries: external object stores/worktree layouts and metadata links are unsupported", async () =>
        {
            await using var fixture = new Fixture();
            foreach (var relative in new[] { "commondir", "objects/info/alternates", "objects/info/http-alternates", "config.worktree", "objects/pack/test.promisor" })
            {
                var path = Path.Combine(fixture.Root, ".git", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, fixture.DirectoryPath);
                try { foreach (var query in fixture.Queries()) await Reject<WorkspaceException>(query); }
                finally { File.Delete(path); }
            }
            var project = Path.Combine(fixture.DirectoryPath, "linked-worktree");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, ".git"), "gitdir: " + Path.Combine(fixture.Root, ".git"));
            await using var linked = fixture.CreateExecution(project);
            await Reject<WorkspaceException>(() => linked.GitStatusAsync());
            var nested = Path.Combine(fixture.Root, "nested"); Directory.CreateDirectory(nested);
            await using var parentDiscovery = fixture.CreateExecution(nested);
            await Reject<WorkspaceException>(() => parentDiscovery.GitStatusAsync());
            var external = Path.Combine(fixture.DirectoryPath, "outside-index");
            File.WriteAllBytes(external, []);
            var index = Path.Combine(fixture.Root, ".git", "index");
            try { File.CreateSymbolicLink(index, external); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            { Console.WriteLine("SKIP Git metadata link checks: Developer Mode/permission required."); return; }
            try { foreach (var query in fixture.Queries()) await Reject<WorkspaceException>(query); }
            finally { File.Delete(index); }
        });
        await check("git queries: missing local objects do not invoke a remote helper or fetch", async () =>
        {
            await using var fixture = new Fixture();
            fixture.Put("a.txt", "base\n"); await fixture.Commit("initial");
            var blob = (await fixture.Git("rev-parse", "HEAD:a.txt")).Trim();
            var objectFile = Path.Combine(fixture.Root, ".git", "objects", blob[..2], blob[2..]);
            File.SetAttributes(objectFile, FileAttributes.Normal);
            File.Delete(objectFile);
            fixture.Put("a.txt", "changed\n");
            var marker = Path.Combine(fixture.DirectoryPath, "remote-helper-ran");
            await fixture.Git("config", "remote.origin.url", "ext::" + HelperCommand(marker));
            await Reject<WorkspaceException>(() => fixture.Execution.GitDiffAsync());
            Assert(!File.Exists(marker), "Missing objects remain local errors");
        });
        await check("git queries: optional missing Git keeps the eight-tool catalog and other tools healthy", async () =>
        {
            await using var fixture = new Fixture(gitPath: "missing-git");
            await File.WriteAllTextAsync(fixture.Configuration.SettingsFilePath, ConfigurationJson.SerializeSettings(fixture.Configuration.Settings));
            var store = new ConfigurationStore(new ConfigurationPaths(Path.Combine(fixture.DirectoryPath, "user"), Path.Combine(fixture.DirectoryPath, "state")));
            var diagnostic = await store.CheckAsync(fixture.Configuration.SettingsFilePath, fixture.DirectoryPath);
            Assert(diagnostic.GitPath is null && diagnostic.GitError is not null && fixture.Configuration.Settings.GitPath == Path.Combine(fixture.DirectoryPath, "missing-git"), "Normalized optional dependency report");
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert(await CliApplication.RunAsync(["check", "--config", fixture.Configuration.SettingsFilePath], output, error,
                default, store, fixture.DirectoryPath) == 0 && output.ToString().Contains("Git 查询：程序不可用"), "CLI optional dependency remains a diagnostic");
            await using var runtime = new McpRuntime(0);
            await runtime.StartAsync(fixture.Configuration);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            await using var client = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
            Assert((await client.ListToolsAsync()).Count == 8, "Fixed catalog");
            Assert((await client.CallToolAsync("git_status", new Dictionary<string, object?>())).IsError == true, "Tool-level dependency error");
            Assert((await client.CallToolAsync("list_files", new Dictionary<string, object?>())).IsError != true && runtime.Snapshot.State == RuntimeState.Running, "Other tools/Host unaffected");
        });
        await check("git queries: all modes expose the same eight real MCP tools and ordinary queries leave files unchanged", async () =>
        {
            string? catalog = null;
            foreach (var mode in Enum.GetValues<ExecutionMode>())
            {
                await using var fixture = new Fixture(mode);
                fixture.Put("a.txt", "one\n"); await fixture.Commit("initial"); fixture.Put("a.txt", "two\n");
                await fixture.Git("config", "--local", "branch.main.vscode-merge-base", "origin/main");
                await using var runtime = new McpRuntime(0);
                await runtime.StartAsync(fixture.Configuration);
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                await using var client = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
                var tools = await client.ListToolsAsync();
                var current = JsonSerializer.Serialize(tools.OrderBy(tool => tool.Name).Select(tool => tool.ProtocolTool));
                catalog ??= current; Assert(current == catalog && tools.Count == 8, "Schemas/descriptions/annotations identical");
                foreach (var tool in tools.Where(tool => tool.Name.StartsWith("git_", StringComparison.Ordinal)))
                {
                    Assert(tool.ProtocolTool.Annotations is { ReadOnlyHint: true, DestructiveHint: false, OpenWorldHint: false }, "Git annotations");
                    Assert(tool.ProtocolTool.Description is { } description && description.Contains("external programs") && description.Contains("even in ReadOnly"),
                        "Git description states the external-program behavior in " + mode);
                }
                var before = fixture.Snapshot();
                foreach (var name in new[] { "git_status", "git_diff", "git_log" })
                {
                    var result = await client.CallToolAsync(name, new Dictionary<string, object?> { ["approved"] = true, ["mode"] = "FullAccess" });
                    Assert(result.IsError != true && result.StructuredContent is not null, "Real MCP Git query: " + name);
                }
                Assert(before.SequenceEqual(fixture.Snapshot()) && !Directory.Exists(fixture.Recovery), "Read-only facts in " + mode);
            }
        });
        await check("git queries: actual cancellation, Runtime stop and bounded output clean query descendants", async () =>
        {
            foreach (var operation in new[] { "cancel", "stop", "flood", "malformed" })
            {
                await using var fixture = new Fixture(gitPath: Environment.ProcessPath!);
                fixture.Put(".git-query-control", operation);
                await using var runtime = new McpRuntime(0);
                await runtime.StartAsync(fixture.Configuration);
                using var cancel = new CancellationTokenSource();
                var query = runtime.Execution!.GitStatusAsync(cancellationToken: cancel.Token);
                using var child = await RuntimeCallChecks.ObserveChildAsync(fixture.PathOf("git-query-child.pid"), query);
                try
                {
                    if (operation == "cancel") cancel.Cancel();
                    if (operation == "stop") await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    if (operation is "cancel" or "stop") await Reject<OperationCanceledException>(() => query).WaitAsync(TimeSpan.FromSeconds(5));
                    else if (operation == "flood") Assert((await query.WaitAsync(TimeSpan.FromSeconds(5))).Truncated, "Output capped");
                    else await Reject<WorkspaceException>(() => query).WaitAsync(TimeSpan.FromSeconds(5));
                    await RuntimeCallChecks.AssertStoppedAsync(child);
                }
                finally { cancel.Cancel(); RuntimeCallChecks.KillIfRunning(child); }
            }
        });
    }

    internal static async Task<int> ChildAsync(string[] arguments)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var marker = Path.Combine(Environment.CurrentDirectory, "git-query-child.pid");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--execution-child"); start.ArgumentList.Add(marker);
        using var child = Process.Start(start)!;
        while (!File.Exists(marker)) await Task.Delay(10);
        var mode = await File.ReadAllTextAsync(Path.Combine(Environment.CurrentDirectory, ".git-query-control"));
        if (mode == "malformed") { Console.Write("invalid\0"); await Console.Out.FlushAsync(); }
        if (mode == "flood") while (true) { Console.Write(new string('x', 4096)); Console.Error.Write(new string('e', 4096)); }
        Console.Write(" M partial"); await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static string HelperCommand(string marker) => "\"" + Environment.ProcessPath!.Replace('\\', '/') + "\" --git-helper \"" + marker.Replace('\\', '/') + "\"";
    private static async Task<T> Reject<T>(Func<Task> query) where T : Exception
    { try { await query(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Git query: " + message); }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "minimcp-git-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public string Recovery { get; }
        public string ConfigFile => Path.Combine(Root, ".git", "config");
        private readonly string _git;
        private readonly ConfigurationPaths _paths;
        public RuntimeConfiguration Configuration { get; }
        public ToolExecutionService Execution { get; }
        public Fixture(ExecutionMode mode = ExecutionMode.ReadOnly, string? gitPath = null)
        {
            Root = Path.Combine(DirectoryPath, "project"); Directory.CreateDirectory(Root);
            _paths = new(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state")); Recovery = _paths.RecoveryDirectory;
            var source = Path.Combine(DirectoryPath, "settings.json");
            _git = ExternalProgramLocator.FindGit("", source) ?? throw new Exception("Git required for checks");
            Directory.CreateDirectory(Path.Combine(DirectoryPath, "empty-templates"));
            Git("init", "--quiet", "--template=" + Path.Combine(DirectoryPath, "empty-templates")).GetAwaiter().GetResult();
            Git("config", "user.name", "MiniMcp checks").GetAwaiter().GetResult();
            Git("config", "user.email", "checks@example.invalid").GetAwaiter().GetResult();
            Configuration = new ConfigurationValidator(_paths).Validate(new AppSettings
            {
                ProjectRoot = Root, GitPath = gitPath ?? _git,
                BashPath = mode == ExecutionMode.FullAccess ? ExternalProgramLocator.FindBash("", source)! : "missing-bash",
                Authentication = new() { Mode = AuthenticationMode.Local }
            }, source, new() { Mode = mode });
            Execution = new(Guid.NewGuid(), Configuration);
        }
        public string PathOf(string path) => Path.Combine(Root, path);
        public void Put(string path, string text)
        { var file = PathOf(path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text, new UTF8Encoding(false)); }
        public async Task Commit(string message) { await Git("add", "."); await Git("-c", "commit.gpgsign=false", "commit", "--quiet", "-m", message); }
        public ToolExecutionService CreateExecution(string root) => new(Guid.NewGuid(), new ConfigurationValidator(_paths).Validate(
            Configuration.Settings with { ProjectRoot = root }, Configuration.SettingsFilePath, Configuration.Policy));
        public IEnumerable<Func<Task>> Queries() => [async () => { await Execution.GitStatusAsync(); }, async () => { await Execution.GitDiffAsync(); }, async () => { await Execution.GitLogAsync(); }];
        public async Task<string> Git(params string[] arguments)
        {
            var start = new ProcessStartInfo(_git) { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false) };
            foreach (var key in start.Environment.Keys.ToArray()) if (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) start.Environment.Remove(key);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var output = await stdout; var error = await stderr;
            if (process.ExitCode != 0) throw new Exception("Git fixture setup failed: " + error);
            return output;
        }
        public string[] Snapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => Path.GetRelativePath(Root, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) + ":" +
                File.GetLastWriteTimeUtc(file).Ticks + ":" + (OperatingSystem.IsWindows() ? "" : File.GetUnixFileMode(file).ToString())).ToArray();
        public async ValueTask DisposeAsync()
        {
            await Execution.DisposeAsync();
            var resolved = Path.GetFullPath(DirectoryPath);
            if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(resolved).StartsWith("minimcp-git-", StringComparison.Ordinal)) throw new Exception("Unexpected Git fixture root");
            foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(resolved, true);
        }
    }
}
