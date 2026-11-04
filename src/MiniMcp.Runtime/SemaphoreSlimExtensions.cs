using System.Runtime.CompilerServices;

namespace MiniMcp;

/// <summary>Exclusive-entry helper for the <c>SemaphoreSlim(1, 1)</c> locks used across the runtime.</summary>
internal static class SemaphoreSlimExtensions
{
    /// <summary>
    /// Waits for the semaphore and returns a lease that releases it on dispose:
    /// <code>using var lease = await semaphore.EnterAsync(cancellationToken);</code>
    /// The continuation always yields and never resumes on the caller's synchronization context,
    /// so work guarded by the lock cannot run synchronously on a UI thread even when the lock is free.
    /// Returning the configured awaitable makes that choice impossible to override at the call site.
    /// </summary>
    public static ConfiguredTaskAwaitable<SemaphoreLease> EnterAsync(
        this SemaphoreSlim semaphore, CancellationToken cancellationToken = default) =>
        EnterCoreAsync(semaphore, cancellationToken).ConfigureAwait(false);

    private static async Task<SemaphoreLease> EnterCoreAsync(SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        return new SemaphoreLease(semaphore);
    }
}

internal readonly struct SemaphoreLease(SemaphoreSlim semaphore) : IDisposable
{
    public void Dispose() => semaphore.Release();
}
