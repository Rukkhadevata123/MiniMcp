namespace MiniMcp.Configuration;

public sealed class ConfigurationLoadException(string filePath, Exception innerException)
    : Exception($"Could not load configuration '{filePath}': {innerException.Message}", innerException)
{
    public string FilePath { get; } = filePath;
}
