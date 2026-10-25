using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Runtime;

internal sealed class TunnelFixture : IAsyncDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-tunnel-{Guid.NewGuid():N}");
    internal string TokenFile { get; }
    internal RuntimeConfiguration Configuration { get; }
    internal McpRuntime Runtime { get; }
    internal HttpClient Http { get; } = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    private readonly List<Process> _observed = [];

    internal TunnelFixture(string behavior = "wait", TunnelCheckpoints? checks = null,
        bool enabled = true, string? executable = null, bool healthProbe = false)
    {
        Directory.CreateDirectory(DirectoryPath);
        var root = Path.Combine(DirectoryPath, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "sample.txt"), "tunnel fixture");
        TokenFile = Path.Combine(DirectoryPath, "credentials & 中 ($value).token");
        var port = 0;
        if (healthProbe)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        File.WriteAllText(TokenFile, JsonSerializer.Serialize(new TunnelScenario(DirectoryPath, behavior,
            healthProbe ? $"http://127.0.0.1:{port}/healthz" : null)));
        var settingsFile = Path.Combine(DirectoryPath, "settings.json");
        var paths = new ConfigurationPaths(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"));
        Configuration = new ConfigurationValidator(paths).Validate(new AppSettings
        {
            ProjectRoot = root,
            Authentication = enabled
                ? new AuthenticationSettings { Mode = AuthenticationMode.CloudflareAccess, Team = "fixture", Audience = "fixture-aud", PublicOrigin = "https://fixture.invalid" }
                : new AuthenticationSettings { Mode = AuthenticationMode.Local },
            Tunnel = new TunnelSettings
            {
                Enabled = enabled,
                ExecutablePath = executable ?? Path.Combine(AppContext.BaseDirectory, "MiniMcp.RuntimeChecks" + (OperatingSystem.IsWindows() ? ".exe" : "")),
                TokenFilePath = TokenFile
            }
        }, settingsFile, new ExecutionPolicy());
        Runtime = new McpRuntime(port, tunnelChecks: checks);
    }

    internal async Task<TunnelChildReport> ReportAsync()
    {
        var file = Path.Combine(DirectoryPath, "report.json");
        await TunnelChild.WaitUntilAsync(() => File.Exists(file));
        return JsonSerializer.Deserialize<TunnelChildReport>(await File.ReadAllTextAsync(file))!;
    }

    internal async Task<Process> ObserveAsync(int pid)
    {
        var process = Process.GetProcessById(pid);
        _ = process.Handle;
        _observed.Add(process);
        await Task.CompletedTask;
        return process;
    }

    internal async Task<Process> DescendantAsync()
    {
        var file = Path.Combine(DirectoryPath, "descendant.pid");
        await TunnelChild.WaitUntilAsync(() => File.Exists(file));
        return await ObserveAsync(int.Parse(await File.ReadAllTextAsync(file), System.Globalization.CultureInfo.InvariantCulture));
    }

    internal async Task HealthyAsync()
    {
        using var response = await Http.GetAsync(new Uri(Runtime.Address!, "healthz"));
        if (response.StatusCode != HttpStatusCode.OK) throw new Exception("MCP health endpoint is unavailable.");
        if ((await Runtime.Execution!.ReadFileAsync("sample.txt")).Content != "tunnel fixture") throw new Exception("The MCP session is unusable.");
    }

    internal RuntimeConfiguration WithExecutable(string executable)
    {
        var paths = new ConfigurationPaths(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"));
        return new ConfigurationValidator(paths).Validate(Configuration.Settings with
        {
            Tunnel = Configuration.Settings.Tunnel with { ExecutablePath = executable }
        }, Configuration.SettingsFilePath, Configuration.Policy);
    }

    internal RuntimeConfiguration FullAccess()
    {
        var paths = new ConfigurationPaths(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"));
        return new ConfigurationValidator(paths).Validate(Configuration.Settings, Configuration.SettingsFilePath,
            Configuration.Policy with { Mode = ExecutionMode.FullAccess });
    }

    internal void TriggerExit() => File.WriteAllText(Path.Combine(DirectoryPath, "exit"), "exit");

    internal void Reset(string behavior = "wait")
    {
        foreach (var name in new[] { "exit", "report.json", "flood.done", "descendant.pid" })
        {
            var path = Path.Combine(DirectoryPath, name);
            if (File.Exists(path)) File.Delete(path);
        }
        File.WriteAllText(TokenFile, JsonSerializer.Serialize(new TunnelScenario(DirectoryPath, behavior)));
    }

    public async ValueTask DisposeAsync()
    {
        try { await Runtime.DisposeAsync(); }
        finally
        {
            foreach (var process in _observed)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            Http.Dispose();
            var target = Path.GetFullPath(DirectoryPath);
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).StartsWith("minimcp-tunnel-", StringComparison.Ordinal))
                throw new Exception("Unsafe tunnel fixture cleanup path.");
            // Image mappings/other Windows readers can briefly retain a terminated executable.
            // Retry cleanup for a bounded time; never suppress a persistent failure.
            var cleanup = Stopwatch.StartNew();
            while (true)
            {
                try { Directory.Delete(target, recursive: true); break; }
                catch (IOException) when (cleanup.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(50); }
                catch (UnauthorizedAccessException) when (cleanup.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(50); }
            }
        }
    }
}
