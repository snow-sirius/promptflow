using PromptFlow.Models;
using PromptFlow.Services;
using System.IO;

namespace PromptFlow.Tests;

public sealed class StorageRepositoryTests
{
    [Fact]
    public void UpsertDeduplicatesAndPreservesFavorite()
    {
        var path = Path.Combine(Path.GetTempPath(), "promptflow-test-" + Guid.NewGuid());
        try
        {
            using var repo = new StorageRepository(path);
            var item = new ClipboardItem { TextContent = "hello", DisplayText = "hello", CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow };
            var first = repo.UpsertClipboard(item, 5);
            repo.SetFavorite(first.Id, true);
            var second = repo.UpsertClipboard(item, 5);
            Assert.Equal(first.Id, second.Id);
            Assert.True(repo.GetHistory(5).Single().IsFavorite);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void HistoryTrimKeepsNewestItems()
    {
        var path = Path.Combine(Path.GetTempPath(), "promptflow-test-" + Guid.NewGuid());
        try
        {
            using var repo = new StorageRepository(path);
            for (var i = 0; i < 3; i++) repo.UpsertClipboard(new ClipboardItem { TextContent = $"item-{i}", DisplayText = $"item-{i}", CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow }, 2);
            Assert.Equal(2, repo.GetHistory(10).Count);
            Assert.DoesNotContain(repo.GetHistory(10), x => x.TextContent == "item-0");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void RemovingLastFolderLinkClearsFavorite()
    {
        var path = Path.Combine(Path.GetTempPath(), "promptflow-test-" + Guid.NewGuid());
        try
        {
            using var repo = new StorageRepository(path);
            var item = repo.UpsertClipboard(new ClipboardItem { TextContent = "folder-item", DisplayText = "folder-item", CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow }, 5);
            var folder = repo.CreateFolder("Test");
            repo.AddToFolder(item.Id, folder);
            Assert.True(repo.GetFolderItems(folder).Single().IsFavorite);
            repo.RemoveFromFolder(item.Id, folder);
            Assert.False(repo.GetHistory(5).Single().IsFavorite);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void HistoryLimitCountsOnlyUnfavoritedItems()
    {
        var path = Path.Combine(Path.GetTempPath(), "promptflow-test-" + Guid.NewGuid());
        try
        {
            using var repo = new StorageRepository(path);
            var favorite = repo.UpsertClipboard(new ClipboardItem { TextContent = "favorite", DisplayText = "favorite", CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow }, 1);
            repo.SetFavorite(favorite.Id, true);
            repo.UpsertClipboard(new ClipboardItem { TextContent = "new-1", DisplayText = "new-1", CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow }, 1);
            repo.UpsertClipboard(new ClipboardItem { TextContent = "new-2", DisplayText = "new-2", CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow }, 1);
            var history = repo.GetHistory(10);
            Assert.Equal(2, history.Count);
            Assert.Contains(history, x => x.TextContent == "favorite" && x.IsFavorite);
            Assert.Contains(history, x => x.TextContent == "new-2" && !x.IsFavorite);
            Assert.DoesNotContain(history, x => x.TextContent == "new-1");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void ClearHistoryRemovesOnlyNonFavoritesAndReturnsCount()
    {
        var path = Path.Combine(Path.GetTempPath(), "promptflow-test-" + Guid.NewGuid());
        try
        {
            using var repo = new StorageRepository(path);
            var favorite = repo.UpsertClipboard(new ClipboardItem { TextContent = "keep", DisplayText = "keep", CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow }, 10);
            repo.SetFavorite(favorite.Id, true);
            repo.UpsertClipboard(new ClipboardItem { TextContent = "remove-1", DisplayText = "remove-1", CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow }, 10);
            repo.UpsertClipboard(new ClipboardItem { TextContent = "remove-2", DisplayText = "remove-2", CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow }, 10);

            Assert.Equal(2, repo.ClearHistory());
            var remaining = repo.GetHistory(10);
            Assert.Single(remaining);
            Assert.Equal("keep", remaining[0].TextContent);
            Assert.True(remaining[0].IsFavorite);
            Assert.Equal(0, repo.ClearHistory());
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
