using MiniMcp.Recovery;

namespace MiniMcp.Services;

// Internal transaction facts, not a public tool result or an approval capability.
internal sealed record RecordedPatchCommit(RecoveryStore.PreparedRecord Record, PatchCommitOutcome Outcome);

internal sealed partial class PatchService
{
    // The caller selects policy/approval before entering this internal transaction. It cannot supply
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
            // No workspace directories or staging files are created until the complete backup and the
            // Applying revision have both been flushed and checked. Preparation can take time;
            // reject external changes again before advancing its record to Applying.
            await ValidatePlanAsync(plan, cancellationToken).ConfigureAwait(false);
            var record = await recovery.PrepareAsync(plan, cancellationToken).ConfigureAwait(false);
            await ValidatePlanAsync(plan, cancellationToken).ConfigureAwait(false);
            await recovery.BeginApplyingAsync(record, plan, cancellationToken).ConfigureAwait(false);

            // Revalidates the whole plan after Applying, then reuses staging/install/rollback and
            // observation under this same lock. Do not call an entry that acquires it a second time.
            var outcome = await ExecuteAndVerifyCoreAsync(plan, record.ExecutionContext, cancellationToken).ConfigureAwait(false);
            // Await the real metadata task without caller/session cancellation, retaining the lock.
            // A failed final save carries the engine outcome; it never triggers another rollback.
            await recovery.CompleteAsync(record, plan, outcome).ConfigureAwait(false);
            return new(record, outcome);
        }
        finally { workspace.Mutations.Release(); }
    }
}
