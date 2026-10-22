using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using PromptFlow.Models;
using System.IO;
using System.Runtime.InteropServices;
using WpfClipboard = System.Windows.Clipboard;
using WpfTextDataFormat = System.Windows.TextDataFormat;
using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
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
    private uint _lastClipboardSequence;
    private bool _hasClipboardSequence;
    private static long _selfWriteSequence;
    private static int _writeInProgress;
    private readonly Dispatcher _dispatcher;
    private readonly object _lifecycle = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private volatile bool _disposed;
    private int _inFlight;
    private long _captureImageLimit;
    private sealed record Snapshot(uint Sequence, string? Text, string? Html, string? Rtf, byte[]? Image, string Signature, string? Notice);

    public bool IsEnabled => _settings.Current.MonitorEnabled;
    public event EventHandler<ClipboardItem>? ItemCaptured;
    public event EventHandler<string>? Notice;

    public ClipboardMonitor(StorageRepository repository, SettingsService settings)
    {
        _repository = repository; _settings = settings;
        AppLog.Info($"Clipboard monitor started. MaxImageBytes={_settings.Current.MaxImageBytes}");
        _dispatcher = Dispatcher.CurrentDispatcher;
        _worker = new Thread(CaptureLoop) { IsBackground = true, Name = "PromptFlow Clipboard STA" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => Poll(), Dispatcher.CurrentDispatcher);
        _timer.Start();
    }

    public void RefreshSettings() => _timer.IsEnabled = _settings.Current.MonitorEnabled;
    // Compatibility for callers arming this before a write: successful writes now
    // record an exact sequence themselves. Failed writes never hide unrelated copies.
    public void IgnoreNextClipboardChange() { }

    // A clipboard write touches the sequence several times (EmptyClipboard plus one
    // SetClipboardData per format). Reading during that window would capture a
    // half-published snapshot, so the monitor stands down while ClipboardWriter runs.
    internal static void BeginWrite() => Interlocked.Increment(ref _writeInProgress);
    internal static void EndWrite() => Interlocked.Decrement(ref _writeInProgress);
    internal static bool IsWriting => Volatile.Read(ref _writeInProgress) != 0;

    private void Poll()
    {
        if (!Monitor.TryEnter(_lifecycle)) return;
        try
        {
            if (_disposed || IsWriting || !_settings.Current.MonitorEnabled || Volatile.Read(ref _inFlight) != 0) return;
            var sequence = GetClipboardSequenceNumber();
            if (sequence != 0 && (sequence == (uint)Interlocked.Read(ref _selfWriteSequence) ||
                (_hasClipboardSequence && sequence == _lastClipboardSequence))) return;
            if (IsExcludedForegroundProcess()) return;
            _captureImageLimit = _settings.Current.MaxImageBytes;
            Interlocked.Exchange(ref _inFlight, 1);
            _wake.Set();
        }
        finally { Monitor.Exit(_lifecycle); }
    }

    private void CaptureLoop()
    {
        try
        {
            while (true)
            {
                _wake.WaitOne();
                if (_disposed) return;
                if (IsWriting)
                {
                    // The write is publishing formats; do not snapshot a half-built clipboard.
                    Interlocked.Exchange(ref _inFlight, 0);
                    continue;
                }
                Snapshot? snapshot = null;
                Exception? failure = null;
                try
                {
                    var sequence = GetClipboardSequenceNumber();
                    if (sequence == 0 || sequence != (uint)Interlocked.Read(ref _selfWriteSequence))
                    {
                        var text = ReadUnicodeText();
                        var html = WpfClipboard.ContainsText(WpfTextDataFormat.Html) ? WpfClipboard.GetText(WpfTextDataFormat.Html) : null;
                        var rtf = WpfClipboard.ContainsText(WpfTextDataFormat.Rtf) ? WpfClipboard.GetText(WpfTextDataFormat.Rtf) : null;
                        var image = TryReadImageBytes(out var notice);
                        // Reject mixed snapshots; neither contention nor sequence changes
                        // advance the committed sequence, so the next tick retries.
                        if (sequence != 0 && sequence == GetClipboardSequenceNumber())
                            snapshot = new Snapshot(sequence, text, html, rtf, image, ComputeSignature(text, html, rtf, image), notice);
                    }
                }
                catch (ExternalException) { }
                catch (Exception ex) { failure = ex; }
                if (_disposed) return;
                var captured = snapshot;
                var error = failure;
                ThreadPool.QueueUserWorkItem(_ => ProcessSnapshot(captured, error));
            }
        }
        finally { lock (_lifecycle) _wake.Dispose(); }
    }

    private void ProcessSnapshot(Snapshot? snapshot, Exception? error)
    {
        try
        {
            lock (_lifecycle)
            {
                if (_disposed) return;
                if (error is not null) throw error;
                if (snapshot is null || !_settings.Current.MonitorEnabled ||
                    snapshot.Sequence == (uint)Interlocked.Read(ref _selfWriteSequence)) return;
                if (snapshot.Notice is not null) PostNotice(snapshot.Notice);
                if (snapshot.Signature != _lastSignature &&
                    (snapshot.Text is not null || snapshot.Html is not null || snapshot.Rtf is not null || snapshot.Image is not null))
                {
                    var item = new ClipboardItem { TextContent = snapshot.Text, HtmlContent = snapshot.Html, RtfContent = snapshot.Rtf,
                        ImagePng = snapshot.Image, DisplayText = snapshot.Text?.ReplaceLineEndings(" ").Trim() ?? (snapshot.Image is not null ? "图片" : "剪贴板内容"),
                        CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow };
                    var saved = _repository.UpsertClipboard(item, _settings.Current.MaxHistoryItems);
                    _lastSignature = snapshot.Signature;
                    _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) ItemCaptured?.Invoke(this, saved); }));
                }
                _lastClipboardSequence = snapshot.Sequence;
                _hasClipboardSequence = true;
            }
        }
        catch (Exception ex) { AppLog.Error("Clipboard polling failed", ex); PostNotice($"读取剪贴板失败：{ex.Message}"); }
        finally { Interlocked.Exchange(ref _inFlight, 0); }
    }

    private void PostNotice(string message)
    {
        if (!_disposed && !_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) Notice?.Invoke(this, message); }));
    }

    private static string ComputeSignature(string? text, string? html, string? rtf, byte[]? image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSignaturePart(hash, text);
        AppendSignaturePart(hash, html);
        AppendSignaturePart(hash, rtf);
        AppendSignaturePart(hash, image);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendSignaturePart(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            hash.AppendData([0]);
            return;
        }

        hash.AppendData([1]);
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static void AppendSignaturePart(IncrementalHash hash, byte[]? value)
    {
        if (value is null)
        {
            hash.AppendData([0]);
            return;
        }

        hash.AppendData([1]);
        hash.AppendData(value);
        hash.AppendData([0]);
    }

    private byte[]? TryReadImageBytes(out string? notice)
    {
        notice = null;
        // One attempt per tick. Let contention escape so the sequence stays retryable.
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
                        if (bytes.Length <= _captureImageLimit)
                        {
                            return bytes;
                        }
                        AppLog.Warn($"Skipped oversized clipboard image. Bytes={bytes.Length}; Limit={_captureImageLimit}");
                        notice = $"图片超过 {_captureImageLimit / (1024 * 1024)} MB，未保存到历史记录";
                        return null;
                    }
                }
                if (WpfClipboard.ContainsImage() && WpfClipboard.GetImage() is BitmapSource fallback)
                {
                    var bytes = EncodeBitmapSource(fallback);
                    if (bytes is { Length: > 0 } && bytes.Length <= _captureImageLimit)
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
                        using var bitmap = new Bitmap(drawingFallback);
                        var bytes = EncodeDrawingBitmap(bitmap);
                        if (bytes is { Length: > 0 } && bytes.Length <= _captureImageLimit)
                        {
                            return bytes;
                        }
                    }
                }
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
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            try { return PngImageCodec.Normalize(bytes, out _); }
            catch (Exception ex) { AppLog.Warn($"Invalid clipboard PNG: {ex.Message}"); return null; }
        }
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

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _wake.Set();
        }
        if (_dispatcher.CheckAccess()) _timer.Stop();
        else if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(new Action(_timer.Stop));
        // Do not Join: delayed rendering can require the UI to service COM messages.
        // The worker owns no repository access; the lifecycle gate drains persistence.
    }

    internal static void RecordSelfWriteSequence()
    {
        // Another application may win immediately after the write returns.
        // Only suppress a stable sequence whose clipboard owner is this process.
        var sequence = GetClipboardSequenceNumber();
        GetWindowThreadProcessId(GetClipboardOwner(), out var owner);
        if (sequence != 0 && owner == (uint)Environment.ProcessId && sequence == GetClipboardSequenceNumber())
            Interlocked.Exchange(ref _selfWriteSequence, sequence);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
}
