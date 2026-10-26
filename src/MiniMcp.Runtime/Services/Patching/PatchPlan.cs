using MiniMcp.Services.Workspace;
using System.Collections.Immutable;

namespace MiniMcp.Services.Patching;

// A content snapshot, not an approval. Commit must validate it again under the workspace mutation lock.
internal sealed class PatchPlan(PatchService owner, ImmutableArray<PlannedPatchChange> changes)
{
    internal PatchService Owner { get; } = owner;
    // The creation policy is captured with the plan; Unix creation still applies the process umask.
    public UnixFileMode? NewFileCreateMode { get; } = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public ImmutableArray<PlannedPatchChange> Changes { get; } = changes;
    public PatchResult ToResult(bool dryRun) => new(dryRun, Changes.Select(change => change.Summary).ToArray());
}

internal sealed record PlannedPatchChange(string Source, string Destination, TextFile? Before,
    ImmutableArray<byte>? After, PatchChange Summary, UnixFileMode? ExactAfterMode = null)
{
    // Ordinary adds request the plan's restrictive creation mode. Restoration may require an exact
    // recorded mode; installation and verification must both honor it, including restored additions.
    internal UnixFileMode? TargetMode(PatchPlan plan) => After is null ? null : ExactAfterMode ?? Before?.Mode ?? plan.NewFileCreateMode;
}
