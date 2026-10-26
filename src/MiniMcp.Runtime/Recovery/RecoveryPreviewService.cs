using MiniMcp.Services.Patching;
using MiniMcp.Services.Workspace;
using System.Collections.Immutable;
using System.Security.Cryptography;
using MiniMcp.Configuration;

namespace MiniMcp.Recovery;

internal enum RecoveryPreviewStage { RecordLoaded, PathRead, BeforeReturn }
internal sealed record RecoveryInspection(RecoveryPreviewResult Preview, RecoveryRecordSnapshot Record, ImmutableArray<PatchDiffChange> Changes);

internal sealed class RecoveryPreviewService
{
    private readonly ProjectWorkspace _workspace;
    private readonly RecoveryStore _store;
    private readonly Func<RecoveryPreviewStage, string?, Task>? _checkpoint;

    public RecoveryPreviewService(ProjectWorkspace workspace, RecoveryStore store,
        Func<RecoveryPreviewStage, string?, Task>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(store);
        if (!string.Equals(workspace.Root, store.ProjectRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Recovery preview storage and workspace must refer to the same project.", nameof(store));
        _workspace = workspace; _store = store; _checkpoint = checkpoint;
    }

    public async Task<RecoveryPreviewResult> PreviewAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        if (changeId == Guid.Empty) throw new ArgumentException("A nonempty recovery record ID is required.", nameof(changeId));
        // Keep the same lock order as recorded commits: workspace first, then short store operations.
        // Release store locks during workspace observation and diff computation.
        await _workspace.Mutations.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            return (await InspectUnderLockAsync(changeId, cancellationToken).ConfigureAwait(false)).Preview;
        }
        finally { _workspace.Mutations.Release(); }
    }

    internal bool IsFor(ProjectWorkspace workspace, RecoveryStore store) => ReferenceEquals(_workspace, workspace) && ReferenceEquals(_store, store);

    public async Task<RecoveryRecordDetails> ReadAsync(Guid changeId, CancellationToken token)
    {
        if (changeId == Guid.Empty) throw new ArgumentException("A nonempty recovery record ID is required.", nameof(changeId));
        await _workspace.Mutations.WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            var inspection = await InspectUnderLockAsync(changeId, token).ConfigureAwait(false);
            return RecoveryRecordDetails.Create(inspection.Record.Manifest, inspection.Preview);
        }
        finally { _workspace.Mutations.Release(); }
    }

    // The local preview or restore coordinator owns this exact workspace's mutation lock.
    internal async Task<RecoveryInspection> InspectUnderLockAsync(Guid changeId, CancellationToken cancellationToken)
    {
        var record = await _store.LoadAsync(changeId, cancellationToken).ConfigureAwait(false);
        await Checkpoint(RecoveryPreviewStage.RecordLoaded, null).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var conflicts = new List<RecoveryConflict>();
        PatchDiff? diff = null;
        ImmutableArray<PatchDiffChange> changes = [];
        if (record.Manifest.State == RecoveryRecordState.Committed)
        {
            var files = await ObserveAsync(record.Manifest, conflicts, reportCheckpoints: true, cancellationToken).ConfigureAwait(false);
            if (conflicts.Count == 0)
            {
                var rendered = await Task.Run(() =>
                {
                    var inverse = ReverseChanges(record, files, cancellationToken);
                    return (Changes: inverse, Diff: PatchDiffBuilder.Create(inverse, cancellationToken, forRecovery: true));
                }, cancellationToken).ConfigureAwait(false);
                changes = rendered.Changes; diff = rendered.Diff;
            }
        }
        await Checkpoint(RecoveryPreviewStage.BeforeReturn, null).ConfigureAwait(false);
        // Do not publish a clean preview from data changed while another path/diff was inspected.
        var latest = await _store.LoadAsync(changeId, cancellationToken).ConfigureAwait(false);
        if (!SameRecord(record, latest)) conflicts.Add(new(null, "RecordChanged", "The recovery record changed during preview; reload it before continuing."));
        if (record.Manifest.State == RecoveryRecordState.Committed)
            _ = await ObserveAsync(record.Manifest, conflicts, reportCheckpoints: false, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var blockers = ImmutableArray.CreateBuilder<string>();
        if (_store.Mode == ExecutionMode.ReadOnly) blockers.Add("ReadOnlyMode");
        else if (_store.Mode is not (ExecutionMode.ProjectEdit or ExecutionMode.FullAccess)) blockers.Add("UnknownMode");
        if (record.Manifest.State != RecoveryRecordState.Committed) blockers.Add("RecordNotCommitted");
        if (conflicts.Count != 0) { blockers.Add("Conflicts"); diff = null; changes = []; }
        var preview = new RecoveryPreviewResult(_store.RunId, changeId, record.Manifest.RunId, record.Manifest.State.ToString(), _store.Mode.ToString(),
            diff?.Diff, diff?.Files.Select(file => new RecoveryFilePreview(file.Summary, file.Statistics)).ToImmutableArray() ?? [], diff?.Statistics,
            conflicts.DistinctBy(conflict => (conflict.Path, conflict.Reason)).ToImmutableArray(), blockers.ToImmutable());
        return new(preview, record, changes);
    }

    internal async Task ValidateRecordAsync(RecoveryRecordSnapshot expected, CancellationToken cancellationToken)
    {
        var current = await _store.LoadAsync(expected.Manifest.ChangeId, cancellationToken).ConfigureAwait(false);
        if (!SameRecord(expected, current)) throw new WorkspaceException("The recovery source record changed since preview; obtain a new preview before restoring.");
    }

    private async Task<Dictionary<string, TextFile>> ObserveAsync(RecoveryManifest manifest, List<RecoveryConflict> conflicts,
        bool reportCheckpoints, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, TextFile>(StringComparer.Ordinal);
        try
        {
            _workspace.Resolve(".");
            if (!Directory.Exists(_workspace.Root))
            { conflicts.Add(new(".", "ProjectMissing", "The project root no longer exists.")); return files; }
        }
        catch (WorkspaceException)
        { conflicts.Add(new(".", "UnsafePath", "The project root or an ancestor is a link or is unsafe to inspect.")); return files; }

        foreach (var path in manifest.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var conflict = await ObservePathAsync(path, files, cancellationToken).ConfigureAwait(false);
            if (conflict is not null) conflicts.Add(conflict);
            if (reportCheckpoints) await Checkpoint(RecoveryPreviewStage.PathRead, path.Path).ConfigureAwait(false);
        }
        return files;
    }

    private async Task<RecoveryConflict?> ObservePathAsync(RecoveryPathState path, Dictionary<string, TextFile> files, CancellationToken cancellationToken)
    {
        try
        {
            var full = _workspace.Resolve(path.Path);
            for (var parent = Path.GetDirectoryName(full); parent != _workspace.Root && parent is not null; parent = Path.GetDirectoryName(parent))
            {
                var attributes = Attributes(parent);
                if (attributes.HasValue && (attributes.Value & FileAttributes.Directory) == 0)
                    return new(path.Path, "ParentNotDirectory", "A parent of this path is occupied by a file.");
            }
            var entry = Attributes(full);
            if (path.After is null)
                return entry is null ? null : new(path.Path, "PathOccupied", "This path should be absent after the recorded change but is now occupied.");
            if (entry is null) return new(path.Path, "FileMissing", "A file from the recorded result is missing.");
            if ((entry.Value & (FileAttributes.Directory | FileAttributes.Device)) != 0)
                return new(path.Path, "NotRegularFile", "The recorded file is occupied by a directory or non-regular entry.");
            var current = await _workspace.SnapshotRegularAsync(full, cancellationToken).ConfigureAwait(false);
            if (current.Bytes.Length != path.After.ByteLength || Convert.ToHexString(SHA256.HashData(current.Bytes.AsSpan())) != path.After.Sha256)
                return new(path.Path, "ContentChanged", "Current file bytes do not match the recorded result; newer content will not be overwritten.");
            if (current.Mode != path.After.UnixMode)
                return new(path.Path, "PermissionsChanged", "Current permissions do not match the recorded result.");
            files.Add(path.Path, current);
            return null;
        }
        catch (WorkspaceException error) { return new(path.Path, "UnsafeOrUnsupportedFile", error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(path.Path, "UnsafeOrUnreadableFile", "This path could not be inspected safely; check links, file type and access permissions."); }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static ImmutableArray<PatchDiffChange> ReverseChanges(RecoveryRecordSnapshot record, Dictionary<string, TextFile> files, CancellationToken cancellationToken)
    {
        var paths = record.Manifest.Paths.ToDictionary(path => path.Path, StringComparer.Ordinal);
        var backups = record.Backups.ToDictionary(backup => backup.FileName, StringComparer.Ordinal);
        var changes = ImmutableArray.CreateBuilder<PatchDiffChange>(record.Manifest.Operations.Length);
        foreach (var operation in record.Manifest.Operations.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = paths[operation.Path];
            var source = operation.MoveTo ?? operation.Path;
            files.TryGetValue(source, out var before);
            var after = original.BackupFile is { } backup ? backups[backup].Bytes : (ImmutableArray<byte>?)null;
            var action = operation.Action switch { "add" => "delete", "delete" => "add", "move" => "move", _ => "update" };
            changes.Add(new(new(action, source, operation.MoveTo is null ? null : operation.Path, before?.Bytes.Length ?? 0, after?.Length ?? 0),
                before, after, original.Before?.UnixMode, AfterModeIsCreateRequest: false));
        }
        return changes.MoveToImmutable();
    }

    private static bool SameRecord(RecoveryRecordSnapshot left, RecoveryRecordSnapshot right) =>
        RecoveryManifestJson.Serialize(left.Manifest).AsSpan().SequenceEqual(RecoveryManifestJson.Serialize(right.Manifest)) &&
        left.Backups.Length == right.Backups.Length && left.Backups.Zip(right.Backups).All(pair =>
            pair.First.FileName == pair.Second.FileName && pair.First.Bytes.AsSpan().SequenceEqual(pair.Second.Bytes.AsSpan()));

    private Task Checkpoint(RecoveryPreviewStage stage, string? path) => _checkpoint?.Invoke(stage, path) ?? Task.CompletedTask;
}
