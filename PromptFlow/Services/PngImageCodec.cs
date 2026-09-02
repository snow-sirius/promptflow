using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PromptFlow.Services;

public static class PngImageCodec
{
    public static BitmapSource Decode(byte[] png, out bool repairedTransparentAlpha)
    {
        using var stream = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault() ?? throw new InvalidDataException("PNG does not contain an image frame.");
        return RepairAllTransparentAlpha(frame, out repairedTransparentAlpha);
    }

    public static byte[] Encode(BitmapSource source, out bool repairedTransparentAlpha)
    {
        var normalized = RepairAllTransparentAlpha(source, out repairedTransparentAlpha);
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(normalized));
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static byte[] Normalize(byte[] png, out bool repairedTransparentAlpha)
    {
        var image = Decode(png, out var repairedOnDecode);
        var bytes = Encode(image, out var repairedOnEncode);
        repairedTransparentAlpha = repairedOnDecode || repairedOnEncode;
        return bytes;
    }

    private static BitmapSource RepairAllTransparentAlpha(BitmapSource source, out bool repairedTransparentAlpha)
    {
        repairedTransparentAlpha = false;
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0) return source;

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0) return source;
        }

        // CF_DIB/CF_DIBV5 producers often leave the alpha channel zero even
        // when RGB contains a fully visible screenshot. Treat that case as opaque.
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = byte.MaxValue;
        var repaired = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            converted.DpiX,
            converted.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        repaired.Freeze();
        repairedTransparentAlpha = true;
        return repaired;
    }
}
