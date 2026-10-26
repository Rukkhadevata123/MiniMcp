using System.Collections.Immutable;
using System.Threading.Channels;

namespace MiniMcp.Diagnostics;

/// <summary>
/// An atomic initial view followed by a private bounded stream. Log readers can detect gaps using
/// Sequence and reload GetLogs(); state streams coalesce to the latest snapshot. No observer code
/// runs inline during publication. Dispose/cancellation unsubscribes and discards pending items.
/// </summary>
public sealed class RuntimeSubscription<T> : IDisposable
{
    private readonly Channel<T> _channel;
    private readonly Action<RuntimeSubscription<T>> _remove;
    private readonly object _gate = new();
    private CancellationTokenRegistration _cancellation;
    private bool _closed;

    internal RuntimeSubscription(ImmutableArray<T> initial, int capacity, Action<RuntimeSubscription<T>> remove)
    {
        Initial = initial;
        _remove = remove;
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
    }

    public ImmutableArray<T> Initial { get; }
    public ChannelReader<T> Reader => _channel.Reader;

    internal void AttachCancellation(CancellationToken cancellationToken)
    {
        var registration = cancellationToken.UnsafeRegister(static value => ((RuntimeSubscription<T>)value!).Dispose(), this);
        lock (_gate)
        {
            if (_closed) registration.Unregister();
            else _cancellation = registration;
        }
    }

    internal void Write(T item) => _channel.Writer.TryWrite(item);

    // Runtime completion retains the final queued diagnostics/state for readers to drain.
    internal void Complete() => Close(discardPending: false);
    public void Dispose() => Close(discardPending: true);

    private void Close(bool discardPending)
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            // Never wait for a callback that may be waiting for the publisher's lock.
            _cancellation.Unregister();
        }
        _remove(this);
        _channel.Writer.TryComplete();
        if (discardPending) while (_channel.Reader.TryRead(out _)) { }
    }
}
