using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MiniMcp.Cli;
using MiniMcp.Configuration;
using MiniMcp.Runtime;
using MiniMcp.Services;

internal sealed record CliProcessScenario(string Directory, string Behavior);

internal static class CliProcessChecks
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var signalNames = OperatingSystem.IsWindows() ? new[] { "Ctrl+C" } : new[] { "SIGINT", "SIGTERM" };
        foreach (var signal in signalNames)
        {
            await check($"CLI process: real {signal} drains active Bash, tunnel descendants and its bound port", async () =>
            {
                await using var fixture = new TunnelFixture("descendant");
                CliRunChecks.Save(fixture, fixture.FullAccess());
                using var child = Launch(fixture, "bash");
                using var output = new CliRunChecks.RecordingWriter();
                using var error = new CliRunChecks.RecordingWriter();
                var stdout = Copy(child.StandardOutput, output);
                var stderr = Copy(child.StandardError, error);
                var address = await output.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                using var tunnel = await fixture.ObserveAsync((await fixture.ReportAsync()).ProcessId);
                using var descendant = await fixture.DescendantAsync();
                using var bash = await RuntimeCallChecks.ObserveChildAsync(Path.Combine(fixture.DirectoryPath, "cli-bash.pid"), Task.Delay(Timeout.Infinite));
                try
                {
                    await Send(child.Process, signal);
                    await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                    CliRunChecks.Assert(child.Process.ExitCode == 0 && output.Text.Contains("已停止") && error.Text == "", "Normal real signal exit: " + error.Text);
                    await RuntimeCallChecks.AssertStoppedAsync(tunnel);
                    await RuntimeCallChecks.AssertStoppedAsync(descendant);
                    await RuntimeCallChecks.AssertStoppedAsync(bash);
                    CliRunChecks.AssertReleased(address.Port);
                }
                finally { RuntimeCallChecks.KillIfRunning(bash); }
            });

            await check($"CLI process: repeated real {signal} during cleanup keeps one stop and exits normally", async () =>
            {
                await using var fixture = new TunnelFixture(enabled: false);
                CliRunChecks.Save(fixture);
                using var child = Launch(fixture, "block-stop");
                using var output = new CliRunChecks.RecordingWriter();
                using var error = new CliRunChecks.RecordingWriter();
                var stdout = Copy(child.StandardOutput, output);
                var stderr = Copy(child.StandardError, error);
                var address = await output.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                try
                {
                    await Send(child.Process, signal);
                    await CliRunChecks.Until(() => File.Exists(Path.Combine(fixture.DirectoryPath, "cli-stop.entered")));
                    await Send(child.Process, signal, 3);
                    CliRunChecks.Assert(!child.Process.HasExited, "Repeated signal must not bypass cleanup");
                    File.WriteAllText(Path.Combine(fixture.DirectoryPath, "cli-release"), "release");
                    await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                    CliRunChecks.Assert(child.Process.ExitCode == 0 &&
                        File.ReadAllText(Path.Combine(fixture.DirectoryPath, "cli-stop.calls")) == "1", "One actual stop");
                    CliRunChecks.AssertReleased(address.Port);
                }
                finally { File.WriteAllText(Path.Combine(fixture.DirectoryPath, "cli-release"), "release"); }
            });

            await check($"CLI process: real {signal} during startup returns 130 after cancellation and cleanup", async () =>
            {
                await using var fixture = new TunnelFixture(enabled: false);
                CliRunChecks.Save(fixture);
                using var child = Launch(fixture, "block-start");
                using var output = new CliRunChecks.RecordingWriter();
                using var error = new CliRunChecks.RecordingWriter();
                var stdout = Copy(child.StandardOutput, output);
                var stderr = Copy(child.StandardError, error);
                try
                {
                    await CliRunChecks.Until(() => File.Exists(Path.Combine(fixture.DirectoryPath, "cli-start.entered")), 15);
                    await Send(child.Process, signal);
                    await CliRunChecks.Until(() => File.Exists(Path.Combine(fixture.DirectoryPath, "cli-start.cancelled")));
                    File.WriteAllText(Path.Combine(fixture.DirectoryPath, "cli-release"), "release");
                    await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                    CliRunChecks.Assert(child.Process.ExitCode == 130 && error.Text.Contains("启动已取消") &&
                        !output.Text.Contains("已停止"), "Real cancelled startup: " + error.Text);
                }
                finally { File.WriteAllText(Path.Combine(fixture.DirectoryPath, "cli-release"), "release"); }
            });
        }
    }

    private static ProcessLifetime Launch(TunnelFixture fixture, string behavior)
    {
        var control = Path.Combine(fixture.DirectoryPath, "cli-process.json");
        File.WriteAllText(control, JsonSerializer.Serialize(new CliProcessScenario(fixture.DirectoryPath, behavior)));
        var executable = Path.Combine(AppContext.BaseDirectory, "MiniMcp.RuntimeChecks" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? executable : "/usr/bin/setsid",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false, CreateNoWindow = !OperatingSystem.IsWindows(), WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (OperatingSystem.IsLinux()) { start.ArgumentList.Add("--"); start.ArgumentList.Add(executable); }
        start.ArgumentList.Add("--cli-process");
        start.ArgumentList.Add(control);
        return ProcessLifetime.Start(start);
    }

    private static async Task Copy(StreamReader source, TextWriter writer)
    {
        while (await source.ReadLineAsync() is { } line) writer.WriteLine(line);
    }

    private static async Task Send(Process child, string signal, int count = 1)
    {
        if (OperatingSystem.IsWindows())
        {
            // The sender has no inherited console. It attaches only to the private new console
            // and verifies its entire process list before sending any CTRL_C_EVENT.
            using var sender = TunnelChild.StartSelf("--cli-console-signal", child.Id.ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture));
            await sender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            CliRunChecks.Assert(sender.ExitCode == 0, "Isolated console signal sender failed");
        }
        else
        {
            for (var index = 0; index < count && !child.HasExited; index++)
            {
                if (Kill(child.Id, signal == "SIGINT" ? 2 : 15) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                await Task.Delay(30);
            }
        }
    }

    internal static async Task<int> ChildAsync(string control)
    {
        var scenario = JsonSerializer.Deserialize<CliProcessScenario>(await File.ReadAllTextAsync(control))!;
        if (OperatingSystem.IsWindows())
        {
            if (GetConsoleWindow() == IntPtr.Zero) throw new Exception("The signal check must have its own console.");
            // Simulate a normal terminal even if the test launcher inherited an ignore-Ctrl+C flag.
            if (!SetConsoleCtrlHandler(IntPtr.Zero, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var paths = new ConfigurationPaths(Path.Combine(scenario.Directory, "user"), Path.Combine(scenario.Directory, "state"));
        var store = new ConfigurationStore(paths);
        var settings = Path.Combine(scenario.Directory, "settings.json");
        McpRuntime? runtime = null;
        using var monitoring = new CancellationTokenSource();
        // The private console's OEM code page is unrelated to these redirected UTF-8 pipes.
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var error = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var running = CliEntry.RunAsync(["run", "--config", settings], output, error, store, scenario.Directory, () =>
            runtime = new McpRuntime(0, services =>
            {
                if (scenario.Behavior is "block-start" or "block-stop")
                    services.AddSingleton<IHostedService>(_ => new ProcessGate(scenario));
            }));
        var bash = scenario.Behavior == "bash" ? Task.Run(async () =>
        {
            while (runtime?.Snapshot.State != RuntimeState.Running) await Task.Delay(10, monitoring.Token);
            await runtime.Execution!.RunBashAsync(RuntimeCallChecks.ChildCommand(Path.Combine(scenario.Directory, "cli-bash.pid")));
        }) : Task.CompletedTask;
        try { return await running; }
        finally
        {
            monitoring.Cancel();
            try { await bash; }
            catch (OperationCanceledException) { }
        }
    }

    internal static async Task<int> SignalAsync(int target, int count)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        FreeConsole();
        if (!AttachConsole((uint)target)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!SetConsoleCtrlHandler(IntPtr.Zero, true)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var processes = new uint[32];
            var length = GetConsoleProcessList(processes, (uint)processes.Length);
            if (length == 0 || length > processes.Length || !processes.Take((int)length).Contains((uint)target) ||
                processes.Take((int)length).Any(pid => pid != (uint)target && pid != (uint)Environment.ProcessId))
                throw new Exception("Refusing to signal a console containing an unrelated process.");
            for (var index = 0; index < count; index++)
            {
                if (!GenerateConsoleCtrlEvent(0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                await Task.Delay(30);
            }
            await Task.Delay(100);
            return 0;
        }
        finally { FreeConsole(); }
    }

    private sealed class ProcessGate(CliProcessScenario scenario) : IHostedService
    {
        private int _stops;
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (scenario.Behavior == "block-start")
            {
                using var registration = cancellationToken.Register(() => File.WriteAllText(Path.Combine(scenario.Directory, "cli-start.cancelled"), "cancelled"));
                Block("cli-start.entered");
            }
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            File.WriteAllText(Path.Combine(scenario.Directory, "cli-stop.calls"), Interlocked.Increment(ref _stops).ToString(CultureInfo.InvariantCulture));
            if (scenario.Behavior == "block-stop") Block("cli-stop.entered");
            return Task.CompletedTask;
        }
        private void Block(string marker)
        {
            File.WriteAllText(Path.Combine(scenario.Directory, marker), "entered");
            while (!File.Exists(Path.Combine(scenario.Directory, "cli-release"))) Thread.Sleep(10);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processes, uint capacity);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
}
