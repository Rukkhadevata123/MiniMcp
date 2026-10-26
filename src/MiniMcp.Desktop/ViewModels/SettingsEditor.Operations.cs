using MiniMcp.Configuration;
using MiniMcp.Diagnostics;

namespace MiniMcp.Desktop.ViewModels;

public sealed partial class SettingsEditor
{
    // The shell asks before discard. A failed load never replaces the existing draft/revisions.
    public Task LoadAsync(string path, CancellationToken cancellationToken = default) => Begin(async token =>
    {
        var source = _paths.ResolveSettingsFile(path, AppContext.BaseDirectory);
        var loaded = await Task.Run(async () =>
        {
            ConfigurationFile<AppSettings> settings;
            try { settings = await _store.ReadSettingsAsync(source, AppContext.BaseDirectory, token); }
            catch (ConfigurationLoadException error) when (error.InnerException is FileNotFoundException or DirectoryNotFoundException)
            { settings = new(source, new AppSettings(), null); }
            var policy = await _store.ReadPolicyAsync(token);
            return (settings, policy);
        }, token);
        token.ThrowIfCancellationRequested();
        _settingsFile = loaded.settings; _policyFile = loaded.policy;
        RequestedPath = loaded.settings.FilePath;
        ResetSettings(loaded.settings.Value); ResetPolicy(loaded.policy.Value);
        _dependencies = "检查草稿可查看本机依赖；不会执行外部程序。";
        _message = HasSavedSettings ? "已加载配置与固定的本机策略。" : "运行配置文件尚不存在，填写后保存即可创建。";
    }, cancellationToken);

    public Task SaveSettingsAsync(CancellationToken cancellationToken = default) => Begin(async token =>
    {
        RequireLoaded();
        var draft = BuildSettings(); var edits = _settingsEdits;
        var file = _settingsFile! with { Value = draft };
        var saved = await Task.Run(() => _store.SaveSettingsAsync(file, token), token);
        // Do not test cancellation after a durable save or lose its updated revision.
        _settingsFile = saved;
        if (_settingsEdits == edits) ResetSettings(saved.Value);
        _message = "运行配置已保存；权限策略请使用下方独立的保存按钮。本次运行不会自动改变。";
    }, cancellationToken);

    public Task SavePolicyAsync(CancellationToken cancellationToken = default) => Begin(async token =>
    {
        RequireLoaded();
        if (_settingsFile!.Revision is null) throw new InvalidOperationException("请先保存运行配置，再保存本机策略。");
        var draft = BuildPolicy(); var edits = _policyEdits;
        var file = _policyFile! with { Value = draft }; var savedSettings = _settingsFile;
        var saved = await Task.Run(() => _store.SavePolicyAsync(file, savedSettings, token), token);
        _policyFile = saved;
        if (_policyEdits == edits) ResetPolicy(saved.Value);
        _message = "本机策略已保存；本次运行继续使用原模式，重启后应用。";
    }, cancellationToken);

    public Task CheckDraftAsync(CancellationToken cancellationToken = default) => Begin(async token =>
    {
        RequireLoaded();
        var settings = BuildSettings(); var policy = BuildPolicy(); var source = _settingsFile!.FilePath;
        var settingsEdits = _settingsEdits; var policyEdits = _policyEdits;
        var dependencies = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var configuration = _validate(settings, source, policy);
            return DescribeDependencies(configuration);
        }, token);
        if (settingsEdits != _settingsEdits || policyEdits != _policyEdits)
        { _message = "检查期间草稿已改变，请重新检查。"; return; }
        _dependencies = dependencies;
        _message = "草稿通过本机配置及必需依赖校验；没有写文件、启动程序或验证公网连接。";
    }, cancellationToken);

    // The lifecycle caller owns this task. It reads saved files, never the bound draft, and
    // carries revisions only for the local restart indication (not execution/approval tickets).
    public async Task<PreparedStartup> ReadSavedForStartupAsync(CancellationToken token = default)
    {
        RequireLoaded();
        var source = _settingsFile!.FilePath;
        var loaded = await Task.Run(async () =>
        {
            var settings = await _store.ReadSettingsAsync(source, AppContext.BaseDirectory, token);
            var policy = await _store.ReadPolicyAsync(token);
            var configuration = _validate(settings.Value, source, policy.Value);
            return (settings, policy, configuration, dependencies: DescribeDependencies(configuration));
        }, token);
        token.ThrowIfCancellationRequested();
        var settingsDirty = IsSettingsDirty; var policyDirty = IsPolicyDirty;
        _settingsFile = loaded.settings; _policyFile = loaded.policy;
        if (!settingsDirty) ResetSettings(loaded.settings.Value);
        if (!policyDirty) ResetPolicy(loaded.policy.Value);
        RaiseState();
        return new(loaded.configuration, new(source, loaded.settings.Revision, loaded.policy.Revision), loaded.dependencies);
    }

    private Task Begin(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        if (_isBusy || _sessionBusy) return Task.CompletedTask;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancelling = Task.CompletedTask;
        _pendingCancellation = cancellation; _isBusy = true; _error = ""; _message = "正在处理…"; RaiseState();
        return _pending = RunAsync();
        async Task RunAsync()
        {
            await Task.Yield();
            try { cancellation.Token.ThrowIfCancellationRequested(); await operation(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { _message = "操作已取消，草稿保留。"; }
            catch (ConfigurationConflictException) { _error = "文件已被外部修改。草稿已保留，请重新加载后合并修改；不会覆盖外部内容。"; _message = "保存冲突"; }
            catch (Exception error) { _error = RuntimeDiagnosticText.Sanitize(error.Message); _message = "操作未完成，草稿保留。"; }
            finally
            {
                try { await _cancelling; }
                catch (Exception error) { _error = RuntimeDiagnosticText.Sanitize("取消回调失败：" + error.Message); }
                _pendingCancellation = null; cancellation.Dispose(); _isBusy = false; Raise(nameof(SettingsPath)); RaiseState();
            }
        }
    }
    private void RequireLoaded() { if (!IsLoaded) throw new InvalidOperationException("请先加载运行配置与本机策略。"); }
    private static string DescribeDependencies(RuntimeConfiguration configuration)
    {
        var settings = configuration.Settings;
        string Optional(string name, Func<string?> locate)
        {
            try { return locate() is { } path ? $"{name}：找到程序文件 {path}" : $"{name}：未找到，仅对应查询不可用"; }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
            { return $"{name}：程序路径不可用，仅对应查询不可用"; }
        }
        var bash = configuration.Policy.Mode == ExecutionMode.FullAccess ? $"Bash：程序文件已检查 {settings.BashPath}" : "Bash：此模式禁止使用，无需安装";
        var rg = Optional("rg", () => ExternalProgramLocator.FindRipgrep(settings.RgPath, configuration.SettingsFilePath, settings.ProjectRoot));
        var git = Optional("Git", () => ExternalProgramLocator.FindGit(settings.GitPath, configuration.SettingsFilePath, settings.ProjectRoot));
        var tunnel = settings.Tunnel.Enabled ? "隧道：cloudflared 文件及 token 文件可读/非空检查通过" : "隧道：关闭";
        return RuntimeDiagnosticText.Sanitize($"{bash}\n{rg}\n{git}\n{tunnel}\n仅本机文件检查；程序版本、仓库支持和公网认证仍由实际操作核验。");
    }
}
