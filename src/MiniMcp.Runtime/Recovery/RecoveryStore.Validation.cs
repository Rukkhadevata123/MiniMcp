using MiniMcp.Services;

namespace MiniMcp.Recovery;

internal sealed partial class RecoveryStore
{
    // A read-only observation, not a reservation/approval or validation of current project contents.
    // Future state updates must call the core validator again while retaining the same store lock.
    public async Task RevalidatePreparedAsync(PreparedRecord record, PatchPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOwnership(record, plan);
        await Writes.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            using var storeLock = OpenExistingStoreLock(cancellationToken);
            _ = await ValidatePreparedCoreAsync(record, plan, storeLock.RootIdentity, cancellationToken).ConfigureAwait(false);
        }
        finally { Writes.Release(); }
    }

    private async Task<LoadedRecord> ValidatePreparedCoreAsync(PreparedRecord record, PatchPlan plan,
        RecoveryFileIdentity rootIdentity, CancellationToken cancellationToken, LoadedFile? temporaryManifest = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOwnership(record, plan);
        if (rootIdentity != record.StorageIdentity)
            throw new IOException("The prepared recovery storage directory was replaced.");
        CheckDirectory(record.DirectoryPath, record.Identity, cancellationToken);
        var loaded = await LoadRecordCoreAsync(record.Manifest.ChangeId, rootIdentity, cancellationToken, temporaryManifest).ConfigureAwait(false);
        if (loaded.Identity != record.Identity || loaded.Files.Length != record.FileIdentities.Count)
            throw new IOException("The prepared recovery record identity or file set changed.");
        foreach (var file in loaded.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!record.FileIdentities.TryGetValue(file.Name, out var identity) || file.Identity != identity)
                throw new IOException("A prepared recovery file was replaced.");
        }
        // Compare the complete originally written manifest, not merely its state, ID or selected hashes.
        // A self-consistent rewritten record may be inspectable, but cannot stand in for this preparation.
        var manifest = loaded.Files.Single(file => file.Name == ManifestFileName);
        if (!manifest.Bytes.AsSpan().SequenceEqual(RecoveryManifestJson.Serialize(record.Manifest)))
            throw new InvalidDataException("The prepared recovery manifest changed since preparation.");
        CheckDirectory(record.DirectoryPath, record.Identity, cancellationToken);
        if (RecoveryFileSystem.DirectoryIdentity(_configuration.Policy.RecoveryDirectory) != record.StorageIdentity)
            throw new IOException("The prepared recovery storage directory changed during validation.");
        return loaded;
    }
}
