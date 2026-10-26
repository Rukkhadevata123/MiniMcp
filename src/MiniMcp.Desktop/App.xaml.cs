using Microsoft.UI.Xaml;
using MiniMcp.Desktop.ViewModels;

namespace MiniMcp.Desktop;

public partial class App : Application
{
    public McpRuntime Runtime { get; }
    public SettingsEditor Settings { get; }
    public MainWindow? MainWindow { get; private set; }

    public App() : this(new McpRuntime(), new SettingsEditor()) { }

    public App(McpRuntime runtime, SettingsEditor settings)
    {
        Runtime = runtime; Settings = settings;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow(Runtime, Settings);
        MainWindow.Activate();
    }
}
