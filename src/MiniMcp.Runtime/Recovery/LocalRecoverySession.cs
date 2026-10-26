using System.Collections.Immutable;
using MiniMcp.Configuration;
using MiniMcp.Services;

namespace MiniMcp.Recovery;

public sealed record RecoveryRecordSummary(Guid ChangeId, string Name, Guid? RecordRunId,
    DateTimeOffset? CreatedAtUtc, string State, int PathCount, string? Error);
public sealed record RecoveryRecordDetails(string ManifestJson, RecoveryPreviewResult Preview)
{
    internal static RecoveryRecordDetails Create(RecoveryManifest manifest, RecoveryPreviewResult preview)
    {
        using var json = System.Text.Json.JsonDocument.Parse(RecoveryManifestJson.Serialize(manifest));
        return new(System.Text.Json.JsonSerializer.Serialize(json, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), preview);
    }
}
public sealed record RecoveryRestoreResult(Guid SourceChangeId, Guid RunId, Guid ChangeId, string State,
    bool CleanupIncomplete, ImmutableArray<string> Errors, DateTimeOffset CreatedAtUtc, int PathCount);

// Local in-process evidence. Only the originating session can execute it, once.
public sealed class LocalRecoveryPreparation
{
    internal LocalRecoveryPreparation(LocalRecoverySession owner, RecoveryRestorePlan plan)
    { Owner = owner; Plan = plan; Record = RecoveryRecordDetails.Create(plan.Source.Manifest, plan.Preview); }
    internal LocalRecoverySession Owner { get; }
    internal RecoveryRestorePlan Plan { get; }
    public RecoveryRecordDetails Record { get; }
    public string ProjectRoot => Owner.Configuration.Settings.ProjectRoot;
    public RecoveryPreviewResult Preview => Record.Preview;
}

/// <summary>Local history access. Standalone sessions own no HTTP host, tunnel or shell process.</summary>
public sealed class LocalRecoverySession : IAsyncDisposable
{
    private readonly ToolExecutionService _execution;
    private readonly bool _ownsExecution;
    private int _disposed;
    internal LocalRecoverySession(ToolExecutionService execution, bool ownsExecution)
    { _execution = execution; _ownsExecution = ownsExecution; }
    public static LocalRecoverySession Open(RuntimeConfiguration configuration) => new(new(Guid.NewGuid(), configuration), true);
    public Guid RunId => _execution.RunId;
    public RuntimeConfiguration Configuration => _execution.Configuration;
    public Task<ImmutableArray<RecoveryRecordSummary>> ListAsync(CancellationToken token = default)
    { EnsureOpen(); return _execution.ListRecoveryAsync(token); }
    public Task<RecoveryRecordDetails> ReadAsync(Guid changeId, CancellationToken token = default)
    { EnsureOpen(); return _execution.ReadRecoveryAsync(changeId, token); }
    public async Task<LocalRecoveryPreparation> PrepareAsync(Guid changeId, CancellationToken token = default)
    { EnsureOpen(); return new(this, await _execution.PrepareRecoveryAsync(changeId, token).ConfigureAwait(false)); }
    // Call only after the local UI/terminal has displayed preparation.Preview and received confirmation.
    public async Task<RecoveryRestoreResult> RestoreAsync(LocalRecoveryPreparation preparation, CancellationToken token = default)
    {
        EnsureOpen(); ArgumentNullException.ThrowIfNull(preparation);
        if (!ReferenceEquals(preparation.Owner, this)) throw new InvalidOperationException("The preparation belongs to another local recovery session.");
        var restored = await _execution.CommitRecoveryAsync(preparation.Plan, token).ConfigureAwait(false);
        var commit = restored.Commit;
        var errors = new[] { commit.Outcome.Failure, commit.Outcome.VerificationFailure }
            .Concat(commit.Outcome.RollbackErrors).Concat(commit.Outcome.CleanupErrors)
            .Where(error => error is not null).Select(error => error!.Message).ToImmutableArray();
        return new(restored.SourceChangeId, commit.Record.Manifest.RunId, commit.Record.Manifest.ChangeId,
            commit.Record.Manifest.State.ToString(), !commit.Outcome.CleanupErrors.IsEmpty, errors,
            commit.Record.Manifest.CreatedAtUtc, commit.Record.Manifest.Paths.Length);
    }
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return _ownsExecution ? _execution.DisposeAsync() : ValueTask.CompletedTask;
    }
}
