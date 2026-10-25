using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniMcp.Serialization;

// Permission and record-state JSON uses exact individual names, without integer or comma-separated aliases.
internal sealed class NamedEnumConverter<T> : JsonConverter<T> where T : struct, Enum
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
