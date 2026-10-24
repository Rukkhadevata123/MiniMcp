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
    public PatchApprovalLimits PatchApprovalLimits { get; init; } = new();
}

/// <summary>
/// Inclusive bounds for patches eligible for automatic approval in ProjectEdit.
/// Each value is nonnegative; zero requires review for any nonzero change in that dimension.
/// These bounds do not override file restrictions or the fixed preview limits.
/// </summary>
public sealed record PatchApprovalLimits
{
    public int MaxPaths { get; init; } = 10;
    public int MaxChangedLines { get; init; } = 500;
    public int MaxChangedBytes { get; init; } = 64 * 1024;
}
