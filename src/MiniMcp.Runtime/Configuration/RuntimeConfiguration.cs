namespace MiniMcp.Configuration;

/// <summary>
/// Immutable normalized configuration. Recovery-only loads skip external-program checks and
/// cannot start an HTTP host. Later file or draft edits do not change either kind of snapshot.
/// </summary>
public sealed class RuntimeConfiguration
{
    internal RuntimeConfiguration(AppSettings settings, ExecutionPolicy policy, string settingsFilePath, bool forRecoveryOnly = false)
    {
        Settings = settings;
        Policy = policy;
        SettingsFilePath = settingsFilePath;
        ForRecoveryOnly = forRecoveryOnly;
    }

    public AppSettings Settings { get; }
    public ExecutionPolicy Policy { get; }
    internal bool ForRecoveryOnly { get; }
    /// <summary>The normalized source path, or intended save path when validating an in-memory draft.</summary>
    public string SettingsFilePath { get; }
}
