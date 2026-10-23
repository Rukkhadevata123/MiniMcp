using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MiniMcp.Configuration;
using MiniMcp.Runtime;
using Windows.Graphics;

namespace MiniMcp.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly McpRuntime _runtime;
    private Task _operation = Task.CompletedTask;
    private CancellationTokenSource? _starting;
    private bool _busy;
    private bool _closing;

    public MainWindow()
    {
        _runtime = ((App)Application.Current).Runtime;
        InitializeComponent();
        AppWindow.Closing += OnClosing;
        try
        {
            var paths = ConfigurationPaths.ForCurrentUser();
            ConfigPathText.Text = paths.SettingsFile;
            PolicyPathText.Text = paths.PolicyFile;
        }
        catch (Exception exception) { ShowError("无法定位配置", exception); }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnRootLoaded;
        var scale = Root.XamlRoot.RasterizationScale;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var area = display.WorkArea;
        var margin = (int)Math.Round(16 * scale);
        var width = Math.Min((int)Math.Round(800 * scale), Math.Max(1, area.Width - 2 * margin));
        var height = Math.Min((int)Math.Round(700 * scale), Math.Max(1, area.Height - 2 * margin));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height), display);
    }

    private async void OnStartClicked(object sender, RoutedEventArgs args) => await BeginOperationAsync(start: true);
    private async void OnStopClicked(object sender, RoutedEventArgs args) => await BeginOperationAsync(start: false);

    private Task BeginOperationAsync(bool start)
    {
        if (_busy || _closing) return Task.CompletedTask;
        _busy = true;
        ErrorBar.IsOpen = false;
        StatusText.Text = start ? "正在读取配置…" : "正在停止…";
        UpdateControls();
        _operation = ExecuteAsync(start);
        return _operation;
    }

    private async Task ExecuteAsync(bool start)
    {
        var source = start ? new CancellationTokenSource() : null;
        _starting = source;
        try
        {
            if (start)
            {
                var token = source!.Token;
                // File metadata and dependency checks also run off the UI thread.
                var configuration = await Task.Run(() => new ConfigurationStore().LoadAsync(
                    path: null, workingDirectory: AppContext.BaseDirectory, cancellationToken: token), token);
                token.ThrowIfCancellationRequested();
                if (!_closing) StatusText.Text = "正在启动…";
                await _runtime.StartAsync(configuration, token);
            }
            else
            {
                await _runtime.StopAsync();
            }
            if (!_closing) StatusText.Text = StateText(_runtime.Snapshot.State);
        }
        catch (OperationCanceledException) when (source?.IsCancellationRequested == true)
        {
            if (!_closing) StatusText.Text = "启动已取消";
        }
        catch (Exception exception)
        {
            if (!_closing) ShowError(start ? "启动失败" : "停止失败", exception);
        }
        finally
        {
            _starting = null;
            source?.Dispose();
            _busy = false;
            UpdateSnapshot();
            UpdateControls();
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        StatusText.Text = "正在关闭…";
        ErrorBar.IsOpen = false;
        UpdateControls();
        try
        {
            _starting?.Cancel();
            await _operation;
            await _runtime.DisposeAsync();
            AppWindow.Closing -= OnClosing;
            Close();
        }
        catch (Exception exception)
        {
            _closing = false;
            ShowError("无法关闭服务", exception);
            UpdateSnapshot();
            UpdateControls();
        }
    }

    private void ShowError(string title, Exception exception)
    {
        StatusText.Text = title;
        ErrorBar.Title = title;
        ErrorBar.Message = exception.Message;
        ErrorBar.IsOpen = true;
    }

    private void UpdateControls()
    {
        var busy = _busy || _closing;
        var state = _runtime.Snapshot.State;
        StartButton.IsEnabled = !busy && state is RuntimeState.Stopped or RuntimeState.Faulted;
        StopButton.IsEnabled = !busy && state is RuntimeState.Running or RuntimeState.Faulted;
        BusyIndicator.IsActive = busy;
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSnapshot()
    {
        var snapshot = _runtime.Snapshot;
        if (snapshot.Configuration is { } configuration)
        {
            ProjectText.Text = configuration.Settings.ProjectRoot;
            ConfigurationText.Text = $"端口 {configuration.Settings.Port} · 认证配置 {configuration.Settings.Authentication.Mode} · 执行配置 {configuration.Policy.Mode}";
        }
        AddressText.Text = snapshot.Address is { } address
            ? (snapshot.State == RuntimeState.Running ? "" : "上次绑定地址（需清理）：") + new Uri(address, "healthz").AbsoluteUri
            : "服务没有监听地址";
    }

    private static string StateText(RuntimeState state) => state switch
    {
        RuntimeState.Stopped => "已停止",
        RuntimeState.Starting => "正在启动…",
        RuntimeState.Running => "运行中",
        RuntimeState.Stopping => "正在停止…",
        RuntimeState.Faulted => "服务故障",
        RuntimeState.Disposed => "已释放",
        _ => "状态未知"
    };
}
