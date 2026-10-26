using MiniMcp.Services.Workspace;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using MiniMcp.Serialization;

namespace MiniMcp.Recovery;

// Format and primitive validation only. Parsed manifests are not trusted paths, verified backups or write authority.
internal static class RecoveryManifestJson
{
    internal const int MaxManifestBytes = 256 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new NamedEnumConverter<RecoveryRecordState>() }
    };

    public static byte[] Serialize(RecoveryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateFields(manifest);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Options);
        Require(bytes.Length < MaxManifestBytes, "$", "Recovery manifests must not exceed 256 KiB including the final newline.");
        return [.. bytes, (byte)'\n'];
    }

    public static RecoveryManifest Parse(ReadOnlySpan<byte> bytes)
    {
        Require(bytes.Length <= MaxManifestBytes, "$", "Recovery manifests must not exceed 256 KiB.");
        string json;
        try { json = Utf8.GetString(bytes); }
        catch (DecoderFallbackException error) { throw new JsonException("Recovery manifests must use valid UTF-8.", error); }
        var manifest = JsonSerializer.Deserialize<RecoveryManifest>(json, Options)
            ?? throw new JsonException("A recovery manifest must be a JSON object.", "$", null, null);
        ValidateFields(manifest);
        return manifest;
    }

    internal static void ValidateFields(RecoveryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Require(manifest.SchemaVersion == 1, "$.schemaVersion", "Unsupported recovery schemaVersion; expected 1.");
        Require(Enum.IsDefined(manifest.State), "$.state", "Unknown recovery record state.");
        Require(manifest.ChangeId != Guid.Empty, "$.changeId", "A nonempty change ID is required.");
        Require(manifest.RunId != Guid.Empty, "$.runId", "A nonempty run ID is required.");
        Require(!string.IsNullOrWhiteSpace(manifest.ProjectRoot) && Path.IsPathFullyQualified(manifest.ProjectRoot),
            "$.projectRoot", "An absolute project root is required.");
        Require(manifest.CreatedAtUtc != default && manifest.CreatedAtUtc.Offset == TimeSpan.Zero,
            "$.createdAtUtc", "A nondefault UTC timestamp is required.");
        Require(!manifest.Operations.IsDefault && manifest.Operations.Length is >= 1 and <= 64,
            "$.operations", "A manifest must contain 1 to 64 operations.");
        Require(!manifest.Paths.IsDefault && manifest.Paths.Length is >= 1 and <= 128,
            "$.paths", "A manifest must contain 1 to 128 path states.");
        for (var i = 0; i < manifest.Operations.Length; i++)
            Require(manifest.Operations[i] is not null, $"$.operations[{i}]", "An operation must not be null.");
        for (var i = 0; i < manifest.Paths.Length; i++)
        {
            var state = manifest.Paths[i];
            Require(state is not null, $"$.paths[{i}]", "A path state must not be null.");
            Image(state!.Before, $"$.paths[{i}].before");
            Image(state.After, $"$.paths[{i}].after");
        }
    }

    private static void Image(RecoveryFileImage? image, string path)
    {
        if (image is null) return;
        Require(image.ByteLength is >= 0 and <= ProjectWorkspace.MaxFileBytes, path + ".byteLength", "Use a byte length between 0 and 256 KiB.");
        Require(image.Sha256 is { Length: 64 } && image.Sha256.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'),
            path + ".sha256", "Use a 64-character uppercase SHA-256 hexadecimal value.");
        Require(image.UnixMode is null || ((int)image.UnixMode.Value & ~0xfff) == 0,
            path + ".unixMode", "Unix mode contains unsupported permission bits.");
        Require(!image.UnixModeIsCreateRequest || image.UnixMode is not null,
            path + ".unixModeIsCreateRequest", "A creation mode request must specify a Unix mode.");
    }

    private static void Require(bool condition, string path, string message)
    {
        if (!condition) throw new JsonException(message, path, null, null);
    }
}
