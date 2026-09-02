using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using PromptFlow.Models;
using System.IO;
using System.Runtime.InteropServices;
using WpfClipboard = System.Windows.Clipboard;
using WpfTextDataFormat = System.Windows.TextDataFormat;
using WpfDataObject = System.Windows.DataObject;
using System.Diagnostics;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using FormsClipboard = System.Windows.Forms.Clipboard;

namespace PromptFlow.Services;

public sealed class ClipboardMonitor : IDisposable
{
    static ClipboardMonitor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    private readonly DispatcherTimer _timer;
    private readonly StorageRepository _repository;
    private readonly SettingsService _settings;
    private string? _lastSignature;
    private bool _ignoreNext;

    public bool IsEnabled => _settings.Current.MonitorEnabled;
    public event EventHandler<ClipboardItem>? ItemCaptured;
    public event EventHandler<string>? Notice;

    public ClipboardMonitor(StorageRepository repository, SettingsService settings)
    {
        _repository = repository; _settings = settings;
        AppLog.Info($"Clipboard monitor started. MaxImageBytes={_settings.Current.MaxImageBytes}");
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => Poll(), Dispatcher.CurrentDispatcher);
        _timer.Start();
    }

    public void RefreshSettings() => _timer.IsEnabled = _settings.Current.MonitorEnabled;
    public void IgnoreNextClipboardChange() => _ignoreNext = true;

    private void Poll()
    {
        if (!_settings.Current.MonitorEnabled || _ignoreNext) { _ignoreNext = false; return; }
        if (IsExcludedForegroundProcess()) return;
        try
        {
            var text = ReadUnicodeText();
            var html = WpfClipboard.ContainsText(WpfTextDataFormat.Html) ? WpfClipboard.GetText(WpfTextDataFormat.Html) : null;
            var rtf = WpfClipboard.ContainsText(WpfTextDataFormat.Rtf) ? WpfClipboard.GetText(WpfTextDataFormat.Rtf) : null;
            var image = TryReadImageBytes();
            if (text is null && html is null && rtf is null && image is null) return;
            var signature = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{text}\n{html}\n{rtf}\n{(image is null ? "" : Convert.ToBase64String(image))}")));
            if (signature == _lastSignature) return;
            _lastSignature = signature;
            var item = new ClipboardItem { TextContent = text, HtmlContent = html, RtfContent = rtf, ImagePng = image, DisplayText = text?.ReplaceLineEndings(" ").Trim() ?? (image is not null ? "图片" : "剪贴板内容"), CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow };
            var saved = _repository.UpsertClipboard(item, _settings.Current.MaxHistoryItems);
            if (image is not null) AppLog.Info($"Captured image item. Bytes={image.Length}; WpfFormats={DescribeFormats(WpfClipboard.GetDataObject())}; NativeFormats={DescribeNativeClipboardFormats()}; ItemId={saved.Id}");
            ItemCaptured?.Invoke(this, saved);
        }
        catch (ExternalException) { }
        catch (Exception ex) { AppLog.Error("Clipboard polling failed", ex); Notice?.Invoke(this, $"读取剪贴板失败：{ex.Message}"); }
    }

    private byte[]? TryReadImageBytes()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var data = WpfClipboard.GetDataObject();
                if (data is not null)
                {
                    // Chromium-based apps and several screenshot tools publish a
                    // native PNG format without the WPF Bitmap format. Reading it
                    // here also forces delayed rendering before a target app pastes it.
                    var bytes = TryReadEncodedImage(data)
                        ?? EncodeClipboardImage(data.GetData(System.Windows.DataFormats.Bitmap, true));
                    if (bytes is { Length: > 0 })
                    {
                        if (bytes.Length <= _settings.Current.MaxImageBytes)
                        {
                            return bytes;
                        }
                        AppLog.Warn($"Skipped oversized clipboard image. Bytes={bytes.Length}; Limit={_settings.Current.MaxImageBytes}");
                        Notice?.Invoke(this, "图片超过 10 MB，未保存到历史记录");
                        return null;
                    }
                }
                if (WpfClipboard.ContainsImage() && WpfClipboard.GetImage() is BitmapSource fallback)
                {
                    var bytes = EncodeBitmapSource(fallback);
                    if (bytes is { Length: > 0 } && bytes.Length <= _settings.Current.MaxImageBytes)
                    {
                        return bytes;
                    }
                }
                // WinForms reads CF_DIB/CF_DIBV5 from applications that expose
                // delayed-rendered images but do not provide a WPF BitmapSource.
                if (FormsClipboard.ContainsImage() && FormsClipboard.GetImage() is Image drawingFallback)
                {
                    using (drawingFallback)
                    {
                        var bytes = EncodeDrawingBitmap(new Bitmap(drawingFallback));
                        if (bytes is { Length: > 0 } && bytes.Length <= _settings.Current.MaxImageBytes)
                        {
                            return bytes;
                        }
                    }
                }
            }
            catch (ExternalException) { }
            catch (Exception ex) when (attempt == 2) { AppLog.Error("Clipboard image capture failed after 3 attempts", ex); Notice?.Invoke(this, $"读取图片失败：{ex.Message}"); }
            Thread.Sleep(25);
        }
        return null;
    }

    private static byte[]? EncodeBitmapSource(BitmapSource source)
    {
        var bytes = PngImageCodec.Encode(source, out var repairedTransparentAlpha);
        if (repairedTransparentAlpha)
            AppLog.Warn($"Repaired all-zero alpha channel from clipboard bitmap. Width={source.PixelWidth}; Height={source.PixelHeight}; PixelFormat={source.Format}");
        return bytes;
    }

    private static byte[]? EncodeDrawingBitmap(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[]? EncodeClipboardImage(object? value)
    {
        return value switch
        {
            BitmapSource source => EncodeBitmapSource(source),
            Bitmap drawing => EncodeDrawingBitmap(drawing),
            byte[] buffer => DecodeAndEncodeImage(buffer),
            Stream stream => DecodeAndEncodeImage(stream),
            _ => null
        };
    }

    private static byte[]? TryReadEncodedImage(System.Windows.IDataObject data)
    {
        foreach (var format in new[] { "PNG", "image/png" })
        {
            if (data.GetDataPresent(format, true))
            {
                var bytes = EncodeClipboardImage(data.GetData(format, true));
                if (bytes is { Length: > 0 }) return bytes;
            }
        }
        return null;
    }

    private static byte[]? DecodeAndEncodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return DecodeAndEncodeImage(stream);
    }

    private static byte[]? DecodeAndEncodeImage(Stream stream)
    {
        try
        {
            if (stream.CanSeek) stream.Position = 0;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count == 0 ? null : EncodeBitmapSource(decoder.Frames[0]);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Clipboard image payload was not a standalone encoded image. Type={stream.GetType().Name}; Error={ex.GetType().Name}: {ex.Message}");
            // CF_DIB is often surfaced as byte[] but is not a standalone image file.
            // Let the WPF and WinForms clipboard fallbacks obtain a real bitmap instead.
            return null;
        }
    }

    private static string? ReadUnicodeText()
    {
        if (WpfClipboard.ContainsText(WpfTextDataFormat.UnicodeText)) return WpfClipboard.GetText(WpfTextDataFormat.UnicodeText);
        if (!WpfClipboard.ContainsText(WpfTextDataFormat.Text)) return null;
        var decoded = WpfClipboard.GetText(WpfTextDataFormat.Text);
        var raw = ReadAnsiClipboard();
        return !string.IsNullOrWhiteSpace(raw) && (decoded.Contains('\ufffd') || LooksLikeMojibake(decoded)) ? raw : decoded;
    }

    private static bool LooksLikeMojibake(string value) => value.Count(c => c == '\ufffd') >= 2 || value.Contains("Ã") || value.Contains("Â");

    private static string? ReadAnsiClipboard()
    {
        const uint CfText = 1;
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(CfText);
            if (handle == IntPtr.Zero) return null;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                var length = 0; while (Marshal.ReadByte(ptr, length) != 0 && length < 16 * 1024 * 1024) length++;
                if (length == 0) return null;
                var bytes = new byte[length]; Marshal.Copy(ptr, bytes, 0, length);
                return Encoding.GetEncoding(936).GetString(bytes);
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    private bool IsExcludedForegroundProcess()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid == 0) return false;
            var name = Process.GetProcessById((int)pid).ProcessName + ".exe";
            return _repository.GetExclusions().Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase) || string.Equals(x, name[..^4], StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public static bool TryPaste(ClipboardItem item, bool plainText, out string? error)
    {
        try
        {
            var imagePng = item.ImagePng;
            var repairedTransparentAlpha = false;
            if (imagePng is { Length: > 0 })
            {
                imagePng = PngImageCodec.Normalize(imagePng, out repairedTransparentAlpha);
                if (repairedTransparentAlpha)
                    AppLog.Warn($"Repaired all-zero alpha channel from historical PNG before paste. ItemId={item.Id}; Bytes={imagePng.Length}");
            }
            AppLog.Info($"Preparing paste. ItemId={item.Id}; PlainText={plainText}; ImageBytes={imagePng?.Length ?? 0}; AlphaRepaired={repairedTransparentAlpha}");
            if (plainText || item.ImagePng is null && item.HtmlContent is null && item.RtfContent is null)
            {
                WpfClipboard.SetText(item.TextContent ?? item.DisplayText);
                error = null;
                return true;
            }

            var data = new WpfDataObject();
            if (item.TextContent is not null) data.SetText(item.TextContent, WpfTextDataFormat.UnicodeText);
            if (item.HtmlContent is not null) data.SetText(item.HtmlContent, WpfTextDataFormat.Html);
            if (item.RtfContent is not null) data.SetText(item.RtfContent, WpfTextDataFormat.Rtf);
            if (imagePng is not null)
            {
                using var stream = new MemoryStream(imagePng, writable: false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                data.SetImage(bitmap);
            }
            WpfClipboard.SetDataObject(data, true);
            if (imagePng is not null) TryPublishNativePng(imagePng);
            AppLog.Info($"Clipboard write succeeded. ItemId={item.Id}; WpfFormats={DescribeFormats(WpfClipboard.GetDataObject())}; NativeFormats={DescribeNativeClipboardFormats()}");
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Clipboard write failed. ItemId={item.Id}", ex);
            error = ex.Message;
            return false;
        }
    }

    public void Dispose() => _timer.Stop();

    private static void TryPublishNativePng(byte[] png)
    {
        // DataObject marshals arbitrary Stream values as OLE data. Some native
        // targets then receive the stream object rather than a PNG byte buffer.
        // Put the registered formats on the clipboard as HGLOBAL memory after
        // WPF has published its standard CF_DIB image format.
        if (!OpenClipboard(IntPtr.Zero))
        {
            AppLog.Warn($"Could not append native PNG formats. Win32Error={Marshal.GetLastWin32Error()}");
            return;
        }
        try
        {
            var pngFormat = RegisterClipboardFormat("PNG");
            var mimeFormat = RegisterClipboardFormat("image/png");
            try { SetClipboardBytes(pngFormat, png); AppLog.Info($"Published native PNG format. Id={pngFormat}; Bytes={png.Length}"); }
            catch (Exception ex) { AppLog.Error("Failed to publish native PNG format", ex); }
            try { SetClipboardBytes(mimeFormat, png); AppLog.Info($"Published native image/png format. Id={mimeFormat}; Bytes={png.Length}"); }
            catch (Exception ex) { AppLog.Error("Failed to publish native image/png format", ex); }
            AppLog.Info($"Native clipboard formats after PNG publish. {DescribeNativeClipboardFormatsUnsafe()}");
        }
        finally { CloseClipboard(); }
    }

    private static void SetClipboardBytes(uint format, byte[] bytes)
    {
        if (format == 0) throw new ExternalException("无法注册 PNG 剪贴板格式", Marshal.GetLastWin32Error());
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero) throw new OutOfMemoryException("无法为 PNG 剪贴板数据分配内存");
        try
        {
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) throw new ExternalException("无法写入 PNG 剪贴板数据", Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(handle); }
            if (SetClipboardData(format, handle) == IntPtr.Zero)
                throw new ExternalException("无法发布 PNG 剪贴板数据", Marshal.GetLastWin32Error());
            handle = IntPtr.Zero; // Clipboard now owns the HGLOBAL.
        }
        finally
        {
            if (handle != IntPtr.Zero) GlobalFree(handle);
        }
    }

    private static string DescribeFormats(System.Windows.IDataObject? data)
    {
        if (data is null) return "<none>";
        try { return string.Join(",", data.GetFormats(false).OrderBy(x => x, StringComparer.Ordinal)); }
        catch (Exception ex) { return $"<unavailable:{ex.GetType().Name}>"; }
    }

    private static string DescribeNativeClipboardFormats()
    {
        if (!OpenClipboard(IntPtr.Zero)) return $"<unavailable:Win32Error={Marshal.GetLastWin32Error()}>";
        try { return DescribeNativeClipboardFormatsUnsafe(); }
        finally { CloseClipboard(); }
    }

    private static string DescribeNativeClipboardFormatsUnsafe()
    {
        var formats = new List<string>();
        uint format = 0;
        while ((format = EnumClipboardFormats(format)) != 0)
        {
            formats.Add(DescribeNativeClipboardFormat(format));
        }
        var error = Marshal.GetLastWin32Error();
        return error == 0 ? string.Join(",", formats) : $"{string.Join(",", formats)}; EnumerationError={error}";
    }

    private static string DescribeNativeClipboardFormat(uint format)
    {
        var knownName = format switch
        {
            1 => "CF_TEXT",
            2 => "CF_BITMAP",
            8 => "CF_DIB",
            13 => "CF_UNICODETEXT",
            17 => "CF_DIBV5",
            _ => null
        };
        if (knownName is not null) return $"{knownName}({format})";
        var name = new StringBuilder(256);
        var length = GetClipboardFormatName(format, name, name.Capacity);
        return length > 0 ? $"{name}({format})" : $"Format{format}";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, StringBuilder formatName, int maxCount);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);

    private const uint GmemMoveable = 0x0002;
}
