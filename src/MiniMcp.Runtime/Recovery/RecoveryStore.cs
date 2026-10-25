using System.Collections.Immutable;
using System.Security.Cryptography;
using MiniMcp.Configuration;
using MiniMcp.Services;

namespace MiniMcp.Recovery;

internal enum RecoveryPreparationStage { BeforeDirectory, BackupWritten, BeforeManifest, ManifestWritten, BeforeReturn }

// Persists and inspects recovery records. Workspace mutation and approval are separate.
internal sealed partial class RecoveryStore
{
    public const string ManifestFileName = "manifest.json";
    private static readonly SemaphoreSlim Writes = new(1, 1);
    private readonly Guid _runId;
    private readonly RuntimeConfiguration _configuration;
    private readonly RecoveryStorageGuard _guard;
    private readonly Func<RecoveryPreparationStage, RecoveryDraft, Task>? _checkpoint;

    internal Guid RunId => _runId;
    internal string ProjectRoot => _configuration.Settings.ProjectRoot;
    internal ExecutionMode Mode => _configuration.Policy.Mode;

    public RecoveryStore(Guid runId, RuntimeConfiguration configuration) : this(runId, configuration, null) { }
    internal RecoveryStore(Guid runId, RuntimeConfiguration configuration, Func<RecoveryPreparationStage, RecoveryDraft, Task>? checkpoint,
        Func<RecoveryLoadingStage, Task>? loadCheckpoint = null,
        Func<RecoveryManifestReplacementStage, string, Task>? replacementCheckpoint = null)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        ArgumentNullException.ThrowIfNull(configuration);
        _runId = runId;
        _configuration = configuration;
        _guard = new(configuration);
        _checkpoint = checkpoint;
        _loadCheckpoint = loadCheckpoint;
        _replacementCheckpoint = replacementCheckpoint;
    }

    public async Task<PreparedRecord> PrepareAsync(PatchPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(plan);
        if (_configuration.Policy.Mode == ExecutionMode.ReadOnly)
            throw new WorkspaceException("ReadOnly mode does not allow recovery preparation for patch writes.");
        if (!Enum.IsDefined(_configuration.Policy.Mode)) throw new WorkspaceException("Unknown execution mode; recovery preparation is denied.");
        await Writes.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            var draft = RecoveryDraftBuilder.Create(_runId, plan, cancellationToken);
            RecoveryManifestValidator.Validate(draft.Manifest, _configuration.Settings.ProjectRoot, draft.Manifest.ChangeId, cancellationToken);
            var manifest = RecoveryManifestJson.Serialize(draft.Manifest);
            var recordBytes = checked(draft.BackupBytes + manifest.Length);
            _ = _guard.Inspect(recordBytes, cancellationToken);
            EnsureRoot(cancellationToken);
            _guard.CheckLocation(cancellationToken);
            using var storeLock = RecoveryFileSystem.OpenFile(Path.Combine(_configuration.Policy.RecoveryDirectory, RecoveryStorageLimits.LockFileName), FileMode.OpenOrCreate);
            RecoveryFileSystem.Lock(storeLock);
            if (storeLock.Length != 0) throw new InvalidDataException("Recovery lock marker must have zero length.");
            var before = _guard.Inspect(recordBytes, cancellationToken);
            return await WriteRecordAsync(draft, manifest, recordBytes, before, cancellationToken).ConfigureAwait(false);
        }
        finally { Writes.Release(); }
    }

    public void ValidateOwnership(PreparedRecord record, PatchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(plan);
        if (!ReferenceEquals(record.Owner, this) || !ReferenceEquals(record.Plan, plan))
            throw new InvalidOperationException("The prepared recovery record belongs to a different store or patch plan.");
        if (record.IsInvalidated)
            throw new InvalidOperationException("The live recovery record was invalidated after an uncertain update; inspect it locally and prepare a new record.");
    }

    private async Task<PreparedRecord> WriteRecordAsync(RecoveryDraft draft, byte[] manifest, long recordBytes,
        RecoveryStorageUsage before, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_configuration.Policy.RecoveryDirectory, draft.Manifest.ChangeId.ToString("N"));
        var files = new List<(string Path, RecoveryFileIdentity Identity)>();
        RecoveryFileIdentity? directoryIdentity = null;
        try
        {
            await Checkpoint(RecoveryPreparationStage.BeforeDirectory, draft).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _guard.CheckLocation(cancellationToken);
            if (!RecoveryFileSystem.CreateDirectory(directory)) throw new IOException("A recovery record directory already exists; existing data was preserved.");
            directoryIdentity = RecoveryFileSystem.DirectoryIdentity(directory);
            foreach (var backup in draft.Backups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckDirectory(directory, directoryIdentity.Value, cancellationToken);
                await WriteFileAsync(Path.Combine(directory, backup.FileName), backup.Bytes.AsMemory(), files, cancellationToken).ConfigureAwait(false);
                await Checkpoint(RecoveryPreparationStage.BackupWritten, draft).ConfigureAwait(false);
            }
            await Checkpoint(RecoveryPreparationStage.BeforeManifest, draft).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckDirectory(directory, directoryIdentity.Value, cancellationToken);
            await WriteFileAsync(Path.Combine(directory, ManifestFileName), manifest, files, cancellationToken).ConfigureAwait(false);
            await Checkpoint(RecoveryPreparationStage.ManifestWritten, draft).ConfigureAwait(false);
            RecoveryFileSystem.FlushDirectory(directory);
            RecoveryFileSystem.FlushDirectory(_configuration.Policy.RecoveryDirectory);
            await Checkpoint(RecoveryPreparationStage.BeforeReturn, draft).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckDirectory(directory, directoryIdentity.Value, cancellationToken);
            // Reopen after all writes/checkpoints, so later corruption of an earlier backup cannot yield a handle.
            foreach (var backup in draft.Backups)
                await VerifyFileAsync(Path.Combine(directory, backup.FileName), backup.Bytes.AsMemory(), files, cancellationToken).ConfigureAwait(false);
            await VerifyFileAsync(Path.Combine(directory, ManifestFileName), manifest, files, cancellationToken).ConfigureAwait(false);
            if (Directory.EnumerateFileSystemEntries(directory).Take(files.Count + 1).Count() != files.Count)
                throw new IOException("Unexpected entries appeared in the new recovery record.");
            var after = _guard.Measure(cancellationToken);
            if (after.Bytes != checked(before.Bytes + recordBytes) || after.RecordSlots != before.RecordSlots + 1)
                throw new IOException("Recovery storage changed during preparation; no prepared handle was returned.");
            CheckDirectory(directory, directoryIdentity.Value, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedRecord(this, draft, directory, directoryIdentity.Value,
                RecoveryFileSystem.DirectoryIdentity(_configuration.Policy.RecoveryDirectory),
                files.ToImmutableDictionary(file => Path.GetFileName(file.Path), file => file.Identity, StringComparer.Ordinal));
        }
        catch (Exception failure)
        {
            if (directoryIdentity is null) throw;
            var errors = new List<Exception> { failure };
            try
            {
                CheckDirectory(directory, directoryIdentity.Value, CancellationToken.None);
                foreach (var file in files.AsEnumerable().Reverse())
                {
                    try
                    {
                        using (var current = RecoveryFileSystem.OpenFile(file.Path, FileMode.Open))
                            if (RecoveryFileSystem.FileIdentity(current) != file.Identity) throw new IOException("A created recovery file was replaced; cleanup refused to delete it.");
                        File.Delete(file.Path);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add(error); }
                }
                CheckDirectory(directory, directoryIdentity.Value, CancellationToken.None);
                Directory.Delete(directory, recursive: false); // Never remove unknown files or an old record tree.
                RecoveryFileSystem.FlushDirectory(_configuration.Policy.RecoveryDirectory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add(error); }
            if (errors.Count > 1) throw new AggregateException("Recovery preparation failed and cleanup was incomplete; inspect the partial record locally.", errors);
            throw;
        }
    }

    private void EnsureRoot(CancellationToken cancellationToken)
    {
        var missing = new Stack<string>();
        for (var current = _configuration.Policy.RecoveryDirectory; !Directory.Exists(current);)
        {
            cancellationToken.ThrowIfCancellationRequested();
            missing.Push(current);
            current = Path.GetDirectoryName(current) ?? throw new DirectoryNotFoundException("Recovery storage has no accessible existing parent.");
        }
        while (missing.TryPop(out var path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _guard.CheckLocation(cancellationToken);
            _ = RecoveryFileSystem.CreateDirectory(path);
            _guard.CheckLocation(cancellationToken);
            RecoveryFileSystem.FlushDirectory(Path.GetDirectoryName(path)!);
        }
    }

    private void CheckDirectory(string directory, RecoveryFileIdentity identity, CancellationToken cancellationToken)
    {
        _guard.CheckLocation(cancellationToken);
        if (RecoveryFileSystem.DirectoryIdentity(directory) != identity) throw new IOException("The recovery record directory was replaced.");
    }

    private static async Task WriteFileAsync(string path, ReadOnlyMemory<byte> bytes,
        List<(string Path, RecoveryFileIdentity Identity)> files, CancellationToken cancellationToken)
    {
        using var stream = RecoveryFileSystem.OpenFile(path, FileMode.CreateNew);
        files.Add((path, RecoveryFileSystem.FileIdentity(stream)));
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        await VerifyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyFileAsync(string path, ReadOnlyMemory<byte> expected,
        List<(string Path, RecoveryFileIdentity Identity)> files, CancellationToken cancellationToken)
    {
        using var stream = RecoveryFileSystem.OpenFile(path, FileMode.Open);
        if (RecoveryFileSystem.FileIdentity(stream) != files.Single(file => file.Path == path).Identity)
            throw new IOException("A recovery file was replaced during preparation.");
        await VerifyAsync(stream, expected, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyAsync(FileStream stream, ReadOnlyMemory<byte> expected, CancellationToken cancellationToken)
    {
        if (stream.Length != expected.Length) throw new IOException("A recovery file has an unexpected length.");
        var buffer = new byte[expected.Length + 1];
        stream.Position = 0;
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count != expected.Length || !buffer.AsSpan(0, count).SequenceEqual(expected.Span) ||
            !SHA256.HashData(buffer.AsSpan(0, count)).AsSpan().SequenceEqual(SHA256.HashData(expected.Span)))
            throw new IOException("Recovery file readback did not match its immutable snapshot.");
    }

    private Task Checkpoint(RecoveryPreparationStage stage, RecoveryDraft draft) => _checkpoint?.Invoke(stage, draft) ?? Task.CompletedTask;

    // Live preparation proof for this store/plan, never manufactured by parsing a saved manifest.
    // Later commit must still revalidate the record and current workspace state under its mutation lock.
    internal sealed class PreparedRecord
    {
        internal PreparedRecord(RecoveryStore owner, RecoveryDraft draft, string path, RecoveryFileIdentity identity,
            RecoveryFileIdentity storageIdentity, ImmutableDictionary<string, RecoveryFileIdentity> fileIdentities)
        {
            Owner = owner; Plan = draft.Plan; DirectoryPath = path; Identity = identity;
            StorageIdentity = storageIdentity; _revision = new(draft.Manifest, fileIdentities);
        }
        private sealed record Revision(RecoveryManifest Manifest, ImmutableDictionary<string, RecoveryFileIdentity> FileIdentities,
            PatchExecutionContext? Context = null);
        private Revision _revision;
        private int _invalidated;
        internal RecoveryStore Owner { get; }
        internal PatchPlan Plan { get; }
        internal RecoveryFileIdentity Identity { get; }
        internal RecoveryFileIdentity StorageIdentity { get; }
        internal ImmutableDictionary<string, RecoveryFileIdentity> FileIdentities => Volatile.Read(ref _revision).FileIdentities;
        internal bool IsInvalidated => Volatile.Read(ref _invalidated) != 0;
        internal PatchExecutionContext ExecutionContext
        {
            get
            {
                var current = Volatile.Read(ref _revision);
                if (IsInvalidated || current.Manifest.State != RecoveryRecordState.Applying || current.Context is null)
                    throw new InvalidOperationException("A valid Applying record is required for an execution context.");
                return current.Context;
            }
        }
        public RecoveryManifest Manifest => Volatile.Read(ref _revision).Manifest;
        public string DirectoryPath { get; }

        // Only the owning store advances a verified revision while retaining its process/file locks.
        // Publish both fields as one immutable revision; store code combining properties holds its gate.
        internal void AcceptVerifiedReplacement(RecoveryManifestReplacement replacement)
        {
            var current = Volatile.Read(ref _revision);
            var context = current.Context ?? (replacement.Manifest.State == RecoveryRecordState.Applying ? new PatchExecutionContext(Plan) : null);
            var next = new Revision(replacement.Manifest, current.FileIdentities.SetItem(ManifestFileName, replacement.Identity), context);
            Volatile.Write(ref _revision, next);
        }
        internal void Invalidate()
        {
            Interlocked.Exchange(ref _invalidated, 1);
            Volatile.Read(ref _revision).Context?.Invalidate();
        }
    }
}
