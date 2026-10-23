namespace MiniMcp.Configuration;

/// <summary>
/// Settings stored in settings.json. Paths remain as entered until configuration is loaded.
/// Execution permissions are stored separately in the local user's ExecutionPolicy.
/// </summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string ProjectRoot { get; init; } = "";
    public int Port { get; init; } = 8000;
    public string BashPath { get; init; } = "";
    public AuthenticationSettings Authentication { get; init; } = new();
    public TunnelSettings Tunnel { get; init; } = new();
}

public sealed record AuthenticationSettings
{
    public AuthenticationMode Mode { get; init; } = AuthenticationMode.CloudflareAccess;
    public string? PublicOrigin { get; init; }
    public string? Team { get; init; }
    public string? Audience { get; init; }
}

public sealed record TunnelSettings
{
    public bool Enabled { get; init; }
    public string ExecutablePath { get; init; } = "";
    public string TokenFilePath { get; init; } = "";
}
