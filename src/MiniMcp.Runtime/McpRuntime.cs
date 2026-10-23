using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MiniMcp.Runtime;

/// <summary>
/// Caller-owned HTTP lifetime prototype. Only /healthz is registered at this stage.
/// Port zero requests an ephemeral loopback port for in-process checks.
/// </summary>
public sealed class McpRuntime : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly int _port;
    private WebApplication? _application;
    private Uri? _address;
    private bool _disposed;

    public McpRuntime(int port = 8000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        _port = port;
    }

    public Uri? Address => Volatile.Read(ref _address);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Host construction can do synchronous work; do not perform it on the caller's UI context.
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_application is not null) throw new InvalidOperationException("The runtime already owns an HTTP host. Stop it before starting again.");

            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                Args = [],
                ApplicationName = typeof(McpRuntime).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, _port));
            builder.Services.AddRouting();
            builder.Services.AddSingleton<IHostLifetime>(new CallerControlledLifetime());
            var application = builder.Build();
            application.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

            try
            {
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var address = new Uri(application.Urls.Single());
                _application = application;
                Volatile.Write(ref _address, address);
            }
            catch (Exception startupError)
            {
                try { await application.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout).ConfigureAwait(false); }
                catch (Exception cleanupError)
                {
                    // Retain ownership so a caller can retry cleanup rather than create a second host.
                    _application = application;
                    throw new AggregateException("Startup failed and HTTP host cleanup did not complete.", startupError, cleanupError);
                }
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
        }
        finally { _lifecycle.Release(); }
        // Keep the managed semaphore usable for calls racing with disposal; it owns no wait handle.
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_application is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ShutdownTimeout);
        await _application.StopAsync(deadline.Token).ConfigureAwait(false);
        await _application.DisposeAsync().AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
        _application = null;
        Volatile.Write(ref _address, null);
    }

    private sealed class CallerControlledLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
