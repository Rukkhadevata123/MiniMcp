namespace MiniMcp.Configuration;

/// <summary>
/// Finds existing program files without running them. A candidate is not a runtime or version check.
/// Missing automatic candidates return null so the caller can decide which dependencies are required.
/// </summary>
public static class ExternalProgramLocator
{
    public static string? FindBash(string configuredPath, string configurationFile) =>
        FindBash(configuredPath, configurationFile, BashInstallDirectories(), Environment.GetEnvironmentVariable("PATH"));

    public static string? FindCloudflared(string configuredPath, string configurationFile) =>
        FindCloudflared(configuredPath, configurationFile, CloudflaredInstallDirectories(), Environment.GetEnvironmentVariable("PATH"));

    internal static string? FindBash(string configuredPath, string configurationFile,
        IEnumerable<string> installDirectories, string? searchPath) =>
        Find(configuredPath, configurationFile, "Bash", BashCandidates(installDirectories, searchPath));

    internal static string? FindCloudflared(string configuredPath, string configurationFile,
        IEnumerable<string> installDirectories, string? searchPath) =>
        Find(configuredPath, configurationFile, "cloudflared", CloudflaredCandidates(installDirectories, searchPath));

    private static string? Find(string configuredPath, string configurationFile, string program, IEnumerable<string?> candidates)
    {
        ArgumentNullException.ThrowIfNull(configuredPath);
        if (configuredPath.Length != 0)
        {
            var path = ConfigurationPaths.ResolveValuePath(configuredPath, configurationFile);
            if (!File.Exists(path))
                throw new FileNotFoundException($"The configured {program} path must point to an existing file.", path);
            return path;
        }
        return candidates.FirstOrDefault(path => path is not null && File.Exists(path));
    }

    private static IEnumerable<string?> BashCandidates(IEnumerable<string> installDirectories, string? searchPath)
    {
        foreach (var directory in AbsoluteDirectories(installDirectories))
            yield return OperatingSystem.IsWindows() ? GitBash(directory) : Path.Combine(directory, "bash");
        foreach (var directory in PathDirectories(searchPath))
        {
            if (!OperatingSystem.IsWindows()) { yield return Path.Combine(directory, "bash"); continue; }
            // Git for Windows adds cmd/, bin/ or usr/bin/ to PATH. Do not select WSL's bash.exe.
            var parent = Path.GetDirectoryName(directory);
            var name = Path.GetFileName(directory);
            if (parent is null) continue;
            if (name.Equals("cmd", StringComparison.OrdinalIgnoreCase)) yield return GitBash(parent);
            else if (name.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                yield return GitBash(parent);
                if (Path.GetFileName(parent).Equals("usr", StringComparison.OrdinalIgnoreCase)
                    && Path.GetDirectoryName(parent) is { } root) yield return GitBash(root);
            }
        }
    }

    private static string? GitBash(string root)
    {
        var shell = Path.Combine(root, "usr", "bin", "bash.exe");
        if (!File.Exists(Path.Combine(root, "cmd", "git.exe")) || !File.Exists(shell)) return null;
        var wrapper = Path.Combine(root, "bin", "bash.exe");
        return File.Exists(wrapper) ? wrapper : shell;
    }

    private static IEnumerable<string> CloudflaredCandidates(IEnumerable<string> installDirectories, string? searchPath)
    {
        var name = OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared";
        foreach (var directory in AbsoluteDirectories(installDirectories).Concat(PathDirectories(searchPath)))
            yield return Path.Combine(directory, name);
    }

    private static IEnumerable<string> PathDirectories(string? searchPath) =>
        AbsoluteDirectories((searchPath ?? "").Split(Path.PathSeparator));

    private static IEnumerable<string> AbsoluteDirectories(IEnumerable<string> directories)
    {
        foreach (var entry in directories)
        {
            var directory = entry;
            if (OperatingSystem.IsWindows() && directory.Length >= 2 && directory[0] == '"' && directory[^1] == '"')
                directory = directory[1..^1];
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) continue;
            string full;
            try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            yield return full;
        }
    }

    private static IEnumerable<string> BashInstallDirectories() => OperatingSystem.IsWindows()
        ? WindowsInstallDirectories("Git").Concat(UserGitDirectory())
        : ["/bin", "/usr/bin"];

    private static IEnumerable<string> CloudflaredInstallDirectories() => OperatingSystem.IsWindows()
        ? WindowsInstallDirectories("cloudflared")
        : ["/usr/local/bin", "/usr/bin", "/bin"];

    private static IEnumerable<string> WindowsInstallDirectories(string name)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var directory = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (Path.IsPathFullyQualified(directory)) yield return Path.Combine(directory, name);
        }
    }

    private static IEnumerable<string> UserGitDirectory()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (Path.IsPathFullyQualified(directory)) yield return Path.Combine(directory, "Programs", "Git");
    }
}
