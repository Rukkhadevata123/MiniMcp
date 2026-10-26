using MiniMcp.Services.Processes;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;

namespace MiniMcp.Services.Workspace;

internal sealed class TextSearchService(ProjectWorkspace workspace, RuntimeConfiguration configuration, ReadOnlyQueryRunner runner)
{
    internal Task<TextSearchResult> SearchAsync(string pattern, string path, string? glob, bool regex,
        bool caseSensitive, int maxDepth, int maxResults, CancellationToken cancellationToken)
    {
        if (pattern.Length == 0 || pattern.Contains('\0') || Encoding.UTF8.GetByteCount(pattern) > 4096)
            throw new WorkspaceException("pattern must contain 1..4096 UTF-8 bytes without NUL.");
        if (glob is not null && (glob.Length == 0 || glob.Contains('\0') || glob.Contains('\\') || Encoding.UTF8.GetByteCount(glob) > 1024))
            throw new WorkspaceException("glob must contain 1..1024 UTF-8 bytes with / separators and no NUL.");
        QueryText.Range(maxDepth, 0, 32, "max_depth");
        QueryText.Range(maxResults, 1, 1000, "max_results");
        var root = QueryText.ValidatePath(workspace, path);
        if (!File.Exists(root) && !Directory.Exists(root)) throw new WorkspaceException("Search path does not exist.");
        string? executable;
        try { executable = ExternalProgramLocator.FindRipgrep(configuration.Settings.RgPath, configuration.SettingsFilePath, workspace.Root); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { throw new WorkspaceException("search_text is unavailable: configure rgPath to an existing ripgrep program. Other tools remain available."); }
        if (executable is null) throw new WorkspaceException("search_text requires ripgrep; install rg on an absolute PATH entry or configure rgPath. No program is downloaded automatically.");
        return runner.ExecuteAsync(token => SearchCoreAsync(executable, root, glob, pattern, regex, caseSensitive,
            maxDepth, maxResults, token), cancellationToken);
    }

    private async Task<TextSearchResult> SearchCoreAsync(string executable, string root, string? glob, string pattern,
        bool regex, bool caseSensitive, int maxDepth, int maxResults, CancellationToken cancellationToken)
    {
        var notices = new HashSet<string>(StringComparer.Ordinal);
        // Inspect the bounded scope before rg reads any project ignore files. In particular an
        // ignore file may itself be a symlink, even when rg does not follow ordinary file links.
        var allowed = await Task.Run(() => Inspect(root, maxDepth, notices, cancellationToken), cancellationToken).ConfigureAwait(false);
        var candidates = new List<string>();
        List<string> common = ["--no-config", "--no-ignore-parent", "--no-ignore-global", "--no-ignore-exclude", "--no-require-git",
            "--no-follow", "--threads", "1", "--max-depth", maxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        if (glob is not null) { common.Add("--glob"); common.Add(glob); }
        // Apply hard exclusions AFTER caller globs. Case rules match ProjectWorkspace, including on Linux.
        foreach (var exclusion in HardExclusions()) { common.Add("--iglob"); common.Add("!" + exclusion); }
        var discovery = await ReadOnlyQueryRunner.RunAsync(executable, workspace.Root,
            [.. common, "--files", "--null", "--", "./" + workspace.Relative(root)], '\0', 8192, QueryText.MaxResultBytes,
            raw =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeResultPath(raw);
                if (allowed.ContainsKey(relative)) candidates.Add(relative);
                else notices.Add("UnsafeOversizedOrUnstableFilesSkipped");
                return candidates.Count < QueryText.MaxWork;
            }, cancellationToken).ConfigureAwait(false);
        CheckExit(discovery, notices);
        if (discovery.OutputLimited) notices.Add("CandidateOutputLimit");
        // rg JSON can emit valid-looking matches before reporting binary_offset at file end.
        // Check bounded candidate bytes first so a result limit cannot publish those early matches.
        candidates.RemoveAll(relative => IsBinaryCandidate(relative, allowed, notices, cancellationToken));
        var matches = new List<TextSearchMatch>();
        var matchedFiles = new HashSet<string>(StringComparer.Ordinal);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(workspace.Relative(root)).Length;
        var stop = false;
        // Explicit candidates preserve rg's earlier ignore/glob decision while preventing the
        // search process from opening an unchecked file. Batches stay below Windows argv limits.
        var batches = new List<List<string>>();
        var batch = new List<string>();
        var length = 0;
        foreach (var candidate in candidates)
        {
            if (length + candidate.Length > 20_000 && batch.Count > 0) { batches.Add(batch); batch = []; length = 0; }
            batch.Add("./" + candidate);
            length += candidate.Length + 4;
        }
        if (batch.Count > 0 || batches.Count == 0) batches.Add(batch);
        foreach (var files in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Recheck every candidate immediately before the child starts, not just returned matches.
            foreach (var file in files) ValidateCandidate(file[2..], allowed);
            List<string> arguments = ["--no-config", "--no-ignore", "--no-follow", "--no-encoding", "--no-mmap", "--json",
                "--threads", "1", "--engine", "default", "--regex-size-limit", "1M", "--dfa-size-limit", "8M",
                "--max-filesize", "256K", "--max-count", (maxResults + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                regex ? "--no-fixed-strings" : "--fixed-strings", caseSensitive ? "--case-sensitive" : "--ignore-case",
                "--regexp", pattern, "--", .. files.Count == 0 ? new List<string> { "-" } : files];
            var result = await ReadOnlyQueryRunner.RunAsync(executable, workspace.Root, arguments, '\n',
                2 * 1024 * 1024, 8 * 1024 * 1024, Accept, cancellationToken).ConfigureAwait(false);
            CheckExit(result, notices);
            if (result.OutputLimited) { notices.Add("ResultOrOutputLimit"); stop = true; }
            if (stop) break;
        }
        foreach (var file in matchedFiles) ValidateCandidate(file, allowed);
        return new(workspace.Relative(root), matches.ToImmutableArray(), notices.Count == 0, stop || discovery.OutputLimited,
            ["Scope: selected root; project-local ignore files/glob, hidden and binary filtering by rg; no parent/global ignores, private paths or links; UTF-8 snippets only, not full-file encoding validation; matching lines, no character columns; live observation, not a snapshot.", .. notices.Order()]);

        bool Accept(string json)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(json);
                var message = document.RootElement;
                var kind = message.GetProperty("type").GetString();
                if (kind is not ("match" or "end")) return true;
                var data = message.GetProperty("data");
                if (kind == "end")
                {
                    if (data.TryGetProperty("binary_offset", out var offset) && offset.ValueKind != JsonValueKind.Null)
                        notices.Add("BinaryContentSkipped");
                    return true;
                }
                if (!data.GetProperty("path").TryGetProperty("text", out var fileText) ||
                    !data.GetProperty("lines").TryGetProperty("text", out var lineText))
                { notices.Add("NonUtf8DataSkipped"); return true; }
                var relative = NormalizeResultPath(fileText.GetString()!);
                ValidateCandidate(relative, allowed);
                if (!candidates.Contains(relative, StringComparer.Ordinal)) throw new WorkspaceException("rg returned a file outside its candidate set.");
                var line = data.GetProperty("line_number").GetInt64();
                if (line < 1) throw new WorkspaceException("rg returned an invalid line number.");
                var text = lineText.GetString()!.TrimEnd('\r', '\n');
                if (text.Contains('\0')) { notices.Add("BinaryContentSkipped"); return true; }
                var preview = QueryText.Display(ProjectWorkspace.Clip(text, 2000));
                var clipped = text.Length > 2000 || preview.Length > 2000;
                var match = new TextSearchMatch(relative, line, ProjectWorkspace.Clip(preview, 2000), clipped);
                var cost = JsonSerializer.SerializeToUtf8Bytes(match).Length + 1;
                if (matches.Count == maxResults || bytes + cost > QueryText.MaxResultBytes - 8192) return false;
                matches.Add(match);
                matchedFiles.Add(relative);
                bytes += cost;
                if (clipped) notices.Add("LinePreviewsClipped");
                return true;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            { throw new WorkspaceException("rg returned unsupported or malformed JSON; no complete search result is available."); }
        }
    }

    private Dictionary<string, Observation> Inspect(string root, int maxDepth, HashSet<string> notices, CancellationToken token)
    {
        var allowed = new Dictionary<string, Observation>(StringComparer.Ordinal);
        var work = 0;
        void InspectFile(string file)
        {
            var relative = workspace.Relative(file);
            try
            {
                workspace.Resolve(relative);
                using var handle = RecoveryFileSystem.OpenSharedRead(file);
                if (handle.Length > ProjectWorkspace.MaxFileBytes)
                {
                    if (IsIgnore(file)) throw new WorkspaceException("A project ignore file exceeds the 256 KiB limit; narrow the search scope.");
                    notices.Add("OversizedFilesSkipped"); return;
                }
                allowed.Add(relative, new(RecoveryFileSystem.FileIdentity(handle), handle.Length, File.GetLastWriteTimeUtc(file)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (IsIgnore(file)) throw new WorkspaceException("A project ignore file cannot be safely inspected; narrow the search scope.");
                notices.Add("NonRegularOrUnavailableFilesSkipped");
            }
        }
        void Walk(string directory, int depth)
        {
            token.ThrowIfCancellationRequested();
            workspace.Resolve(workspace.Relative(directory));
            try
            {
                foreach (var file in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (++work > QueryText.MaxWork) throw new WorkspaceException("Search scope exceeds the 10000-entry inspection budget; choose a narrower path or depth.");
                    if (ProjectWorkspace.IsPrivateName(Path.GetFileName(file))) continue;
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        if (IsIgnore(file)) throw new WorkspaceException("Linked project ignore files are unsupported; choose another search scope.");
                        notices.Add("LinksSkipped"); continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    { if (depth + 1 < maxDepth) Walk(file, depth + 1); }
                    else if (depth < maxDepth || IsIgnore(file)) InspectFile(file);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new WorkspaceException("Search scope could not be safely inspected; choose a narrower accessible path."); }
        }
        if (Directory.Exists(root)) Walk(root, 0); else InspectFile(root);
        return allowed;
    }

    private void ValidateCandidate(string relative, Dictionary<string, Observation> allowed)
    {
        if (!allowed.TryGetValue(relative, out var previous)) throw new WorkspaceException("The search candidate is outside the inspected scope.");
        var file = workspace.Resolve(relative);
        using var handle = RecoveryFileSystem.OpenSharedRead(file);
        if (RecoveryFileSystem.FileIdentity(handle) != previous.Identity || handle.Length != previous.Length ||
            File.GetLastWriteTimeUtc(file) != previous.Modified)
            throw new WorkspaceException("A search candidate changed during the query; retry to obtain a fresh result.");
    }

    private bool IsBinaryCandidate(string relative, Dictionary<string, Observation> allowed,
        HashSet<string> notices, CancellationToken token)
    {
        ValidateCandidate(relative, allowed);
        using var file = RecoveryFileSystem.OpenSharedRead(workspace.Resolve(relative));
        var buffer = new byte[4096];
        var total = 0;
        int count;
        while ((count = file.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            total += count;
            if (total > ProjectWorkspace.MaxFileBytes) throw new WorkspaceException("A search candidate grew during binary inspection; retry the query.");
            if (buffer.AsSpan(0, count).Contains((byte)0)) { notices.Add("BinaryContentSkipped"); return true; }
        }
        return false;
    }

    private string NormalizeResultPath(string path)
    {
        var relative = path.Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
        return workspace.Relative(workspace.Resolve(relative));
    }
    private static bool IsIgnore(string path) => Path.GetFileName(path) is ".ignore" or ".rgignore" or ".gitignore";
    private static IEnumerable<string> HardExclusions()
    {
        foreach (var name in ProjectWorkspace.PrivateNames.Concat([ProjectWorkspace.PrivatePrefix + "*"])
            .Concat(ProjectWorkspace.PrivateExtensions.Select(extension => "*" + extension)))
        { yield return "**/" + name; yield return "**/" + name + "/**"; }
    }
    private static void CheckExit(QueryProcessResult result, HashSet<string> notices)
    {
        if (result.OutputLimited) return;
        if (result.ExitCode is not (0 or 1)) throw new WorkspaceException(result.Failure switch
        {
            QueryFailure.Regex => "ripgrep rejected the regular expression: invalid syntax or a feature unsupported by the Rust regex engine.",
            QueryFailure.Glob => "ripgrep rejected the glob: invalid glob syntax.",
            QueryFailure.UnsupportedArguments => "The installed ripgrep does not support the required controlled arguments; install a supported official rg build.",
            _ => "ripgrep could not read its inputs or complete the query. No complete result is available; check file access and the configured program."
        });
        if (result.HasErrors) notices.Add("RipgrepReportedReadErrors");
    }
    private sealed record Observation(RecoveryFileIdentity Identity, long Length, DateTime Modified);
}
