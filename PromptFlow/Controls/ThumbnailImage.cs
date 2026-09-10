using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PromptFlow.Services;

namespace PromptFlow.Controls;

/// <summary>Bind Service and ItemId, not Source. WPF virtualization creates this control only for
/// realized cards; recycled or hidden cards cancel interest and reject late completions.</summary>
public sealed class ThumbnailImage : System.Windows.Controls.Image
{
    public static readonly DependencyProperty ServiceProperty = DependencyProperty.Register(
        nameof(Service), typeof(ThumbnailService), typeof(ThumbnailImage), new PropertyMetadata(null, InputChanged));
    public static readonly DependencyProperty ItemIdProperty = DependencyProperty.Register(
        nameof(ItemId), typeof(long), typeof(ThumbnailImage), new PropertyMetadata(0L, InputChanged));
    public ThumbnailService? Service { get => (ThumbnailService?)GetValue(ServiceProperty); set => SetValue(ServiceProperty, value); }
    public long ItemId { get => (long)GetValue(ItemIdProperty); set => SetValue(ItemIdProperty, value); }
    private CancellationTokenSource? _interest;
    private DispatcherTimer? _retry;
    private long _generation;
    private bool _requested;

    public ThumbnailImage()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => CheckVisibility();
        DataContextChanged += (_, _) => Reset();
        Stretch = Stretch.Uniform;
    }

    private static void InputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ThumbnailImage)d).Reset();
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LayoutUpdated += OnLayoutUpdated;
        CheckVisibility();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        Cancel();
    }
    private void OnLayoutUpdated(object? sender, EventArgs e) => CheckVisibility();
    private void Reset() { Cancel(); CheckVisibility(); }
    private void Cancel()
    {
        _generation++;
        _interest?.Cancel();
        _interest?.Dispose();
        _interest = null;
        _retry?.Stop();
        _requested = false;
        Source = null;
    }

    private bool IsRealizedCard()
    {
        // With a recycling VirtualizingStackPanel, a loaded image belongs to a
        // realized card. Checking ScrollContentPresenter intersections here was
        // unreliable: the presenter can report an intermediate zero-sized layout
        // while an item is plainly visible, leaving all thumbnails blank.
        // A virtualized item is not loaded until WPF realizes its container.
        // ContainerFromElement is not reliable for a control nested in an item
        // template, so Loaded/Unloaded is the correct ownership boundary here.
        return IsLoaded && IsVisible;
    }

    private void CheckVisibility()
    {
        if (!IsRealizedCard())
        {
            if (_requested || _interest is not null || _retry?.IsEnabled == true) Cancel();
            return;
        }
        if (_requested || Service is null || Service.IsDisposed || ItemId <= 0) return;
        StartRequest();
    }

    private async void StartRequest()
    {
        var service = Service!;
        var id = ItemId;
        var generation = _generation;
        _interest ??= new CancellationTokenSource();
        if (!service.TryRequest(id, _interest.Token, out var task))
        {
            if (!service.IsDisposed)
            {
                _retry ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
                    (_, _) => { _retry!.Stop(); CheckVisibility(); }, Dispatcher);
                _retry.Start();
            }
            return;
        }
        _retry?.Stop();
        _requested = true;
        try
        {
            var image = await task;
            if (generation == _generation && ReferenceEquals(service, Service) && id == ItemId && IsRealizedCard())
                Source = image;
        }
        catch (Exception) { /* Async event boundary: a failed preview must not crash the dispatcher. */ }
    }
}
