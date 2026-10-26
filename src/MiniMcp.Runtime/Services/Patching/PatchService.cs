using MiniMcp.Services.Workspace;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Serialization;

namespace MiniMcp.Services.Patching;

public sealed record PatchChange(string Action, string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? MoveTo, int BeforeBytes, int AfterBytes);
public sealed record PatchResult(bool DryRun, PatchChange[] Changes);

// Independent implementation of the Codex patch syntax; see README for references and differences.
internal sealed partial class PatchService(ProjectWorkspace workspace, Func<PatchCommitStage, int, Task>? checkpoint = null)
{
    private readonly object _outcomeIssuer = new();
    internal string ProjectRoot => workspace.Root;
    internal bool OwnsWorkspace(ProjectWorkspace candidate) => ReferenceEquals(workspace, candidate);

    private sealed record Chunk(string? Anchor, string[] Lines, bool EndOfFile);
    private sealed record Operation(string Action, string Path, string? MoveTo, string? Content, List<Chunk> Chunks);
    private sealed class Change(PlannedPatchChange plan)
    {
        public PlannedPatchChange Plan => plan;
        public string Source => plan.Source;
        public string Destination => plan.Destination;
        public TextFile? Before => plan.Before;
        public ImmutableArray<byte>? After => plan.After;
        public UnixFileMode? ExactAfterMode => plan.ExactAfterMode;
        public string? Temporary { get; set; }
        public string? Backup { get; set; }
        public bool Installed { get; set; }
    }

    public async Task<PatchPlan> PrepareAsync(string patch, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operations = Parse(patch);
        await workspace.Mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            workspace.Resolve(".");
            if (!Directory.Exists(workspace.Root)) throw new WorkspaceException("The project root no longer exists.");
            var changes = new List<PlannedPatchChange>();
            var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var operation in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = workspace.Resolve(operation.Path);
                var destination = operation.MoveTo is null ? source : workspace.Resolve(operation.MoveTo);
                if (!paths.Add(source) || (destination != source && !paths.Add(destination)))
                    throw new WorkspaceException("A patch may touch each path only once; split move chains into separate calls.");
                if (Directory.Exists(source) || Directory.Exists(destination))
                    throw new WorkspaceException("Patch operations require file paths, not directories.");
                if ((operation.Action == "add" || operation.MoveTo is not null) && File.Exists(destination))
                    throw new WorkspaceException("An add/move destination already exists.");
                TextFile? before = operation.Action == "add" ? null : await workspace.SnapshotAsync(source, cancellationToken).ConfigureAwait(false);
                ImmutableArray<byte>? after = operation.Action switch
                {
                    "add" => ProjectWorkspace.Encode(operation.Content!).ToImmutableArray(),
                    "update" => ProjectWorkspace.Encode(Update(before!.Text, operation.Chunks), before.HasBom).ToImmutableArray(),
                    _ => null
                };
                changes.Add(new PlannedPatchChange(source, destination, before, after,
                    new PatchChange(operation.MoveTo is null ? operation.Action : "move", workspace.Relative(source),
                        operation.MoveTo is null ? null : workspace.Relative(destination), before?.Bytes.Length ?? 0, after?.Length ?? 0)));
            }
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var path in paths)
            {
                if (paths.Any(other => other.StartsWith(path + Path.DirectorySeparatorChar, comparison)))
                    throw new WorkspaceException("Patch file paths cannot be ancestors of other patch paths.");
                for (var parent = Path.GetDirectoryName(path); parent != workspace.Root && parent is not null; parent = Path.GetDirectoryName(parent))
                    if (File.Exists(parent)) throw new WorkspaceException("A parent path is a file.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new PatchPlan(this, changes.ToImmutableArray());
        }
        finally { workspace.Mutations.Release(); }
    }

    public async Task<PatchResult> CommitAsync(PatchPlan plan, CancellationToken cancellationToken = default)
    {
        var outcome = await CommitWithOutcomeAsync(plan, cancellationToken).ConfigureAwait(false);
        if (outcome.VerificationFailure is not null)
            throw new WorkspaceException("Patch installation or rollback finished, but the resulting files could not be verified. Inspect the affected paths before continuing.");
        if (outcome.VerifiedState == PatchCommitState.Incomplete)
            throw new WorkspaceException("Patch failed and rollback or staging cleanup was incomplete. Stop editing and inspect .minimcp-* files beside the affected paths; original files may remain in .minimcp-*.bak.");
        if (outcome.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
        return plan.ToResult(dryRun: false);
    }

    // Argument, ownership, lock-wait and initial validation failures still throw before engine work.
    // Once staging begins, the returned outcome describes installation, rollback and cleanup separately.
    internal Task<PatchCommitOutcome> CommitWithOutcomeAsync(PatchPlan plan, CancellationToken cancellationToken = default) =>
        CommitWithOutcomeCoreAsync(plan, null, cancellationToken);

    // Low-level engine entry, not the complete recorded transaction. The coordinator must
    // retain its workspace lock across preparation, execution, observation and final record update.
    internal Task<PatchCommitOutcome> CommitWithContextAsync(PatchPlan plan, PatchExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CommitWithOutcomeCoreAsync(plan, context, cancellationToken);
    }

    internal void ValidateOutcome(PatchPlan plan, PatchCommitOutcome outcome, PatchExecutionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outcome);
        if (!ReferenceEquals(plan.Owner, this) || !outcome.IsIssuedFor(plan, _outcomeIssuer) || (context is not null && !outcome.HasContext(context)))
            throw new InvalidOperationException("The verified patch outcome belongs to another service, plan or execution context.");
    }

    private async Task<PatchCommitOutcome> CommitWithOutcomeCoreAsync(PatchPlan plan, PatchExecutionContext? context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ReferenceEquals(plan.Owner, this)) throw new WorkspaceException("The patch plan belongs to a different patch service.");
        await workspace.Mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteAndVerifyCoreAsync(plan, context, cancellationToken).ConfigureAwait(false);
        }
        finally { workspace.Mutations.Release(); }
    }

    // Caller retains the workspace mutation lock, including any surrounding recovery transaction.
    private async Task<PatchCommitOutcome> ExecuteAndVerifyCoreAsync(PatchPlan plan, PatchExecutionContext? context, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? validateSourceRecord = null)
    {
        // Reject stale plans before creating any directories, staging files or rollback backups.
        if (context is null) await ValidatePlanAsync(plan, cancellationToken).ConfigureAwait(false);
        else
        {
            context.Start(plan);
            try
            {
                await ValidatePlanAsync(plan, cancellationToken).ConfigureAwait(false);
                if (validateSourceRecord is not null) await validateSourceRecord(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                // An Applying attempt that was refused before staging still needs truthful final
                // evidence. Verify the original state; external changes downgrade it to Incomplete.
                var declined = new PatchCommitOutcome(plan, _outcomeIssuer, context, PatchCommitState.RolledBack, failure, [], []);
                return await VerifyOutcomeCoreAsync(plan, declined).ConfigureAwait(false);
            }
        }
        var outcome = await CommitCoreAsync(plan, context, cancellationToken, validateSourceRecord).ConfigureAwait(false);
        return await VerifyOutcomeCoreAsync(plan, outcome).ConfigureAwait(false);
    }

    private static List<Operation> Parse(string patch)
    {
        if (string.IsNullOrEmpty(patch)) throw new WorkspaceException("patch is required.");
        try
        {
            if (ProjectWorkspace.Utf8.GetByteCount(patch) > 1024 * 1024)
                throw new WorkspaceException("Patch exceeds the 1 MiB limit.");
        }
        catch (EncoderFallbackException) { throw new WorkspaceException("Patch is not valid UTF-8 text."); }
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var length = lines.Length;
        if (lines[^1] == "") length--;
        if (length < 3 || lines[0] != "*** Begin Patch" || lines[length - 1] != "*** End Patch")
            throw new WorkspaceException("Expected *** Begin Patch and *** End Patch around file operations.");
        var result = new List<Operation>();
        var i = 1;
        while (i < length - 1)
        {
            var header = lines[i++];
            var action = header.StartsWith("*** Add File: ", StringComparison.Ordinal) ? "add" :
                header.StartsWith("*** Delete File: ", StringComparison.Ordinal) ? "delete" :
                header.StartsWith("*** Update File: ", StringComparison.Ordinal) ? "update" :
                throw new WorkspaceException("Expected an Add File, Delete File or Update File header.");
            var path = header[(header.IndexOf(": ", StringComparison.Ordinal) + 2)..];
            string? move = null, content = null;
            var chunks = new List<Chunk>();
            if (action == "add")
            {
                var added = new StringBuilder();
                while (i < length - 1 && lines[i].StartsWith('+')) added.Append(lines[i++][1..]).Append('\n');
                content = added.ToString();
            }
            if (action == "update")
            {
                if (lines[i].StartsWith("*** Move to: ", StringComparison.Ordinal)) move = lines[i++][13..];
                while (i < length - 1 && (lines[i] == "@@" || lines[i].StartsWith("@@ ", StringComparison.Ordinal)))
                {
                    var anchor = lines[i] == "@@" ? null : lines[i][3..];
                    i++;
                    var edits = new List<string>();
                    while (i < length - 1 && lines[i].Length > 0 && lines[i][0] is ' ' or '+' or '-') edits.Add(lines[i++]);
                    if (edits.Count == 0 || edits.All(line => line[0] == ' '))
                        throw new WorkspaceException("Each @@ chunk must contain a change; blank context lines need a space prefix.");
                    var eof = lines[i] == "*** End of File";
                    if (eof) i++;
                    chunks.Add(new Chunk(anchor, edits.ToArray(), eof));
                    if (eof) break;
                }
                if (chunks.Count == 0 && move is null) throw new WorkspaceException("Update File requires a chunk or Move to header.");
            }
            result.Add(new Operation(action, path, move, content, chunks));
            if (result.Count > 64) throw new WorkspaceException("A patch may contain at most 64 file operations.");
        }
        return result;
    }

    private static string Update(string text, List<Chunk> chunks)
    {
        var original = ProjectWorkspace.SplitLines(text);
        var ending = original.FirstOrDefault(line => line.Ending.Length > 0)?.Ending ?? "\n";
        var finalNewline = original.Count == 0 || original[^1].Ending.Length > 0;
        var output = new List<TextLine>();
        var cursor = 0;
        foreach (var chunk in chunks)
        {
            var start = cursor;
            if (chunk.Anchor is not null)
                start = FindUnique(original, [chunk.Anchor], cursor, false) + 1;
            var old = chunk.Lines.Where(line => line[0] != '+').Select(line => line[1..]).ToArray();
            var index = old.Length == 0 ? original.Count : FindUnique(original, old, start, chunk.EndOfFile);
            output.AddRange(original.GetRange(cursor, index - cursor));
            var consumed = index;
            foreach (var edit in chunk.Lines)
            {
                if (edit[0] == ' ') output.Add(original[consumed++]);
                else if (edit[0] == '-') consumed++;
                else output.Add(new TextLine(edit[1..], ending));
            }
            cursor = consumed;
        }
        output.AddRange(original.Skip(cursor));
        var result = new StringBuilder();
        for (var i = 0; i < output.Count; i++)
        {
            var line = output[i];
            result.Append(line.Text);
            result.Append(i == output.Count - 1 && !finalNewline ? "" : line.Ending.Length == 0 ? ending : line.Ending);
        }
        return result.ToString();
    }

    private static int FindUnique(List<TextLine> lines, string[] expected, int start, bool endOfFile)
    {
        if (endOfFile)
        {
            var index = lines.Count - expected.Length;
            if (index >= start && lines.Skip(index).Select(line => line.Text).SequenceEqual(expected)) return index;
            throw new WorkspaceException("Patch context did not match exactly at the end of the file.");
        }
        // Prefix matching keeps repeated lines linear in the file/context size.
        var prefix = new int[expected.Length];
        for (int i = 1, length = 0; i < expected.Length; i++)
        {
            while (length > 0 && expected[i] != expected[length]) length = prefix[length - 1];
            if (expected[i] == expected[length]) length++;
            prefix[i] = length;
        }
        var match = -1;
        for (int i = start, length = 0; i < lines.Count; i++)
        {
            while (length > 0 && lines[i].Text != expected[length]) length = prefix[length - 1];
            if (lines[i].Text == expected[length]) length++;
            if (length != expected.Length) continue;
            if (match >= 0) throw new WorkspaceException("Patch context is ambiguous; include more context or an exact @@ anchor.");
            match = i - expected.Length + 1;
            length = prefix[length - 1];
        }
        if (match < 0) throw new WorkspaceException("Patch context did not match exactly; read the file again.");
        return match;
    }

    private async Task ValidatePlanAsync(PatchPlan plan, CancellationToken cancellationToken)
    {
        workspace.Resolve(".");
        if (!Directory.Exists(workspace.Root)) throw new WorkspaceException("The project root no longer exists.");
        foreach (var change in plan.Changes)
            await ValidateChangeAsync(change, cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateChangeAsync(PlannedPatchChange change, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        workspace.Resolve(workspace.Relative(change.Source));
        workspace.Resolve(workspace.Relative(change.Destination));
        if (!Directory.Exists(workspace.Root)) throw new WorkspaceException("The project root no longer exists.");
        if (Directory.Exists(change.Source) || Directory.Exists(change.Destination))
            throw new WorkspaceException("A patch path became a directory; prepare a new plan.");
        foreach (var path in new[] { change.Source, change.Destination })
            for (var parent = Path.GetDirectoryName(path); parent != workspace.Root && parent is not null; parent = Path.GetDirectoryName(parent))
                if (File.Exists(parent)) throw new WorkspaceException("A patch parent path became a file; prepare a new plan.");
        if (change.Before is not null)
        {
            var current = await workspace.SnapshotRegularAsync(change.Source, cancellationToken).ConfigureAwait(false);
            if (!current.Bytes.AsSpan().SequenceEqual(change.Before.Bytes.AsSpan()) || current.Mode != change.Before.Mode)
                throw new WorkspaceException("A file changed since the patch was prepared; prepare a new plan.");
        }
        if ((change.Before is null || change.Source != change.Destination) && File.Exists(change.Destination))
            throw new WorkspaceException("A patch destination appeared since the patch was prepared.");
    }

    private async Task<PatchCommitOutcome> CommitCoreAsync(PatchPlan plan, PatchExecutionContext? context, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? validateSourceRecord)
    {
        var changes = plan.Changes.Select(change => new Change(change)).ToList();
        var directories = new List<string>();
        var committed = false;
        Exception? failure = null;
        var rollbackErrors = ImmutableArray.CreateBuilder<Exception>();
        var cleanupErrors = ImmutableArray.CreateBuilder<Exception>();
        try
        {
            // Stage all new contents before changing any existing file.
            foreach (var change in changes.Where(change => change.After is not null))
            {
                var parent = Path.GetDirectoryName(change.Destination)!;
                var missing = new Stack<string>();
                for (var current = parent; !Directory.Exists(current); current = Path.GetDirectoryName(current)!) missing.Push(current);
                while (missing.TryPop(out var directory)) { Directory.CreateDirectory(directory); directories.Add(directory); }
                workspace.Resolve(workspace.Relative(change.Destination));
                change.Temporary = Path.Combine(parent, $".minimcp-{Guid.NewGuid():N}.tmp");
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(change.Temporary, options))
                {
                    await stream.WriteAsync(change.After!.Value.AsMemory(), cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                if (!OperatingSystem.IsWindows() && (change.ExactAfterMode ?? change.Before?.Mode) is { } mode)
                    File.SetUnixFileMode(change.Temporary, mode);
            }
            await Checkpoint(PatchCommitStage.Staged, -1).ConfigureAwait(false);
            await ValidatePlanAsync(plan, cancellationToken).ConfigureAwait(false);
            if (validateSourceRecord is not null) await validateSourceRecord(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Once commit starts, finish or roll back instead of leaving half a patch on cancellation.
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                await Checkpoint(PatchCommitStage.BeforeInstall, i).ConfigureAwait(false);
                if (validateSourceRecord is not null) await validateSourceRecord(CancellationToken.None).ConfigureAwait(false);
                // Earlier operations may already be installed, so recheck this untouched operation
                // rather than the full original plan. Late cancellation must not split the batch.
                await ValidateChangeAsync(plan.Changes[i], CancellationToken.None).ConfigureAwait(false);
                if (change.Before is not null)
                {
                    var backup = Path.Combine(Path.GetDirectoryName(change.Source)!, $".minimcp-{Guid.NewGuid():N}.bak");
                    File.Move(change.Source, backup);
                    change.Backup = backup;
                }
                if (change.Temporary is not null)
                {
                    File.Move(change.Temporary, change.Destination);
                    change.Temporary = null;
                    change.Installed = true;
                }
                await Checkpoint(PatchCommitStage.Installed, i).ConfigureAwait(false);
            }
            if (validateSourceRecord is not null) await validateSourceRecord(CancellationToken.None).ConfigureAwait(false);
            committed = true;
        }
        catch (Exception error)
        {
            failure = error;
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                try
                {
                    workspace.Resolve(workspace.Relative(change.Source));
                    workspace.Resolve(workspace.Relative(change.Destination));
                    if (change.Installed)
                    {
                        // Undo only the bytes/mode installed by this attempt. An external edit is
                        // preserved with its rollback backup and an explicit Incomplete outcome.
                        await VerifyInstalledForRollbackAsync(change.Plan, plan).ConfigureAwait(false);
                        File.Delete(change.Destination);
                    }
                    if (change.Backup is not null) { File.Move(change.Backup, change.Source); change.Backup = null; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceException) { rollbackErrors.Add(ex); }
            }
        }
        finally
        {
            foreach (var change in changes)
            {
                if (change.Temporary is not null) TryDelete(change.Temporary, cleanupErrors);
                if (committed && change.Backup is not null) TryDelete(change.Backup, cleanupErrors);
            }
            if (!committed)
                foreach (var directory in directories.AsEnumerable().Reverse())
                    try { Directory.Delete(directory); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { cleanupErrors.Add(error); }
        }
        var state = committed ? PatchCommitState.Committed : rollbackErrors.Count == 0 && cleanupErrors.Count == 0
            ? PatchCommitState.RolledBack : PatchCommitState.Incomplete;
        return new PatchCommitOutcome(plan, _outcomeIssuer, context, state, failure, rollbackErrors.ToImmutable(), cleanupErrors.ToImmutable());
    }

    private async Task VerifyInstalledForRollbackAsync(PlannedPatchChange change, PatchPlan plan)
    {
        var current = await workspace.SnapshotRegularAsync(change.Destination, CancellationToken.None).ConfigureAwait(false);
        var mode = change.TargetMode(plan);
        var creation = change.Before is null && change.ExactAfterMode is null;
        if (change.After is null || !current.Bytes.AsSpan().SequenceEqual(change.After.Value.AsSpan()) ||
            (!creation && current.Mode != mode) || (creation && current.Mode is { } actual && mode is { } request && (actual & ~request) != 0))
            throw new IOException("Rollback refused to overwrite a file changed after installation; the original backup was preserved.");
    }

    private Task Checkpoint(PatchCommitStage stage, int index) => checkpoint?.Invoke(stage, index) ?? Task.CompletedTask;

    private static void TryDelete(string path, ImmutableArray<Exception>.Builder errors)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add(error); }
    }
}
