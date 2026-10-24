using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MiniMcp.Configuration;
using MiniMcp.Runtime;
using MiniMcp.Services;

internal static class RuntimeSessionChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-sessions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        var paths = new ConfigurationPaths(Path.Combine(temporary, "user"), Path.Combine(temporary, "state"));
        var bash = Path.Combine(temporary, "bash-fixture");
        File.WriteAllText(bash, "Dependency fixture; this file must never be executed.");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(bash, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        RuntimeConfiguration Configure(string name, ExecutionMode mode = ExecutionMode.ReadOnly)
        {
            var root = Path.Combine(temporary, name);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "sample.txt"), name);
            return new ConfigurationValidator(paths).Validate(new AppSettings
            {
                ProjectRoot = root, BashPath = mode == ExecutionMode.ReadOnly ? "" : bash,
                Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local }
            }, Path.Combine(temporary, "settings.json"), new ExecutionPolicy { Mode = mode });
        }
        var firstConfiguration = Configure("first");
        var secondConfiguration = Configure("second", ExecutionMode.ProjectEdit);
        try
        {
            await check("runtime sessions isolate projects, policies and IDs and dispose on restart", async () =>
            {
                await using var first = new McpRuntime(0);
                await using var second = new McpRuntime(0);
                await first.StartAsync(firstConfiguration);
                await second.StartAsync(secondConfiguration);
                var a = first.Execution!;
                var b = second.Execution!;
                Assert(!ReferenceEquals(a, b) && a.RunId != b.RunId);
                Assert(a.RunId == first.Snapshot.RunId && b.RunId == second.Snapshot.RunId);
                Assert(ReferenceEquals(a.Configuration, firstConfiguration) && ReferenceEquals(b.Configuration, secondConfiguration));
                Assert((await a.ReadFileAsync("sample.txt")).Content == "first");
                Assert((await b.ReadFileAsync("sample.txt")).Content == "second");
                Assert((await Reject<WorkspaceException>(() => a.RunBashAsync("exit 0"))).Message.Contains("ReadOnly"));
                Assert((await Reject<WorkspaceException>(() => b.RunBashAsync("exit 0"))).Message.Contains("local approval"));
                var active = first.Snapshot;
                await Reject<InvalidOperationException>(() => first.StartAsync(secondConfiguration));
                Assert(ReferenceEquals(first.Snapshot, active) && ReferenceEquals(first.Execution, a) && !a.IsDisposed);

                await first.StopAsync();
                Assert(first.Execution is null && a.IsDisposed && !b.IsDisposed);
                await Reject<ObjectDisposedException>(() => a.ReadFileAsync("sample.txt"));
                await Reject<ObjectDisposedException>(() => a.ApplyPatchAsync("invalid", dryRun: true));
                await Reject<ObjectDisposedException>(() => a.RunBashAsync("exit 0"));
                await first.StartAsync(secondConfiguration);
                Assert(first.Execution!.RunId == first.Snapshot.RunId && first.Execution.RunId != a.RunId);
                Assert(!ReferenceEquals(first.Execution, a) && (await first.Execution.ReadFileAsync("sample.txt")).Content == "second");
                Assert((await b.ReadFileAsync("sample.txt")).Content == "second");
            });

            await check("shared registration keeps one owned execution service across request scopes", async () =>
            {
                var runId = Guid.NewGuid();
                var services = new ServiceCollection();
                services.AddToolExecution(runId, firstConfiguration);
                using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                using var first = provider.CreateScope();
                using var second = provider.CreateScope();
                var execution = first.ServiceProvider.GetRequiredService<ToolExecutionService>();
                Assert(ReferenceEquals(execution, second.ServiceProvider.GetRequiredService<ToolExecutionService>()));
                Assert(ReferenceEquals(execution, provider.GetRequiredService<ToolExecutionService>()));
                Assert(execution.RunId == runId && ReferenceEquals(execution.Configuration, firstConfiguration));
                provider.Dispose();
                Assert(execution.IsDisposed);
                await Reject<ObjectDisposedException>(() => execution.ReadFileAsync("sample.txt"));
            });

            await check("DI build failure leaves no owned session and permits a fresh startup", async () =>
            {
                var failBuild = true;
                await using var runtime = new McpRuntime(0, services =>
                {
                    if (failBuild) services.AddSingleton<UnconstructableService>();
                });
                await Reject<ArgumentException>(() => runtime.StartAsync(firstConfiguration));
                var failed = runtime.Snapshot;
                Assert(failed.State == RuntimeState.Faulted && failed.Address is null && runtime.Execution is null);
                failBuild = false;
                await runtime.StartAsync(firstConfiguration);
                Assert(runtime.Execution!.RunId == runtime.Snapshot.RunId && runtime.Execution.RunId != failed.RunId);
            });

            await check("workspace initialization fails before listening and releases the host for retry", async () =>
            {
                var configuration = Configure("removed");
                Directory.Delete(configuration.Settings.ProjectRoot, recursive: true);
                await using var runtime = new McpRuntime(0);
                await Reject<InvalidOperationException>(() => runtime.StartAsync(configuration));
                Assert(runtime.Snapshot.State == RuntimeState.Faulted && runtime.Address is null && runtime.Execution is null);
                Directory.CreateDirectory(configuration.Settings.ProjectRoot);
                File.WriteAllText(Path.Combine(configuration.Settings.ProjectRoot, "sample.txt"), "restored");
                await runtime.StartAsync(configuration);
                Assert((await runtime.Execution!.ReadFileAsync("sample.txt")).Content == "restored");
            });

            await check("host startup failure disposes its initialized session before retry", async () =>
            {
                var failStart = true;
                var probes = new List<LifecycleProbe>();
                await using var runtime = new McpRuntime(0, services => services.AddSingleton<IHostedService>(provider =>
                {
                    var probe = new LifecycleProbe(provider.GetRequiredService<ToolExecutionService>()) { FailStart = failStart };
                    probes.Add(probe);
                    return probe;
                }));
                await Reject<InvalidOperationException>(() => runtime.StartAsync(firstConfiguration));
                Assert(probes.Count == 1 && probes[0].Execution.IsDisposed && runtime.Execution is null);
                Assert(runtime.Snapshot.State == RuntimeState.Faulted && probes[0].Execution.RunId == runtime.Snapshot.RunId);
                failStart = false;
                await runtime.StartAsync(firstConfiguration);
                Assert(probes.Count == 2 && ReferenceEquals(probes[1].Execution, runtime.Execution));
                Assert(runtime.Execution!.RunId != probes[0].Execution.RunId && !runtime.Execution.IsDisposed);
            });

            await check("port binding failure disposes only the contender session", async () =>
            {
                await using var owner = new McpRuntime(0);
                await owner.StartAsync(firstConfiguration);
                var probes = new List<LifecycleProbe>();
                await using var contender = new McpRuntime(owner.Address!.Port, services => services.AddSingleton<IHostedService>(provider =>
                {
                    var probe = new LifecycleProbe(provider.GetRequiredService<ToolExecutionService>());
                    probes.Add(probe);
                    return probe;
                }));
                await Reject<IOException>(() => contender.StartAsync(secondConfiguration));
                Assert(probes.Count == 1 && probes[0].Execution.IsDisposed && contender.Execution is null);
                Assert(!owner.Execution!.IsDisposed && (await owner.Execution.ReadFileAsync("sample.txt")).Content == "first");
                await owner.StopAsync();
                await contender.StartAsync(secondConfiguration);
                Assert(probes.Count == 2 && ReferenceEquals(contender.Execution, probes[1].Execution));
                Assert(probes[0].Execution.RunId != contender.Execution!.RunId);
            });

            await check("failed shutdown retains session ownership until cleanup succeeds", async () =>
            {
                LifecycleProbe? probe = null;
                await using var runtime = new McpRuntime(0, services => services.AddSingleton<IHostedService>(provider =>
                    probe = new LifecycleProbe(provider.GetRequiredService<ToolExecutionService>())));
                await runtime.StartAsync(firstConfiguration);
                var execution = runtime.Execution!;
                probe!.FailStop = true;
                var error = await Reject<Exception>(() => runtime.StopAsync());
                Assert(error.Message.Contains("fixture stop failure"));
                var failed = runtime.Snapshot;
                Assert(failed.State == RuntimeState.Faulted && ReferenceEquals(runtime.Execution, execution) && !execution.IsDisposed);
                await Reject<InvalidOperationException>(() => runtime.StartAsync(secondConfiguration));
                Assert(ReferenceEquals(runtime.Snapshot, failed));
                probe.FailStop = false;
                await runtime.StopAsync();
                Assert(runtime.Execution is null && execution.IsDisposed && runtime.Snapshot.State == RuntimeState.Stopped);
                await runtime.StartAsync(secondConfiguration);
                Assert(runtime.Execution!.RunId != execution.RunId && runtime.Execution.RunId == runtime.Snapshot.RunId);
            });
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new Exception("Runtime session assertion failed.");
    }

    private static async Task<TException> Reject<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException error) { return error; }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private abstract class UnconstructableService { }

    private sealed class LifecycleProbe(ToolExecutionService execution) : IHostedService
    {
        public ToolExecutionService Execution { get; } = execution;
        public bool FailStart { get; init; }
        public bool FailStop { get; set; }
        public Task StartAsync(CancellationToken cancellationToken) => FailStart
            ? Task.FromException(new InvalidOperationException("fixture startup failure")) : Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => FailStop
            ? Task.FromException(new InvalidOperationException("fixture stop failure")) : Task.CompletedTask;
    }
}
