using Microsoft.UI.Xaml.Controls;
using MiniMcp.Desktop.ViewModels;
using MiniMcp.Desktop.Views;
using MiniMcp.Recovery;

namespace MiniMcp.Desktop;

public sealed partial class MainWindow
{
    private readonly RecoveryHistory _history = new();
    private RecoveryView? _records;
    private CancellationTokenSource? _recovering;
    private Task _cancellingRecovery = Task.CompletedTask;

    private async void OnRecoveryAction(object? sender, string action)
    {
        if (_busy || _uiAction || _closing || _editor.IsBusy) return;
        var row = _records?.Selection;
        _uiAction = true; _editor.SetInteractionBusy(true);
        _recovering = new(); _cancellingRecovery = Task.CompletedTask; UpdateControls();
        _operation = RecoveryActionAsync(action, row, _recovering);
        await _operation;
    }
    private async Task RecoveryActionAsync(string action, DesktopRecoveryRow? row, CancellationTokenSource cancellation)
    {
        try
        {
            var token = cancellation.Token;
            if (action == "reload")
            {
                LocalRecoverySession session;
                var running = _runtime.Snapshot.State == RuntimeState.Running;
                if (running) session = _runtime.OpenRecoverySession();
                else
                {
                    if (_runtime.Snapshot.HasOwnedResources) throw new InvalidOperationException("请先停止并完成服务清理，再加载记录。");
                    session = LocalRecoverySession.Open(await _editor.ReadSavedForRecoveryAsync(token));
                }
                await _history.LoadAsync(session, running, token);
            }
            else if (action == "select") await _history.SelectAsync(row, token);
            else if (action == "restore" && _history.Preparation is { } preparation)
            {
                if (_records?.DisplaysPreview(preparation.Preview.Diff!) != true)
                    throw new InvalidOperationException("完整反向预览尚未显示，请重新选择记录后查看。");
                _dialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, Title = "确认恢复所选记录？",
                    Content = $"项目：{preparation.ProjectRoot}\n记录：{preparation.Preview.ChangeId:N}\n模式：{preparation.Preview.Mode}\n影响 {preparation.Preview.Changes.Length} 项变更。\n请先查看页面中的完整反向预览。恢复前会保存完整备份，并创建新的修改记录。",
                    PrimaryButtonText = "确认恢复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
                };
                ContentDialogResult result;
                try { result = await _dialog.ShowAsync(); }
                finally { _dialog = null; }
                if (result == ContentDialogResult.Primary && !_closing)
                    await _history.RestoreAsync(preparation, token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { _history.Invalidate("记录操作已取消，请重新加载。"); }
        catch (Exception error) { _history.Invalidate("操作未完成，请重新加载记录检查文件和恢复状态。"); ShowError("记录操作失败", error); }
        finally
        {
            try { await _cancellingRecovery; }
            catch (Exception error) { ShowError("记录操作取消失败", error); }
            _recovering = null; cancellation.Dispose(); _uiAction = false; _editor.SetInteractionBusy(false); UpdateControls();
        }
    }
    private async Task ReleaseHistoryAsync(string message)
    { _history.Invalidate(message); await _history.DisposeAsync(); }
}
