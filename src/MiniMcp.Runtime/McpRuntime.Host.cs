using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MiniMcp.Configuration;
using MiniMcp.Diagnostics;
using MiniMcp.Security;
using MiniMcp.Services;
using MiniMcp.Tools;
using ModelContextProtocol.AspNetCore;

namespace MiniMcp;

// HTTP host construction, pipeline and disposal.
public sealed partial class McpRuntime
{
    private WebApplication BuildHost(RuntimeConfiguration configuration, Guid runId, RuntimeLoggerProvider logProvider)
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
            case AuthenticationMode.Local:
                break;
            case AuthenticationMode.CloudflareAccess:
                allowedHosts.Add(new Uri(authentication.PublicOrigin!).Host);
                builder.Services.AddCloudflareAccess(authentication);
                break;
            default:
                throw new InvalidOperationException("Unknown authentication mode.");
        }
        builder.Services.AddHostFiltering(options =>
        {
            options.AllowedHosts = allowedHosts;
            options.AllowEmptyHosts = false;
            options.IncludeFailureMessage = false;
        });

        _configureServices?.Invoke(builder.Services);
        return builder.Build();
    }

    private void ConfigureHttpPipeline(WebApplication application, AuthenticationMode authenticationMode)
    {
        application.UseHostFiltering();
        application.UseRouting();
        if (authenticationMode == AuthenticationMode.CloudflareAccess)
        {
            application.UseAuthentication();
            application.UseAuthorization();
        }
        application.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        var mcp = application.MapMcp("/mcp");
        if (authenticationMode == AuthenticationMode.CloudflareAccess)
            mcp.RequireAuthorization();
        _configureEndpoints?.Invoke(application);
    }

    private void ReleaseHost() => Volatile.Write(ref _host, null);

    /// <summary>
    /// Everything one run owns once BuildHost has succeeded. Stop and dispose tasks are kept so a
    /// failed shutdown can be retried without overlapping work that is still in flight.
    /// </summary>
    private sealed class HostLease(WebApplication application)
    {
        private volatile ToolExecutionService? _execution;
        private Task? _stop;
        private Task? _dispose;

        public WebApplication Application { get; } = application;

        /// <summary>Resolved before the host listens; readers outside the lifecycle lock see it via a volatile read.</summary>
        public ToolExecutionService? Execution
        {
            get => _execution;
            set => _execution = value;
        }

        /// <summary>True once Kestrel has started; an unstarted host is only disposed.</summary>
        public bool Started { get; set; }

        public async Task StopAndDisposeAsync(CancellationToken cancellationToken)
        {
            // A failed stop can be retried; never overlap stopping with unfinished disposal.
            if (Started && _dispose is null)
            {
                if (_stop is null || _stop.IsFaulted || _stop.IsCanceled)
                    _stop = Task.Run(() => Application.StopAsync(cancellationToken), CancellationToken.None);
                await _stop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            // Dispatch even synchronous disposal work so a blocking service cannot bypass the shared deadline.
            // Neither Task.Run takes the caller token: once dispatched, stop/dispose must run to completion
            // so a later retry observes a settled task instead of one that never started.
            _dispose ??= Task.Run(async () => await Application.DisposeAsync().ConfigureAwait(false), CancellationToken.None);
            await _dispose.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class CallerControlledLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
