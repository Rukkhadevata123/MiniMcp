using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MiniMcp.Configuration;

namespace MiniMcp.Security;

public static class CloudflareAccessAuthentication
{
    public static IServiceCollection AddCloudflareAccess(
        this IServiceCollection services, AuthenticationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Mode != AuthenticationMode.CloudflareAccess)
            throw new InvalidOperationException("Cloudflare Access registration requires CloudflareAccess authentication mode.");
        var issuer = $"https://{settings.Team}.cloudflareaccess.com";
        services.AddSingleton<IDocumentRetriever>(_ => new HttpDocumentRetriever { RequireHttps = true });
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IDocumentRetriever>((options, documents) =>
            {
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.RefreshOnIssuerKeyNotFound = true;
                // Access publishes JWKS directly, not an OIDC discovery document here.
                // ConfigurationManager caches keys, refreshes periodically, and refreshes on an unknown kid.
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    $"{issuer}/cdn-cgi/access/certs",
                    new AccessSigningKeysRetriever(issuer),
                    documents)
                {
                    AutomaticRefreshInterval = TimeSpan.FromHours(6),
                    RefreshInterval = TimeSpan.FromMinutes(5)
                };
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "email"
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var assertion = context.Request.Headers["Cf-Access-Jwt-Assertion"];
                        if (assertion.Count != 1 || string.IsNullOrWhiteSpace(assertion[0]))
                            context.NoResult();
                        else
                            context.Token = assertion[0];
                        // Never treat the client's opaque OAuth Bearer token as an Access JWT.
                        return Task.CompletedTask;
                    }
                };
            });
        services.AddAuthorization();
        return services;
    }

    private sealed class AccessSigningKeysRetriever(string issuer)
        : IConfigurationRetriever<OpenIdConnectConfiguration>
    {
        public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
            string address, IDocumentRetriever retriever, CancellationToken cancellationToken)
        {
            var document = await retriever.GetDocumentAsync(address, cancellationToken);
            var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
            foreach (var key in new JsonWebKeySet(document).GetSigningKeys())
                configuration.SigningKeys.Add(key);
            if (configuration.SigningKeys.Count == 0)
                throw new InvalidOperationException("Cloudflare Access returned no usable signing keys.");
            return configuration;
        }
    }
}
