using System.ComponentModel;
using MiniMcp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MiniMcp.Tools;

[McpServerToolType]
internal sealed class WorkspaceTools(ToolExecutionService execution)
{
    [McpServerTool(Name = "read_file", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a UTF-8 project file, at most 256 KiB. Optional 1-based inclusive line range. Returns raw content and line metadata, capped at 16000 characters; truncated means the selected range was clipped. Paths are relative to the project root; private files and symlinks are rejected.")]
    public Task<ReadResult> ReadFile(string path, CancellationToken cancellationToken, int start_line = 1, int? end_line = null) =>
        Run(() => execution.ReadFileAsync(path, start_line, end_line, cancellationToken));

    [McpServerTool(Name = "apply_patch", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Preview a text patch inside the project root with dry_run=true. Actual patch writes are disabled. Read existing files first. Format: *** Begin Patch, then *** Add File: path with +lines; *** Delete File: path; or *** Update File: path with optional *** Move to: path and @@ chunks (space=context, -=remove, +=insert). Optional @@ exact anchor and *** End of File. Finish with *** End Patch. Context must match exactly and uniquely; no fuzzy matching or unified-diff line numbers. New/moved destinations must not exist. UTF-8 files <=256 KiB; patch <=1 MiB, <=64 operations. Returns dryRun=true, a complete review diff, changes with summary/statistics, and total statistics. Diff lines are quoted with escaped controls and original line endings; metadata shows BOM, endings and applicable modes. Statistics count full added/removed UTF-8 lines plus BOM changes, excluding context and display escapes. The complete diff is limited to 256 KiB UTF-8 and bounded comparison work; oversized/complex previews are rejected with a request to split the patch, never truncated. Previewing creates no files or approvals and grants no write permission.")]
    public Task<PatchPreviewResult> ApplyPatch(string patch, CancellationToken cancellationToken, bool dry_run = false) =>
        Run(() => execution.ApplyPatchAsync(patch, dry_run, cancellationToken));

    [McpServerTool(Name = "bash", ReadOnly = false, Destructive = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Run a one-shot Bash command as the service account. Available only in FullAccess mode. The project root is the default working directory, NOT a shell sandbox: commands can access other paths and the network with that account's permissions. Each call starts a fresh noninteractive shell; stdin is closed and shell state does not persist. workdir is a relative existing project directory. timeout_seconds is 1..600 (default 30); timeout/cancellation and normal completion clean up ordinary child processes. No background job/session API. Returns exitCode, stdout, stderr, timedOut and outputTruncated; each output stream is capped at 16000 characters. Nonzero exits are returned as results.")]
    public Task<BashResult> Bash(string command, CancellationToken cancellationToken, string workdir = ".", int timeout_seconds = 30) =>
        Run(() => execution.RunBashAsync(command, workdir, timeout_seconds, cancellationToken));

    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (WorkspaceException ex) { throw new McpException(ex.Message); }
        catch (UnauthorizedAccessException) { throw new McpException("Access denied by the operating system."); }
        catch (IOException) { throw new McpException("The filesystem operation failed; check the path and service account permissions."); }
    }
}
