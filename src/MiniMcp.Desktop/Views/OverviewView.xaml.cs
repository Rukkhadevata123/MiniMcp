using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MiniMcp.Desktop.Views;

public sealed partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();
    public event EventHandler<string>? ActionRequested;
    private void OnStart(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "start");
    private void OnStop(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "stop");
    private void OnRestart(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "restart");
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
        BusyIndicator.IsActive = busy; BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot.Configuration is { } configuration)
        {
            ProjectText.Text = configuration.Settings.ProjectRoot;
            ConfigurationText.Text = $"本次实际配置：端口 {configuration.Settings.Port} · 认证 {configuration.Settings.Authentication.Mode} · 模式 {configuration.Policy.Mode}";
        }
        AddressText.Text = snapshot.Address is { } address
            ? (snapshot.State == RuntimeState.Running ? "" : "上次绑定地址（需清理）：\n") + $"MCP：{new Uri(address, "mcp")}\n健康检查：{new Uri(address, "healthz")}"
            : "服务没有监听地址";
    }
}
