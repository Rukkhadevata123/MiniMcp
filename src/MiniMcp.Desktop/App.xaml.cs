using Microsoft.UI.Xaml;
using MiniMcp.Runtime;

namespace MiniMcp.Desktop;

public partial class App : Application
{
    private Window? _window;
    internal McpRuntime Runtime { get; } = new(8000);

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
