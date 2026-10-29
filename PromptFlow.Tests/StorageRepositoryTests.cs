using Microsoft.Data.Sqlite;
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
    public void HistoryPagesReturnAllRowsWithoutLoadingImagePayloads()
    {
        var path = Path.Combine(Path.GetTempPath(), "promptflow-test-" + Guid.NewGuid());
        try
        {
            using var repo = new StorageRepository(path);
            for (var i = 0; i < 25; i++)
            {
                repo.UpsertClipboard(new ClipboardItem
                {
                    TextContent = $"page-item-{i}",
                    DisplayText = $"page-item-{i}",
                    ImagePng = i % 2 == 0 ? [1, 2, 3] : null,
                    CreatedAt = DateTime.UtcNow,
                    LastCopiedAt = DateTime.UtcNow
                }, 100);
            }

            var all = new List<ClipboardItem>();
            DateTime? beforeTime = null;
            long beforeId = 0;
            var pageCount = 0;
            while (true)
            {
                var page = repo.GetHistoryPage(10, beforeTime, beforeId);
                pageCount++;
                all.InsertRange(0, page.Items);
                Assert.All(page.Items, item => Assert.Null(item.ImagePng));
                if (!page.HasMore) break;
                beforeTime = page.BeforeTime;
                beforeId = page.BeforeId ?? 0;
            }

            Assert.Equal(3, pageCount);
            Assert.Equal(25, all.Count);
            Assert.Equal(25, all.Select(item => item.Id).Distinct().Count());
            Assert.Equal("page-item-0", all[0].DisplayText);
            Assert.Equal("page-item-24", all[^1].DisplayText);
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

    [Fact]
    public void HistoryPagingTiesPreserveExactUtcCursor()
    {
        WithRepository((repo, path) =>
        {
            var ids = Seed(repo, 5);
            const string timestamp = "2026-08-01T12:34:56.1234567Z";
            ExecuteSql(path, $"UPDATE clipboard_items SET created_at='{timestamp}',last_copied_at='{timestamp}'");
            var first = repo.GetHistoryPage(2);
            Assert.True(first.HasMore);
            Assert.Equal(timestamp, first.BeforeTime!.Value.ToString("O"));
            Assert.All(first.Items, item => Assert.Equal(DateTimeKind.Utc, item.CreatedAt.Kind));
            Assert.Equal(new[] { ids[3], ids[4] }, first.Items.Select(x => x.Id));
            var second = repo.GetHistoryPage(2, first.BeforeTime, first.BeforeId!.Value);
            var third = repo.GetHistoryPage(2, second.BeforeTime, second.BeforeId!.Value);
            Assert.Equal(new[] { ids[1], ids[2] }, second.Items.Select(x => x.Id));
            Assert.Equal(ids[0], Assert.Single(third.Items).Id);
            Assert.False(third.HasMore);
            Assert.Empty(repo.GetHistoryPage(2, third.BeforeTime, third.BeforeId!.Value).Items);
            Assert.Equal(DateTimeKind.Utc, repo.GetItem(ids[0])!.LastCopiedAt.Kind);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FolderPagesPreserveOrderAndDoNotHydratePayloads(bool tiedSortOrders)
    {
        WithRepository((repo, path) =>
        {
            var ids = Seed(repo, 5);
            var folder = repo.CreateFolder("paged");
            foreach (var id in ids) repo.AddToFolder(id, folder);
            var other = repo.CreateFolder("other");
            repo.AddToFolder(ids[0], other);
            if (tiedSortOrders) ExecuteSql(path, $"UPDATE folder_items SET sort_order=7 WHERE folder_id={folder}");
            var first = repo.GetFolderPage(folder, 2);
            var second = repo.GetFolderPage(folder, 2, first.BeforeSortOrder, first.BeforeId!.Value);
            var third = repo.GetFolderPage(folder, 2, second.BeforeSortOrder, second.BeforeId!.Value);
            Assert.True(first.HasMore);
            Assert.True(second.HasMore);
            Assert.False(third.HasMore);
            var all = first.Items.Concat(second.Items).Concat(third.Items).ToArray();
            Assert.Equal(ids.Reverse(), all.Select(x => x.Id));
            Assert.All(all, item => { Assert.Null(item.TextContent); Assert.Null(item.ImagePng); Assert.True(item.HasImage); });
            Assert.Empty(repo.GetFolderPage(folder, 2, third.BeforeSortOrder, third.BeforeId!.Value).Items);
            Assert.Single(repo.GetFolderPage(other, 2).Items);
        });
    }

    [Fact]
    public void PartialFolderReorderPreservesUnloadedAndUnspecifiedPositions()
    {
        WithRepository((repo, _) =>
        {
            var ids = Seed(repo, 6);
            var folder = repo.CreateFolder("partial");
            foreach (var id in ids) repo.AddToFolder(id, folder);
            var loaded = repo.GetFolderPage(folder, 3).Items.Select(x => x.Id).ToArray();
            repo.ReorderFolderItems(folder, new[] { loaded[2], loaded[0], loaded[1] });
            Assert.Equal(new[] { ids[3], ids[5], ids[4], ids[2], ids[1], ids[0] }, repo.GetFolderPage(folder, 10).Items.Select(x => x.Id));
            // Noncontiguous supplied IDs swap only their occupied slots; duplicates/stale IDs are ignored.
            repo.ReorderFolderItems(folder, new[] { ids[1], long.MaxValue, ids[5], ids[1] });
            Assert.Equal(new[] { ids[3], ids[1], ids[4], ids[2], ids[5], ids[0] }, repo.GetFolderPage(folder, 10).Items.Select(x => x.Id));
            Assert.Equal(6, repo.GetFolders().Single().ItemCount);
        });
    }

    [Fact]
    public void UpdatingSummaryDisplayTextPreservesStoredPayload()
    {
        WithRepository((repo, _) =>
        {
            var original = repo.UpsertClipboard(new ClipboardItem
            {
                TextContent = "original text", DisplayText = "original label", HtmlContent = "<b>original</b>",
                RtfContent = "rtf", ImagePng = [1, 2, 3], ExtraFormatsJson = "{}"
            }, 10);
            var summary = Assert.Single(repo.GetHistoryPage(5).Items);
            Assert.Null(summary.TextContent);
            repo.UpdateDisplayText(summary.Id, "edited label");
            var saved = repo.GetItem(original.Id)!;
            Assert.Equal("edited label", saved.DisplayText);
            Assert.Equal(original.TextContent, saved.TextContent);
            Assert.Equal(original.HtmlContent, saved.HtmlContent);
            Assert.Equal(original.RtfContent, saved.RtfContent);
            Assert.Equal(original.ImagePng, saved.ImagePng);
            Assert.Equal(original.ExtraFormatsJson, saved.ExtraFormatsJson);
            Assert.Equal("edited label", repo.UpsertClipboard(original, 10).DisplayText);
        });
    }

    [Fact]
    public void PageQueriesReportTextLengthWithoutLineBreaks()
    {
        WithRepository((repo, _) =>
        {
            var text = repo.UpsertClipboard(new ClipboardItem
            {
                TextContent = "第一行\r\n第二行\n", DisplayText = "第一行 第二行",
                CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow
            }, 10);
            var image = repo.UpsertClipboard(new ClipboardItem
            {
                DisplayText = "图片", ImagePng = [1, 2, 3],
                CreatedAt = DateTime.UtcNow, LastCopiedAt = DateTime.UtcNow
            }, 10);
            var folder = repo.CreateFolder("counted");
            repo.AddToFolder(text.Id, folder);
            repo.AddToFolder(image.Id, folder);

            var history = Assert.Single(repo.GetHistoryPage(10).Items, i => i.Id == text.Id);
            Assert.Equal(6, history.TextLength);
            Assert.True(history.HasTextCount);
            Assert.Equal("6 字", history.TextLengthText);

            // 收藏夹分页走另一条 SELECT，字数同样来自数据库统计而不是展示文字。
            var fromFolder = Assert.Single(repo.GetFolderPage(folder, 10).Items, i => i.Id == text.Id);
            Assert.Equal(6, fromFolder.TextLength);

            var imageItem = Assert.Single(repo.GetHistoryPage(10).Items, i => i.Id == image.Id);
            Assert.Equal(0, imageItem.TextLength);
            Assert.False(imageItem.HasTextCount);
            Assert.Empty(imageItem.TextLengthText);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PageLimitsMustBePositive(int limit)
    {
        WithRepository((repo, _) =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => repo.GetHistoryPage(limit));
            Assert.Throws<ArgumentOutOfRangeException>(() => repo.GetFolderPage(1, limit));
        });
    }

    [Fact]
    public void MaximumPageLimitDoesNotOverflow()
    {
        WithRepository((repo, _) =>
        {
            Seed(repo, 1);
            Assert.Single(repo.GetHistoryPage(int.MaxValue).Items);
            Assert.Empty(repo.GetFolderPage(1, int.MaxValue).Items);
        });
    }

    private static long[] Seed(StorageRepository repo, int count) => Enumerable.Range(0, count)
        .Select(i => repo.UpsertClipboard(new ClipboardItem { TextContent = $"seed-{i}", DisplayText = $"seed-{i}", ImagePng = [1, 2, 3] }, 100).Id).ToArray();

    private static void ExecuteSql(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(path, "promptflow.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void WithRepository(Action<StorageRepository, string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "promptflow-test-" + Guid.NewGuid());
        try
        {
            using var repo = new StorageRepository(path);
            action(repo, path);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
