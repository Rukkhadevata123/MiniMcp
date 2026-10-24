using System.Collections.Immutable;
using MiniMcp.Configuration;

namespace MiniMcp.Services;

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
public sealed record PatchRiskSummary(PatchBatchSize Size, PatchApprovalLimits Limits, ImmutableArray<string> Reasons);
public sealed record PatchWriteSummary(string Mode, string Disposition, ImmutableArray<string> Blockers,
    bool RequiresRecoveryRecord, bool CanCommit);

public sealed record PatchPreviewResult(string Diff, ImmutableArray<PatchFilePreview> Changes, PatchStatistics Statistics,
    PatchRiskSummary Risk, PatchWriteSummary Decision)
{
    public bool DryRun => true;

    // Project only review data. Never serialize the internal assessment, plan, ownership or raw snapshots.
    internal static PatchPreviewResult FromDecision(PatchWriteDecision decision)
    {
        var assessment = decision.Assessment;
        var preview = assessment.Preview;
        var files = preview.Files.Select((file, index) => new PatchFilePreview(file.Summary, file.Statistics,
            new(assessment.Changes[index].Kind.ToString(), Reasons(assessment.Changes[index].Reasons)))).ToImmutableArray();
        return new(preview.Diff, files, preview.Statistics, new(assessment.Size, assessment.Limits, Reasons(assessment.Reasons)),
            new(decision.Mode.ToString(), decision.Disposition.ToString(),
                Enum.GetValues<PatchWriteBlocker>().Where(value => value != PatchWriteBlocker.None && (decision.Blockers & value) != 0)
                    .Select(value => value.ToString()).ToImmutableArray(), decision.RequiresRecoveryRecord, decision.CanCommit));
    }

    private static ImmutableArray<string> Reasons(PatchRiskReason reasons) => Enum.GetValues<PatchRiskReason>()
        .Where(value => value != PatchRiskReason.None && (reasons & value) != 0).Select(value => value.ToString()).ToImmutableArray();
}

internal sealed record PatchFileDiff(PatchChange Summary, PatchStatistics Statistics);

// The display stays bound to its immutable source plan; it never becomes an approval or commit ticket.
internal sealed record PatchPreview(PatchPlan Plan, string Diff,
    ImmutableArray<PatchFileDiff> Files, PatchStatistics Statistics);
