using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MiniMcp.Services;

internal enum QueryFailure { None, Regex, Glob, UnsupportedArguments, InputOrQuery }
internal sealed record QueryProcessResult(int ExitCode, bool OutputLimited, bool HasErrors, QueryFailure Failure);

// A small rg/Git process adapter, not an arbitrary-command tool. Admission is per runtime session.
internal sealed class ReadOnlyQueryRunner : IDisposable
{
    private readonly SemaphoreSlim _capacity = new(10, 10); // two executing, at most eight waiting
    private readonly SemaphoreSlim _running = new(2, 2);

    internal async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> query, CancellationToken cancellationToken)
    {
        if (!await _capacity.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new WorkspaceException("Read-only query capacity reached; retry after another query finishes.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(QueryText.Timeout);
        var entered = false;
        try
        {
            await _running.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            return await query(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new WorkspaceException("Read-only query exceeded its 10 second budget, including admission wait. Its process was cleaned up; no complete result is available."); }
        finally
        {
            if (entered) _running.Release();
            _capacity.Release();
        }
    }

    internal static async Task<QueryProcessResult> RunAsync(string executable, string directory,
        IEnumerable<string> arguments, char separator, int maxFrameCharacters, int maxOutputCharacters,
        Func<string, bool> accept, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new WorkspaceException("The query executable is missing. Configure an existing absolute program path.");
        if (OperatingSystem.IsLinux() && !File.Exists("/usr/bin/setsid"))
            throw new WorkspaceException("Read-only queries require util-linux /usr/bin/setsid for process cleanup.");
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsLinux() ? "/usr/bin/setsid" : executable,
            WorkingDirectory = directory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false)
        };
        if (OperatingSystem.IsLinux()) { start.ArgumentList.Add("--"); start.ArgumentList.Add(executable); }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // No user rg configuration, Git overrides, pager, preprocessing or injected loader options.
        foreach (var key in start.Environment.Keys.ToArray())
            if (!new[] { "SystemRoot", "WINDIR", "TMP", "TEMP", "TMPDIR", "LANG" }.Contains(key, StringComparer.OrdinalIgnoreCase))
                start.Environment.Remove(key);
        if (environment is not null)
            foreach (var (key, value) in environment) start.Environment[key] = value;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ProcessLifetime lifetime;
        cancellationToken.ThrowIfCancellationRequested();
        try { lifetime = ProcessLifetime.Start(start); }
        catch (Exception error) when (error is Win32Exception or IOException)
        { throw new WorkspaceException("The query program could not start or process containment could not be established. Check the configured executable and permissions."); }
        using (lifetime)
        {
            var limited = false;
            var errors = false;
            var errorPrefix = new StringBuilder();
            var output = ReadOutputAsync();
            var stderr = ReadErrorsAsync();
            try
            {
                await Task.WhenAll(lifetime.Process.WaitForExitAsync(limit.Token), output, stderr).WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested) { }
            catch (Exception) when (cancellationToken.IsCancellationRequested) { }
            finally
            {
                // Keep the actual call, slots and handles owned until descendants and both streams finish.
                while (true)
                {
                    try { await lifetime.TerminateAsync().ConfigureAwait(false); break; }
                    catch (Exception error) when (error is Win32Exception or IOException)
                    {
                        // A failed termination observation is not proof of cleanup. Keep this actual
                        // call and its handles owned; Runtime's stop wait remains bounded separately.
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                }
                try { await Task.WhenAll(output, stderr).ConfigureAwait(false); }
                catch (Exception) when (cancellationToken.IsCancellationRequested) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = errorPrefix.ToString();
            var failure = !errors ? QueryFailure.None : diagnostic.Contains("regex parse error", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.Contains("regex could not be compiled", StringComparison.OrdinalIgnoreCase) ? QueryFailure.Regex :
                diagnostic.Contains("error parsing glob", StringComparison.OrdinalIgnoreCase) ? QueryFailure.Glob :
                diagnostic.Contains("unrecognized flag", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("unexpected argument", StringComparison.OrdinalIgnoreCase)
                    ? QueryFailure.UnsupportedArguments : QueryFailure.InputOrQuery;
            return new(lifetime.Process.ExitCode, limited, errors, failure);

            async Task ReadOutputAsync()
            {
                try
                {
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                    var buffer = new char[4096];
                    var frame = new StringBuilder();
                    var total = 0;
                    int count;
                    while ((count = await lifetime.StandardOutput.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                    {
                        for (var index = 0; index < count && !limited; index++)
                        {
                            if (++total > maxOutputCharacters || frame.Length >= maxFrameCharacters)
                            { limited = true; limit.Cancel(); break; }
                            if (buffer[index] != separator) { frame.Append(buffer[index]); continue; }
                            if (!accept(frame.ToString())) { limited = true; limit.Cancel(); }
                            frame.Clear();
                        }
                    }
                    if (!limited && !cancellationToken.IsCancellationRequested && frame.Length != 0)
                        throw new WorkspaceException("The query program returned an incomplete output record.");
                }
                catch (DecoderFallbackException)
                {
                    limit.Cancel();
                    throw new WorkspaceException("The query program returned invalid UTF-8 protocol output.");
                }
                catch { limit.Cancel(); throw; }
            }
            async Task ReadErrorsAsync()
            {
                try
                {
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                    var buffer = new char[4096];
                    int count;
                    while ((count = await lifetime.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                    {
                        errors = true;
                        errorPrefix.Append(buffer, 0, Math.Min(count, 4096 - errorPrefix.Length));
                    }
                    // Raw diagnostics can include excluded filenames; never return or log them as data.
                }
                catch { limit.Cancel(); throw; }
            }
        }
    }

    public void Dispose() { _running.Dispose(); _capacity.Dispose(); }
}
