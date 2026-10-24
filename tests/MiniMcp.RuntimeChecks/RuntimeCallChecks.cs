using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MiniMcp.Configuration;
using MiniMcp.Runtime;
using MiniMcp.Services;
using MiniMcp.Tools;

internal static class RuntimeCallChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-calls-{Guid.NewGuid():N}");
        var root = Path.Combine(temporary, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "sample.txt"), "original\n");
        var bash = ExternalProgramLocator.FindBash(Environment.GetEnvironmentVariable("MINIMCP_TEST_BASH_PATH") ?? "",
            Path.Combine(Environment.CurrentDirectory, "settings.json")) ?? throw new Exception("Bash is required for runtime call checks.");
        var paths = new ConfigurationPaths(Path.Combine(temporary, "user"), Path.Combine(temporary, "state"));
        RuntimeConfiguration Configure(ExecutionMode mode) => new ConfigurationValidator(paths).Validate(new AppSettings
        {
            ProjectRoot = root, BashPath = bash,
            Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local }
        }, Path.Combine(temporary, "settings.json"), new ExecutionPolicy { Mode = mode });
        var full = Configure(ExecutionMode.FullAccess);
        var readOnly = Configure(ExecutionMode.ReadOnly);
        try
        {
            await check("client cancellation drains a real Bash descendant and releases call and mutation tracking", async () =>
            {
                await using var runtime = new McpRuntime(0);
                await runtime.StartAsync(full);
                var execution = runtime.Execution!;
                using var client = new CancellationTokenSource();
                var marker = Path.Combine(root, "client-child.pid");
                var running = new WorkspaceTools(execution).Bash(ChildCommand(marker), client.Token);
                using var child = await ObserveChildAsync(marker, running);
                try
                {
                    Assert(execution.ActiveCalls == 1);
                    await client.CancelAsync();
                    await Reject<OperationCanceledException>(() => running).WaitAsync(TimeSpan.FromSeconds(10));
                    await AssertStoppedAsync(child);
                    Assert(execution.ActiveCalls == 0 && !execution.IsDisposed);
                    Assert((await execution.RunBashAsync("exit 0")).ExitCode == 0);
                    await Reject<WorkspaceException>(() => execution.ApplyPatchAsync("malformed", dryRun: true));
                    Assert(execution.ActiveCalls == 0 && (await execution.ReadFileAsync("sample.txt")).Content == "original\n");
                }
                finally { client.Cancel(); KillIfRunning(child); }
            });

            await check("runtime stop cancels running Bash and queued previews before disposing or restarting", async () =>
            {
                await using var runtime = new McpRuntime(0);
                await runtime.StartAsync(full);
                var execution = runtime.Execution!;
                var port = runtime.Address!.Port;
                var marker = Path.Combine(root, "stop-child.pid");
                var running = execution.RunBashAsync(ChildCommand(marker));
                using var child = await ObserveChildAsync(marker, running);
                try
                {
                    var preview = execution.ApplyPatchAsync("*** Begin Patch\n*** Add File: preview/new.txt\n+new\n*** End Patch", dryRun: true);
                    Assert(execution.ActiveCalls == 2 && !preview.IsCompleted);
                    var stopping = runtime.StopAsync();
                    await Reject<OperationCanceledException>(() => running).WaitAsync(TimeSpan.FromSeconds(10));
                    await Reject<OperationCanceledException>(() => preview).WaitAsync(TimeSpan.FromSeconds(10));
                    await stopping.WaitAsync(TimeSpan.FromSeconds(10));
                    await AssertStoppedAsync(child);
                    Assert(execution.ActiveCalls == 0 && execution.IsDisposed && runtime.Execution is null);
                    Assert(!Directory.Exists(Path.Combine(root, "preview")) && !Directory.Exists(paths.RecoveryDirectory));
                    await using (var replacement = new McpRuntime(port)) await replacement.StartAsync(readOnly);

                    await runtime.StartAsync(readOnly);
                    var current = runtime.Snapshot;
                    Assert(current.RunId != execution.RunId && runtime.Execution!.RunId == current.RunId);
                    await execution.StopAsync();
                    await Reject<ObjectDisposedException>(() => execution.ReadFileAsync("sample.txt"));
                    Assert(ReferenceEquals(current, runtime.Snapshot) && (await runtime.Execution!.ReadFileAsync("sample.txt")).Content == "original\n");
                }
                finally { KillIfRunning(child); }
            });

            await check("shutdown deadline bounds blocking host stop and retries share its unfinished task", () =>
                CheckBlockedCleanupAsync(readOnly, blockStop: true));
            await check("cancelled shutdown retains one unfinished host disposal and truthful session ownership", () =>
                CheckBlockedCleanupAsync(readOnly, blockStop: false));
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static async Task CheckBlockedCleanupAsync(RuntimeConfiguration configuration, bool blockStop)
    {
        var probes = new List<GatedLifecycleService>();
        await using var runtime = new McpRuntime(0, services => services.AddSingleton<IHostedService>(_ =>
        {
            var probe = new GatedLifecycleService(probes.Count == 0 && blockStop, probes.Count == 0 && !blockStop);
            probes.Add(probe);
            return probe;
        }));
        await runtime.StartAsync(configuration);
        var execution = runtime.Execution!;
        var gate = probes[0];
        using var cancel = new CancellationTokenSource();
        var stopping = runtime.StopAsync(cancel.Token);
        try
        {
            await (blockStop ? gate.StopEntered.Task : gate.DisposeEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert(runtime.Snapshot.State == RuntimeState.Stopping && execution.ActiveCalls == 0 && !execution.IsDisposed);
            Assert((await Reject<WorkspaceException>(() => execution.ReadFileAsync("sample.txt"))).Message.Contains("stopping"));
            await Reject<WorkspaceException>(() => execution.ApplyPatchAsync("invalid", dryRun: true));
            await Reject<WorkspaceException>(() => execution.RunBashAsync("exit 0"));
            if (!blockStop) cancel.Cancel(); // The stop case exercises the actual ten-second runtime deadline.
            await Reject<OperationCanceledException>(() => stopping).WaitAsync(TimeSpan.FromSeconds(15));
            Assert(runtime.Snapshot.State == RuntimeState.Faulted && ReferenceEquals(runtime.Execution, execution));
            await Reject<InvalidOperationException>(() => runtime.StartAsync(configuration));

            using (var retryDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                await Reject<OperationCanceledException>(() => runtime.StopAsync(retryDeadline.Token));
            Assert(gate.StopCalls == 1 && gate.DisposeCalls == (blockStop ? 0 : 1));
            var failed = runtime.Snapshot;
            gate.Release();
            await (blockStop ? gate.StopFinished.Task : gate.DisposeFinished.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert(ReferenceEquals(runtime.Snapshot, failed));
            // The old host stop can finish cancelled after its original deadline; retry it once it has settled.
            for (var attempt = 0; ; attempt++)
            {
                try { await runtime.StopAsync(); break; }
                catch (OperationCanceledException) when (attempt == 0) { }
            }
            Assert(runtime.Snapshot.State == RuntimeState.Stopped && runtime.Execution is null && execution.IsDisposed);
            Assert(gate.DisposeCalls == 1);
            await runtime.StartAsync(configuration);
            var current = runtime.Snapshot;
            Assert(current.RunId != execution.RunId && runtime.Execution!.RunId == current.RunId);
            await execution.StopAsync();
            Assert(ReferenceEquals(runtime.Snapshot, current));
        }
        finally
        {
            gate.Release();
            try { await stopping; } catch (OperationCanceledException) { }
        }
    }

    internal static string ChildCommand(string marker)
    {
        static string Quote(string value) => "'" + value.Replace('\\', '/').Replace("'", "'\\''") + "'";
        var executable = Environment.ProcessPath ?? throw new Exception("Cannot locate the check executable.");
        var assembly = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? " " + Quote(typeof(RuntimeCallChecks).Assembly.Location) : "";
        return Quote(executable) + assembly + " --execution-child " + Quote(marker) + " & wait";
    }

    internal static async Task<Process> ObserveChildAsync(string marker, Task running)
    {
        for (var i = 0; i < 500; i++)
        {
            if (File.Exists(marker))
            {
                var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture));
                if (child.HasExited) { child.Dispose(); throw new Exception("Observed child exited before cancellation."); }
                return child;
            }
            if (running.IsCompleted) { await running; throw new Exception("Bash completed before the child marker was written."); }
            await Task.Delay(10);
        }
        throw new Exception("Timed out observing the actual Bash descendant.");
    }

    internal static async Task AssertStoppedAsync(Process child)
    {
        for (var i = 0; i < 150; i++)
        {
            var stat = $"/proc/{child.Id}/stat";
            if (child.HasExited || (OperatingSystem.IsLinux() && File.Exists(stat) && File.ReadAllText(stat).Contains(") Z "))) return;
            await Task.Delay(20);
        }
        throw new Exception($"Bash descendant {child.Id} is still running.");
    }

    internal static void KillIfRunning(Process child)
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static async Task<TException> Reject<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException error) { return error; }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new Exception("Runtime call lifecycle assertion failed.");
    }

    private sealed class GatedLifecycleService(bool blockStop, bool blockDispose) : IHostedService, IAsyncDisposable
    {
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCalls;
        public int DisposeCalls;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StopCalls);
            StopEntered.TrySetResult();
            if (blockStop) _released.Task.GetAwaiter().GetResult(); // Deliberately ignores cancellation and blocks synchronously.
            StopFinished.TrySetResult();
            return Task.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            DisposeEntered.TrySetResult();
            if (blockDispose) await _released.Task;
            DisposeFinished.TrySetResult();
        }
        public void Release() => _released.TrySetResult();
    }
}
