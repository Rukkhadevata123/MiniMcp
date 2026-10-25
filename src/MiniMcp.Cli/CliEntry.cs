using System.Runtime.InteropServices;
using MiniMcp.Configuration;
using MiniMcp.Runtime;

namespace MiniMcp.Cli;

// The executable and controlled process checks share the actual signal registration and entry path.
internal static class CliEntry
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        ConfigurationStore? store = null, string? workingDirectory = null, Func<McpRuntime>? runtimeFactory = null)
    {
        using var signals = new CliSignals();
        return await CliApplication.RunAsync(args, output, error, signals.Token, store, workingDirectory, runtimeFactory).ConfigureAwait(false);
    }
}

internal sealed class CliSignals : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConsoleCancelEventHandler _handler;
    private readonly PosixSignalRegistration? _terminate;
    private Task _cancelling = Task.CompletedTask;
    private bool _requested, _disposed;

    internal CliSignals()
    {
        _handler = (_, args) => { args.Cancel = true; RequestStop(); };
        Console.CancelKeyPress += _handler;
        if (OperatingSystem.IsLinux())
            _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; RequestStop(); });
    }

    internal CancellationToken Token => _shutdown.Token;

    private void RequestStop()
    {
        lock (_gate)
        {
            if (_requested || _disposed) return;
            _requested = true;
            // Callbacks can block; they must not run in the OS signal handler.
            _cancelling = _shutdown.CancelAsync();
            _ = _cancelling.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= _handler;
        _terminate?.Dispose();
        lock (_gate)
        {
            _disposed = true;
            if (_cancelling.IsCompleted) _shutdown.Dispose();
            else _ = _cancelling.ContinueWith(_ => _shutdown.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
