namespace MiniMcp.Configuration;

/// <summary>
/// Parsed file contents with a SHA-256 revision of the exact bytes read.
/// A null revision identifies an absent file; saving it requires the target to remain absent.
/// </summary>
public sealed record ConfigurationFile<T>(string FilePath, T Value, string? Revision);
