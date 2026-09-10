using PromptFlow.Services;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PromptFlow.Tests;

public sealed class PngImageCodecTests
{
    [Fact]
    public void NormalizePreservesUnchangedPayloadAndPartialAlpha()
    {
        var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 10, 20, 30, 0, 40, 50, 60, 128 }, 8);
        var png = PngImageCodec.Encode(source, out var repaired);
        var normalized = PngImageCodec.Normalize(png, out var normalizedRepair);
        var decoded = PngImageCodec.Decode(normalized, out _);
        var output = new byte[8];
        decoded.CopyPixels(output, 8, 0);
        Assert.False(repaired);
        Assert.False(normalizedRepair);
        Assert.Same(png, normalized);
        Assert.True(decoded.IsFrozen);
        Assert.Equal((byte)0, output[3]);
        Assert.Equal((byte)128, output[7]);
    }

    [Theory]
    [InlineData(2048, 1024, 100, 80, 100, 50)]
    [InlineData(1024, 2048, 100, 80, 40, 80)]
    [InlineData(2, 1, 100, 80, 2, 1)]
    public void ThumbnailIsBoundedFrozenAndPreservesAspect(int width, int height, int maxWidth, int maxHeight, int expectedWidth, int expectedHeight)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null,
            new byte[width * height * 3], width * 3);
        var png = PngImageCodec.Encode(source, out _);
        var thumbnail = PngImageCodec.DecodeThumbnail(png, maxWidth, maxHeight, out var repaired);
        Assert.Equal(expectedWidth, thumbnail.PixelWidth);
        Assert.Equal(expectedHeight, thumbnail.PixelHeight);
        Assert.True(thumbnail.IsFrozen);
        Assert.False(repaired);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void ThumbnailRejectsInvalidBounds(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PngImageCodec.DecodeThumbnail([], width, height, out _));

    [Fact]
    public void ThumbnailRejectsMalformedHeader() =>
        Assert.Throws<System.IO.InvalidDataException>(() => PngImageCodec.DecodeThumbnail(new byte[33], 10, 10, out _));

    [Fact]
    public void DecodeRepairsLegacyPngAndFreezesOutput()
    {
        var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 10, 20, 30, 0 }, 4);
        using var stream = new System.IO.MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(stream);
        var normalized = PngImageCodec.Normalize(stream.ToArray(), out var repaired);
        var decoded = PngImageCodec.Decode(normalized, out var repairedAgain);
        var pixels = new byte[4];
        decoded.CopyPixels(pixels, 4, 0);
        Assert.True(repaired);
        Assert.False(repairedAgain);
        Assert.True(decoded.IsFrozen);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, pixels);
    }

    [Fact]
    public void EncodeRepairsAllZeroAlphaWhenRgbContainsClipboardPixels()
    {
        var pixels = new byte[]
        {
            10, 20, 30, 0,
            40, 50, 60, 0
        };
        var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);

        var png = PngImageCodec.Encode(source, out var repaired);
        var decoded = PngImageCodec.Decode(png, out var repairedOnDecode);
        var output = new byte[8];
        decoded.CopyPixels(output, 8, 0);

        Assert.True(repaired);
        Assert.False(repairedOnDecode);
        Assert.Equal((byte)255, output[3]);
        Assert.Equal((byte)255, output[7]);
        Assert.Equal((byte)30, output[2]);
        Assert.Equal((byte)60, output[6]);
    }
}
