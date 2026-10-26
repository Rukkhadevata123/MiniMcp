using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Runtime;
using MiniMcp.Services;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static PatchExecutionFixture;

internal static class RecordedToolHttpChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        foreach (var mode in Enum.GetValues<ExecutionMode>())
            await check($"MCP HTTP {mode}: actual recorded patch results, backup identity and whole-batch policy", async () =>
            {
                using var fixture = new PatchExecutionFixture(mode);
                await using var runtime = fixture.Runtime();
                await runtime.StartAsync(fixture.Configuration);
                using var http = Client();
                await using var client = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
                var tools = await client.ListToolsAsync();
                var patchTool = tools.Single(tool => tool.Name == "apply_patch");
                Assert(patchTool.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "dry_run", "patch" }) &&
                    patchTool.ProtocolTool.OutputSchema!.Value.GetProperty("properties").TryGetProperty("commit", out _), "Commit identity belongs only in output, never caller-supplied authorization");
                var patch = Patch("*** Add File: nested/new.md\n+added\n*** Update File: sample.md\n@@\n-old\n+new");
                var preview = Result(await Call(client, patch, dryRun: true));
                Assert(preview.GetProperty("dryRun").GetBoolean() && preview.GetProperty("commit").ValueKind == JsonValueKind.Null &&
                    !Directory.Exists(fixture.RecoveryDirectory) && fixture.Get("sample.md") == "old\n", "HTTP preview is complete and creates no records or workspace directories");
                var write = await Call(client, patch);
                if (mode == ExecutionMode.ReadOnly)
                {
                    Error(write, "ReadOnly");
                    Assert(!Directory.Exists(fixture.RecoveryDirectory) && !Directory.Exists(fixture.PathOf("nested")), "ReadOnly cannot be overridden over MCP");
                    return;
                }
                var result = Result(write).Deserialize<PatchApplyResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var saved = await fixture.Load(result);
                Assert(result.Commit!.RunId == runtime.Snapshot.RunId && Encoding.UTF8.GetString(saved.Backups.Single().Bytes.AsSpan()) == "old\n" &&
                    fixture.Get("nested/new.md") == "added\n" && fixture.Get("sample.md") == "new\n", "HTTP success requires the actual finalized session record and original bytes");
                fixture.Put("move.md", "move\n");
                var mixed = Patch("*** Add File: ordinary.md\n+ordinary\n*** Add File: unknown.data\n+data\n*** Add File: Directory.Build.cs\n+build\n" +
                    "*** Delete File: sample.md\n*** Update File: move.md\n*** Move to: moved.md");
                var before = fixture.ProjectSnapshot();
                var mixedResult = await Call(client, mixed);
                if (mode == ExecutionMode.ProjectEdit)
                {
                    Error(mixedResult, "ProjectEdit restrictions");
                    Assert(before.SequenceEqual(fixture.ProjectSnapshot()) && fixture.RecordIds().Length == 1, "High-risk mixed batch does not execute or back up its ordinary portion");
                }
                else
                {
                    var completed = Result(mixedResult).Deserialize<PatchApplyResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                    var record = await fixture.Load(completed);
                    Assert(record.Backups.Length == 2 && !File.Exists(fixture.PathOf("sample.md")) && fixture.Get("moved.md") == "move\n" &&
                        fixture.Get("Directory.Build.cs") == "build\n" && fixture.RecordIds().Length == 2, "FullAccess records high-risk mixed operations as one real transaction");
                }
            });

        await check("MCP HTTP backup failure cannot be bypassed by forged recovery fields and preserves service availability", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.RecoveryDirectory)!);
            File.WriteAllText(fixture.RecoveryDirectory, "foreign occupant");
            await using var runtime = fixture.Runtime();
            await runtime.StartAsync(fixture.Configuration);
            using var http = Client();
            await using var client = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
            var patch = Patch("*** Add File: nested/new.md\n+new");
            Assert(Result(await Call(client, patch, true)).GetProperty("decision").GetProperty("canCommit").GetBoolean(), "Policy eligibility performs no storage preflight");
            Error(await Call(client, patch), "Recovery data is invalid");
            var read = await client.CallToolAsync("read_file", new Dictionary<string, object?> { ["path"] = "sample.md" });
            Assert(McpHttpChecks.Structured(read).GetProperty("content").GetString() == "old\n" && !Directory.Exists(fixture.PathOf("nested")) &&
                File.ReadAllText(fixture.RecoveryDirectory) == "foreign occupant" && runtime.Execution!.ActiveCalls == 0, "No unrecorded fallback, foreign-data cleanup or poisoned session");
        });

        await check("MCP HTTP errors preserve actual rollback, verification and final-save facts without partial success or raw diagnostics", async () =>
        {
            foreach (var scenario in new[] { "rollback", "verification", "final-save" })
            {
                using var fixture = new PatchExecutionFixture();
                const string secret = "DO_NOT_EXPORT_THIS_IO_ERROR";
                var finishing = false;
                await using var runtime = fixture.Runtime((stage, _) =>
                {
                    if (scenario == "rollback" && stage == PatchCommitStage.Installed) return Task.FromException(new IOException(secret + fixture.Root));
                    if (scenario == "verification" && stage == PatchCommitStage.BeforeVerification) fixture.Put("sample.md", "external\n");
                    return Task.CompletedTask;
                }, replace: (stage, path) =>
                {
                    if (stage == RecoveryManifestReplacementStage.TemporaryWritten) finishing = RecoveryManifestJson.Parse(File.ReadAllBytes(path)).State == RecoveryRecordState.Committed;
                    return scenario == "final-save" && finishing && stage == RecoveryManifestReplacementStage.BeforeReplace ? Task.FromException(new IOException(secret + fixture.Root)) : Task.CompletedTask;
                });
                await runtime.StartAsync(fixture.Configuration);
                using var http = Client();
                await using var client = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
                var failed = await Call(client, Patch("*** Update File: sample.md\n@@\n-old\n+new"));
                Error(failed, scenario == "final-save" ? "could not be saved and verified" : "verified result:");
                var record = await fixture.Load(fixture.RecordIds().Single());
                var text = string.Join('\n', failed.Content.OfType<TextContentBlock>().Select(block => block.Text));
                Assert(!text.Contains(secret) && !text.Contains(fixture.Root) && text.Contains(record.Manifest.ChangeId.ToString("N")) &&
                    record.Manifest.RunId == runtime.Snapshot.RunId && record.Manifest.State == (scenario == "rollback" ? RecoveryRecordState.RolledBack : scenario == "verification" ? RecoveryRecordState.Incomplete : RecoveryRecordState.Applying), "Public tool error retains safe record identity and actual last durable state");
                Assert(fixture.Get("sample.md") == (scenario == "rollback" ? "old\n" : scenario == "verification" ? "external\n" : "new\n") &&
                    Encoding.UTF8.GetString(record.Backups.Single().Bytes.AsSpan()) == "old\n", "No extra rollback or raw failure mislabeled as no installation");
            }
        });

        await check("MCP HTTP client cancellation during final save leaves the real commit tracked until durable completion", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            var entered = Signal(); var release = Signal();
            await using var runtime = fixture.Runtime(replace: async (stage, path) =>
            {
                if (stage == RecoveryManifestReplacementStage.TemporaryWritten && RecoveryManifestJson.Parse(File.ReadAllBytes(path)).State == RecoveryRecordState.Committed)
                { entered.TrySetResult(); await release.Task; }
            });
            await runtime.StartAsync(fixture.Configuration);
            using var http = Client();
            await using var client = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
            using var cancel = new CancellationTokenSource();
            var call = Call(client, Patch("*** Update File: sample.md\n@@\n-old\n+new"), cancellationToken: cancel.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                cancel.Cancel();
                await Reject<OperationCanceledException>(() => call).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(runtime.Execution!.ActiveCalls == 1 && fixture.Get("sample.md") == "new\n" && fixture.ReadManifest().State == RecoveryRecordState.Applying && runtime.Snapshot.State == RuntimeState.Running,
                    "Lost client response does not abandon server-side finalization or its in-flight ownership");
                release.TrySetResult();
                await Until(() => runtime.Execution!.ActiveCalls == 0);
                Assert((await fixture.Load(fixture.RecordIds().Single())).Manifest.State == RecoveryRecordState.Committed, "Server completes durable state despite caller cancellation");
                Assert(McpHttpChecks.Structured(await client.CallToolAsync("read_file", new Dictionary<string, object?> { ["path"] = "sample.md" }))
                    .GetProperty("content").GetString() == "new\n", "Session remains usable after the client drops its wait");
            }
            finally { cancel.Cancel(); release.TrySetResult(); await Drain(call); }
        });

        await check("MCP HTTP cancellation of an actual patch waiting behind Bash creates no record and leaves the active child alone", async () =>
        {
            using var fixture = new PatchExecutionFixture();
            await using var runtime = fixture.Runtime();
            await runtime.StartAsync(fixture.Configuration);
            using var http = Client();
            await using var first = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
            await using var second = await McpHttpChecks.ConnectAsync(runtime.Address!, http);
            var execution = runtime.Execution!;
            using var cancel = new CancellationTokenSource();
            var marker = fixture.PathOf("patch-child.pid");
            var bash = first.CallToolAsync("bash", new Dictionary<string, object?> { ["command"] = RuntimeCallChecks.ChildCommand(marker) }).AsTask();
            using var child = await RuntimeCallChecks.ObserveChildAsync(marker, bash);
            Task<CallToolResult>? patch = null;
            try
            {
                patch = Call(second, Patch("*** Add File: nested/new.md\n+new"), cancellationToken: cancel.Token);
                await Until(() => execution.ActiveCalls == 2);
                cancel.Cancel();
                await Reject<OperationCanceledException>(() => patch).WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => execution.ActiveCalls == 1);
                Assert(!child.HasExited && !Directory.Exists(fixture.RecoveryDirectory) && !Directory.Exists(fixture.PathOf("nested")), "Waiting write cannot create backups or cancel a different caller's Bash");
                await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await Drain(bash);
                await RuntimeCallChecks.AssertStoppedAsync(child);
                Assert(execution.IsDisposed && execution.ActiveCalls == 0 && runtime.Snapshot.State == RuntimeState.Stopped, "Stop drains actual and queued tool calls before disposal");
            }
            finally { cancel.Cancel(); RuntimeCallChecks.KillIfRunning(child); if (patch is not null) await Drain(patch); await Drain(bash); }
        });
    }

    private static HttpClient Client() => new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private static Task<CallToolResult> Call(McpClient client, string patch, bool dryRun = false, CancellationToken cancellationToken = default) => client.CallToolAsync("apply_patch",
        new Dictionary<string, object?> { ["patch"] = patch, ["dry_run"] = dryRun, ["approved"] = true, ["mode"] = "FullAccess", ["recoveryReady"] = false,
            ["disableRecovery"] = true, ["runId"] = Guid.Empty, ["changeId"] = Guid.Empty }, cancellationToken: cancellationToken).AsTask();
    private static JsonElement Result(CallToolResult result)
    {
        var json = McpHttpChecks.Structured(result);
        using var text = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
        Assert(JsonElement.DeepEquals(json, text.RootElement), "Text and structured output contain the same complete public result");
        return json;
    }
    private static void Error(CallToolResult result, string message) => Assert(result.IsError == true && result.StructuredContent is null &&
        result.Content.OfType<TextContentBlock>().Any(block => block.Text.Contains(message, StringComparison.Ordinal)) &&
        !result.Content.OfType<TextContentBlock>().Any(block => block.Text.Contains("# Review diff")), "Failure returns no partial preview or false success: " + message);
    private static async Task Until(Func<bool> condition)
    { for (var index = 0; index < 500; index++) { if (condition()) return; await Task.Delay(10); } throw new Exception("Timed out waiting for actual patch lifecycle state."); }
    private static void Assert(bool condition, string message) => PatchExecutionFixture.Assert(condition, message);
}
