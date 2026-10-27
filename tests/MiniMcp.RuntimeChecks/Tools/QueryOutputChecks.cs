using MiniMcp.Services.Processes;
using MiniMcp.Services.Workspace;
using System.Diagnostics;
using System.Text;

internal static class QueryOutputChecks
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        foreach (var boundary in new[] { "record", "frame", "total" })
            await check($"query output: {boundary} limit discards a pending UTF-8 tail and terminates the writer", async () =>
            {
                var frames = new List<string>();
                var result = await RunAsync("limited-tail", boundary == "frame" ? 8 : 128, boundary == "total" ? 8 : 1024,
                    frame => { frames.Add(frame); return boundary != "record"; });
                Assert(result.OutputLimited, "The requested boundary is reported as truncation");
                Assert(boundary == "record" ? frames.SequenceEqual(new[] { new string('a', 32) }) : frames.Count == 0,
                    "Only complete records preceding the limit are delivered");
            });

        await check("query output: split UTF-8 sequences remain valid before the output limit", async () =>
        {
            var frames = new List<string>();
            var result = await RunAsync("split-utf8", 128, 1024, frame => { frames.Add(frame); return true; });
            Assert(result.ExitCode == 0 && !result.OutputLimited && frames.SequenceEqual(new[] { "first", "中👋" }),
                "Multibyte characters survive separate pipe writes");
        });

        foreach (var mode in new[] { "invalid-utf8", "incomplete-utf8", "incomplete-record" })
            await check($"query output: {mode} without truncation remains a protocol error", async () =>
            {
                try { await RunAsync(mode, 128, 1024, _ => true); }
                catch (WorkspaceException error)
                {
                    Assert(error.Message.Contains(mode == "incomplete-record" ? "incomplete output record" : "invalid UTF-8"),
                        "The original protocol validation remains strict");
                    return;
                }
                throw new Exception("Expected malformed query output to be rejected.");
            });
    }

    private static async Task<QueryProcessResult> RunAsync(string mode, int frameLimit, int outputLimit, Func<string, bool> accept)
    {
        var directory = Path.Combine(Path.GetTempPath(), "minimcp-query-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "writer.pid");
        var executable = Environment.ProcessPath!;
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(typeof(QueryOutputChecks).Assembly.Location);
        arguments.AddRange(["--query-output-child", mode, marker]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            return await ReadOnlyQueryRunner.RunAsync(executable, directory, arguments, '\n', frameLimit, outputLimit,
                accept, deadline.Token);
        }
        finally
        {
            if (File.Exists(marker))
            {
                var pid = int.Parse(await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    using var process = Process.GetProcessById(pid);
                    Assert(process.HasExited, "The query writer is stopped before its result or error is returned");
                }
                catch (ArgumentException) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static async Task<int> ChildAsync(string mode, string marker)
    {
        await File.WriteAllTextAsync(marker, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var output = Console.OpenStandardOutput();
        if (mode == "limited-tail")
        {
            // One short write leaves a complete record and a pending three-byte UTF-8 character
            // in StreamReader. Only termination closes the pipe and makes that tail incomplete.
            byte[] bytes = [.. Encoding.UTF8.GetBytes(new string('a', 32) + "\n"), 0xe4];
            await output.WriteAsync(bytes);
            await output.FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        else if (mode == "split-utf8")
        {
            byte[] prefix = [.. Encoding.UTF8.GetBytes("first\n"), 0xe4];
            await output.WriteAsync(prefix);
            await output.FlushAsync();
            await Task.Delay(20);
            byte[] suffix = [0xb8, 0xad, .. Encoding.UTF8.GetBytes("👋\n")];
            await output.WriteAsync(suffix);
        }
        else if (mode == "invalid-utf8") await output.WriteAsync(new byte[] { 0xff, 10 });
        else if (mode == "incomplete-utf8") await output.WriteAsync(new byte[] { 0xe4, 0xb8 });
        else if (mode == "incomplete-record") await output.WriteAsync(Encoding.UTF8.GetBytes("no separator"));
        else throw new ArgumentException("Unknown query output fixture mode.");
        await output.FlushAsync();
        return 0;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Query output check: " + message);
    }
}
