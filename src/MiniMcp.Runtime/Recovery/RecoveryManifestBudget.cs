using System.Collections.Immutable;

namespace MiniMcp.Recovery;

internal static class RecoveryManifestBudget
{
    // Incomplete is tied for the longest terminal name. Replacing every creation request with false and the
    // largest valid Unix mode bounds all combinations of planned/observed new-file permissions.
    // Serialization enforces the same complete UTF-8 limit as the eventual writer, without file I/O.
    public static int MaximumFinalBytes(RecoveryManifest applying)
    {
        ArgumentNullException.ThrowIfNull(applying);
        if (applying.State != RecoveryRecordState.Applying) throw new InvalidOperationException("An Applying manifest is required for final-state sizing.");
        RecoveryManifestJson.ValidateFields(applying);
        var worst = applying with
        {
            State = RecoveryRecordState.Incomplete,
            Paths = applying.Paths.Select(path => path.After is { UnixModeIsCreateRequest: true } image
                ? path with { After = image with { UnixMode = (UnixFileMode)0xfff, UnixModeIsCreateRequest = false } } : path).ToImmutableArray()
        };
        return RecoveryManifestJson.Serialize(worst).Length;
    }
}
