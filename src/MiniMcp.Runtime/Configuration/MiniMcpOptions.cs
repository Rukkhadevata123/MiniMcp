namespace MiniMcp.Configuration;

public sealed record MiniMcpOptions(
    string Root,
    string AuthMode,
    Uri? PublicOrigin,
    string? AccessTeam,
    string? AccessAudience,
    int Port = 8000,
    string? BashPath = null)
{
    public bool IsLocal => AuthMode == "Local";
    public string AccessIssuer => $"https://{AccessTeam}.cloudflareaccess.com";
}
