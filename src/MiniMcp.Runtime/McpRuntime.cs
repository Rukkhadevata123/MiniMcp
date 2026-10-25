using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    private WebApplication? _application;
    private ToolExecutionService? _execution;
    private Task? _hostStopTask;
    private Task? _hostDisposeTask;
    private RuntimeSnapshot _snapshot = new(RuntimeState.Stopped);
    private bool _disposed;

    public McpRuntime() { }

    // Checks can use port zero, failing services and probe routes without changing the public HTTP surface.
    internal McpRuntime(int testPort, Action<IServiceCollection>? configureServices = null,
        Action<IEndpointRouteBuilder>? configureEndpoints = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(testPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(testPort, 65535);
        _testPortOverride = testPort;
        _configureServices = configureServices;
        _configureEndpoints = configureEndpoints;
    }

    public RuntimeSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public Uri? Address => Snapshot.Address;
    internal ToolExecutionService? Execution => Volatile.Read(ref _execution);

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
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_application is not null) throw new InvalidOperationException("The runtime already owns an HTTP host. Stop it before starting again.");

            var runId = Guid.NewGuid();
            Publish(new RuntimeSnapshot(RuntimeState.Starting, runId, configuration));
            try
            {
                var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
                {
                    Args = [],
                    ApplicationName = typeof(McpRuntime).Assembly.GetName().Name,
                    ContentRootPath = AppContext.BaseDirectory
                });
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

                try
                {
                    // Resolve before listening, so workspace initialization cannot fail on the first tool request.
                    execution = application.Services.GetRequiredService<ToolExecutionService>();
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
                    cancellationToken.ThrowIfCancellationRequested();
                    var running = Snapshot with { State = RuntimeState.Running, Address = new Uri(application.Urls.Single()) };
                    _application = application;
                    Volatile.Write(ref _execution, execution);
                    Publish(running);
                }
                catch (Exception startupError)
                {
                    // Retain both ownership and any unfinished cleanup task until cleanup actually completes.
                    _application = application;
                    Volatile.Write(ref _execution, execution);
                    try
                    {
                        using var deadline = new CancellationTokenSource(ShutdownTimeout);
                        if (execution is not null) await execution.StopAsync(deadline.Token).ConfigureAwait(false);
                        await DisposeHostAsync(application, deadline.Token).ConfigureAwait(false);
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
                Publish(Snapshot with { State = cancelled ? RuntimeState.Stopped : RuntimeState.Faulted, Error = cancelled ? null : error.Message });
                throw;
            }
        }
        finally { _lifecycle.Release(); }
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
            var draining = _execution?.StopAsync(deadline.Token);
            Publish(Snapshot with { State = RuntimeState.Stopping, Error = null });
            if (draining is not null) await draining.ConfigureAwait(false);
            var application = _application;
            var token = deadline.Token;
            // A failed stop can be retried; never overlap stopping with an unfinished disposal.
            if (_hostDisposeTask is null)
            {
                if (_hostStopTask is null || _hostStopTask.IsFaulted || _hostStopTask.IsCanceled)
                    _hostStopTask = Task.Run(() => application.StopAsync(token));
                await _hostStopTask.WaitAsync(token).ConfigureAwait(false);
            }
            await DisposeHostAsync(application, token).ConfigureAwait(false);
            ReleaseHost();
            Publish(Snapshot with { State = RuntimeState.Stopped, Address = null });
        }
        catch (Exception error)
        {
            Publish(Snapshot with { State = RuntimeState.Faulted, Error = error.Message });
            throw;
        }
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
    }

    private void Publish(RuntimeSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);

    private sealed class CallerControlledLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
