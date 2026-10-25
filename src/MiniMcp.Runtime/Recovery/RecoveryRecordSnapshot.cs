using System.Collections.Immutable;

namespace MiniMcp.Recovery;

// A bounded, self-consistent snapshot of local metadata and original bytes, not proof of authenticity,
// current workspace state, a live PreparedRecord, or authority to commit/restore any state.
internal sealed class RecoveryRecordSnapshot(RecoveryManifest manifest, ImmutableArray<RecoveryBackup> backups)
{
    public RecoveryManifest Manifest { get; } = manifest;
    public ImmutableArray<RecoveryBackup> Backups { get; } = backups;
}
