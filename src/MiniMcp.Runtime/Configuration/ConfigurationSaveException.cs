namespace MiniMcp.Configuration;

public sealed class ConfigurationSaveException(string filePath, Exception innerException)
    : ConfigurationException($"Could not save configuration '{filePath}': {innerException.Message}", innerException)
{
    public string FilePath { get; } = filePath;
}
