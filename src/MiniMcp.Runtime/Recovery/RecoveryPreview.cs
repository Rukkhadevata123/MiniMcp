using MiniMcp.Services.Patching;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MiniMcp.Recovery;

public sealed record RecoveryConflict(string? Path, string Reason, string Message);
public sealed record RecoveryFilePreview(PatchChange Summary, PatchStatistics Statistics);

// Local read-only observation, not an approval, a stored-record proof or an executable restore plan.
// Restoration re-reads the record, rechecks the workspace and requires explicit local confirmation.
public sealed record RecoveryPreviewResult(Guid RunId, Guid ChangeId, Guid RecordRunId, string RecordState, string Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Diff,
    ImmutableArray<RecoveryFilePreview> Changes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PatchStatistics? Statistics,
    ImmutableArray<RecoveryConflict> Conflicts, ImmutableArray<string> Blockers)
{
    public bool HasConflicts => !Conflicts.IsEmpty;
    public bool RequiresConfirmation => true;
}
