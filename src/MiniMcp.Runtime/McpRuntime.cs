using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMcp.Configuration;
using MiniMcp.Security;
using MiniMcp.Services;
using MiniMcp.Tools;
using MiniMcp.Recovery;
using ModelContextProtocol.AspNetCore;

namespace MiniMcp.Runtime;

/// <summary>
/// Caller-owned loopback HTTP lifetime started with validated configuration.
/// Provides stateless MCP over HTTP and an anonymous health endpoint.
/// </summary>
public sealed class McpRuntime : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly int? _testPortOverride;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly Action<IEndpointRouteBuilder>? _configureEndpoints;
    private readonly TunnelCheckpoints? _tunnelChecks;
    private WebApplication? _application;
    private ToolExecutionService? _execution;
    private Task? _hostStopTask;
    private Task? _hostDisposeTask;
    private readonly RuntimeDiagnostics _diagnostics = new();
    private ILogger? _runtimeLogger;
    private RuntimeLoggerProvider? _logProvider;
    private TunnelProcess? _tunnel;
    private bool _hostStarted;
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
    internal ToolExecutionService? Execution => Volatile.Read(ref _execution);

    /// <summary>Up to 1000 entries / 512 Ki UTF-16 characters; each message is at most 4096 characters.</summary>
    public RuntimeLogSnapshot GetLogs() => _diagnostics.GetLogs();
    public void ClearLogs() => _diagnostics.ClearLogs();

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

    public async Task StartAsync(RuntimeConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // Host construction can do synchronous work; do not perform it on the caller's UI context.
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try { await StartCoreAsync(configuration, cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    /// <summary>The caller supplies a freshly loaded/validated saved configuration.</summary>
    public async Task RestartAsync(RuntimeConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            await StartCoreAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StartTunnelAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Snapshot.State != RuntimeState.Running)
                throw new InvalidOperationException("The MCP host must be running before starting its tunnel.");
            if (Snapshot.Configuration?.Settings.Tunnel.Enabled != true)
                throw new InvalidOperationException("The saved session configuration disables the managed tunnel.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ShutdownTimeout);
            if (_tunnel is not null)
            {
                if (!_tunnel.HasExited) throw new InvalidOperationException("The runtime already owns a running tunnel.");
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
        finally { _lifecycle.Release(); }
    }

    public async Task StopTunnelAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ShutdownTimeout);
            await StopTunnelCoreAsync(deadline.Token).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StartCoreAsync(RuntimeConfiguration configuration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_application is not null) throw new InvalidOperationException("The runtime already owns an HTTP host. Stop it before starting again.");

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
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                Args = [],
                ApplicationName = typeof(McpRuntime).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders().SetMinimumLevel(LogLevel.Information).AddProvider(logProvider);
            var port = _testPortOverride ?? configuration.Settings.Port;
            builder.WebHost.UseKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, port);
                options.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
            });
            builder.Services.AddRouting();
            builder.Services.AddSingleton<IHostLifetime>(new CallerControlledLifetime());
            builder.Services.AddToolExecution(runId, configuration);
            builder.Services.AddMcpServer()
                .WithHttpTransport(options =>
                {
                    // Stateless disallows GET/DELETE; legacy SSE mapping stays at its disabled SDK default.
                    options.SessionMode = HttpServerSessionMode.Stateless;
                })
                .WithTools<WorkspaceTools>()
                .AddAuthorizationFilters();
            var authentication = configuration.Settings.Authentication;
            List<string> allowedHosts = ["localhost", "127.0.0.1", "[::1]"];
            switch (authentication.Mode)
            {
                case AuthenticationMode.Local: break;
                case AuthenticationMode.CloudflareAccess:
                    allowedHosts.Add(new Uri(authentication.PublicOrigin!).Host);
                    builder.Services.AddCloudflareAccess(authentication);
                    break;
                default: throw new InvalidOperationException("Unknown authentication mode.");
            }
            builder.Services.AddHostFiltering(options =>
            {
                options.AllowedHosts = allowedHosts;
                options.AllowEmptyHosts = false;
                options.IncludeFailureMessage = false;
            });
            _configureServices?.Invoke(builder.Services);
            var application = builder.Build();
            ToolExecutionService? execution = null;
            _application = application;

            try
            {
                // Resolve before listening, so workspace initialization cannot fail on the first tool request.
                execution = application.Services.GetRequiredService<ToolExecutionService>();
                Volatile.Write(ref _execution, execution);
                application.UseHostFiltering();
                application.UseRouting();
                if (authentication.Mode == AuthenticationMode.CloudflareAccess)
                {
                    application.UseAuthentication();
                    application.UseAuthorization();
                }
                application.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
                var mcp = application.MapMcp("/mcp");
                if (authentication.Mode == AuthenticationMode.CloudflareAccess) mcp.RequireAuthorization();
                _configureEndpoints?.Invoke(application);
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                _hostStarted = true;
                cancellationToken.ThrowIfCancellationRequested();
                Publish(Snapshot with { Address = new Uri(application.Urls.Single()) });
                if (configuration.Settings.Tunnel.Enabled)
                    await StartTunnelCoreAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var running = Snapshot with { State = RuntimeState.Running, Address = new Uri(application.Urls.Single()) };
                _application = application;
                Volatile.Write(ref _execution, execution);
                Publish(running);
                ObserveTunnel();
            }
            catch (Exception startupError)
            {
                // Retain both ownership and any unfinished cleanup task until cleanup actually completes.
                _application = application;
                Volatile.Write(ref _execution, execution);
                try
                {
                    using var deadline = new CancellationTokenSource(ShutdownTimeout);
                    await StopOwnedResourcesAsync(deadline.Token).ConfigureAwait(false);
                    ReleaseHost();
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Startup failed and HTTP host cleanup did not complete.", startupError, cleanupError);
                }
                throw;
            }
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException && cancellationToken.IsCancellationRequested && _application is null;
            Publish(Snapshot with
            {
                State = cancelled ? RuntimeState.Stopped : RuntimeState.Faulted,
                Address = _application is null ? null : Snapshot.Address,
                Error = cancelled ? null : error.Message
            });
            if (_application is null) _diagnostics.EndRun(runId);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try { await StopCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (_disposed) return;
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            _disposed = true;
            Publish(Snapshot with { State = RuntimeState.Disposed });
            _diagnostics.Complete();
        }
        finally { _lifecycle.Release(); }
        // Keep the managed semaphore usable for calls racing with disposal; it owns no wait handle.
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_application is null)
        {
            if (!_disposed) Publish(Snapshot with { State = RuntimeState.Stopped, Address = null, Error = null });
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ShutdownTimeout);
            var draining = _execution?.StopAsync(deadline.Token) ?? Task.CompletedTask;
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

    private async Task StopOwnedResourcesAsync(CancellationToken cancellationToken, Task? draining = null)
    {
        draining ??= _execution?.StopAsync(cancellationToken) ?? Task.CompletedTask;
        var tunnel = StopTunnelCoreAsync(cancellationToken);
        await Task.WhenAll(draining, tunnel).ConfigureAwait(false);
        var application = _application!;
        // A failed stop can be retried; never overlap stopping with unfinished disposal.
        if (_hostStarted && _hostDisposeTask is null)
        {
            if (_hostStopTask is null || _hostStopTask.IsFaulted || _hostStopTask.IsCanceled)
                _hostStopTask = Task.Run(() => application.StopAsync(cancellationToken));
            await _hostStopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await DisposeHostAsync(application, cancellationToken).ConfigureAwait(false);
    }

    private async Task StartTunnelCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish(Snapshot with { Tunnel = new(TunnelState.Starting) });
        try
        {
            _tunnel = TunnelProcess.Start(Snapshot.Configuration!.Settings.Tunnel, _logProvider!, _tunnelChecks);
            Publish(Snapshot with { Tunnel = new(TunnelState.Starting, _tunnel.InstanceId, _tunnel.ProcessId) });
            await _tunnel.CheckStartedAsync(cancellationToken).ConfigureAwait(false);
            Publish(Snapshot with { Tunnel = Snapshot.Tunnel with { State = TunnelState.Running } });
        }
        catch (Exception error)
        {
            Publish(Snapshot with { Tunnel = Snapshot.Tunnel with { State = TunnelState.Faulted, ExitCode = _tunnel?.ExitCode, Error = error.Message } });
            throw;
        }
    }

    private async Task StopTunnelCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tunnel = _tunnel;
        if (tunnel is null)
        {
            if (Snapshot.Tunnel.State is not (TunnelState.Stopped or TunnelState.Disabled))
                Publish(Snapshot with { Tunnel = new(Snapshot.Configuration?.Settings.Tunnel.Enabled == true ? TunnelState.Stopped : TunnelState.Disabled) });
            return;
        }
        Publish(Snapshot with { Tunnel = Snapshot.Tunnel with { State = TunnelState.Stopping, Error = null } });
        try
        {
            await tunnel.StopAsync(cancellationToken).ConfigureAwait(false);
            _tunnel = null;
            Publish(Snapshot with { Tunnel = new(TunnelState.Stopped, tunnel.InstanceId, ExitCode: tunnel.ExitCode) });
        }
        catch (Exception error)
        {
            Publish(Snapshot with { Tunnel = Snapshot.Tunnel with { State = TunnelState.Faulted, ExitCode = tunnel.ExitCode, Error = error.Message } });
            throw;
        }
    }

    private void ObserveTunnel()
    {
        if (_tunnel is not { } tunnel || Snapshot.RunId is not { } runId) return;
        var logger = _logProvider!.CreateLogger("MiniMcp.Tunnel");
        tunnel.Observe((exitCode, error) =>
        {
            var safeError = LogSanitizer.Sanitize(error, RuntimeDiagnostics.MaxEntryCharacters, out _);
            if (_diagnostics.PublishTunnelFault(runId, tunnel.InstanceId, exitCode, safeError))
                logger.LogError("Tunnel fault: {Error}", safeError);
        });
    }

    private Task DisposeHostAsync(WebApplication application, CancellationToken cancellationToken)
    {
        // Dispatch even synchronous disposal work so a blocking service cannot bypass the shared deadline.
        _hostDisposeTask ??= Task.Run(async () => await application.DisposeAsync().ConfigureAwait(false));
        return _hostDisposeTask.WaitAsync(cancellationToken);
    }

    private void ReleaseHost()
    {
        _application = null;
        Volatile.Write(ref _execution, null);
        _hostStopTask = null;
        _hostDisposeTask = null;
        _hostStarted = false;
    }

    private void Publish(RuntimeSnapshot snapshot)
    {
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
                else _runtimeLogger?.LogInformation("Tunnel state: {State}; process: {ProcessId}.", snapshot.Tunnel.State, snapshot.Tunnel.ProcessId);
            }
        }
    }

    private void LogState(RuntimeSnapshot snapshot)
    {
        if (snapshot.Error is { } error) _runtimeLogger?.LogError("Runtime state: {State}. {Error}", snapshot.State, error);
        else _runtimeLogger?.LogInformation("Runtime state: {State}.", snapshot.State);
    }

    private sealed class CallerControlledLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
