using MiniMcp.Services.Workspace;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace MiniMcp.Recovery;

internal enum RecoveryLoadingStage { ManifestRead, BackupRead, BeforeReturn }

internal sealed partial class RecoveryStore
{
    private readonly Func<RecoveryLoadingStage, Task>? _loadCheckpoint;

    // Local inspection is allowed in ReadOnly. Nothing is created, repaired, removed or promoted
    // to a live preparation capability. Even a Committed record does not prove current file state.
    public async Task<RecoveryRecordSnapshot> LoadAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (changeId == Guid.Empty) throw new ArgumentException("A nonempty record ID is required.", nameof(changeId));
        await Writes.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            using var storeLock = OpenExistingStoreLock(cancellationToken);
            return (await LoadRecordCoreAsync(changeId, storeLock.RootIdentity, cancellationToken).ConfigureAwait(false)).Snapshot;
        }
        finally { Writes.Release(); }
    }

    // Callers retain both the process gate and store lock until all record checks/operations finish.
    private async Task<LoadedRecord> LoadRecordCoreAsync(Guid changeId, RecoveryFileIdentity rootIdentity, CancellationToken cancellationToken,
        LoadedFile? temporaryManifest = null, bool forCatalog = false)
    {
        var storage = _configuration.Policy.RecoveryDirectory;
        var directory = Path.Combine(storage, changeId.ToString("N"));
        var identity = RecoveryFileSystem.DirectoryIdentity(directory);
        var files = new List<LoadedFile>();
        CheckRecord();
        var manifestFile = await ReadFileAsync(ManifestFileName, RecoveryManifestJson.MaxManifestBytes, null).ConfigureAwait(false);
        var manifest = RecoveryManifestJson.Parse(manifestFile.Bytes.AsSpan());
        RecoveryManifestValidator.Validate(manifest, forCatalog ? manifest.ProjectRoot : _configuration.Settings.ProjectRoot, changeId, cancellationToken);
        await LoadingCheckpoint(RecoveryLoadingStage.ManifestRead).ConfigureAwait(false);
        CheckRecord();
        var expected = manifest.Paths.Where(path => path.Before is not null).ToArray();
        CheckEntries(directory, ExpectedNames(expected.Select(path => path.BackupFile!).Prepend(ManifestFileName)), cancellationToken);
        var backups = ImmutableArray.CreateBuilder<RecoveryBackup>(forCatalog ? 0 : expected.Length);
        foreach (var path in forCatalog ? [] : expected)
        {
            CheckRecord();
            var image = path.Before!;
            var backup = await ReadFileAsync(path.BackupFile!, image.ByteLength, image.ByteLength).ConfigureAwait(false);
            if (Convert.ToHexString(SHA256.HashData(backup.Bytes.AsSpan())) != image.Sha256)
                throw new InvalidDataException("Recovery backup SHA-256 does not match its manifest.");
            ValidateBackupText(backup.Bytes.AsSpan());
            backups.Add(new(path.BackupFile!, backup.Bytes));
            await LoadingCheckpoint(RecoveryLoadingStage.BackupRead).ConfigureAwait(false);
        }
        await LoadingCheckpoint(RecoveryLoadingStage.BeforeReturn).ConfigureAwait(false);
        // Reopen every entry after reading the whole record. Detect replacement or later corruption
        // of an earlier file; this is observation, not isolation from uncooperative external writers.
        foreach (var file in files)
        {
            CheckRecord();
            using var stream = RecoveryFileSystem.OpenRead(Path.Combine(directory, file.Name));
            if (RecoveryFileSystem.FileIdentity(stream) != file.Identity)
                throw new IOException("A recovery file was replaced during loading.");
            await VerifyAsync(stream, file.Bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (RecoveryFileSystem.FileIdentity(stream) != file.Identity)
                throw new IOException("A recovery file changed during loading.");
        }
        CheckEntries(directory, ExpectedNames(forCatalog ? expected.Select(path => path.BackupFile!).Prepend(ManifestFileName) : files.Select(file => file.Name)), cancellationToken);
        CheckRecord();
        return new LoadedRecord(new RecoveryRecordSnapshot(manifest, backups.MoveToImmutable()), identity, files.ToImmutableArray());

        IEnumerable<string> ExpectedNames(IEnumerable<string> names) => temporaryManifest is null ? names : names.Append(temporaryManifest.Name);

        void CheckRecord()
        {
            CheckDirectory(directory, identity, cancellationToken);
            if (RecoveryFileSystem.DirectoryIdentity(storage) != rootIdentity)
                throw new IOException("Recovery storage was replaced during loading.");
        }

        async Task<LoadedFile> ReadFileAsync(string name, int maximum, int? exactLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = RecoveryFileSystem.OpenRead(Path.Combine(directory, name));
            var fileIdentity = RecoveryFileSystem.FileIdentity(stream);
            var length = stream.Length;
            if (length < 0 || length > maximum || (exactLength.HasValue && length != exactLength.Value))
                throw new InvalidDataException("Recovery file length is outside its permitted bound.");
            var buffer = new byte[maximum + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count != length || stream.Length != length || RecoveryFileSystem.FileIdentity(stream) != fileIdentity)
                throw new IOException("A recovery file changed while it was read.");
            var file = new LoadedFile(name, fileIdentity, ImmutableArray.Create(buffer, 0, count));
            files.Add(file);
            return file;
        }
    }

    private ExistingStoreLock OpenExistingStoreLock(CancellationToken cancellationToken)
    {
        _guard.CheckLocation(cancellationToken);
        var storage = _configuration.Policy.RecoveryDirectory;
        var identity = RecoveryFileSystem.DirectoryIdentity(storage);
        // Inspection opens an existing marker with read access, including on read-only files.
        var stream = RecoveryFileSystem.OpenRead(Path.Combine(storage, RecoveryStorageLimits.LockFileName));
        try
        {
            RecoveryFileSystem.Lock(stream);
            if (stream.Length != 0) throw new InvalidDataException("Recovery lock marker must have zero length.");
            _guard.CheckLocation(cancellationToken);
            if (RecoveryFileSystem.DirectoryIdentity(storage) != identity) throw new IOException("Recovery storage was replaced while locking.");
            return new(stream, identity);
        }
        catch { stream.Dispose(); throw; }
    }

    private static void CheckEntries(string directory, IEnumerable<string> expectedNames, CancellationToken cancellationToken)
    {
        var remaining = expectedNames.ToHashSet(StringComparer.Ordinal);
        var maximum = remaining.Count;
        var count = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > maximum || !remaining.Remove(Path.GetFileName(entry)))
                throw new InvalidDataException("The recovery record contains unexpected or noncanonical entries.");
        }
        if (remaining.Count != 0) throw new InvalidDataException("The recovery record is missing required files.");
    }

    private static void ValidateBackupText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains((byte)0)) throw new InvalidDataException("Recovery backups must be UTF-8 text without NUL bytes.");
        try { _ = ProjectWorkspace.Utf8.GetCharCount(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Recovery backups must be valid UTF-8 text.", error); }
    }

    private Task LoadingCheckpoint(RecoveryLoadingStage stage) => _loadCheckpoint?.Invoke(stage) ?? Task.CompletedTask;
    private sealed record LoadedFile(string Name, RecoveryFileIdentity Identity, ImmutableArray<byte> Bytes);
    private sealed record LoadedRecord(RecoveryRecordSnapshot Snapshot, RecoveryFileIdentity Identity, ImmutableArray<LoadedFile> Files);
    private sealed record ExistingStoreLock(FileStream Stream, RecoveryFileIdentity RootIdentity) : IDisposable
    {
        public void Dispose() => Stream.Dispose();
    }
}
