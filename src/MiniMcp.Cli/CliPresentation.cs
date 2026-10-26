using MiniMcp.Diagnostics;
using System.Threading.Channels;
using MiniMcp.Configuration;

namespace MiniMcp.Cli;

// One background writer; a blocked TextWriter never owns Runtime or lifecycle control.
internal sealed class CliPresentation
{
    private static readonly Task Never = Task.Delay(Timeout.Infinite);
    private sealed record Notice(string Text, bool Error = false);
    private readonly McpRuntime _runtime;
    private readonly TextWriter _output, _error;
    private readonly CliShutdownBudget _budget;
    private readonly RuntimeSubscription<RuntimeLogEntry> _logs;
    private readonly RuntimeSubscription<RuntimeSnapshot> _states;
    private readonly Channel<Notice> _notices = Channel.CreateBounded<Notice>(new BoundedChannelOptions(32)
    {
        FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false
    });
    private readonly object _stateGate = new();
    private readonly Task _statePump, _writer;
    private RuntimeSnapshot? _lastState;
    private TextWriter? _activeWriter;
    private Notice? _final;
    private int _ready, _hadFault, _finishing, _abandoned;
    private long _lastSequence;

    internal CliPresentation(McpRuntime runtime, TextWriter output, TextWriter error, CliShutdownBudget budget)
    {
        _runtime = runtime; _output = output; _error = error; _budget = budget;
        _logs = runtime.SubscribeLogs();
        _states = runtime.SubscribeState();
        _statePump = Task.Run(PumpStatesAsync);
        _writer = Task.Factory.StartNew(Render, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool HadFault => Volatile.Read(ref _hadFault) != 0;
    internal bool OutputFailed => Failure.Task.IsCompleted;

    internal void Capture(RuntimeSnapshot snapshot)
    {
        lock (_stateGate)
        {
            if (Volatile.Read(ref _finishing) != 0) return;
            if (snapshot.RunId != _runtime.Snapshot.RunId) return;
            var fault = snapshot.FaultCount != 0 || snapshot.State == RuntimeState.Faulted ||
                (snapshot.State == RuntimeState.Running && snapshot.Tunnel.State == TunnelState.Faulted);
            if (fault) Volatile.Write(ref _hadFault, 1);
            if (snapshot.State is RuntimeState.Stopping or RuntimeState.Faulted) _budget.Begin();
            if (Volatile.Read(ref _ready) != 0 && snapshot.State is RuntimeState.Stopped or RuntimeState.Disposed &&
                !_budget.Started.Task.IsCompleted)
            {
                Volatile.Write(ref _hadFault, 1);
                _budget.Begin();
            }
            var previous = _lastState;
            _lastState = snapshot;
            if (snapshot.RunId is null || (previous is not null && previous.State == snapshot.State &&
                previous.Tunnel == snapshot.Tunnel && previous.Error == snapshot.Error)) return;
            var text = $"状态：MCP={snapshot.State}；隧道={snapshot.Tunnel.State}";
            if (snapshot.Tunnel.ProcessId is { } pid) text += $"；进程={pid}";
            var detail = snapshot.Tunnel.Error ?? snapshot.Error;
            if (detail is not null) text += "；" + detail;
            Add(text, fault);
        }
    }

    internal void Ready(RuntimeSnapshot snapshot)
    {
        Volatile.Write(ref _ready, 1);
        Add($"本机健康检查：{new Uri(snapshot.Address!, "healthz").AbsoluteUri}");
        Add($"MCP：{new Uri(snapshot.Address!, "mcp").AbsoluteUri}");
        Add($"认证模式：{snapshot.Configuration!.Settings.Authentication.Mode}");
        Add($"项目：{snapshot.Configuration.Settings.ProjectRoot}");
        Add($"配置执行模式：{snapshot.Configuration.Policy.Mode}");
        if (snapshot.Configuration.Settings.Authentication.Mode == AuthenticationMode.CloudflareAccess)
            Add($"配置公网 MCP：{new Uri(new Uri(snapshot.Configuration.Settings.Authentication.PublicOrigin!), "/mcp").AbsoluteUri}（未验证）");
        if (snapshot.Configuration.Settings.Tunnel.Enabled)
            Add("隧道 Running 仅表示本机进程存活；公网路由、Access 登录和 OAuth 未验证。");
        Add("按 Ctrl+C 停止。");
        Capture(snapshot);
    }

    private void Add(string text, bool error = false) => _notices.Writer.TryWrite(new(CliText.Visible(text), error));

    private async Task PumpStatesAsync()
    {
        try
        {
            foreach (var initial in _states.Initial) Capture(initial);
            await foreach (var state in _states.Reader.ReadAllAsync().ConfigureAwait(false)) Capture(state);
        }
        catch (Exception exception) { Failure.TrySetResult(exception); _budget.Begin(); }
    }

    private void Write(Notice notice)
    {
        var writer = notice.Error ? _error : _output;
        Volatile.Write(ref _activeWriter, writer);
        writer.WriteLine(notice.Text);
        Volatile.Write(ref _activeWriter, null);
    }

    private void Render()
    {
        try
        {
            var initialIndex = 0;
            var tail = 0;
            Task<bool>? logsReady = null, noticesReady = null;
            var logsClosed = false;
            while (Volatile.Read(ref _abandoned) == 0)
            {
                if (_notices.Reader.TryRead(out var notice)) { Write(notice); continue; }
                RuntimeLogEntry? entry = null;
                if (initialIndex < _logs.Initial.Length) entry = _logs.Initial[initialIndex++];
                else if (Volatile.Read(ref _finishing) == 0 || tail < 128) _logs.Reader.TryRead(out entry);
                if (entry is not null)
                {
                    if (Volatile.Read(ref _finishing) != 0) tail++;
                    var previous = _lastSequence;
                    _lastSequence = entry.Sequence;
                    if (entry.RunId != _runtime.Snapshot.RunId) continue;
                    if (entry.Sequence > previous + 1)
                        Write(new($"日志已省略 {entry.Sequence - previous - 1} 条（显示队列已满）。"));
                    Write(new(CliText.Visible($"{entry.Timestamp:HH:mm:ss.fff}Z [{entry.Level}] {entry.Category}: {entry.Message}" +
                        (entry.Truncated ? " [已截断]" : ""))));
                    continue;
                }
                if (Volatile.Read(ref _finishing) != 0)
                {
                    if (_final is { } final) Write(final);
                    Volatile.Write(ref _activeWriter, _output);
                    _output.Flush();
                    Volatile.Write(ref _activeWriter, _error);
                    _error.Flush();
                    Volatile.Write(ref _activeWriter, null);
                    return;
                }
                if (!logsClosed) logsReady ??= _logs.Reader.WaitToReadAsync().AsTask();
                noticesReady ??= _notices.Reader.WaitToReadAsync().AsTask();
                // This is a dedicated background thread, not the UI or lifecycle thread.
                Task.WhenAny((Task?)logsReady ?? Never, noticesReady).GetAwaiter().GetResult();
                if (logsReady?.IsCompleted == true) { logsClosed = !logsReady.GetAwaiter().GetResult(); logsReady = null; }
                if (noticesReady.IsCompleted) { noticesReady.GetAwaiter().GetResult(); noticesReady = null; }
            }
        }
        catch (Exception exception) { Failure.TrySetResult(exception); _budget.Begin(); }
    }

    internal async Task<bool> FinishAsync(string text, bool error)
    {
        Capture(_runtime.Snapshot);
        _states.Dispose();
        var limit = _budget.Remaining < CliShutdownBudget.DisplayGrace ? _budget.Remaining : CliShutdownBudget.DisplayGrace;
        using var deadline = new CancellationTokenSource(limit);
        try
        {
            await _statePump.WaitAsync(deadline.Token).ConfigureAwait(false);
            _final = new(CliText.Visible(text), error);
            Volatile.Write(ref _finishing, 1);
            _notices.Writer.TryComplete();
            await _writer.WaitAsync(deadline.Token).ConfigureAwait(false);
            return !OutputFailed;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            Volatile.Write(ref _abandoned, 1);
            _notices.Writer.TryComplete();
            _logs.Dispose();
        }
    }

    internal async Task ReportUnavailableAsync(string text)
    {
        // An emergency report uses the other stream, never the writer that is already stuck.
        var active = Volatile.Read(ref _activeWriter);
        var writer = ReferenceEquals(active, _error) ? _output : _error;
        if (ReferenceEquals(active, writer)) return;
        var report = Task.Run(() => writer.WriteLine(CliText.Visible(text)));
        _ = report.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var limit = _budget.Remaining < TimeSpan.FromMilliseconds(100) ? _budget.Remaining : TimeSpan.FromMilliseconds(100);
        try { await report.WaitAsync(limit).ConfigureAwait(false); }
        catch (Exception) { } // The failure exit code remains authoritative if neither stream works.
    }
}
