using System.Globalization;
using System.Windows.Data;

namespace PromptFlow.Converters;

public sealed class BoolToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "★" : "☆";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
