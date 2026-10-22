using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PromptFlow.Models;

namespace PromptFlow.Services;

internal readonly record struct ClipboardWriteOutcome(bool Success, int Attempts, string? Error, int Win32Error);

/// <summary>
/// Writes the whole clipboard payload in a single OpenClipboard session.
///
/// WPF's Clipboard.SetDataObject(data, copy: true) publishes through OLE and then
/// opens the clipboard a second time for OleFlushClipboard. Clipboard sync clients
/// (remote-control software, clipboard history) read the clipboard as soon as it
/// changes, so that second open races their read and fails with CLIPBRD_E_CANT_OPEN.
/// Writing every format between one OpenClipboard/CloseClipboard pair removes the
/// second window entirely; the sync client then reads an already complete clipboard.
/// </summary>
internal static class ClipboardWriter
{
    private const uint CfText = 1;
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly int[] RetryDelaysMs = [30, 50, 80, 120, 180, 250, 350, 450, 550];
    private static readonly object OwnerGate = new();
    private static readonly Dictionary<string, uint> FormatIds = new(StringComparer.Ordinal);
    private static HwndSource? _ownerSource;

    static ClipboardWriter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<ClipboardWriteOutcome> WriteAsync(ClipboardItem item, bool plainText, TimeSpan budget)
    {
        var formats = BuildFormats(item, plainText, out var prepareError);
        if (formats is null) return new ClipboardWriteOutcome(false, 0, prepareError, 0);

        var owner = EnsureOwnerWindow();
        if (owner == IntPtr.Zero) return new ClipboardWriteOutcome(false, 0, "无法创建剪贴板所有者窗口", 0);

        var deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        var attempts = 0;
        var lastError = 0;
        while (true)
        {
            attempts++;
            ClipboardMonitor.BeginWrite();
            try
            {
                if (TryWriteOnce(owner, formats, out lastError))
                {
                    ClipboardMonitor.RecordSelfWriteSequence();
                    AppLog.Info($"Clipboard write succeeded. ItemId={item.Id}; Formats={formats.Count}; Attempts={attempts}; PlainText={plainText}");
                    return new ClipboardWriteOutcome(true, attempts, null, 0);
                }
            }
            finally { ClipboardMonitor.EndWrite(); }

            if (Environment.TickCount64 >= deadline) break;
            await Task.Delay(RetryDelaysMs[Math.Min(attempts - 1, RetryDelaysMs.Length - 1)]);
        }

        AppLog.Warn($"Clipboard write gave up. ItemId={item.Id}; Attempts={attempts}; Win32Error={lastError}; Holder={DescribeClipboardHolder()}");
        return new ClipboardWriteOutcome(false, attempts, $"写入剪贴板失败 (Win32Error={lastError})", lastError);
    }

    /// <summary>Diagnostic: which process currently keeps the clipboard open.</summary>
    public static string DescribeClipboardHolder()
    {
        var hwnd = GetOpenClipboardWindow();
        if (hwnd == IntPtr.Zero) return "<none>";
        GetWindowThreadProcessId(hwnd, out var pid);
        try { return $"{Process.GetProcessById((int)pid).ProcessName}.exe(pid={pid})"; }
        catch { return $"pid={pid}"; }
    }

    private static bool TryWriteOnce(IntPtr owner, List<ClipboardFormatData> formats, out int win32Error)
    {
        win32Error = 0;
        if (!OpenClipboard(owner))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            // EmptyClipboard assigns ownership to the window that opened the clipboard.
            // Opening with a NULL owner would leave the owner NULL and SetClipboardData
            // would fail, which is why a real (hidden) window is used.
            if (!EmptyClipboard())
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            foreach (var format in formats)
            {
                if (!SetClipboardFormat(format.Format, format.Bytes))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return false;
                }
            }
            return true;
        }
        finally { CloseClipboard(); }
    }

    private static bool SetClipboardFormat(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return false;
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(handle); }

            if (SetClipboardData(format, handle) == IntPtr.Zero) return false;
            handle = IntPtr.Zero; // The clipboard now owns the HGLOBAL.
            return true;
        }
        finally
        {
            if (handle != IntPtr.Zero) GlobalFree(handle);
        }
    }

    private static List<ClipboardFormatData>? BuildFormats(ClipboardItem item, bool plainText, out string? error)
    {
        error = null;
        var formats = new List<ClipboardFormatData>(6);

        if (plainText)
        {
            AddText(formats, item.TextContent ?? item.DisplayText);
            return formats;
        }

        if (!string.IsNullOrEmpty(item.TextContent)) AddText(formats, item.TextContent);
        else if (item.ImagePng is null && string.IsNullOrEmpty(item.HtmlContent) && string.IsNullOrEmpty(item.RtfContent))
            AddText(formats, item.DisplayText);

        if (!string.IsNullOrEmpty(item.HtmlContent))
        {
            var id = RegisteredFormat("HTML Format");
            if (id != 0) formats.Add(new ClipboardFormatData(id, BuildHtmlPayload(item.HtmlContent)));
        }

        if (!string.IsNullOrEmpty(item.RtfContent))
        {
            var id = RegisteredFormat("Rich Text Format");
            if (id != 0) formats.Add(new ClipboardFormatData(id, Encoding.UTF8.GetBytes(item.RtfContent)));
        }

        if (item.ImagePng is { Length: > 0 } png)
        {
            try
            {
                var image = PngImageCodec.Decode(png, out var repaired);
                if (repaired) png = PngImageCodec.Encode(image, out _);
                var pngId = RegisteredFormat("PNG");
                if (pngId != 0) formats.Add(new ClipboardFormatData(pngId, png));
                var mimeId = RegisteredFormat("image/png");
                if (mimeId != 0) formats.Add(new ClipboardFormatData(mimeId, png));
                // CF_DIB is what Word and most native editors prefer; the PNG formats
                // above preserve transparency for Chromium-based targets.
                formats.Add(new ClipboardFormatData(CfDib, BuildDib(image)));
            }
            catch (Exception ex)
            {
                AppLog.Error($"Could not build the clipboard image payload. ItemId={item.Id}", ex);
                error = $"无法准备图片剪贴板数据：{ex.Message}";
                return null;
            }
        }

        if (formats.Count == 0) AddText(formats, item.DisplayText);
        return formats;
    }

    private static void AddText(List<ClipboardFormatData> formats, string text)
    {
        formats.Add(new ClipboardFormatData(CfUnicodeText, Encoding.Unicode.GetBytes(text + "\0")));
        var ansi = EncodeAnsi(text);
        if (ansi is not null) formats.Add(new ClipboardFormatData(CfText, ansi));
    }

    private static byte[]? EncodeAnsi(string text)
    {
        try
        {
            var encoding = Encoding.GetEncoding((int)GetACP(), EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
            var bytes = encoding.GetBytes(text);
            // Skip CF_TEXT when the text is not representable in the ANSI code page;
            // CF_UNICODETEXT already carries the exact content.
            if (encoding.GetString(bytes) != text) return null;
            return [.. bytes, (byte)0];
        }
        catch { return null; }
    }

    /// <summary>
    /// Builds a CF_HTML payload. Captured HTML already carries the "Version:" header,
    /// in which case the captured bytes are written verbatim. Fragments that lost the
    /// header are wrapped so that Word/browsers still accept the format.
    /// </summary>
    internal static byte[] BuildHtmlPayload(string html)
    {
        if (html.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8.GetBytes(html);

        const string headerTemplate = "Version:0.9\r\nStartHTML:{0}\r\nEndHTML:{1}\r\nStartFragment:{2}\r\nEndFragment:{3}\r\n";
        const string prefix = "<html><body>\r\n<!--StartFragment-->";
        const string suffix = "<!--EndFragment-->\r\n</body></html>";

        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var fragmentBytes = Encoding.UTF8.GetBytes(html);
        var suffixBytes = Encoding.UTF8.GetBytes(suffix);
        string Header(int startHtml, int endHtml, int startFragment, int endFragment) => string.Format(
            headerTemplate,
            startHtml.ToString("D10"),
            endHtml.ToString("D10"),
            startFragment.ToString("D10"),
            endFragment.ToString("D10"));

        // Offset fields are fixed-width, so the header length does not depend on values.
        var headerLength = Header(0, 0, 0, 0).Length;
        var startHtmlOffset = headerLength;
        var startFragmentOffset = startHtmlOffset + prefixBytes.Length;
        var endFragmentOffset = startFragmentOffset + fragmentBytes.Length;
        var endHtmlOffset = endFragmentOffset + suffixBytes.Length;
        var headerBytes = Encoding.ASCII.GetBytes(Header(startHtmlOffset, endHtmlOffset, startFragmentOffset, endFragmentOffset));

        var payload = new byte[headerBytes.Length + prefixBytes.Length + fragmentBytes.Length + suffixBytes.Length];
        var offset = 0;
        headerBytes.CopyTo(payload, offset); offset += headerBytes.Length;
        prefixBytes.CopyTo(payload, offset); offset += prefixBytes.Length;
        fragmentBytes.CopyTo(payload, offset); offset += fragmentBytes.Length;
        suffixBytes.CopyTo(payload, offset);
        return payload;
    }

    internal static byte[] BuildDib(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bgra.CopyPixels(pixels, stride, 0);

        var header = new byte[40];
        using (var stream = new MemoryStream(header))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(40);            // biSize
            writer.Write(width);         // biWidth
            writer.Write(height);        // biHeight (positive = bottom-up rows)
            writer.Write((short)1);      // biPlanes
            writer.Write((short)32);     // biBitCount
            writer.Write(0);             // biCompression = BI_RGB
            writer.Write(pixels.Length); // biSizeImage
            writer.Write(0);             // biXPelsPerMeter
            writer.Write(0);             // biYPelsPerMeter
            writer.Write(0);             // biClrUsed
            writer.Write(0);             // biClrImportant
        }

        var dib = new byte[header.Length + pixels.Length];
        header.CopyTo(dib, 0);
        var offset = header.Length;
        for (var y = height - 1; y >= 0; y--)
        {
            Buffer.BlockCopy(pixels, y * stride, dib, offset, stride);
            offset += stride;
        }
        return dib;
    }

    private static uint RegisteredFormat(string name)
    {
        lock (FormatIds)
        {
            if (FormatIds.TryGetValue(name, out var known)) return known;
            var id = RegisterClipboardFormat(name);
            FormatIds[name] = id;
            return id;
        }
    }

    private static IntPtr EnsureOwnerWindow()
    {
        if (_ownerSource is not null) return _ownerSource.Handle;
        lock (OwnerGate)
        {
            if (_ownerSource is not null) return _ownerSource.Handle;
            _ownerSource = TryCreateOwnerWindow(HwndMessage) ?? TryCreateOwnerWindow(IntPtr.Zero);
            if (_ownerSource is null)
            {
                AppLog.Error("Could not create the clipboard owner window", new InvalidOperationException("HwndSource creation failed"));
                return IntPtr.Zero;
            }
            return _ownerSource.Handle;
        }
    }

    private static HwndSource? TryCreateOwnerWindow(IntPtr parent)
    {
        try
        {
            var parameters = new HwndSourceParameters("PromptFlowClipboardOwner")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            };
            if (parent != IntPtr.Zero) parameters.ParentWindow = parent;
            return new HwndSource(parameters);
        }
        catch { return null; }
    }

    private sealed record ClipboardFormatData(uint Format, byte[] Bytes);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern uint GetACP();
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
