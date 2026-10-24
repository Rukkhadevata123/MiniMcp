using MiniMcp.Configuration;

internal static class ConfigurationValueChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var local = new AppSettings { Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local } };
        var cloud = new AppSettings
        {
            Authentication = new AuthenticationSettings
            {
                PublicOrigin = "https://mcp.example.com", Team = "mini-dev", Audience = "application-aud-tag"
            }
        };
        var policy = new ExecutionPolicy();
        void Reject(string field, AppSettings settings, ExecutionPolicy? selectedPolicy = null)
        {
            try { ConfigurationValidator.ValidateValues(settings, selectedPolicy ?? policy); }
            catch (ConfigurationValidationException exception)
            {
                if (exception.Field == field) return;
                throw new Exception($"Expected field {field}, got {exception.Field}.", exception);
            }
            throw new Exception($"Expected a validation error for {field}.");
        }

        await check("authentication and execution modes are independent for every supported execution mode", () =>
        {
            foreach (var mode in Enum.GetValues<ExecutionMode>())
            {
                ConfigurationValidator.ValidateValues(local, policy with { Mode = mode });
                ConfigurationValidator.ValidateValues(cloud, policy with { Mode = mode });
            }
            ConfigurationValidator.ValidateValues(local with { Port = 1 }, policy);
            ConfigurationValidator.ValidateValues(local with { Port = 65535 }, policy);
            // Inactive Cloudflare fields can be retained while selecting local authentication.
            ConfigurationValidator.ValidateValues(cloud with { Authentication = cloud.Authentication with { Mode = AuthenticationMode.Local } }, policy);
            return Task.CompletedTask;
        });

        await check("configuration ports reject zero and values outside the listen-port range", () =>
        {
            foreach (var port in new[] { int.MinValue, -1, 0, 65536, int.MaxValue }) Reject("port", local with { Port = port });
            return Task.CompletedTask;
        });

        await check("in-memory drafts reject unsupported schemas, undefined modes and null sections", () =>
        {
            Reject("schemaVersion", local with { SchemaVersion = 2 });
            Reject("policy.schemaVersion", local, policy with { SchemaVersion = 2 });
            Reject("mode", local, policy with { Mode = (ExecutionMode)999 });
            Reject("authentication.mode", local with { Authentication = local.Authentication with { Mode = (AuthenticationMode)999 } });
            Reject("authentication", local with { Authentication = null! });
            Reject("tunnel", local with { Tunnel = null! });
            return Task.CompletedTask;
        });

        await check("Cloudflare authentication requires an origin, team prefix and application audience", () =>
        {
            foreach (var value in new string?[] { null, "", " " })
            {
                Reject("authentication.publicOrigin", cloud with { Authentication = cloud.Authentication with { PublicOrigin = value } });
                Reject("authentication.team", cloud with { Authentication = cloud.Authentication with { Team = value } });
                Reject("authentication.audience", cloud with { Authentication = cloud.Authentication with { Audience = value } });
            }
            return Task.CompletedTask;
        });

        await check("HTTPS origins accept an optional root slash and port without network access", () =>
        {
            foreach (var origin in new[] { "https://mcp.example.com", "https://MCP.example.com/", "HTTPS://mcp.example.com",
                "https://mcp.example.com:8443", "https://[2001:db8::1]:8443/" })
                ConfigurationValidator.ValidateValues(cloud with { Authentication = cloud.Authentication with { PublicOrigin = origin } }, policy);
            return Task.CompletedTask;
        });

        await check("HTTPS origins reject credentials, paths, queries, fragments and ambiguous URL syntax", () =>
        {
            foreach (var origin in new[]
            {
                "http://mcp.example.com", "mcp.example.com", "https://", "https://*", "https://*.example.com",
                "https://mcp.example.com:0", "https://mcp.example.com:65536",
                "https://user:password@mcp.example.com", "https://@mcp.example.com", "https://mcp.example.com/mcp",
                "https://mcp.example.com/.", "https://mcp.example.com/mcp/..", "https://mcp.example.com/%2e",
                "https://mcp.example.com//", "https://mcp.example.com?", "https://mcp.example.com/?x=1",
                "https://mcp.example.com#", "https://mcp.example.com/#fragment", " https://mcp.example.com",
                "https://mcp.example.com ", "https://mcp.\nexample.com", "https://mcp.example.com\\"
            }) Reject("authentication.publicOrigin", cloud with { Authentication = cloud.Authentication with { PublicOrigin = origin } });
            return Task.CompletedTask;
        });

        await check("team prefixes are single DNS labels and AUD tags cannot contain whitespace or controls", () =>
        {
            foreach (var team in new[] { "a", "Mini-dev1", new string('a', 63) })
                ConfigurationValidator.ValidateValues(cloud with { Authentication = cloud.Authentication with { Team = team } }, policy);
            foreach (var team in new[] { "-team", "team-", "a.b", "team.cloudflareaccess.com", "https://team", "team:443",
                "team_name", " team", "team\0", "团队", new string('a', 64) })
                Reject("authentication.team", cloud with { Authentication = cloud.Authentication with { Team = team } });
            foreach (var audience in new[] { " leading", "trailing ", "two tags", "line\nbreak", "aud\0tag" })
                Reject("authentication.audience", cloud with { Authentication = cloud.Authentication with { Audience = audience } });
            return Task.CompletedTask;
        });

        await check("managed tunnels require Cloudflare authentication and a token file path", () =>
        {
            Reject("tunnel.enabled", local with { Tunnel = new TunnelSettings { Enabled = true, TokenFilePath = "token" } });
            foreach (var tokenPath in new string?[] { null, "", " " })
                Reject("tunnel.tokenFilePath", cloud with { Tunnel = new TunnelSettings { Enabled = true, TokenFilePath = tokenPath! } });
            // Value checks do not probe files, programs or their runtime permissions.
            ConfigurationValidator.ValidateValues(cloud with
            { Tunnel = new TunnelSettings { Enabled = true, TokenFilePath = "missing token file", ExecutablePath = "missing program" } }, policy);
            return Task.CompletedTask;
        });
    }
}
