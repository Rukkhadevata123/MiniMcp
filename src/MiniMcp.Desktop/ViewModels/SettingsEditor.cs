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

    private ConfigurationFile<AppSettings>? _settingsBaseline;
    private ConfigurationFile<ExecutionPolicy>? _policyBaseline;
    private AppSettings _settingsDraft = new();
    private ExecutionPolicy _policyDraft = new();
    private string _portText = "8000", _maxPathsText = "10", _maxLinesText = "500", _maxBytesText = "65536";
    private string _requestedPath;
    private int _settingsEditVersion;
    private int _policyEditVersion;

    private RuntimeSnapshot _runtime = new(RuntimeState.Stopped);
    private SavedConfigurationStamp? _appliedStamp;
    private Guid? _appliedRunId;
    private string _activeDependencies = "启动后显示本次使用的程序路径和文件检查结果。";

    private bool _isBusy, _sessionBusy, _interactionBusy;
    private string _error = "", _message = "", _dependencies = "点击“检查草稿与本机依赖”查看检查结果。";
    private CancellationTokenSource? _pendingCancellation;
    private Task _pending = Task.CompletedTask;
    private Task _cancelling = Task.CompletedTask;

    public SettingsEditor() : this(new ConfigurationStore(), ConfigurationPaths.ForCurrentUser(), new ConfigurationValidator().Validate) { }
    public SettingsEditor(ConfigurationStore store, ConfigurationPaths paths,
        Func<AppSettings, string, ExecutionPolicy, RuntimeConfiguration> validate)
    {
        _store = store;
        _paths = paths;
        _validate = validate;
        _requestedPath = paths.SettingsFile;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string DefaultSettingsPath => _paths.SettingsFile;
    public string PolicyPath => _paths.PolicyFile;
    public string DefaultRecoveryPath => _paths.RecoveryDirectory;
    public string SettingsPath => _settingsBaseline?.FilePath ?? "尚未加载配置";
    public string RequestedPath
    {
        get => _requestedPath;
        set
        {
            if (_requestedPath == value) return;
            _requestedPath = value ?? "";
            Raise();
        }
    }

    public bool IsLoaded => _settingsBaseline is not null && _policyBaseline is not null;
    public bool HasSavedSettings => _settingsBaseline?.Revision is not null;
    public bool IsBusy => _isBusy;
    public bool CanLoad => !_isBusy && !_sessionBusy && !_interactionBusy;
    public bool CanEdit => IsLoaded && CanLoad;
    public bool CanSaveSettings => CanEdit && (IsSettingsDirty || !HasSavedSettings);
    public bool CanSavePolicy => CanEdit && HasSavedSettings && (IsPolicyDirty || _policyBaseline?.Revision is null);

    public bool IsSettingsDirty => _settingsBaseline is not null &&
        (_settingsDraft != _settingsBaseline.Value || _portText != Number(_settingsBaseline.Value.Port));
    public bool IsPolicyDirty
    {
        get
        {
            if (_policyBaseline is null) return false;
            var savedPolicy = _policyBaseline.Value;
            var limits = savedPolicy.ProjectEditLimits;
            return _policyDraft != savedPolicy ||
                _maxPathsText != Number(limits.MaxPaths) ||
                _maxLinesText != Number(limits.MaxChangedLines) ||
                _maxBytesText != Number(limits.MaxChangedBytes);
        }
    }
    public bool HasUnsavedChanges => IsSettingsDirty || IsPolicyDirty;
    public string DraftStatus
    {
        get
        {
            if (!IsLoaded) return "加载配置文件后开始编辑。";
            if (HasUnsavedChanges) return "有未保存的修改。启动服务使用已保存的配置。";
            return HasSavedSettings ? "草稿与已保存文件一致。" : "保存运行配置后创建文件。";
        }
    }

    public string SavedMode => _policyBaseline is null ? "尚未加载本机策略" : $"已保存的默认模式：{_policyBaseline.Value.Mode}";
    public string ActiveMode => _runtime.Configuration is { } configuration
        ? $"本次模式：{configuration.Policy.Mode} · 状态：{_runtime.State}" : "尚未启动会话";
    public bool NeedsRestart => _runtime.HasOwnedResources && _runtime.RunId == _appliedRunId && _appliedStamp is not null && CurrentStamp != _appliedStamp;
    public string RestartMessage => "配置已更新，点击概览中的“重启并应用配置”后生效。";
    public string ErrorMessage => _error;
    public bool HasError => _error.Length != 0;
    public string Message => _message;
    public string Dependencies => _dependencies;
    public string ActiveDependencies => _activeDependencies;
    public Task PendingOperation => _pending;
    private SavedConfigurationStamp? CurrentStamp => _settingsBaseline is null || _policyBaseline is null ? null :
        new(_settingsBaseline.FilePath, _settingsBaseline.Revision, _policyBaseline.Revision);

    public string ProjectRoot { get => _settingsDraft.ProjectRoot; set => EditSettings(_settingsDraft with { ProjectRoot = value ?? "" }); }
    public string PortText
    {
        get => _portText;
        set
        {
            if (_portText == value) return;
            _portText = value ?? "";
            _settingsEditVersion++;
            Changed();
        }
    }
    public string BashPath { get => _settingsDraft.BashPath; set => EditSettings(_settingsDraft with { BashPath = value ?? "" }); }
    public string RgPath { get => _settingsDraft.RgPath; set => EditSettings(_settingsDraft with { RgPath = value ?? "" }); }
    public string GitPath { get => _settingsDraft.GitPath; set => EditSettings(_settingsDraft with { GitPath = value ?? "" }); }
    public int AuthenticationIndex
    {
        get => (int)_settingsDraft.Authentication.Mode;
        set
        {
            if (value is < 0 or > 1) return;
            var mode = (AuthenticationMode)value;
            var next = _settingsDraft with { Authentication = _settingsDraft.Authentication with { Mode = mode } };
            if (mode == AuthenticationMode.Local)
                next = next with { Tunnel = next.Tunnel with { Enabled = false } };
            EditSettings(next);
            Raise(nameof(IsCloudflareAuthentication));
            Raise(nameof(TunnelEnabled));
        }
    }
    public bool IsCloudflareAuthentication => _settingsDraft.Authentication.Mode == AuthenticationMode.CloudflareAccess;
    public string PublicOrigin
    {
        get => _settingsDraft.Authentication.PublicOrigin ?? "";
        set => EditSettings(_settingsDraft with { Authentication = _settingsDraft.Authentication with { PublicOrigin = value } });
    }
    public string Team
    {
        get => _settingsDraft.Authentication.Team ?? "";
        set => EditSettings(_settingsDraft with { Authentication = _settingsDraft.Authentication with { Team = value } });
    }
    public string Audience
    {
        get => _settingsDraft.Authentication.Audience ?? "";
        set => EditSettings(_settingsDraft with { Authentication = _settingsDraft.Authentication with { Audience = value } });
    }
    public bool TunnelEnabled
    {
        get => _settingsDraft.Tunnel.Enabled;
        set => EditSettings(_settingsDraft with { Tunnel = _settingsDraft.Tunnel with { Enabled = value } });
    }
    public string CloudflaredPath
    {
        get => _settingsDraft.Tunnel.ExecutablePath;
        set => EditSettings(_settingsDraft with { Tunnel = _settingsDraft.Tunnel with { ExecutablePath = value ?? "" } });
    }
    public string TokenFilePath
    {
        get => _settingsDraft.Tunnel.TokenFilePath;
        set => EditSettings(_settingsDraft with { Tunnel = _settingsDraft.Tunnel with { TokenFilePath = value ?? "" } });
    }
    public int ModeIndex
    {
        get => (int)_policyDraft.Mode;
        set
        {
            if (value is < 0 or > 2) return;
            EditPolicy(_policyDraft with { Mode = (ExecutionMode)value });
            Raise(nameof(ModeDescription));
        }
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

    public void SetSessionBusy(bool value)
    {
        _sessionBusy = value;
        RaiseState();
    }

    public void SetInteractionBusy(bool value)
    {
        _interactionBusy = value;
        RaiseState();
    }

    public void SetRuntime(RuntimeSnapshot snapshot, PreparedStartup? applied = null)
    {
        _runtime = snapshot;
        if (applied is not null && snapshot.Configuration == applied.Configuration)
        {
            _appliedStamp = applied.Stamp;
            _appliedRunId = snapshot.RunId;
            _activeDependencies = applied.Dependencies;
        }
        Raise(nameof(ActiveMode));
        Raise(nameof(NeedsRestart));
        Raise(nameof(ActiveDependencies));
    }
    public Task CancelPendingAsync() => _cancelling = _pendingCancellation?.CancelAsync() ?? Task.CompletedTask;

    private void EditSettings(AppSettings value, [CallerMemberName] string property = "")
    {
        if (_settingsDraft == value) return;
        _settingsDraft = value;
        _settingsEditVersion++;
        Changed(property);
    }
    private void EditPolicy(ExecutionPolicy value, [CallerMemberName] string property = "")
    {
        if (_policyDraft == value) return;
        _policyDraft = value;
        _policyEditVersion++;
        Changed(property);
    }
    private void EditLimit(ref string field, string value, [CallerMemberName] string property = "")
    {
        if (field == value) return;
        field = value ?? "";
        _policyEditVersion++;
        Changed(property);
    }

    private void Changed([CallerMemberName] string property = "")
    {
        Raise(property);
        RaiseState();
    }

    private void RaiseState()
    {
        string[] properties =
        [
            nameof(IsLoaded), nameof(HasSavedSettings), nameof(IsBusy), nameof(CanLoad), nameof(CanEdit),
            nameof(CanSaveSettings), nameof(CanSavePolicy), nameof(IsSettingsDirty), nameof(IsPolicyDirty), nameof(HasUnsavedChanges),
            nameof(DraftStatus), nameof(SavedMode), nameof(NeedsRestart), nameof(HasError), nameof(ErrorMessage), nameof(Message), nameof(Dependencies)
        ];
        foreach (var property in properties) Raise(property);
    }
    private void Raise([CallerMemberName] string property = "") => PropertyChanged?.Invoke(this, new(property));
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static int Integer(string text, string field)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ConfigurationValidationException(field, "请输入范围内的整数。");
        return value;
    }
    private AppSettings BuildSettings() => _settingsDraft with { Port = Integer(_portText, "port") };
    private ExecutionPolicy BuildPolicy() => _policyDraft with
    {
        ProjectEditLimits = new()
        {
            MaxPaths = Integer(_maxPathsText, "projectEditLimits.maxPaths"),
            MaxChangedLines = Integer(_maxLinesText, "projectEditLimits.maxChangedLines"),
            MaxChangedBytes = Integer(_maxBytesText, "projectEditLimits.maxChangedBytes")
        }
    };

    private void ResetSettings(AppSettings value)
    {
        _settingsDraft = value;
        _portText = Number(value.Port);
        Raise("");
    }

    private void ResetPolicy(ExecutionPolicy value)
    {
        _policyDraft = value;
        _maxPathsText = Number(value.ProjectEditLimits.MaxPaths);
        _maxLinesText = Number(value.ProjectEditLimits.MaxChangedLines);
        _maxBytesText = Number(value.ProjectEditLimits.MaxChangedBytes);
        Raise("");
    }
}

public sealed record SavedConfigurationStamp(string SettingsPath, string? SettingsRevision, string? PolicyRevision);
public sealed record PreparedStartup(RuntimeConfiguration Configuration, SavedConfigurationStamp Stamp, string Dependencies);
