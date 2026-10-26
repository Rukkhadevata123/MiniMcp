using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MiniMcp.Diagnostics;

namespace MiniMcp.Desktop.ViewModels;

public sealed class DesktopLogRow
{
    private static readonly JsonSerializerOptions DisplayOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal DesktopLogRow(RuntimeLogEntry entry)
    {
        Entry = entry;
        Text = $"{entry.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm:ss.fff}Z [{entry.Level}] [{entry.RunId.ToString("N")[..8]}] " +
            $"{Escape(entry.Category)}: {Escape(entry.Message)}" + (entry.Truncated ? " [已裁剪]" : "");
    }
    public RuntimeLogEntry Entry { get; }
    public string Text { get; }
    internal int Cost => Entry.Category.Length + Entry.Message.Length + Text.Length + 1;
    private static string Escape(string value) => JsonSerializer.Serialize(value, DisplayOptions)[1..^1];
}

// UI-owned bounded presentation. Producers only write Runtime's private channels; at most one
// pending dispatcher callback per stream is awaited. State never depends on displayed log text.
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class RuntimeMonitor : INotifyPropertyChanged, IAsyncDisposable
{
    public const int MaxRows = 1000;
    public const int MaxCharacters = 512 * 1024;
    private readonly McpRuntime _runtime;
    private readonly Func<Action, bool> _postState, _postLogs;
    private readonly RuntimeSubscription<RuntimeLogEntry> _logs;
    private readonly RuntimeSubscription<RuntimeSnapshot> _states;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _logPump, _statePump;
    private readonly Queue<DesktopLogRow> _retained = new();
    private readonly ObservableCollection<DesktopLogRow> _visible = [];
    private readonly object _disposeGate = new();
    private Task? _disposing;
    private int _characters, _minimumLevel;
    private long _lastSequence, _clearThrough, _omitted, _evicted;
    private string _search = "", _actionMessage = "", _observationError = "";
    private bool _currentRunOnly, _autoScroll = true;

    public RuntimeMonitor(McpRuntime runtime, Func<Action, bool> postState, Func<Action, bool>? postLogs = null)
    {
        _runtime = runtime; _postState = postState; _postLogs = postLogs ?? postState;
        Rows = new(_visible);
        _logs = runtime.SubscribeLogs(); _states = runtime.SubscribeState(); Snapshot = runtime.Snapshot;
        foreach (var entry in _logs.Initial) Append(entry);
        _logPump = Task.Run(PumpLogsAsync); _statePump = Task.Run(PumpStateAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<RuntimeSnapshot>? StateChanged;
    public event EventHandler? BatchApplied;
    public ReadOnlyObservableCollection<DesktopLogRow> Rows { get; }
    public RuntimeSnapshot Snapshot { get; private set; }
    public int RetainedCount => _retained.Count;
    public int CharacterCount => _characters;
    public long OmittedCount => _omitted;
    public string Summary => $"显示 {_visible.Count} / 保留 {_retained.Count} 条 · 队列缺口 {_omitted} 条 · 容量淘汰 {_evicted} 条";
    public string ActionMessage => _actionMessage;
    public string ObservationError => Volatile.Read(ref _observationError);
    public string SearchText
    {
        get => _search;
        set { var text = value ?? ""; if (text.Length > 256) text = text[..256]; if (_search == text) return; _search = text; Refilter(); Raise(); }
    }
    public int MinimumLevelIndex
    {
        get => _minimumLevel;
        set { if (value is < 0 or > 3 || _minimumLevel == value) return; _minimumLevel = value; Refilter(); Raise(); }
    }
    public bool CurrentRunOnly { get => _currentRunOnly; set { if (_currentRunOnly == value) return; _currentRunOnly = value; Refilter(); Raise(); } }
    public bool AutoScroll { get => _autoScroll; set { if (_autoScroll == value) return; _autoScroll = value; Raise(); if (value) BatchApplied?.Invoke(this, EventArgs.Empty); } }

    public void Clear()
    {
        _clearThrough = _runtime.ClearLogs(); _lastSequence = _clearThrough;
        _retained.Clear(); _visible.Clear(); _characters = 0; _omitted = 0; _evicted = 0;
        SetActionMessage("日志已清空。"); NotifyRows();
    }
    public void ReloadBuffer()
    {
        var snapshot = _runtime.GetLogs();
        _retained.Clear(); _visible.Clear(); _characters = 0; _lastSequence = _clearThrough; _omitted = 0; _evicted = 0;
        foreach (var entry in snapshot.Entries) Append(entry);
        SetActionMessage("已重新载入缓存中的日志。"); NotifyRows();
    }
    public string FilteredText() => string.Join(Environment.NewLine, _visible.Select(row => row.Text));
    public string SelectedText(IEnumerable<DesktopLogRow> rows)
    {
        var selected = rows.ToHashSet();
        return string.Join(Environment.NewLine, _visible.Where(selected.Contains).Select(row => row.Text));
    }
    public void SetActionMessage(string message) { _actionMessage = RuntimeDiagnosticText.Sanitize(message); Raise(nameof(ActionMessage)); }

    private async Task PumpLogsAsync()
    {
        try
        {
            while (await _logs.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                if (!await DispatchAsync(_postLogs, () =>
                {
                    var started = Stopwatch.GetTimestamp();
                    for (var count = 0; count < 64 && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 8 && _logs.Reader.TryRead(out var entry); count++) Append(entry);
                    NotifyRows();
                }).ConfigureAwait(false)) break;
                await Task.Delay(33, _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception error) { Volatile.Write(ref _observationError, RuntimeDiagnosticText.Sanitize("日志更新失败：" + error.Message)); }
        finally { _logs.Dispose(); }
    }
    private async Task PumpStateAsync()
    {
        try
        {
            while (await _states.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                if (!await DispatchAsync(_postState, () =>
                {
                    while (_states.Reader.TryRead(out _)) { }
                    var previous = Snapshot;
                    Snapshot = _runtime.Snapshot; // The live authoritative value wins over an old queued state.
                    if (_currentRunOnly && previous.RunId != Snapshot.RunId) Refilter();
                    Raise(nameof(Snapshot)); StateChanged?.Invoke(this, Snapshot);
                }).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception error) { Volatile.Write(ref _observationError, RuntimeDiagnosticText.Sanitize("状态更新失败：" + error.Message)); }
        finally { _states.Dispose(); }
    }
    private async Task<bool> DispatchAsync(Func<Action, bool> post, Action action)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!post(() =>
        {
            if (_stopping.IsCancellationRequested) { done.TrySetResult(false); return; }
            try { action(); done.TrySetResult(true); }
            catch (Exception error) { done.TrySetException(error); }
        }))
        { Volatile.Write(ref _observationError, "界面更新已停止：窗口无法处理新的显示任务。"); return false; }
        return await done.Task.WaitAsync(_stopping.Token).ConfigureAwait(false);
    }
    private void Append(RuntimeLogEntry entry)
    {
        if (entry.Sequence <= _clearThrough || entry.Sequence <= _lastSequence) return;
        if (entry.Sequence > _lastSequence + 1) _omitted += entry.Sequence - _lastSequence - 1;
        _lastSequence = entry.Sequence;
        var row = new DesktopLogRow(entry);
        while (_retained.Count > 0 && (_retained.Count >= MaxRows || _characters + row.Cost > MaxCharacters))
        { var removed = _retained.Dequeue(); _characters -= removed.Cost; _visible.Remove(removed); _evicted++; }
        if (row.Cost > MaxCharacters) { _evicted++; return; }
        _retained.Enqueue(row); _characters += row.Cost;
        if (Matches(row)) _visible.Add(row);
    }
    private bool Matches(DesktopLogRow row)
    {
        var level = _minimumLevel switch { 1 => LogLevel.Information, 2 => LogLevel.Warning, 3 => LogLevel.Error, _ => LogLevel.Trace };
        return row.Entry.Level >= level && (!_currentRunOnly || row.Entry.RunId == Snapshot.RunId) &&
            (_search.Length == 0 || row.Entry.Message.Contains(_search, StringComparison.OrdinalIgnoreCase) || row.Entry.Category.Contains(_search, StringComparison.OrdinalIgnoreCase));
    }
    private void Refilter()
    {
        var desired = _retained.Where(Matches).ToArray(); var keep = desired.ToHashSet();
        for (var index = _visible.Count - 1; index >= 0; index--) if (!keep.Contains(_visible[index])) _visible.RemoveAt(index);
        for (var index = 0; index < desired.Length; index++)
            if (index == _visible.Count) _visible.Add(desired[index]);
            else if (!ReferenceEquals(_visible[index], desired[index])) _visible.Insert(index, desired[index]);
        NotifyRows();
    }
    private void NotifyRows()
    {
        foreach (var property in new[] { nameof(Summary), nameof(RetainedCount), nameof(CharacterCount), nameof(OmittedCount), nameof(ObservationError) }) Raise(property);
        BatchApplied?.Invoke(this, EventArgs.Empty);
    }
    private void Raise([CallerMemberName] string property = "") => PropertyChanged?.Invoke(this, new(property));
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposing ??= StopAsync());
    }
    private async Task StopAsync()
    {
        _logs.Dispose(); _states.Dispose();
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_logPump, _statePump).ConfigureAwait(false);
        _stopping.Dispose();
    }
}
