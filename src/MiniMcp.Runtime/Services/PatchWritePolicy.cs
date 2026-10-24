using MiniMcp.Configuration;

namespace MiniMcp.Services;

internal enum PatchWriteDisposition { Denied, Automatic, RequiresApproval }

[Flags]
internal enum PatchWriteBlocker
{
    None = 0,
    ReadOnlyMode = 1,
    UnknownMode = 2,
    RecoveryUnavailable = 4,
    ApprovalUnavailable = 8
}

// Describes the entire assessed preview. It grants neither approval nor a commit ticket.
internal sealed record PatchWriteDecision(PatchRiskAssessment Assessment, ExecutionMode Mode,
    PatchWriteDisposition Disposition, PatchWriteBlocker Blockers)
{
    public bool RequiresRecoveryRecord => Disposition is PatchWriteDisposition.Automatic or PatchWriteDisposition.RequiresApproval;
    public bool CanCommit => Blockers == PatchWriteBlocker.None;
    public string RejectionMessage => Blockers switch
    {
        PatchWriteBlocker.ReadOnlyMode => "ReadOnly mode does not allow patch writes. Use dry_run=true to preview changes.",
        PatchWriteBlocker.UnknownMode => "Unknown execution mode; patch writes are denied. Use dry_run=true to preview changes.",
        PatchWriteBlocker.RecoveryUnavailable => "Patch writes are unavailable until recovery records are supported. Use dry_run=true to preview changes.",
        PatchWriteBlocker.ApprovalUnavailable | PatchWriteBlocker.RecoveryUnavailable =>
            "Patch writes require local approval and recovery records, which are unavailable. Use dry_run=true to preview changes.",
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
        // Missing services remain blockers. There are no caller-provided approval or recovery-ready switches.
        return mode switch
        {
            ExecutionMode.ReadOnly => new(assessment, mode, PatchWriteDisposition.Denied, PatchWriteBlocker.ReadOnlyMode),
            ExecutionMode.ProjectEdit when assessment.RequiresApproval => new(assessment, mode,
                PatchWriteDisposition.RequiresApproval, PatchWriteBlocker.ApprovalUnavailable | PatchWriteBlocker.RecoveryUnavailable),
            ExecutionMode.ProjectEdit or ExecutionMode.FullAccess => new(assessment, mode,
                PatchWriteDisposition.Automatic, PatchWriteBlocker.RecoveryUnavailable),
            _ => new(assessment, mode, PatchWriteDisposition.Denied, PatchWriteBlocker.UnknownMode)
        };
    }
}
