using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MiniMcp.Desktop.ViewModels;

namespace MiniMcp.Desktop.Controls;

public sealed class LogLevelTemplateSelector : DataTemplateSelector
{
    public DataTemplate NormalTemplate { get; set; } = null!;
    public DataTemplate WarningTemplate { get; set; } = null!;
    public DataTemplate ErrorTemplate { get; set; } = null!;
    public DataTemplate CriticalTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item) => item is DesktopLogRow row ? row.Entry.Level switch
    {
        LogLevel.Warning => WarningTemplate,
        LogLevel.Error => ErrorTemplate,
        LogLevel.Critical => CriticalTemplate,
        _ => NormalTemplate
    } : NormalTemplate;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
