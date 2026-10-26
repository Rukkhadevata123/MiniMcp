using System.Net;
using System.Net.Sockets;
using MiniMcp.Cli;
using MiniMcp.Configuration;
using MiniMcp.Recovery;

internal static class CliChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-cli-{Guid.NewGuid():N}");
        var root = Path.Combine(temporary, "project");
        var working = Path.Combine(temporary, "work");
        var paths = new ConfigurationPaths(Path.Combine(temporary, "user", "config"), Path.Combine(temporary, "state"));
        var store = new ConfigurationStore(paths);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "cli.txt"), "CLI MCP fixture\n");
        Directory.CreateDirectory(working);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(3) };
        void Assert(bool condition)
        {
            if (!condition) throw new Exception("CLI assertion failed.");
        }
        int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }
        async Task WriteSettings(string path, int port, string? bashPath = null, string? projectRoot = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, ConfigurationJson.SerializeSettings(new AppSettings
            {
                ProjectRoot = projectRoot ?? root, Port = port, BashPath = bashPath ?? "",
                Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local }
            }));
        }
        async Task<(int Code, string Output, string Error)> Invoke(string[] args, CancellationToken token = default)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var code = await CliApplication.RunAsync(args, output, error, deadline.Token, store, working);
            return (code, output.ToString(), error.ToString());
        }
        async Task RunAndStop(string[] args, ExecutionMode mode)
        {
            using var output = new ReadyWriter();
            using var error = new StringWriter();
            using var stop = new CancellationTokenSource();
            var running = CliApplication.RunAsync(args, output, error, stop.Token, store, working);
            try
            {
                var first = await Task.WhenAny(running, output.Ready.Task).WaitAsync(TimeSpan.FromSeconds(10));
                if (first == running) throw new Exception($"CLI exited before startup: {await running}; {error}");
                var address = await output.Ready.Task;
                using var response = await client.GetAsync(address);
                Assert(response.StatusCode == HttpStatusCode.OK);
                await using (var mcp = await McpHttpChecks.ConnectAsync(address, client, stop.Token))
                {
                    Assert((await mcp.ListToolsAsync(cancellationToken: stop.Token)).Count == 5);
                    var read = await mcp.CallToolAsync("read_file", new Dictionary<string, object?> { ["path"] = "cli.txt" }, cancellationToken: stop.Token);
                    Assert(McpHttpChecks.Structured(read).GetProperty("content").GetString() == "CLI MCP fixture\n");
                    var file = $"cli-{mode}.md";
                    var arguments = new Dictionary<string, object?> { ["patch"] = PatchExecutionFixture.Patch($"*** Add File: {file}\n+first"),
                        ["approved"] = true, ["mode"] = "FullAccess", ["disableRecovery"] = true };
                    var write = await mcp.CallToolAsync("apply_patch", arguments, cancellationToken: stop.Token);
                    if (mode == ExecutionMode.ReadOnly) Assert(write.IsError == true && !File.Exists(Path.Combine(root, file)));
                    else
                    {
                        var committed = McpHttpChecks.Structured(write).GetProperty("commit");
                        var id = committed.GetProperty("changeId").GetGuid();
                        var manifest = RecoveryManifestJson.Parse(await File.ReadAllBytesAsync(Path.Combine(paths.RecoveryDirectory, id.ToString("N"), RecoveryStore.ManifestFileName)));
                        Assert(manifest.State == RecoveryRecordState.Committed && manifest.RunId == committed.GetProperty("runId").GetGuid() && File.ReadAllText(Path.Combine(root, file)) == "first\n");
                        var update = await mcp.CallToolAsync("apply_patch", new Dictionary<string, object?>
                        { ["patch"] = PatchExecutionFixture.Patch($"*** Update File: {file}\n@@\n-first\n+second") }, cancellationToken: stop.Token);
                        var updateId = McpHttpChecks.Structured(update).GetProperty("commit").GetProperty("changeId").GetGuid();
                        var recordPath = Path.Combine(paths.RecoveryDirectory, updateId.ToString("N"));
                        Assert(updateId != id && File.ReadAllText(Path.Combine(root, file)) == "second\n" &&
                            File.ReadAllText(Path.Combine(recordPath, "before-0000.bin")) == "first\n" &&
                            RecoveryManifestJson.Parse(await File.ReadAllBytesAsync(Path.Combine(recordPath, RecoveryStore.ManifestFileName))).State == RecoveryRecordState.Committed);
                    }
                }
                stop.Cancel();
                Assert(await running.WaitAsync(TimeSpan.FromSeconds(15)) == 0);
                Assert(error.ToString() == "" && output.ToString().Contains(root, StringComparison.Ordinal));
                Assert(output.ToString().Contains($"配置执行模式：{mode}", StringComparison.Ordinal));
                Assert(output.ToString().Contains(new Uri(address, "mcp").AbsoluteUri, StringComparison.Ordinal));
                var released = new TcpListener(IPAddress.Loopback, address.Port);
                try { released.Start(); }
                finally { released.Stop(); }
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        try
        {
            await check("CLI help works without configuration and describes JSON and session mode selection", async () =>
            {
                foreach (var args in new[] { Array.Empty<string>(), ["--help"], ["-h"], ["run", "--help"] })
                {
                    var result = await Invoke(args);
                    Assert(result.Code == 0 && result.Error == "" && result.Output.Contains("--config", StringComparison.Ordinal));
                    Assert(result.Output.Contains("--mode", StringComparison.Ordinal) && !result.Output.Contains("--port", StringComparison.Ordinal));
                }
                Assert(!Directory.Exists(Path.GetDirectoryName(paths.SettingsFile)));
            });

            await check("CLI rejects unknown, repeated and missing arguments plus non-exact mode names", async () =>
            {
                foreach (var args in new[]
                {
                    new[] { "unknown" }, ["run", "--port", "8000"], ["run", "--config"], ["run", "--mode"],
                    ["run", "--config", ""], ["run", "--config", "--mode", "ReadOnly"],
                    ["run", "--config", "a", "--config", "b"], ["run", "--mode", "ReadOnly", "--mode", "FullAccess"],
                    ["run", "--mode", "2"], ["run", "--mode", "readonly"], ["run", "--mode", "ReadOnly, FullAccess"],
                    ["run", "--unknown\u001b[2J"]
                })
                {
                    var result = await Invoke(args);
                    Assert(result.Code == 2 && result.Output == "" && !result.Error.Contains('\u001b'));
                }
            });

            await check("CLI reports missing, malformed and invalid configuration with exit code two", async () =>
            {
                Assert((await Invoke(["run"])).Code == 2);
                var path = Path.Combine(working, "bad.json");
                foreach (var json in new[] { "{", """{"port":8000,"port":9000}""", """{"projectRoot":"absent","authentication":{"mode":"Local"}}""",
                    """{"port":0,"authentication":{"mode":"Local"}}""" })
                {
                    await File.WriteAllTextAsync(path, json);
                    var result = await Invoke(["run", "--config", path]);
                    Assert(result.Code == 2 && result.Error.Contains("配置错误", StringComparison.Ordinal));
                }
            });

            await check("CLI mode overrides do not bypass malformed local policy files", async () =>
            {
                await WriteSettings(paths.SettingsFile, FreePort());
                await File.WriteAllTextAsync(paths.PolicyFile, """{"mode":"Unknown"}""");
                var result = await Invoke(["run", "--mode", "ReadOnly"]);
                Assert(result.Code == 2 && result.Error.Contains(paths.PolicyFile, StringComparison.Ordinal));
                Assert((await Invoke(["run", "--help"])).Code == 0);
                File.Delete(paths.PolicyFile);
            });

            await check("CLI runs the default user JSON and releases its listener on normal cancellation", async () =>
            {
                await WriteSettings(paths.SettingsFile, FreePort());
                await RunAndStop(["run"], ExecutionMode.ReadOnly);
                Assert(!File.Exists(paths.PolicyFile) && !Directory.Exists(paths.RecoveryDirectory));
            });

            await check("CLI resolves relative config paths and all session modes without rewriting local policy", async () =>
            {
                var path = Path.Combine(working, "configs", "settings.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bash = Path.Combine(working, "configs", "bash");
                await File.WriteAllTextAsync(bash, "Fixture only; never execute.");
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(bash, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                const string policy = """{"mode":"FullAccess"}""";
                await File.WriteAllTextAsync(paths.PolicyFile, policy);
                foreach (var mode in Enum.GetValues<ExecutionMode>())
                {
                    await WriteSettings(path, FreePort(), "./bash", "../../project");
                    await RunAndStop(["run", "--mode", mode.ToString(), "--config", "configs/settings.json"], mode);
                }
                Assert(await File.ReadAllTextAsync(paths.PolicyFile) == policy);
            });

            await check("CLI classifies mode dependency failures and pre-start cancellation separately", async () =>
            {
                await WriteSettings(paths.SettingsFile, FreePort(), "missing-bash");
                await File.WriteAllTextAsync(paths.PolicyFile, "{}");
                var result = await Invoke(["run", "--mode", "FullAccess"]);
                Assert(result.Code == 2 && result.Error.Contains("bashPath", StringComparison.Ordinal));
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                Assert((await Invoke(["run"], cancelled.Token)).Code == 130);
            });

            await check("CLI port conflicts return a runtime failure and leave the existing listener intact", async () =>
            {
                var owner = new TcpListener(IPAddress.Loopback, 0);
                owner.Start();
                try
                {
                    var port = ((IPEndPoint)owner.LocalEndpoint).Port;
                    await WriteSettings(paths.SettingsFile, port);
                    var result = await Invoke(["run"]);
                    Assert(result.Code == 1 && owner.Server.IsBound && result.Error.Contains("失败", StringComparison.Ordinal));
                }
                finally { owner.Stop(); }
            });
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private sealed class ReadyWriter : StringWriter
    {
        public TaskCompletionSource<Uri> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            var index = value?.IndexOf("http://127.0.0.1:", StringComparison.Ordinal) ?? -1;
            if (index >= 0 && Uri.TryCreate(value![index..], UriKind.Absolute, out var uri) && uri.AbsolutePath == "/healthz")
                Ready.TrySetResult(uri);
        }
    }
}
