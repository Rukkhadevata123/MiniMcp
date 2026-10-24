using System.Net;
using System.Net.Sockets;
using MiniMcp.Configuration;
using MiniMcp.Runtime;

internal static class RuntimeSnapshotChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-snapshots-{Guid.NewGuid():N}");
        var source = Path.Combine(temporary, "settings.json");
        Directory.CreateDirectory(Path.Combine(temporary, "project"));
        var store = new ConfigurationStore(new ConfigurationPaths(Path.Combine(temporary, "user"), Path.Combine(temporary, "state")));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(3) };
        void Assert(bool condition)
        {
            if (!condition) throw new Exception("Runtime snapshot assertion failed.");
        }
        async Task Reject<TException>(Func<Task> action) where TException : Exception
        {
            try { await action(); }
            catch (TException) { return; }
            throw new Exception($"Expected {typeof(TException).Name} from the configured runtime.");
        }
        int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }
        async Task<RuntimeConfiguration> Configuration(int port)
        {
            var settings = new AppSettings
            { ProjectRoot = "./project", Port = port, Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } };
            await File.WriteAllTextAsync(source, ConfigurationJson.SerializeSettings(settings));
            return await store.LoadAsync(source, temporary);
        }
        async Task Healthy(McpRuntime runtime)
        {
            using var response = await client.GetAsync(new Uri(runtime.Snapshot.Address!, "healthz"));
            Assert(response.StatusCode == HttpStatusCode.OK);
        }
        try
        {
            await check("configured startup binds the JSON port and publishes a coherent running snapshot", async () =>
            {
                var configuration = await Configuration(FreePort());
                await using var runtime = new McpRuntime();
                Assert(runtime.Snapshot.State == RuntimeState.Stopped && runtime.Snapshot.RunId is null);
                await runtime.StartAsync(configuration);
                var snapshot = runtime.Snapshot;
                Assert(snapshot.State == RuntimeState.Running && snapshot.RunId is { } id && id != Guid.Empty);
                Assert(ReferenceEquals(snapshot.Configuration, configuration) && snapshot.Error is null);
                Assert(snapshot.Address!.Port == configuration.Settings.Port && snapshot.Address.Host == "127.0.0.1");
                Assert(runtime.Address == snapshot.Address);
                await Healthy(runtime);
                using var mcpGet = await client.GetAsync(new Uri(snapshot.Address, "mcp"));
                Assert(mcpGet.StatusCode == HttpStatusCode.MethodNotAllowed);
            });

            await check("stopped sessions retain their configuration and restart with a new run ID and selected port", async () =>
            {
                var firstConfiguration = await Configuration(FreePort());
                await using var runtime = new McpRuntime();
                await runtime.StartAsync(firstConfiguration);
                var first = runtime.Snapshot;
                await runtime.StopAsync();
                Assert(runtime.Snapshot.State == RuntimeState.Stopped && runtime.Snapshot.Address is null);
                Assert(runtime.Snapshot.RunId == first.RunId && ReferenceEquals(runtime.Snapshot.Configuration, firstConfiguration));
                var secondPort = FreePort();
                var secondConfiguration = await Configuration(secondPort);
                await runtime.StartAsync(secondConfiguration);
                Assert(runtime.Snapshot.RunId != first.RunId && runtime.Snapshot.Address!.Port == secondPort);
                Assert(ReferenceEquals(runtime.Snapshot.Configuration, secondConfiguration));
                Assert(first.State == RuntimeState.Running && first.Address!.Port == firstConfiguration.Settings.Port);
                await Healthy(runtime);
            });

            await check("duplicate configured starts preserve the active session and its configuration", async () =>
            {
                var configuration = await Configuration(FreePort());
                await using var runtime = new McpRuntime();
                await runtime.StartAsync(configuration);
                var active = runtime.Snapshot;
                var other = await Configuration(FreePort());
                await Reject<InvalidOperationException>(() => runtime.StartAsync(other));
                Assert(ReferenceEquals(runtime.Snapshot, active));
                await Healthy(runtime);
            });

            await check("configured port conflicts report faults without affecting the owner and allow a clean retry", async () =>
            {
                await using var owner = new McpRuntime(0);
                await owner.StartAsync(await Configuration(8000));
                var configuration = await Configuration(owner.Address!.Port);
                await using var runtime = new McpRuntime();
                await Reject<IOException>(() => runtime.StartAsync(configuration));
                var failed = runtime.Snapshot;
                Assert(failed.State == RuntimeState.Faulted && failed.Address is null && !string.IsNullOrEmpty(failed.Error));
                Assert(ReferenceEquals(failed.Configuration, configuration));
                await Healthy(owner);
                await owner.StopAsync();
                await runtime.StartAsync(configuration);
                Assert(runtime.Snapshot.State == RuntimeState.Running && runtime.Snapshot.RunId != failed.RunId && runtime.Snapshot.Error is null);
                await Healthy(runtime);
            });

            await check("cancelled operations preserve truthful state and disposal publishes a final snapshot", async () =>
            {
                var configuration = await Configuration(FreePort());
                await using var runtime = new McpRuntime();
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => runtime.StartAsync(configuration, cancelled.Token));
                Assert(runtime.Snapshot.State == RuntimeState.Stopped && runtime.Snapshot.RunId is null);
                await runtime.StartAsync(configuration);
                var active = runtime.Snapshot;
                await Reject<OperationCanceledException>(() => runtime.StopAsync(cancelled.Token));
                Assert(ReferenceEquals(runtime.Snapshot, active));
                await Healthy(runtime);
                await runtime.DisposeAsync();
                var disposed = runtime.Snapshot;
                Assert(disposed.State == RuntimeState.Disposed && disposed.Address is null && disposed.RunId == active.RunId);
                await Reject<ObjectDisposedException>(() => runtime.StartAsync(configuration));
                await runtime.StopAsync();
                await runtime.DisposeAsync();
                Assert(ReferenceEquals(runtime.Snapshot, disposed));
            });
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
