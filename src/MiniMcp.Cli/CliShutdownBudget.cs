using System.Diagnostics;

namespace MiniMcp.Cli;

internal sealed class CliShutdownBudget
{
    internal static readonly TimeSpan Total = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DisplayGrace = TimeSpan.FromMilliseconds(500);
    private long _began;
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Begin()
    {
        if (Interlocked.CompareExchange(ref _began, Stopwatch.GetTimestamp(), 0) == 0) Started.TrySetResult();
    }

    internal TimeSpan Remaining
    {
        get
        {
            var began = Volatile.Read(ref _began);
            var remaining = began == 0 ? Total : Total - Stopwatch.GetElapsedTime(began);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    internal TimeSpan CleanupRemaining
    {
        get
        {
            var remaining = Remaining;
            return remaining > DisplayGrace ? remaining - DisplayGrace : TimeSpan.Zero;
        }
    }
}
