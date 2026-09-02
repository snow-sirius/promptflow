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
            ItemCaptured?.Invoke(this, saved);
        }
        catch (ExternalException) { }
        catch (Exception ex) { Notice?.Invoke(this, $"读取剪贴板失败：{ex.Message}"); }
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
                    var raw = data.GetData(System.Windows.DataFormats.Bitmap, true);
                    byte[]? bytes = raw switch
                    {
                        BitmapSource source => EncodeBitmapSource(source),
                        Bitmap drawing => EncodeDrawingBitmap(drawing),
                        byte[] buffer => buffer,
                        _ => null
                    };
                    if (bytes is { Length: > 0 })
                    {
                        if (bytes.Length <= _settings.Current.MaxImageBytes) return bytes;
                        Notice?.Invoke(this, "图片超过 10 MB，未保存到历史记录");
                        return null;
                    }
                }
                if (WpfClipboard.ContainsImage() && WpfClipboard.GetImage() is BitmapSource fallback)
                {
                    var bytes = EncodeBitmapSource(fallback);
                    if (bytes is { Length: > 0 } && bytes.Length <= _settings.Current.MaxImageBytes) return bytes;
                }
                // WinForms reads CF_DIB/CF_DIBV5 from applications that expose
                // delayed-rendered images but do not provide a WPF BitmapSource.
                if (FormsClipboard.ContainsImage() && FormsClipboard.GetImage() is Image drawingFallback)
                {
                    using (drawingFallback)
                    {
                        var bytes = EncodeDrawingBitmap(new Bitmap(drawingFallback));
                        if (bytes is { Length: > 0 } && bytes.Length <= _settings.Current.MaxImageBytes) return bytes;
                    }
                }
            }
            catch (ExternalException) { }
            catch (Exception ex) when (attempt == 2) { Notice?.Invoke(this, $"读取图片失败：{ex.Message}"); }
            Thread.Sleep(25);
        }
        return null;
    }

    private static byte[]? EncodeBitmapSource(BitmapSource source)
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static byte[]? EncodeDrawingBitmap(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
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
            if (item.ImagePng is not null)
            {
                using var stream = new MemoryStream(item.ImagePng, writable: false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                data.SetImage(bitmap);
                // Some native editors only consume a PNG stream (CF_PNG)
                // instead of WPF's BitmapSource/CF_DIB representation.
                data.SetData("PNG", item.ImagePng, true);
            }
            WpfClipboard.SetDataObject(data, true);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Dispose() => _timer.Stop();

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
}
