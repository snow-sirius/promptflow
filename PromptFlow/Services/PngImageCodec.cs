using System.Buffers.Binary;
using System.IO;
using System.Windows;
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

    public static byte[] Encode(BitmapSource source, out bool repairedTransparentAlpha) =>
        EncodeNormalized(RepairAllTransparentAlpha(source, out repairedTransparentAlpha));

    private static byte[] EncodeNormalized(BitmapSource source)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapSource DecodeThumbnail(byte[] png, int maxWidth, int maxHeight, out bool repairedTransparentAlpha)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        // Read IHDR instead of constructing an OnLoad full-resolution decoder first.
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)) != 13 || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Invalid PNG header.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            throw new InvalidDataException("Invalid PNG dimensions.");
        var scale = Math.Min(1d, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        using var stream = new MemoryStream(png, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.DecodePixelWidth = Math.Max(1, (int)(width * scale));
        image.DecodePixelHeight = Math.Max(1, (int)(height * scale));
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return RepairAllTransparentAlpha(image, out repairedTransparentAlpha);
    }

    public static byte[] Normalize(byte[] png, out bool repairedTransparentAlpha)
    {
        var image = Decode(png, out repairedTransparentAlpha);
        // Preserve the original compressed payload when no compatibility repair is needed.
        return repairedTransparentAlpha ? EncodeNormalized(image) : png;
    }

    private static BitmapSource RepairAllTransparentAlpha(BitmapSource source, out bool repairedTransparentAlpha)
    {
        repairedTransparentAlpha = false;
        if (!source.IsFrozen) source = source.GetAsFrozen() as BitmapSource ?? source;
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0) return source;
        // These common opaque formats cannot need alpha repair.
        if (source.Format == PixelFormats.Bgr24 || source.Format == PixelFormats.Bgr32 ||
            source.Format == PixelFormats.Gray8 || source.Format == PixelFormats.Gray16 ||
            source.Format == PixelFormats.BlackWhite) return source;

        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        if (!converted.IsFrozen) converted.Freeze();
        var stride = checked(converted.PixelWidth * 4);
        // Scan bounded strips, stopping as soon as real alpha is found. Allocate a
        // full pixel buffer only for the exceptional all-zero-alpha repair case.
        var rows = Math.Min(converted.PixelHeight, Math.Max(1, 65536 / stride));
        var strip = new byte[checked(stride * rows)];
        for (var y = 0; y < converted.PixelHeight; y += rows)
        {
            var count = Math.Min(rows, converted.PixelHeight - y);
            converted.CopyPixels(new Int32Rect(0, y, converted.PixelWidth, count), strip, stride, 0);
            for (var index = 3; index < stride * count; index += 4)
                if (strip[index] != 0) return source;
        }

        // Legacy CF_DIB producers leave alpha zero even for visible RGB screenshots.
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = byte.MaxValue;
        var repaired = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
            converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        repaired.Freeze();
        repairedTransparentAlpha = true;
        return repaired;
    }
}
