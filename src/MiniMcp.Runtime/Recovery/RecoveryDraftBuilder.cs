using System.Collections.Immutable;
using System.Security.Cryptography;
using MiniMcp.Services;

namespace MiniMcp.Recovery;

internal static class RecoveryDraftBuilder
{
    public static RecoveryDraft Create(Guid runId, PatchPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        ArgumentNullException.ThrowIfNull(plan);
        var operations = ImmutableArray.CreateBuilder<RecoveryOperation>(plan.Changes.Length);
        var paths = ImmutableArray.CreateBuilder<RecoveryPathState>();
        var backups = ImmutableArray.CreateBuilder<RecoveryBackup>();
        foreach (var change in plan.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var summary = change.Summary;
            operations.Add(new(summary.Action, summary.Path, summary.MoveTo));
            RecoveryFileImage? before = null;
            string? backupFile = null;
            if (change.Before is { } original)
            {
                // Backup names come only from this bounded sequence, never from a project filename.
                backupFile = $"before-{backups.Count:D4}.bin";
                backups.Add(new(backupFile, original.Bytes));
                before = Image(original.Bytes, original.Mode);
            }
            var after = change.After is { } bytes ? Image(bytes,
                change.TargetMode(plan),
                // Until installation is observed, an add's permissions remain a requested target.
                // Exact restoration semantics live in the signed plan and verified engine outcome.
                createRequest: change.Before is null && change.TargetMode(plan) is not null) : null;
            if (summary.MoveTo is { } destination)
            {
                paths.Add(new(summary.Path, before, null, backupFile));
                paths.Add(new(destination, null, after, null));
            }
            else paths.Add(new(summary.Path, before, after, backupFile));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = new RecoveryManifest(Guid.NewGuid(), runId, plan.Owner.ProjectRoot, DateTimeOffset.UtcNow,
            operations.MoveToImmutable(), paths.ToImmutable());
        return new(plan, manifest, backups.ToImmutable());
    }

    private static RecoveryFileImage Image(ImmutableArray<byte> bytes, UnixFileMode? mode, bool createRequest = false) =>
        new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes.AsSpan())), mode, createRequest);
}
