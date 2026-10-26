using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MiniMcp.Desktop.ViewModels;

namespace MiniMcp.Desktop.Views;

public sealed partial class LogsView : Page
{
    private RuntimeMonitor? _monitor;
    public LogsView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshTail();
        SizeChanged += (_, args) =>
        {
            var wide = args.NewSize.Width >= 720;
            Grid.SetColumnSpan(SearchField, wide ? 1 : 2);
            SearchField.Margin = new Thickness(0, 0, wide ? 12 : 0, 0);
            Grid.SetColumn(LevelField, wide ? 1 : 0); Grid.SetRow(LevelField, wide ? 0 : 1); Grid.SetColumnSpan(LevelField, wide ? 1 : 2);
            Grid.SetRow(FilterToggles, wide ? 1 : 2);
        };
    }
    public event EventHandler<string>? ActionRequested;
    public void Attach(RuntimeMonitor monitor)
    {
        Detach(); _monitor = monitor; DataContext = monitor; monitor.BatchApplied += OnBatch;
    }
    public void Detach() { if (_monitor is not null) _monitor.BatchApplied -= OnBatch; _monitor = null; }
    public void RefreshTail() => OnBatch(this, EventArgs.Empty);
    public void SetActionsEnabled(bool enabled) => LogCommands.IsEnabled = enabled;
    public string SelectionText() => _monitor?.SelectedText(LogList.SelectedItems.OfType<DesktopLogRow>()) ?? "";
    private void OnBatch(object? sender, EventArgs args)
    {
        if (IsLoaded && Visibility == Visibility.Visible && _monitor is { AutoScroll: true, Rows.Count: > 0 }) LogList.ScrollIntoView(_monitor.Rows[^1]);
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args) => SelectedText.Text = SelectionText();
    private void OnCopySelected(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "copy-selected");
    private void OnCopyFiltered(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "copy-filtered");
    private void OnExport(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "export");
    private void OnReload(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "reload");
    private void OnClear(object sender, RoutedEventArgs args) => ActionRequested?.Invoke(this, "clear");
}
