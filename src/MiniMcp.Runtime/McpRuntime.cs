using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MiniMcp.Configuration;
using MiniMcp.Diagnostics;
using MiniMcp.Recovery;
using MiniMcp.Services;
using MiniMcp.Services.Processes;

namespace MiniMcp;

/// <summary>
/// Caller-owned loopback HTTP lifetime started with validated configuration.
/// Provides stateless MCP over HTTP and an anonymous health endpoint.
/// Host construction lives in McpRuntime.Host.cs; the managed tunnel in McpRuntime.Tunnel.cs.
/// </summary>
public sealed partial class McpRuntime : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    // Fixed at construction
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly RuntimeDiagnostics _diagnostics = new();
    private readonly int? _testPortOverride;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly Action<IEndpointRouteBuilder>? _configureEndpoints;
    private readonly TunnelCheckpoints? _tunnelChecks;

    // Owned for the duration of one run; null exactly when no host resources are held
    private HostLease? _host;
    private ILogger? _runtimeLogger;
    private RuntimeLoggerProvider? _logProvider;
    private TunnelProcess? _tunnel;

    private bool _disposed;

    public McpRuntime() { }

    // Checks can use port zero, failing services and probe routes without changing the public HTTP surface.
    internal McpRuntime(int testPort, Action<IServiceCollection>? configureServices = null,
        Action<IEndpointRouteBuilder>? configureEndpoints = null, TunnelCheckpoints? tunnelChecks = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(testPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(testPort, 65535);
        _testPortOverride = testPort;
        _configureServices = configureServices;
        _configureEndpoints = configureEndpoints;
        _tunnelChecks = tunnelChecks;
    }

    public RuntimeSnapshot Snapshot => _diagnostics.Snapshot;
    public Uri? Address => Snapshot.Address;
    internal ToolExecutionService? Execution => Volatile.Read(ref _host)?.Execution;
    internal (int Logs, int States) DiagnosticSubscriberCounts => _diagnostics.SubscriberCounts;

    /// <summary>Up to 1000 entries / 512 Ki UTF-16 characters; each message is at most 4096 characters.</summary>
    public RuntimeLogSnapshot GetLogs() => _diagnostics.GetLogs();
    /// <summary>Clears retained logs and returns the sequence through which queued display entries can be discarded.</summary>
    public long ClearLogs() => _diagnostics.ClearLogs();

    /// <summary>Atomically captures retained logs and subscribes to new entries (128 queued entries maximum).</summary>
    public RuntimeSubscription<RuntimeLogEntry> SubscribeLogs(CancellationToken cancellationToken = default) =>
        _diagnostics.SubscribeLogs(cancellationToken);

    /// <summary>Captures the current state and subscribes to changes; a slow reader receives the latest state.</summary>
    public RuntimeSubscription<RuntimeSnapshot> SubscribeState(CancellationToken cancellationToken = default) =>
        _diagnostics.SubscribeState(cancellationToken);

    // In-process local entry; admission/cancellation belongs to the actual session, not the lifecycle lock.
    public Task<RecoveryPreviewResult> PreviewRecoveryAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Snapshot.State == RuntimeState.Disposed, this);
        var execution = Execution ?? throw new InvalidOperationException("Start a runtime session before previewing a recovery record.");
        return execution.PreviewRecoveryAsync(changeId, cancellationToken);
    }

    public LocalRecoverySession OpenRecoverySession()
    {
        ObjectDisposedException.ThrowIf(Snapshot.State == RuntimeState.Disposed, this);
        if (Snapshot.State != RuntimeState.Running || Execution is not { } execution)
            throw new InvalidOperationException("A running runtime session is required for shared recovery access.");
        return new(execution, ownsExecution: false);
    }

    public async Task StartAsync(RuntimeConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Host construction can do synchronous work; EnterAsync keeps it off the caller's UI context.
        using var lease = await _lifecycle.EnterAsync(cancellationToken);
        await StartCoreAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The caller supplies a freshly loaded/validated saved configuration.</summary>
    public async Task RestartAsync(RuntimeConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.ForRecoveryOnly)
            throw new InvalidOperationException("Load a complete runtime configuration before restarting the MCP host.");

        using var lease = await _lifecycle.EnterAsync(cancellationToken);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        await StartCoreAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartTunnelAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await _lifecycle.EnterAsync(cancellationToken);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Snapshot.State != RuntimeState.Running)
            throw new InvalidOperationException("The MCP host must be running before starting its tunnel.");
        if (Snapshot.Configuration?.Settings.Tunnel.Enabled != true)
            throw new InvalidOperationException("The saved session configuration disables the managed tunnel.");

        using var deadline = cancellationToken.WithTimeout(ShutdownTimeout);
        if (_tunnel is not null)
        {
            if (!_tunnel.HasExited)
                throw new InvalidOperationException("The runtime already owns a running tunnel.");
            await StopTunnelCoreAsync(deadline.Token).ConfigureAwait(false);
        }

        try
        {
            await StartTunnelCoreAsync(deadline.Token).ConfigureAwait(false);
            ObserveTunnel();
        }
        catch (Exception startupError)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(ShutdownTimeout);
                await StopTunnelCoreAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Tunnel startup failed and cleanup did not complete.", startupError, cleanupError);
            }

            Publish(Snapshot with { Tunnel = Snapshot.Tunnel with { State = TunnelState.Faulted, Error = startupError.Message } });
            throw;
        }
    }

    public async Task StopTunnelAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await _lifecycle.EnterAsync(cancellationToken);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var deadline = cancellationToken.WithTimeout(ShutdownTimeout);
        await StopTunnelCoreAsync(deadline.Token).ConfigureAwait(false);
    }

    private async Task StartCoreAsync(RuntimeConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.ForRecoveryOnly)
            throw new InvalidOperationException("Recovery-only configuration cannot start the MCP host; load a complete runtime configuration.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_host is not null)
            throw new InvalidOperationException("The runtime already owns an HTTP host. Stop it before starting again.");

        var runId = Guid.NewGuid();
        var logProvider = new RuntimeLoggerProvider(_diagnostics, runId);
        _logProvider = logProvider;
        _runtimeLogger = logProvider.CreateLogger(typeof(McpRuntime).FullName!);
        _diagnostics.BeginRun(new RuntimeSnapshot(RuntimeState.Starting, runId, configuration)
        {
            Tunnel = new(configuration.Settings.Tunnel.Enabled ? TunnelState.Stopped : TunnelState.Disabled)
        });
        LogState(Snapshot);

        try
        {
            var host = new HostLease(BuildHost(configuration, runId, logProvider));
            Volatile.Write(ref _host, host);
            Publish(Snapshot);

            try
            {
                await StartHostAsync(host, configuration, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception startupError)
            {
                await CleanUpFailedStartAsync(host, startupError, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception error)
        {
            PublishStartFailure(runId, error, cancellationToken);
            throw;
        }
    }

    private async Task StartHostAsync(HostLease host, RuntimeConfiguration configuration, CancellationToken cancellationToken)
    {
        var application = host.Application;

        // Resolve before listening, so workspace initialization cannot fail on the first tool request.
        host.Execution = application.Services.GetRequiredService<ToolExecutionService>();
        ConfigureHttpPipeline(application, configuration.Settings.Authentication.Mode);
        await application.StartAsync(cancellationToken).ConfigureAwait(false);
        host.Started = true;
        cancellationToken.ThrowIfCancellationRequested();

        var address = new Uri(application.Urls.Single());
        Publish(Snapshot with { Address = address });
        if (configuration.Settings.Tunnel.Enabled)
            await StartTunnelCoreAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        Publish(Snapshot with { State = RuntimeState.Running, Address = address });
        ObserveTunnel();
    }

    // The lease stays in _host until cleanup actually completes, so a failed cleanup can be retried.
    private async Task CleanUpFailedStartAsync(HostLease host, Exception startupError, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = new CancellationTokenSource(ShutdownTimeout);
            var draining = host.Execution?.StopAsync(deadline.Token) ?? Task.CompletedTask;
            var cancelledByCaller = startupError is OperationCanceledException && cancellationToken.IsCancellationRequested;

            Publish(Snapshot with
            {
                State = RuntimeState.Stopping,
                Error = cancelledByCaller ? null : startupError.Message
            });
            await StopOwnedResourcesAsync(deadline.Token, draining).ConfigureAwait(false);
            ReleaseHost();
        }
        catch (Exception cleanupError)
        {
            throw new AggregateException("Startup failed and HTTP host cleanup did not complete.", startupError, cleanupError);
        }
    }

    private void PublishStartFailure(Guid runId, Exception error, CancellationToken cancellationToken)
    {
        var released = _host is null;
        var cancelled = released && error is OperationCanceledException && cancellationToken.IsCancellationRequested;

        Publish(Snapshot with
        {
            State = cancelled ? RuntimeState.Stopped : RuntimeState.Faulted,
            Address = released ? null : Snapshot.Address,
            Error = cancelled ? null : error.Message
        });
        if (released)
            _diagnostics.EndRun(runId);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await _lifecycle.EnterAsync(cancellationToken);
        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => DisposeAsync(CancellationToken.None);

    /// <summary>Disposes within the caller's remaining shutdown budget, retaining ownership on failure.</summary>
    public async ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        // The managed semaphore stays usable for calls racing with disposal; it owns no wait handle.
        using var lease = await _lifecycle.EnterAsync(cancellationToken);
        if (_disposed)
            return;

        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        _disposed = true;
        Publish(Snapshot with { State = RuntimeState.Disposed });
        _diagnostics.Complete();
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_host is null)
        {
            if (!_disposed)
                Publish(Snapshot with { State = RuntimeState.Stopped, Address = null, Error = null });
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var deadline = cancellationToken.WithTimeout(ShutdownTimeout);
            var draining = _host.Execution?.StopAsync(deadline.Token) ?? Task.CompletedTask;
            Publish(Snapshot with { State = RuntimeState.Stopping, Error = null });
            await StopOwnedResourcesAsync(deadline.Token, draining).ConfigureAwait(false);
            ReleaseHost();

            Publish(Snapshot with { State = RuntimeState.Stopped, Address = null });
            _diagnostics.EndRun(Snapshot.RunId);
        }
        catch (Exception error)
        {
            Publish(Snapshot with { State = RuntimeState.Faulted, Error = error.Message });
            throw;
        }
    }

    private async Task StopOwnedResourcesAsync(CancellationToken cancellationToken, Task draining)
    {
        var tunnel = StopTunnelCoreAsync(cancellationToken);
        await Task.WhenAll(draining, tunnel).ConfigureAwait(false);
        await _host!.StopAndDisposeAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Publish(RuntimeSnapshot snapshot)
    {
        snapshot = snapshot with { HasOwnedResources = _host is not null };
        var previousTunnelState = Snapshot.Tunnel.State;
        if (snapshot.Error is { } error)
            snapshot = snapshot with { Error = LogSanitizer.Sanitize(error, RuntimeDiagnostics.MaxEntryCharacters, out _) };
        if (snapshot.Tunnel.Error is { } tunnelError)
            snapshot = snapshot with { Tunnel = snapshot.Tunnel with { Error = LogSanitizer.Sanitize(tunnelError, RuntimeDiagnostics.MaxEntryCharacters, out _) } };
        if (_diagnostics.Publish(snapshot))
        {
            LogState(snapshot);
            if (snapshot.Tunnel.State != previousTunnelState)
            {
                if (snapshot.Tunnel.Error is { } failure)
                    _runtimeLogger?.LogError("Tunnel state: {State}. {Error}", snapshot.Tunnel.State, failure);
                else
                    _runtimeLogger?.LogInformation("Tunnel state: {State}; process: {ProcessId}.", snapshot.Tunnel.State, snapshot.Tunnel.ProcessId);
            }
        }
    }

    private void LogState(RuntimeSnapshot snapshot)
    {
        if (snapshot.Error is { } error)
            _runtimeLogger?.LogError("Runtime state: {State}. {Error}", snapshot.State, error);
        else
            _runtimeLogger?.LogInformation("Runtime state: {State}.", snapshot.State);
    }

}
