using MiniMcp.Configuration;
using MiniMcp.Recovery;

namespace MiniMcp.Services;

/// <summary>
/// Owned tool execution boundary for one runtime session and validated configuration snapshot.
/// Approval and recovery support must be present before their dependent operations can run.
/// </summary>
internal sealed class ToolExecutionService : IAsyncDisposable
{
    private readonly object _callsGate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ProjectWorkspace _workspace;
    private readonly PatchService _patches;
    private readonly RecoveryStore _recovery;
    private readonly Lazy<BashRunner> _bash;
    private bool _accepting = true;
    private int _activeCalls;
    private Task? _stopTask;
    private Task? _disposeTask;
    private int _disposed;

    public ToolExecutionService(Guid runId, RuntimeConfiguration configuration) : this(runId, configuration, null) { }

    // Checks observe/inject failures in the real engine and storage; no alternate commit or backup implementation.
    internal ToolExecutionService(Guid runId, RuntimeConfiguration configuration,
        Func<PatchCommitStage, int, Task>? patchCheckpoint,
        Func<RecoveryPreparationStage, RecoveryDraft, Task>? preparationCheckpoint = null,
        Func<RecoveryManifestReplacementStage, string, Task>? replacementCheckpoint = null)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        ArgumentNullException.ThrowIfNull(configuration);
        RunId = runId;
        Configuration = configuration;
        _workspace = new ProjectWorkspace(configuration.Settings.ProjectRoot);
        _patches = new PatchService(_workspace, patchCheckpoint);
        _recovery = new RecoveryStore(runId, configuration, preparationCheckpoint, null, replacementCheckpoint);
        _bash = new Lazy<BashRunner>(() => new BashRunner(_workspace, configuration.Settings.BashPath));
    }

    public Guid RunId { get; }
    public RuntimeConfiguration Configuration { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public int ActiveCalls { get { lock (_callsGate) return _activeCalls; } }

    public Task<ReadResult> ReadFileAsync(string path, int startLine = 1, int? endLine = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => _workspace.ReadFileAsync(path, startLine, endLine, token), cancellationToken);

    public Task<PatchApplyResult> ApplyPatchAsync(string patch, bool dryRun = false,
        CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
        {
            var plan = await _patches.PrepareAsync(patch, token).ConfigureAwait(false);
            // Await the actual computation: cancellation must finish before the call leaves in-flight tracking.
            var decision = await Task.Run(() =>
            {
                var preview = PatchDiffBuilder.Create(plan, token);
                var assessment = PatchRiskClassifier.Classify(preview, Configuration.SettingsFilePath, Configuration.Policy.PatchApprovalLimits, token);
                return PatchWritePolicy.Decide(assessment, Configuration.Policy.Mode, token);
            }, token).ConfigureAwait(false);
            if (dryRun)
            {
                token.ThrowIfCancellationRequested();
                return PatchApplyResult.FromDecision(decision);
            }
            if (!decision.CanCommit) throw new WorkspaceException(decision.RejectionMessage);
            var committed = await _patches.CommitWithRecoveryAsync(plan, _recovery, token).ConfigureAwait(false);
            if (committed.Outcome.VerifiedState != PatchCommitState.Committed)
                throw new RecordedPatchExecutionException(committed);
            return PatchApplyResult.FromDecision(decision, committed);
        }, cancellationToken, checkCancellationAfterCompletion: false);

    public Task<BashResult> RunBashAsync(string command, string workdir = ".", int timeoutSeconds = 30,
        CancellationToken cancellationToken = default) => ExecuteAsync(token =>
        {
            if (Configuration.Policy.Mode != ExecutionMode.FullAccess)
                throw new WorkspaceException(Configuration.Policy.Mode switch
                {
                    ExecutionMode.ReadOnly => "ReadOnly mode does not allow Bash commands.",
                    ExecutionMode.ProjectEdit => "Bash requires local approval in ProjectEdit mode; approvals are not available.",
                    _ => "Unknown execution mode; Bash commands are denied."
                });
            return _bash.Value.RunAsync(command, workdir, timeoutSeconds, token);
        }, cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken,
        bool checkCancellationAfterCompletion = true)
    {
        CancellationTokenSource linked;
        lock (_callsGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (!_accepting) throw new WorkspaceException("This runtime session is stopping; new tool calls are denied.");
            cancellationToken.ThrowIfCancellationRequested();
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            _activeCalls++;
        }
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var result = await operation(linked.Token).ConfigureAwait(false);
            // Patch writes own their completion boundary: an installed/finalized result must not
            // become cancellation here. Their preview/preparation paths still observe the token.
            if (checkCancellationAfterCompletion) linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            linked.Dispose();
            lock (_callsGate)
            {
                _activeCalls--;
                if (!_accepting && _activeCalls == 0) _drained.TrySetResult();
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopping;
        lock (_callsGate)
        {
            if (_stopTask is null)
            {
                _accepting = false;
                if (_activeCalls == 0) _drained.TrySetResult();
                _stopTask = StopCoreAsync();
            }
            stopping = _stopTask;
        }
        // Cancelling the wait never reopens admission or abandons cancellation/draining.
        return stopping.WaitAsync(cancellationToken);
    }

    private async Task StopCoreAsync()
    {
        // Do not run cancellation callbacks under the admission lock or on the caller's UI context.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try { await _stopping.CancelAsync().ConfigureAwait(false); }
        finally { await _drained.Task.ConfigureAwait(false); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_callsGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _workspace.Mutations.Dispose();
        _stopping.Dispose();
        Volatile.Write(ref _disposed, 1);
    }
}
