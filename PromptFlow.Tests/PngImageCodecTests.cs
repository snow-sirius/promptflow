using PromptFlow.Services;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PromptFlow.Tests;

public sealed class PngImageCodecTests
{
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
