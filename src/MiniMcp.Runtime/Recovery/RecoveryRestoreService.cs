using System.Collections.Immutable;
using MiniMcp.Configuration;
using MiniMcp.Services;

namespace MiniMcp.Recovery;

// Internal preflight evidence, never human approval. No public constructor or serialized DTO can
// mint a valid plan. CLI/WinUI must bind explicit local confirmation before exposing execution.
internal sealed class RecoveryRestorePlan
{
    private readonly RecoveryRestoreService _owner;
    private readonly object _issuer;
    private int _started;
    internal RecoveryRestorePlan(RecoveryRestoreService owner, object issuer, RecoveryInspection inspection, PatchPlan plan)
    { _owner = owner; _issuer = issuer; Preview = inspection.Preview; Source = inspection.Record; Plan = plan; }
    internal RecoveryPreviewResult Preview { get; }
    internal RecoveryRecordSnapshot Source { get; }
    internal PatchPlan Plan { get; }
    internal void Validate(RecoveryRestoreService owner, object issuer)
    {
        if (!ReferenceEquals(owner, _owner) || !ReferenceEquals(issuer, _issuer))
            throw new InvalidOperationException("The restore plan belongs to another session or restore coordinator.");
        if (Volatile.Read(ref _started) != 0) throw new InvalidOperationException("This one-time restore plan has already been used; obtain a new preview.");
    }
    internal void Start(RecoveryRestoreService owner, object issuer)
    {
        Validate(owner, issuer);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("This one-time restore plan has already been used.");
    }
}

internal sealed record RecoveryRestoreCommit(Guid SourceChangeId, RecordedPatchCommit Commit);

// Only trusted in-process coordination/tests call these primitives. No MCP, public Runtime write
// method, confirmation boolean or automatic approval is supplied before local interaction is wired.
internal sealed class RecoveryRestoreService
{
    private readonly object _issuer = new();
    private readonly ProjectWorkspace _workspace;
    private readonly PatchService _patches;
    private readonly RecoveryStore _store;
    private readonly RecoveryPreviewService _previews;
    public RecoveryRestoreService(ProjectWorkspace workspace, PatchService patches, RecoveryStore store, RecoveryPreviewService previews)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(previews);
        if (!patches.OwnsWorkspace(workspace) || !previews.IsFor(workspace, store))
            throw new ArgumentException("Restore execution must share the exact workspace, patch service and recovery store used for preview.");
        _workspace = workspace; _patches = patches; _store = store; _previews = previews;
    }

    public async Task<RecoveryRestorePlan> PrepareAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        if (changeId == Guid.Empty) throw new ArgumentException("A nonempty recovery record ID is required.", nameof(changeId));
        EnsureWritable();
        await _workspace.Mutations.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            var inspection = await _previews.InspectUnderLockAsync(changeId, cancellationToken).ConfigureAwait(false);
            if (!inspection.Preview.Blockers.IsEmpty || inspection.Preview.Diff is null)
                throw new WorkspaceException("Restoration requires a complete conflict-free committed-record preview. Blocking reasons: " + string.Join(", ", inspection.Preview.Blockers));
            var changes = inspection.Changes.Select(change => new PlannedPatchChange(
                _workspace.Resolve(change.Summary.Path), _workspace.Resolve(change.Summary.MoveTo ?? change.Summary.Path),
                change.Before, change.After, change.Summary, change.AfterMode)).ToImmutableArray();
            var plan = new PatchPlan(_patches, changes);
            var executable = await Task.Run(() => PatchDiffBuilder.Create(plan, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (executable.Diff != inspection.Preview.Diff || executable.Statistics != inspection.Preview.Statistics)
                throw new InvalidOperationException("The restore plan does not reproduce the complete reviewed diff.");
            cancellationToken.ThrowIfCancellationRequested();
            return new(this, _issuer, inspection, plan);
        }
        finally { _workspace.Mutations.Release(); }
    }

    public async Task<RecoveryRestoreCommit> CommitAsync(RecoveryRestorePlan preparation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        EnsureWritable();
        preparation.Validate(this, _issuer);
        await _workspace.Mutations.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            preparation.Start(this, _issuer);
            var commit = await _patches.CommitRecordedUnderLockAsync(preparation.Plan, _store,
                token => _previews.ValidateRecordAsync(preparation.Source, token), cancellationToken).ConfigureAwait(false);
            return new(preparation.Source.Manifest.ChangeId, commit);
        }
        finally { _workspace.Mutations.Release(); }
    }

    private void EnsureWritable()
    {
        if (_store.Mode == ExecutionMode.ReadOnly) throw new WorkspaceException("ReadOnly mode does not allow recovery writes.");
        if (_store.Mode is not (ExecutionMode.ProjectEdit or ExecutionMode.FullAccess)) throw new WorkspaceException("Unknown execution mode; recovery writes are denied.");
    }
}
