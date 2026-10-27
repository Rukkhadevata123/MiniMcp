using MiniMcp;
using MiniMcp.Diagnostics;
using MiniMcp.Services.Processes;
using System.Diagnostics;
using System.Text.Json;

internal static class TunnelChecks
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("tunnel: real child starts after local MCP readiness with exact arguments and hidden redirected IO", async () =>
        {
            var inherited = Environment.GetEnvironmentVariable("TUNNEL_TOKEN");
            Environment.SetEnvironmentVariable("TUNNEL_TOKEN", "private-inherited-value");
            try
            {
                await using var fixture = new TunnelFixture(healthProbe: true);
                using var states = fixture.Runtime.SubscribeState();
                await fixture.Runtime.StartAsync(fixture.Configuration);
                var report = await fixture.ReportAsync();
                using var process = await fixture.ObserveAsync(report.ProcessId);
                Assert(report.Arguments.SequenceEqual(new[] { "tunnel", "--no-autoupdate", "run", "--token-file", fixture.TokenFile }), "Exact argv");
                Assert(report.HostReady && report.Redirected && !report.HasInheritedToken, "Host ready, redirected, explicit session environment");
                if (OperatingSystem.IsWindows()) Assert(report.InJob && !report.HasConsole, "Job ownership and hidden startup");
                var snapshot = fixture.Runtime.Snapshot;
                Assert(snapshot.State == RuntimeState.Running && snapshot.Tunnel is { State: TunnelState.Running, InstanceId: not null } &&
                    snapshot.Tunnel.ProcessId == report.ProcessId, "Actual local process state");
                await fixture.HealthyAsync();
                await fixture.Runtime.StopAsync();
                await RuntimeCallChecks.AssertStoppedAsync(process);
                Assert(fixture.Runtime.Snapshot.Tunnel is { State: TunnelState.Stopped, ProcessId: null }, "Verified stop");
            }
            finally { Environment.SetEnvironmentVariable("TUNNEL_TOKEN", inherited); }
        });

        await check("tunnel: disabled sessions never launch a child and reject independent startup", async () =>
        {
            await using var fixture = new TunnelFixture(enabled: false, executable: "missing-program");
            await fixture.Runtime.StartAsync(fixture.Configuration);
            Assert(fixture.Runtime.Snapshot.Tunnel.State == TunnelState.Disabled, "Disabled state");
            await Reject<InvalidOperationException>(() => fixture.Runtime.StartTunnelAsync());
            await fixture.Runtime.StopTunnelAsync();
            Assert(!File.Exists(Path.Combine(fixture.DirectoryPath, "report.json")), "No process");
            await fixture.HealthyAsync();
        });

        await check("tunnel: real stdout stderr floods and long lines drain while observers remain idle", async () =>
        {
            await using var fixture = new TunnelFixture("flood");
            using var slow = fixture.Runtime.SubscribeLogs();
            await fixture.Runtime.StartAsync(fixture.Configuration);
            await TunnelChild.WaitUntilAsync(() => File.Exists(Path.Combine(fixture.DirectoryPath, "flood.done")), TimeSpan.FromSeconds(20));
            await fixture.HealthyAsync();
            // Stop the producer and drain its redirected output before measuring the
            // queued backlog. Reading concurrently would count newly arriving logs too.
            await fixture.Runtime.StopAsync();
            var retained = fixture.Runtime.GetLogs();
            Assert(retained.Entries.Length <= 1000 && retained.CharacterCount <= 512 * 1024, "Bounded retained output");
            Assert(!retained.Entries.Any(entry => entry.Message.Contains("private-")), "No credential fragments");
            Assert(retained.Entries.Any(entry => entry.Category.StartsWith("MiniMcp.Tunnel.")), "Output routed through diagnostics");
            var queued = new List<RuntimeLogEntry>();
            while (slow.Reader.TryRead(out var entry)) queued.Add(entry);
            Assert(queued.Count <= RuntimeDiagnostics.LogSubscriptionCapacity, "Slow observer bounded");
        });

        await check("tunnel: independent stop and start preserve the MCP session and isolate process attempts", async () =>
        {
            await using var fixture = new TunnelFixture("descendant");
            await fixture.Runtime.StartAsync(fixture.Configuration);
            var first = fixture.Runtime.Snapshot;
            using var process = await fixture.ObserveAsync((await fixture.ReportAsync()).ProcessId);
            using var descendant = await fixture.DescendantAsync();
            await Reject<InvalidOperationException>(() => fixture.Runtime.StartTunnelAsync());
            await fixture.Runtime.StopTunnelAsync();
            await RuntimeCallChecks.AssertStoppedAsync(process);
            await RuntimeCallChecks.AssertStoppedAsync(descendant);
            await fixture.HealthyAsync();
            Assert(fixture.Runtime.Snapshot.RunId == first.RunId && fixture.Runtime.Snapshot.State == RuntimeState.Running, "MCP still owns same session");
            fixture.Reset();
            await fixture.Runtime.StartTunnelAsync();
            var second = fixture.Runtime.Snapshot;
            Assert(second.RunId == first.RunId && second.Tunnel.InstanceId != first.Tunnel.InstanceId &&
                second.Tunnel.ProcessId != first.Tunnel.ProcessId && second.Tunnel.State == TunnelState.Running, "Independent process identity");
            await fixture.Runtime.StopTunnelAsync();
            await fixture.Runtime.StopTunnelAsync();
        });

        await check("tunnel: invalid executable during initial startup cleans the Host and permits a fresh startup", async () =>
        {
            TunnelFixture? fixture = null;
            var waitingForInvalidProgram = true;
            var checks = new TunnelCheckpoints
            {
                AfterStart = async () =>
                {
                    // Linux starts setsid first, so an invalid target executable can fail only
                    // after process creation. Make its exit observable before checking startup.
                    if (!waitingForInvalidProgram || !OperatingSystem.IsLinux()) return;
                    var pid = fixture!.Runtime.Snapshot.Tunnel.ProcessId ?? throw new Exception("Missing tunnel process ID.");
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch (ArgumentException) { } // It may already have been reaped.
                }
            };
            await using var owned = fixture = new TunnelFixture(checks: checks);
            var invalid = Path.Combine(fixture.DirectoryPath, "not a program.exe");
            File.WriteAllText(invalid, "This is not an executable image.");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(invalid, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var configuration = fixture.WithExecutable(invalid);
            await Reject<Exception>(() => fixture.Runtime.StartAsync(configuration));
            Assert(fixture.Runtime.Snapshot is { State: RuntimeState.Faulted, Address: null } && fixture.Runtime.Execution is null &&
                fixture.Runtime.Snapshot.Tunnel.ProcessId is null, "Failed startup releases all ownership");
            waitingForInvalidProgram = false;
            await fixture.Runtime.StartAsync(fixture.Configuration);
            await fixture.ReportAsync();
            await fixture.HealthyAsync();
        });

        await check("tunnel: exit during initial startup fails the whole start and releases its bound port", async () =>
        {
            TunnelFixture? fixture = null;
            Uri? address = null;
            var checks = new TunnelCheckpoints
            {
                AfterStart = async () =>
                {
                    address = fixture!.Runtime.Address;
                    var report = await fixture.ReportAsync();
                    try
                    {
                        using var process = await fixture.ObserveAsync(report.ProcessId);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch (ArgumentException) { } // The real child may have already exited before PID lookup.
                }
            };
            await using var owned = fixture = new TunnelFixture("exit", checks);
            var error = await Reject<IOException>(() => owned.Runtime.StartAsync(owned.Configuration));
            Assert(error.Message.Contains("during startup"), "Real early exit");
            Assert(owned.Runtime.Snapshot.State == RuntimeState.Faulted && owned.Runtime.Address is null && owned.Runtime.Execution is null, "No partial successful startup");
            using var local = new RuntimeLifetimeFixture();
            await using var replacement = new McpRuntime(address!.Port);
            await replacement.StartAsync(local.Configuration);
        });

        await check("tunnel: cancelling startup kills the real child and descendants before releasing the session", async () =>
        {
            TunnelFixture? fixture = null;
            Process? parent = null, descendant = null;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var checks = new TunnelCheckpoints
            {
                AfterStart = async () =>
                {
                    parent = await fixture!.ObserveAsync((await fixture.ReportAsync()).ProcessId);
                    descendant = await fixture.DescendantAsync();
                    entered.TrySetResult();
                    await released.Task;
                }
            };
            await using var owned = fixture = new TunnelFixture("descendant", checks);
            using var cancellation = new CancellationTokenSource();
            var starting = owned.Runtime.StartAsync(owned.Configuration, cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                await Reject<OperationCanceledException>(() => starting);
                await RuntimeCallChecks.AssertStoppedAsync(parent!);
                await RuntimeCallChecks.AssertStoppedAsync(descendant!);
                Assert(owned.Runtime.Snapshot.State == RuntimeState.Stopped && owned.Runtime.Address is null &&
                    owned.Runtime.Execution is null, "Cancellation stops the whole initial session");
            }
            finally { released.TrySetResult(); }
        });

        await check("tunnel: unexpected exit faults only the tunnel, cleans descendants and never restarts automatically", async () =>
        {
            await using var fixture = new TunnelFixture("exit-descendant");
            await fixture.Runtime.StartAsync(fixture.Configuration);
            var original = fixture.Runtime.Snapshot;
            using var process = await fixture.ObserveAsync((await fixture.ReportAsync()).ProcessId);
            using var descendant = await fixture.DescendantAsync();
            fixture.TriggerExit();
            await TunnelChild.WaitUntilAsync(() => fixture.Runtime.Snapshot.Tunnel.State == TunnelState.Faulted);
            Assert(fixture.Runtime.Snapshot is { State: RuntimeState.Running, Address: not null } &&
                fixture.Runtime.Snapshot.Tunnel.ExitCode == 23, "MCP remains actually running");
            await RuntimeCallChecks.AssertStoppedAsync(process);
            await RuntimeCallChecks.AssertStoppedAsync(descendant);
            await fixture.HealthyAsync();
            await Task.Delay(100);
            Assert((await fixture.ReportAsync()).ProcessId == process.Id &&
                fixture.Runtime.Snapshot.Tunnel.InstanceId == original.Tunnel.InstanceId, "No replacement process");
            fixture.Reset();
            await fixture.Runtime.StartTunnelAsync();
            Assert(fixture.Runtime.Snapshot.RunId == original.RunId && fixture.Runtime.Snapshot.Tunnel.InstanceId != original.Tunnel.InstanceId, "Explicit restart only");
        });

        await check("tunnel: RestartAsync fully replaces host, process and session using the supplied saved configuration", async () =>
        {
            await using var first = new TunnelFixture("descendant");
            await using var next = new TunnelFixture();
            await first.Runtime.StartAsync(first.Configuration);
            var previous = first.Runtime.Snapshot;
            var execution = first.Runtime.Execution!;
            using var parent = await first.ObserveAsync((await first.ReportAsync()).ProcessId);
            using var descendant = await first.DescendantAsync();
            await first.Runtime.RestartAsync(next.Configuration);
            await RuntimeCallChecks.AssertStoppedAsync(parent);
            await RuntimeCallChecks.AssertStoppedAsync(descendant);
            var current = first.Runtime.Snapshot;
            Assert(current.State == RuntimeState.Running && current.RunId != previous.RunId &&
                current.Tunnel.InstanceId != previous.Tunnel.InstanceId && ReferenceEquals(current.Configuration, next.Configuration) &&
                execution.IsDisposed, "Fresh supplied configuration/session");
            await next.ReportAsync();
            using var response = await first.Http.GetAsync(new Uri(current.Address!, "healthz"));
            Assert(response.IsSuccessStatusCode, "New Host ready");
        });

        await check("tunnel: program and token paths with spaces and Unicode remain exact executable arguments", async () =>
        {
            await using var fixture = new TunnelFixture();
            var programDirectory = Path.Combine(fixture.DirectoryPath, "program & 中");
            Directory.CreateDirectory(programDirectory);
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                if (new[] { ".dll", ".json", ".exe" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) ||
                    Path.GetFileName(file) == "MiniMcp.RuntimeChecks")
                    File.Copy(file, Path.Combine(programDirectory, Path.GetFileName(file)));
            }
            var source = Path.Combine(programDirectory, "MiniMcp.RuntimeChecks" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            var executable = Path.Combine(programDirectory, "cloudflared check" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            File.Copy(source, executable);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(executable, File.GetUnixFileMode(source));
            await fixture.Runtime.StartAsync(fixture.WithExecutable(executable));
            var report = await fixture.ReportAsync();
            Assert(report.Arguments[^1] == fixture.TokenFile && report.Arguments.Length == 5, "No shell parsing or argument injection");
            await fixture.HealthyAsync();
        });

        await check("tunnel: cancelled and concurrent independent controls preserve one actual process", async () =>
        {
            await using var fixture = new TunnelFixture();
            await fixture.Runtime.StartAsync(fixture.Configuration);
            var previous = fixture.Runtime.Snapshot;
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Reject<OperationCanceledException>(() => fixture.Runtime.StartTunnelAsync(cancelled.Token));
            await Reject<OperationCanceledException>(() => fixture.Runtime.StopTunnelAsync(cancelled.Token));
            await Reject<OperationCanceledException>(() => fixture.Runtime.RestartAsync(fixture.Configuration, cancelled.Token));
            Assert(ReferenceEquals(previous, fixture.Runtime.Snapshot), "Pre-cancelled operations do not mutate ownership");
            await Task.WhenAll(fixture.Runtime.StopTunnelAsync(), fixture.Runtime.StopTunnelAsync());
            fixture.Reset();
            async Task<bool> Start()
            {
                try { await fixture.Runtime.StartTunnelAsync(); return true; }
                catch (InvalidOperationException) { return false; }
            }
            Assert((await Task.WhenAll(Start(), Start())).Count(success => success) == 1, "Only one concurrent start wins");
            var report = await fixture.ReportAsync();
            Assert(fixture.Runtime.Snapshot.Tunnel.ProcessId == report.ProcessId && fixture.Runtime.Snapshot.RunId == previous.RunId, "Single new process in the same MCP run");
        });

        await check("tunnel: manual startup failure leaves MCP available and can be retried explicitly", async () =>
        {
            TunnelFixture? fixture = null;
            var attempts = 0;
            var checks = new TunnelCheckpoints
            {
                AfterStart = async () =>
                {
                    if (Interlocked.Increment(ref attempts) != 2) return;
                    await fixture!.ReportAsync();
                    await TunnelChild.WaitUntilAsync(() => fixture.Runtime.Snapshot.Tunnel.ProcessId is { } pid && Gone(pid));
                }
            };
            await using var owned = fixture = new TunnelFixture(checks: checks);
            await owned.Runtime.StartAsync(owned.Configuration);
            var run = owned.Runtime.Snapshot.RunId;
            await owned.Runtime.StopTunnelAsync();
            owned.Reset("exit");
            await Reject<IOException>(() => owned.Runtime.StartTunnelAsync());
            Assert(owned.Runtime.Snapshot.State == RuntimeState.Running && owned.Runtime.Snapshot.Tunnel is { State: TunnelState.Faulted, ProcessId: null }, "Failure isolated to tunnel");
            await owned.HealthyAsync();
            owned.Reset();
            await owned.Runtime.StartTunnelAsync();
            Assert(owned.Runtime.Snapshot.RunId == run && owned.Runtime.Snapshot.Tunnel.State == TunnelState.Running, "Explicit retry");
        });

        await check("tunnel: runtime stop cancels real Bash and queued patch work while cleaning tunnel descendants", async () =>
        {
            await using var fixture = new TunnelFixture("descendant");
            await fixture.Runtime.StartAsync(fixture.FullAccess());
            var execution = fixture.Runtime.Execution!;
            using var tunnel = await fixture.ObserveAsync((await fixture.ReportAsync()).ProcessId);
            using var descendant = await fixture.DescendantAsync();
            var marker = Path.Combine(fixture.DirectoryPath, "bash-child.pid");
            var bash = execution.RunBashAsync(RuntimeCallChecks.ChildCommand(marker));
            using var bashChild = await RuntimeCallChecks.ObserveChildAsync(marker, bash);
            var preview = execution.ApplyPatchAsync("*** Begin Patch\n*** Add File: queued.txt\n+new\n*** End Patch", dryRun: true);
            await fixture.Runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Reject<OperationCanceledException>(() => bash);
            await Reject<OperationCanceledException>(() => preview);
            await RuntimeCallChecks.AssertStoppedAsync(tunnel);
            await RuntimeCallChecks.AssertStoppedAsync(descendant);
            await RuntimeCallChecks.AssertStoppedAsync(bashChild);
            Assert(execution.IsDisposed && execution.ActiveCalls == 0 && fixture.Runtime.Snapshot.State == RuntimeState.Stopped &&
                !File.Exists(Path.Combine(fixture.Configuration.Settings.ProjectRoot, "queued.txt")), "Whole session drains without queued effects");
        });

        await check("tunnel: ten-second stop deadline retains real cleanup and retries cannot start another session", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var checks = new TunnelCheckpoints
            {
                BeforeRelease = () => { Interlocked.Increment(ref calls); entered.TrySetResult(); return released.Task; }
            };
            await using var fixture = new TunnelFixture("descendant", checks);
            try
            {
                await fixture.Runtime.StartAsync(fixture.Configuration);
                var run = fixture.Runtime.Snapshot.RunId;
                var execution = fixture.Runtime.Execution!;
                using var parent = await fixture.ObserveAsync((await fixture.ReportAsync()).ProcessId);
                using var descendant = await fixture.DescendantAsync();
                var elapsed = Stopwatch.StartNew();
                var stopping = fixture.Runtime.StopAsync();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Reject<OperationCanceledException>(() => stopping);
                Assert(elapsed.Elapsed >= TimeSpan.FromSeconds(9) && elapsed.Elapsed < TimeSpan.FromSeconds(16), "Shared real deadline");
                Assert(fixture.Runtime.Snapshot.State == RuntimeState.Faulted && fixture.Runtime.Execution == execution && !execution.IsDisposed, "Host ownership retained");
                await RuntimeCallChecks.AssertStoppedAsync(parent);
                await RuntimeCallChecks.AssertStoppedAsync(descendant);
                await Reject<InvalidOperationException>(() => fixture.Runtime.StartAsync(fixture.Configuration));
                using (var retry = new CancellationTokenSource(100))
                    await Reject<OperationCanceledException>(() => fixture.Runtime.RestartAsync(fixture.Configuration, retry.Token));
                Assert(calls == 1 && fixture.Runtime.Snapshot.RunId == run, "One unfinished cleanup task, no replacement run");
                released.TrySetResult();
                await fixture.Runtime.StopAsync();
                Assert(execution.IsDisposed && fixture.Runtime.Snapshot.State == RuntimeState.Stopped, "Retry completes actual ownership release");
                fixture.Reset();
                await fixture.Runtime.StartAsync(fixture.Configuration);
                Assert(fixture.Runtime.Snapshot.RunId != run, "New run only after release");
            }
            finally { released.TrySetResult(); }
        });

        await check("tunnel: cancelled independent stop retains process cleanup without closing MCP admission", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var checks = new TunnelCheckpoints
            {
                BeforeRelease = () => { Interlocked.Increment(ref calls); entered.TrySetResult(); return released.Task; }
            };
            await using var fixture = new TunnelFixture(checks: checks);
            try
            {
                await fixture.Runtime.StartAsync(fixture.Configuration);
                using var cancellation = new CancellationTokenSource();
                var stopping = fixture.Runtime.StopTunnelAsync(cancellation.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                await Reject<OperationCanceledException>(() => stopping);
                Assert(fixture.Runtime.Snapshot is { State: RuntimeState.Running, Tunnel.State: TunnelState.Faulted }, "Accurate incomplete independent stop");
                await fixture.HealthyAsync();
                using (var retry = new CancellationTokenSource(100))
                    await Reject<OperationCanceledException>(() => fixture.Runtime.StartTunnelAsync(retry.Token));
                Assert(calls == 1, "No duplicate cleanup");
                released.TrySetResult();
                await fixture.Runtime.StopTunnelAsync();
                Assert(fixture.Runtime.Snapshot.Tunnel.State == TunnelState.Stopped, "Owned cleanup released");
            }
            finally { released.TrySetResult(); }
        });

        await check("tunnel: cleanup failure retains ownership and only explicit retry releases it", async () =>
        {
            var calls = 0;
            var checks = new TunnelCheckpoints
            {
                BeforeRelease = () => Interlocked.Increment(ref calls) == 1
                    ? Task.FromException(new IOException("fixture cleanup failure")) : Task.CompletedTask
            };
            await using var fixture = new TunnelFixture(checks: checks);
            await fixture.Runtime.StartAsync(fixture.Configuration);
            await fixture.ReportAsync();
            await Reject<IOException>(() => fixture.Runtime.StopAsync());
            Assert(fixture.Runtime.Snapshot.State == RuntimeState.Faulted && fixture.Runtime.Execution is not null && calls == 1, "Failed cleanup does not discard ownership");
            await Reject<InvalidOperationException>(() => fixture.Runtime.StartAsync(fixture.Configuration));
            await fixture.Runtime.StopAsync();
            Assert(calls == 2 && fixture.Runtime.Snapshot.State == RuntimeState.Stopped, "Explicit cleanup retry");
        });

        await check("tunnel: a delayed exit notification remains owned through stop and cannot affect a later run", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var checks = new TunnelCheckpoints
            {
                BeforeExitNotification = () => { entered.TrySetResult(); return released.Task; }
            };
            await using var fixture = new TunnelFixture(checks: checks);
            try
            {
                await fixture.Runtime.StartAsync(fixture.Configuration);
                var old = fixture.Runtime.Snapshot;
                await fixture.ReportAsync();
                fixture.TriggerExit();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using (var stop = new CancellationTokenSource(100))
                    await Reject<OperationCanceledException>(() => fixture.Runtime.StopAsync(stop.Token));
                Assert(fixture.Runtime.Snapshot.State == RuntimeState.Faulted && fixture.Runtime.Execution is not null, "Pending observation still owned");
                released.TrySetResult();
                await fixture.Runtime.StopAsync();
                fixture.Reset();
                await fixture.Runtime.StartAsync(fixture.Configuration);
                var next = fixture.Runtime.Snapshot;
                Assert(next.RunId != old.RunId && next.Tunnel.InstanceId != old.Tunnel.InstanceId, "New identities");
                await Task.Delay(100);
                Assert(fixture.Runtime.Snapshot.State == RuntimeState.Running && fixture.Runtime.Snapshot.Tunnel.State == TunnelState.Running, "No stale callback state change");
            }
            finally { released.TrySetResult(); }
        });

        await check("tunnel: fault publication rejects old run and process identities within the same run", () =>
        {
            var diagnostics = new RuntimeDiagnostics();
            var firstRun = Guid.NewGuid();
            var oldProcess = Guid.NewGuid();
            var newProcess = Guid.NewGuid();
            var current = new RuntimeSnapshot(RuntimeState.Running, firstRun) { Tunnel = new(TunnelState.Running, newProcess, 42) };
            diagnostics.BeginRun(current);
            Assert(!diagnostics.PublishTunnelFault(firstRun, oldProcess, 1, "old") &&
                !diagnostics.PublishTunnelFault(Guid.NewGuid(), newProcess, 1, "old") &&
                ReferenceEquals(current, diagnostics.Snapshot), "Old identity cannot overwrite current state");
            Assert(diagnostics.PublishTunnelFault(firstRun, newProcess, 23, "current") &&
                diagnostics.Snapshot.Tunnel is { State: TunnelState.Faulted, ExitCode: 23 }, "Actual process identity can publish fault");
            return Task.CompletedTask;
        });

        await check(OperatingSystem.IsWindows()
            ? "tunnel: killing its owner closes the Windows job and terminates root and descendants"
            : "tunnel: Linux owner SIGKILL exposes the documented surviving process-group boundary", async () =>
        {
            await using var fixture = new TunnelFixture("descendant");
            var ownerFile = Path.Combine(fixture.DirectoryPath, "owner.json");
            File.WriteAllText(ownerFile, JsonSerializer.Serialize(fixture.Configuration.Settings));
            using var owner = TunnelChild.StartSelf("--tunnel-owner", ownerFile);
            try
            {
                await TunnelChild.WaitUntilAsync(() => File.Exists(Path.Combine(fixture.DirectoryPath, "owner.ready")));
                using var parent = await fixture.ObserveAsync((await fixture.ReportAsync()).ProcessId);
                using var descendant = await fixture.DescendantAsync();
                owner.Kill(entireProcessTree: false);
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (OperatingSystem.IsWindows())
                {
                    await RuntimeCallChecks.AssertStoppedAsync(parent);
                    await RuntimeCallChecks.AssertStoppedAsync(descendant);
                }
                else
                {
                    Assert(!parent.HasExited && !descendant.HasExited, "SIGKILL does not run managed Linux cleanup");
                    RuntimeCallChecks.KillIfRunning(parent);
                    RuntimeCallChecks.KillIfRunning(descendant);
                }
            }
            finally { RuntimeCallChecks.KillIfRunning(owner); }
        });
    }

    private static void Assert(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }

    private static bool Gone(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
