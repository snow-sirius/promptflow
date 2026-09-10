using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PromptFlow.Models;

namespace PromptFlow.Tests;

public sealed class PaginationAnchorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(137)]
    [InlineData(1237)]
    public void PrependingPagesPreservesVisibleIdAndPixelOffset(double offset)
    {
        RunSta(() =>
        {
            var items = new ObservableCollection<ClipboardItem>(Enumerable.Range(100, 100)
                .Select(id => new ClipboardItem { Id = id, DisplayText = $"Item {id}" }));
            var list = new ListBox { ItemsSource = items, Width = 300, Height = 230 };
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 70d));
            list.ItemContainerStyle = style;
            ScrollViewer.SetCanContentScroll(list, true);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
            var window = new Window { Content = list, Width = 330, Height = 280, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                Pump();
                var viewer = Child<ScrollViewer>(list)!;
                viewer.ScrollToVerticalOffset(offset);
                Pump();
                var capture = typeof(MainWindow).GetMethod("CaptureViewAnchor", BindingFlags.Static | BindingFlags.NonPublic)!;
                var restore = typeof(MainWindow).GetMethod("RestoreViewAnchor", BindingFlags.Static | BindingFlags.NonPublic)!;
                var anchor = capture.Invoke(null, [list]);
                Assert.NotNull(anchor);
                var type = anchor!.GetType();
                var originalId = (long)type.GetProperty("ItemId")!.GetValue(anchor)!;
                var originalTop = (double)type.GetProperty("Top")!.GetValue(anchor)!;
                for (var page = 0; page < 3; page++)
                {
                    for (var i = 0; i < 10; i++) items.Insert(0, new ClipboardItem { Id = 99 - page * 10 - i });
                    restore.Invoke(null, [list, anchor]);
                    Pump();
                    restore.Invoke(null, [list, anchor]);
                    Pump();
                    var after = capture.Invoke(null, [list])!;
                    Assert.Equal(originalId, (long)type.GetProperty("ItemId")!.GetValue(after)!);
                    Assert.InRange(Math.Abs(originalTop - (double)type.GetProperty("Top")!.GetValue(after)!), 0, 0.5);
                    Assert.True(Child<VirtualizingStackPanel>(list)!.Children.Count < items.Count);
                }
            }
            finally { window.Close(); }
        });
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static T? Child<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            if (Child<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF anchor test timed out");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
