using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
using MiniMcp.Desktop.ViewModels;
using MiniMcp.Desktop.Views;
using MiniMcp.Diagnostics;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.ViewManagement;

namespace MiniMcp.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly McpRuntime _runtime;
    private readonly SettingsEditor _editor;
    private readonly RuntimeMonitor _monitor;
    private readonly UISettings _uiSettings = new();
    private OverviewView? _overview;
    private SettingsView? _settings;
    private LogsView? _logs;
    private Task _operation = Task.CompletedTask;
    private CancellationTokenSource? _starting;
    private Task _cancellingStart = Task.CompletedTask;
    private CancellationTokenSource? _exporting;
    private Task _cancellingExport = Task.CompletedTask;
    private ContentDialog? _dialog;
    private bool _busy, _uiAction, _closing;
    private string? _status;

    public MainWindow() : this(((App)Application.Current).Runtime, ((App)Application.Current).Settings) { }

    public MainWindow(McpRuntime runtime, SettingsEditor editor)
    {
        _runtime = runtime; _editor = editor;
        InitializeComponent();
        var queue = DispatcherQueue;
        _monitor = new(_runtime, action => queue.TryEnqueue(DispatcherQueuePriority.Normal, () => action()),
            action => queue.TryEnqueue(DispatcherQueuePriority.Low, () => action()));
        _monitor.StateChanged += OnRuntimeState;
        Navigation.DataContext = _editor;
        _editor.PropertyChanged += OnEditorChanged;
        Navigation.SelectedItem = OverviewNavigation;
        AppWindow.Closing += OnClosing;
        _editor.SetRuntime(_runtime.Snapshot);
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnRootLoaded;
        var scale = Root.XamlRoot.RasterizationScale;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var area = display.WorkArea; var margin = (int)Math.Round(16 * scale);
        var width = Math.Min((int)Math.Round(1040 * scale), Math.Max(1, area.Width - 2 * margin));
        var height = Math.Min((int)Math.Round(780 * scale), Math.Max(1, area.Height - 2 * margin));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height), display);
        _operation = _editor.LoadAsync(_editor.DefaultSettingsPath);
        await _operation;
        if (!_closing && (!_editor.HasSavedSettings || _editor.HasError)) Navigation.SelectedItem = SettingsNavigation;
        UpdateControls();
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (PageFrame is null) return;
        var page = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var type = page switch { "overview" => typeof(OverviewView), "settings" => typeof(SettingsView), "logs" => typeof(LogsView), _ => null };
        if (type is null || PageFrame.CurrentSourcePageType == type) return;
        NavigationTransitionInfo transition = _uiSettings.AnimationsEnabled ? new EntranceNavigationTransitionInfo() : new SuppressNavigationTransitionInfo();
        PageFrame.Navigate(type, null, transition);
        if (sender.DisplayMode != NavigationViewDisplayMode.Expanded) sender.IsPaneOpen = false;
    }

    private void OnPageNavigated(object sender, NavigationEventArgs args)
    {
        // Required page caching preserves editor controls, list selection and scroll position;
        // the window retains its existing Runtime/monitor subscriptions across every navigation.
        switch (args.Content)
        {
            case OverviewView view:
                if (!ReferenceEquals(_overview, view)) { _overview = view; view.DataContext = _editor; view.ActionRequested += OnLifecycleAction; }
                break;
            case SettingsView view:
                if (!ReferenceEquals(_settings, view)) { _settings = view; view.DataContext = _editor; view.ActionRequested += OnSettingsAction; }
                break;
            case LogsView view:
                if (!ReferenceEquals(_logs, view)) { _logs = view; view.Attach(_monitor); view.ActionRequested += OnLogAction; }
                break;
        }
        if (args.Content is FrameworkElement content) content.RequestedTheme = Root.ActualTheme;
        UpdateControls();
    }
    private void OnRootThemeChanged(FrameworkElement sender, object args)
    {
        // Cached pages are disconnected while inactive and otherwise fall back to App's theme.
        // Keep their template resources consistent with the same workspace theme when reattached.
        foreach (var page in new Page?[] { _overview, _settings, _logs })
            if (page is not null) page.RequestedTheme = sender.ActualTheme;
    }
    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var gutter = args.NewSize.Width >= 1000 ? 32 : args.NewSize.Width >= 720 ? 24 : 16;
        WorkspaceColumn.Margin = new Thickness(gutter, 20, gutter, 24);
    }

    private async void OnSettingsAction(object? sender, string action)
    {
        if (_busy || _uiAction || _closing || _editor.IsBusy) return;
        _uiAction = true; _editor.SetInteractionBusy(true); UpdateControls();
        _operation = HandleSettingsActionAsync(action);
        await _operation;
    }

    private async Task HandleSettingsActionAsync(string action)
    {
        try { await SettingsActionAsync(action); }
        catch (Exception error) { ShowError("设置操作失败", error); }
        finally { _uiAction = false; _editor.SetInteractionBusy(false); UpdateControls(); }
    }

    private async Task SettingsActionAsync(string action)
    {
        switch (action)
        {
            case "choose-file":
                var picker = new FileOpenPicker(AppWindow.Id) { CommitButtonText = "选择配置" };
                var file = await picker.PickSingleFileAsync();
                if (file is null || _closing) return;
                if (!await ConfirmDiscardAsync()) return;
                _editor.RequestedPath = file.Path;
                await _editor.LoadAsync(file.Path);
                break;
            case "choose-project":
                var folder = await new FolderPicker(AppWindow.Id) { CommitButtonText = "选择项目目录" }.PickSingleFolderAsync();
                if (folder is not null && !_closing) _editor.ProjectRoot = folder.Path;
                break;
            case "default-path":
                if (!await ConfirmDiscardAsync()) return;
                _editor.RequestedPath = _editor.DefaultSettingsPath;
                await _editor.LoadAsync(_editor.RequestedPath);
                break;
            case "load":
                if (await ConfirmDiscardAsync()) await _editor.LoadAsync(_editor.RequestedPath);
                break;
            case "save-settings": await _editor.SaveSettingsAsync(); break;
            case "save-policy": await _editor.SavePolicyAsync(); break;
            case "check": await _editor.CheckDraftAsync(); break;
        }
    }

    private async Task<bool> ConfirmDiscardAsync(string title = "放弃未保存的草稿？")
    {
        if (!_editor.HasUnsavedChanges) return !_closing;
        _dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = title,
            Content = "运行配置或本机策略有未保存的修改。继续将放弃这些修改。",
            PrimaryButtonText = "放弃草稿并继续", CloseButtonText = "保留草稿", DefaultButton = ContentDialogButton.Close
        };
        try { return await _dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { _dialog = null; }
    }

    private async void OnLifecycleAction(object? sender, string action)
    {
        if (_busy || _uiAction || _closing || _editor.IsBusy) return;
        _busy = true; _status = action == "stop" ? "正在停止…" : "正在读取已保存配置…";
        _editor.SetSessionBusy(true); ErrorBar.IsOpen = false; UpdateControls();
        _operation = LifecycleAsync(action);
        await _operation;
    }

    private async Task LifecycleAsync(string action)
    {
        var cancellation = action == "stop" ? null : new CancellationTokenSource();
        _starting = cancellation;
        _cancellingStart = Task.CompletedTask;
        try
        {
            if (action == "stop") await _runtime.StopAsync();
            else if (action == "tunnel-start") await _runtime.StartTunnelAsync(cancellation!.Token);
            else if (action == "tunnel-stop") await _runtime.StopTunnelAsync(cancellation!.Token);
            else
            {
                var token = cancellation!.Token;
                var saved = await _editor.ReadSavedForStartupAsync(token);
                token.ThrowIfCancellationRequested();
                _status = action == "restart" ? "正在停止旧会话并启动新会话…" : "正在启动…";
                UpdateControls();
                if (action == "restart") await _runtime.RestartAsync(saved.Configuration, token);
                else await _runtime.StartAsync(saved.Configuration, token);
                _editor.SetRuntime(_runtime.Snapshot, saved);
            }
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { _status = "启动已取消"; }
        catch (Exception error) { ShowError("服务操作失败", error); }
        finally
        {
            try { await _cancellingStart; }
            catch (Exception error) { ShowError("启动取消回调失败", error); }
            _starting = null; cancellation?.Dispose();
            _busy = false; _status = null; _editor.SetSessionBusy(false); _editor.SetRuntime(_runtime.Snapshot); UpdateControls();
        }
    }

    private void OnRuntimeState(object? sender, RuntimeSnapshot snapshot)
    {
        _editor.SetRuntime(snapshot); UpdateControls();
    }

    private async void OnLogAction(object? sender, string action)
    {
        if (_busy || _editor.IsBusy || _uiAction || _closing) return;
        _uiAction = true; _editor.SetInteractionBusy(true); UpdateControls();
        _operation = HandleLogActionAsync(action);
        await _operation;
    }
    private async Task HandleLogActionAsync(string action)
    {
        try { await LogActionAsync(action); }
        catch (OperationCanceledException) { _monitor.SetActionMessage("导出已取消。"); }
        catch (Exception error) { _monitor.SetActionMessage("日志操作失败：" + error.Message); }
        finally
        {
            try { await _cancellingExport; }
            catch (Exception error) { _monitor.SetActionMessage("导出取消回调失败：" + error.Message); }
            _exporting?.Dispose(); _exporting = null; _cancellingExport = Task.CompletedTask;
            _uiAction = false; _editor.SetInteractionBusy(false); UpdateControls();
        }
    }
    private async Task LogActionAsync(string action)
    {
        if (action == "clear") { _monitor.Clear(); return; }
        if (action == "reload") { _monitor.ReloadBuffer(); return; }
        var text = action == "copy-selected" ? _logs?.SelectionText() ?? "" : _monitor.FilteredText();
        if (text.Length == 0) { _monitor.SetActionMessage("当前范围内没有日志，请先选择条目或调整筛选条件。"); return; }
        if (action is "copy-selected" or "copy-filtered")
        {
            var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package);
            _monitor.SetActionMessage("日志已复制。"); return;
        }
        if (action == "export")
        {
            var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "minimcp-logs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
            picker.FileTypeChoices.Add("日志文本", new List<string> { ".log", ".txt" });
            var result = await picker.PickSaveFileAsync();
            if (result is null || _closing) { _monitor.SetActionMessage("已取消导出。"); return; }
            _exporting = new CancellationTokenSource(); var token = _exporting.Token;
            await Task.Run(() => LogExport.WriteAsync(result.Path, text, token));
            _monitor.SetActionMessage("筛选结果已导出：" + result.Path);
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (_closing) return;
        // The native picker owns its modal interaction; keep this window/resources alive until
        // the user returns from it. Never abandon an outstanding picker/confirmation task.
        if (_uiAction && _dialog is null && !_editor.IsBusy && _exporting is null)
        { _status = "请先完成或取消文件选择窗口，再关闭 MiniMcp。"; UpdateControls(); return; }
        _closing = true; UpdateControls();
        try
        {
            if (_dialog is not null) { _dialog.Hide(); await _operation; }
            if (_editor.HasUnsavedChanges && !await ConfirmDiscardAsync("退出并放弃未保存的草稿？"))
            { _closing = false; UpdateControls(); return; }
            _status = "正在关闭并清理服务…"; UpdateControls();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _cancellingStart = _starting?.CancelAsync() ?? Task.CompletedTask;
            _cancellingExport = _exporting?.CancelAsync() ?? Task.CompletedTask;
            var cancellingEditor = _editor.CancelPendingAsync();
            await Task.WhenAll(_operation, _editor.PendingOperation, _cancellingStart, _cancellingExport, cancellingEditor).WaitAsync(deadline.Token);
            await _runtime.DisposeAsync(deadline.Token);
            await _monitor.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            _monitor.StateChanged -= OnRuntimeState; _logs?.Detach();
            _editor.PropertyChanged -= OnEditorChanged;
            AppWindow.Closing -= OnClosing;
            Close();
        }
        catch (Exception error)
        {
            _closing = false; _status = null;
            ShowError("关闭未完成，可重试关闭", error); UpdateControls();
        }
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs args) => UpdateControls();
    private void UpdateControls()
    {
        _overview?.ShowStatus(_runtime.Snapshot, _busy || _uiAction || _closing || _editor.IsBusy,
            _editor.HasSavedSettings, _status);
        _logs?.SetActionsEnabled(!_busy && !_editor.IsBusy && !_uiAction && !_closing);
        if (_monitor.ObservationError.Length != 0 && !_closing)
        { ErrorBar.Title = "界面更新异常"; ErrorBar.Message = _monitor.ObservationError; ErrorBar.IsOpen = true; }
    }
    private void ShowError(string title, Exception error)
    {
        ErrorBar.Title = title; ErrorBar.Message = RuntimeDiagnosticText.Sanitize(error.Message); ErrorBar.IsOpen = true;
    }
}
