using System.Collections.Immutable;

namespace MiniMcp.Services;

// A content snapshot, not an approval. Commit must validate it again under the workspace mutation lock.
internal sealed class PatchPlan(PatchService owner, ImmutableArray<PlannedPatchChange> changes)
{
    internal PatchService Owner { get; } = owner;
    public ImmutableArray<PlannedPatchChange> Changes { get; } = changes;
    public PatchResult ToResult(bool dryRun) => new(dryRun, Changes.Select(change => change.Summary).ToArray());
}

internal sealed record PlannedPatchChange(string Source, string Destination, TextFile? Before,
    ImmutableArray<byte>? After, PatchChange Summary);
