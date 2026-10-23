namespace MiniMcp.Configuration;

/// <summary>
/// Selects configuration locations and resolves paths without creating directories or files.
/// Filesystem existence, links and project boundaries are checked by configuration validation.
/// </summary>
public sealed class ConfigurationPaths
{
    private ConfigurationPaths(string configurationDirectory, string stateDirectory)
    {
        SettingsFile = Path.GetFullPath(Path.Combine(configurationDirectory, "settings.json"));
        PolicyFile = Path.GetFullPath(Path.Combine(configurationDirectory, "policy.json"));
        RecoveryDirectory = Path.GetFullPath(Path.Combine(stateDirectory, "recovery"));
    }

    public string SettingsFile { get; }
    public string PolicyFile { get; }
    public string RecoveryDirectory { get; }

    public static ConfigurationPaths ForCurrentUser()
    {
        if (OperatingSystem.IsWindows())
        {
            var directory = Path.Combine(UserFolder(Environment.SpecialFolder.LocalApplicationData), "MiniMcp");
            return new ConfigurationPaths(directory, directory);
        }

        if (OperatingSystem.IsLinux())
        {
            var userDirectory = UserFolder(Environment.SpecialFolder.UserProfile);
            var configurationDirectory = XdgDirectory("XDG_CONFIG_HOME", Path.Combine(userDirectory, ".config"));
            var stateDirectory = XdgDirectory("XDG_STATE_HOME", Path.Combine(userDirectory, ".local", "state"));
            return new ConfigurationPaths(Path.Combine(configurationDirectory, "minimcp"),
                Path.Combine(stateDirectory, "minimcp"));
        }

        throw new PlatformNotSupportedException("MiniMcp configuration paths support Windows and Linux.");
    }

    /// <summary>
    /// Resolves --config against the caller's captured working directory; null selects the default.
    /// Choosing a settings file does not change the local policy or default recovery location.
    /// </summary>
    public string ResolveSettingsFile(string? path, string workingDirectory) =>
        path is null ? SettingsFile : ResolvePath(path, workingDirectory);

    /// <summary>
    /// Resolves a nonempty JSON path against its settings.json or policy.json location.
    /// Tilde, environment variables and shell expressions are treated as literal text.
    /// </summary>
    public static string ResolveValuePath(string path, string configurationFile)
    {
        RequireAbsolute(configurationFile);
        var directory = Path.GetDirectoryName(configurationFile);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("The configuration path must name a file.", nameof(configurationFile));
        return ResolvePath(path, directory);
    }

    private static string ResolvePath(string path, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RequireAbsolute(baseDirectory);
        if (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Use a relative path or a fully qualified absolute path.", nameof(path));
        return Path.GetFullPath(path, baseDirectory);
    }

    private static void RequireAbsolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The base path must be fully qualified.", nameof(path));
    }

    private static string UserFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"Cannot determine an absolute user directory for {folder}.");
        return path;
    }

    private static string XdgDirectory(string variable, string fallback)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) ? path : fallback;
    }
}
