using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MiniMcp.Configuration;
using MiniMcp.Security;

internal static class AuthenticationRegistrationChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var settings = new AuthenticationSettings
        {
            Mode = AuthenticationMode.CloudflareAccess,
            PublicOrigin = "https://mcp.example.com",
            Team = "mini-dev",
            Audience = "application-aud-tag"
        };
        ConfigurationValidator.ValidateValues(new AppSettings { Authentication = settings }, new ExecutionPolicy());

        await check("Cloudflare registration uses validated team and audience without fetching keys", () =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCloudflareAccess(settings);
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme);
            var validation = options.TokenValidationParameters;
            const string issuer = "https://mini-dev.cloudflareaccess.com";
            if (validation.ValidIssuer != issuer || validation.ValidAudience != settings.Audience ||
                options.ConfigurationManager is not ConfigurationManager<OpenIdConnectConfiguration> manager ||
                manager.MetadataAddress != issuer + "/cdn-cgi/access/certs")
                throw new Exception("Cloudflare JWT registration did not use the supplied authentication settings.");
            return Task.CompletedTask;
        });

        await check("Cloudflare registration rejects local and undefined authentication modes", () =>
        {
            foreach (var mode in new[] { AuthenticationMode.Local, (AuthenticationMode)999 })
            {
                var services = new ServiceCollection();
                try { services.AddCloudflareAccess(settings with { Mode = mode }); }
                catch (InvalidOperationException) when (services.Count == 0) { continue; }
                throw new Exception("Cloudflare registration accepted an unsupported authentication mode.");
            }
            return Task.CompletedTask;
        });
    }
}
