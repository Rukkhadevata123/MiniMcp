namespace MiniMcp.Services.Patching;

// Local callers retain the real engine result; the MCP adapter exports only this bounded diagnostic.
internal sealed class RecordedPatchExecutionException(RecordedPatchCommit commit) : Exception(
    $"Patch engine result: {commit.Outcome.State}; verified result: {commit.Outcome.VerifiedState}. " +
    $"Recovery record {commit.Record.Manifest.ChangeId:N}, run {commit.Record.Manifest.RunId:N}. " +
    (commit.Outcome.VerifiedState == PatchCommitState.RolledBack
        ? "The patch was not committed; rollback was verified."
        : "The patch result or rollback is incomplete. Inspect the affected paths and recovery record before continuing."))
{
    public RecordedPatchCommit Commit { get; } = commit;
}
