using MiniMcp.Services.Workspace;
using System.Text.Json;

namespace MiniMcp.Recovery;

// Metadata consistency only: storage links, backup bytes and current workspace state require later checks.
// Successful validation does not produce a stored-record capability or permission to commit/restore.
internal static class RecoveryManifestValidator
{
    private const string EmptySha256 = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";
    private static StringComparer Paths => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static void Validate(RecoveryManifest manifest, string expectedProjectRoot, Guid expectedChangeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProjectRoot);
        if (!Path.IsPathFullyQualified(expectedProjectRoot) || NormalizeRoot(expectedProjectRoot) != expectedProjectRoot)
            throw new ArgumentException("Use the validated, normalized absolute workspace root.", nameof(expectedProjectRoot));
        if (expectedChangeId == Guid.Empty) throw new ArgumentException("An expected change ID is required.", nameof(expectedChangeId));
        RecoveryManifestJson.ValidateFields(manifest);
        Require(manifest.ChangeId == expectedChangeId, "$.changeId", "The change ID does not match the selected record.");
        string normalizedRecordRoot;
        try { normalizedRecordRoot = NormalizeRoot(manifest.ProjectRoot); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new JsonException("Invalid recovery workspace root.", "$.projectRoot", null, null); }
        Require(normalizedRecordRoot == manifest.ProjectRoot && Paths.Equals(manifest.ProjectRoot, expectedProjectRoot),
            "$.projectRoot", "The record must name the expected normalized workspace root.");

        var states = new Dictionary<string, (RecoveryPathState State, int Index)>(Paths);
        var backups = new HashSet<string>(StringComparer.Ordinal);
        var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < manifest.Paths.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = manifest.Paths[i];
            var field = $"$.paths[{i}]";
            ProjectPath(state.Path, expectedProjectRoot, field + ".path");
            Require(states.TryAdd(state.Path, (state, i)), field + ".path", "Each project path must appear only once.");
            CheckImage(state.Before, field + ".before", lengths);
            CheckImage(state.After, field + ".after", lengths);
            Require(state.Before?.UnixModeIsCreateRequest != true, field + ".before.unixModeIsCreateRequest", "Original permissions must be observed values.");
            if (state.Before is null)
                Require(state.BackupFile is null, field + ".backupFile", "An absent original must not reference a backup.");
            else
                Require(IsBackupName(state.BackupFile) && backups.Add(state.BackupFile!), field + ".backupFile", "Each original needs a unique generated backup name.");
        }
        foreach (var (path, entry) in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                Require(!states.ContainsKey(path[..slash]), $"$.paths[{entry.Index}].path", "File paths cannot be ancestors of other file paths.");
        }

        var used = new HashSet<string>(Paths);
        for (var i = 0; i < manifest.Operations.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = manifest.Operations[i];
            var field = $"$.operations[{i}]";
            var source = Consume(operation.Path, field + ".path");
            Require(operation.Action == "move" || operation.MoveTo is null, field + ".moveTo", "Only moves may specify a destination.");
            switch (operation.Action)
            {
                case "add":
                    Require(source.Before is null && source.After is not null, field, "An add requires an absent original and a resulting file.");
                    var image = source.After!;
                    Require(manifest.State != RecoveryRecordState.Committed || !image.UnixModeIsCreateRequest, field,
                        "A committed add must record actual permissions, not a creation request.");
                    if (manifest.State is RecoveryRecordState.Prepared or RecoveryRecordState.RolledBack)
                        Require(image.UnixModeIsCreateRequest == image.UnixMode.HasValue, field, "A planned add must identify its Unix creation request.");
                    break;
                case "update":
                    Require(source.Before is not null && source.After is not null, field, "An update requires both file images.");
                    PreserveMode(source.Before!, source.After!, field);
                    break;
                case "delete":
                    Require(source.Before is not null && source.After is null, field, "A delete requires an original and an absent result.");
                    break;
                case "move":
                    var destination = Consume(operation.MoveTo!, field + ".moveTo");
                    Require(source.Before is not null && source.After is null && destination.Before is null && destination.After is not null,
                        field, "A move requires the source to disappear and the previously absent destination to contain the result.");
                    PreserveMode(source.Before!, destination.After!, field);
                    break;
                default:
                    throw new JsonException("Unknown recovery operation action.", field + ".action", null, null);
            }
        }
        Require(used.Count == states.Count, "$.paths", "All path states must belong to exactly one operation.");
        cancellationToken.ThrowIfCancellationRequested();

        RecoveryPathState Consume(string path, string field)
        {
            ProjectPath(path, expectedProjectRoot, field);
            Require(states.TryGetValue(path, out var entry), field, "An operation is missing its path state.");
            Require(entry.State!.Path == path && used.Add(path), field, "Operations must use the same path spelling and cannot share paths or form move chains.");
            return entry.State;
        }
    }

    private static string NormalizeRoot(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void ProjectPath(string path, string root, string field)
    {
        Require(!string.IsNullOrWhiteSpace(path) && path.Split('/').All(part => part.Length != 0 && part is not "." and not ".."),
            field, "Use nonempty canonical project path components without dot segments or a trailing slash.");
        try
        {
            var full = ProjectWorkspace.ResolvePath(root, path);
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            Require(path != "." && relative == path, field, "Use a canonical relative project file path.");
            Require(!OperatingSystem.IsWindows() || path.Split('/').All(part => part.TrimEnd(' ', '.') == part),
                field, "Trailing spaces and dots are not canonical Windows path components.");
            _ = ProjectWorkspace.Utf8.GetByteCount(path);
        }
        catch (Exception error) when (error is WorkspaceException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new JsonException("Invalid recovery project path: " + error.Message, field, null, null);
        }
    }

    private static void CheckImage(RecoveryFileImage? image, string field, Dictionary<string, int> lengths)
    {
        if (image is null) return;
        Require(image.UnixMode.HasValue == !OperatingSystem.IsWindows(), field + ".unixMode", "Permissions must match the workspace platform's tracking rules.");
        Require((image.ByteLength == 0) == (image.Sha256 == EmptySha256), field + ".sha256", "The empty-content SHA-256 must describe exactly zero bytes.");
        Require(!lengths.TryGetValue(image.Sha256, out var length) || length == image.ByteLength, field + ".byteLength", "Identical hashes cannot describe different lengths.");
        lengths[image.Sha256] = image.ByteLength;
    }

    private static void PreserveMode(RecoveryFileImage before, RecoveryFileImage after, string field) =>
        Require(!after.UnixModeIsCreateRequest && after.UnixMode == before.UnixMode, field, "Updates and moves must preserve observed original permissions.");

    private static bool IsBackupName(string? name) => name is { Length: 15 } && name.StartsWith("before-", StringComparison.Ordinal) &&
        name.EndsWith(".bin", StringComparison.Ordinal) && name.AsSpan(7, 4).IndexOfAnyExceptInRange('0', '9') < 0 &&
        int.Parse(name.AsSpan(7, 4), System.Globalization.CultureInfo.InvariantCulture) < 64;

    private static void Require(bool condition, string field, string message)
    {
        if (!condition) throw new JsonException(message, field, null, null);
    }
}
