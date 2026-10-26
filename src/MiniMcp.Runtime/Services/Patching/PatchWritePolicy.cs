using MiniMcp.Configuration;

namespace MiniMcp.Services.Patching;

internal enum PatchWriteDisposition { Denied, Automatic }

internal enum PatchWriteBlocker { None, ReadOnlyMode, UnknownMode, ProjectEditRestriction }

// Describes the entire assessed preview; it is not a commit ticket.
internal sealed record PatchWriteDecision(PatchRiskAssessment Assessment, ExecutionMode Mode,
    PatchWriteDisposition Disposition, PatchWriteBlocker Blockers)
{
    public bool RequiresRecoveryRecord => Disposition == PatchWriteDisposition.Automatic;
    public bool CanCommit => Disposition == PatchWriteDisposition.Automatic && Blockers == PatchWriteBlocker.None;
    public string RejectionMessage => Blockers switch
    {
        PatchWriteBlocker.ReadOnlyMode => "ReadOnly mode does not allow patch writes. Use dry_run=true to preview changes.",
        PatchWriteBlocker.UnknownMode => "Unknown execution mode; patch writes are denied. Use dry_run=true to preview changes.",
        PatchWriteBlocker.ProjectEditRestriction =>
            "ProjectEdit restrictions deny this entire patch batch. Use dry_run=true to inspect the reasons; a local user can stop and restart in FullAccess if needed.",
        _ => "Patch writes are unavailable. Use dry_run=true to preview changes."
    };
}

internal static class PatchWritePolicy
{
    public static PatchWriteDecision Decide(PatchRiskAssessment assessment, ExecutionMode mode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(assessment);
        // Eligible attempts always go through real recovery preparation in ToolExecutionService.
        // This pure decision performs no storage preflight and grants no approval or backup capability.
        return mode switch
        {
            ExecutionMode.ReadOnly => new(assessment, mode, PatchWriteDisposition.Denied, PatchWriteBlocker.ReadOnlyMode),
            ExecutionMode.ProjectEdit when assessment.RestrictedInProjectEdit => new(assessment, mode,
                PatchWriteDisposition.Denied, PatchWriteBlocker.ProjectEditRestriction),
            ExecutionMode.ProjectEdit or ExecutionMode.FullAccess => new(assessment, mode,
                PatchWriteDisposition.Automatic, PatchWriteBlocker.None),
            _ => new(assessment, mode, PatchWriteDisposition.Denied, PatchWriteBlocker.UnknownMode)
        };
    }
}
