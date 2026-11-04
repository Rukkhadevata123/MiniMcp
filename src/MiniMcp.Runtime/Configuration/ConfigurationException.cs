namespace MiniMcp.Configuration;

/// <summary>
/// Base for configuration load, validation, save and conflict failures, so callers that treat all
/// of them as "the configuration is unusable" can match one type.
/// </summary>
public abstract class ConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
