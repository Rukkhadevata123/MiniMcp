using MiniMcp.Configuration;

namespace MiniMcp.Services;

/// <summary>
/// Owned tool execution boundary for one runtime session and validated configuration snapshot.
/// Approval and recovery support must be present before their dependent operations can run.
/// </summary>
internal sealed class ToolExecutionService : IDisposable
{
    private readonly ProjectWorkspace _workspace;
    private readonly PatchService _patches;
    private readonly Lazy<BashRunner> _bash;
    private int _disposed;

    public ToolExecutionService(Guid runId, RuntimeConfiguration configuration)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        ArgumentNullException.ThrowIfNull(configuration);
        RunId = runId;
        Configuration = configuration;
        _workspace = new ProjectWorkspace(configuration.Settings.ProjectRoot);
        _patches = new PatchService(_workspace);
        _bash = new Lazy<BashRunner>(() => new BashRunner(_workspace, configuration.Settings.BashPath));
    }

    public Guid RunId { get; }
    public RuntimeConfiguration Configuration { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Task<ReadResult> ReadFileAsync(string path, int startLine = 1, int? endLine = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return _workspace.ReadFileAsync(path, startLine, endLine, cancellationToken);
    }

    public Task<PatchResult> ApplyPatchAsync(string patch, bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!dryRun)
            throw new WorkspaceException(Configuration.Policy.Mode == ExecutionMode.ReadOnly
                ? "ReadOnly mode does not allow patch writes. Use dry_run=true to preview changes."
                : "Patch writes are unavailable until recovery records are supported. Use dry_run=true to preview changes.");
        return _patches.ApplyAsync(patch, dryRun: true, cancellationToken);
    }

    public Task<BashResult> RunBashAsync(string command, string workdir = ".", int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Configuration.Policy.Mode != ExecutionMode.FullAccess)
            throw new WorkspaceException(Configuration.Policy.Mode switch
            {
                ExecutionMode.ReadOnly => "ReadOnly mode does not allow Bash commands.",
                ExecutionMode.ProjectEdit => "Bash requires local approval in ProjectEdit mode; approvals are not available.",
                _ => "Unknown execution mode; Bash commands are denied."
            });
        return _bash.Value.RunAsync(command, workdir, timeoutSeconds, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _workspace.Mutations.Dispose();
    }
}
