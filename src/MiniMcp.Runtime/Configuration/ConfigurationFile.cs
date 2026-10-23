namespace MiniMcp.Configuration;

/// <summary>
/// Parsed file contents with a SHA-256 revision of the exact bytes read.
/// A null revision identifies a missing policy file whose value uses the defaults.
/// </summary>
public sealed record ConfigurationFile<T>(string FilePath, T Value, string? Revision);
