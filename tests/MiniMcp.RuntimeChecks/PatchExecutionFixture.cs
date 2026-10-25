using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MiniMcp.Configuration;
using MiniMcp.Recovery;
using MiniMcp.Runtime;
using MiniMcp.Services;

internal sealed class PatchExecutionFixture : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"minimcp-patch-execution-{Guid.NewGuid():N}");
    public Guid RunId { get; } = Guid.NewGuid();
    public string Root { get; }
    public RuntimeConfiguration Configuration { get; }
    public string RecoveryDirectory => Configuration.Policy.RecoveryDirectory;

    public PatchExecutionFixture(ExecutionMode mode = ExecutionMode.FullAccess, PatchApprovalLimits? limits = null)
    {
        Root = Directory.CreateDirectory(Path.Combine(DirectoryPath, "project")).FullName;
        Put("sample.md", "old\n");
        var paths = new ConfigurationPaths(Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"));
        Configuration = new ConfigurationValidator(paths).Validate(new AppSettings
        {
            ProjectRoot = Root, Authentication = new AuthenticationSettings { Mode = AuthenticationMode.Local },
            BashPath = mode == ExecutionMode.ReadOnly ? "" : ExternalProgramLocator.FindBash(Environment.GetEnvironmentVariable("MINIMCP_TEST_BASH_PATH") ?? "",
                Path.Combine(DirectoryPath, "settings.json")) ?? throw new Exception("Bash required for editing-mode checks.")
        }, Path.Combine(DirectoryPath, "settings.json"), new ExecutionPolicy { Mode = mode, PatchApprovalLimits = limits ?? new() });
    }

    public ToolExecutionService Execution(Func<PatchCommitStage, int, Task>? patch = null,
        Func<RecoveryPreparationStage, RecoveryDraft, Task>? prepare = null,
        Func<RecoveryManifestReplacementStage, string, Task>? replace = null,
        Func<RecoveryPreviewStage, string?, Task>? preview = null) => new(RunId, Configuration, patch, prepare, replace, preview);

    public McpRuntime Runtime(Func<PatchCommitStage, int, Task>? patch = null,
        Func<RecoveryPreparationStage, RecoveryDraft, Task>? prepare = null,
        Func<RecoveryManifestReplacementStage, string, Task>? replace = null,
        Func<RecoveryPreviewStage, string?, Task>? preview = null)
    {
        McpRuntime? runtime = null;
        runtime = new McpRuntime(0, services => services.Replace(ServiceDescriptor.Singleton(_ =>
            new ToolExecutionService(runtime!.Snapshot.RunId!.Value, runtime.Snapshot.Configuration!, patch, prepare, replace, preview))));
        return runtime;
    }

    public RuntimeConfiguration ConfigurationFor(ExecutionMode mode) => new ConfigurationValidator(new ConfigurationPaths(
        Path.Combine(DirectoryPath, "user"), Path.Combine(DirectoryPath, "state"))).Validate(Configuration.Settings, Configuration.SettingsFilePath, Configuration.Policy with { Mode = mode });

    public string PathOf(string path) => Path.Combine(Root, path);
    public void Put(string path, string content) => File.WriteAllText(PathOf(path), content, new UTF8Encoding(false));
    public string Get(string path) => File.ReadAllText(PathOf(path));
    public static string Patch(string body) => "*** Begin Patch\n" + body + "\n*** End Patch";
    public string[] ProjectSnapshot() => Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(Root, path) + ":" + (Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
    public Guid[] RecordIds() => Directory.Exists(RecoveryDirectory) ? Directory.EnumerateDirectories(RecoveryDirectory)
        .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)).Select(path => Guid.ParseExact(Path.GetFileName(path), "N")).ToArray() : [];
    public Task<RecoveryRecordSnapshot> Load(Guid changeId) => new RecoveryStore(RunId, Configuration).LoadAsync(changeId);
    public RecoveryManifest ReadManifest() => RecoveryManifestJson.Parse(File.ReadAllBytes(Path.Combine(RecoveryDirectory, RecordIds().Single().ToString("N"), RecoveryStore.ManifestFileName)));
    public async Task<RecoveryRecordSnapshot> Load(PatchApplyResult result)
    {
        Assert(!result.DryRun && result.Commit is { State: "Committed" }, "Only actual finalized writes return commit metadata");
        var saved = await Load(result.Commit!.ChangeId);
        Assert(saved.Manifest.RunId == result.Commit.RunId && saved.Manifest.State == RecoveryRecordState.Committed && saved.Manifest.ProjectRoot == Root,
            "Public commit matches the real durable record, workspace and session");
        return saved;
    }
    public static void Assert(bool condition, string message) { if (!condition) throw new Exception("Patch execution: " + message); }
    public static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception($"Expected {typeof(T).Name}."); }
    public static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task Drain(Task task) { try { await task; } catch { /* Drain owned work without replacing the check's original error. */ } }
    public void Dispose()
    {
        var directory = Path.GetFullPath(DirectoryPath);
        if (!string.Equals(Path.GetDirectoryName(directory), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Path.GetFileName(directory).StartsWith("minimcp-patch-execution-", StringComparison.Ordinal)) throw new IOException("Unexpected test cleanup path.");
        Directory.Delete(directory, recursive: true);
    }
}
