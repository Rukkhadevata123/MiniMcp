using System.Collections.Immutable;

namespace MiniMcp.Services;

internal sealed partial class PatchService
{
    // Called before releasing the same mutation lock used by the engine. Observations are immutable
    // facts at this point, not permission or a promise that external writers cannot change files later.
    private async Task<PatchCommitOutcome> VerifyOutcomeCoreAsync(PatchPlan plan, PatchCommitOutcome outcome)
    {
        if (outcome.State == PatchCommitState.Incomplete) return outcome.WithVerification(_outcomeIssuer, [], null);
        try
        {
            await Checkpoint(PatchCommitStage.BeforeVerification, -1).ConfigureAwait(false);
            var paths = ImmutableArray.CreateBuilder<VerifiedPatchPath>();
            foreach (var change in plan.Changes)
            {
                if (outcome.State == PatchCommitState.RolledBack)
                {
                    await ObserveAsync(change.Source, change.Before?.Bytes, change.Before?.Mode, creation: false).ConfigureAwait(false);
                    if (change.Source != change.Destination) await ObserveAsync(change.Destination, null, null, creation: false).ConfigureAwait(false);
                }
                else
                {
                    if (change.Source != change.Destination) await ObserveAsync(change.Source, null, null, creation: false).ConfigureAwait(false);
                    await ObserveAsync(change.Destination, change.After, change.Before?.Mode ?? plan.NewFileCreateMode, creation: change.Before is null).ConfigureAwait(false);
                }
            }
            workspace.Resolve(".");
            if (!Directory.Exists(workspace.Root)) throw new WorkspaceException("The project root disappeared during outcome verification.");
            return outcome.WithVerification(_outcomeIssuer, paths.ToImmutable(), null);

            async Task ObserveAsync(string path, ImmutableArray<byte>? expected, UnixFileMode? mode, bool creation)
            {
                var relative = workspace.Relative(path);
                workspace.Resolve(relative);
                if (!Directory.Exists(workspace.Root)) throw new WorkspaceException("The project root disappeared during outcome verification.");
                for (var parent = Path.GetDirectoryName(path); parent != workspace.Root && parent is not null; parent = Path.GetDirectoryName(parent))
                    if (File.Exists(parent)) throw new WorkspaceException("A patch parent became a file during outcome verification.");
                if (expected is null)
                {
                    try { _ = File.GetAttributes(path); }
                    catch (FileNotFoundException) { paths.Add(new(relative, null)); return; }
                    catch (DirectoryNotFoundException) { paths.Add(new(relative, null)); return; }
                    throw new WorkspaceException("A path expected to be absent is occupied after the patch.");
                }
                // Reuse the native non-following/nonblocking regular-file check. Verification never
                // opens FIFOs/devices or silently reads a hard-linked replacement outside the plan.
                var snapshot = await workspace.SnapshotRegularAsync(path, CancellationToken.None).ConfigureAwait(false);
                if (!snapshot.Bytes.AsSpan().SequenceEqual(expected.Value.AsSpan()))
                    throw new WorkspaceException("Patch outcome bytes do not match its plan.");
                if ((!creation && snapshot.Mode != mode) || (creation && snapshot.Mode is { } actual && mode is { } request && (actual & ~request) != 0))
                    throw new WorkspaceException("Patch outcome permissions do not match the plan or its creation request.");
                workspace.Resolve(relative);
                paths.Add(new(relative, snapshot));
            }
        }
        catch (Exception error)
        {
            // Installation/rollback may already have completed. Never run another rollback or replace
            // the engine's actual state/error with an optimistic claim after observation failed.
            return outcome.WithVerification(_outcomeIssuer, [], error);
        }
    }
}
