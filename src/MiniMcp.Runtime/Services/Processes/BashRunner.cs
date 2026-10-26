using MiniMcp.Services.Workspace;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;

namespace MiniMcp.Services.Processes;

public sealed record BashResult([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ExitCode,
    string Stdout, string Stderr, bool TimedOut, bool OutputTruncated);

internal sealed class BashRunner
{
    private readonly ProjectWorkspace _workspace;
    private readonly string _bashPath;

    public BashRunner(ProjectWorkspace workspace, string bashPath)
    {
        _workspace = workspace;
        _bashPath = bashPath;
        if (string.IsNullOrWhiteSpace(_bashPath) || !Path.IsPathFullyQualified(_bashPath) || !File.Exists(_bashPath))
            throw new InvalidOperationException("The Bash path must point to an existing executable (on Windows, install Git for Windows).");
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new InvalidOperationException("Bash process cleanup is supported on Linux and Windows.");
        if (OperatingSystem.IsLinux() && !File.Exists("/usr/bin/setsid"))
            throw new InvalidOperationException("Install util-linux: /usr/bin/setsid is required for Bash process cleanup.");
    }

    public async Task<BashResult> RunAsync(string command, string workdir = ".", int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Contains('\0') || Encoding.UTF8.GetByteCount(command) > 64 * 1024)
            throw new WorkspaceException("command must contain 1..65536 UTF-8 bytes and no NUL characters.");
        if (timeoutSeconds is < 1 or > 600) throw new WorkspaceException("timeout_seconds must be between 1 and 600.");
        await _workspace.Mutations.WaitAsync(cancellationToken);
        try
        {
            var directory = _workspace.Resolve(workdir);
            if (!Directory.Exists(directory)) throw new WorkspaceException("workdir must be an existing project directory.");
            var start = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsLinux() ? "/usr/bin/setsid" : _bashPath,
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true
            };
            if (OperatingSystem.IsLinux()) { start.ArgumentList.Add("--"); start.ArgumentList.Add(_bashPath); }
            start.ArgumentList.Add("--noprofile");
            start.ArgumentList.Add("--norc");
            start.ArgumentList.Add("-c");
            // The user command cannot run before Windows job assignment has succeeded.
            start.ArgumentList.Add("IFS= read -r _minimcp_start || exit 125; exec \"$BASH\" --noprofile --norc -c \"$1\"");
            start.ArgumentList.Add("minimcp");
            start.ArgumentList.Add(command);
            foreach (var key in start.Environment.Keys.ToArray())
                if (!KeepEnvironment(key)) start.Environment.Remove(key);

            using var process = new Process { StartInfo = start };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var reading = new CancellationTokenSource();
            ProcessLifetime? lifetime = null;
            var stdout = new CapturedOutput();
            var stderr = new CapturedOutput();
            Task outputTask = Task.CompletedTask, errorTask = Task.CompletedTask;
            var started = false;
            var timedOut = false;
            int? exitCode = null;
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!process.Start()) throw new WorkspaceException("Could not start Bash.");
                started = true;
                outputTask = stdout.ReadAsync(process.StandardOutput, reading.Token);
                errorTask = stderr.ReadAsync(process.StandardError, reading.Token);
                lifetime = new ProcessLifetime(process);
                deadline.Token.ThrowIfCancellationRequested();
                await process.StandardInput.WriteLineAsync("run".AsMemory(), deadline.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token);
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
            }
            catch (Win32Exception) { throw new WorkspaceException("Bash could not start or process containment could not be established. Check the Bash path and service permissions."); }
            finally
            {
                if (started)
                {
                    lifetime?.Dispose();
                    // Also handles failures before lifetime setup completed.
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException) { }
                    reading.CancelAfter(TimeSpan.FromSeconds(5));
                    await Task.WhenAll(outputTask, errorTask);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new BashResult(exitCode, stdout.Text, stderr.Text, timedOut, stdout.Truncated || stderr.Truncated);
        }
        finally { _workspace.Mutations.Release(); }
    }

    private static bool KeepEnvironment(string key) =>
        key.StartsWith("LC_", StringComparison.OrdinalIgnoreCase) ||
        new[] { "PATH", "HOME", "USER", "LOGNAME", "LANG", "TMP", "TEMP", "TMPDIR", "SystemRoot", "WINDIR",
            "COMSPEC", "PATHEXT", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
            "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" }
            .Contains(key, StringComparer.OrdinalIgnoreCase);

    private sealed class CapturedOutput
    {
        private readonly StringBuilder _text = new();
        public bool Truncated { get; private set; }
        public string Text => ProjectWorkspace.Clip(_text.ToString(), ProjectWorkspace.MaxOutputCharacters);

        public async Task ReadAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    // One extra character lets Clip avoid cutting a surrogate pair at the boundary.
                    var take = Math.Min(count, ProjectWorkspace.MaxOutputCharacters + 1 - _text.Length);
                    _text.Append(buffer, 0, take);
                    Truncated |= count > take || _text.Length > ProjectWorkspace.MaxOutputCharacters;
                }
            }
            catch (OperationCanceledException) { Truncated = true; }
            catch (IOException) { Truncated = true; }
        }
    }
}
