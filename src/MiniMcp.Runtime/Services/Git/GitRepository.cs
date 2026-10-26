using MiniMcp.Services.Processes;
using MiniMcp.Services.Workspace;
using System.Security.Cryptography;
using MiniMcp.Recovery;

namespace MiniMcp.Services.Git;

// One bounded inspection of a standard, in-root repository. No worktree/gitdir discovery,
// config editor, Git object implementation, temporary repository or permission bypass.
internal sealed class GitRepository : IDisposable
{
    private readonly ProjectWorkspace _workspace;
    private readonly FileStream _config;
    private readonly byte[] _configHash;
    private readonly RecoveryFileIdentity _directoryIdentity, _configIdentity;
    private Dictionary<string, MetadataFact?> _controlFiles = new();
    internal string GitDirectory { get; }
    internal string Executable { get; }
    internal string Root => _workspace.Root;
    private static string NullFile => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
    private static readonly IReadOnlyDictionary<string, string> EnvironmentValues = new Dictionary<string, string>
    {
        ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_SYSTEM"] = NullFile, ["GIT_CONFIG_GLOBAL"] = NullFile,
        ["GIT_ATTR_NOSYSTEM"] = "1", ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_NO_LAZY_FETCH"] = "1",
        ["GIT_NO_REPLACE_OBJECTS"] = "1", ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C"
    };

    private GitRepository(ProjectWorkspace workspace, string executable)
    {
        _workspace = workspace;
        Executable = executable;
        GitDirectory = Path.Combine(Root, ".git");
        workspace.Resolve(".");
        if (!Directory.Exists(GitDirectory) || File.Exists(GitDirectory))
            throw new WorkspaceException("Git queries require projectRoot itself to be a standard worktree with an in-root .git directory; parent discovery, linked worktrees and bare repositories are unsupported.");
        _directoryIdentity = RecoveryFileSystem.DirectoryIdentity(GitDirectory);
        CheckRedirects();
        _config = RecoveryFileSystem.OpenSharedRead(Path.Combine(GitDirectory, "config"));
        try
        {
            _configIdentity = RecoveryFileSystem.FileIdentity(_config);
            _configHash = ReadConfigHash();
        }
        catch { _config.Dispose(); throw; }
    }

    internal static async Task<GitRepository> OpenAsync(ProjectWorkspace workspace, string executable, CancellationToken token)
    {
        GitRepository? repository = null;
        try
        {
            repository = new(workspace, executable);
            await Task.Run(() => repository.InspectMetadata(token), token).ConfigureAwait(false);
            repository.CaptureControlFiles();
            // `git config` performs gentle repository discovery before parsing --file. Run inside
            // the inspected object directory with a ceiling at its parent, so even that setup
            // cannot read an ambient/target repository config before --no-includes takes effect.
            var inspectionEnvironment = new Dictionary<string, string>(EnvironmentValues)
            { ["GIT_CEILING_DIRECTORIES"] = repository.GitDirectory };
            var result = await ReadOnlyQueryRunner.RunAsync(executable, Path.Combine(repository.GitDirectory, "objects"),
                ["--no-pager", "--no-optional-locks", "--no-lazy-fetch", "config", "--null", "--no-includes",
                    "--file", Path.Combine(repository.GitDirectory, "config"), "--list"], '\0', 65536, 128 * 1024,
                record => { ValidateConfig(record); return true; }, token, inspectionEnvironment).ConfigureAwait(false);
            CheckResult(result);
            repository.Validate();
            return repository;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            repository?.Dispose();
            throw new WorkspaceException("Git metadata is unavailable or unsafe (links, special files, hard links or concurrent changes); this repository is unsupported for read-only queries.", error);
        }
        catch { repository?.Dispose(); throw; }
    }

    internal async Task<QueryProcessResult> RunAsync(IEnumerable<string> arguments, char separator, int frameLimit,
        int outputLimit, Func<string, bool> accept, CancellationToken token)
    {
        Validate();
        List<string> fixedArguments = ["--no-pager", "--no-optional-locks", "--no-lazy-fetch", "--no-replace-objects",
            "--git-dir=" + GitDirectory, "--work-tree=" + Root];
        foreach (var setting in new[] { "protocol.allow=never", "credential.helper=", "core.fsmonitor=false",
            "core.untrackedCache=false", "core.attributesFile=" + NullFile, "core.excludesFile=" + NullFile,
            "core.quotePath=true", "color.ui=false", "diff.external=", "diff.renames=false", "diff.trustExitCode=false",
            "submodule.recurse=false", "fetch.recurseSubmodules=false", "gc.auto=0", "maintenance.auto=false",
            "log.showSignature=false", "i18n.logOutputEncoding=UTF-8", "index.threads=1" })
        { fixedArguments.Add("-c"); fixedArguments.Add(setting); }
        fixedArguments.AddRange(arguments);
        var result = await ReadOnlyQueryRunner.RunAsync(Executable, Root, fixedArguments, separator, frameLimit,
            outputLimit, accept, token, EnvironmentValues).ConfigureAwait(false);
        Validate();
        return result;
    }

    internal static IEnumerable<string> Pathspecs(string scope)
    {
        yield return ":(top,literal)" + (scope == "." ? "" : scope);
        // No caller-supplied pathspec magic; private exclusions always follow the literal scope.
        foreach (var pattern in ProjectWorkspace.PrivateNames.Concat([ProjectWorkspace.PrivatePrefix + "*"])
            .Concat(ProjectWorkspace.PrivateExtensions.Select(extension => "*" + extension)))
        {
            yield return ":(top,exclude,icase,glob)**/" + pattern;
            yield return ":(top,exclude,icase,glob)**/" + pattern + "/**";
        }
    }

    internal static bool IsHash(string value) => value.Length is 40 or 64 && value.All(character => char.IsAsciiHexDigit(character));
    internal static void CheckResult(QueryProcessResult result, bool allowLimited = false)
    {
        if (allowLimited && result.OutputLimited) return;
        if (result.OutputLimited) throw new WorkspaceException("Git metadata exceeded its inspection limit; no complete query result is available.");
        if (result.ExitCode != 0 || result.HasErrors)
            throw new WorkspaceException("Git query failed: unsupported arguments/configuration, unreadable metadata or missing/corrupt local objects. No fetch or external helper was attempted.");
    }

    private void Validate()
    {
        _workspace.Resolve(".");
        if (RecoveryFileSystem.DirectoryIdentity(GitDirectory) != _directoryIdentity)
            throw new WorkspaceException("The repository directory changed during the query; retry.");
        CheckRedirects();
        foreach (var (file, fact) in _controlFiles)
            if (ObserveMetadata(file) != fact) throw new WorkspaceException("Git HEAD, refs or index changed during the query; retry.");
        using var current = RecoveryFileSystem.OpenSharedRead(Path.Combine(GitDirectory, "config"));
        if (RecoveryFileSystem.FileIdentity(current) != _configIdentity || !ReadConfigHash().SequenceEqual(_configHash))
            throw new WorkspaceException("The repository configuration changed during the query; retry.");
    }

    private void CheckRedirects()
    {
        foreach (var relative in new[] { "commondir", "gitdir", "config.worktree", "worktrees", "info/grafts",
            "objects/info/alternates", "objects/info/http-alternates", "objects/.git", "objects/HEAD" })
        {
            try
            {
                _ = File.GetAttributes(Path.Combine(GitDirectory, relative));
                throw new WorkspaceException("Git redirects, alternates, linked worktrees, grafts and per-worktree configuration are unsupported.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private void InspectMetadata(CancellationToken token)
    {
        var work = 0;
        void Walk(string directory, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 32) throw new WorkspaceException("Git metadata exceeds the supported inspection depth.");
            _ = RecoveryFileSystem.DirectoryIdentity(directory);
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++work > QueryText.MaxWork) throw new WorkspaceException("Git metadata exceeds the 10000-entry inspection budget.");
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new WorkspaceException("Linked Git metadata is unsupported.");
                if ((attributes & FileAttributes.Directory) != 0) Walk(item, depth + 1);
                else
                {
                    if (item.EndsWith(".promisor", StringComparison.OrdinalIgnoreCase))
                        throw new WorkspaceException("Partial clone/promisor repositories are unsupported; queries never fetch missing objects.");
                    using var stream = RecoveryFileSystem.OpenSharedRead(item);
                    if (Path.GetFileName(item) == "index" && stream.Length > 16 * 1024 * 1024)
                        throw new WorkspaceException("The Git index exceeds the supported 16 MiB inspection limit.");
                }
            }
        }
        Walk(GitDirectory, 0);
        using var head = RecoveryFileSystem.OpenSharedRead(Path.Combine(GitDirectory, "HEAD"));
        if (head.Length is < 1 or > 4096) throw new WorkspaceException("Invalid Git HEAD metadata.");
        _ = RecoveryFileSystem.DirectoryIdentity(Path.Combine(GitDirectory, "objects"));
    }

    private byte[] ReadConfigHash()
    {
        var length = _config.Length;
        if (length > 65536) throw new WorkspaceException("Git config exceeds the supported 64 KiB limit.");
        _config.Position = 0;
        var bytes = new byte[(int)length];
        _config.ReadExactly(bytes);
        return SHA256.HashData(bytes);
    }

    private void CaptureControlFiles()
    {
        var files = new List<string> { Path.Combine(GitDirectory, "HEAD"), Path.Combine(GitDirectory, "index"), Path.Combine(GitDirectory, "packed-refs") };
        using var stream = RecoveryFileSystem.OpenSharedRead(files[0]);
        if (stream.Length is < 1 or > 4096) throw new WorkspaceException("Invalid HEAD metadata size.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        var head = ProjectWorkspace.Utf8.GetString(bytes).TrimEnd('\r', '\n');
        if (head.StartsWith("ref: refs/", StringComparison.Ordinal))
        {
            var relative = head[5..];
            if (relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(part => part is "" or "." or ".."))
                throw new WorkspaceException("Unsupported HEAD reference layout.");
            files.Add(Path.GetFullPath(Path.Combine(GitDirectory, relative)));
        }
        else if (!IsHash(head)) throw new WorkspaceException("Invalid HEAD reference metadata.");
        _controlFiles = files.ToDictionary(file => file, ObserveMetadata, StringComparer.Ordinal);
    }

    private static MetadataFact? ObserveMetadata(string path)
    {
        try
        {
            using var file = RecoveryFileSystem.OpenSharedRead(path);
            return new(RecoveryFileSystem.FileIdentity(file), file.Length, File.GetLastWriteTimeUtc(path));
        }
        catch (IOException) when (!File.Exists(path) && !Directory.Exists(path)) { return null; }
    }
    private sealed record MetadataFact(RecoveryFileIdentity Identity, long Length, DateTime Modified);

    private static void ValidateConfig(string record)
    {
        var separator = record.IndexOf('\n');
        var key = (separator < 0 ? record : record[..separator]).ToLowerInvariant();
        var value = separator < 0 ? "true" : record[(separator + 1)..];
        bool Boolean(params string[] allowed) => allowed.Contains(value.ToLowerInvariant(), StringComparer.Ordinal);
        var safe = key switch
        {
            "core.repositoryformatversion" => value is "0" or "1",
            "core.bare" => Boolean("false", "no", "off", "0"),
            "core.fsmonitor" or "core.sparsecheckout" or "core.sparsecheckoutcone" => Boolean("false", "no", "off", "0"),
            "core.filemode" or "core.logallrefupdates" or "core.symlinks" or "core.ignorecase" or "core.precomposeunicode" or
                "core.quotepath" or "core.protectntfs" or "core.protecthfs" => Boolean("false", "true", "yes", "no", "on", "off", "0", "1"),
            "core.autocrlf" => Boolean("true", "false", "input"),
            "core.eol" => value is "lf" or "crlf" or "native",
            "core.safecrlf" => Boolean("true", "false", "warn"),
            "extensions.objectformat" => value is "sha1" or "sha256",
            "user.name" or "user.email" or "user.signingkey" => true,
            _ => (key.StartsWith("remote.", StringComparison.Ordinal) && (key.EndsWith(".url", StringComparison.Ordinal) ||
                    key.EndsWith(".pushurl", StringComparison.Ordinal) || key.EndsWith(".fetch", StringComparison.Ordinal) || key.EndsWith(".tagopt", StringComparison.Ordinal))) ||
                (key.StartsWith("branch.", StringComparison.Ordinal) && (key.EndsWith(".remote", StringComparison.Ordinal) ||
                    key.EndsWith(".merge", StringComparison.Ordinal) || key.EndsWith(".rebase", StringComparison.Ordinal)))
        };
        if (!safe) throw new WorkspaceException("Unsupported repository configuration: read-only queries permit only documented basic core/user/branch/remote settings. Includes, external filters/diff/fsmonitor, redirects and unknown settings are rejected before querying.");
    }

    public void Dispose() => _config.Dispose();
}
