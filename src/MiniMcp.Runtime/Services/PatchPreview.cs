using System.Collections.Immutable;

namespace MiniMcp.Services;

// Byte counts describe complete +/- UTF-8 lines (including their endings) and BOM changes.
// Context, display escapes and path-only moves do not contribute to these counts.
public sealed record PatchStatistics(int AddedLines, int RemovedLines, long AddedBytes, long RemovedBytes)
{
    public static PatchStatistics operator +(PatchStatistics left, PatchStatistics right) => new(
        left.AddedLines + right.AddedLines, left.RemovedLines + right.RemovedLines,
        left.AddedBytes + right.AddedBytes, left.RemovedBytes + right.RemovedBytes);
}

public sealed record PatchFilePreview(PatchChange Summary, PatchStatistics Statistics);

public sealed record PatchPreviewResult(string Diff, ImmutableArray<PatchFilePreview> Changes, PatchStatistics Statistics)
{
    public bool DryRun => true;
}

// The display stays bound to its immutable source plan; it never becomes an approval or commit ticket.
internal sealed record PatchPreview(PatchPlan Plan, string Diff,
    ImmutableArray<PatchFilePreview> Files, PatchStatistics Statistics)
{
    public PatchPreviewResult ToResult() => new(Diff, Files, Statistics);
}
