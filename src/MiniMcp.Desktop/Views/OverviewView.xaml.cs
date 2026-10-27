using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MiniMcp.Desktop.Views;

public sealed partial class OverviewView : Page
{
    public OverviewView()
    {
        InitializeComponent();
        SizeChanged += (_, args) =>
        {
            ServiceActions.Orientation = args.NewSize.Width >= 640 ? Orientation.Horizontal : Orientation.Vertical;
            TunnelActions.Orientation = args.NewSize.Width >= 480 ? Orientation.Horizontal : Orientation.Vertical;
        };
    }
    public event EventHandler<string>? ActionRequested;
    private void OnStart(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "start");
    private void OnStop(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "stop");
    private void OnRestart(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "restart");
    private void OnStartTunnel(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "tunnel-start");
    private void OnStopTunnel(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "tunnel-stop");
    public void ShowStatus(RuntimeSnapshot snapshot, bool busy, bool canStart, string? status = null)
    {
        StatusText.Text = status ?? snapshot.State switch
        {
            RuntimeState.Stopped => "已停止", RuntimeState.Starting => "正在启动…", RuntimeState.Running => "运行中",
            RuntimeState.Stopping => "正在停止…", RuntimeState.Faulted => "服务故障", RuntimeState.Disposed => "已释放", _ => "状态未知"
        };
        StartButton.IsEnabled = !busy && canStart && !snapshot.HasOwnedResources && snapshot.State is RuntimeState.Stopped or RuntimeState.Faulted;
        StopButton.IsEnabled = !busy && (snapshot.HasOwnedResources || snapshot.State is RuntimeState.Running or RuntimeState.Faulted);
        RestartButton.IsEnabled = !busy && canStart && snapshot.HasOwnedResources;
        StartTunnelButton.IsEnabled = !busy && snapshot.State == RuntimeState.Running && snapshot.Configuration?.Settings.Tunnel.Enabled == true &&
            snapshot.Tunnel.State is TunnelState.Stopped or TunnelState.Faulted;
        StopTunnelButton.IsEnabled = !busy && snapshot.State == RuntimeState.Running && snapshot.Tunnel.State is TunnelState.Running or TunnelState.Faulted;
        BusyIndicator.IsActive = busy; BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot.Configuration is { } configuration)
        {
            ProjectText.Text = configuration.Settings.ProjectRoot;
            ConfigurationText.Text = $"本次配置：端口 {configuration.Settings.Port} · 认证 {configuration.Settings.Authentication.Mode} · 模式 {configuration.Policy.Mode}";
        }
        RunText.Text = snapshot.RunId is { } id ? $"RunId：{id} · 本次故障计数：{snapshot.FaultCount}" : "尚未创建会话";
        RuntimeError.Text = snapshot.Error ?? "";
        RuntimeError.Visibility = string.IsNullOrEmpty(snapshot.Error) ? Visibility.Collapsed : Visibility.Visible;
        TunnelText.Text = $"本机隧道状态：{snapshot.Tunnel.State}" + (snapshot.Tunnel.ProcessId is { } pid ? $" · PID {pid}" : "") +
            (snapshot.Tunnel.ExitCode is { } exit ? $" · 退出码 {exit}" : "");
        TunnelError.Text = snapshot.Tunnel.Error ?? "";
        TunnelError.Visibility = string.IsNullOrEmpty(snapshot.Tunnel.Error) ? Visibility.Collapsed : Visibility.Visible;
        var origin = snapshot.Configuration?.Settings.Authentication.PublicOrigin;
        PublicAddress.Text = !string.IsNullOrWhiteSpace(origin)
            ? $"配置的公网 MCP 地址（待验证）：{origin.TrimEnd('/')}/mcp" : "未配置公网地址";
        AddressText.Text = snapshot.Address is { } address
            ? (snapshot.State == RuntimeState.Running ? "" : "上次绑定地址（需清理）：\n") + $"MCP：{new Uri(address, "mcp")}\n健康检查：{new Uri(address, "healthz")}"
            : "服务没有监听地址";
    }
}
