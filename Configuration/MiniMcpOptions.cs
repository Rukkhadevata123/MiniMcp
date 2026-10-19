using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace MiniMcp.Configuration;

public sealed record MiniMcpOptions(
    string Root,
    string AuthMode,
    Uri? PublicOrigin,
    string? AccessTeam,
    string? AccessAudience,
    int Port = 8000,
    bool EnableBuild = false,
    int BuildTimeoutSeconds = 60)
{
    public bool IsLocal => AuthMode == "Local";
    public string AccessIssuer => $"https://{AccessTeam}.cloudflareaccess.com";

    public static MiniMcpOptions Load(IConfiguration configuration)
    {
        string Required(string key) => string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException($"Set {key} before starting MiniMcp.")
            : configuration[key]!.Trim();

        int Number(string key, int fallback, int minimum, int maximum)
        {
            var text = configuration[key];
            if (string.IsNullOrEmpty(text)) return fallback;
            if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
                throw new InvalidOperationException($"{key} must be between {minimum} and {maximum}.");
            return value;
        }

        var mode = configuration["MCP_AUTH_MODE"] ?? "CloudflareAccess";
        if (mode is not ("Local" or "CloudflareAccess"))
            throw new InvalidOperationException("MCP_AUTH_MODE must be Local or CloudflareAccess.");

        Uri? origin = null;
        string? team = null, audience = null;
        if (mode == "CloudflareAccess")
        {
            var publicUrl = Required("MCP_PUBLIC_URL");
            if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out origin) ||
                origin.Scheme != "https" || !string.IsNullOrEmpty(origin.UserInfo) ||
                origin.AbsolutePath != "/" || origin.Query != "" || origin.Fragment != "")
                throw new InvalidOperationException("MCP_PUBLIC_URL must be an HTTPS origin, such as https://mcp.example.com (without /mcp).");

            team = Required("CF_ACCESS_TEAM");
            if (!Regex.IsMatch(team, @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z"))
                throw new InvalidOperationException("CF_ACCESS_TEAM must be the team subdomain prefix, without https:// or .cloudflareaccess.com.");
            audience = Required("CF_ACCESS_AUD");
        }

        var buildText = configuration["MCP_ENABLE_BUILD"];
        var enableBuild = false;
        if (!string.IsNullOrEmpty(buildText) && !bool.TryParse(buildText, out enableBuild))
            throw new InvalidOperationException("MCP_ENABLE_BUILD must be true or false.");

        return new MiniMcpOptions(Required("MCP_ROOT"), mode, origin, team, audience,
            Number("MCP_PORT", 8000, 1, 65535), enableBuild,
            Number("MCP_BUILD_TIMEOUT_SECONDS", 60, 1, 600));
    }
}
