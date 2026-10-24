using System.Collections.Immutable;
using MiniMcp.Services;

namespace MiniMcp.Recovery;

internal enum RecoveryRecordState { Prepared, Applying, Committed, RolledBack, Incomplete }

// Creation requests are not observed post-commit modes: the process umask can remove permission bits.
internal sealed record RecoveryFileImage(int ByteLength, string Sha256, UnixFileMode? UnixMode, bool UnixModeIsCreateRequest);

// A null image means no file. An existing empty file has a non-null image and SHA-256 of zero bytes.
internal sealed record RecoveryPathState(string Path, RecoveryFileImage? Before, RecoveryFileImage? After, string? BackupFile);
internal sealed record RecoveryOperation(string Action, string Path, string? MoveTo);
internal sealed record RecoveryBackup(string FileName, ImmutableArray<byte> Bytes);

internal sealed record RecoveryManifest(Guid ChangeId, Guid RunId, string ProjectRoot, DateTimeOffset CreatedAtUtc,
    ImmutableArray<RecoveryOperation> Operations, ImmutableArray<RecoveryPathState> Paths)
{
    public int SchemaVersion { get; init; } = 1;
    public RecoveryRecordState State { get; init; } = RecoveryRecordState.Prepared;
}

// An in-memory draft is neither a stored backup nor permission to write. Only the manifest is persisted later.
internal sealed record RecoveryDraft(PatchPlan Plan, RecoveryManifest Manifest, ImmutableArray<RecoveryBackup> Backups)
{
    public long BackupBytes => Backups.Sum(backup => (long)backup.Bytes.Length);
}
