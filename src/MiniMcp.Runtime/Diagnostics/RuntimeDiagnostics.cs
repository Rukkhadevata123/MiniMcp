using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace MiniMcp.Diagnostics;

/// <summary>One short publication lock; formatting, IO and observer continuations stay outside it.</summary>
internal sealed class RuntimeDiagnostics
{
    internal const int MaxEntries = 1000;
    internal const int MaxCharacters = 512 * 1024;
    internal const int MaxEntryCharacters = 4096;
    internal const int MaxCategoryCharacters = 256;
    internal const int LogSubscriptionCapacity = 128;
    private readonly object _gate = new();
    private readonly Queue<RuntimeLogEntry> _logs = new();
    private readonly HashSet<RuntimeSubscription<RuntimeLogEntry>> _logSubscriptions = [];
    private readonly HashSet<RuntimeSubscription<RuntimeSnapshot>> _stateSubscriptions = [];
    private RuntimeSnapshot _snapshot = new(RuntimeState.Stopped);
    private Guid? _acceptingRun;
    private int _characters;
    private long _sequence;
    private bool _completed;

    public RuntimeSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public RuntimeLogSnapshot GetLogs()
    {
        lock (_gate) return new(_logs.ToImmutableArray(), _characters);
    }

    public void ClearLogs()
    {
        // Existing subscriptions/snapshots retain their bounded immutable views. Sequences never reset.
        lock (_gate) { _logs.Clear(); _characters = 0; }
    }

    public RuntimeSubscription<RuntimeLogEntry> SubscribeLogs(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var subscription = new RuntimeSubscription<RuntimeLogEntry>(_logs.ToImmutableArray(), LogSubscriptionCapacity,
                value => { lock (_gate) _logSubscriptions.Remove(value); });
            _logSubscriptions.Add(subscription);
            if (_completed) subscription.Complete();
            subscription.AttachCancellation(cancellationToken);
            return subscription;
        }
    }

    public RuntimeSubscription<RuntimeSnapshot> SubscribeState(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var subscription = new RuntimeSubscription<RuntimeSnapshot>([Snapshot], 1,
                value => { lock (_gate) _stateSubscriptions.Remove(value); });
            _stateSubscriptions.Add(subscription);
            if (_completed) subscription.Complete();
            subscription.AttachCancellation(cancellationToken);
            return subscription;
        }
    }

    public void BeginRun(RuntimeSnapshot snapshot)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            _acceptingRun = snapshot.RunId;
            PublishCore(snapshot);
        }
    }

    public bool Publish(RuntimeSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_completed || snapshot.RunId != Snapshot.RunId) return false;
            var current = Snapshot;
            var faults = current.FaultCount;
            if ((snapshot.State == RuntimeState.Faulted &&
                (current.State != RuntimeState.Faulted || current.Error != snapshot.Error)) ||
                (snapshot.State == RuntimeState.Running && snapshot.Tunnel.State == TunnelState.Faulted &&
                (current.Tunnel.State != TunnelState.Faulted || current.Tunnel.Error != snapshot.Tunnel.Error))) faults++;
            if (snapshot.FaultCount != faults) snapshot = snapshot with { FaultCount = faults };
            PublishCore(snapshot);
            return true;
        }
    }

    // Process observation never waits for the lifecycle lock. The identity/state checks and update
    // are atomic with lifecycle publication, and cannot overwrite a stop or replacement process.
    public bool PublishTunnelFault(Guid runId, Guid instanceId, int? exitCode, string error)
    {
        lock (_gate)
        {
            var current = Snapshot;
            if (_completed || current.RunId != runId || current.State != RuntimeState.Running ||
                current.Tunnel.InstanceId != instanceId ||
                current.Tunnel.State is not (TunnelState.Running or TunnelState.Faulted)) return false;
            PublishCore(current with
            {
                FaultCount = current.FaultCount + (current.Tunnel.State != TunnelState.Faulted || current.Tunnel.Error != error ? 1 : 0),
                Tunnel = current.Tunnel with { State = TunnelState.Faulted, ExitCode = exitCode, Error = error }
            });
            return true;
        }
    }

    private void PublishCore(RuntimeSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        foreach (var subscription in _stateSubscriptions) subscription.Write(snapshot);
    }

    public void EndRun(Guid? runId)
    {
        lock (_gate) if (_acceptingRun == runId) _acceptingRun = null;
    }

    public bool IsAccepting(Guid runId)
    {
        lock (_gate) return !_completed && _acceptingRun == runId;
    }

    // Strings must already be sanitized before reaching the buffer or any subscriber.
    public void Write(Guid runId, LogLevel level, string category, int eventId, string message, bool truncated)
    {
        lock (_gate)
        {
            if (_completed || _acceptingRun != runId) return;
            var entry = new RuntimeLogEntry(++_sequence, DateTimeOffset.UtcNow, runId, level, category, eventId, message, truncated);
            var size = category.Length + message.Length;
            while (_logs.Count > 0 && (_logs.Count >= MaxEntries || _characters + size > MaxCharacters))
            {
                var removed = _logs.Dequeue();
                _characters -= removed.Category.Length + removed.Message.Length;
            }
            _logs.Enqueue(entry);
            _characters += size;
            foreach (var subscription in _logSubscriptions) subscription.Write(entry);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            _acceptingRun = null;
            foreach (var subscription in _logSubscriptions.ToArray()) subscription.Complete();
            foreach (var subscription in _stateSubscriptions.ToArray()) subscription.Complete();
        }
    }

    internal (int Logs, int States) SubscriberCounts
    {
        get { lock (_gate) return (_logSubscriptions.Count, _stateSubscriptions.Count); }
    }
}
