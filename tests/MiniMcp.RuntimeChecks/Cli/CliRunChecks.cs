using MiniMcp;
using MiniMcp.Diagnostics;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMcp.Cli;
using MiniMcp.Configuration;

internal static class CliRunChecks
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("CLI run: real ILogger output is bounded, escaped and redacted without deriving failure from log level", async () =>
        {
            ILogger? logger = null;
            await using var fixture = new TunnelFixture(enabled: false, configureServices: services =>
                services.AddSingleton<IHostedService>(provider => new LogProbe(logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("cli-check"))));
            using var output = new RecordingWriter();
            using var error = new RecordingWriter();
            using var stop = new CancellationTokenSource();
            var running = Run(fixture, output, error, stop.Token);
            await output.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var activeLogger = logger!;
            activeLogger.LogInformation("Authorization: Bearer private-cli-value");
            activeLogger.LogInformation("CONTROL\u001b[2J\nTAIL");
            activeLogger.LogInformation("{Long}", new string('x', 6000) + "private-long-tail");
            activeLogger.LogError("EXPECTED-TOOL-ERROR Runtime state: Faulted");
            await Until(() => output.Text.Contains("EXPECTED-TOOL-ERROR"));
            Assert(!running.IsCompleted && fixture.Runtime.Snapshot.FaultCount == 0, "Error log is not a Runtime fault");
            await fixture.HealthyAsync();
            stop.Cancel();
            Assert(await running.WaitAsync(TimeSpan.FromSeconds(12)) == 0, "Normal shutdown");
            Assert(output.Text.Contains("cli-check") && output.Text.Contains("\\u001b[2J\\u000aTAIL") &&
                !output.Text.Contains('\u001b') && !output.Text.Contains("private-cli-value") && !output.Text.Contains("private-long-tail") &&
                output.MaxLineLength <= 24_576 && error.Text == "", "Safe bounded rendering");
            Assert(fixture.Runtime.Snapshot.State == RuntimeState.Disposed && fixture.Runtime.DiagnosticSubscriberCounts == (0, 0), "Subscriptions released");
        });

        await check("CLI run: a blocked display cannot backpressure real tunnel floods and resumes with a dropped-log notice", async () =>
        {
            await using var fixture = new TunnelFixture("flood");
            using var output = new GateWriter();
            using var error = new RecordingWriter();
            using var stop = new CancellationTokenSource();
            var running = Run(fixture, output, error, stop.Token);
            try
            {
                await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Until(() => File.Exists(Path.Combine(fixture.DirectoryPath, "flood.done")), 20);
                await fixture.HealthyAsync();
                Assert(fixture.Runtime.GetLogs().Entries.Length <= 1000, "Producer drained while display is blocked");
                output.Release();
                await output.Recording.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Until(() => output.Recording.Text.Contains("日志已省略"));
                stop.Cancel();
                Assert(await running.WaitAsync(TimeSpan.FromSeconds(12)) == 0 &&
                    !output.Recording.Text.Contains("private-") && error.Text == "", "Resumed display and clean stop");
            }
            finally { output.Release(); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(12)); }
        });

        await check("CLI run: permanently blocked shared output still permits bounded cleanup and releases subscribers", async () =>
        {
            await using var fixture = new TunnelFixture("descendant");
            using var blocked = new GateWriter();
            using var stop = new CancellationTokenSource();
            var running = Run(fixture, blocked, blocked, stop.Token);
            try
            {
                await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using var parent = await fixture.ObserveAsync((await fixture.ReportAsync()).ProcessId);
                using var descendant = await fixture.DescendantAsync();
                var elapsed = Stopwatch.StartNew();
                stop.Cancel();
                Assert(await running.WaitAsync(TimeSpan.FromSeconds(5)) == 1 && elapsed.Elapsed < TimeSpan.FromSeconds(4), "Display timeout does not block shutdown");
                await RuntimeCallChecks.AssertStoppedAsync(parent);
                await RuntimeCallChecks.AssertStoppedAsync(descendant);
                Assert(fixture.Runtime.Snapshot.State == RuntimeState.Disposed &&
                    fixture.Runtime.DiagnosticSubscriberCounts == (0, 0), "Actual cleanup and unsubscribe despite stuck writer");
                blocked.Release();
                await blocked.Exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert(!blocked.Recording.Text.Contains("已停止"), "Abandoned display does not emit a late success");
            }
            finally { blocked.Release(); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(12)); }
        });

        await check("CLI run: a broken output channel cancels and cleans an active Runtime without a user signal", async () =>
        {
            ILogger? logger = null;
            await using var fixture = new TunnelFixture(enabled: false, configureServices: services =>
                services.AddSingleton<IHostedService>(provider => new LogProbe(logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("cli-check"))));
            using var output = new RecordingWriter { FailWhen = value => value.Contains("BREAK-OUTPUT") };
            using var error = new RecordingWriter();
            var running = Run(fixture, output, error, default);
            var address = await output.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            logger!.LogInformation("BREAK-OUTPUT");
            Assert(await running.WaitAsync(TimeSpan.FromSeconds(12)) == 1, "Broken pipe returns failure");
            Assert(fixture.Runtime.Snapshot.State == RuntimeState.Disposed && fixture.Runtime.DiagnosticSubscriberCounts == (0, 0), "Runtime stopped independently of writer");
            AssertReleased(address.Port);
        });

        await check("CLI run: unexpected tunnel exit preserves MCP diagnosis and retains failure through explicit tunnel restart", async () =>
        {
            await using var fixture = new TunnelFixture("exit-descendant");
            using var output = new RecordingWriter();
            using var error = new RecordingWriter();
            using var stop = new CancellationTokenSource();
            var running = Run(fixture, output, error, stop.Token);
            await output.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.ReportAsync();
            using var descendant = await fixture.DescendantAsync();
            fixture.TriggerExit();
            await Until(() => fixture.Runtime.Snapshot.Tunnel.State == TunnelState.Faulted);
            await Until(() => error.Text.Contains("Faulted"));
            Assert(!running.IsCompleted && fixture.Runtime.Snapshot.State == RuntimeState.Running, "CLI retains local diagnostics");
            await fixture.HealthyAsync();
            await RuntimeCallChecks.AssertStoppedAsync(descendant);
            fixture.Reset();
            await fixture.Runtime.StartTunnelAsync();
            Assert(fixture.Runtime.Snapshot.Tunnel.State == TunnelState.Running && fixture.Runtime.Snapshot.FaultCount > 0, "Fault persists independently of latest state");
            stop.Cancel();
            Assert(await running.WaitAsync(TimeSpan.FromSeconds(12)) == 1 && !output.Text.Contains("认证成功"), "Typed fault controls exit code");
        });

        await check("CLI run: one shutdown budget bounds a blocking Host stop without an implicit disposal retry", async () =>
        {
            var gate = new LifecycleGate(stop: true);
            await using var fixture = new TunnelFixture(enabled: false, configureServices: services =>
                services.AddSingleton<IHostedService>(_ => gate));
            using var output = new RecordingWriter();
            using var error = new RecordingWriter();
            using var stop = new CancellationTokenSource();
            var running = Run(fixture, output, error, stop.Token);
            try
            {
                await output.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var elapsed = Stopwatch.StartNew();
                stop.Cancel();
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(await running.WaitAsync(TimeSpan.FromSeconds(13)) == 1 &&
                    elapsed.Elapsed >= TimeSpan.FromSeconds(9) && elapsed.Elapsed < TimeSpan.FromSeconds(13), "One real ten-second budget");
                Assert(gate.StopCalls == 1 && fixture.Runtime.Snapshot is { State: RuntimeState.Faulted, HasOwnedResources: true } &&
                    error.Text.Contains("清理尚未完成") && !output.Text.Contains("已停止"), "Ownership retained; no second cleanup wait or false stop");
            }
            finally
            {
                gate.Release();
                await running.WaitAsync(TimeSpan.FromSeconds(13));
                await RetryStop(fixture.Runtime);
            }
        });

        await check("CLI run: startup ignoring cancellation remains owned and cannot trap the foreground indefinitely", async () =>
        {
            var gate = new LifecycleGate(stop: false);
            await using var fixture = new TunnelFixture(enabled: false, configureServices: services =>
                services.AddSingleton<IHostedService>(_ => gate));
            using var output = new RecordingWriter();
            using var error = new RecordingWriter();
            using var stop = new CancellationTokenSource();
            var running = Run(fixture, output, error, stop.Token);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                stop.Cancel();
                Assert(await running.WaitAsync(TimeSpan.FromSeconds(13)) == 1 &&
                    fixture.Runtime.Snapshot.HasOwnedResources && fixture.Runtime.DiagnosticSubscriberCounts == (0, 0) &&
                    !output.Text.Contains("已停止"), "Pending startup is not disposed or reported stopped");
            }
            finally
            {
                gate.Release();
                await running.WaitAsync(TimeSpan.FromSeconds(13));
                await fixture.Runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
            }
        });

        await check("CLI run: coalesced state and cleanup cannot erase a fault count or let an old process change it", () =>
        {
            var diagnostics = new RuntimeDiagnostics();
            var run = Guid.NewGuid();
            var process = Guid.NewGuid();
            var initial = new RuntimeSnapshot(RuntimeState.Running, run) { Tunnel = new(TunnelState.Running, process, 123) };
            diagnostics.BeginRun(initial);
            using var states = diagnostics.SubscribeState(default);
            Assert(diagnostics.PublishTunnelFault(run, process, 23, "actual exit"), "Actual fault accepted");
            diagnostics.Publish(initial with { State = RuntimeState.Stopped, Tunnel = new(TunnelState.Stopped) });
            Assert(states.Reader.ReadAsync().Result.FaultCount == 1 && diagnostics.Snapshot.FaultCount == 1, "Stale lifecycle copy preserves fault count");
            Assert(!diagnostics.PublishTunnelFault(run, process, 24, "late"), "Stopped process notification rejected");
            diagnostics.BeginRun(new(RuntimeState.Starting, Guid.NewGuid()));
            Assert(diagnostics.Snapshot.FaultCount == 0, "New run resets fault history");
            return Task.CompletedTask;
        });
    }

    internal static ConfigurationStore Save(TunnelFixture fixture, RuntimeConfiguration? selected = null)
    {
        selected ??= fixture.Configuration;
        File.WriteAllText(selected.SettingsFilePath, ConfigurationJson.SerializeSettings(selected.Settings));
        var paths = new ConfigurationPaths(Path.Combine(fixture.DirectoryPath, "user"), Path.Combine(fixture.DirectoryPath, "state"));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PolicyFile)!);
        File.WriteAllText(paths.PolicyFile, ConfigurationJson.SerializePolicy(selected.Policy));
        return new(paths);
    }

    private static Task<int> Run(TunnelFixture fixture, TextWriter output, TextWriter error, CancellationToken stop) =>
        CliApplication.RunAsync(["run", "--config", fixture.Configuration.SettingsFilePath], output, error, stop,
            Save(fixture), fixture.DirectoryPath, () => fixture.Runtime);

    internal static Task Until(Func<bool> condition, int seconds = 10) =>
        TunnelChild.WaitUntilAsync(condition, TimeSpan.FromSeconds(seconds));

    internal static void AssertReleased(int port)
    {
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { ExclusiveAddressUse = true };
        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
    }

    internal static void Assert(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }

    private static async Task RetryStop(McpRuntime runtime)
    {
        try { await runtime.StopAsync(); }
        catch (OperationCanceledException) { await runtime.StopAsync(); }
    }

    private sealed class LogProbe(ILogger logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { logger.LogInformation("real logger ready"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class LifecycleGate(bool stop) : IHostedService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCalls;
        public Task StartAsync(CancellationToken cancellationToken) { if (!stop) Block(); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { Interlocked.Increment(ref StopCalls); if (stop) Block(); return Task.CompletedTask; }
        private void Block() { Entered.TrySetResult(); _release.Task.GetAwaiter().GetResult(); }
        public void Release() => _release.TrySetResult();
    }

    internal sealed class RecordingWriter : TextWriter
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        internal Func<string, bool>? FailWhen { get; init; }
        internal TaskCompletionSource<Uri> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int MaxLineLength { get; private set; }
        public override Encoding Encoding => Encoding.UTF8;
        internal string Text { get { lock (_gate) return _text.ToString(); } }
        public override void WriteLine(string? value)
        {
            value ??= "";
            if (FailWhen?.Invoke(value) == true) throw new IOException("Controlled broken output.");
            lock (_gate)
            {
                MaxLineLength = Math.Max(MaxLineLength, value.Length);
                _text.AppendLine(value);
                if (_text.Length > 512 * 1024) _text.Remove(0, _text.Length - 512 * 1024);
            }
            var index = value.IndexOf("http://127.0.0.1:", StringComparison.Ordinal);
            if (index >= 0 && Uri.TryCreate(value[index..], UriKind.Absolute, out var uri) && uri.AbsolutePath == "/healthz")
                Ready.TrySetResult(uri);
        }
    }

    private sealed class GateWriter : TextWriter
    {
        private readonly ManualResetEventSlim _released = new();
        internal RecordingWriter Recording { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            Entered.TrySetResult();
            _released.Wait();
            Recording.WriteLine(value);
            Exited.TrySetResult();
        }
        internal void Release() => _released.Set();
        protected override void Dispose(bool disposing) { _released.Set(); base.Dispose(disposing); }
    }
}
