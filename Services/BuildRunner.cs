using System.Diagnostics;
using System.Text;
using MiniMcp.Configuration;

namespace MiniMcp.Services;

public sealed record BuildResult(int? ExitCode, bool TimedOut, bool OutputTruncated, string Output);

public sealed class BuildRunner(ProjectWorkspace workspace, MiniMcpOptions settings)
{
    public const int MaxOutputCharacters = 16000;
    private readonly SemaphoreSlim _builds = new(1, 1);
    private readonly string _dotnetExecutable = "dotnet";

    internal BuildRunner(ProjectWorkspace workspace, MiniMcpOptions settings, string dotnetExecutable)
        : this(workspace, settings) => _dotnetExecutable = dotnetExecutable;

    public async Task<BuildResult> BuildAsync(string path = ".", CancellationToken cancellationToken = default)
    {
        if (!settings.EnableBuild) throw new WorkspaceException("The build tool is disabled.");
        var target = workspace.Resolve(path);
        if (!Directory.Exists(target) && (!File.Exists(target) ||
            !new[] { ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx" }
                .Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase)))
            throw new WorkspaceException("Build a project directory, project file, or solution inside MCP_ROOT.");

        await _builds.WaitAsync(cancellationToken);
        try
        {
            var start = new ProcessStartInfo(_dotnetExecutable)
            {
                WorkingDirectory = workspace.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "build", target, "--nologo", "--verbosity", "minimal" })
                start.ArgumentList.Add(argument);
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";

            using var process = Process.Start(start) ?? throw new WorkspaceException("Could not start dotnet build.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.BuildTimeoutSeconds));
            var output = new StringBuilder();
            var truncated = false;
            async Task Drain(StreamReader reader)
            {
                var buffer = new char[4096];
                try
                {
                    int read;
                    while ((read = await reader.ReadAsync(buffer.AsMemory(), deadline.Token)) > 0)
                    {
                        lock (output)
                        {
                            var keep = Math.Min(read, MaxOutputCharacters - output.Length);
                            output.Append(buffer, 0, keep);
                            truncated |= keep < read;
                        }
                    }
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            }

            var readers = Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError));
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                await readers.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await readers.WaitAsync(TimeSpan.FromSeconds(5));
                cancellationToken.ThrowIfCancellationRequested();
            }
            return new BuildResult(timedOut ? null : process.ExitCode, timedOut, truncated, output.ToString());
        }
        finally { _builds.Release(); }
    }
}
