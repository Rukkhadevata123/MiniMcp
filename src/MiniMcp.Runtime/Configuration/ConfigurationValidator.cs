using MiniMcp.Services;

namespace MiniMcp.Configuration;

/// <summary>
/// Shared validation for parsed files and in-memory drafts. Path checks inspect the current
/// filesystem; they do not provide isolation from concurrent filesystem changes.
/// </summary>
public sealed class ConfigurationValidator
{
    private readonly ConfigurationPaths _paths;
    private readonly Func<string, string, string?> _findBash;
    private readonly Func<string, string, string?> _findCloudflared;

    public ConfigurationValidator() : this(ConfigurationPaths.ForCurrentUser()) { }

    internal ConfigurationValidator(ConfigurationPaths paths)
        : this(paths, ExternalProgramLocator.FindBash, ExternalProgramLocator.FindCloudflared) { }

    internal ConfigurationValidator(ConfigurationPaths paths,
        Func<string, string, string?> findBash, Func<string, string, string?> findCloudflared)
    {
        _paths = paths;
        _findBash = findBash;
        _findCloudflared = findCloudflared;
    }

    /// <summary>
    /// Validates values, normalizes paths and checks enabled dependencies without starting programs.
    /// Returns an immutable snapshot containing the normalized models; input drafts are unchanged.
    /// </summary>
    public RuntimeConfiguration Validate(
        AppSettings settings, string settingsFile, ExecutionPolicy policy)
    {
        ValidateValues(settings, policy);
        var normalized = NormalizePaths(settings, settingsFile, policy);
        settings = normalized.Settings;
        if (policy.Mode == ExecutionMode.FullAccess)
        {
            var bash = Field("bashPath", () => CheckProgram(_findBash(settings.BashPath, settingsFile), "Bash"));
            settings = settings with { BashPath = bash };
        }
        if (OperatingSystem.IsLinux() && (policy.Mode == ExecutionMode.FullAccess || settings.Tunnel.Enabled))
            Field("setsid", () =>
            {
                if (!File.Exists("/usr/bin/setsid"))
                    throw new InvalidOperationException("Install util-linux: /usr/bin/setsid is required for managed process cleanup.");
                return CheckProgram("/usr/bin/setsid", "setsid");
            });
        if (settings.Tunnel.Enabled)
        {
            var cloudflared = Field("tunnel.executablePath", () =>
                CheckProgram(_findCloudflared(settings.Tunnel.ExecutablePath, settingsFile), "cloudflared"));
            Field("tunnel.tokenFilePath", () =>
            {
                var path = settings.Tunnel.TokenFilePath;
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                    throw new InvalidOperationException("The tunnel token path must name a readable, nonempty file.");
                // Inspect metadata before opening to avoid opening empty files and Unix devices/FIFOs with zero size.
                if (new FileInfo(path).Length == 0)
                    throw new InvalidOperationException("The tunnel token file must not be empty.");
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (file.Length == 0) throw new InvalidOperationException("The tunnel token file must not be empty.");
                return path;
            });
            settings = settings with { Tunnel = settings.Tunnel with { ExecutablePath = cloudflared } };
        }
        return new RuntimeConfiguration(settings, normalized.Policy, normalized.SettingsFilePath);
    }

    /// <summary>
    /// Checks scalar values and mode combinations for parsed files or drafts without I/O.
    /// Paths and external dependencies are checked separately.
    /// </summary>
    public static void ValidateValues(AppSettings settings, ExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(policy);
        Require(settings.SchemaVersion == 1, "schemaVersion", "Unsupported settings schema version; expected 1.");
        Require(policy.SchemaVersion == 1, "policy.schemaVersion", "Unsupported policy schema version; expected 1.");
        Require(settings.Port is >= 1 and <= 65535, "port", "Use a port between 1 and 65535.");
        Require(Enum.IsDefined(policy.Mode), "mode", "Unknown execution mode.");
        var limits = policy.ProjectEditLimits
            ?? throw new ConfigurationValidationException("projectEditLimits", "Must not be null.");
        Require(limits.MaxPaths >= 0, "projectEditLimits.maxPaths", "Use a nonnegative integer.");
        Require(limits.MaxChangedLines >= 0, "projectEditLimits.maxChangedLines", "Use a nonnegative integer.");
        Require(limits.MaxChangedBytes >= 0, "projectEditLimits.maxChangedBytes", "Use a nonnegative integer.");
        var authentication = settings.Authentication
            ?? throw new ConfigurationValidationException("authentication", "Must not be null.");
        var tunnel = settings.Tunnel ?? throw new ConfigurationValidationException("tunnel", "Must not be null.");
        Require(Enum.IsDefined(authentication.Mode), "authentication.mode", "Unknown authentication mode.");
        Require(!tunnel.Enabled || authentication.Mode == AuthenticationMode.CloudflareAccess,
            "tunnel.enabled", "A managed tunnel requires CloudflareAccess authentication.");

        if (authentication.Mode == AuthenticationMode.CloudflareAccess)
        {
            Require(IsHttpsOrigin(authentication.PublicOrigin), "authentication.publicOrigin",
                "Use an HTTPS origin with a host and optional port, without credentials, a path, query or fragment.");
            Require(IsTeamPrefix(authentication.Team), "authentication.team",
                "Use a team prefix of 1 to 63 ASCII letters, digits or hyphens, starting and ending with a letter or digit.");
            Require(IsSingleValue(authentication.Audience), "authentication.audience",
                "An application AUD tag is required and must not contain whitespace or control characters.");
        }
        Require(!tunnel.Enabled || !string.IsNullOrWhiteSpace(tunnel.TokenFilePath),
            "tunnel.tokenFilePath", "A token file path is required when the tunnel is enabled.");
    }

    /// <summary>
    /// Returns copies with absolute paths and the normalized source path, without changing drafts or creating files.
    /// Authentication, dependency and other business checks are performed separately.
    /// </summary>
    public (AppSettings Settings, ExecutionPolicy Policy, string SettingsFilePath) NormalizePaths(
        AppSettings settings, string settingsFile, ExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(policy);
        if (settings.Tunnel is null) throw new ConfigurationValidationException("tunnel", "Must not be null.");

        var source = Field("settingsFile", () =>
        {
            if (!Path.IsPathFullyQualified(settingsFile)) throw new ArgumentException("Use an absolute configuration file path.");
            var path = Path.GetFullPath(settingsFile);
            CheckStoragePath(path, directory: false);
            return path;
        });
        var root = Field("projectRoot", () => ProjectWorkspace.ValidateRoot(
            ConfigurationPaths.ResolveValuePath(settings.ProjectRoot, source)));
        Field("policyFile", () =>
        {
            CheckStoragePath(_paths.PolicyFile, directory: false);
            if (Contains(root, _paths.PolicyFile))
                throw new InvalidOperationException("The local policy file must be outside the project root.");
            return _paths.PolicyFile;
        });
        var recovery = Field("recoveryDirectory", () =>
        {
            var path = Path.TrimEndingDirectorySeparator(policy.RecoveryDirectory == ""
                ? _paths.RecoveryDirectory : ConfigurationPaths.ResolveValuePath(policy.RecoveryDirectory, _paths.PolicyFile));
            CheckStoragePath(path, directory: true);
            if (Contains(root, path) || Contains(path, root))
                throw new InvalidOperationException("The recovery directory and project root must not contain one another.");
            return path;
        });

        if (Contains(source, _paths.PolicyFile) || Contains(source, recovery))
            throw new ConfigurationValidationException("settingsFile", "A settings file must not occupy the local policy file or a required storage directory.");

        string OptionalPath(string field, string path) => path == "" ? ""
            : Field(field, () => ConfigurationPaths.ResolveValuePath(path, source));
        return (settings with
        {
            ProjectRoot = root,
            BashPath = OptionalPath("bashPath", settings.BashPath),
            RgPath = OptionalPath("rgPath", settings.RgPath),
            Tunnel = settings.Tunnel with
            {
                ExecutablePath = OptionalPath("tunnel.executablePath", settings.Tunnel.ExecutablePath),
                TokenFilePath = OptionalPath("tunnel.tokenFilePath", settings.Tunnel.TokenFilePath)
            }
        }, policy with { RecoveryDirectory = recovery }, source);
    }

    private static string CheckProgram(string? path, string name)
    {
        if (path is null) throw new InvalidOperationException($"Could not locate {name}; install it or configure its executable path.");
        if (!Path.IsPathFullyQualified(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new InvalidOperationException($"The {name} path must name an existing program file using an absolute path.");
        if (OperatingSystem.IsLinux())
        {
            const UnixFileMode executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(path) & executable) == 0)
                throw new InvalidOperationException($"The {name} file must have a Unix execute permission bit set.");
        }
        return path;
    }

    private static bool IsHttpsOrigin(string? value)
    {
        if (value is null || !IsSingleValue(value) || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.Contains('\\') || value.Contains('@')) return false;
        var authority = value.AsSpan("https://".Length);
        var delimiter = authority.IndexOfAny('/', '?', '#');
        // Inspect the original suffix as Uri otherwise normalizes paths such as /mcp/.. to /.
        if (delimiter >= 0 && !authority[delimiter..].SequenceEqual("/".AsSpan())) return false;
        return Uri.TryCreate(value, UriKind.Absolute, out var origin)
            && origin.Scheme == Uri.UriSchemeHttps && origin.Port is >= 1 and <= 65535
            && origin.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
            && origin.UserInfo.Length == 0 && origin.AbsolutePath == "/"
            && origin.Query.Length == 0 && origin.Fragment.Length == 0;
    }

    private static bool IsTeamPrefix(string? value) => value is { Length: > 0 and <= 63 }
        && char.IsAsciiLetterOrDigit(value[0]) && char.IsAsciiLetterOrDigit(value[^1])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsSingleValue(string? value) => !string.IsNullOrEmpty(value)
        && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    private static void Require(bool condition, string field, string message)
    {
        if (!condition) throw new ConfigurationValidationException(field, message);
    }

    private static bool Contains(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static void CheckStoragePath(string path, bool directory)
    {
        for (var part = new DirectoryInfo(path); part is not null; part = part.Parent)
        {
            try
            {
                var attributes = File.GetAttributes(part.FullName);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Configuration and recovery paths must not contain symbolic links or junctions.");
                if (((attributes & FileAttributes.Directory) != 0) != directory)
                    throw new InvalidOperationException(directory ? "A directory path is occupied by a file." : "A configuration file path is occupied by a directory.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            directory = true;
        }
    }

    private static T Field<T>(string field, Func<T> action)
    {
        try { return action(); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or WorkspaceException)
        {
            throw new ConfigurationValidationException(field, exception.Message, exception);
        }
    }
}
