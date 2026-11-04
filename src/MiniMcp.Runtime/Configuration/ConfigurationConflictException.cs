namespace MiniMcp.Configuration;

public sealed class ConfigurationConflictException(string filePath)
    : ConfigurationException($"Configuration '{filePath}' changed since it was read. Reload it before saving.")
{
    public string FilePath { get; } = filePath;
}
