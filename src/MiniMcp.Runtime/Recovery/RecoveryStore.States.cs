using MiniMcp.Services.Patching;

namespace MiniMcp.Recovery;

internal sealed class RecoveryFinalizationException(RecoveryStore.PreparedRecord record, PatchCommitOutcome outcome, Exception failure)
    : IOException($"Patch engine result: {outcome.State}; verified result: {outcome.VerifiedState}. Final recovery record {record.Manifest.ChangeId:N}, run {record.Manifest.RunId:N}, could not be saved and verified. Inspect the record locally; no additional workspace rollback was attempted.", failure)
{
    public RecoveryStore.PreparedRecord Record { get; } = record;
    public PatchCommitOutcome Outcome { get; } = outcome;
}

internal sealed partial class RecoveryStore
{
    // Final metadata must finish even after caller/session cancellation. A coordinator tracks and
    // awaits this real task; cancelling a UI wait must never abandon the write or change its result.
    internal async Task CompleteAsync(PreparedRecord record, PatchPlan plan, PatchCommitOutcome outcome)
    {
        ValidateOwnership(record, plan);
        ArgumentNullException.ThrowIfNull(outcome);
        EnsureManifestWritesAllowed();
        await Writes.WaitAsync().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            ValidateOwnership(record, plan);
            if (record.Manifest.State != RecoveryRecordState.Applying)
                throw new InvalidOperationException("Only a live Applying recovery record may be completed.");
            // Contract errors are rejected before storage access; never take a caller-supplied state.
            var completed = RecoveryCompletionBuilder.Create(record, plan, outcome);
            try
            {
                using var storeLock = OpenExistingStoreLock(CancellationToken.None);
                var current = await ValidatePreparedCoreAsync(record, plan, storeLock.RootIdentity, CancellationToken.None).ConfigureAwait(false);
                var bytes = ValidateReplacement(record.Manifest, completed);
                var before = _guard.Measure();
                var oldBytes = current.Files.Single(file => file.Name == ManifestFileName).Bytes.Length;
                before.EnsureCanReplaceManifest(current.Files.Sum(file => (long)file.Bytes.Length), oldBytes, bytes.Length);
                await ReplaceAndAdvanceAsync(record, plan, completed, bytes, before, oldBytes, storeLock.RootIdentity, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                // The engine result stays attached even if writing, readback or quota validation fails.
                // Do not replay the engine or label a committed patch as cancelled/rolled back.
                throw new RecoveryFinalizationException(record, outcome, failure);
            }
        }
        finally { Writes.Release(); }
    }

    // Persists Applying before the workspace engine starts; this method only updates recovery metadata.
    internal async Task BeginApplyingAsync(PreparedRecord record, PatchPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOwnership(record, plan);
        EnsureManifestWritesAllowed();
        await Writes.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            // Another queued transition may have completed while this call was waiting.
            ValidateOwnership(record, plan);
            if (record.Manifest.State != RecoveryRecordState.Prepared)
                throw new InvalidOperationException("Only a live Prepared recovery record may begin applying.");
            using var storeLock = OpenExistingStoreLock(cancellationToken);
            var current = await ValidatePreparedCoreAsync(record, plan, storeLock.RootIdentity, cancellationToken).ConfigureAwait(false);
            var applying = record.Manifest with { State = RecoveryRecordState.Applying };
            var bytes = ValidateReplacement(record.Manifest, applying);
            var maximumFinalBytes = RecoveryManifestBudget.MaximumFinalBytes(applying);
            var before = _guard.Measure(cancellationToken);
            var oldBytes = current.Files.Single(file => file.Name == ManifestFileName).Bytes.Length;
            before.EnsureCanBeginApplying(current.Files.Sum(file => (long)file.Bytes.Length), oldBytes, bytes.Length, maximumFinalBytes);
            await ReplaceAndAdvanceAsync(record, plan, applying, bytes, before, oldBytes, storeLock.RootIdentity, cancellationToken).ConfigureAwait(false);
        }
        finally { Writes.Release(); }
    }

    private async Task ReplaceAndAdvanceAsync(PreparedRecord record, PatchPlan plan, RecoveryManifest manifest, byte[] bytes,
        RecoveryStorageUsage before, int oldBytes, RecoveryFileIdentity rootIdentity, CancellationToken cancellationToken)
    {
        var replacementVerified = false;
        try
        {
            var result = await ReplaceManifestCoreAsync(record, plan, manifest, bytes, before, oldBytes, rootIdentity, cancellationToken).ConfigureAwait(false);
            replacementVerified = true;
            // Keep both locks until the complete live manifest/identity revision has been published.
            record.AcceptVerifiedReplacement(result);
        }
        catch (RecoveryManifestReplacementException)
        {
            record.Invalidate();
            throw;
        }
        catch
        {
            if (replacementVerified) record.Invalidate();
            throw;
        }
    }
}
