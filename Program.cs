using System.Net;
using MiniMcp.Configuration;
using MiniMcp.Security;
using MiniMcp.Services;
using MiniMcp.Tools;
using ModelContextProtocol.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var settings = MiniMcpOptions.Load(builder.Configuration);

builder.Configuration["AllowedHosts"] = settings.IsLocal
    ? "localhost;127.0.0.1;[::1]"
    : $"localhost;127.0.0.1;[::1];{settings.PublicOrigin!.Host}";
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, settings.Port);
    options.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
});

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<ProjectWorkspace>();
builder.Services.AddSingleton<PatchService>();
builder.Services.AddSingleton<BashRunner>();
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<WorkspaceTools>();

if (!settings.IsLocal)
{
    builder.Services.AddCloudflareAccess(settings);
}

var app = builder.Build();
// Resolve now so invalid/nonexistent roots fail at startup, before accepting requests.
_ = app.Services.GetRequiredService<ProjectWorkspace>();
_ = app.Services.GetRequiredService<BashRunner>();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
if (settings.IsLocal)
{
    app.MapMcp("/mcp");
    app.Logger.LogWarning("Local mode: authentication is disabled; only loopback hosts are accepted.");
}
else
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapMcp("/mcp").RequireAuthorization();
}

app.Logger.LogInformation("MiniMcp mode: {Mode}; tools: read_file, apply_patch, bash", settings.AuthMode);
app.Run();
