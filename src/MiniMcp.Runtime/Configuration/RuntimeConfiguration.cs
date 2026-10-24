namespace MiniMcp.Configuration;

/// <summary>
/// Immutable input produced by complete configuration validation. Paths and enabled program
/// dependencies have been resolved; later edits to files or drafts do not change this snapshot.
/// </summary>
public sealed class RuntimeConfiguration
{
    internal RuntimeConfiguration(AppSettings settings, ExecutionPolicy policy, string settingsFilePath)
    {
        Settings = settings;
        Policy = policy;
        SettingsFilePath = settingsFilePath;
    }

    public AppSettings Settings { get; }
    public ExecutionPolicy Policy { get; }
    /// <summary>The normalized source path, or intended save path when validating an in-memory draft.</summary>
    public string SettingsFilePath { get; }
}
