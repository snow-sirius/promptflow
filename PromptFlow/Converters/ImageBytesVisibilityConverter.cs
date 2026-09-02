using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PromptFlow.Converters;

public sealed class ImageBytesVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is byte[] bytes && bytes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
