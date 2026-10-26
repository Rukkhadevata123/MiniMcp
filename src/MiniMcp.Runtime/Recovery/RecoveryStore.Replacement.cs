using MiniMcp.Services.Patching;
using MiniMcp.Services.Workspace;
using System.Collections.Immutable;
using MiniMcp.Configuration;

namespace MiniMcp.Recovery;

internal enum RecoveryManifestReplacementStage { BeforeTemporaryFile, TemporaryWritten, BeforeReplace, Replaced, BeforeReturn }

internal sealed class RecoveryManifestReplacementException(bool attempted, string temporaryPath, Exception failure,
    ImmutableArray<Exception> cleanupErrors) : IOException(attempted
        ? "Recovery manifest replacement was attempted but could not be fully verified; inspect the record locally."
        : "Recovery manifest replacement did not start; temporary-file cleanup was incomplete. Inspect the record locally.", failure)
{
    public bool ReplacementAttempted { get; } = attempted;
    public string TemporaryPath { get; } = temporaryPath;
    public ImmutableArray<Exception> CleanupErrors { get; } = cleanupErrors;
}

// Verified storage output only; never a live preparation, permission or evidence of workspace changes.
internal sealed record RecoveryManifestReplacement(RecoveryManifest Manifest, RecoveryFileIdentity Identity);

internal sealed partial class RecoveryStore
{
    private readonly Func<RecoveryManifestReplacementStage, string, Task>? _replacementCheckpoint;

    // Low-level storage primitive. Controlled state APIs advance the live descriptor under this same
    // lock. This entry intentionally leaves that descriptor stale and is not a state-transition API.
    internal async Task<RecoveryManifestReplacement> ReplaceManifestAsync(PreparedRecord record, PatchPlan plan,
        RecoveryManifest replacement, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOwnership(record, plan);
        EnsureManifestWritesAllowed();
        ArgumentNullException.ThrowIfNull(replacement);
        await Writes.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            var bytes = ValidateReplacement(record.Manifest, replacement);
            using var storeLock = OpenExistingStoreLock(cancellationToken);
            var current = await ValidatePreparedCoreAsync(record, plan, storeLock.RootIdentity, cancellationToken).ConfigureAwait(false);
            var before = _guard.Measure(cancellationToken);
            var oldBytes = current.Files.Single(file => file.Name == ManifestFileName).Bytes.Length;
            before.EnsureCanReplaceManifest(current.Files.Sum(file => (long)file.Bytes.Length), oldBytes, bytes.Length);
            return await ReplaceManifestCoreAsync(record, plan, replacement, bytes, before, oldBytes, storeLock.RootIdentity, cancellationToken).ConfigureAwait(false);
        }
        finally { Writes.Release(); }
    }

    private byte[] ValidateReplacement(RecoveryManifest original, RecoveryManifest replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        RecoveryManifestValidator.Validate(replacement, _configuration.Settings.ProjectRoot, original.ChangeId);
        var bytes = RecoveryManifestJson.Serialize(replacement);
        if (replacement.Paths.Length != original.Paths.Length) throw new InvalidDataException("Replacement path states must match the prepared record.");
        // Only status and actual permissions for newly created files can vary. Approval/state policy
        // and observation of those permissions belong to the caller, never to untrusted tool arguments.
        var normalized = replacement with
        {
            State = original.State,
            Paths = replacement.Paths.Select((path, index) => original.Paths[index] is { Before: null, After.UnixModeIsCreateRequest: true } old &&
                path.After is { UnixModeIsCreateRequest: false }
                ? path with { After = path.After with { UnixMode = old.After.UnixMode, UnixModeIsCreateRequest = true } } : path).ToImmutableArray()
        };
        if (!RecoveryManifestJson.Serialize(normalized).AsSpan().SequenceEqual(RecoveryManifestJson.Serialize(original)))
            throw new InvalidDataException("Replacement must preserve prepared identity, operations, file contents and backups.");
        return bytes;
    }

    private void EnsureManifestWritesAllowed()
    {
        if (_configuration.Policy.Mode is not (ExecutionMode.ProjectEdit or ExecutionMode.FullAccess))
            throw new WorkspaceException("The execution mode does not allow recovery manifest writes.");
    }

    private async Task<RecoveryManifestReplacement> ReplaceManifestCoreAsync(PreparedRecord record, PatchPlan plan,
        RecoveryManifest replacement, byte[] bytes, RecoveryStorageUsage before, int oldBytes, RecoveryFileIdentity rootIdentity,
        CancellationToken cancellationToken)
    {
        var name = $".manifest-{Guid.NewGuid():N}.tmp";
        var path = Path.Combine(record.DirectoryPath, name);
        var target = Path.Combine(record.DirectoryPath, ManifestFileName);
        var files = new List<(string Path, RecoveryFileIdentity Identity)>();
        var attempted = false;
        try
        {
            await ReplacementCheckpoint(RecoveryManifestReplacementStage.BeforeTemporaryFile, path).ConfigureAwait(false);
            CheckReplacementLocation(record, cancellationToken);
            await WriteFileAsync(path, bytes, files, cancellationToken).ConfigureAwait(false);
            var temporary = new LoadedFile(name, files.Single().Identity, bytes.ToImmutableArray());
            await ReplacementCheckpoint(RecoveryManifestReplacementStage.TemporaryWritten, path).ConfigureAwait(false);
            await ReplacementCheckpoint(RecoveryManifestReplacementStage.BeforeReplace, path).ConfigureAwait(false);
            // Our one tracked temporary file is the only additional entry tolerated by the common reader.
            _ = await ValidatePreparedCoreAsync(record, plan, rootIdentity, cancellationToken, temporary).ConfigureAwait(false);
            await VerifyReplacementFileAsync(path, temporary.Identity, bytes, flush: false, cancellationToken).ConfigureAwait(false);
            var staged = _guard.Measure(cancellationToken);
            if (staged.RecordSlots != before.RecordSlots || staged.Bytes != checked(before.Bytes + bytes.Length))
                throw new IOException("Recovery usage changed while staging the manifest.");
            CheckReplacementLocation(record, cancellationToken);
            // Recheck both names after measurement, immediately before the replace attempt.
            await VerifyReplacementFileAsync(target, record.FileIdentities[ManifestFileName], RecoveryManifestJson.Serialize(record.Manifest), false, cancellationToken).ConfigureAwait(false);
            await VerifyReplacementFileAsync(path, temporary.Identity, bytes, false, cancellationToken).ConfigureAwait(false);
            if (temporary.Identity.Device != record.FileIdentities[ManifestFileName].Device)
                throw new IOException("Recovery manifest replacement requires the same filesystem device.");
            CheckReplacementLocation(record, cancellationToken);
            attempted = true;
            RecoveryFileSystem.ReplaceFile(path, target);
            // Complete verification once replacement begins, even if cancellation arrives afterwards.
            await ReplacementCheckpoint(RecoveryManifestReplacementStage.Replaced, path).ConfigureAwait(false);
            await VerifyReplacementFileAsync(target, temporary.Identity, bytes, flush: true, CancellationToken.None).ConfigureAwait(false);
            RecoveryFileSystem.FlushDirectory(record.DirectoryPath);
            await ReplacementCheckpoint(RecoveryManifestReplacementStage.BeforeReturn, path).ConfigureAwait(false);
            var after = await LoadRecordCoreAsync(record.Manifest.ChangeId, rootIdentity, CancellationToken.None).ConfigureAwait(false);
            foreach (var file in after.Files)
            {
                var expectedIdentity = file.Name == ManifestFileName ? temporary.Identity : record.FileIdentities[file.Name];
                if (file.Identity != expectedIdentity) throw new IOException("A recovery file was replaced after the manifest replacement.");
            }
            if (!after.Files.Single(file => file.Name == ManifestFileName).Bytes.AsSpan().SequenceEqual(bytes))
                throw new IOException("The final recovery manifest does not match the replacement.");
            var usage = _guard.Measure();
            if (usage.RecordSlots != before.RecordSlots || usage.Bytes != checked(before.Bytes - oldBytes + bytes.Length))
                throw new IOException("Recovery usage changed after manifest replacement.");
            CheckReplacementLocation(record, CancellationToken.None);
            return new(replacement, temporary.Identity);
        }
        catch (Exception failure)
        {
            // An attempted rename may have changed persistent state even if it reported an error.
            // Preserve all evidence in that case; never roll back or delete an uncertain manifest.
            if (attempted) throw new RecoveryManifestReplacementException(true, path, failure, []);
            var errors = ImmutableArray.CreateBuilder<Exception>();
            if (files.Count != 0)
            {
                try
                {
                    CheckReplacementLocation(record, CancellationToken.None);
                    using (var stream = RecoveryFileSystem.OpenRead(path))
                        if (RecoveryFileSystem.FileIdentity(stream) != files.Single().Identity)
                            throw new IOException("The temporary manifest was replaced; cleanup preserved it.");
                    File.Delete(path);
                    RecoveryFileSystem.FlushDirectory(record.DirectoryPath);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { errors.Add(error); }
            }
            if (errors.Count != 0) throw new RecoveryManifestReplacementException(false, path, failure, errors.ToImmutable());
            throw;
        }
    }

    private void CheckReplacementLocation(PreparedRecord record, CancellationToken cancellationToken)
    {
        CheckDirectory(record.DirectoryPath, record.Identity, cancellationToken);
        if (RecoveryFileSystem.DirectoryIdentity(_configuration.Policy.RecoveryDirectory) != record.StorageIdentity)
            throw new IOException("Recovery storage changed during manifest replacement.");
    }

    private static async Task VerifyReplacementFileAsync(string path, RecoveryFileIdentity identity, ReadOnlyMemory<byte> expected,
        bool flush, CancellationToken cancellationToken)
    {
        using var stream = flush ? RecoveryFileSystem.OpenFile(path, FileMode.Open) : RecoveryFileSystem.OpenRead(path);
        if (RecoveryFileSystem.FileIdentity(stream) != identity) throw new IOException("The recovery manifest file identity changed.");
        if (flush) stream.Flush(flushToDisk: true);
        await VerifyAsync(stream, expected, cancellationToken).ConfigureAwait(false);
        if (RecoveryFileSystem.FileIdentity(stream) != identity) throw new IOException("The recovery manifest file changed during verification.");
    }

    private Task ReplacementCheckpoint(RecoveryManifestReplacementStage stage, string temporary) =>
        _replacementCheckpoint?.Invoke(stage, temporary) ?? Task.CompletedTask;
}
