using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PromptFlow.Services;

namespace PromptFlow.Tests;

public sealed class ClipboardWriterTests
{
    [Fact]
    public void BuildHtmlPayloadWrapsFragmentWithByteAccurateOffsets()
    {
        const string fragment = "<b>你好 &amp; goodbye</b>";

        var payload = ClipboardWriter.BuildHtmlPayload(fragment);
        var header = Encoding.UTF8.GetString(payload);

        Assert.StartsWith("Version:0.9\r\n", header, StringComparison.Ordinal);
        var startHtml = ReadOffset(header, "StartHTML");
        var endHtml = ReadOffset(header, "EndHTML");
        var startFragment = ReadOffset(header, "StartFragment");
        var endFragment = ReadOffset(header, "EndFragment");

        // Offsets are byte offsets into the payload, not character indexes.
        Assert.Equal("<html>", Encoding.UTF8.GetString(payload, startHtml, 6));
        Assert.Equal(fragment, Encoding.UTF8.GetString(payload, startFragment, endFragment - startFragment));
        Assert.Equal("</html>", Encoding.UTF8.GetString(payload, endHtml - 7, 7));
    }

    [Fact]
    public void BuildHtmlPayloadKeepsPayloadThatAlreadyHasHeader()
    {
        const string captured =
            "Version:0.9\r\nStartHTML:0000000105\r\nEndHTML:0000000300\r\n" +
            "StartFragment:0000000141\r\nEndFragment:0000000250\r\n" +
            "<html><body><!--StartFragment--><i>x</i><!--EndFragment--></body></html>";

        var payload = ClipboardWriter.BuildHtmlPayload(captured);

        Assert.Equal(Encoding.UTF8.GetBytes(captured), payload);
    }

    [Fact]
    public void BuildDibWritesBottomUpBgraRows()
    {
        var stride = 2 * 4;
        var pixels = new byte[]
        {
            0, 0, 255, 255, 0, 255, 0, 255,          // top row: red, green
            255, 0, 0, 255, 255, 255, 255, 255       // bottom row: blue, white
        };
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

        var dib = ClipboardWriter.BuildDib(source);

        Assert.Equal(40 + pixels.Length, dib.Length);
        Assert.Equal(40, BitConverter.ToInt32(dib, 0));   // biSize
        Assert.Equal(2, BitConverter.ToInt32(dib, 4));    // biWidth
        Assert.Equal(2, BitConverter.ToInt32(dib, 8));    // biHeight
        Assert.Equal(1, BitConverter.ToInt16(dib, 12));   // biPlanes
        Assert.Equal(32, BitConverter.ToInt16(dib, 14));  // biBitCount
        Assert.Equal(0, BitConverter.ToInt32(dib, 16));   // BI_RGB
        Assert.Equal(pixels.Length, BitConverter.ToInt32(dib, 20));

        // CF_DIB rows are stored bottom-up, so the bottom row comes first.
        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 255, 255, 255 }, dib[40..48]);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, dib[48..56]);
    }

    private static int ReadOffset(string header, string name)
    {
        var match = Regex.Match(header, name + @":(\d{10})");
        Assert.True(match.Success, $"{name} is missing from the CF_HTML header");
        return int.Parse(match.Groups[1].Value);
    }
}
