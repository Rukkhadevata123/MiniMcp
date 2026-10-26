using MiniMcp;
using MiniMcp.Configuration;
using MiniMcp.DesktopChecks;

if (args is ["--ui"]) return UiSmoke.Run();
if (args is ["--ui-screenshots", var screenshotDirectory]) return UiSmoke.Run(screenshotDirectory);
if (args is ["tunnel", "--no-autoupdate", "run", "--token-file", var tunnelTokenFile]) return await DesktopTunnelChild.Run(tunnelTokenFile);

var passed = 0; var failed = 0;
async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error}"); }
}

await Check("settings: missing files load restrictive drafts without creating configuration or recovery storage", async () =>
{
    await using var fixture = new SettingsFixture(seed: false);
    await fixture.Editor.LoadAsync(fixture.Source);
    Assert(fixture.Editor.IsLoaded && !fixture.Editor.HasSavedSettings && fixture.Editor.ModeIndex == 0 && fixture.Editor.AuthenticationIndex == 0, "Restrictive defaults");
    Assert(!File.Exists(fixture.Source) && !File.Exists(fixture.Paths.PolicyFile) && !Directory.Exists(fixture.Paths.RecoveryDirectory), "Load creates nothing");
    Assert(fixture.Editor.CanSaveSettings && !fixture.Editor.CanSavePolicy, "First-run workflow");
    fixture.Editor.ProjectRoot = fixture.Root; fixture.Editor.AuthenticationIndex = 1;
    await fixture.Editor.SaveSettingsAsync();
    Assert(!fixture.Editor.HasError && File.Exists(fixture.Source) && fixture.Editor.HasSavedSettings, "First settings save");
    await fixture.Editor.SavePolicyAsync();
    Assert(!fixture.Editor.HasError && File.Exists(fixture.Paths.PolicyFile), "First fixed policy save");
});
await Check("settings: raw relative paths and revisions roundtrip through real independent saves", async () =>
{
    await using var fixture = new SettingsFixture();
    await fixture.Load();
    fixture.Editor.ProjectRoot = "./project"; fixture.Editor.PortText = "8123"; fixture.Editor.RgPath = "missing-rg"; fixture.Editor.GitPath = "missing-git";
    await fixture.Editor.SaveSettingsAsync();
    Assert(!fixture.Editor.HasError && !fixture.Editor.IsSettingsDirty, "Saved draft clean");
    var saved = await fixture.Store.ReadSettingsAsync(fixture.Source, fixture.DirectoryPath);
    Assert(saved.Value.ProjectRoot == "./project" && saved.Value.RgPath == "missing-rg" && saved.Value.Port == 8123 && saved.Revision is not null, "Raw paths retained");
    fixture.Editor.ModeIndex = 1; fixture.Editor.MaxPathsText = "0"; fixture.Editor.RecoveryDirectory = "../records";
    await fixture.Editor.SavePolicyAsync();
    Assert(!fixture.Editor.HasError && !fixture.Editor.IsPolicyDirty, "Policy revision updated");
    var policy = await fixture.Store.ReadPolicyAsync();
    Assert(policy.Value.Mode == ExecutionMode.ProjectEdit && policy.Value.ProjectEditLimits.MaxPaths == 0 && policy.Value.RecoveryDirectory == "../records", "Policy independent values");
    fixture.Editor.MaxPathsText = "3"; await fixture.Editor.SavePolicyAsync();
    Assert(!fixture.Editor.HasError, "Subsequent save uses new revision");
});
await Check("settings: invalid integer and workspace/recovery boundaries retain input and existing bytes", async () =>
{
    await using var fixture = new SettingsFixture(); await fixture.Load();
    var before = File.ReadAllBytes(fixture.Source);
    fixture.Editor.PortText = "not an integer"; await fixture.Editor.SaveSettingsAsync();
    Assert(fixture.Editor.HasError && fixture.Editor.PortText == "not an integer" && before.SequenceEqual(File.ReadAllBytes(fixture.Source)), "Invalid input kept");
    fixture.Editor.PortText = "0"; await fixture.Editor.SaveSettingsAsync();
    Assert(fixture.Editor.HasError && fixture.Editor.PortText == "0", "Port bounds");
    fixture.Editor.PortText = "8000"; fixture.Editor.ProjectRoot = Path.GetPathRoot(fixture.Root)!; await fixture.Editor.CheckDraftAsync();
    Assert(fixture.Editor.HasError && before.SequenceEqual(File.ReadAllBytes(fixture.Source)), "Filesystem root rejected");
    fixture.Editor.ProjectRoot = fixture.Root; fixture.Editor.RecoveryDirectory = fixture.Root; await fixture.Editor.SavePolicyAsync();
    Assert(fixture.Editor.HasError && !File.Exists(fixture.Paths.PolicyFile), "In-project recovery rejected without policy writes");
});
await Check("settings: external settings/policy edits and malformed reloads never overwrite or discard the draft", async () =>
{
    await using var fixture = new SettingsFixture(); await fixture.Load();
    fixture.Editor.PortText = "8111";
    var external = fixture.Settings with { Port = 8222 };
    await File.WriteAllTextAsync(fixture.Source, ConfigurationJson.SerializeSettings(external));
    await fixture.Editor.SaveSettingsAsync();
    Assert(fixture.Editor.HasError && fixture.Editor.PortText == "8111" && fixture.Editor.ErrorMessage.Contains("外部修改"), "Settings conflict keeps draft");
    Assert((await fixture.Store.ReadSettingsAsync(fixture.Source, fixture.DirectoryPath)).Value.Port == 8222, "External bytes kept");
    var invalid = Path.Combine(fixture.DirectoryPath, "broken.json"); await File.WriteAllTextAsync(invalid, "{bad json");
    await fixture.Editor.LoadAsync(invalid);
    Assert(fixture.Editor.HasError && fixture.Editor.SettingsPath == fixture.Source && fixture.Editor.PortText == "8111", "Failed load keeps old baseline/draft");
    await fixture.Load(); fixture.Editor.ModeIndex = 1;
    Directory.CreateDirectory(Path.GetDirectoryName(fixture.Paths.PolicyFile)!);
    await File.WriteAllTextAsync(fixture.Paths.PolicyFile, ConfigurationJson.SerializePolicy(new() { Mode = ExecutionMode.ReadOnly, ProjectEditLimits = new() { MaxPaths = 8 } }));
    await fixture.Editor.SavePolicyAsync();
    Assert(fixture.Editor.HasError && fixture.Editor.ModeIndex == 1 && (await fixture.Store.ReadPolicyAsync()).Value.ProjectEditLimits.MaxPaths == 8, "Policy conflict keeps both sides");
});
await Check("settings: authentication switching preserves cloud fields and disables incompatible tunnels", async () =>
{
    await using var fixture = new SettingsFixture(); await fixture.Load();
    fixture.Editor.AuthenticationIndex = 0; fixture.Editor.PublicOrigin = "https://mcp.example.com"; fixture.Editor.Team = "team";
    fixture.Editor.Audience = "application-aud"; fixture.Editor.TunnelEnabled = true;
    fixture.Editor.AuthenticationIndex = 1;
    Assert(!fixture.Editor.TunnelEnabled && fixture.Editor.Team == "team" && fixture.Editor.PublicOrigin.Contains("https"), "Local disables tunnel without clearing cloud values");
    await fixture.Editor.SaveSettingsAsync(); Assert(!fixture.Editor.HasError, "Local config saves");
});
await Check("settings: optional rg/Git and prohibited Bash do not block two modes or execute fixture files", async () =>
{
    await using var fixture = new SettingsFixture(); await fixture.Load();
    fixture.Editor.BashPath = "missing-bash"; fixture.Editor.RgPath = "missing-rg"; fixture.Editor.GitPath = "missing-git";
    foreach (var mode in new[] { 0, 1 })
    {
        fixture.Editor.ModeIndex = mode; await fixture.Editor.CheckDraftAsync();
        Assert(!fixture.Editor.HasError && fixture.Editor.Dependencies.Contains("无需安装") && fixture.Editor.Dependencies.Contains("对应查询不可用"), "Optional/disabled dependencies");
    }
    fixture.Editor.ModeIndex = 2; await fixture.Editor.CheckDraftAsync();
    Assert(fixture.Editor.HasError, "FullAccess still requires Bash");
    Assert(!Directory.Exists(fixture.Paths.RecoveryDirectory), "Checks create no recovery storage");
});
await Check("settings: policy saves use persisted settings while unsaved settings remain editable", async () =>
{
    await using var fixture = new SettingsFixture(); await fixture.Load();
    fixture.Editor.ProjectRoot = "missing-project"; fixture.Editor.ModeIndex = 1;
    await fixture.Editor.SavePolicyAsync();
    Assert(!fixture.Editor.HasError && fixture.Editor.IsSettingsDirty && fixture.Editor.ProjectRoot == "missing-project", "Policy uses saved project, draft kept");
    Assert((await fixture.Store.ReadPolicyAsync()).Value.Mode == ExecutionMode.ProjectEdit, "Policy actually persisted");
});
await Check("settings: invalid FullAccess dependencies can be repaired by saving a ReadOnly policy first", async () =>
{
    await using var fixture = new SettingsFixture();
    await File.WriteAllTextAsync(fixture.Source, ConfigurationJson.SerializeSettings(fixture.Settings with { BashPath = "missing-bash" }));
    Directory.CreateDirectory(Path.GetDirectoryName(fixture.Paths.PolicyFile)!);
    await File.WriteAllTextAsync(fixture.Paths.PolicyFile, ConfigurationJson.SerializePolicy(new() { Mode = ExecutionMode.FullAccess }));
    await fixture.Load(); fixture.Editor.ProjectRoot = "./project"; fixture.Editor.ModeIndex = 0;
    await fixture.Editor.SavePolicyAsync(); Assert(!fixture.Editor.HasError, "Mode downgrade does not need disabled Bash");
    await fixture.Editor.SaveSettingsAsync(); Assert(!fixture.Editor.HasError, "Settings can then be saved");
});
await Check("settings: cancellation before save creates nothing and a subsequent save remains usable", async () =>
{
    await using var fixture = new SettingsFixture(); await fixture.Load(); var before = File.ReadAllBytes(fixture.Source);
    fixture.Editor.PortText = "8333"; using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await fixture.Editor.SaveSettingsAsync(cancelled.Token);
    Assert(!fixture.Editor.HasError && fixture.Editor.IsSettingsDirty && before.SequenceEqual(File.ReadAllBytes(fixture.Source)), "Cancelled save kept draft");
    await fixture.Editor.SaveSettingsAsync(); Assert(!fixture.Editor.HasError && !fixture.Editor.IsSettingsDirty, "Retry succeeds");
});
await Check("settings: concurrent draft edits survive an in-flight real save and receive a usable new baseline", async () =>
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var fixture = new SettingsFixture(beforeCommit: async () => { entered.TrySetResult(); await release.Task; });
    await fixture.Load(); fixture.Editor.PortText = "8444";
    var saving = fixture.Editor.SaveSettingsAsync();
    try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(4)); fixture.Editor.PortText = "8555"; }
    finally { release.TrySetResult(); await saving; }
    Assert(!fixture.Editor.HasError && fixture.Editor.IsSettingsDirty && fixture.Editor.PortText == "8555", "New edit survives");
    Assert((await fixture.Store.ReadSettingsAsync(fixture.Source, fixture.DirectoryPath)).Value.Port == 8444, "Captured save committed");
    await fixture.Editor.SaveSettingsAsync(); Assert(!fixture.Editor.HasError, "Saved baseline carries new revision");
});
await Check("settings: actual Runtime remains unchanged after saves and applies the new mode only after explicit restart", async () =>
{
    await using var fixture = new SettingsFixture(); await fixture.Load();
    await using var runtime = new McpRuntime(0);
    var first = await fixture.Editor.ReadSavedForStartupAsync(); await runtime.StartAsync(first.Configuration); fixture.Editor.SetRuntime(runtime.Snapshot, first);
    var id = runtime.Snapshot.RunId;
    Assert(!fixture.Editor.NeedsRestart && runtime.Snapshot.Configuration!.Policy.Mode == ExecutionMode.ReadOnly, "Initial applied state");
    var activeDependencies = fixture.Editor.ActiveDependencies;
    fixture.Editor.ModeIndex = 1; await fixture.Editor.CheckDraftAsync();
    Assert(fixture.Editor.ActiveDependencies == activeDependencies, "Draft checking cannot rewrite running dependency display");
    await fixture.Editor.SavePolicyAsync();
    Assert(!fixture.Editor.HasError && fixture.Editor.NeedsRestart && runtime.Snapshot.RunId == id && runtime.Snapshot.Configuration!.Policy.Mode == ExecutionMode.ReadOnly, "Save never hot-switches");
    var second = await fixture.Editor.ReadSavedForStartupAsync(); await runtime.RestartAsync(second.Configuration); fixture.Editor.SetRuntime(runtime.Snapshot, second);
    Assert(runtime.Snapshot.RunId != id && runtime.Snapshot.Configuration!.Policy.Mode == ExecutionMode.ProjectEdit && !fixture.Editor.NeedsRestart, "Explicit restart creates new run");
});
await Check("settings: startup ignores unsaved drafts and selecting another source preserves the fixed policy location", async () =>
{
    await using var fixture = new SettingsFixture(); await fixture.Load(); fixture.Editor.PortText = "invalid-draft";
    var startup = await fixture.Editor.ReadSavedForStartupAsync();
    Assert(startup.Configuration.Settings.Port == fixture.Settings.Port && fixture.Editor.PortText == "invalid-draft", "Only saved file starts");
    var other = Path.Combine(fixture.DirectoryPath, "selected.md"); await File.WriteAllTextAsync(other, ConfigurationJson.SerializeSettings(fixture.Settings));
    await fixture.Editor.LoadAsync(other);
    Assert(fixture.Editor.SettingsPath == other && fixture.Editor.PolicyPath == fixture.Paths.PolicyFile && !fixture.Editor.HasUnsavedChanges, "Explicit selected source with fixed policy");
});

await MonitorChecks.Run(Check);

Console.WriteLine($"\n{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
