using System.Collections.Immutable;

namespace MiniMcp.Recovery;

internal sealed partial class RecoveryStore
{
    public async Task<ImmutableArray<RecoveryRecordSummary>> ListAsync(CancellationToken token)
    {
        await Writes.WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            _guard.CheckLocation(token);
            var storage = _configuration.Policy.RecoveryDirectory;
            if (!Directory.Exists(storage) || !Directory.EnumerateFileSystemEntries(storage).Any()) return [];
            using var held = OpenExistingStoreLock(token);
            _ = _guard.Measure(token);
            var rows = ImmutableArray.CreateBuilder<RecoveryRecordSummary>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(storage))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                if (name == RecoveryStorageLimits.LockFileName) continue;
                var id = Guid.TryParseExact(name, "N", out var parsed) && parsed.ToString("N") == name ? parsed : Guid.Empty;
                try
                {
                    if (id == Guid.Empty) throw new InvalidDataException("Recovery entry has a noncanonical record name.");
                    var loaded = await LoadRecordCoreAsync(id, held.RootIdentity, token, forCatalog: true).ConfigureAwait(false);
                    var manifest = loaded.Snapshot.Manifest;
                    if (!string.Equals(manifest.ProjectRoot, ProjectRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                    rows.Add(new(id, name, manifest.RunId, manifest.CreatedAtUtc, manifest.State.ToString(), manifest.Paths.Length, null));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                { rows.Add(new(id, name, null, null, "Unavailable", 0, Diagnostics.RuntimeDiagnosticText.Sanitize(error.Message))); }
            }
            _guard.CheckLocation(token);
            if (RecoveryFileSystem.DirectoryIdentity(storage) != held.RootIdentity) throw new IOException("Recovery storage changed during listing.");
            return rows.OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.Name, StringComparer.Ordinal).ToImmutableArray();
        }
        finally { Writes.Release(); }
    }
}
