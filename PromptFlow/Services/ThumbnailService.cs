using System.Threading.Channels;
using System.Windows.Media.Imaging;

namespace PromptFlow.Services;

/// <summary>One worker, a bounded non-waiting queue, and an LRU of frozen 128px images.
/// Dispose before disposing the supplied repository. No UI thread performs SQLite or image decoding.</summary>
public sealed class ThumbnailService : IDisposable
{
    public const int PixelSize = 128;
    private readonly StorageRepository _repository;
    private readonly Channel<Request> _queue;
    private readonly Task _worker;
    private readonly int _memoryCapacity;
    private readonly Dictionary<long, LinkedListNode<Entry>> _cache = new();
    private readonly LinkedList<Entry> _lru = new();
    private int _disposed;

    public ThumbnailService(StorageRepository repository, int memoryCapacity = 128, int queueCapacity = 128)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        _repository = repository;
        _memoryCapacity = memoryCapacity;
        _queue = Channel.CreateBounded<Request>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(WorkAsync);
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>False means queue full/disposed; caller may retry while still visible.
    /// Cancellation removes interest, and queued cancelled work never reads the repository.</summary>
    public bool TryRequest(long itemId, CancellationToken cancellation, out Task<BitmapSource?> result)
    {
        var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        result = completion.Task;
        if (IsDisposed || !_queue.Writer.TryWrite(new Request(itemId, cancellation, completion)))
        {
            completion.TrySetResult(null);
            return false;
        }
        return true;
    }

    private async Task WorkAsync()
    {
        await foreach (var request in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            BitmapSource? image = null;
            if (!IsDisposed && !request.Cancellation.IsCancellationRequested)
            {
                try { image = Load(request.ItemId, request.Cancellation); }
                catch (Exception) { /* Missing/corrupt images and storage failures are a blank preview, not a UI failure. */ }
            }
            request.Completion.TrySetResult(request.Cancellation.IsCancellationRequested ? null : image);
        }
        _cache.Clear();
        _lru.Clear();
    }

    private BitmapSource? Load(long itemId, CancellationToken cancellation)
    {
        if (_cache.TryGetValue(itemId, out var cached))
        {
            if (_repository.GetThumbnailVersion(itemId) == cached.Value.Version)
            {
                _lru.Remove(cached);
                _lru.AddFirst(cached);
                return cached.Value.Image;
            }
            _cache.Remove(itemId);
            _lru.Remove(cached);
        }
        var input = _repository.GetThumbnailInput(itemId);
        if (input is null || cancellation.IsCancellationRequested) return null;
        BitmapSource image;
        if (input.ThumbnailPng is not null)
        {
            try { image = PngImageCodec.DecodeThumbnail(input.ThumbnailPng, PixelSize, PixelSize, out _); }
            catch (Exception)
            {
                // Recover a corrupt persistent preview from the untouched original, once.
                _repository.InvalidateThumbnail(itemId, input.Version);
                input = _repository.GetThumbnailInput(itemId);
                if (input?.OriginalPng is null || cancellation.IsCancellationRequested) return null;
                image = Generate(itemId, input);
            }
        }
        else
        {
            if (input.OriginalPng is null) return null;
            image = Generate(itemId, input);
        }
        if (cancellation.IsCancellationRequested || _repository.GetThumbnailVersion(itemId) != input.Version) return null;
        image.Freeze();
        var node = _lru.AddFirst(new Entry(itemId, input.Version, image));
        _cache[itemId] = node;
        while (_cache.Count > _memoryCapacity)
        {
            var last = _lru.Last!;
            _cache.Remove(last.Value.ItemId);
            _lru.RemoveLast();
        }
        return image;
    }

    private BitmapSource Generate(long itemId, StorageRepository.ThumbnailInput input)
    {
        var image = PngImageCodec.DecodeThumbnail(input.OriginalPng!, PixelSize, PixelSize, out _);
        var png = PngImageCodec.Encode(image, out _);
        _repository.SaveThumbnail(itemId, input.Version, png);
        return image;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        // Worker never calls the dispatcher. Waiting protects repository disposal from an in-flight decode/write.
        _worker.GetAwaiter().GetResult();
    }

    private sealed record Request(long ItemId, CancellationToken Cancellation, TaskCompletionSource<BitmapSource?> Completion);
    private sealed record Entry(long ItemId, string Version, BitmapSource Image);
}
