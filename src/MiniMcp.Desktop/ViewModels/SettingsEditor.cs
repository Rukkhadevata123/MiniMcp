using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using MiniMcp.Configuration;

namespace MiniMcp.Desktop.ViewModels;

// Drafts belong to this window, not to the running Runtime. The immutable file baselines retain
// the exact revisions required by ConfigurationStore; bindings never mutate a running snapshot.
[Microsoft.UI.Xaml.Data.Bindable]
public sealed partial class SettingsEditor : INotifyPropertyChanged
{
    private readonly ConfigurationStore _store;
    private readonly ConfigurationPaths _paths;
    private readonly Func<AppSettings, string, ExecutionPolicy, RuntimeConfiguration> _validate;
    private ConfigurationFile<AppSettings>? _settingsFile;
    private ConfigurationFile<ExecutionPolicy>? _policyFile;
    private AppSettings _draft = new();
    private ExecutionPolicy _policyDraft = new();
    private string _portText = "8000", _maxPathsText = "10", _maxLinesText = "500", _maxBytesText = "65536";
    private string _requestedPath;
    private int _settingsEdits, _policyEdits;
    private bool _isBusy, _sessionBusy, _interactionBusy;
    private RuntimeSnapshot _runtime = new(RuntimeState.Stopped);
    private SavedConfigurationStamp? _applied;
    private Guid? _appliedRunId;
    private string _error = "", _message = "", _dependencies = "点击“检查草稿与本机依赖”查看检查结果。";
    private string _activeDependencies = "启动后显示本次使用的程序路径和文件检查结果。";
    private CancellationTokenSource? _pendingCancellation;
    private Task _pending = Task.CompletedTask;
    private Task _cancelling = Task.CompletedTask;

    public SettingsEditor() : this(new ConfigurationStore(), ConfigurationPaths.ForCurrentUser(), new ConfigurationValidator().Validate) { }
    public SettingsEditor(ConfigurationStore store, ConfigurationPaths paths,
        Func<AppSettings, string, ExecutionPolicy, RuntimeConfiguration> validate)
    {
        _store = store; _paths = paths; _validate = validate; _requestedPath = paths.SettingsFile;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string DefaultSettingsPath => _paths.SettingsFile;
    public string PolicyPath => _paths.PolicyFile;
    public string DefaultRecoveryPath => _paths.RecoveryDirectory;
    public string SettingsPath => _settingsFile?.FilePath ?? "尚未加载配置";
    public string RequestedPath { get => _requestedPath; set { if (_requestedPath == value) return; _requestedPath = value ?? ""; Raise(); } }
    public bool IsLoaded => _settingsFile is not null && _policyFile is not null;
    public bool HasSavedSettings => _settingsFile?.Revision is not null;
    public bool IsBusy => _isBusy;
    public bool CanLoad => !_isBusy && !_sessionBusy && !_interactionBusy;
    public bool CanEdit => IsLoaded && CanLoad;
    public bool CanSaveSettings => CanEdit && (IsSettingsDirty || !HasSavedSettings);
    public bool CanSavePolicy => CanEdit && HasSavedSettings && (IsPolicyDirty || _policyFile?.Revision is null);
    public bool IsSettingsDirty => _settingsFile is not null && (_draft != _settingsFile.Value || _portText != Number(_settingsFile.Value.Port));
    public bool IsPolicyDirty => _policyFile is not null && (_policyDraft != _policyFile.Value ||
        _maxPathsText != Number(_policyFile.Value.ProjectEditLimits.MaxPaths) ||
        _maxLinesText != Number(_policyFile.Value.ProjectEditLimits.MaxChangedLines) ||
        _maxBytesText != Number(_policyFile.Value.ProjectEditLimits.MaxChangedBytes));
    public bool HasUnsavedChanges => IsSettingsDirty || IsPolicyDirty;
    public string DraftStatus => !IsLoaded ? "加载配置文件后开始编辑。" : HasUnsavedChanges ? "有未保存的修改。启动服务使用已保存的配置。" :
        HasSavedSettings ? "草稿与已保存文件一致。" : "保存运行配置后创建文件。";
    public string SavedMode => _policyFile is null ? "尚未加载本机策略" : $"已保存的默认模式：{_policyFile.Value.Mode}";
    public string ActiveMode => _runtime.Configuration is { } configuration
        ? $"本次模式：{configuration.Policy.Mode} · 状态：{_runtime.State}" : "尚未启动会话";
    public bool NeedsRestart => _runtime.HasOwnedResources && _runtime.RunId == _appliedRunId && _applied is not null && CurrentStamp != _applied;
    public string RestartMessage => "配置已更新，点击概览中的“重启并应用配置”后生效。";
    public string ErrorMessage => _error;
    public bool HasError => _error.Length != 0;
    public string Message => _message;
    public string Dependencies => _dependencies;
    public string ActiveDependencies => _activeDependencies;
    public Task PendingOperation => _pending;
    private SavedConfigurationStamp? CurrentStamp => _settingsFile is null || _policyFile is null ? null :
        new(_settingsFile.FilePath, _settingsFile.Revision, _policyFile.Revision);

    public string ProjectRoot { get => _draft.ProjectRoot; set => EditSettings(_draft with { ProjectRoot = value ?? "" }); }
    public string PortText { get => _portText; set { if (_portText == value) return; _portText = value ?? ""; _settingsEdits++; Changed(); } }
    public string BashPath { get => _draft.BashPath; set => EditSettings(_draft with { BashPath = value ?? "" }); }
    public string RgPath { get => _draft.RgPath; set => EditSettings(_draft with { RgPath = value ?? "" }); }
    public string GitPath { get => _draft.GitPath; set => EditSettings(_draft with { GitPath = value ?? "" }); }
    public int AuthenticationIndex
    {
        get => (int)_draft.Authentication.Mode;
        set
        {
            if (value is < 0 or > 1) return;
            var next = _draft with { Authentication = _draft.Authentication with { Mode = (AuthenticationMode)value } };
            if ((AuthenticationMode)value == AuthenticationMode.Local) next = next with { Tunnel = next.Tunnel with { Enabled = false } };
            EditSettings(next); Raise(nameof(IsCloudflareAuthentication)); Raise(nameof(TunnelEnabled));
        }
    }
    public bool IsCloudflareAuthentication => _draft.Authentication.Mode == AuthenticationMode.CloudflareAccess;
    public string PublicOrigin { get => _draft.Authentication.PublicOrigin ?? ""; set => EditSettings(_draft with { Authentication = _draft.Authentication with { PublicOrigin = value } }); }
    public string Team { get => _draft.Authentication.Team ?? ""; set => EditSettings(_draft with { Authentication = _draft.Authentication with { Team = value } }); }
    public string Audience { get => _draft.Authentication.Audience ?? ""; set => EditSettings(_draft with { Authentication = _draft.Authentication with { Audience = value } }); }
    public bool TunnelEnabled { get => _draft.Tunnel.Enabled; set => EditSettings(_draft with { Tunnel = _draft.Tunnel with { Enabled = value } }); }
    public string CloudflaredPath { get => _draft.Tunnel.ExecutablePath; set => EditSettings(_draft with { Tunnel = _draft.Tunnel with { ExecutablePath = value ?? "" } }); }
    public string TokenFilePath { get => _draft.Tunnel.TokenFilePath; set => EditSettings(_draft with { Tunnel = _draft.Tunnel with { TokenFilePath = value ?? "" } }); }
    public int ModeIndex
    {
        get => (int)_policyDraft.Mode;
        set { if (value is < 0 or > 2) return; EditPolicy(_policyDraft with { Mode = (ExecutionMode)value }); Raise(nameof(ModeDescription)); }
    }
    public string ModeDescription => _policyDraft.Mode switch
    {
        ExecutionMode.ReadOnly => "ReadOnly：允许读取文件、搜索文本、查询 Git 和预览完整补丁；写入和 Bash 已禁用。",
        ExecutionMode.ProjectEdit => "ProjectEdit：允许限额内的普通源码、文档补丁，执行前自动备份。涉及删除、移动、关键文件或未知文件的补丁会整批拒绝；Bash 已禁用。",
        _ => "FullAccess：允许通过基础检查的补丁和 Bash，补丁执行前自动备份。Bash 按运行账号权限访问文件和网络，其改动不在补丁恢复范围内。"
    };
    public string RecoveryDirectory { get => _policyDraft.RecoveryDirectory; set => EditPolicy(_policyDraft with { RecoveryDirectory = value ?? "" }); }
    public string MaxPathsText { get => _maxPathsText; set => EditLimit(ref _maxPathsText, value); }
    public string MaxLinesText { get => _maxLinesText; set => EditLimit(ref _maxLinesText, value); }
    public string MaxBytesText { get => _maxBytesText; set => EditLimit(ref _maxBytesText, value); }

    public void SetSessionBusy(bool value) { _sessionBusy = value; RaiseState(); }
    public void SetInteractionBusy(bool value) { _interactionBusy = value; RaiseState(); }
    public void SetRuntime(RuntimeSnapshot snapshot, PreparedStartup? applied = null)
    {
        _runtime = snapshot;
        if (applied is not null && snapshot.Configuration == applied.Configuration)
        { _applied = applied.Stamp; _appliedRunId = snapshot.RunId; _activeDependencies = applied.Dependencies; }
        Raise(nameof(ActiveMode)); Raise(nameof(NeedsRestart)); Raise(nameof(ActiveDependencies));
    }
    public Task CancelPendingAsync() => _cancelling = _pendingCancellation?.CancelAsync() ?? Task.CompletedTask;

    private void EditSettings(AppSettings value, [CallerMemberName] string property = "")
    {
        if (_draft == value) return;
        _draft = value; _settingsEdits++; Changed(property);
    }
    private void EditPolicy(ExecutionPolicy value, [CallerMemberName] string property = "")
    {
        if (_policyDraft == value) return;
        _policyDraft = value; _policyEdits++; Changed(property);
    }
    private void EditLimit(ref string field, string value, [CallerMemberName] string property = "")
    {
        if (field == value) return;
        field = value ?? ""; _policyEdits++; Changed(property);
    }
    private void Changed([CallerMemberName] string property = "") { Raise(property); RaiseState(); }
    private void RaiseState()
    {
        foreach (var property in new[] { nameof(IsLoaded), nameof(HasSavedSettings), nameof(IsBusy), nameof(CanLoad), nameof(CanEdit),
            nameof(CanSaveSettings), nameof(CanSavePolicy), nameof(IsSettingsDirty), nameof(IsPolicyDirty), nameof(HasUnsavedChanges),
            nameof(DraftStatus), nameof(SavedMode), nameof(NeedsRestart), nameof(HasError), nameof(ErrorMessage), nameof(Message), nameof(Dependencies) }) Raise(property);
    }
    private void Raise([CallerMemberName] string property = "") => PropertyChanged?.Invoke(this, new(property));
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static int Integer(string text, string field)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ConfigurationValidationException(field, "请输入范围内的整数。");
        return value;
    }
    private AppSettings BuildSettings() => _draft with { Port = Integer(_portText, "port") };
    private ExecutionPolicy BuildPolicy() => _policyDraft with
    {
        ProjectEditLimits = new() { MaxPaths = Integer(_maxPathsText, "projectEditLimits.maxPaths"),
            MaxChangedLines = Integer(_maxLinesText, "projectEditLimits.maxChangedLines"), MaxChangedBytes = Integer(_maxBytesText, "projectEditLimits.maxChangedBytes") }
    };
    private void ResetSettings(AppSettings value) { _draft = value; _portText = Number(value.Port); Raise(""); }
    private void ResetPolicy(ExecutionPolicy value)
    {
        _policyDraft = value; _maxPathsText = Number(value.ProjectEditLimits.MaxPaths);
        _maxLinesText = Number(value.ProjectEditLimits.MaxChangedLines); _maxBytesText = Number(value.ProjectEditLimits.MaxChangedBytes); Raise("");
    }
}

public sealed record SavedConfigurationStamp(string SettingsPath, string? SettingsRevision, string? PolicyRevision);
public sealed record PreparedStartup(RuntimeConfiguration Configuration, SavedConfigurationStamp Stamp, string Dependencies);
