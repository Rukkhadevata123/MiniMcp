using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using MiniMcp.Services;

namespace MiniMcp.Tools;

[McpServerToolType]
public sealed class FileTools(ProjectWorkspace workspace)
{
    [McpServerTool(Name = "list_files", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("List up to 200 entries inside the configured project directory. Private credential files are excluded.")]
    public DirectoryListing ListFiles([Description("Relative directory path using / separators.")] string path = ".") =>
        ToolErrors.Run(() => workspace.ListFiles(path));

    [McpServerTool(Name = "read_file", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read a UTF-8 project file up to 256 KiB. Paths must stay inside the configured project.")]
    public Task<string> ReadFile([Description("Relative file path using / separators.")] string path,
        CancellationToken cancellationToken) =>
        ToolErrors.RunAsync(() => workspace.ReadFileAsync(path, cancellationToken));

    [McpServerTool(Name = "write_file", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create a UTF-8 project file, creating parent directories as needed. Replacing a file requires overwrite=true. Maximum 256 KiB of UTF-8 bytes.")]
    public Task<WriteResult> WriteFile(
        [Description("Relative file path using / separators.")] string path,
        [Description("Complete UTF-8 text to write.")] string content,
        CancellationToken cancellationToken,
        [Description("Explicitly allow replacing an existing file.")] bool overwrite = false) =>
        ToolErrors.RunAsync(() => workspace.WriteFileAsync(path, content, overwrite, cancellationToken));
}

internal static class ToolErrors
{
    public static T Run<T>(Func<T> action)
    {
        try { return action(); }
        catch (WorkspaceException exception) { throw new McpException(exception.Message); }
        catch (IOException) { throw new McpException("The filesystem operation failed. Check the path and file permissions."); }
        catch (UnauthorizedAccessException) { throw new McpException("The operating system denied access to this path."); }
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (WorkspaceException exception) { throw new McpException(exception.Message); }
        catch (IOException) { throw new McpException("The filesystem operation failed. Check the path and file permissions."); }
        catch (UnauthorizedAccessException) { throw new McpException("The operating system denied access to this path."); }
    }
}
