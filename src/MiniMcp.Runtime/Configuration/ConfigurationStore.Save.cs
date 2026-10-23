using System.Security.Cryptography;

namespace MiniMcp.Configuration;

public sealed partial class ConfigurationStore
{
    // Serialize saves across store instances in this process; external writers are checked by revision.
    private static readonly SemaphoreSlim Saves = new(1, 1);

    /// <summary>
    /// Saves a draft with its last-read revision. A null revision creates a new file without overwriting.
    /// Validation uses the current fixed local policy; the original draft paths are persisted.
    /// </summary>
    public async Task<ConfigurationFile<AppSettings>> SaveSettingsAsync(ConfigurationFile<AppSettings> file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!Path.IsPathFullyQualified(file.FilePath))
            throw new ConfigurationValidationException("settingsFile", "Use an absolute configuration file path.");
        file = file with { FilePath = Path.GetFullPath(file.FilePath) };
        await Saves.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            var policy = await ReadPolicyAsync(cancellationToken).ConfigureAwait(false);
            var validator = new ConfigurationValidator(_paths);
            void Validate() => validator.Validate(file.Value, file.FilePath, policy.Value);
            Validate();
            return await WriteAsync(file, EncodeJson(ConfigurationJson.SerializeSettings(file.Value)), Validate,
                token => CheckRevisionAsync(policy.FilePath, policy.Revision, token), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or AggregateException)
        {
            throw new ConfigurationSaveException(file.FilePath, exception);
        }
        finally { Saves.Release(); }
    }

    /// <summary>
    /// Saves a local policy draft only to the fixed user policy file. Settings must identify an
    /// existing, last-read file; validation reloads that file instead of using its unsaved Value.
    /// Both revisions are checked before staging and again before committing.
    /// </summary>
    public async Task<ConfigurationFile<ExecutionPolicy>> SavePolicyAsync(ConfigurationFile<ExecutionPolicy> file,
        ConfigurationFile<AppSettings> settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(settings);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.IsPathFullyQualified(file.FilePath)
            || !string.Equals(Path.GetFullPath(file.FilePath), _paths.PolicyFile, comparison))
            throw new ConfigurationValidationException("policyFile", "Policy can only be saved to the fixed local user policy file.");
        if (!Path.IsPathFullyQualified(settings.FilePath) || settings.Revision is null)
            throw new ConfigurationValidationException("settingsFile", "Read or save an existing settings file before saving policy.");
        file = file with { FilePath = _paths.PolicyFile };
        settings = settings with { FilePath = Path.GetFullPath(settings.FilePath) };
        await Saves.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await CheckRevisionAsync(settings.FilePath, settings.Revision, cancellationToken).ConfigureAwait(false);
            var current = await ReadSettingsAsync(settings.FilePath, Path.GetDirectoryName(settings.FilePath)!, cancellationToken).ConfigureAwait(false);
            if (current.Revision != settings.Revision) throw new ConfigurationConflictException(settings.FilePath);
            var validator = new ConfigurationValidator(_paths);
            void Validate() => validator.Validate(current.Value, current.FilePath, file.Value);
            Validate();
            return await WriteAsync(file, EncodeJson(ConfigurationJson.SerializePolicy(file.Value)), Validate,
                token => CheckRevisionAsync(current.FilePath, current.Revision, token), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or AggregateException)
        {
            throw new ConfigurationSaveException(file.FilePath, exception);
        }
        finally { Saves.Release(); }
    }

    private static byte[] EncodeJson(string json)
    {
        if (Utf8.GetByteCount(json) > MaxFileBytes)
            throw new InvalidDataException($"Configuration files must not exceed {MaxFileBytes} UTF-8 bytes.");
        return Utf8.GetBytes(json);
    }

    private async Task<ConfigurationFile<T>> WriteAsync<T>(ConfigurationFile<T> file, byte[] bytes,
        Action validate, Func<CancellationToken, Task> checkDependencies, CancellationToken cancellationToken)
    {
        var saved = file with { Revision = Convert.ToHexString(SHA256.HashData(bytes)) };
        string? temporary = null;
        var createdDirectories = new List<string>();
        try
        {
            await CheckRevisionAsync(file.FilePath, file.Revision, cancellationToken).ConfigureAwait(false);
            await checkDependencies(cancellationToken).ConfigureAwait(false);
            var parent = Path.GetDirectoryName(file.FilePath)!;
            var missing = new Stack<string>();
            for (var directory = parent; !Directory.Exists(directory);)
            {
                missing.Push(directory);
                directory = Path.GetDirectoryName(directory)
                    ?? throw new DirectoryNotFoundException($"Cannot locate an existing parent directory for '{file.FilePath}'.");
            }
            while (missing.TryPop(out var directory))
            {
                if (OperatingSystem.IsLinux())
                    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                else Directory.CreateDirectory(directory);
                createdDirectories.Add(directory);
            }
            validate();
            var candidate = Path.Combine(parent, $".minimcp-config-{Guid.NewGuid():N}.tmp");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(candidate, options))
            {
                temporary = candidate;
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (_beforeCommit is not null) await _beforeCommit().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            validate();
            await checkDependencies(cancellationToken).ConfigureAwait(false);
            await CheckRevisionAsync(file.FilePath, file.Revision, cancellationToken).ConfigureAwait(false);
            if (OperatingSystem.IsLinux() && file.Revision is not null)
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(file.FilePath));
            cancellationToken.ThrowIfCancellationRequested();
            // Commit is not cancellable: once installed, report the saved snapshot rather than cancellation.
            if (file.Revision is null) File.Move(temporary, file.FilePath);
            else File.Replace(temporary, file.FilePath, destinationBackupFileName: null);
            temporary = null;
            return saved;
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { failures.Add(cleanup); }
            foreach (var directory in createdDirectories.AsEnumerable().Reverse())
            {
                try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
                catch (DirectoryNotFoundException) { }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { failures.Add(cleanup); }
            }
            if (failures.Count > 1) throw new AggregateException("Saving failed and temporary storage cleanup did not complete.", failures);
            throw;
        }
    }

    private static async Task CheckRevisionAsync(string filePath, string? expected, CancellationToken cancellationToken)
    {
        string? current;
        try { current = Convert.ToHexString(SHA256.HashData(await ReadBytesAsync(filePath, cancellationToken).ConfigureAwait(false))); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { current = null; }
        catch (InvalidDataException) { throw new ConfigurationConflictException(filePath); }
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(current, expected, StringComparison.Ordinal)) throw new ConfigurationConflictException(filePath);
    }
}
