using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using MiniMcp.Configuration;
using MiniMcp.Services;

namespace MiniMcp.Runtime;

// Checkpoints pause real child creation/cleanup; they do not replace process or pipe implementations.
internal sealed class TunnelCheckpoints
{
    internal Func<Task>? AfterStart { get; init; }
    internal Func<Task>? BeforeRelease { get; init; }
    internal Func<Task>? BeforeExitNotification { get; init; }
}

internal sealed class TunnelProcess
{
    private readonly ProcessLifetime _lifetime;
    private readonly ILogger _logger;
    private readonly TunnelCheckpoints? _checks;
    private readonly Task _stdout, _stderr;
    private readonly Task<int> _exit;
    private readonly object _gate = new();
    private Task? _stopping, _observation;
    private int _stopRequested;

    private TunnelProcess(ProcessLifetime lifetime, ILoggerProvider logs, TunnelCheckpoints? checks)
    {
        _lifetime = lifetime;
        _checks = checks;
        ProcessId = lifetime.Process.Id;
        _logger = logs.CreateLogger("MiniMcp.Tunnel");
        _stdout = ProcessOutputReader.DrainAsync(lifetime.StandardOutput, logs.CreateLogger("MiniMcp.Tunnel.stdout"), LogLevel.Information);
        _stderr = ProcessOutputReader.DrainAsync(lifetime.StandardError, logs.CreateLogger("MiniMcp.Tunnel.stderr"), LogLevel.Warning);
        _exit = ExitAsync();
    }

    internal Guid InstanceId { get; } = Guid.NewGuid();
    internal int ProcessId { get; }
    internal bool HasExited
    {
        get
        {
            try { return _exit.IsCompleted || _lifetime.Process.HasExited; }
            catch (InvalidOperationException) when (_exit.IsCompleted) { return true; } // Cleanup may have disposed the handle between reads.
        }
    }
    internal int? ExitCode => _exit.IsCompletedSuccessfully ? _exit.Result : null;

    internal static TunnelProcess Start(TunnelSettings settings, ILoggerProvider logs, TunnelCheckpoints? checks)
    {
        if (!settings.Enabled) throw new InvalidOperationException("The saved session configuration disables the managed tunnel.");
        if (!Path.IsPathFullyQualified(settings.ExecutablePath) || !Path.IsPathFullyQualified(settings.TokenFilePath))
            throw new InvalidOperationException("The managed tunnel requires validated absolute program and token-file paths.");
        if (OperatingSystem.IsLinux() && !File.Exists("/usr/bin/setsid"))
            throw new InvalidOperationException("Install util-linux: /usr/bin/setsid is required for tunnel process cleanup.");
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsLinux() ? "/usr/bin/setsid" : settings.ExecutablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        if (OperatingSystem.IsLinux()) { start.ArgumentList.Add("--"); start.ArgumentList.Add(settings.ExecutablePath); }
        foreach (var argument in new[] { "tunnel", "--no-autoupdate", "run", "--token-file", settings.TokenFilePath })
            start.ArgumentList.Add(argument);
        // Inherited TUNNEL_* options/tokens must not override this session's explicit arguments.
        foreach (var key in start.Environment.Keys.ToArray())
            if (!KeepEnvironment(key)) start.Environment.Remove(key);
        var lifetime = ProcessLifetime.Start(start);
        try { return new(lifetime, logs, checks); }
        catch { lifetime.Dispose(); throw; }
    }

    internal async Task CheckStartedAsync(CancellationToken cancellationToken)
    {
        if (_checks?.AfterStart is { } checkpoint) await checkpoint().WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (HasExited) throw new IOException($"The tunnel exited during startup (exit code {ExitCode?.ToString() ?? "pending"}).");
        if (_stdout.IsFaulted) await _stdout.ConfigureAwait(false);
        if (_stderr.IsFaulted) await _stderr.ConfigureAwait(false);
    }

    internal void Observe(Action<int?, string> fault)
    {
        _observation = Task.Run(async () =>
        {
            try
            {
                var pending = new List<Task> { _exit, _stdout, _stderr };
                while (true)
                {
                    var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                    await completed.ConfigureAwait(false);
                    if (ReferenceEquals(completed, _exit))
                    {
                        if (_checks?.BeforeExitNotification is { } checkpoint) await checkpoint().ConfigureAwait(false);
                        if (Volatile.Read(ref _stopRequested) == 0)
                            fault(ExitCode, $"The tunnel process exited unexpectedly (exit code {ExitCode}).");
                        break;
                    }
                    pending.Remove(completed); // Closing one output stream does not mean process exit.
                }
            }
            catch (Exception error)
            {
                if (Volatile.Read(ref _stopRequested) == 0) fault(ExitCode, "Tunnel observation failed: " + error.Message);
            }
            try { await EnsureStopTask().ConfigureAwait(false); }
            catch (Exception error)
            {
                _logger.LogError(error, "Tunnel cleanup did not complete.");
                fault(ExitCode, "Tunnel cleanup did not complete: " + error.Message);
            }
        });
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureStopTask(retry: true).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_observation is { } observation) await observation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task EnsureStopTask(bool retry = false)
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            if (_stopping is null || (retry && (_stopping.IsFaulted || _stopping.IsCanceled)))
                _stopping = Task.Run(CleanupAsync);
            return _stopping;
        }
    }

    private async Task CleanupAsync()
    {
        await _lifetime.TerminateAsync().ConfigureAwait(false);
        await _exit.ConfigureAwait(false);
        try { await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false); }
        catch (Exception error) { _logger.LogWarning(error, "Tunnel output ended with an IO error."); }
        if (_checks?.BeforeRelease is { } checkpoint) await checkpoint().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task<int> ExitAsync()
    {
        await _lifetime.Process.WaitForExitAsync().ConfigureAwait(false);
        return _lifetime.Process.ExitCode;
    }

    private static bool KeepEnvironment(string key) =>
        key.StartsWith("LC_", StringComparison.OrdinalIgnoreCase) ||
        new[] { "PATH", "HOME", "USER", "LOGNAME", "LANG", "TMP", "TEMP", "TMPDIR", "SystemRoot", "WINDIR",
            "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
            "SSL_CERT_FILE", "SSL_CERT_DIR" }.Contains(key, StringComparer.OrdinalIgnoreCase);
}
