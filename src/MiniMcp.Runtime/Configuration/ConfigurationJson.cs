using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace MiniMcp.Configuration;

/// <summary>
/// Parses and formats the v1 configuration format. Filesystem and business validation run separately.
/// JsonException preserves the JSON field path and available line/byte positions for callers.
/// </summary>
public static class ConfigurationJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new NamedEnumConverter<AuthenticationMode>(), new NamedEnumConverter<ExecutionMode>() }
    };

    public static AppSettings ParseSettings(string json)
    {
        var settings = Parse<AppSettings>(json);
        CheckVersion(settings.SchemaVersion);
        return settings;
    }

    public static ExecutionPolicy ParsePolicy(string json)
    {
        var policy = Parse<ExecutionPolicy>(json);
        CheckVersion(policy.SchemaVersion);
        return policy;
    }

    public static string SerializeSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CheckVersion(settings.SchemaVersion);
        return JsonSerializer.Serialize(settings, Options) + "\n";
    }

    public static string SerializePolicy(ExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        CheckVersion(policy.SchemaVersion);
        return JsonSerializer.Serialize(policy, Options) + "\n";
    }

    private static T Parse<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException("Configuration must be a JSON object.", "$", null, null);

    private static void CheckVersion(int version)
    {
        if (version != 1)
            throw new JsonException($"Unsupported schemaVersion {version}; expected 1.", "$.schemaVersion", null, null);
    }

    // The standard enum converter also accepts case variants and comma-separated names.
    // Configuration uses only exact, individual enum names, including for execution permissions.
    private sealed class NamedEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var name = reader.GetString();
                if (Enum.TryParse<T>(name, out var value) && Enum.GetName(value) == name) return value;
            }
            throw new JsonException($"Use an exact {typeof(T).Name} name: {string.Join(", ", Enum.GetNames<T>())}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Enum.GetName(value) ?? throw new JsonException($"Unknown {typeof(T).Name} value."));
    }
}
