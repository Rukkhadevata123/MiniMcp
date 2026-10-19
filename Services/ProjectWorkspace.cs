using System.Text;
using MiniMcp.Configuration;

namespace MiniMcp.Services;

public sealed class WorkspaceException(string message) : Exception(message);
public sealed record ProjectEntry(string Name, string Type);
public sealed record DirectoryListing(string Path, ProjectEntry[] Entries, bool Truncated);
public sealed record WriteResult(string Path, int Bytes, bool Overwritten);

public sealed class ProjectWorkspace
{
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxEntries = 200;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly SemaphoreSlim _writes = new(1, 1);
    public string Root { get; }

    public ProjectWorkspace(MiniMcpOptions settings)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.Root));
        if (!Directory.Exists(Root))
            throw new InvalidOperationException("MCP_ROOT must be an existing project directory.");
        if (Root == Path.GetPathRoot(Root) || Root ==
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
            throw new InvalidOperationException("MCP_ROOT must name a project, not the filesystem root or your home directory.");
        for (var directory = new DirectoryInfo(Root); directory is not null; directory = directory.Parent)
            RejectLink(directory.FullName);
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
            throw new WorkspaceException("The path is outside MCP_ROOT.");

        // Recheck the root's ancestors as well as every existing path component on each operation.
        for (var directory = new DirectoryInfo(Root); directory is not null; directory = directory.Parent)
            RejectLink(directory.FullName);
        var current = Root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsPrivateName(part))
                throw new WorkspaceException("Credential files and private metadata directories are not exposed.");
            current = Path.Combine(current, part);
            RejectLink(current);
        }
        return full;
    }

    public DirectoryListing ListFiles(string path = ".")
    {
        var directory = Resolve(path);
        if (!Directory.Exists(directory)) throw new WorkspaceException("Directory does not exist.");
        var entries = Directory.EnumerateFileSystemEntries(directory)
            .Where(p => !IsPrivateName(Path.GetFileName(p)))
            .Take(MaxEntries + 1)
            .Select(p => new ProjectEntry(Path.GetFileName(p),
                (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0 ? "link" :
                Directory.Exists(p) ? "directory" : "file"))
            .ToArray();
        return new DirectoryListing(Relative(directory),
            entries.Take(MaxEntries).OrderBy(e => e.Name, StringComparer.Ordinal).ToArray(),
            entries.Length > MaxEntries);
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = Resolve(path);
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
        if (bytes.AsSpan(0, count).Contains((byte)0))
            throw new WorkspaceException("Only UTF-8 text files are supported.");
        try
        {
            var offset = count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            return Utf8.GetString(bytes, offset, count - offset);
        }
        catch (DecoderFallbackException) { throw new WorkspaceException("The file is not valid UTF-8 text."); }
    }

    public async Task<WriteResult> WriteFileAsync(string path, string content, bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (content is null) throw new WorkspaceException("content is required.");
        int length;
        try { length = Utf8.GetByteCount(content); }
        catch (EncoderFallbackException) { throw new WorkspaceException("content is not valid UTF-8 text."); }
        if (length > MaxFileBytes) throw new WorkspaceException("Content exceeds the 256 KiB UTF-8 byte limit.");
        if (content.Contains('\0')) throw new WorkspaceException("Only UTF-8 text files are supported.");

        await _writes.WaitAsync(cancellationToken);
        string? temporaryFile = null;
        try
        {
            var file = Resolve(path);
            if (Directory.Exists(file)) throw new WorkspaceException("The destination is a directory.");
            var exists = File.Exists(file);
            if (exists && !overwrite)
                throw new WorkspaceException("File exists. Set overwrite=true to replace it.");
            var parent = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(parent);
            file = Resolve(path);
            temporaryFile = Path.Combine(parent, $".minimcp-{Guid.NewGuid():N}.tmp");
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
                streamOptions.UnixCreateMode = exists
                    ? File.GetUnixFileMode(file)
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporaryFile, streamOptions))
            {
                await stream.WriteAsync(Utf8.GetBytes(content), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Resolve(path);
            File.Move(temporaryFile, file, overwrite);
            temporaryFile = null;
            return new WriteResult(Relative(file), length, exists);
        }
        finally
        {
            if (temporaryFile is not null) File.Delete(temporaryFile);
            _writes.Release();
        }
    }

    private string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static bool IsPrivateName(string name) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".ssh", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".aws", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".codex", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".agents", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".key", StringComparison.OrdinalIgnoreCase);

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new WorkspaceException("Symbolic links and junctions are not exposed.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
