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

    private sealed class CallerControlledLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
