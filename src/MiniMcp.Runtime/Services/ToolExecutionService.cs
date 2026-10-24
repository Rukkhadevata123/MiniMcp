using MiniMcp.Configuration;

namespace MiniMcp.Services;

/// <summary>
/// Shared tool execution boundary for one validated configuration snapshot.
/// Approval and recovery support must be present before their dependent operations can run.
/// </summary>
internal sealed class ToolExecutionService
{
    private readonly ExecutionPolicy _policy;
    private readonly ProjectWorkspace _workspace;
    private readonly PatchService _patches;
    private readonly Lazy<BashRunner> _bash;

    public ToolExecutionService(RuntimeConfiguration configuration)
    {
        _policy = configuration.Policy;
        _workspace = new ProjectWorkspace(configuration.Settings.ProjectRoot);
        _patches = new PatchService(_workspace);
        _bash = new Lazy<BashRunner>(() => new BashRunner(_workspace, configuration.Settings.BashPath));
    }

    public Task<ReadResult> ReadFileAsync(string path, int startLine = 1, int? endLine = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _workspace.ReadFileAsync(path, startLine, endLine, cancellationToken);
    }

    public Task<PatchResult> ApplyPatchAsync(string patch, bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!dryRun)
            throw new WorkspaceException(_policy.Mode == ExecutionMode.ReadOnly
                ? "ReadOnly mode does not allow patch writes. Use dry_run=true to preview changes."
                : "Patch writes are unavailable until recovery records are supported. Use dry_run=true to preview changes.");
        return _patches.ApplyAsync(patch, dryRun: true, cancellationToken);
    }

    public Task<BashResult> RunBashAsync(string command, string workdir = ".", int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_policy.Mode != ExecutionMode.FullAccess)
            throw new WorkspaceException(_policy.Mode switch
            {
                ExecutionMode.ReadOnly => "ReadOnly mode does not allow Bash commands.",
                ExecutionMode.ProjectEdit => "Bash requires local approval in ProjectEdit mode; approvals are not available.",
                _ => "Unknown execution mode; Bash commands are denied."
            });
        return _bash.Value.RunAsync(command, workdir, timeoutSeconds, cancellationToken);
    }
}
