namespace MiniMcp.Configuration;

public sealed class ConfigurationSaveException(string filePath, Exception innerException)
    : Exception($"Could not save configuration '{filePath}': {innerException.Message}", innerException)
{
    public string FilePath { get; } = filePath;
}
