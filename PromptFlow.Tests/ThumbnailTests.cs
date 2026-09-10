using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using PromptFlow.Models;
using PromptFlow.Services;

namespace PromptFlow.Tests;

public sealed class ThumbnailTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PromptFlow-thumbnails-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BackupPreservesPayloadAndThumbnailAsIndependentSnapshot()
    {
        using var repository = new StorageRepository(_directory);
        var original = MakePng();
        var item = Add(repository, original);
        var input = repository.GetThumbnailInput(item.Id)!;
        var thumbnail = MakePng(128, 64);
        Assert.True(repository.SaveThumbnail(item.Id, input.Version, thumbnail));
        var backupDirectory = Path.Combine(_directory, "backup");
        var backupPath = Path.Combine(backupDirectory, "promptflow.db");
        repository.BackupTo(backupPath);
        Assert.Throws<IOException>(() => repository.BackupTo(backupPath));
        Assert.Throws<ArgumentException>(() => repository.BackupTo(Path.Combine(_directory, "promptflow.db")));
        repository.DeleteItem(item.Id);
        using var backup = new StorageRepository(backupDirectory);
        Assert.Equal(original, backup.GetItem(item.Id)!.ImagePng);
        var saved = backup.GetThumbnailInput(item.Id)!;
        Assert.Equal(input.Version, saved.Version);
        Assert.Equal(thumbnail, saved.ThumbnailPng);
        Assert.Null(saved.OriginalPng);
        backup.UpdateDisplayText(item.Id, "only backup");
        Assert.Null(repository.GetItem(item.Id));
        Assert.Equal("only backup", backup.GetItem(item.Id)!.DisplayText);
    }

    [Fact]
    public async Task LegacyImageGeneratesOnDemandAndPersistsWithoutHydratingPageOrOriginalAgain()
    {
        long id;
        byte[] original = MakePng();
        using (var repository = new StorageRepository(_directory))
        {
            id = Add(repository, original).Id;
            Assert.Null(repository.GetThumbnailVersion(id));
            var summary = Assert.Single(repository.GetHistoryPage(10).Items);
            Assert.True(summary.HasImage);
            Assert.Null(summary.ImagePng);
            using var service = new ThumbnailService(repository, 1, 2);
            Assert.True(service.TryRequest(id, default, out var task));
            var image = await task;
            Assert.NotNull(image);
            Assert.True(image.IsFrozen);
            Assert.Equal(128, image.PixelWidth);
            Assert.Equal(64, image.PixelHeight);
            var input = repository.GetThumbnailInput(id)!;
            Assert.NotNull(input.ThumbnailPng);
            Assert.Null(input.OriginalPng);
            Assert.Equal(original, repository.GetItem(id)!.ImagePng);
        }
        using (var repository = new StorageRepository(_directory))
        {
            Assert.NotNull(repository.GetThumbnailInput(id)!.ThumbnailPng);
            Assert.Null(repository.GetThumbnailInput(id)!.OriginalPng);
            using var service = new ThumbnailService(repository);
            Assert.True(service.TryRequest(id, default, out var task));
            Assert.NotNull(await task);
        }
    }

    [Fact]
    public void LateWriteCannotResurrectDeletedOrReplacedImage()
    {
        using var repository = new StorageRepository(_directory);
        var item = Add(repository, MakePng());
        var input = repository.GetThumbnailInput(item.Id)!;
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "promptflow.db")};Pooling=False"))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE clipboard_items SET image_png_blob=$png WHERE id=$id";
            update.Parameters.AddWithValue("$png", MakePng(32, 32));
            update.Parameters.AddWithValue("$id", item.Id);
            update.ExecuteNonQuery();
        }
        Assert.False(repository.SaveThumbnail(item.Id, input.Version, MakePng(8, 8)));
        var replacement = repository.GetThumbnailInput(item.Id)!;
        Assert.NotEqual(input.Version, replacement.Version);
        repository.DeleteItem(item.Id);
        Assert.Null(repository.GetThumbnailVersion(item.Id));
        Assert.Null(repository.GetThumbnailInput(item.Id));
        Assert.False(repository.SaveThumbnail(item.Id, replacement.Version, MakePng(8, 8)));
    }

    [Fact]
    public void MetadataUpdateAndDuplicatePreserveCacheButClearAndTrimDeleteIt()
    {
        using var repository = new StorageRepository(_directory);
        var png = MakePng();
        var item = Add(repository, png);
        var input = repository.GetThumbnailInput(item.Id)!;
        Assert.True(repository.SaveThumbnail(item.Id, input.Version, MakePng(8, 8)));
        repository.UpdateDisplayText(item.Id, "renamed");
        Add(repository, png);
        Assert.Equal(input.Version, repository.GetThumbnailVersion(item.Id));
        Add(repository, MakePng(33, 33), 1);
        Assert.Null(repository.GetThumbnailVersion(item.Id));
        var remaining = Assert.Single(repository.GetHistoryPage(10).Items);
        repository.GetThumbnailInput(remaining.Id);
        repository.ClearHistory();
        Assert.Null(repository.GetThumbnailVersion(remaining.Id));
    }

    [Fact]
    public async Task CorruptPersistentThumbnailRecoversAndDeletedMemoryEntryIsNotReturned()
    {
        using var repository = new StorageRepository(_directory);
        var item = Add(repository, MakePng());
        var input = repository.GetThumbnailInput(item.Id)!;
        repository.SaveThumbnail(item.Id, input.Version, [1, 2, 3]);
        using var service = new ThumbnailService(repository);
        Assert.True(service.TryRequest(item.Id, default, out var task));
        Assert.NotNull(await task);
        Assert.NotEqual(input.Version, repository.GetThumbnailVersion(item.Id));
        repository.DeleteItem(item.Id);
        Assert.True(service.TryRequest(item.Id, default, out task));
        Assert.Null(await task);
    }

    [Fact]
    public async Task CancelledAndMalformedRequestsDoNotFaultWorkerAndDisposedRejectsRequests()
    {
        using var repository = new StorageRepository(_directory);
        var valid = Add(repository, MakePng());
        var invalid = Add(repository, [1, 2, 3]);
        var service = new ThumbnailService(repository, 1, 1);
        using (service)
        {
            Assert.True(service.TryRequest(valid.Id, new CancellationToken(true), out var task));
            Assert.Null(await task);
            Assert.Null(repository.GetThumbnailVersion(valid.Id));
            Assert.True(service.TryRequest(invalid.Id, default, out task));
            Assert.Null(await task);
            Assert.True(service.TryRequest(valid.Id, default, out task));
            Assert.NotNull(await task);
        }
        Assert.False(service.TryRequest(valid.Id, default, out var rejected));
        Assert.Null(await rejected);
    }

    [Fact]
    public async Task MemoryCacheEvictsAtConfiguredCapacityAndReturnsFrozenImages()
    {
        using var repository = new StorageRepository(_directory);
        var first = Add(repository, MakePng());
        var second = Add(repository, MakePng(256, 512));
        using var service = new ThumbnailService(repository, memoryCapacity: 1);
        Assert.True(service.TryRequest(first.Id, default, out var task));
        var original = await task;
        Assert.NotNull(original);
        Assert.True(original.IsFrozen);
        Assert.True(service.TryRequest(first.Id, default, out task));
        Assert.Same(original, await task);
        Assert.True(service.TryRequest(second.Id, default, out task));
        var other = await task;
        Assert.NotNull(other);
        Assert.True(other.IsFrozen);
        Assert.True(service.TryRequest(first.Id, default, out task));
        var reloaded = await task;
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsFrozen);
        Assert.NotSame(original, reloaded);
        Assert.Equal(original.PixelWidth, reloaded.PixelWidth);
        Assert.Null(repository.GetThumbnailInput(first.Id)!.OriginalPng);
    }

    [Fact]
    public void UnrealizedControlDoesNotRequestAndRecycledInputClearsSource()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var repository = new StorageRepository(_directory);
                var item = Add(repository, MakePng());
                using var service = new ThumbnailService(repository);
                var control = new PromptFlow.Controls.ThumbnailImage { Service = service, ItemId = item.Id };
                Assert.Null(repository.GetThumbnailVersion(item.Id));
                control.Source = PngImageCodec.DecodeThumbnail(MakePng(), 8, 8, out _);
                control.ItemId = item.Id + 1;
                Assert.Null(control.Source);
                control.Source = PngImageCodec.DecodeThumbnail(MakePng(), 8, 8, out _);
                control.DataContext = new object();
                Assert.Null(control.Source);
                Assert.Null(repository.GetThumbnailVersion(item.Id));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void RealizedVirtualizedCardRequestsAndRendersThumbnail()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var repository = new StorageRepository(_directory);
                var item = Add(repository, MakePng());
                using var service = new ThumbnailService(repository);
                var image = new PromptFlow.Controls.ThumbnailImage
                {
                    Service = service,
                    ItemId = item.Id,
                    Width = 48,
                    Height = 48
                };
                var list = new System.Windows.Controls.ListBox
                {
                    Width = 120,
                    Height = 80,
                    ItemsSource = new[] { image }
                };
                System.Windows.Controls.ScrollViewer.SetCanContentScroll(list, true);
                System.Windows.Controls.VirtualizingPanel.SetIsVirtualizing(list, true);
                var window = new System.Windows.Window { Content = list, Width = 140, Height = 110, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    window.Show();
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(20), System.Windows.Threading.DispatcherPriority.Background,
                        (_, _) =>
                        {
                            if (image.Source is not null || DateTime.UtcNow >= deadline) frame.Continue = false;
                        }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
                    timer.Start();
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                    timer.Stop();
                    Assert.NotNull(image.Source);
                    Assert.True(image.Source!.IsFrozen);
                }
                finally { window.Close(); }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Thumbnail control test timed out");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static ClipboardItem Add(StorageRepository repository, byte[] png, int max = 100) =>
        repository.UpsertClipboard(new ClipboardItem { ImagePng = png, DisplayText = "image" }, max);

    private static byte[] MakePng(int width = 512, int height = 256)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null,
            new byte[width * height * 3], width * 3);
        return PngImageCodec.Encode(bitmap, out _);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
