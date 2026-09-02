using System.Globalization;
using System.Windows.Data;
using PromptFlow.Services;

namespace PromptFlow.Converters;

public sealed class ByteArrayToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0) return null;
        try
        {
            var image = PngImageCodec.Decode(bytes, out var repairedTransparentAlpha);
            if (repairedTransparentAlpha)
                AppLog.Warn($"Repaired all-zero alpha channel for history thumbnail. Bytes={bytes.Length}; Width={image.PixelWidth}; Height={image.PixelHeight}");
            return image;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Thumbnail PNG decode failed. Bytes={bytes.Length}", ex);
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
