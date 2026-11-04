namespace MiniMcp.Configuration;

public sealed class ConfigurationValidationException(string field, string message, Exception? innerException = null)
    : ConfigurationException($"{field}: {message}", innerException)
{
    public string Field { get; } = field;
}
