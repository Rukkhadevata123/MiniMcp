using Microsoft.Extensions.Logging;
using MiniMcp.Desktop.ViewModels;

namespace MiniMcp.DesktopChecks;

internal static class MonitorChecks
{
    internal static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("monitor: one subscription per stream captures actual lifecycle and Error logs do not become runtime faults", async () =>
        {
            await using var fixture = new MonitorFixture(); var dispatch = new ManualDispatcher();
            await using var monitor = new RuntimeMonitor(fixture.Runtime, dispatch.Post);
            Assert(fixture.Runtime.DiagnosticSubscriberCounts == (1, 1), "One pair of subscriptions");
            await fixture.Start(); await dispatch.Until(() => monitor.Snapshot.State == RuntimeState.Running);
            fixture.Logger!.LogError("display-error-only");
            await dispatch.Until(() => monitor.Rows.Any(row => row.Entry.Message == "display-error-only"));
            Assert(monitor.Snapshot.FaultCount == 0 && fixture.Runtime.Snapshot.State == RuntimeState.Running, "Log severity is not state");
            await fixture.Runtime.StopAsync(); await dispatch.Until(() => monitor.Snapshot.State == RuntimeState.Stopped);
        });
        await check("monitor: blocked log display has bounded callbacks/gaps and cannot hold state or actual stop", async () =>
        {
            await using var fixture = new MonitorFixture(); var states = new ManualDispatcher(); var logs = new ManualDispatcher();
            await using var monitor = new RuntimeMonitor(fixture.Runtime, states.Post, logs.Post);
            await fixture.Start(); await states.Until(() => monitor.Snapshot.State == RuntimeState.Running);
            for (var index = 0; index < 4000; index++) fixture.Logger!.LogInformation("flood {Index} {Text}", index, new string('x', 2048));
            await Task.Delay(60);
            Assert(logs.Pending <= 1 && states.Pending <= 1, "At most one queued callback per stream");
            await fixture.Runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));
            await states.Until(() => monitor.Snapshot.State == RuntimeState.Stopped);
            Assert(logs.Pending <= 1, "State did not require draining logs");
            await logs.Until(() => monitor.OmittedCount > 0 && monitor.RetainedCount > 0);
            Assert(monitor.RetainedCount <= RuntimeMonitor.MaxRows && monitor.CharacterCount <= RuntimeMonitor.MaxCharacters, "Count and total characters bounded");
        });
        await check("monitor: search/level/run filters retain references and rescope after a real restart", async () =>
        {
            await using var fixture = new MonitorFixture(); var dispatch = new ManualDispatcher();
            await using var monitor = new RuntimeMonitor(fixture.Runtime, dispatch.Post);
            await fixture.Start(); await dispatch.Until(() => monitor.Snapshot.State == RuntimeState.Running);
            fixture.Logger!.LogInformation("needle info"); fixture.Logger.LogWarning("Needle warning"); fixture.Logger.LogError("needle error");
            await dispatch.Until(() => monitor.Rows.Any(row => row.Entry.Message == "needle error"));
            var kept = monitor.Rows.Single(row => row.Entry.Message == "Needle warning");
            monitor.SearchText = "NEEDLE"; monitor.MinimumLevelIndex = 2;
            Assert(monitor.Rows.Count == 2 && monitor.Rows.Contains(kept), "Literal case-insensitive search and warning threshold");
            monitor.MinimumLevelIndex = 0; monitor.CurrentRunOnly = true;
            var first = fixture.Runtime.Snapshot.RunId;
            var next = await fixture.Settings.Editor.ReadSavedForStartupAsync(); await fixture.Runtime.RestartAsync(next.Configuration); await fixture.Capture();
            fixture.Logger!.LogInformation("needle next");
            await dispatch.Until(() => monitor.Snapshot.RunId != first && monitor.Rows.Any(row => row.Entry.Message == "needle next"));
            Assert(monitor.Rows.All(row => row.Entry.RunId == monitor.Snapshot.RunId), "Current run filter follows authoritative identity");
            monitor.CurrentRunOnly = false; Assert(monitor.Rows.Contains(kept), "Old retained runs remain available");
        });
        await check("monitor: rendered/selected/filtered/exported logs use sanitized escaped data and atomic cancellation", async () =>
        {
            await using var fixture = new MonitorFixture(); var dispatch = new ManualDispatcher();
            await using var monitor = new RuntimeMonitor(fixture.Runtime, dispatch.Post);
            await fixture.Start();
            fixture.Logger!.LogInformation("Authorization: Bearer fixture-private-secret");
            fixture.Logger.LogWarning("safe-line\t\u001b[31m\nnext");
            await dispatch.Until(() => monitor.Rows.Any(row => row.Entry.Message.Contains("safe-line")));
            var all = monitor.FilteredText();
            Assert(!all.Contains("fixture-private-secret") && all.Contains("[REDACTED]"), "Runtime redaction retained");
            Assert(all.Contains("\\t") && all.Contains("\\u001b", StringComparison.OrdinalIgnoreCase) && !all.Contains('\u001b'), "Controls visibly escaped");
            monitor.SearchText = "safe-line";
            var selected = monitor.SelectedText(monitor.Rows.Take(1));
            Assert(selected == monitor.FilteredText(), "Copy follows visible selection/filter");
            var path = Path.Combine(fixture.Settings.DirectoryPath, "logs.txt");
            await LogExport.WriteAsync(path, selected);
            Assert(await File.ReadAllTextAsync(path) == selected, "Actual UTF-8 export");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await LogExport.WriteAsync(path, "replacement", cancelled.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            Assert(await File.ReadAllTextAsync(path) == selected && !Directory.EnumerateFiles(fixture.Settings.DirectoryPath, ".minimcp-log-*.tmp").Any(), "Cancelled export keeps existing file and cleans staging");
        });
        await check("monitor: atomic clear watermark prevents queued old entries from reappearing", async () =>
        {
            await using var fixture = new MonitorFixture(); var dispatch = new ManualDispatcher();
            await using var monitor = new RuntimeMonitor(fixture.Runtime, dispatch.Post);
            await fixture.Start(); fixture.Logger!.LogInformation("before-clear");
            await Task.Delay(40); monitor.Clear(); fixture.Logger.LogInformation("after-clear");
            await dispatch.Until(() => monitor.Rows.Any(row => row.Entry.Message == "after-clear"));
            Assert(!monitor.Rows.Any(row => row.Entry.Message == "before-clear"), "Old private queue filtered through the clear sequence");
        });
        await check("monitor: disposal with unprocessed callbacks unsubscribes and late callbacks cannot mutate the view", async () =>
        {
            await using var fixture = new MonitorFixture(); var dispatch = new ManualDispatcher();
            var monitor = new RuntimeMonitor(fixture.Runtime, dispatch.Post);
            await fixture.Start(); fixture.Logger!.LogInformation("queued-before-close"); await Task.Delay(40);
            var count = monitor.RetainedCount;
            await monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert(fixture.Runtime.DiagnosticSubscriberCounts == (0, 0), "Both subscriptions released");
            dispatch.Drain(); Assert(monitor.RetainedCount == count && fixture.Runtime.Snapshot.State == RuntimeState.Running, "Late UI work ignored without stopping Runtime");
        });
        await check("monitor: a rejected dispatcher stops presentation safely without faulting the service", async () =>
        {
            await using var fixture = new MonitorFixture();
            await using var monitor = new RuntimeMonitor(fixture.Runtime, _ => false);
            await fixture.Start(); fixture.Logger!.LogInformation("no-dispatcher"); await Task.Delay(60);
            Assert(monitor.ObservationError.Length > 0 && fixture.Runtime.Snapshot.State == RuntimeState.Running && fixture.Runtime.DiagnosticSubscriberCounts == (0, 0), "Display errors stay separate from lifecycle and subscriptions close");
        });
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
