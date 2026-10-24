using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MiniMcp.Configuration;
using MiniMcp.Services;

namespace MiniMcp.Runtime;

/// <summary>
/// Caller-owned loopback HTTP lifetime started with validated configuration.
/// The HTTP surface currently provides /healthz.
/// </summary>
public sealed class McpRuntime : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly int? _testPortOverride;
    private readonly Action<IServiceCollection>? _configureServices;
    private WebApplication? _application;
    private ToolExecutionService? _execution;
    private RuntimeSnapshot _snapshot = new(RuntimeState.Stopped);
    private bool _disposed;

    public McpRuntime() { }

    // In-process checks can use port zero and inject service failures without changing public startup.
    internal McpRuntime(int testPort, Action<IServiceCollection>? configureServices = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(testPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(testPort, 65535);
        _testPortOverride = testPort;
        _configureServices = configureServices;
    }

    public RuntimeSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public Uri? Address => Snapshot.Address;
    internal ToolExecutionService? Execution => Volatile.Read(ref _execution);

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
                builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
                builder.Services.AddRouting();
                builder.Services.AddSingleton<IHostLifetime>(new CallerControlledLifetime());
                builder.Services.AddToolExecution(runId, configuration);
                _configureServices?.Invoke(builder.Services);
                var application = builder.Build();
                ToolExecutionService? execution = null;

                try
                {
                    // Resolve before listening, so workspace initialization cannot fail on the first tool request.
                    execution = application.Services.GetRequiredService<ToolExecutionService>();
                    application.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
                    await application.StartAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var running = Snapshot with { State = RuntimeState.Running, Address = new Uri(application.Urls.Single()) };
                    _application = application;
                    Volatile.Write(ref _execution, execution);
                    Publish(running);
                }
                catch (Exception startupError)
                {
                    try { await application.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout).ConfigureAwait(false); }
                    catch (Exception cleanupError)
                    {
                        // Retain ownership so a caller can retry cleanup rather than create a second host.
                        _application = application;
                        Volatile.Write(ref _execution, execution);
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
        Publish(Snapshot with { State = RuntimeState.Stopping, Error = null });
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ShutdownTimeout);
            await _application.StopAsync(deadline.Token).ConfigureAwait(false);
            await _application.DisposeAsync().AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            _application = null;
            Volatile.Write(ref _execution, null);
            Publish(Snapshot with { State = RuntimeState.Stopped, Address = null });
        }
        catch (Exception error)
        {
            Publish(Snapshot with { State = RuntimeState.Faulted, Error = error.Message });
            throw;
        }
    }

    private void Publish(RuntimeSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);

    private sealed class CallerControlledLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
