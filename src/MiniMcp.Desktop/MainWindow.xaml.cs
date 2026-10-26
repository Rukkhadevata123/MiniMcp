using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using MiniMcp.Desktop.ViewModels;
using MiniMcp.Diagnostics;
using Windows.Graphics;

namespace MiniMcp.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly McpRuntime _runtime;
    private readonly SettingsEditor _editor;
    private Task _operation = Task.CompletedTask;
    private CancellationTokenSource? _starting;
    private Task _cancellingStart = Task.CompletedTask;
    private ContentDialog? _dialog;
    private bool _busy, _uiAction, _closing;
    private string? _status;

    public MainWindow() : this(((App)Application.Current).Runtime, ((App)Application.Current).Settings) { }

    public MainWindow(McpRuntime runtime, SettingsEditor editor)
    {
        _runtime = runtime; _editor = editor;
        InitializeComponent();
        Navigation.DataContext = _editor;
        _editor.PropertyChanged += OnEditorChanged;
        Navigation.SelectedItem = OverviewNavigation;
        AppWindow.Closing += OnClosing;
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
        if (Overview is null || Settings is null) return;
        var settings = (args.SelectedItem as NavigationViewItem)?.Tag as string == "settings";
        Overview.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        Settings.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        if (sender.DisplayMode != NavigationViewDisplayMode.Expanded) sender.IsPaneOpen = false;
    }

    private async void OnSettingsAction(object? sender, string action)
    {
        if (_busy || _uiAction || _closing || _editor.IsBusy) return;
        _uiAction = true; _editor.SetInteractionBusy(true); UpdateControls();
        _operation = SettingsActionAsync(action);
        try { await _operation; }
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
            Content = "当前运行配置或本机策略有未保存的草稿。继续将放弃这些草稿，已保存文件不会因此改变。",
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

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (_closing) return;
        // The native picker owns its modal interaction; keep this window/resources alive until
        // the user returns from it. Never abandon an outstanding picker/confirmation task.
        if (_uiAction && _dialog is null && !_editor.IsBusy)
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
            var cancellingEditor = _editor.CancelPendingAsync();
            await Task.WhenAll(_operation, _editor.PendingOperation, _cancellingStart, cancellingEditor).WaitAsync(deadline.Token);
            await _runtime.DisposeAsync(deadline.Token);
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
        if (Overview is null) return;
        Overview.ShowStatus(_runtime.Snapshot, _busy || _uiAction || _closing || _editor.IsBusy,
            _editor.HasSavedSettings, _status);
    }
    private void ShowError(string title, Exception error)
    {
        ErrorBar.Title = title; ErrorBar.Message = RuntimeDiagnosticText.Sanitize(error.Message); ErrorBar.IsOpen = true;
    }
}
