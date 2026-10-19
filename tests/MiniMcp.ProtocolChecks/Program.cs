using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MiniMcp.Configuration;
using MiniMcp.Services;
using MiniMcp.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Exercise the real SDK and registered tools over in-memory JSON-RPC streams, without opening a port.
var root = Path.Combine(Path.GetTempPath(), $"minimcp-protocol-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    File.WriteAllText(Path.Combine(root, "sample.txt"), "first\nsecond\n");
    var input = new Pipe();
    var output = new Pipe();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(new MiniMcpOptions(root, "Local", null, null, null,
        BashPath: Environment.GetEnvironmentVariable("MCP_BASH_PATH")));
    services.AddSingleton<ProjectWorkspace>();
    services.AddSingleton<PatchService>();
    services.AddSingleton<BashRunner>();
    services.AddMcpServer().WithStreamServerTransport(input.Reader.AsStream(), output.Writer.AsStream()).WithTools<WorkspaceTools>();
    await using var provider = services.BuildServiceProvider();
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
        Console.WriteLine("PASS MCP initialize, exact tool list and parameter schemas");

        async Task<JsonElement> Call(string name, Dictionary<string, object?> arguments, bool error = false)
        {
            var result = await client.CallToolAsync(name, arguments, cancellationToken: timeout.Token);
            Assert((result.IsError == true) == error, "Tool error state: " + name);
            return result.StructuredContent is null ? default : JsonSerializer.SerializeToElement(result.StructuredContent);
        }

        var read = await Call("read_file", new() { ["path"] = "sample.txt", ["start_line"] = 2, ["end_line"] = 2 });
        Assert(read.GetProperty("content").GetString() == "second\n" && read.GetProperty("totalLines").GetInt32() == 2, "Read range");
        const string add = "*** Begin Patch\n*** Add File: new.txt\n+hello\n*** End Patch\n";
        var preview = await Call("apply_patch", new() { ["patch"] = add, ["dry_run"] = true });
        Assert(preview.GetProperty("dryRun").GetBoolean() && !File.Exists(Path.Combine(root, "new.txt")), "Dry run");
        await Call("apply_patch", new() { ["patch"] = add });
        await Call("apply_patch", new() { ["patch"] = "*** Begin Patch\n*** Update File: new.txt\n@@\n-hello\n+updated\n*** End Patch" });
        Assert((await Call("read_file", new() { ["path"] = "new.txt" })).GetProperty("content").GetString() == "updated\n", "Patch readback");
        await Call("apply_patch", new() { ["patch"] = "*** Begin Patch\n*** Delete File: new.txt\n*** End Patch" });
        Assert(!File.Exists(Path.Combine(root, "new.txt")), "Delete");
        Console.WriteLine("PASS MCP read ranges and patch preview/add/update/delete");

        var command = await Call("bash", new() { ["command"] = "printf hello; printf diagnostic >&2; exit 7" });
        Assert(command.GetProperty("exitCode").GetInt32() == 7 && command.GetProperty("stdout").GetString() == "hello" &&
            command.GetProperty("stderr").GetString() == "diagnostic", "Bash exit and output");
        var expired = await Call("bash", new() { ["command"] = "printf partial; sleep 30", ["timeout_seconds"] = 1 });
        Assert(expired.GetProperty("timedOut").GetBoolean() && expired.GetProperty("exitCode").ValueKind == JsonValueKind.Null &&
            expired.GetProperty("stdout").GetString() == "partial", "Bash timeout");
        await Call("read_file", new() { ["path"] = "../outside" }, error: true);
        await Call("apply_patch", new() { ["patch"] = "malformed" }, error: true);
        await Call("bash", new() { ["command"] = "pwd", ["timeout_seconds"] = 0 }, error: true);
        Console.WriteLine("PASS MCP Bash nonzero exit, timeout and tool errors");
    }
    finally
    {
        await timeout.CancelAsync();
        try { await running; } catch (OperationCanceledException) { }
    }
    return 0;
}
finally { Directory.Delete(root, recursive: true); }

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
