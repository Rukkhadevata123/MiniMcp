using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;

namespace MiniMcp.Services;

public sealed record GitStatusEntry(string Path, string IndexStatus, string WorktreeStatus);
public sealed record GitStatusResult(string Scope, ImmutableArray<GitStatusEntry> Entries,
    bool Complete, bool Truncated, ImmutableArray<string> Notices);
public sealed record GitDiffFile(string Path, string Status, ImmutableArray<string> Lines);
public sealed record GitDiffResult(string Scope, bool Staged, ImmutableArray<GitDiffFile> Files,
    bool Complete, bool Truncated, ImmutableArray<string> Notices);
public sealed record GitLogEntry(string Commit, string Author, string AuthoredAt, string Subject);
public sealed record GitLogResult(string Scope, string? Head, ImmutableArray<GitLogEntry> Commits,
    bool Complete, bool Truncated, ImmutableArray<string> Notices);

internal sealed class GitQueryService(ProjectWorkspace workspace, RuntimeConfiguration configuration, ReadOnlyQueryRunner runner)
{
    private const string ScopeNotice = "Scope: literal project paths; private paths, links and submodule contents excluded; no external helpers, global/system config or network; live observation, not a transaction snapshot.";

    internal Task<GitStatusResult> StatusAsync(string path, int maxEntries, CancellationToken cancellationToken)
    {
        QueryText.Range(maxEntries, 1, 2000, "max_entries");
        return ExecuteAsync(path, async (repository, scope, token) =>
        {
            var entries = new List<GitStatusEntry>();
            var notices = new HashSet<string>();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(scope).Length;
            var result = await repository.RunAsync(["status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignored=no",
                "--no-renames", "--ignore-submodules=all", "--", .. GitRepository.Pathspecs(scope)], '\0', 8192, 2 * 1024 * 1024,
                record =>
                {
                    token.ThrowIfCancellationRequested();
                    string pathValue, status;
                    if (record.StartsWith("? ", StringComparison.Ordinal)) { pathValue = record[2..]; status = "??"; }
                    else if (record.StartsWith("1 ", StringComparison.Ordinal) || record.StartsWith("u ", StringComparison.Ordinal))
                    {
                        var unmerged = record[0] == 'u';
                        var fields = record.Split(' ', unmerged ? 11 : 9);
                        if (fields.Length != (unmerged ? 11 : 9) || fields[1].Length != 2 || fields[1].Any(value => !". MADU?!T".Contains(value)))
                            throw new WorkspaceException("Git returned an unsupported status record.");
                        if (fields.Skip(3).Take(unmerged ? 4 : 3).Any(mode => !RegularMode(mode)))
                        { notices.Add("NonRegularChangesSkipped"); return true; }
                        pathValue = fields[^1]; status = fields[1].Replace('.', ' ');
                    }
                    else throw new WorkspaceException("Git returned an unsupported status record (renames must be disabled).");
                    var relative = SafeResultPath(pathValue, scope, notices);
                    if (relative is null) return true;
                    var entry = new GitStatusEntry(relative, status[..1], status.Substring(1, 1));
                    var cost = JsonSerializer.SerializeToUtf8Bytes(entry).Length + 1;
                    if (entries.Count == maxEntries || bytes + cost > QueryText.MaxResultBytes - 8192) return false;
                    entries.Add(entry); bytes += cost;
                    return true;
                }, token).ConfigureAwait(false);
            GitRepository.CheckResult(result, allowLimited: true);
            if (result.OutputLimited) notices.Add("EntryOrOutputLimit");
            return new GitStatusResult(scope, entries.ToImmutableArray(), notices.Count == 0, result.OutputLimited, Notices(notices));
        }, cancellationToken);
    }

    internal Task<GitDiffResult> DiffAsync(string path, bool staged, int maxFiles, int maxLines, CancellationToken cancellationToken)
    {
        QueryText.Range(maxFiles, 1, 100, "max_files");
        QueryText.Range(maxLines, 1, 4000, "max_lines");
        return ExecuteAsync(path, async (repository, scope, token) =>
        {
            var notices = new HashSet<string>();
            var candidates = new List<DiffCandidate>();
            string[]? pending = null;
            var raw = await repository.RunAsync(["diff", "--raw", "-z", "--no-abbrev", "--no-renames", "--no-ext-diff", "--no-textconv",
                "--ignore-submodules=all", .. staged ? new[] { "--cached" } : [], "--", .. GitRepository.Pathspecs(scope)],
                '\0', 8192, 2 * 1024 * 1024, record =>
                {
                    token.ThrowIfCancellationRequested();
                    if (pending is null)
                    {
                        pending = record.Split(' ');
                        if (pending.Length != 5 || !pending[0].StartsWith(':') || !GitRepository.IsHash(pending[2]) ||
                            !GitRepository.IsHash(pending[3]) || pending[4].Length != 1 || !"ADMTUX".Contains(pending[4][0]))
                            throw new WorkspaceException("Git returned unsupported diff metadata.");
                        return true;
                    }
                    var fields = pending; pending = null;
                    var relative = SafeResultPath(record, scope, notices);
                    if (relative is null) return true;
                    if (!RegularMode(fields[0][1..]) || !RegularMode(fields[1]) || fields[4] is "U" or "X")
                    { notices.Add("NonRegularOrUnmergedChangesSkipped"); return true; }
                    if (candidates.Count == maxFiles) return false;
                    candidates.Add(new(relative, fields[4], fields[2], fields[3]));
                    return true;
                }, token).ConfigureAwait(false);
            GitRepository.CheckResult(raw, allowLimited: true);
            if (pending is not null && !raw.OutputLimited) throw new WorkspaceException("Git returned incomplete diff metadata.");
            var truncated = raw.OutputLimited;
            if (truncated) notices.Add("FileOrMetadataLimit");
            var files = new List<GitDiffFile>();
            var linesRemaining = maxLines;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(scope).Length;
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                FileObservation? before;
                try { before = await ObserveFileAsync(candidate.Path, token).ConfigureAwait(false); }
                catch (WorkspaceException error) when (error.Message.Contains("UTF-8", StringComparison.Ordinal))
                { notices.Add("BinaryOrNonUtf8FilesSkipped"); continue; }
                if (before is { Length: > ProjectWorkspace.MaxFileBytes }) { notices.Add("OversizedFilesSkipped"); continue; }
                var blobs = new[] { candidate.Before, candidate.After }.Where(hash => hash.Any(character => character != '0')).Distinct();
                var oversized = false;
                foreach (var blob in blobs)
                {
                    long? length = null;
                    var size = await repository.RunAsync(["cat-file", "-s", blob], '\n', 64, 128,
                        value =>
                        {
                            if (length is not null || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
                                throw new WorkspaceException("Git returned invalid object size metadata.");
                            length = parsed; return true;
                        }, token).ConfigureAwait(false);
                    GitRepository.CheckResult(size);
                    if (length is null) throw new WorkspaceException("Git returned no object size metadata.");
                    oversized |= length > ProjectWorkspace.MaxFileBytes;
                }
                if (oversized) { notices.Add("OversizedObjectsSkipped"); continue; }
                var lines = new List<string>();
                var pathBytes = JsonSerializer.SerializeToUtf8Bytes(candidate.Path).Length;
                if (bytes + pathBytes + 256 > QueryText.MaxResultBytes - 8192)
                { truncated = true; notices.Add("OutputLimit"); break; }
                bytes += pathBytes + 256;
                var patch = await repository.RunAsync(["diff", "--patch", "--no-ext-diff", "--no-textconv", "--no-renames",
                    "--no-color", "--no-prefix", "--unified=3", "--ignore-submodules=all",
                    .. staged ? new[] { "--cached" } : [], "--", .. GitRepository.Pathspecs(candidate.Path)], '\n', 2 * 1024 * 1024,
                    8 * 1024 * 1024, line =>
                    {
                        token.ThrowIfCancellationRequested();
                        var display = QueryText.Display(ProjectWorkspace.Clip(line.TrimEnd('\r'), 2000));
                        if (line.Length > 2000 || display.Length > 2000) notices.Add("LinePreviewsClipped");
                        display = ProjectWorkspace.Clip(display, 2000);
                        var cost = JsonSerializer.SerializeToUtf8Bytes(display).Length + 1;
                        if (linesRemaining == 0 || bytes + cost > QueryText.MaxResultBytes - 8192) return false;
                        linesRemaining--; bytes += cost; lines.Add(display);
                        return true;
                    }, token).ConfigureAwait(false);
                GitRepository.CheckResult(patch, allowLimited: true);
                if (before != await ObserveFileAsync(candidate.Path, token).ConfigureAwait(false))
                    throw new WorkspaceException("A diff file changed during the query; retry for a fresh result.");
                files.Add(new(candidate.Path, candidate.Status, lines.ToImmutableArray()));
                if (patch.OutputLimited) { truncated = true; notices.Add("LineOrOutputLimit"); break; }
            }
            return new GitDiffResult(scope, staged, files.ToImmutableArray(), notices.Count == 0, truncated, Notices(notices));
        }, cancellationToken);
    }

    internal Task<GitLogResult> LogAsync(string path, int maxCount, CancellationToken cancellationToken)
    {
        QueryText.Range(maxCount, 1, 100, "max_count");
        return ExecuteAsync(path, async (repository, scope, token) =>
        {
            string? head = null;
            var resolution = await repository.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD"], '\n', 128, 256,
                value =>
                {
                    if (head is not null || !GitRepository.IsHash(value)) throw new WorkspaceException("Git returned invalid HEAD metadata.");
                    head = value; return true;
                }, token).ConfigureAwait(false);
            if (resolution.ExitCode == 1 && !resolution.HasErrors && !resolution.OutputLimited)
                return new GitLogResult(scope, null, [], true, false, [ScopeNotice, "NoHeadCommit: HEAD does not resolve to a commit (for example, an unborn branch)."]);
            GitRepository.CheckResult(resolution);
            if (head is null) throw new WorkspaceException("Git returned no HEAD metadata.");
            var commits = new List<GitLogEntry>();
            var fields = new List<string>(4);
            var notices = new HashSet<string>();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(scope).Length;
            var result = await repository.RunAsync(["log", "--no-show-signature", "--no-decorate", "--no-notes", "--no-color",
                "--encoding=UTF-8", "--format=%H%x00%an%x00%aI%x00%s", "-z", "--max-count=" + (maxCount + 1).ToString(CultureInfo.InvariantCulture),
                head, "--", .. GitRepository.Pathspecs(scope)], '\0', ProjectWorkspace.MaxFileBytes * 2, 2 * 1024 * 1024,
                value =>
                {
                    token.ThrowIfCancellationRequested();
                    fields.Add(value);
                    if (fields.Count < 4) return true;
                    if (!GitRepository.IsHash(fields[0]) || !DateTimeOffset.TryParse(fields[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        throw new WorkspaceException("Git returned unsupported commit metadata.");
                    string Display(string input)
                    {
                        var text = QueryText.Display(ProjectWorkspace.Clip(input, 2000));
                        if (input.Length > 2000 || text.Length > 2000) notices.Add("CommitMetadataClipped");
                        return ProjectWorkspace.Clip(text, 2000);
                    }
                    var entry = new GitLogEntry(fields[0], Display(fields[1]), fields[2], Display(fields[3]));
                    fields.Clear();
                    var cost = JsonSerializer.SerializeToUtf8Bytes(entry).Length + 1;
                    if (commits.Count == maxCount || bytes + cost > QueryText.MaxResultBytes - 8192) return false;
                    commits.Add(entry); bytes += cost; return true;
                }, token).ConfigureAwait(false);
            GitRepository.CheckResult(result, allowLimited: true);
            if (fields.Count != 0 && !result.OutputLimited) throw new WorkspaceException("Git returned incomplete commit metadata.");
            if (result.OutputLimited) notices.Add("CommitOrOutputLimit");
            return new GitLogResult(scope, head, commits.ToImmutableArray(), notices.Count == 0, result.OutputLimited, Notices(notices));
        }, cancellationToken);
    }

    private Task<T> ExecuteAsync<T>(string path, Func<GitRepository, string, CancellationToken, Task<T>> query, CancellationToken cancellationToken)
    {
        var scope = workspace.Relative(QueryText.ValidatePath(workspace, path));
        string? executable;
        try { executable = ExternalProgramLocator.FindGit(configuration.Settings.GitPath, configuration.SettingsFilePath, workspace.Root); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { throw new WorkspaceException("Git queries are unavailable: configure gitPath to an existing trusted Git program. Other tools remain available."); }
        if (executable is null) throw new WorkspaceException("Git queries require a local Git installation or an explicit gitPath. No program is downloaded automatically.");
        return runner.ExecuteAsync(async token =>
        {
            using var repository = await GitRepository.OpenAsync(workspace, executable, token).ConfigureAwait(false);
            return await query(repository, scope, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private string? SafeResultPath(string path, string scope, HashSet<string> notices)
    {
        try
        {
            var relative = workspace.Relative(workspace.Resolve(path));
            if (scope != "." && !relative.Equals(scope, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                !relative.StartsWith(scope + "/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new WorkspaceException("Git returned a path outside the requested literal scope.");
            return relative;
        }
        catch (WorkspaceException) { notices.Add("PrivateLinkedOrUnsupportedPathsSkipped"); return null; }
    }

    private async Task<FileObservation?> ObserveFileAsync(string path, CancellationToken token)
    {
        var full = workspace.Resolve(path);
        if (!File.Exists(full))
        {
            if (Directory.Exists(full)) throw new WorkspaceException("A diff file path is occupied by a directory.");
            return null;
        }
        using var stream = RecoveryFileSystem.OpenSharedRead(full);
        var identity = RecoveryFileSystem.FileIdentity(stream);
        var length = stream.Length;
        token.ThrowIfCancellationRequested();
        // Observe bytes for a stable result even when an editor preserves the file's timestamp.
        if (length > ProjectWorkspace.MaxFileBytes) return new(identity, length, "", null);
        var image = await workspace.SnapshotRegularAsync(full, token).ConfigureAwait(false);
        return new(identity, length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Bytes.AsSpan())), image.Mode);
    }
    private static bool RegularMode(string mode) => mode is "000000" or "100644" or "100755";
    private static ImmutableArray<string> Notices(HashSet<string> notices) => [ScopeNotice, .. notices.Order()];
    private sealed record DiffCandidate(string Path, string Status, string Before, string After);
    private sealed record FileObservation(RecoveryFileIdentity Identity, long Length, string Hash, UnixFileMode? Mode);
}
