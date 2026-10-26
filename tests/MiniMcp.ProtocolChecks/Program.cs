using MiniMcp.Services.Patching;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MiniMcp.Configuration;
using MiniMcp.Services;
using MiniMcp.Recovery;
using MiniMcp.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Exercise the real SDK and shared execution boundary over in-memory JSON-RPC streams.
var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-protocol-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporary);
try
{
    foreach (var mode in Enum.GetValues<ExecutionMode>()) await CheckModeAsync(temporary, mode);
    return 0;
}
finally { Directory.Delete(temporary, recursive: true); }

static async Task CheckModeAsync(string temporary, ExecutionMode mode)
{
    var directory = Path.Combine(temporary, mode.ToString());
    var root = Path.Combine(directory, "project");
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "sample.txt"), "first\nsecond\n");
    File.WriteAllText(Path.Combine(root, ".env"), "PRIVATE_TEST_VALUE=fixture\n");
    File.WriteAllText(Path.Combine(root, "complex.txt"), string.Concat(Enumerable.Range(0, 1000).Select(i => $"old{i}\n")));
    File.WriteAllText(Path.Combine(root, "moving.cs"), "same\n");
    var paths = new ConfigurationPaths(Path.Combine(directory, "user"), Path.Combine(directory, "state"));
    var settings = new AppSettings
    {
        ProjectRoot = root,
        // ReadOnly must work even when no Bash path has been resolved.
        BashPath = mode == ExecutionMode.ReadOnly ? "" : Environment.GetEnvironmentVariable("MINIMCP_TEST_BASH_PATH") ?? "",
        Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local }
    };
    var source = Path.Combine(root, "custom.md");
    File.WriteAllText(source, ConfigurationJson.SerializeSettings(settings));
    var before = Snapshot(directory);
    var configuration = new ConfigurationValidator(paths).Validate(settings, source, new ExecutionPolicy { Mode = mode });

    var input = new Pipe();
    var output = new Pipe();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddToolExecution(Guid.NewGuid(), configuration);
    services.AddMcpServer().WithStreamServerTransport(input.Reader.AsStream(), output.Writer.AsStream()).WithTools<WorkspaceTools>();
    await using var provider = services.BuildServiceProvider();
    var execution = provider.GetRequiredService<ToolExecutionService>();
    var server = provider.GetRequiredService<McpServer>();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var running = server.RunAsync(timeout.Token);
    try
    {
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(input.Writer.AsStream(), output.Reader.AsStream()),
            cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert(tools.Select(tool => tool.Name).Order().SequenceEqual(new[] { "apply_patch", "bash", "git_diff", "git_log", "git_status", "list_files", "read_file", "search_text" }), "Exactly eight tools");
        foreach (var tool in tools)
        {
            var properties = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order().ToArray();
            string[] expected = tool.Name switch
            {
                "read_file" => ["end_line", "path", "start_line"],
                "apply_patch" => ["dry_run", "patch"],
                "list_files" => ["format", "include_hidden", "max_depth", "max_entries", "path"],
                "search_text" => ["case_sensitive", "glob", "max_depth", "max_results", "path", "pattern", "regex"],
                "git_status" => ["max_entries", "path"],
                "git_diff" => ["max_files", "max_lines", "path", "staged"],
                "git_log" => ["max_count", "path"],
                _ => ["command", "timeout_seconds", "workdir"]
            };
            Assert(properties.SequenceEqual(expected), "Schema for " + tool.Name);
            if (tool.Name == "apply_patch")
                Assert(tool.ProtocolTool.OutputSchema is { } outputSchema && outputSchema.GetProperty("properties").EnumerateObject()
                    .Select(property => property.Name).Order().SequenceEqual(new[] { "changes", "commit", "decision", "diff", "dryRun", "risk", "statistics" }), "Public preview output schema");
        }
        Console.WriteLine($"PASS {mode}: MCP initialize, exact tool list and parameter schemas");

        async Task<JsonElement> Call(string name, Dictionary<string, object?> arguments, bool error = false, string? message = null)
        {
            var result = await client.CallToolAsync(name, arguments, cancellationToken: timeout.Token);
            Assert((result.IsError == true) == error, "Tool error state: " + name);
            if (message is not null)
                Assert(result.Content.OfType<TextContentBlock>().Any(block => block.Text.Contains(message, StringComparison.Ordinal)),
                    "Expected tool error message: " + message);
            var payload = result.StructuredContent is null ? default : JsonSerializer.SerializeToElement(result.StructuredContent);
            if (name == "apply_patch")
            {
                if (error)
                    Assert(result.StructuredContent is null && !result.Content.OfType<TextContentBlock>().Any(block => block.Text.Contains("# Review diff")), "Errors expose no partial preview");
                else
                {
                    Assert(payload.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "changes", "commit", "decision", "diff", "dryRun", "risk", "statistics" }), "Only public preview fields are exported");
                    using var textPayload = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
                    Assert(JsonElement.DeepEquals(textPayload.RootElement, payload), "Text and structured content contain the same complete preview and decision");
                }
            }
            return payload;
        }

        var read = await Call("read_file", new() { ["path"] = "sample.txt", ["start_line"] = 2, ["end_line"] = 2 });
        Assert(read.GetProperty("content").GetString() == "second\n" && read.GetProperty("totalLines").GetInt32() == 2, "Read range");
        await Call("read_file", new() { ["path"] = "../outside" }, error: true, message: "outside");
        await Call("read_file", new() { ["path"] = ".env" }, error: true, message: "private");
        await Call("apply_patch", new() { ["patch"] = "malformed", ["dry_run"] = true }, error: true);
        await Call("apply_patch", new()
        {
            ["patch"] = "*** Begin Patch\n*** Delete File: .env\n*** End Patch", ["dry_run"] = true
        }, error: true, message: "private");

        string[] patches =
        [
            "*** Begin Patch\n*** Add File: nested/new.txt\n+hello\n*** End Patch",
            "*** Begin Patch\n*** Update File: sample.txt\n@@\n-first\n+updated\n*** End Patch",
            "*** Begin Patch\n*** Update File: sample.txt\n*** Move to: moved/new.txt\n*** End Patch",
            "*** Begin Patch\n*** Delete File: sample.txt\n*** End Patch"
        ];
        var actions = new[] { "add", "update", "move", "delete" };
        var expectedStatistics = new[] { new PatchStatistics(1, 0, 6, 0), new PatchStatistics(1, 1, 8, 6),
            new PatchStatistics(0, 0, 0, 0), new PatchStatistics(0, 2, 0, 13) };
        for (var i = 0; i < patches.Length; i++)
        {
            var preview = await Call("apply_patch", new() { ["patch"] = patches[i], ["dry_run"] = true });
            Assert(preview.GetProperty("dryRun").GetBoolean() &&
                preview.GetProperty("changes")[0].GetProperty("summary").GetProperty("action").GetString() == actions[i], "Patch preview: " + actions[i]);
            Assert(preview.GetProperty("diff").GetString()!.Contains("# action: " + actions[i]), "Complete diff action");
            var expected = JsonSerializer.SerializeToElement(expectedStatistics[i], new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert(JsonElement.DeepEquals(preview.GetProperty("statistics"), expected) &&
                JsonElement.DeepEquals(preview.GetProperty("changes")[0].GetProperty("statistics"), expected), "Per-file and total statistics");
            CheckReview(preview, mode, actions[i] is "move" ? ["Move"] : actions[i] is "delete" ? ["Delete"] : []);
            Assert(preview.GetProperty("changes")[0].GetProperty("risk").GetProperty("kind").GetString() == "Ordinary", "File kind is a string");
            Assert(preview.GetProperty("commit").ValueKind == JsonValueKind.Null, "Preview has no commit record or authorization");
            if (mode == ExecutionMode.ReadOnly || (mode == ExecutionMode.ProjectEdit && i >= 2))
                await Call("apply_patch", new() { ["patch"] = patches[i] }, error: true,
                    message: mode == ExecutionMode.ReadOnly ? "ReadOnly" : "ProjectEdit restrictions");
        }
        // Agent-supplied approval or mode fields cannot grant write permissions.
        if (mode != ExecutionMode.FullAccess)
            await Call("apply_patch", new()
            {
                ["patch"] = patches[2], ["approved"] = true, ["mode"] = "FullAccess"
            }, error: true);
        Console.WriteLine($"PASS {mode}: read boundaries, patch previews and locally denied writes");

        var riskCases = new (string Body, string[] Reasons)[]
        {
            ("*** Add File: nested/ordinary.md\n+x\n*** Add File: Directory.Build.cs\n+b\n*** Add File: data.json\n+u\n" +
                "*** Delete File: sample.txt\n*** Update File: moving.cs\n*** Move to: renamed.cs", ["CriticalFile", "UnknownFileType", "Delete", "Move"]),
            (string.Join('\n', Enumerable.Range(0, 11).Select(i => $"*** Add File: path{i}.md")), ["PathLimitExceeded"]),
            ("*** Add File: lines.md\n" + string.Join('\n', Enumerable.Repeat("+", 501)), ["ChangedLineLimitExceeded"]),
            ("*** Add File: bytes.md\n+" + new string('x', 65536), ["ChangedByteLimitExceeded"]),
            ("*** Update File: custom.md\n@@\n-  \"port\": 8000,\n+  \"port\": 8123,", ["CriticalFile"])
        };
        foreach (var (body, reasons) in riskCases)
        {
            var patch = "*** Begin Patch\n" + body + "\n*** End Patch";
            var preview = await Call("apply_patch", new()
            {
                ["patch"] = patch, ["dry_run"] = true, ["approved"] = true, ["mode"] = "FullAccess", ["recoveryReady"] = true,
                ["projectEditLimits"] = new { maxPaths = int.MaxValue, maxChangedLines = int.MaxValue, maxChangedBytes = int.MaxValue }
            });
            CheckReview(preview, mode, reasons);
            Assert(preview.GetProperty("risk").GetProperty("limits").GetProperty("maxPaths").GetInt32() == 10, "Only local limits apply");
            if (body.StartsWith("*** Update File: custom.md", StringComparison.Ordinal))
                Assert(preview.GetProperty("changes")[0].GetProperty("risk").GetProperty("kind").GetString() == "Critical", "Actual selected settings source is critical");
            if (mode != ExecutionMode.FullAccess)
                await Call("apply_patch", new() { ["patch"] = patch, ["approved"] = true, ["mode"] = "FullAccess", ["recoveryReady"] = true },
                    error: true, message: mode == ExecutionMode.ReadOnly ? "ReadOnly" : "ProjectEdit restrictions");
        }
        Console.WriteLine($"PASS {mode}: risk causes, batch thresholds, selected settings and mode decisions cannot be overridden by tool arguments");

        var longLine = new string('x', 180_000) + "TAIL中👋\t\u001b";
        var large = await Call("apply_patch", new() { ["patch"] = "*** Begin Patch\n*** Add File: large.txt\n+" + longLine + "\n*** End Patch", ["dry_run"] = true });
        var largeDiff = large.GetProperty("diff").GetString()!;
        Assert(largeDiff.Length > 16_000 && largeDiff.Contains("TAIL中👋\\t\\u001b\\n\""), "Large preview remains complete and escaped");
        Assert(large.GetProperty("statistics").GetProperty("addedBytes").GetInt64() == System.Text.Encoding.UTF8.GetByteCount(longLine + "\n"), "Statistics use unescaped UTF-8 bytes");
        CheckReview(large, mode, "ChangedByteLimitExceeded");
        Console.WriteLine($"PASS {mode}: complete large escaped diff survives structured and text serialization");

        await Call("apply_patch", new()
        {
            ["patch"] = "*** Begin Patch\n*** Add File: oversized.txt\n+" + new string('\u001b', 50_000) + "\n*** End Patch", ["dry_run"] = true
        }, error: true, message: "display limit");
        await Call("apply_patch", new()
        {
            ["patch"] = "*** Begin Patch\n*** Add File: oversized.txt\n+" + new string('\u001b', 50_000) + "\n*** End Patch", ["approved"] = true
        }, error: true, message: "display limit");
        var complex = "*** Begin Patch\n*** Update File: complex.txt\n@@\n" +
            string.Concat(Enumerable.Range(0, 1000).Select(i => $"-old{i}\n")) + string.Concat(Enumerable.Range(0, 1000).Select(i => $"+new{i}\n")) + "*** End Patch";
        await Call("apply_patch", new() { ["patch"] = complex, ["dry_run"] = true }, error: true, message: "computation limit");
        Console.WriteLine($"PASS {mode}: oversized and complex previews return tool errors without partial content");

        if (mode != ExecutionMode.FullAccess)
        {
            var deniedCommand = new Dictionary<string, object?> { ["command"] = "printf unexpected > bash-denied.txt" };
            await Call("bash", deniedCommand, error: true,
                message: mode == ExecutionMode.ReadOnly ? "ReadOnly" : "ProjectEdit restrictions");
            await Call("bash", new()
            {
                ["command"] = deniedCommand["command"], ["approved"] = true, ["mode"] = "FullAccess"
            }, error: true);
            await Call("bash", deniedCommand, error: true,
                message: mode == ExecutionMode.ReadOnly ? "ReadOnly" : "ProjectEdit restrictions");
        }
        else
        {
            var command = await Call("bash", new() { ["command"] = "printf hello; printf diagnostic >&2; exit 7" });
            Assert(command.GetProperty("exitCode").GetInt32() == 7 && command.GetProperty("stdout").GetString() == "hello" &&
                command.GetProperty("stderr").GetString() == "diagnostic", "Bash exit and output");
            var expired = await Call("bash", new() { ["command"] = "printf partial; sleep 30", ["timeout_seconds"] = 1 });
            Assert(expired.GetProperty("timedOut").GetBoolean() && expired.GetProperty("exitCode").ValueKind == JsonValueKind.Null &&
                expired.GetProperty("stdout").GetString() == "partial", "Bash timeout");
            await Call("bash", new() { ["command"] = "pwd", ["timeout_seconds"] = 0 }, error: true);
            await Call("bash", new() { ["command"] = "pwd", ["workdir"] = ".." }, error: true, message: "outside");
        }
        Console.WriteLine($"PASS {mode}: Bash permissions and account execution boundaries");

        // Tool errors leave the server usable; previews and denials must not create storage artifacts.
        Assert((await Call("read_file", new() { ["path"] = "sample.txt" })).GetProperty("content").GetString() == "first\nsecond\n",
            "Read after tool errors");
        Assert(before.SequenceEqual(Snapshot(directory)), "Tools changed files, directories, private data or recovery storage");
        Console.WriteLine($"PASS {mode}: tool errors preserve service availability and leave files/directories unchanged");

        var writes = new[] { "*** Add File: written.md\n+first", "*** Update File: written.md\n@@\n-first\n+second",
            "*** Update File: written.md\n*** Move to: committed/moved.md", "*** Delete File: committed/moved.md" };
        var ids = new HashSet<Guid>();
        for (var i = 0; i < writes.Length; i++)
        {
            var patch = "*** Begin Patch\n" + writes[i] + "\n*** End Patch";
            var arguments = new Dictionary<string, object?> { ["patch"] = patch, ["approved"] = true, ["mode"] = "FullAccess",
                ["requiresRecoveryRecord"] = false, ["recoveryReady"] = false, ["disableRecovery"] = true };
            if (mode == ExecutionMode.ReadOnly || (mode == ExecutionMode.ProjectEdit && i >= 2))
            {
                // ReadOnly has no written.md, so use a valid fresh add for each denial.
                if (mode == ExecutionMode.ReadOnly) arguments["patch"] = patches[0];
                await Call("apply_patch", arguments, error: true, message: mode == ExecutionMode.ReadOnly ? "ReadOnly" : "ProjectEdit restrictions");
                if (mode == ExecutionMode.ProjectEdit) break;
                continue;
            }
            var written = await Call("apply_patch", arguments);
            var commit = written.GetProperty("commit");
            var id = commit.GetProperty("changeId").GetGuid();
            Assert(!written.GetProperty("dryRun").GetBoolean() && commit.GetProperty("state").GetString() == "Committed" &&
                commit.GetProperty("runId").GetGuid() == execution.RunId && !commit.GetProperty("cleanupIncomplete").GetBoolean() && ids.Add(id), "Bound public commit identity and successful final state");
            var record = await new RecoveryStore(execution.RunId, configuration).LoadAsync(id);
            Assert(record.Manifest.State == RecoveryRecordState.Committed && record.Manifest.RunId == execution.RunId &&
                record.Manifest.Operations.Single().Action == (i == 0 ? "add" : i == 1 ? "update" : i == 2 ? "move" : "delete"), "Actual durable record exists despite forged recovery parameters");
            if (i > 0) Assert(System.Text.Encoding.UTF8.GetString(record.Backups.Single().Bytes.AsSpan()) == (i == 1 ? "first\n" : "second\n"), "Original content precedes each actual write");
        }
        Assert(ids.Count == (mode == ExecutionMode.ReadOnly ? 0 : mode == ExecutionMode.ProjectEdit ? 2 : 4), "Only locally allowed writes get records");
        Assert(mode == ExecutionMode.ReadOnly ? !Directory.Exists(configuration.Policy.RecoveryDirectory) :
            Directory.EnumerateDirectories(configuration.Policy.RecoveryDirectory).Count() == ids.Count, "No unrecorded fallback or extra records");
        if (mode == ExecutionMode.ProjectEdit) Assert(File.ReadAllText(Path.Combine(root, "written.md")) == "second\n", "Approval denial preserves existing local output");
        Console.WriteLine($"PASS {mode}: real recorded writes, original backups and unforgeable local policy");
    }
    finally
    {
        await timeout.CancelAsync();
        try { await running; } catch (OperationCanceledException) { }
    }
}

static string[] Snapshot(string directory) => Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
    .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path) +
        (Directory.Exists(path) ? "/" : ":" + Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();

static void CheckReview(JsonElement preview, ExecutionMode mode, params string[] reasons)
{
    var risk = preview.GetProperty("risk");
    Assert(risk.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal)
        .SequenceEqual(reasons.Order(StringComparer.Ordinal)), "Whole-batch risk names");
    var statistics = preview.GetProperty("statistics");
    Assert(risk.GetProperty("size").GetProperty("changedLines").GetInt64() == statistics.GetProperty("addedLines").GetInt64() + statistics.GetProperty("removedLines").GetInt64() &&
        risk.GetProperty("size").GetProperty("changedBytes").GetInt64() == statistics.GetProperty("addedBytes").GetInt64() + statistics.GetProperty("removedBytes").GetInt64(), "Risk and diff share real statistics");
    var decision = preview.GetProperty("decision");
    var review = mode == ExecutionMode.ProjectEdit && reasons.Length != 0;
    Assert(decision.GetProperty("mode").GetString() == mode.ToString() &&
        decision.GetProperty("disposition").GetString() == (mode == ExecutionMode.ReadOnly || review ? "Denied" : "Automatic"), "Local mode decision");
    string[] blockers = mode == ExecutionMode.ReadOnly ? ["ReadOnlyMode"] : review ? ["ProjectEditRestriction"] : [];
    Assert(decision.GetProperty("blockers").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal)
        .SequenceEqual(blockers.Order(StringComparer.Ordinal)), "Current blocking causes");
    Assert(decision.GetProperty("canCommit").GetBoolean() == (blockers.Length == 0) && decision.GetProperty("requiresRecoveryRecord").GetBoolean() == (blockers.Length == 0), "Local blockers determine eligibility; every writable mode requires a real recovery record");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
