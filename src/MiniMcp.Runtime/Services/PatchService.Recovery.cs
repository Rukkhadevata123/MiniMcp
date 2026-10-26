using MiniMcp.Recovery;

namespace MiniMcp.Services;

// Internal transaction facts, not a public tool result or an approval capability.
internal sealed record RecordedPatchCommit(RecoveryStore.PreparedRecord Record, PatchCommitOutcome Outcome);

internal sealed partial class PatchService
{
    // The caller checks mode policy before entering this internal transaction. It cannot supply
    // a prepared flag, a stored manifest or a result state in place of real recovery preparation.
    internal async Task<RecordedPatchCommit> CommitWithRecoveryAsync(PatchPlan plan, RecoveryStore recovery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(recovery);
        if (!ReferenceEquals(plan.Owner, this)) throw new WorkspaceException("The patch plan belongs to a different patch service.");
        await workspace.Mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CommitRecordedUnderLockAsync(plan, recovery, null, cancellationToken).ConfigureAwait(false);
        }
        finally { workspace.Mutations.Release(); }
    }

    // Trusted internal coordinator entry. Caller retains this service's actual workspace mutex;
    // restoration uses this core so source validation and recorded mutation share one lock scope.
    internal async Task<RecordedPatchCommit> CommitRecordedUnderLockAsync(PatchPlan plan, RecoveryStore recovery,
        Func<CancellationToken, Task>? validateSourceRecord, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(recovery);
        if (!ReferenceEquals(plan.Owner, this)) throw new WorkspaceException("The patch plan belongs to a different patch service.");
        await ValidatePlanAsync(plan, cancellationToken).ConfigureAwait(false);
        if (validateSourceRecord is not null) await validateSourceRecord(cancellationToken).ConfigureAwait(false);
        var record = await recovery.PrepareAsync(plan, cancellationToken).ConfigureAwait(false);
        await ValidatePlanAsync(plan, cancellationToken).ConfigureAwait(false);
        if (validateSourceRecord is not null) await validateSourceRecord(cancellationToken).ConfigureAwait(false);
        await recovery.BeginApplyingAsync(record, plan, cancellationToken).ConfigureAwait(false);
        var outcome = await ExecuteAndVerifyCoreAsync(plan, record.ExecutionContext, cancellationToken, validateSourceRecord).ConfigureAwait(false);
        // Never drop real finalization or relabel installed files because of a late caller cancellation.
        await recovery.CompleteAsync(record, plan, outcome).ConfigureAwait(false);
        return new(record, outcome);
    }
}
