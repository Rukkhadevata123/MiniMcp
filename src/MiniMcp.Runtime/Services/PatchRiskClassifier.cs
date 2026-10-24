using System.Collections.Immutable;
using MiniMcp.Configuration;

namespace MiniMcp.Services;

internal enum PatchFileKind { Ordinary, Critical, Unknown }

[Flags]
internal enum PatchRiskReason
{
    None = 0,
    CriticalFile = 1,
    UnknownFileType = 2,
    Delete = 4,
    Move = 8,
    PathLimitExceeded = 16,
    ChangedLineLimitExceeded = 32,
    ChangedByteLimitExceeded = 64
}

internal sealed record PatchChangeRisk(PatchChange Summary, PatchFileKind Kind, PatchRiskReason Reasons);

// Classification describes this exact snapshot. It is neither approval nor permission to commit it.
internal sealed record PatchRiskAssessment(PatchPreview Preview, ImmutableArray<PatchChangeRisk> Changes,
    PatchBatchSize Size, PatchApprovalLimits Limits, PatchRiskReason Reasons)
{
    public PatchPlan Plan => Preview.Plan;
    public bool RequiresApproval => Reasons != PatchRiskReason.None;
}

internal static class PatchRiskClassifier
{
    // Fixed code tables, never repository-supplied policy. Case-insensitive on every platform.
    private static readonly HashSet<string> OrdinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".fsi", ".vb", ".xaml", ".axaml", ".razor", ".cshtml",
        ".c", ".h", ".cc", ".hh", ".cpp", ".hpp", ".cxx", ".hxx",
        ".rs", ".go", ".java", ".kt", ".py", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx",
        ".html", ".htm", ".css", ".scss", ".sass", ".less",
        ".md", ".markdown", ".txt", ".rst", ".adoc"
    };

    private static readonly HashSet<string> DocumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "README", "LICENSE", "COPYING", "NOTICE", "AUTHORS", "CHANGELOG", "CONTRIBUTING"
    };

    private static readonly HashSet<string> CriticalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".vbs", ".wsf", ".csx", ".fsx", ".kts",
        ".sln", ".slnx", ".slnf", ".csproj", ".fsproj", ".vbproj", ".vcxproj", ".wixproj", ".proj", ".props", ".targets",
        ".mk", ".mak", ".cmake", ".gradle"
    };

    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.json", "NuGet.Config", "packages.config", "packages.lock.json",
        "Makefile", "GNUmakefile", "CMakeLists.txt", "CMakePresets.json", "CMakeUserPresets.json", "Justfile", ".justfile", "Taskfile.yml", "Taskfile.yaml",
        "Dockerfile", "Containerfile", "compose.yml", "compose.yaml", "docker-compose.yml", "docker-compose.yaml", ".dockerignore",
        ".gitlab-ci.yml", ".gitlab-ci.yaml", "Jenkinsfile", "azure-pipelines.yml", "azure-pipelines.yaml",
        "package.json", "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", ".npmrc", ".yarnrc", ".yarnrc.yml",
        "pyproject.toml", "setup.py", "setup.cfg", "tox.ini", "Pipfile", "Pipfile.lock", "poetry.lock", "uv.lock",
        "Cargo.toml", "Cargo.lock", "build.rs", "rust-toolchain", "rust-toolchain.toml", "go.mod", "go.sum", "go.work", "go.work.sum",
        "pom.xml", "gradlew", "gradle.properties", "Gemfile", "Gemfile.lock", "Rakefile",
        ".profile", ".bashrc", ".bash_profile", ".bash_login", ".bash_logout", ".zshrc", ".zshenv", ".zprofile", ".zlogin", ".zlogout",
        ".gitignore", ".gitattributes", ".gitmodules"
    };

    private static readonly HashSet<string> CriticalDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".github", ".gitlab", ".circleci", ".buildkite", ".devcontainer", ".vscode", ".idea"
    };

    public static PatchRiskAssessment Classify(PatchPreview preview, string settingsFilePath,
        PatchApprovalLimits limits, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(limits);
        if (!Path.IsPathFullyQualified(settingsFilePath))
            throw new ArgumentException("The validated settings file path must be absolute.", nameof(settingsFilePath));
        var plan = preview.Plan;
        var changes = ImmutableArray.CreateBuilder<PatchChangeRisk>(plan.Changes.Length);
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var total = PatchRiskReason.None;
        foreach (var change in plan.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths.Add(change.Source);
            paths.Add(change.Destination);
            var reasons = PathReason(change.Summary.Path);
            if (change.Summary.MoveTo is { } destination) reasons |= PathReason(destination);
            // The selected settings file can have any name, including an ordinary source/document suffix.
            if (string.Equals(change.Source, settingsFilePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(change.Destination, settingsFilePath, StringComparison.OrdinalIgnoreCase))
                reasons |= PatchRiskReason.CriticalFile;
            if (HasShebang(change.Before?.Bytes ?? []) || HasShebang(change.After ?? []) ||
                (change.Before?.Mode is { } mode &&
                 (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
                reasons |= PatchRiskReason.CriticalFile;
            reasons |= change.Summary.Action switch
            {
                "add" or "update" => PatchRiskReason.None,
                "delete" => PatchRiskReason.Delete,
                "move" => PatchRiskReason.Move,
                _ => throw new InvalidOperationException("Unexpected validated patch action.")
            };
            var kind = (reasons & PatchRiskReason.CriticalFile) != 0 ? PatchFileKind.Critical :
                (reasons & PatchRiskReason.UnknownFileType) != 0 ? PatchFileKind.Unknown : PatchFileKind.Ordinary;
            changes.Add(new(change.Summary, kind, reasons));
            total |= reasons;
        }
        // Use exactly the complete preview's changed-content statistics, never file sizes or display length.
        var size = new PatchBatchSize(paths.Count, checked((long)preview.Statistics.AddedLines + preview.Statistics.RemovedLines),
            checked(preview.Statistics.AddedBytes + preview.Statistics.RemovedBytes));
        if (size.Paths > limits.MaxPaths) total |= PatchRiskReason.PathLimitExceeded;
        if (size.ChangedLines > limits.MaxChangedLines) total |= PatchRiskReason.ChangedLineLimitExceeded;
        if (size.ChangedBytes > limits.MaxChangedBytes) total |= PatchRiskReason.ChangedByteLimitExceeded;
        return new(preview, changes.MoveToImmutable(), size, limits, total);
    }

    private static PatchRiskReason PathReason(string path)
    {
        var parts = path.Split('/');
        // Treat Windows trailing-dot/space aliases conservatively, including on Linux.
        var names = parts.Select(part => part.TrimEnd(' ', '.')).ToArray();
        var name = names[^1];
        if (names.Any(CriticalDirectories.Contains) || CriticalNames.Contains(name) || CriticalExtensions.Contains(Path.GetExtension(name)) ||
            Starts(name, "Directory.Build.") || Starts(name, "Directory.Packages.") || Starts(name, "Dockerfile.") || Starts(name, "Containerfile.") ||
            IsJsonFamily(name, "settings") || IsJsonFamily(name, "policy") || IsJsonFamily(name, "appsettings") ||
            (Starts(name, "requirements") && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            return PatchRiskReason.CriticalFile;
        if (!parts.SequenceEqual(names)) return PatchRiskReason.UnknownFileType;
        return OrdinaryExtensions.Contains(Path.GetExtension(name)) || DocumentNames.Contains(name)
            ? PatchRiskReason.None : PatchRiskReason.UnknownFileType;
    }

    private static bool Starts(string name, string prefix) => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    private static bool IsJsonFamily(string name, string stem) => name.Equals(stem + ".json", StringComparison.OrdinalIgnoreCase) ||
        (Starts(name, stem + ".") && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    private static bool HasShebang(ImmutableArray<byte> content)
    {
        var bytes = content.AsSpan();
        if (bytes.StartsWith<byte>([0xef, 0xbb, 0xbf])) bytes = bytes[3..];
        return bytes.StartsWith<byte>([(byte)'#', (byte)'!']);
    }
}
