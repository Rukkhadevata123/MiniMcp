using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MiniMcp.Configuration;
using MiniMcp.Recovery;

namespace MiniMcp.Services.Patching;

// Byte counts describe complete +/- UTF-8 lines (including their endings) and BOM changes.
// Context, display escapes and path-only moves do not contribute to these counts.
public sealed record PatchStatistics(int AddedLines, int RemovedLines, long AddedBytes, long RemovedBytes)
{
    public static PatchStatistics operator +(PatchStatistics left, PatchStatistics right) => new(
        left.AddedLines + right.AddedLines, left.RemovedLines + right.RemovedLines,
        left.AddedBytes + right.AddedBytes, left.RemovedBytes + right.RemovedBytes);
}

public sealed record PatchBatchSize(int Paths, long ChangedLines, long ChangedBytes);
public sealed record PatchFileRisk(string Kind, ImmutableArray<string> Reasons);
public sealed record PatchFilePreview(PatchChange Summary, PatchStatistics Statistics, PatchFileRisk Risk);
public sealed record PatchRiskSummary(PatchBatchSize Size, ProjectEditLimits Limits, ImmutableArray<string> Reasons);
public sealed record PatchWriteSummary(string Mode, string Disposition, ImmutableArray<string> Blockers,
    bool RequiresRecoveryRecord, bool CanCommit);

public sealed record PatchCommitSummary(Guid RunId, Guid ChangeId, string State, bool CleanupIncomplete);

public sealed record PatchApplyResult(bool DryRun, string Diff, ImmutableArray<PatchFilePreview> Changes, PatchStatistics Statistics,
    PatchRiskSummary Risk, PatchWriteSummary Decision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PatchCommitSummary? Commit)
{
    // Project only review data. Never serialize the internal assessment, plan, ownership or raw snapshots.
    internal static PatchApplyResult FromDecision(PatchWriteDecision decision, RecordedPatchCommit? committed = null)
    {
        var assessment = decision.Assessment;
        PatchCommitSummary? commit = null;
        if (committed is not null)
        {
            committed.Record.Owner.ValidateOwnership(committed.Record, assessment.Plan);
            assessment.Plan.Owner.ValidateOutcome(assessment.Plan, committed.Outcome);
            if (!decision.CanCommit || committed.Outcome.VerifiedState != PatchCommitState.Committed ||
                committed.Record.Manifest.State != RecoveryRecordState.Committed)
                throw new InvalidOperationException("Only a verified, durably finalized commit may produce a successful patch result.");
            commit = new(committed.Record.Manifest.RunId, committed.Record.Manifest.ChangeId,
                committed.Record.Manifest.State.ToString(), !committed.Outcome.CleanupErrors.IsEmpty);
        }
        var preview = assessment.Preview;
        var files = preview.Files.Select((file, index) => new PatchFilePreview(file.Summary, file.Statistics,
            new(assessment.Changes[index].Kind.ToString(), Reasons(assessment.Changes[index].Reasons)))).ToImmutableArray();
        return new(committed is null, preview.Diff, files, preview.Statistics, new(assessment.Size, assessment.Limits, Reasons(assessment.Reasons)),
            new(decision.Mode.ToString(), decision.Disposition.ToString(),
                decision.Blockers == PatchWriteBlocker.None ? [] : [decision.Blockers.ToString()],
                decision.RequiresRecoveryRecord, decision.CanCommit), commit);
    }

    private static ImmutableArray<string> Reasons(PatchRiskReason reasons) => Enum.GetValues<PatchRiskReason>()
        .Where(value => value != PatchRiskReason.None && (reasons & value) != 0).Select(value => value.ToString()).ToImmutableArray();
}

internal sealed record PatchFileDiff(PatchChange Summary, PatchStatistics Statistics);

// The display stays bound to its immutable source plan; it never becomes an approval or commit ticket.
internal sealed record PatchPreview(PatchPlan Plan, string Diff,
    ImmutableArray<PatchFileDiff> Files, PatchStatistics Statistics);
