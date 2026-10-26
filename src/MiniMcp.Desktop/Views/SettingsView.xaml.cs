using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MiniMcp.Desktop.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    public event EventHandler<string>? ActionRequested;
    private void Request(string action) => ActionRequested?.Invoke(this, action);
    private void OnReload(object sender, RoutedEventArgs args) => Request("load");
    private void OnChooseFile(object sender, RoutedEventArgs args) => Request("choose-file");
    private void OnDefaultPath(object sender, RoutedEventArgs args) => Request("default-path");
    private void OnChooseProject(object sender, RoutedEventArgs args) => Request("choose-project");
    private void OnSaveSettings(object sender, RoutedEventArgs args) => Request("save-settings");
    private void OnSavePolicy(object sender, RoutedEventArgs args) => Request("save-policy");
    private void OnCheck(object sender, RoutedEventArgs args) => Request("check");
}
