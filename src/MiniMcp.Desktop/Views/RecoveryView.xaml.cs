using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MiniMcp.Desktop.ViewModels;

namespace MiniMcp.Desktop.Views;

public sealed partial class RecoveryView : Page
{
    public RecoveryView() => InitializeComponent();
    public event EventHandler<string>? ActionRequested;
    public DesktopRecoveryRow? Selection => RecordList.SelectedItem as DesktopRecoveryRow;
    // The native multiline editor normalizes display line endings. Original file line endings
    // are already escaped inside the review diff's quoted lines and remain compared verbatim.
    public bool DisplaysPreview(string diff) => DisplayLines(DiffText.Text) == DisplayLines(diff);
    private static string DisplayLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
    public void SetActionsEnabled(bool enabled) { ActionsHost.IsEnabled = enabled; RecordList.IsEnabled = enabled; }
    private void OnReload(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "reload");
    private void OnRestore(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "restore");
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    { if (RecordList.SelectedItem is not null) ActionRequested?.Invoke(this, "select"); }
    private void OnBodySizeChanged(object sender, SizeChangedEventArgs args)
    {
        var wide = args.NewSize.Width >= 860;
        Body.ColumnSpacing = wide ? 16 : 0; Body.RowSpacing = wide ? 0 : 16;
        Body.ColumnDefinitions[0].Width = wide ? new GridLength(300) : new GridLength(1, GridUnitType.Star);
        Body.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Body.RowDefinitions[0].Height = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(140);
        Body.RowDefinitions[1].Height = wide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(DetailCard, wide ? 1 : 0); Grid.SetRow(DetailCard, wide ? 0 : 1);
    }
}
