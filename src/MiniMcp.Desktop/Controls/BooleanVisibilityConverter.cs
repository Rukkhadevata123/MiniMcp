using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MiniMcp.Desktop.Controls;

public sealed class BooleanVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
