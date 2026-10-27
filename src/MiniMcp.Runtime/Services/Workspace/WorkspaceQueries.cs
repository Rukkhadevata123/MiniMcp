using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace MiniMcp.Services.Workspace;

public sealed record FileListEntry(string Path, string Kind, int Depth, long? Size, bool Expanded);
public sealed record FileListResult(string Scope, ImmutableArray<FileListEntry> Entries, string? Tree,
    bool Complete, bool Truncated, ImmutableArray<string> Notices);
public sealed record TextSearchMatch(string Path, long Line, string Preview, bool PreviewTruncated);
public sealed record TextSearchResult(string Scope, ImmutableArray<TextSearchMatch> Matches,
    bool Complete, bool Truncated, ImmutableArray<string> Notices);

// Data results use their own limits; logging must never become the source of query content.
internal static class QueryText
{
    internal const int MaxResultBytes = 256 * 1024;
    internal const int MaxWork = 10_000;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions DisplayOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static string Display(string value) => JsonSerializer.Serialize(value, DisplayOptions)[1..^1];

    internal static string ValidatePath(ProjectWorkspace workspace, string path)
    {
        if (Encoding.UTF8.GetByteCount(path) > 4096) throw new WorkspaceException("Query paths are limited to 4096 UTF-8 bytes.");
        return workspace.Resolve(path);
    }

    internal static void Range(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum) throw new WorkspaceException($"{name} must be between {minimum} and {maximum}.");
    }
}

internal sealed class WorkspaceQueries(ProjectWorkspace workspace)
{
    internal static readonly string[] GeneratedDirectories = ["node_modules", "bin", "obj", ".vs", "dist", "build", "target", "__pycache__", ".venv", "venv"];

    internal Task<FileListResult> ListAsync(string path, int maxDepth, int maxEntries, bool includeHidden,
        string format, CancellationToken cancellationToken)
    {
        QueryText.Range(maxDepth, 0, 8, "max_depth");
        QueryText.Range(maxEntries, 1, 2000, "max_entries");
        if (format is not ("flat" or "tree")) throw new WorkspaceException("format must be flat or tree.");
        var root = QueryText.ValidatePath(workspace, path);
        if (!Directory.Exists(root)) throw new WorkspaceException("list_files path must name an existing project directory.");
        // Directory enumeration is synchronous; it must not occupy the MCP dispatch thread.
        return Task.Run(() => List(root, maxDepth, maxEntries, includeHidden, format, cancellationToken), cancellationToken);
    }

    private FileListResult List(string root, int maxDepth, int maxEntries, bool includeHidden,
        string format, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(QueryText.Timeout);
        var entries = new List<FileListEntry>();
        var notices = new HashSet<string>(StringComparer.Ordinal);
        var work = 0;
        var scope = workspace.Relative(root);
        // Include the scope itself and its doubly escaped tree representation in the byte budget.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(scope).Length * 3;
        var stopped = false;
        void Walk(string directory, int depth)
        {
            if (depth == maxDepth) { notices.Add("DepthLimit: boundary directories were not expanded. This notice does not indicate a listing failure."); return; }
            try
            {
                workspace.Resolve(workspace.Relative(directory));
                foreach (var item in Directory.EnumerateFileSystemEntries(directory))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (++work > QueryText.MaxWork) { notices.Add("TraversalLimit"); stopped = true; return; }
                    var name = Path.GetFileName(item);
                    if (ProjectWorkspace.IsPrivateName(name)) continue;
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(item); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { notices.Add("UnavailableEntries"); continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { notices.Add("LinksSkipped"); continue; }
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    if (!includeHidden && (name.StartsWith('.') || (attributes & FileAttributes.Hidden) != 0)) continue;
                    if (isDirectory && GeneratedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (entries.Count == maxEntries) { notices.Add("EntryLimit"); stopped = true; return; }
                    try
                    {
                        var relative = workspace.Relative(item);
                        workspace.Resolve(relative);
                        var entry = new FileListEntry(relative, isDirectory ? "directory" : "file", depth + 1,
                            isDirectory ? null : new FileInfo(item).Length, isDirectory && depth + 1 < maxDepth);
                        // Reserve space for the tree, metadata and notices, even for heavily escaped names.
                        var cost = JsonSerializer.SerializeToUtf8Bytes(entry).Length * 2 + 64;
                        if (bytes + cost > QueryText.MaxResultBytes - 8192) { notices.Add("OutputLimit"); stopped = true; return; }
                        bytes += cost;
                        entries.Add(entry);
                        if (isDirectory) Walk(item, depth + 1);
                        if (stopped) return;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkspaceException)
                    { notices.Add("UnstableOrUnavailableEntries"); }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkspaceException)
            { notices.Add("UnstableOrUnavailableDirectories"); }
        }
        try { Walk(root, 0); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { notices.Add("Timeout"); }
        cancellationToken.ThrowIfCancellationRequested();
        var tree = format == "tree" ? QueryText.Display(scope) + "/\n" + string.Concat(entries.Select(entry =>
            new string(' ', (entry.Depth - 1) * 2) + QueryText.Display(entry.Path.Split('/')[^1]) +
            (entry.Kind == "directory" ? entry.Expanded ? "/" : "/ [not expanded]" : "") + "\n")) : null;
        return new(scope, entries.ToImmutableArray(), tree, notices.Count == 0, notices.Count > 0,
            ["Scope: private paths and links excluded; hidden entries excluded unless include_hidden; generated directories skipped except when selected as path; no gitignore parsing; enumeration order; live observation, not a snapshot.", .. notices.Order()]);
    }
}
