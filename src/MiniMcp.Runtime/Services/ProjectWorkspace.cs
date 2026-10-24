using System.Text;

namespace MiniMcp.Services;

public sealed class WorkspaceException(string message) : Exception(message);
public sealed record ReadResult(string Path, int StartLine, int EndLine, int TotalLines, bool Truncated, string Content);
internal sealed record TextFile(byte[] Bytes, string Text, bool HasBom, UnixFileMode? Mode);
internal sealed record TextLine(string Text, string Ending);

internal sealed class ProjectWorkspace
{
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxOutputCharacters = 16_000;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal SemaphoreSlim Mutations { get; } = new(1, 1);
    public string Root { get; }

    public ProjectWorkspace(string projectRoot) => Root = ValidateRoot(projectRoot);

    internal static string ValidateRoot(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(root))
            throw new InvalidOperationException("The project root must be an existing project directory.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(root, Path.GetPathRoot(root), comparison) || string.Equals(root,
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), comparison))
            throw new InvalidOperationException("The project root must name a project, not the filesystem root or your home directory.");
        CheckRoot(root);
        return root;
    }

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
            path.Contains('\\') || path.Contains(':') || path.Contains('\0'))
            throw new WorkspaceException("Use a relative project path with / separators.");
        string full;
        try { full = Path.GetFullPath(Path.Combine(Root, path)); }
        catch (ArgumentException) { throw new WorkspaceException("Invalid project path."); }
        var relative = Path.GetRelativePath(Root, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new WorkspaceException("The path is outside the project root.");

        CheckRoot(Root);
        var current = Root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsPrivateName(part))
                throw new WorkspaceException("Credential files and private metadata directories are not exposed by file tools.");
            current = Path.Combine(current, part);
            RejectLink(current);
        }
        return full;
    }

    public async Task<ReadResult> ReadFileAsync(string path, int startLine = 1, int? endLine = null,
        CancellationToken cancellationToken = default)
    {
        if (startLine < 1 || endLine < startLine)
            throw new WorkspaceException("Use 1-based line numbers with end_line >= start_line.");
        var file = Resolve(path);
        var snapshot = await SnapshotAsync(file, cancellationToken);
        var lines = SplitLines(snapshot.Text);
        if (startLine > Math.Max(1, lines.Count)) throw new WorkspaceException("start_line is beyond the end of the file.");
        var end = Math.Min(endLine ?? lines.Count, lines.Count);
        var content = new StringBuilder();
        var last = startLine - 1;
        var truncated = false;
        for (var i = startLine - 1; i < end; i++)
        {
            var text = lines[i].Text + lines[i].Ending;
            var remaining = MaxOutputCharacters - content.Length;
            if (remaining == 0) { truncated = true; break; }
            if (text.Length > remaining)
            {
                content.Append(Clip(text, remaining));
                last = i + 1;
                truncated = true;
                break;
            }
            content.Append(text);
            last = i + 1;
        }
        return new ReadResult(Relative(file), startLine, last, lines.Count, truncated, content.ToString());
    }

    internal async Task<TextFile> SnapshotAsync(string file, CancellationToken cancellationToken)
    {
        if (!File.Exists(file)) throw new WorkspaceException("File does not exist.");
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[MaxFileBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxFileBytes) throw new WorkspaceException("File exceeds the 256 KiB limit.");
        if (bytes.AsSpan(0, count).Contains((byte)0)) throw new WorkspaceException("Only UTF-8 text files are supported.");
        try
        {
            var bom = count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
            var offset = bom ? 3 : 0;
            return new TextFile(bytes[..count], Utf8.GetString(bytes, offset, count - offset), bom,
                OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(file));
        }
        catch (DecoderFallbackException) { throw new WorkspaceException("The file is not valid UTF-8 text."); }
    }

    internal static byte[] Encode(string text, bool bom = false)
    {
        if (text.Contains('\0')) throw new WorkspaceException("Only UTF-8 text files are supported.");
        byte[] bytes;
        try { bytes = Utf8.GetBytes(text); }
        catch (EncoderFallbackException) { throw new WorkspaceException("Content is not valid UTF-8 text."); }
        if (bytes.Length + (bom ? 3 : 0) > MaxFileBytes)
            throw new WorkspaceException("Content exceeds the 256 KiB UTF-8 byte limit.");
        return bom ? [0xef, 0xbb, 0xbf, .. bytes] : bytes;
    }

    internal static List<TextLine> SplitLines(string text)
    {
        var result = new List<TextLine>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var crlf = i > start && text[i - 1] == '\r';
            result.Add(new TextLine(text[start..(crlf ? i - 1 : i)], crlf ? "\r\n" : "\n"));
            start = i + 1;
        }
        if (start < text.Length) result.Add(new TextLine(text[start..], ""));
        return result;
    }

    internal static string Clip(string text, int maximum)
    {
        var length = Math.Min(text.Length, maximum);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    internal string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static void CheckRoot(string root)
    {
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            RejectLink(directory.FullName);
    }

    private static bool IsPrivateName(string name) =>
        new[] { ".git", ".ssh", ".aws", ".config", ".codex", ".agents", ".cloudflared", "tunnel-token" }
            .Contains(name, StringComparer.OrdinalIgnoreCase) ||
        name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
        new[] { ".pfx", ".p12", ".key", ".pem" }.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new WorkspaceException("Symbolic links and junctions are not exposed by file tools.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
