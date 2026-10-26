using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MiniMcp.Recovery;

namespace MiniMcp.Desktop.ViewModels;

public sealed class DesktopRecoveryRow
{
    internal DesktopRecoveryRow(RecoveryRecordSummary summary) => Summary = summary;
    internal RecoveryRecordSummary Summary { get; }
    public Guid ChangeId => Summary.ChangeId;
    public string Name => Summary.Name;
    public string CreatedText => Summary.CreatedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "时间未知";
    public string State => Summary.State;
    public string? Error => Summary.Error;
}

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class RecoveryHistory : INotifyPropertyChanged, IAsyncDisposable
{
    private LocalRecoverySession? _session;
    private LocalRecoveryPreparation? _preparation;
    private int _generation;
    private string _context = "加载已保存配置中的修改记录。", _metadata = "", _diff = "", _message = "请选择记录查看详情与完整反向预览。";
    public ObservableCollection<DesktopRecoveryRow> Records { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Context => _context;
    public string Metadata => _metadata;
    public string Diff => _diff;
    public string Message => _message;
    public bool CanRestore => _preparation is not null;
    public bool UsesRuntime { get; private set; }
    public Guid? RunId => _session?.RunId;
    public LocalRecoveryPreparation? Preparation => _preparation;

    public async Task LoadAsync(LocalRecoverySession session, bool usesRuntime, CancellationToken token)
    {
        await DisposeAsync();
        _session = session; UsesRuntime = usesRuntime;
        var generation = _generation;
        Records.Clear(); _metadata = _diff = "";
        _context = $"项目：{session.Configuration.Settings.ProjectRoot}\n模式：{session.Configuration.Policy.Mode} · {(usesRuntime ? "运行中服务" : "本地维护")}";
        _message = "正在加载记录…"; RaiseAll();
        var rows = await session.ListAsync(token);
        if (generation != _generation || !ReferenceEquals(session, _session)) return;
        foreach (var row in rows) Records.Add(new(row));
        _message = rows.IsEmpty ? "当前项目还没有修改记录。" : $"共 {rows.Length} 条记录。请选择一条查看完整预览。"; RaiseAll();
    }
    public async Task SelectAsync(DesktopRecoveryRow? selected, CancellationToken token)
    {
        var row = selected?.Summary;
        var generation = ++_generation;
        _preparation = null; _metadata = _diff = ""; _message = ""; RaiseAll();
        if (row is null) return;
        if (row.Error is not null) { _message = row.Error; RaiseAll(); return; }
        var session = _session ?? throw new InvalidOperationException("请先加载记录。");
        var details = await session.ReadAsync(row.ChangeId, token);
        if (generation != _generation || !ReferenceEquals(session, _session)) return;
        if (details.Preview.Blockers.IsEmpty && details.Preview.Diff is not null)
        {
            var prepared = await session.PrepareAsync(row.ChangeId, token);
            if (generation != _generation || !ReferenceEquals(session, _session)) return;
            _preparation = prepared; details = prepared.Record;
        }
        _metadata = details.ManifestJson;
        _diff = details.Preview.Diff ?? "当前无法生成完整反向预览。";
        _message = string.Join(Environment.NewLine, details.Preview.Blockers.Select(Blocker)
            .Concat(details.Preview.Conflicts.Select(conflict => $"冲突：{conflict.Path ?? "记录"} · {conflict.Message}")));
        if (_preparation is not null) _message = "查看完整反向预览后，可确认恢复。恢复会创建新的备份记录。";
        RaiseAll();
    }
    public async Task RestoreAsync(LocalRecoveryPreparation expected, CancellationToken token)
    {
        var session = _session ?? throw new InvalidOperationException("请先加载记录。");
        if (!ReferenceEquals(expected, _preparation)) throw new InvalidOperationException("预览已改变，请重新选择记录。");
        _preparation = null; RaiseAll();
        var result = await session.RestoreAsync(expected, token);
        Records.Insert(0, new(new(result.ChangeId, result.ChangeId.ToString("N"), result.RunId, result.CreatedAtUtc, result.State, result.PathCount, null)));
        _message = $"恢复结果：{result.State} · 新记录：{result.ChangeId:N}" +
            (result.CleanupIncomplete ? "\n临时文件清理未完成，请检查记录和错误信息。" : "") +
            (result.Errors.IsEmpty ? "" : "\n" + string.Join(Environment.NewLine, result.Errors));
        _diff = ""; _metadata = ""; RaiseAll();
    }
    public void Invalidate(string message)
    { _generation++; _preparation = null; _metadata = _diff = ""; _message = message; RaiseAll(); }
    private static string Blocker(string reason) => reason switch
    {
        "ReadOnlyMode" => "ReadOnly 模式可查看完整预览。恢复需使用 ProjectEdit 或 FullAccess 会话。",
        "RecordNotCommitted" => "恢复适用于 Committed 记录，请结合记录详情检查实际文件。",
        "Conflicts" => "文件或记录已发生变化，请先处理以下冲突。",
        _ => "恢复限制：" + reason
    };
    private void RaiseAll()
    { foreach (var name in new[] { nameof(Context), nameof(Metadata), nameof(Diff), nameof(Message), nameof(CanRestore) }) Raise(name); }
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public async ValueTask DisposeAsync()
    {
        _generation++; _preparation = null; var session = _session;
        if (session is not null) await session.DisposeAsync();
        _session = null;
        RaiseAll();
    }
}
