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
}
