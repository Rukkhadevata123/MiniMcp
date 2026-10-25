using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Runtime;

internal sealed record TunnelScenario(string Directory, string Behavior = "wait", string? HealthAddress = null);
internal sealed record TunnelChildReport(int ProcessId, string[] Arguments, bool Redirected, bool InJob,
    bool HasConsole, bool HostReady, bool HasInheritedToken);

internal static class TunnelChild
{
    internal static async Task<int> RunAsync(string tokenFile, string[] arguments)
    {
        var scenario = JsonSerializer.Deserialize<TunnelScenario>(await File.ReadAllTextAsync(tokenFile))!;
        var directory = scenario.Directory;
        if (scenario.Behavior is "descendant" or "exit-descendant")
        {
            using var descendant = StartSelf("--tunnel-descendant", Path.Combine(directory, "descendant.pid"));
            await WaitUntilAsync(() => File.Exists(Path.Combine(directory, "descendant.pid")));
        }
        var hostReady = true;
        if (scenario.HealthAddress is { } health)
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            hostReady = (await http.GetAsync(health)).IsSuccessStatusCode;
        }
        var inJob = false;
        if (OperatingSystem.IsWindows() && !IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out inJob))
            throw new Exception("Cannot inspect child job membership.");
        var report = new TunnelChildReport(Environment.ProcessId, arguments, Console.IsOutputRedirected && Console.IsErrorRedirected,
            inJob, OperatingSystem.IsWindows() && GetConsoleWindow() != IntPtr.Zero, hostReady,
            Environment.GetEnvironmentVariable("TUNNEL_TOKEN") is not null);
        await AtomicWriteAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report));
        await Console.Out.WriteLineAsync("Authorization: Bearer private-tunnel-value");
        await Console.Error.WriteLineAsync("""{"clientSecret":"private-tunnel-value"}""");
        if (scenario.Behavior == "flood")
        {
            static async Task WriteFlood(TextWriter writer, string label)
            {
                var chunk = new string('x', 4096);
                await writer.WriteAsync("token=private-long-output");
                for (var i = 0; i < 512; i++) await writer.WriteAsync(chunk);
                await writer.WriteLineAsync();
                for (var i = 0; i < 3000; i++) await writer.WriteLineAsync(label + i);
                await writer.FlushAsync();
            }
            await Task.WhenAll(WriteFlood(Console.Out, "stdout-"), WriteFlood(Console.Error, "stderr-"));
            await AtomicWriteAsync(Path.Combine(directory, "flood.done"), "done");
        }
        if (scenario.Behavior == "exit") return 17;
        await WaitUntilAsync(() => File.Exists(Path.Combine(directory, "exit")), Timeout.InfiniteTimeSpan);
        return 23;
    }

    internal static async Task<int> DescendantAsync(string marker)
    {
        await AtomicWriteAsync(marker, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    internal static async Task<int> OwnerAsync(string ownerFile)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(ownerFile))!;
        var parent = Path.GetDirectoryName(ownerFile)!;
        var paths = new ConfigurationPaths(Path.Combine(parent, "user"), Path.Combine(parent, "state"));
        var configuration = new ConfigurationValidator(paths).Validate(settings, ownerFile, new ExecutionPolicy());
        await using var runtime = new McpRuntime(0);
        await runtime.StartAsync(configuration);
        await AtomicWriteAsync(Path.Combine(parent, "owner.ready"), JsonSerializer.Serialize(runtime.Snapshot.Tunnel));
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    internal static Process StartSelf(params string[] arguments)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "MiniMcp.RuntimeChecks" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new Exception("Cannot start the controlled child.");
    }

    internal static Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null) =>
        WaitAsync(condition, timeout ?? TimeSpan.FromSeconds(10));

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    internal static async Task AtomicWriteAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path + ".tmp", content);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
