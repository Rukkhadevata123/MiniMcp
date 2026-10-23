using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiniMcp.Configuration;

/// <summary>
/// Reads and saves configuration files. Raw reads check JSON format only;
/// LoadAsync also applies path normalization and shared business/dependency validation.
/// </summary>
public sealed partial class ConfigurationStore
{
    public const int MaxFileBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ConfigurationPaths _paths;
    private readonly Func<Task>? _beforeCommit;

    public ConfigurationStore() : this(ConfigurationPaths.ForCurrentUser()) { }

    internal ConfigurationStore(ConfigurationPaths paths, Func<Task>? beforeCommit = null)
    {
        _paths = paths;
        _beforeCommit = beforeCommit;
    }

    /// <summary>
    /// Loads the selected settings and fixed local policy, then applies the shared validation pipeline.
    /// ReadSettingsAsync/ReadPolicyAsync remain available for editing drafts and retaining file revisions.
    /// </summary>
    public async Task<(AppSettings Settings, ExecutionPolicy Policy)> LoadAsync(string? path, string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsAsync(path, workingDirectory, cancellationToken).ConfigureAwait(false);
        var policy = await ReadPolicyAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new ConfigurationValidator(_paths).Validate(settings.Value, settings.FilePath, policy.Value);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public Task<ConfigurationFile<AppSettings>> ReadSettingsAsync(string? path, string workingDirectory,
        CancellationToken cancellationToken = default) =>
        ReadAsync(_paths.ResolveSettingsFile(path, workingDirectory), ConfigurationJson.ParseSettings,
            missingValue: null, cancellationToken);

    // The caller can select settings.json but cannot redirect policy.json through this API.
    public Task<ConfigurationFile<ExecutionPolicy>> ReadPolicyAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(_paths.PolicyFile, ConfigurationJson.ParsePolicy, new ExecutionPolicy(), cancellationToken);

    private static async Task<ConfigurationFile<T>> ReadAsync<T>(string filePath, Func<string, T> parse,
        T? missingValue, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            byte[] bytes;
            try { bytes = await ReadBytesAsync(filePath, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (missingValue is not null
                && exception is FileNotFoundException or DirectoryNotFoundException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ConfigurationFile<T>(filePath, missingValue, Revision: null);
            }

            var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
            var value = parse(Utf8.GetString(bytes, offset, bytes.Length - offset));
            cancellationToken.ThrowIfCancellationRequested();
            return new ConfigurationFile<T>(filePath, value, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or DecoderFallbackException or JsonException)
        {
            throw new ConfigurationLoadException(filePath, exception);
        }
    }

    private static async Task<byte[]> ReadBytesAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[MaxFileBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxFileBytes)
            throw new InvalidDataException($"Configuration files must not exceed {MaxFileBytes} UTF-8 bytes.");
        return buffer[..count];
    }
}
