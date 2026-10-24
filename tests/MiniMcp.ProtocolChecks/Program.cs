using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MiniMcp.Configuration;
using MiniMcp.Services;
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
    var before = Snapshot(directory);
    var paths = new ConfigurationPaths(Path.Combine(directory, "user"), Path.Combine(directory, "state"));
    var configuration = new ConfigurationValidator(paths).Validate(new AppSettings
    {
        ProjectRoot = root,
        // ReadOnly must work even when no Bash path has been resolved.
        BashPath = mode == ExecutionMode.ReadOnly ? "" : Environment.GetEnvironmentVariable("MINIMCP_TEST_BASH_PATH") ?? "",
        Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local }
    }, Path.Combine(directory, "settings.json"), new ExecutionPolicy { Mode = mode });

    var input = new Pipe();
    var output = new Pipe();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddToolExecution(Guid.NewGuid(), configuration);
    services.AddMcpServer().WithStreamServerTransport(input.Reader.AsStream(), output.Writer.AsStream()).WithTools<WorkspaceTools>();
    await using var provider = services.BuildServiceProvider();
    _ = provider.GetRequiredService<ToolExecutionService>();
    var server = provider.GetRequiredService<McpServer>();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var running = server.RunAsync(timeout.Token);
    try
    {
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(input.Writer.AsStream(), output.Reader.AsStream()),
            cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert(tools.Select(tool => tool.Name).Order().SequenceEqual(new[] { "apply_patch", "bash", "read_file" }), "Exactly three tools");
        foreach (var tool in tools)
        {
            var properties = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order().ToArray();
            string[] expected = tool.Name switch
            {
                "read_file" => ["end_line", "path", "start_line"],
                "apply_patch" => ["dry_run", "patch"],
                _ => ["command", "timeout_seconds", "workdir"]
            };
            Assert(properties.SequenceEqual(expected), "Schema for " + tool.Name);
        }
        Console.WriteLine($"PASS {mode}: MCP initialize, exact tool list and parameter schemas");

        async Task<JsonElement> Call(string name, Dictionary<string, object?> arguments, bool error = false, string? message = null)
        {
            var result = await client.CallToolAsync(name, arguments, cancellationToken: timeout.Token);
            Assert((result.IsError == true) == error, "Tool error state: " + name);
            if (message is not null)
                Assert(result.Content.OfType<TextContentBlock>().Any(block => block.Text.Contains(message, StringComparison.Ordinal)),
                    "Expected tool error message: " + message);
            return result.StructuredContent is null ? default : JsonSerializer.SerializeToElement(result.StructuredContent);
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
        for (var i = 0; i < patches.Length; i++)
        {
            var preview = await Call("apply_patch", new() { ["patch"] = patches[i], ["dry_run"] = true });
            Assert(preview.GetProperty("dryRun").GetBoolean() &&
                preview.GetProperty("changes")[0].GetProperty("action").GetString() == actions[i], "Patch preview: " + actions[i]);
            await Call("apply_patch", new() { ["patch"] = patches[i] }, error: true,
                message: mode == ExecutionMode.ReadOnly ? "ReadOnly" : "recovery records");
        }
        // Agent-supplied approval or mode fields cannot grant write permissions.
        await Call("apply_patch", new()
        {
            ["patch"] = patches[0], ["approved"] = true, ["mode"] = "FullAccess"
        }, error: true);
        Console.WriteLine($"PASS {mode}: read boundaries, patch previews and denied add/update/move/delete");

        if (mode != ExecutionMode.FullAccess)
        {
            var deniedCommand = new Dictionary<string, object?> { ["command"] = "printf unexpected > bash-denied.txt" };
            await Call("bash", deniedCommand, error: true,
                message: mode == ExecutionMode.ReadOnly ? "ReadOnly" : "local approval");
            await Call("bash", new()
            {
                ["command"] = deniedCommand["command"], ["approved"] = true, ["mode"] = "FullAccess"
            }, error: true);
            await Call("bash", deniedCommand, error: true,
                message: mode == ExecutionMode.ReadOnly ? "ReadOnly" : "local approval");
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

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
