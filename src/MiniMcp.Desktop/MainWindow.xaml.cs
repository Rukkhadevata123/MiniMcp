using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MiniMcp.Runtime;
using Windows.Graphics;

namespace MiniMcp.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly McpRuntime _runtime;
    private Task _operation = Task.CompletedTask;
    private CancellationTokenSource? _starting;
    private bool _busy;
    private bool _running;
    private bool _mayOwnHost;
    private bool _closing;

    public MainWindow()
    {
        _runtime = ((App)Application.Current).Runtime;
        InitializeComponent();
        AppWindow.Closing += OnClosing;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnRootLoaded;
        var scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new SizeInt32((int)Math.Round(760 * scale), (int)Math.Round(480 * scale)));
    }

    private async void OnStartClicked(object sender, RoutedEventArgs args) => await BeginOperationAsync(start: true);
    private async void OnStopClicked(object sender, RoutedEventArgs args) => await BeginOperationAsync(start: false);

    private Task BeginOperationAsync(bool start)
    {
        if (_busy || _closing) return Task.CompletedTask;
        _busy = true;
        ErrorBar.IsOpen = false;
        StatusText.Text = start ? "正在启动…" : "正在停止…";
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
                _mayOwnHost = true;
                await _runtime.StartAsync(source!.Token);
                _running = true;
            }
            else
            {
                await _runtime.StopAsync();
                _running = false;
                _mayOwnHost = false;
            }
            if (!_closing) StatusText.Text = _running ? "运行中" : "已停止";
        }
        catch (OperationCanceledException) when (source?.IsCancellationRequested == true)
        {
            _running = _runtime.Address is not null;
            if (!_closing) StatusText.Text = "启动已取消";
        }
        catch (Exception exception)
        {
            _running = _runtime.Address is not null;
            if (!_closing) ShowError(start ? "启动失败" : "停止失败", exception);
        }
        finally
        {
            _starting = null;
            source?.Dispose();
            _busy = false;
            AddressText.Text = _runtime.Address is { } address
                ? new Uri(address, "healthz").AbsoluteUri
                : "启动后显示本机地址";
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
            _running = _runtime.Address is not null;
            ShowError("无法关闭服务", exception);
            UpdateControls();
        }
    }

    private void ShowError(string title, Exception exception)
    {
        StatusText.Text = title;
        ErrorBar.Title = title;
        ErrorBar.Message = exception.GetBaseException().Message;
        ErrorBar.IsOpen = true;
    }

    private void UpdateControls()
    {
        var busy = _busy || _closing;
        StartButton.IsEnabled = !busy && !_running;
        StopButton.IsEnabled = !busy && _mayOwnHost;
        BusyIndicator.IsActive = busy;
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}
