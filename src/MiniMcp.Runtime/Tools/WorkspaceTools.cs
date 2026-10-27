using MiniMcp.Services.Git;
using MiniMcp.Services.Patching;
using MiniMcp.Services.Processes;
using MiniMcp.Services.Workspace;
using System.ComponentModel;
using System.Text.Json;
using MiniMcp.Services;
using MiniMcp.Recovery;
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

    [McpServerTool(Name = "list_files", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("List project files/directories in all three modes. path defaults to .; max_depth defaults to 2 (root=0), range 0..8; max_entries defaults to 200, range 1..2000. Directories at max_depth are listed with expanded=false but not traversed; max_depth=0 does not enumerate the selected root. DepthLimit with complete=false and truncated=true means a boundary directory was not expanded; it does not by itself mean the parent listing failed. format=flat|tree; tree is generated from the same structured entries. Private paths and links are always excluded. include_hidden defaults to false. Generated directories (node_modules, bin, obj, .vs, dist, build, target, __pycache__, .venv, venv) are skipped unless selected directly as path. No gitignore parsing or global sorting. Bounded traversal (10000 examined entries), 10 seconds and 256 KiB result; complete/truncated/notices explain partial results and unexpanded directories. Relative paths only; no writes, approval or external dependency.")]
    public Task<FileListResult> ListFiles(CancellationToken cancellationToken, string path = ".", int max_depth = 2,
        int max_entries = 200, bool include_hidden = false, string format = "flat") =>
        Run(() => execution.ListFilesAsync(path, max_depth, max_entries, include_hidden, format, cancellationToken));

    [McpServerTool(Name = "search_text", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Search UTF-8 text snippets using locally installed ripgrep, in all three modes; missing/unsupported rg returns a tool error. pattern is literal by default (1..4096 UTF-8 bytes); regex=true enables the Rust regex engine, case_sensitive defaults to true. Optional path=. and glob; max_depth=16 (0..32), max_results=100 matching lines (1..1000). Respects ignore files within the selected root and normal rg hidden/binary filtering; never parent/global ignores, private paths, symlinks or files above 256 KiB. A bounded 10000-entry inspection rejects scopes too large to safely check, including unsafe ignore files. No arbitrary flags, preprocessing, PCRE2, shell or downloads. Returns relative paths, 1-based line numbers, escaped previews <=2000 characters and explicit completeness/truncation notices; no character columns or full-file UTF-8 guarantee. Shared 10 second budget including bounded queue, at most 2 external queries; 256 KiB result. No writes or recovery records.")]
    public Task<TextSearchResult> SearchText(string pattern, CancellationToken cancellationToken, string path = ".",
        string? glob = null, bool regex = false, bool case_sensitive = true, int max_depth = 16, int max_results = 100) =>
        Run(() => execution.SearchTextAsync(pattern, path, glob, regex, case_sensitive, max_depth, max_results, cancellationToken));

    [McpServerTool(Name = "git_status", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Query staged, unstaged and untracked Git status in all modes. path=. is a literal relative path, never pathspec/revision syntax; max_entries=200 (1..2000). Returns structured relative paths and index/worktree status characters. Requires local Git and projectRoot itself to be a standard worktree with a real in-root .git directory. Private paths, links and submodule contents excluded; renames shown as separate adds/deletes. External metadata, oversized metadata or missing/corrupt objects returns a tool error. Git reads repository configuration, including includes, and may invoke configured external programs with the service account's file and network permissions even in ReadOnly; these side effects have no recovery record. Default system/global Git config and inherited Git variables are ignored; fixed options disable external diff, fsmonitor, lazy fetch and index refresh writes. Shared 10 second budget, 2 active external queries/8 waiters; 256 KiB result and explicit complete/truncated/notices.")]
    public Task<GitStatusResult> GitStatus(CancellationToken cancellationToken, string path = ".", int max_entries = 200) =>
        Run(() => execution.GitStatusAsync(path, max_entries, cancellationToken));

    [McpServerTool(Name = "git_diff", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Query Git worktree versus index, or index versus HEAD with staged=true, in all modes. No untracked file bodies or arbitrary revisions/flags. path=. is literal and relative; max_files=20 (1..100), max_lines=1000 (1..4000), whole result <=256 KiB, each escaped line preview <=2000 characters. Returns per-file status and diff lines; query output can be partial with explicit notices (unlike apply_patch's complete preview). Files/blobs <=256 KiB; private paths, links, submodules and unsupported changes excluded. Requires local Git and the same standard repository boundaries as git_status. Git reads repository configuration and may execute its clean filters or other external programs with the service account's file and network permissions even in ReadOnly; these side effects have no recovery record. Default system/global config is ignored; fixed options disable external diff, textconv, fsmonitor, lazy fetch and index refresh writes. Shared 10 second budget and bounded concurrency; complete/truncated/notices describe scope and limits.")]
    public Task<GitDiffResult> GitDiff(CancellationToken cancellationToken, string path = ".", bool staged = false,
        int max_files = 20, int max_lines = 1000) =>
        Run(() => execution.GitDiffAsync(path, staged, max_files, max_lines, cancellationToken));

    [McpServerTool(Name = "git_log", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read bounded commit summaries reachable from current HEAD, in all modes. path=. is a literal relative scope with private paths excluded; max_count=20 (1..100). Returns commit hash, author name, authored date and subject; no blob content, arbitrary revision or full message body. NoHeadCommit explicitly reports an unborn/unresolved HEAD. Fields escape controls and are capped at 2000 characters; total <=256 KiB with complete/truncated/notices. Requires local Git and the same standard in-root repository boundaries as git_status. Git reads repository configuration, including includes; its configuration can cause external programs to run with the service account's file and network permissions even in ReadOnly, with no recovery record. Default system/global config is ignored; fixed options disable signature verification, external diff, fsmonitor, lazy fetch and index refresh writes. Shared 10 second budget and bounded concurrency. Live observation, not a repository snapshot.")]
    public Task<GitLogResult> GitLog(CancellationToken cancellationToken, string path = ".", int max_count = 20) =>
        Run(() => execution.GitLogAsync(path, max_count, cancellationToken));

    [McpServerTool(Name = "apply_patch", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Preview a text patch with dry_run=true or apply it with dry_run=false (default). Read existing files first. ReadOnly denies writes; ProjectEdit permits ordinary source/document changes but directly denies the entire batch for deletion, moves, critical/unknown files or exceeded ProjectEdit limits; FullAccess permits these after all base checks. Every actual write requires a complete recovery record outside the project; backup/storage failure denies installation, with no unrecorded fallback. Format: *** Begin Patch, then *** Add File: path with +lines; *** Delete File: path; or *** Update File: path with optional *** Move to: path and @@ chunks (space=context, -=remove, +=insert). Optional @@ exact anchor and *** End of File. Finish with *** End Patch. Context must match exactly and uniquely; no fuzzy matching or unified-diff line numbers. New/moved destinations must not exist. UTF-8 files <=256 KiB; patch <=1 MiB, <=64 operations. Returns dryRun, complete diff, changes with summary/statistics/risk, total statistics, risk with batch size/local limits/reasons, decision with mode/disposition/blockers/requiresRecoveryRecord/canCommit, and commit. Preview commit is null; successful writes have dryRun=false and commit with runId/changeId/state=Committed/cleanupIncomplete. Cleanup warnings do not imply rollback. Errors after installation report its actual result and record identity; a failed final record save cannot be treated as no write. File risk kinds are Ordinary, Critical or Unknown; reason/blocker names are string arrays. canCommit allows a recorded attempt, not proof of backup readiness or an approval. Batch size counts distinct paths (both ends of moves) and added/removed lines and bytes; only exceeding a limit restricts ProjectEdit writes. Diff lines are quoted with escaped controls and original line endings; metadata shows BOM, endings and applicable modes. Statistics count complete changed UTF-8 lines plus BOM changes, excluding context and display escapes. Diff is limited to 256 KiB UTF-8 and bounded computation; oversized/complex patches are rejected, never truncated. Previewing creates no files or pending approvals and grants no write permission.")]
    public Task<PatchApplyResult> ApplyPatch(string patch, CancellationToken cancellationToken, bool dry_run = false) =>
        Run(() => execution.ApplyPatchAsync(patch, dry_run, cancellationToken));

    [McpServerTool(Name = "bash", ReadOnly = false, Destructive = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Run a one-shot Bash command as the service account. Available only in FullAccess mode. The project root is the default working directory, NOT a shell sandbox: commands can access other paths and the network with that account's permissions. Each call starts a fresh noninteractive shell; stdin is closed and shell state does not persist. workdir is a relative existing project directory. timeout_seconds is 1..600 (default 30); timeout/cancellation and normal completion clean up ordinary child processes. No background job/session API. Returns exitCode, stdout, stderr, timedOut and outputTruncated; each output stream is capped at 16000 characters. Nonzero exits are returned as results.")]
    public Task<BashResult> Bash(string command, CancellationToken cancellationToken, string workdir = ".", int timeout_seconds = 30) =>
        Run(() => execution.RunBashAsync(command, workdir, timeout_seconds, cancellationToken));

    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (RecoveryFinalizationException ex) { throw new McpException(ex.Message); }
        catch (RecordedPatchExecutionException ex) { throw new McpException(ex.Message); }
        catch (InvalidDataException) { throw new McpException("Recovery data is invalid; patch installation was not started."); }
        catch (JsonException) { throw new McpException("Recovery data validation failed; patch installation was not started."); }
        catch (WorkspaceException ex) { throw new McpException(ex.Message); }
        catch (UnauthorizedAccessException) { throw new McpException("Access denied by the operating system."); }
        catch (IOException) { throw new McpException("The filesystem operation failed; check the path and service account permissions."); }
    }
}
