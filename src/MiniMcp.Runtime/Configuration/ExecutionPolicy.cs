namespace MiniMcp.Configuration;

/// <summary>
/// Policy stored in the local user's policy.json, outside the project root.
/// An empty recovery directory selects the platform's default state directory.
/// </summary>
public sealed record ExecutionPolicy
{
    public int SchemaVersion { get; init; } = 1;
    public ExecutionMode Mode { get; init; } = ExecutionMode.ReadOnly;
    public string RecoveryDirectory { get; init; } = "";
}
