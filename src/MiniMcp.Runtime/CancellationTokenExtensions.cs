namespace MiniMcp;

internal static class CancellationTokenExtensions
{
    /// <summary>
    /// Creates a source that cancels when <paramref name="cancellationToken"/> does or after
    /// <paramref name="timeout"/>, whichever comes first. Dispose it when the bounded operation ends:
    /// <code>using var deadline = cancellationToken.WithTimeout(ShutdownTimeout);</code>
    /// </summary>
    public static CancellationTokenSource WithTimeout(this CancellationToken cancellationToken, TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }
}
