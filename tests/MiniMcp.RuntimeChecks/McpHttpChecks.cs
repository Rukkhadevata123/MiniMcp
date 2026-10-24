using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Runtime;
using MiniMcp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class McpHttpChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        foreach (var mode in Enum.GetValues<ExecutionMode>())
            await check($"MCP HTTP {mode}: exact schemas, reads, previews, write denials and Bash policy", () => CheckModeAsync(mode));

        await check("MCP HTTP exports complete bounded previews without plan data, files or recovery records", async () =>
        {
            await using var fixture = new HttpSecurityFixture(AuthenticationMode.Local, mapProbes: false);
            File.WriteAllText(Path.Combine(fixture.Root, "complex.txt"), string.Concat(Enumerable.Range(0, 1000).Select(i => $"old{i}\n")));
            var storage = Directory.GetParent(fixture.Root)!.FullName;
            var before = Snapshot(storage);
            await fixture.Runtime.StartAsync(fixture.Configuration);
            await using var client = await ConnectAsync(fixture.Runtime.Address!, fixture.Client);
            var line = new string('x', 180_000) + "TAIL中👋\t\u001b";
            var patch = Patch("*** Add File: preview/large.txt\n+" + line);
            var preview = FullPreview(await Call(client, "apply_patch", new() { ["patch"] = patch, ["dry_run"] = true }));
            var diff = preview.GetProperty("diff").GetString()!;
            Assert(diff.Length > 16_000 && diff.Contains("TAIL中👋\\t\\u001b\\n\""), "Complete escaped response exceeds the read-file clipping size");
            Assert(preview.GetProperty("statistics").GetProperty("addedBytes").GetInt64() == Encoding.UTF8.GetByteCount(line + "\n"), "Raw UTF-8 statistics");
            Error(await Call(client, "apply_patch", new() { ["patch"] = patch, ["approved"] = true, ["mode"] = "FullAccess" }), "ReadOnly");
            Error(await Call(client, "apply_patch", new()
            {
                ["patch"] = Patch("*** Add File: oversized.txt\n+" + new string('\u001b', 50_000)), ["dry_run"] = true
            }), "display limit");
            var complex = Patch("*** Update File: complex.txt\n@@\n" + string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"-old{i}")) + "\n" +
                string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"+new{i}")));
            Error(await Call(client, "apply_patch", new() { ["patch"] = complex, ["dry_run"] = true }), "computation limit");
            Assert(Structured(await Call(client, "read_file", new() { ["path"] = "sample.txt" })).GetProperty("content").GetString() == "read-only fixture\n", "Service remains usable after preview rejection");
            Assert(before.SequenceEqual(Snapshot(storage)) && fixture.Runtime.Execution!.ActiveCalls == 0 && fixture.Runtime.Snapshot.State == RuntimeState.Running,
                "Preview success and failure create no workspace, configuration or recovery artifacts");
        });

        await check("MCP HTTP is stateless, omits session headers and disables GET, DELETE and legacy SSE", async () =>
        {
            await using var fixture = new HttpSecurityFixture(AuthenticationMode.Local, mapProbes: false);
            await fixture.Runtime.StartAsync(fixture.Configuration);
            using var trace = new SessionHeaderObserver { InnerHandler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false } };
            using var http = new HttpClient(trace);
            await using var client = await ConnectAsync(fixture.Runtime.Address!, http);
            Assert((await client.ListToolsAsync()).Count == 3 && !trace.SawSessionHeader, "Stateless tool discovery");
            await using (var initialized = await ConnectAsync(fixture.Runtime.Address!, http, protocolVersion: "2025-11-25"))
                Assert((await initialized.ListToolsAsync()).Count == 3 && !trace.SawSessionHeader, "Initialize-handshake client stays stateless");
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete })
            {
                using var request = fixture.Request("mcp", method: method);
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                using var response = await fixture.Client.SendAsync(request);
                Assert(response.StatusCode == HttpStatusCode.MethodNotAllowed, "Stateless method: " + method);
            }
            foreach (var path in new[] { "sse", "message", "mcp/sse", "mcp/message" })
            {
                using var response = await fixture.Client.GetAsync(new Uri(fixture.Runtime.Address!, path));
                Assert(response.StatusCode == HttpStatusCode.NotFound, "Legacy endpoint: " + path);
            }
            using var oversized = RpcRequest(fixture, new string('x', 2 * 1024 * 1024));
            using var rejected = await fixture.Client.SendAsync(oversized);
            Assert(rejected.StatusCode == HttpStatusCode.RequestEntityTooLarge, "MCP request body limit");
        });

        await check("MCP HTTP Cloudflare authorization protects every call and cannot elevate local policy", async () =>
        {
            await using var fixture = new HttpSecurityFixture(mapProbes: false);
            await fixture.Runtime.StartAsync(fixture.Configuration);
            using (var request = RpcRequest(fixture))
            using (var response = await fixture.Client.SendAsync(request))
                Assert(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous MCP access");
            using (var request = RpcRequest(fixture))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + fixture.Token());
                using var response = await fixture.Client.SendAsync(request);
                Assert(response.StatusCode == HttpStatusCode.Unauthorized, "Bearer is not an Access assertion");
            }
            using (var request = RpcRequest(fixture))
            {
                request.Headers.TryAddWithoutValidation("Cf-Access-Jwt-Assertion", fixture.Token(audience: "wrong-audience"));
                using var response = await fixture.Client.SendAsync(request);
                Assert(response.StatusCode == HttpStatusCode.Unauthorized, "Wrong application AUD");
            }
            await using var client = await ConnectAsync(fixture.Runtime.Address!, fixture.Client, headers: new()
            {
                ["Cf-Access-Jwt-Assertion"] = fixture.Token(), ["Authorization"] = "Bearer opaque-client-token",
                ["X-Execution-Mode"] = "FullAccess"
            });
            Assert((await client.ListToolsAsync()).Count == 3, "Authorized discovery");
            Assert(Structured(await Call(client, "read_file", new() { ["path"] = "sample.txt" }))
                .GetProperty("content").GetString() == "read-only fixture\n", "Authorized read");
            Error(await Call(client, "bash", new() { ["command"] = "printf unexpected > forbidden.txt", ["approved"] = true, ["mode"] = "FullAccess" }), "ReadOnly");
            Error(await Call(client, "apply_patch", new() { ["patch"] = Patch("*** Add File: forbidden.txt\n+unexpected"), ["approved"] = true }), "ReadOnly");
            Assert(!File.Exists(Path.Combine(fixture.Root, "forbidden.txt")) && fixture.Runtime.Snapshot.State == RuntimeState.Running,
                "Authenticated identity cannot change execution policy");
            using var anonymousAgain = RpcRequest(fixture);
            using var deniedAgain = await fixture.Client.SendAsync(anonymousAgain);
            Assert(deniedAgain.StatusCode == HttpStatusCode.Unauthorized, "Authorization is evaluated per request");
        });

        await check("MCP HTTP client cancellation terminates an observed Bash child and releases execution", async () =>
        {
            await using var fixture = new HttpSecurityFixture(AuthenticationMode.Local, ExecutionMode.FullAccess, mapProbes: false);
            fixture.Client.Timeout = Timeout.InfiniteTimeSpan;
            await fixture.Runtime.StartAsync(fixture.Configuration);
            await using var client = await ConnectAsync(fixture.Runtime.Address!, fixture.Client);
            using var cancel = new CancellationTokenSource();
            var marker = Path.Combine(fixture.Root, "http-client-child.pid");
            var running = client.CallToolAsync("bash", new Dictionary<string, object?> { ["command"] = RuntimeCallChecks.ChildCommand(marker) }, cancellationToken: cancel.Token).AsTask();
            using var child = await RuntimeCallChecks.ObserveChildAsync(marker, running);
            try
            {
                await cancel.CancelAsync();
                await Reject<OperationCanceledException>(() => running).WaitAsync(TimeSpan.FromSeconds(5));
                await RuntimeCallChecks.AssertStoppedAsync(child);
                await Until(() => fixture.Runtime.Execution!.ActiveCalls == 0);
                Assert(Structured(await Call(client, "bash", new() { ["command"] = "exit 0" })).GetProperty("exitCode").GetInt32() == 0,
                    "Cancellation releases the shared mutation lock");
            }
            finally { cancel.Cancel(); RuntimeCallChecks.KillIfRunning(child); }
        });

        await check("MCP HTTP runtime stop drains active and queued calls from separate clients", async () =>
        {
            await using var fixture = new HttpSecurityFixture(AuthenticationMode.Local, ExecutionMode.FullAccess, mapProbes: false);
            fixture.Client.Timeout = Timeout.InfiniteTimeSpan;
            await fixture.Runtime.StartAsync(fixture.Configuration);
            var execution = fixture.Runtime.Execution!;
            var port = fixture.Runtime.Address!.Port;
            await using var first = await ConnectAsync(fixture.Runtime.Address, fixture.Client);
            await using var second = await ConnectAsync(fixture.Runtime.Address, fixture.Client);
            var marker = Path.Combine(fixture.Root, "http-stop-child.pid");
            var running = first.CallToolAsync("bash", new Dictionary<string, object?> { ["command"] = RuntimeCallChecks.ChildCommand(marker) }).AsTask();
            using var child = await RuntimeCallChecks.ObserveChildAsync(marker, running);
            try
            {
                var preview = second.CallToolAsync("apply_patch", new Dictionary<string, object?>
                { ["patch"] = Patch("*** Add File: pending/new.txt\n+new"), ["dry_run"] = true }).AsTask();
                await Until(() => execution.ActiveCalls == 2);
                Assert(!preview.IsCompleted, "Requests share one mutation lock");
                await fixture.Runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await Interrupted(running);
                await Interrupted(preview);
                await RuntimeCallChecks.AssertStoppedAsync(child);
                Assert(execution.IsDisposed && execution.ActiveCalls == 0 && !Directory.Exists(Path.Combine(fixture.Root, "pending")), "Stop drains before disposal");
                var listener = new TcpListener(IPAddress.Loopback, port);
                try { listener.Start(); } finally { listener.Stop(); }
            }
            finally { RuntimeCallChecks.KillIfRunning(child); }
        });

        await check("MCP HTTP cancelling a queued preview releases only that call before runtime stop", async () =>
        {
            await using var fixture = new HttpSecurityFixture(AuthenticationMode.Local, ExecutionMode.FullAccess, mapProbes: false);
            fixture.Client.Timeout = Timeout.InfiniteTimeSpan;
            await fixture.Runtime.StartAsync(fixture.Configuration);
            var execution = fixture.Runtime.Execution!;
            await using var first = await ConnectAsync(fixture.Runtime.Address!, fixture.Client);
            await using var second = await ConnectAsync(fixture.Runtime.Address!, fixture.Client);
            using var cancel = new CancellationTokenSource();
            var marker = Path.Combine(fixture.Root, "preview-cancel-child.pid");
            var running = first.CallToolAsync("bash", new Dictionary<string, object?> { ["command"] = RuntimeCallChecks.ChildCommand(marker) }).AsTask();
            using var child = await RuntimeCallChecks.ObserveChildAsync(marker, running);
            try
            {
                var preview = second.CallToolAsync("apply_patch", new Dictionary<string, object?>
                { ["patch"] = Patch("*** Add File: pending/new.txt\n+new"), ["dry_run"] = true }, cancellationToken: cancel.Token).AsTask();
                await Until(() => execution.ActiveCalls == 2);
                await cancel.CancelAsync();
                await Reject<OperationCanceledException>(() => preview).WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => execution.ActiveCalls == 1);
                Assert(!child.HasExited && !Directory.Exists(Path.Combine(fixture.Root, "pending")), "Preview cancellation does not stop another client's Bash");
                await fixture.Runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await Interrupted(running);
                await RuntimeCallChecks.AssertStoppedAsync(child);
                Assert(execution.IsDisposed && execution.ActiveCalls == 0, "Cancelled preview does not obstruct draining or disposal");
            }
            finally { cancel.Cancel(); RuntimeCallChecks.KillIfRunning(child); }
        });

        await check("MCP HTTP restart changes project, mode and run identity without stale client state", async () =>
        {
            await using var fixture = new HttpSecurityFixture(AuthenticationMode.Local, ExecutionMode.FullAccess, mapProbes: false);
            await using var next = new HttpSecurityFixture(AuthenticationMode.Local, mapProbes: false);
            File.WriteAllText(Path.Combine(next.Root, "sample.txt"), "second project\n");
            await fixture.Runtime.StartAsync(fixture.Configuration);
            var original = fixture.Runtime.Snapshot;
            var oldExecution = fixture.Runtime.Execution!;
            await using var oldClient = await ConnectAsync(fixture.Runtime.Address!, fixture.Client);
            await Reject<InvalidOperationException>(() => fixture.Runtime.StartAsync(next.Configuration));
            Assert(ReferenceEquals(original, fixture.Runtime.Snapshot), "Duplicate startup cannot replace the session");
            Assert(Structured(await Call(oldClient, "bash", new() { ["command"] = "exit 0" })).GetProperty("exitCode").GetInt32() == 0, "Original FullAccess policy");
            await fixture.Runtime.StopAsync();
            await fixture.Runtime.StartAsync(next.Configuration);
            var current = fixture.Runtime.Snapshot;
            Assert(oldExecution.IsDisposed && current.RunId != original.RunId, "New runtime identity");
            await oldClient.DisposeAsync();
            await using var client = await ConnectAsync(fixture.Runtime.Address!, fixture.Client);
            Assert(Structured(await Call(client, "read_file", new() { ["path"] = "sample.txt" })).GetProperty("content").GetString() == "second project\n", "New project root");
            Error(await Call(client, "bash", new() { ["command"] = "exit 0" }), "ReadOnly");
            Assert(ReferenceEquals(current, fixture.Runtime.Snapshot), "Old client disposal cannot rewrite the new snapshot");
        });
    }

    private static async Task CheckModeAsync(ExecutionMode mode)
    {
        await using var fixture = new HttpSecurityFixture(AuthenticationMode.Local, mode, mapProbes: false);
        File.WriteAllText(Path.Combine(fixture.Root, ".env"), "PRIVATE_FIXTURE=value\n");
        await fixture.Runtime.StartAsync(fixture.Configuration);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(fixture.Runtime.Address!, fixture.Client, deadline.Token);
        var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
        Assert(tools.Select(tool => tool.Name).Order().SequenceEqual(new[] { "apply_patch", "bash", "read_file" }), "Exactly three tools");
        foreach (var tool in tools)
        {
            var properties = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order();
            string[] expected = tool.Name switch
            {
                "read_file" => ["end_line", "path", "start_line"], "apply_patch" => ["dry_run", "patch"],
                _ => ["command", "timeout_seconds", "workdir"]
            };
            Assert(properties.SequenceEqual(expected), "Schema: " + tool.Name);
            if (tool.Name == "apply_patch")
                Assert(tool.ProtocolTool.OutputSchema is { } outputSchema && outputSchema.GetProperty("properties").EnumerateObject()
                    .Select(property => property.Name).Order().SequenceEqual(new[] { "changes", "diff", "dryRun", "statistics" }), "Public preview output schema");
        }
        var read = Structured(await Call(client, "read_file", new() { ["path"] = "sample.txt", ["start_line"] = 1, ["end_line"] = 1 }));
        Assert(read.GetProperty("content").GetString() == "read-only fixture\n", "Read range");
        Error(await Call(client, "read_file", new() { ["path"] = "../outside" }), "outside");
        Error(await Call(client, "read_file", new() { ["path"] = ".env" }), "private");
        var patches = new[] { "*** Add File: preview/new.txt\n+new", "*** Update File: sample.txt\n@@\n-read-only fixture\n+updated",
            "*** Update File: sample.txt\n*** Move to: moved/new.txt", "*** Delete File: sample.txt" };
        var expectedStatistics = new[] { new PatchStatistics(1, 0, 4, 0), new PatchStatistics(1, 1, 8, 18),
            new PatchStatistics(0, 0, 0, 0), new PatchStatistics(0, 1, 0, 18) };
        for (var i = 0; i < patches.Length; i++)
        {
            var preview = FullPreview(await Call(client, "apply_patch", new() { ["patch"] = Patch(patches[i]), ["dry_run"] = true }));
            Assert(preview.GetProperty("dryRun").GetBoolean(), "Patch preview");
            var expected = JsonSerializer.SerializeToElement(expectedStatistics[i], new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert(JsonElement.DeepEquals(preview.GetProperty("statistics"), expected) &&
                JsonElement.DeepEquals(preview.GetProperty("changes")[0].GetProperty("statistics"), expected), "Per-file and total preview statistics");
            Assert(preview.GetProperty("changes")[0].GetProperty("summary").GetProperty("path").GetString() == (i == 0 ? "preview/new.txt" : "sample.txt"), "Preview summary paths");
            Error(await Call(client, "apply_patch", new() { ["patch"] = Patch(patches[i]), ["approved"] = true, ["mode"] = "FullAccess" }),
                mode == ExecutionMode.ReadOnly ? "ReadOnly" : "recovery records");
        }
        if (mode == ExecutionMode.FullAccess)
        {
            var result = Structured(await Call(client, "bash", new() { ["command"] = "printf hello; printf diagnostic >&2; exit 7" }));
            Assert(result.GetProperty("exitCode").GetInt32() == 7 && result.GetProperty("stdout").GetString() == "hello" && result.GetProperty("stderr").GetString() == "diagnostic", "Bash result");
            var timeout = Structured(await Call(client, "bash", new() { ["command"] = "printf partial; sleep 30", ["timeout_seconds"] = 1 }));
            Assert(timeout.GetProperty("timedOut").GetBoolean() && timeout.GetProperty("stdout").GetString() == "partial", "Bash timeout");
        }
        else Error(await Call(client, "bash", new() { ["command"] = "printf unexpected > denied.txt", ["approved"] = true }),
            mode == ExecutionMode.ReadOnly ? "ReadOnly" : "local approval");
        Assert(File.ReadAllText(Path.Combine(fixture.Root, "sample.txt")) == "read-only fixture\n" && Directory.EnumerateFileSystemEntries(fixture.Root).Count() == 2,
            "Previews and denials leave workspace unchanged");
        Assert(fixture.Runtime.Execution!.ActiveCalls == 0 && fixture.Runtime.Snapshot.State == RuntimeState.Running, "Tool errors preserve runtime availability");
    }

    internal static Task<McpClient> ConnectAsync(Uri address, HttpClient httpClient, CancellationToken cancellationToken = default,
        Dictionary<string, string>? headers = null, string? protocolVersion = null) => McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(address, "mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false, ConnectionTimeout = TimeSpan.FromSeconds(3), AdditionalHeaders = headers
        }, httpClient, loggerFactory: null, ownsHttpClient: false), new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: cancellationToken);

    internal static JsonElement Structured(CallToolResult result)
    {
        Assert(result.IsError != true && result.StructuredContent is not null, "Successful structured tool result");
        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }

    private static async Task<CallToolResult> Call(McpClient client, string name, Dictionary<string, object?> arguments)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await client.CallToolAsync(name, arguments, cancellationToken: deadline.Token);
    }

    private static JsonElement FullPreview(CallToolResult result)
    {
        var preview = Structured(result);
        Assert(preview.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "changes", "diff", "dryRun", "statistics" }), "Preview contains no plan, owner or original file snapshots");
        foreach (var change in preview.GetProperty("changes").EnumerateArray())
            Assert(change.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "statistics", "summary" }), "Public per-file preview shape");
        using var text = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
        Assert(text.RootElement.GetProperty("diff").GetString() == preview.GetProperty("diff").GetString(), "Text and structured preview stay complete and consistent");
        return preview;
    }

    private static void Error(CallToolResult result, string message) => Assert(result.IsError == true && result.StructuredContent is null &&
        result.Content.OfType<TextContentBlock>().Any(block => block.Text.Contains(message, StringComparison.Ordinal)) &&
        !result.Content.OfType<TextContentBlock>().Any(block => block.Text.Contains("# Review diff")), "Tool error without partial preview: " + message);
    private static string Patch(string body) => "*** Begin Patch\n" + body + "\n*** End Patch";
    private static string[] Snapshot(string directory) => Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path) +
            (Directory.Exists(path) ? "/" : ":" + Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();

    private static HttpRequestMessage RpcRequest(HttpSecurityFixture fixture, string padding = "")
    {
        var request = fixture.Request("mcp", method: HttpMethod.Post);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        request.Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { padding } }), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500; i++) { if (condition()) return; await Task.Delay(10); }
        throw new Exception("Timed out waiting for HTTP call lifecycle state.");
    }

    private static async Task Interrupted(Task<CallToolResult> call)
    {
        try { Assert((await call.WaitAsync(TimeSpan.FromSeconds(10))).IsError == true, "Stopped call cannot succeed"); }
        catch (Exception error) when (error is OperationCanceledException or McpException or HttpRequestException or IOException) { }
    }

    private static async Task Reject<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); } catch (TException) { return; }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class SessionHeaderObserver : DelegatingHandler
    {
        private int _sawSessionHeader;
        public bool SawSessionHeader => Volatile.Read(ref _sawSessionHeader) != 0;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Headers.Contains("Mcp-Session-Id") || response.Headers.Contains("Mcp-Session-Id")) Interlocked.Exchange(ref _sawSessionHeader, 1);
            return response;
        }
    }
}
