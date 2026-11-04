using MiniMcp.Services.Workspace;
using MiniMcp.Configuration;
using MiniMcp.Services;

internal static class ToolExecutionChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("execution defers Bash initialization past reads, previews, denials and cancellation", async () =>
        {
            var temporary = TemporaryTestDirectory.Reserve("execution");
            var root = Path.Combine(temporary, "project");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "sample.txt"), "original\n");
            var paths = new ConfigurationPaths(Path.Combine(temporary, "user"), Path.Combine(temporary, "state"));
            try
            {
                foreach (var mode in new[] { ExecutionMode.ProjectEdit, ExecutionMode.FullAccess })
                {
                    var bash = Path.Combine(temporary, "bash");
                    File.WriteAllText(bash, "Validation fixture; this file must never be executed.");
                    if (OperatingSystem.IsLinux())
                        File.SetUnixFileMode(bash, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    var configuration = new ConfigurationValidator(paths).Validate(new AppSettings
                    {
                        ProjectRoot = root,
                        BashPath = bash,
                        Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local }
                    }, Path.Combine(temporary, "settings.json"), new ExecutionPolicy { Mode = mode });
                    File.Delete(bash); // A dependency can disappear after complete configuration validation.

                    await using var execution = new ToolExecutionService(Guid.NewGuid(), configuration);
                    if ((await execution.ReadFileAsync("sample.txt")).Content != "original\n")
                        throw new Exception("Read failed.");
                    var preview = await execution.ApplyPatchAsync("*** Begin Patch\n*** Add File: preview/new.txt\n+new\n*** End Patch", dryRun: true);
                    if (!preview.DryRun || !preview.Diff.Contains("b/preview/new.txt") || preview.Statistics.AddedLines != 1 ||
                        preview.Changes[0].Summary.Action != "add")
                        throw new Exception("Complete preview failed.");
                    using var cancelled = new CancellationTokenSource();
                    cancelled.Cancel();
                    await Reject<OperationCanceledException>(() => execution.ApplyPatchAsync("invalid", dryRun: true, cancellationToken: cancelled.Token));
                    await Reject<OperationCanceledException>(() => execution.RunBashAsync("exit 0", cancellationToken: cancelled.Token));
                    if (mode == ExecutionMode.ProjectEdit)
                        await Reject<WorkspaceException>(() => execution.RunBashAsync("exit 0"));
                    else
                        await Reject<InvalidOperationException>(() => execution.RunBashAsync("exit 0"));
                    if ((await execution.ReadFileAsync("sample.txt")).Content != "original\n")
                        throw new Exception("Read after failure failed.");
                }
                if (Directory.EnumerateFileSystemEntries(root).Count() != 1 ||
                    Directory.Exists(Path.GetDirectoryName(paths.PolicyFile)) || Directory.Exists(Path.GetDirectoryName(paths.RecoveryDirectory)))
                    throw new Exception("Preview, denial or cancellation created files or state directories.");
            }
            finally { TemporaryTestDirectory.Delete(temporary); }
        });
    }
}
