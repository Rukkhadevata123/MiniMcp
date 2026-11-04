using MiniMcp;
using System.Net;
using System.Text.Json;
using MiniMcp.Configuration;
using MiniMcp.Recovery;

if (args is ["--query-output-child", var outputMode, var outputMarker])
    return await QueryOutputChecks.ChildAsync(outputMode, outputMarker);

if (args is ["--cli-recovery-terminal", var terminalAction] && terminalAction is "restore" or "cancel" or "interrupt")
    return await CliRecoveryTerminalChecks.RunAsync(terminalAction);

if (args.Length > 0 && args[0] == "--no-config") return await WorkspaceQueryChecks.ChildAsync(args);
if (args.Length > 0 && args[0] == "--no-pager") return await GitQueryChecks.ChildAsync(args);
if (args is ["--git-helper", var gitMarker]) { await File.WriteAllTextAsync(gitMarker, "helper executed"); return 0; }

if (args is ["--cli-process", var cliControl])
    return await CliProcessChecks.ChildAsync(cliControl);
if (args is ["--cli-console-signal", var cliTarget, var cliSignalCount])
    return await CliProcessChecks.SignalAsync(int.Parse(cliTarget, System.Globalization.CultureInfo.InvariantCulture),
        int.Parse(cliSignalCount, System.Globalization.CultureInfo.InvariantCulture));

if (args is ["tunnel", "--no-autoupdate", "run", "--token-file", var tunnelTokenFile])
    return await TunnelChild.RunAsync(tunnelTokenFile, args);
if (args is ["--tunnel-descendant", var tunnelDescendantMarker])
    return await TunnelChild.DescendantAsync(tunnelDescendantMarker);
if (args is ["--tunnel-owner", var tunnelOwnerFile])
    return await TunnelChild.OwnerAsync(tunnelOwnerFile);

if (args is ["--recovery-lock-child", var lockPath, var lockMarker])
{
    using var held = RecoveryFileSystem.OpenFile(lockPath, FileMode.OpenOrCreate);
    RecoveryFileSystem.Lock(held);
    await File.WriteAllTextAsync(lockMarker + ".tmp", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    File.Move(lockMarker + ".tmp", lockMarker);
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

// A real descendant process used to verify Bash cancellation and runtime shutdown containment.
if (args is ["--execution-child", var marker])
{
    await File.WriteAllTextAsync(marker + ".tmp", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    File.Move(marker + ".tmp", marker);
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
{
    Timeout = TimeSpan.FromSeconds(3)
};
var runner = new CheckRunner("runtime", args);
using var lifetimeFixture = new RuntimeLifetimeFixture();
var configuration = lifetimeFixture.Configuration;

var Check = runner.InGroup("host");

async Task Healthy(McpRuntime runtime)
{
    using var response = await client.GetAsync(new Uri(runtime.Address!, "healthz"));
    Assert(response.StatusCode == HttpStatusCode.OK);
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Assert(body.RootElement.GetProperty("status").GetString() == "ok");
}

await Check("real loopback endpoints ignore ambient URLs and reject standalone MCP GET", async () =>
{
    var previous = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    try
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:1");
        await using var runtime = new McpRuntime(0);
        await runtime.StartAsync(configuration);
        Assert(runtime.Address!.Host == "127.0.0.1" && runtime.Address.Port > 1);
        Assert(ReferenceEquals(runtime.Snapshot.Configuration, configuration));
        await Healthy(runtime);
        using var mcpGet = await client.GetAsync(new Uri(runtime.Address, "mcp"));
        Assert(mcpGet.StatusCode == HttpStatusCode.MethodNotAllowed);
    }
    finally { Environment.SetEnvironmentVariable("ASPNETCORE_URLS", previous); }
});

await Check("stop is idempotent, releases its port and permits restarting", async () =>
{
    await using var runtime = new McpRuntime(0);
    await runtime.StartAsync(configuration);
    var port = runtime.Address!.Port;
    await runtime.StopAsync();
    await runtime.StopAsync();
    Assert(runtime.Address is null);
    await using (var replacement = new McpRuntime(port))
    {
        await replacement.StartAsync(configuration);
        await Healthy(replacement);
    }
    await runtime.StartAsync(configuration);
    await Healthy(runtime);
});

await Check("concurrent starts create only one host", async () =>
{
    await using var runtime = new McpRuntime(0);
    async Task<bool> Attempt()
    {
        try
        { await runtime.StartAsync(configuration); return true; }
        catch (InvalidOperationException) { return false; }
    }
    var attempts = await Task.WhenAll(Attempt(), Attempt());
    Assert(attempts.Count(success => success) == 1);
    await Healthy(runtime);
});

await Check("port conflict cleans failed startup without stopping its existing owner", async () =>
{
    await using var owner = new McpRuntime(0);
    await owner.StartAsync(configuration);
    await using var contender = new McpRuntime(owner.Address!.Port);
    await Reject<IOException>(() => contender.StartAsync(configuration));
    Assert(contender.Address is null);
    await Healthy(owner);
    await owner.StopAsync();
    await contender.StartAsync(configuration);
    await Healthy(contender);
});

await Check("cancelled start and stop retain consistent ownership", async () =>
{
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await using var runtime = new McpRuntime(0);
    await Reject<OperationCanceledException>(() => runtime.StartAsync(configuration, cancelled.Token));
    Assert(runtime.Address is null);
    await runtime.StartAsync(configuration);
    await Reject<OperationCanceledException>(() => runtime.StopAsync(cancelled.Token));
    await Healthy(runtime);
    await runtime.StopAsync();
    Assert(runtime.Address is null);
});

await Check("disposal releases the port and prevents subsequent startup", async () =>
{
    var runtime = new McpRuntime(0);
    try
    {
        await runtime.StartAsync(configuration);
        var port = runtime.Address!.Port;
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        await Reject<ObjectDisposedException>(() => runtime.StartAsync(configuration));
        await using var replacement = new McpRuntime(port);
        await replacement.StartAsync(configuration);
        await Healthy(replacement);
    }
    finally { await runtime.DisposeAsync(); }
});

await Check("host lifecycle does not dispatch work to the caller synchronization context", async () =>
{
    await using var runtime = new McpRuntime(0);
    var context = new RecordingSynchronizationContext();
    Task Invoke(Func<Task> operation)
    {
        var previous = SynchronizationContext.Current;
        try
        { SynchronizationContext.SetSynchronizationContext(context); return operation(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    await Invoke(() => runtime.StartAsync(configuration)).WaitAsync(TimeSpan.FromSeconds(5));
    await Healthy(runtime);
    await Invoke(() => runtime.StopAsync()).WaitAsync(TimeSpan.FromSeconds(5));
    Assert(context.PostCount == 0);
});

await Check("invalid internal test port overrides are rejected", () =>
{
    foreach (var port in new[] { -1, 65536 })
    {
        try
        { _ = new McpRuntime(port); }
        catch (ArgumentOutOfRangeException) { continue; }
        throw new Exception("Invalid port was accepted.");
    }
    return Task.CompletedTask;
});

await Check("configuration defaults follow user directories and native XDG rules", () =>
{
    var previousConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    var previousState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
    var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-paths-{Guid.NewGuid():N}");
    try
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(temporary, "config"));
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(temporary, "state"));
        var paths = ConfigurationPaths.ForCurrentUser();
        if (OperatingSystem.IsWindows())
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniMcp");
            Assert(paths.SettingsFile == Path.Combine(directory, "settings.json"));
            Assert(paths.PolicyFile == Path.Combine(directory, "policy.json"));
            Assert(paths.RecoveryDirectory == Path.Combine(directory, "recovery"));
        }
        else
        {
            Assert(paths.SettingsFile == Path.Combine(temporary, "config", "minimcp", "settings.json"));
            Assert(paths.PolicyFile == Path.Combine(temporary, "config", "minimcp", "policy.json"));
            Assert(paths.RecoveryDirectory == Path.Combine(temporary, "state", "minimcp", "recovery"));
            foreach (var value in new[] { "", "relative/path" })
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", value);
                Environment.SetEnvironmentVariable("XDG_STATE_HOME", value);
                var fallback = ConfigurationPaths.ForCurrentUser();
                var userDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Assert(fallback.SettingsFile == Path.Combine(userDirectory, ".config", "minimcp", "settings.json"));
                Assert(fallback.PolicyFile == Path.Combine(userDirectory, ".config", "minimcp", "policy.json"));
                Assert(fallback.RecoveryDirectory == Path.Combine(userDirectory, ".local", "state", "minimcp", "recovery"));
            }
        }
        Assert(!Directory.Exists(temporary));
    }
    finally
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousConfig);
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", previousState);
    }
    return Task.CompletedTask;
});

await Check("settings selection is anchored to the caller and leaves local policy fixed", () =>
{
    var paths = ConfigurationPaths.ForCurrentUser();
    var workingDirectory = Path.Combine(Path.GetTempPath(), "minimcp caller");
    var selected = paths.ResolveSettingsFile("configs/../settings.json", workingDirectory);
    Assert(selected == Path.Combine(workingDirectory, "settings.json"));
    Assert(paths.ResolveSettingsFile(null, workingDirectory) == paths.SettingsFile);
    Assert(paths.ResolveSettingsFile(selected, Path.GetTempPath()) == selected);
    var defaults = ConfigurationPaths.ForCurrentUser();
    Assert(paths.PolicyFile == defaults.PolicyFile && paths.RecoveryDirectory == defaults.RecoveryDirectory);
    return Task.CompletedTask;
});

await Check("JSON paths use their own file directories and remain stable after cwd changes", () =>
{
    var previous = Environment.CurrentDirectory;
    var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-paths-{Guid.NewGuid():N}");
    var settings = Path.Combine(temporary, "config", "settings.json");
    var policy = Path.Combine(temporary, "user", "policy.json");
    try
    {
        var project = ConfigurationPaths.ResolveValuePath("../project with spaces", settings);
        Environment.CurrentDirectory = Path.GetTempPath();
        Assert(project == Path.Combine(temporary, "project with spaces"));
        Assert(ConfigurationPaths.ResolveValuePath("../project with spaces", settings) == project);
        Assert(ConfigurationPaths.ResolveValuePath("recovery", policy) == Path.Combine(temporary, "user", "recovery"));
        Assert(ConfigurationPaths.ResolveValuePath(project, policy) == project);
        Assert(!Directory.Exists(temporary));
    }
    finally { Environment.CurrentDirectory = previous; }
    return Task.CompletedTask;
});

await Check("configuration paths do not expand home, environment or shell expressions", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "minimcp literal paths");
    var settings = Path.Combine(directory, "settings.json");
    foreach (var path in new[] { "~/project", "%LOCALAPPDATA%/project", "$HOME/project", "$(pwd)/project" })
        Assert(ConfigurationPaths.ResolveValuePath(path, settings) == Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)));
    return Task.CompletedTask;
});

await Check("configuration paths reject empty, malformed and ambiguous Windows paths", async () =>
{
    var paths = ConfigurationPaths.ForCurrentUser();
    var settings = paths.SettingsFile;
    foreach (var path in new[] { "", " ", "invalid\0path" })
        await Reject<ArgumentException>(() => Task.FromResult(ConfigurationPaths.ResolveValuePath(path, settings)));
    await Reject<ArgumentException>(() => Task.FromResult(paths.ResolveSettingsFile("settings.json", "relative/base")));
    await Reject<ArgumentException>(() => Task.FromResult(ConfigurationPaths.ResolveValuePath("project", "relative/settings.json")));
    if (OperatingSystem.IsWindows())
    {
        foreach (var path in new[] { "C:project", "C:", "\\project", "/project" })
        {
            await Reject<ArgumentException>(() => Task.FromResult(ConfigurationPaths.ResolveValuePath(path, settings)));
            await Reject<ArgumentException>(() => Task.FromResult(paths.ResolveSettingsFile(path, Path.GetTempPath())));
        }
    }
});

await CheckRunnerChecks.RunAsync(runner.InGroup("infrastructure.runner"));
await ExternalProgramLocatorChecks.RunAsync(runner.InGroup("configuration.external-programs"));
await ConfigurationJsonChecks.RunAsync(runner.InGroup("configuration.json"));
await ConfigurationStoreChecks.RunAsync(runner.InGroup("configuration.store"));
await ConfigurationValidatorChecks.RunAsync(runner.InGroup("configuration.validator"));
await ConfigurationValueChecks.RunAsync(runner.InGroup("configuration.values"));
await AuthenticationRegistrationChecks.RunAsync(runner.InGroup("tools.authentication"));
await ToolExecutionChecks.RunAsync(runner.InGroup("tools.execution"));
await QueryOutputChecks.RunAsync(runner.InGroup("tools.query-output"));
await WorkspaceQueryChecks.RunAsync(runner.InGroup("tools.workspace-query"));
await GitQueryChecks.RunAsync(runner.InGroup("tools.git-query"));
await ConfigurationDependencyChecks.RunAsync(runner.InGroup("configuration.dependencies"));
await ConfigurationSerializationChecks.RunAsync(runner.InGroup("configuration.serialization"));
await ConfigurationSaveChecks.RunAsync(runner.InGroup("configuration.settings-save"));
await ConfigurationPolicySaveChecks.RunAsync(runner.InGroup("configuration.policy-save"));
await ConfigurationPatchLimitChecks.RunAsync(runner.InGroup("configuration.patch-limits"));
await RecoveryStorageGuardChecks.RunAsync(runner.InGroup("recovery.storage-guard"));
await RecoveryStoreChecks.RunAsync(runner.InGroup("recovery.store"));
await RecoveryLoadingChecks.RunAsync(runner.InGroup("recovery.loading"));
await RecoveryPreparationValidationChecks.RunAsync(runner.InGroup("recovery.preparation"));
await RecoveryManifestReplacementChecks.RunAsync(runner.InGroup("recovery.manifest-replacement"));
await RecoveryApplyingStateChecks.RunAsync(runner.InGroup("recovery.applying"));
await RecoveryCompletionChecks.RunAsync(runner.InGroup("recovery.completion"));
await RecoveryFinalizationChecks.RunAsync(runner.InGroup("recovery.finalization"));
await RecordedPatchCommitChecks.RunAsync(runner.InGroup("recovery.patch-commit"));
await RecordedPatchFaultChecks.RunAsync(runner.InGroup("recovery.patch-fault"));
await RecordedPatchConcurrencyChecks.RunAsync(runner.InGroup("recovery.patch-concurrency"));
await RecordedToolExecutionChecks.RunAsync(runner.InGroup("recovery.tool-execution"));
await RecoveryPreviewChecks.RunAsync(runner.InGroup("recovery.preview"));
await RecoveryPreviewLifetimeChecks.RunAsync(runner.InGroup("recovery.preview-lifetime"));
await RecoveryRestoreChecks.RunAsync(runner.InGroup("recovery.restore"));
await RecoveryRestoreFaultChecks.RunAsync(runner.InGroup("recovery.restore-fault"));
await RecoveryRestoreLifetimeChecks.RunAsync(runner.InGroup("recovery.restore-lifetime"));
await LocalRecoveryEntryChecks.RunAsync(runner.InGroup("recovery.local-entry"));
await RecordedToolLifetimeChecks.RunAsync(runner.InGroup("recovery.tool-lifetime"));
await RuntimeConfigurationChecks.RunAsync(runner.InGroup("host.configuration"));
await RuntimeSnapshotChecks.RunAsync(runner.InGroup("host.snapshot"));
await RuntimeDiagnosticsChecks.RunAsync(runner.InGroup("host.diagnostics"));
await ProcessOutputReaderChecks.RunAsync(runner.InGroup("host.process-output"));
await TunnelChecks.RunAsync(runner.InGroup("host.tunnel"));
await RuntimeSessionChecks.RunAsync(runner.InGroup("host.session"));
await RuntimeCallChecks.RunAsync(runner.InGroup("host.calls"));
await HttpSecurityChecks.RunAsync(runner.InGroup("http.security"));
await McpHttpChecks.RunAsync(runner.InGroup("http.mcp"));
await RecordedToolHttpChecks.RunAsync(runner.InGroup("http.recorded-tools"));
await CliChecks.RunAsync(runner.InGroup("cli.basic"));
await CliCheckCommandChecks.RunAsync(runner.InGroup("cli.check-command"));
await CliRunChecks.RunAsync(runner.InGroup("cli.run"));
await CliProcessChecks.RunAsync(runner.InGroup("cli.process"));

return runner.Finish();

internal sealed class RecordingSynchronizationContext : SynchronizationContext
{
    private int _posts;
    public int PostCount => Volatile.Read(ref _posts);

    public override void Post(SendOrPostCallback callback, object? state)
    {
        Interlocked.Increment(ref _posts);
        base.Post(callback, state);
    }
}
