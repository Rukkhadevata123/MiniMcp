using System.Collections.Immutable;
using System.Security.Cryptography;
using MiniMcp.Services;

namespace MiniMcp.Recovery;

// Pure proposal construction from an exact live applying record and its verified engine attempt.
// It does not update storage; the coordinator/final updater must retain the relevant locks and recheck.
internal static class RecoveryCompletionBuilder
{
    public static RecoveryManifest Create(RecoveryStore.PreparedRecord record, PatchPlan plan, PatchCommitOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Owner.ValidateOwnership(record, plan);
        ArgumentNullException.ThrowIfNull(outcome);
        var applying = record.Manifest;
        var context = record.ExecutionContext;
        plan.Owner.ValidateOutcome(plan, outcome, context);
        var state = outcome.VerifiedState switch
        {
            PatchCommitState.Committed => RecoveryRecordState.Committed,
            PatchCommitState.RolledBack => RecoveryRecordState.RolledBack,
            _ => RecoveryRecordState.Incomplete
        };
        if (state != RecoveryRecordState.Committed) return applying with { State = state };
        var files = outcome.Paths.ToDictionary(path => path.Path, StringComparer.Ordinal);
        var paths = applying.Paths.Select(path =>
        {
            if (!files.Remove(path.Path, out var actual)) throw new InvalidOperationException("Verified outcome is missing a record path.");
            var expected = path.After;
            if ((expected is null) != (actual.File is null)) throw new InvalidOperationException("Verified outcome existence facts disagree with the record.");
            if (expected is null) return path;
            var observed = actual.File!;
            if (observed.Bytes.Length != expected.ByteLength || Convert.ToHexString(SHA256.HashData(observed.Bytes.AsSpan())) != expected.Sha256 ||
                (!expected.UnixModeIsCreateRequest && observed.Mode != expected.UnixMode))
                throw new InvalidOperationException("Verified outcome contents or permissions disagree with the record.");
            return path with { After = expected with { UnixMode = observed.Mode, UnixModeIsCreateRequest = false } };
        }).ToImmutableArray();
        if (files.Count != 0) throw new InvalidOperationException("Verified outcome contains unrelated paths.");
        var completed = applying with { State = state, Paths = paths };
        RecoveryManifestValidator.Validate(completed, applying.ProjectRoot, applying.ChangeId);
        return completed;
    }
}
