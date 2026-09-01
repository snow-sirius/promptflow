using System.Globalization;
using System.Windows.Data;

namespace PromptFlow.Converters;

public sealed class BoolToLockConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "锁" : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
