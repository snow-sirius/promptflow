using System.Text.Json;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PromptFlow.Models;
using System.IO;

namespace PromptFlow.Services;

public sealed class StorageRepository : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public StorageRepository(string directory)
    {
        Directory.CreateDirectory(directory);
        _connection = new SqliteConnection($"Data Source={Path.Combine(directory, "promptflow.db")};Pooling=False");
        _connection.Open();
        Initialize();
    }

    /// <summary>Creates a consistent standalone snapshot, including original payloads and thumbnails.
    /// An existing destination is never overwritten.</summary>
    public void BackupTo(string targetDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDbPath);
        var target = Path.GetFullPath(targetDbPath);
        lock (_gate)
        {
            if (string.Equals(target, Path.GetFullPath(_connection.DataSource), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Backup destination must differ from the current database.", nameof(targetDbPath));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Reserve exclusively: never overwrite another directory's existing user database.
            using (new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            try
            {
                using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = target, Pooling = false
                }.ToString());
                destination.Open();
                _connection.BackupDatabase(destination);
            }
            catch
            {
                // Only this invocation created this file. Do not leave a failed snapshot to be
                // mistaken for a complete database by the next directory-switch attempt.
                try { File.Delete(target); } catch (IOException) { }
                throw;
            }
        }
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
    CREATE TABLE IF NOT EXISTS clipboard_items (
     id INTEGER PRIMARY KEY AUTOINCREMENT, text_content TEXT, html_content TEXT, rtf_content TEXT,
     image_png_blob BLOB, extra_formats_json TEXT, display_text TEXT NOT NULL, created_at TEXT NOT NULL,
     last_copied_at TEXT NOT NULL, is_favorite INTEGER NOT NULL DEFAULT 0);
    DROP INDEX IF EXISTS idx_clipboard_text;
    CREATE UNIQUE INDEX IF NOT EXISTS idx_clipboard_content ON clipboard_items(text_content, html_content, rtf_content, image_png_blob);
    CREATE TABLE IF NOT EXISTS folders (
     id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, sort_order INTEGER NOT NULL,
     is_locked INTEGER NOT NULL DEFAULT 0, last_used_at TEXT NULL);
    CREATE TABLE IF NOT EXISTS folder_items (
     folder_id INTEGER NOT NULL, item_id INTEGER NOT NULL, sort_order INTEGER NOT NULL, created_at TEXT NOT NULL,
     PRIMARY KEY(folder_id, item_id));
    CREATE TABLE IF NOT EXISTS clipboard_thumbnails (
     item_id INTEGER PRIMARY KEY, version TEXT NOT NULL, png BLOB NULL);
    CREATE TRIGGER IF NOT EXISTS clipboard_thumbnail_delete AFTER DELETE ON clipboard_items
     BEGIN DELETE FROM clipboard_thumbnails WHERE item_id=OLD.id; END;
    CREATE TRIGGER IF NOT EXISTS clipboard_thumbnail_image_update AFTER UPDATE OF image_png_blob ON clipboard_items
     WHEN OLD.image_png_blob IS NOT NEW.image_png_blob
     BEGIN DELETE FROM clipboard_thumbnails WHERE item_id=OLD.id; END;
    CREATE TABLE IF NOT EXISTS app_exclusions (process_name TEXT PRIMARY KEY);
    CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
    CREATE INDEX IF NOT EXISTS idx_clipboard_last_copied ON clipboard_items(last_copied_at DESC, id DESC);
    CREATE INDEX IF NOT EXISTS idx_folder_items_page ON folder_items(folder_id, sort_order DESC, item_id DESC);
    INSERT INTO schema_version(version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);";
            cmd.ExecuteNonQuery();
        }
    }

    public List<ClipboardItem> GetHistory(int limit)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM clipboard_items ORDER BY last_copied_at ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadItems(cmd);
        }
    }

    public ClipboardItem? GetItem(long itemId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM clipboard_items WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", itemId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        }
    }

    public ClipboardItemPage GetHistoryPage(int limit, DateTime? beforeTime = null, long beforeId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT id, display_text, created_at, last_copied_at,
                                       is_favorite, image_png_blob IS NOT NULL AS has_image
                                FROM clipboard_items
                                WHERE ($beforeTime IS NULL OR last_copied_at < $beforeTime
                                      OR (last_copied_at = $beforeTime AND id < $beforeId))
                                ORDER BY last_copied_at DESC, id DESC
                                LIMIT $limit";
            cmd.Parameters.AddWithValue("$beforeTime", beforeTime.HasValue ? beforeTime.Value.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$beforeId", beforeId);
            cmd.Parameters.AddWithValue("$limit", (long)limit + 1);
            var items = ReadSummaryItems(cmd);
            var hasMore = items.Count > limit;
            if (hasMore) items.RemoveAt(items.Count - 1);
            var cursor = items.Count == 0 ? null : items[^1];
            items.Reverse();
            return new ClipboardItemPage(items, hasMore, cursor?.LastCopiedAt, cursor?.Id);
        }
    }

    public ClipboardItemPage GetFolderPage(long folderId, int limit, int? beforeSortOrder = null, long beforeItemId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT ci.id, ci.display_text, ci.created_at,
                                       ci.last_copied_at, ci.is_favorite, ci.image_png_blob IS NOT NULL AS has_image,
                                       fi.sort_order
                                FROM clipboard_items ci
                                JOIN folder_items fi ON fi.item_id=ci.id
                                WHERE fi.folder_id=$folder
                                  AND ($beforeSortOrder IS NULL OR fi.sort_order < $beforeSortOrder
                                      OR (fi.sort_order = $beforeSortOrder AND fi.item_id < $beforeItemId))
                                ORDER BY fi.sort_order DESC, fi.item_id DESC
                                LIMIT $limit";
            cmd.Parameters.AddWithValue("$folder", folderId);
            cmd.Parameters.AddWithValue("$beforeSortOrder", beforeSortOrder.HasValue ? beforeSortOrder.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$beforeItemId", beforeItemId);
            cmd.Parameters.AddWithValue("$limit", (long)limit + 1);
            var items = ReadFolderSummaryItems(cmd);
            var hasMore = items.Count > limit;
            if (hasMore) items.RemoveAt(items.Count - 1);
            var cursor = items.Count == 0 ? null : items[^1];
            return new ClipboardItemPage(items, hasMore, BeforeSortOrder: cursor?.SortOrder, BeforeId: cursor?.Id);
        }
    }

    // The token also validates the service's memory cache without selecting the original BLOB.
    public string? GetThumbnailVersion(long itemId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT version FROM clipboard_thumbnails WHERE item_id=$id";
            cmd.Parameters.AddWithValue("$id", itemId);
            return cmd.ExecuteScalar() as string;
        }
    }

    public ThumbnailInput? GetThumbnailInput(long itemId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var seed = _connection.CreateCommand();
            seed.Transaction = tx;
            seed.CommandText = @"INSERT OR IGNORE INTO clipboard_thumbnails(item_id,version)
                SELECT id,$version FROM clipboard_items WHERE id=$id AND image_png_blob IS NOT NULL";
            seed.Parameters.AddWithValue("$id", itemId);
            seed.Parameters.AddWithValue("$version", Guid.NewGuid().ToString("N"));
            seed.ExecuteNonQuery();
            using var read = _connection.CreateCommand();
            read.Transaction = tx;
            // CASE avoids retrieving the original PNG once a persistent thumbnail exists.
            read.CommandText = @"SELECT t.version,t.png,
                CASE WHEN t.png IS NULL THEN c.image_png_blob ELSE NULL END
                FROM clipboard_thumbnails t JOIN clipboard_items c ON c.id=t.item_id WHERE t.item_id=$id";
            read.Parameters.AddWithValue("$id", itemId);
            ThumbnailInput? result;
            using (var reader = read.ExecuteReader())
                result = reader.Read() ? new ThumbnailInput(reader.GetString(0),
                    reader.IsDBNull(1) ? null : (byte[])reader[1],
                    reader.IsDBNull(2) ? null : (byte[])reader[2]) : null;
            tx.Commit();
            return result;
        }
    }

    public bool SaveThumbnail(long itemId, string version, byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            // Generation happens outside this lock/transaction. A delete or image replacement
            // removes the token, so a late worker cannot resurrect stale cached data.
            cmd.CommandText = "UPDATE clipboard_thumbnails SET png=$png WHERE item_id=$id AND version=$version";
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.Parameters.AddWithValue("$version", version);
            cmd.Parameters.AddWithValue("$png", png);
            return cmd.ExecuteNonQuery() != 0;
        }
    }

    public void InvalidateThumbnail(long itemId, string version)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_thumbnails WHERE item_id=$id AND version=$version";
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.Parameters.AddWithValue("$version", version);
            cmd.ExecuteNonQuery();
        }
    }

    public sealed record ThumbnailInput(string Version, byte[]? ThumbnailPng, byte[]? OriginalPng);

    public List<Folder> GetFolders()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT f.*, (SELECT COUNT(*) FROM folder_items fi WHERE fi.folder_id=f.id) item_count FROM folders f ORDER BY sort_order ASC";
            using var reader = cmd.ExecuteReader();
            var result = new List<Folder>();
            while (reader.Read()) result.Add(new Folder {
                Id = reader.GetInt64(0), Name = reader.GetString(1), SortOrder = reader.GetInt32(2),
                IsLocked = reader.GetInt32(3) != 0, LastUsedAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ItemCount = reader.GetInt32(5)
            });
            return result;
        }
    }

    public List<ClipboardItem> GetFolderItems(long folderId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT ci.* FROM clipboard_items ci JOIN folder_items fi ON fi.item_id=ci.id WHERE fi.folder_id=$folder ORDER BY fi.sort_order DESC";
            cmd.Parameters.AddWithValue("$folder", folderId);
            return ReadItems(cmd);
        }
    }

    public ClipboardItem UpsertClipboard(ClipboardItem item, int maxHistory)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var find = _connection.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT id, created_at, is_favorite, display_text FROM clipboard_items WHERE text_content IS $text AND html_content IS $html AND rtf_content IS $rtf AND image_png_blob IS $image";
            find.Parameters.AddWithValue("$text", (object?)item.TextContent ?? DBNull.Value);
            find.Parameters.AddWithValue("$html", (object?)item.HtmlContent ?? DBNull.Value);
            find.Parameters.AddWithValue("$rtf", (object?)item.RtfContent ?? DBNull.Value);
            find.Parameters.AddWithValue("$image", (object?)item.ImagePng ?? DBNull.Value);
            using var reader = find.ExecuteReader();
            long id = 0; DateTime created = DateTime.UtcNow; bool favorite = item.IsFavorite; string display = item.DisplayText;
            if (reader.Read()) { id = reader.GetInt64(0); created = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); favorite = reader.GetInt32(2) != 0; display = reader.GetString(3); }
            reader.Close();
            using var save = _connection.CreateCommand(); save.Transaction = tx;
            if (id == 0)
            {
                save.CommandText = "INSERT INTO clipboard_items(text_content,html_content,rtf_content,image_png_blob,extra_formats_json,display_text,created_at,last_copied_at,is_favorite) VALUES($text,$html,$rtf,$image,$extra,$display,$created,$copied,$favorite); SELECT last_insert_rowid();";
            }
            else
            {
                save.CommandText = "UPDATE clipboard_items SET image_png_blob=$image,extra_formats_json=$extra,last_copied_at=$copied,is_favorite=$favorite WHERE id=$id";
                save.Parameters.AddWithValue("$id", id);
            }
            save.Parameters.AddWithValue("$text", (object?)item.TextContent ?? DBNull.Value);
            save.Parameters.AddWithValue("$html", (object?)item.HtmlContent ?? DBNull.Value);
            save.Parameters.AddWithValue("$rtf", (object?)item.RtfContent ?? DBNull.Value);
            save.Parameters.AddWithValue("$image", (object?)item.ImagePng ?? DBNull.Value);
            save.Parameters.AddWithValue("$extra", (object?)item.ExtraFormatsJson ?? DBNull.Value);
            save.Parameters.AddWithValue("$display", display);
            save.Parameters.AddWithValue("$created", created.ToString("O"));
            save.Parameters.AddWithValue("$copied", DateTime.UtcNow.ToString("O"));
            save.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
            if (id == 0) id = (long)save.ExecuteScalar()!; else save.ExecuteNonQuery();
            tx.Commit();
            TrimHistory(maxHistory);
            return item with { Id = id, CreatedAt = created, LastCopiedAt = DateTime.UtcNow, IsFavorite = favorite, DisplayText = display };
        }
    }

    public long CreateFolder(string name)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO folders(name,sort_order) VALUES($name,COALESCE((SELECT MAX(sort_order)+1 FROM folders),0)); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$name", name);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void SetFolderLock(long id, bool locked) { lock (_gate) { using var cmd = _connection.CreateCommand(); cmd.CommandText = "UPDATE folders SET is_locked=$locked WHERE id=$id"; cmd.Parameters.AddWithValue("$locked", locked ? 1 : 0); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); } }
    public void SetFavorite(long itemId, bool favorite) { lock (_gate) { using var cmd = _connection.CreateCommand(); cmd.CommandText = "UPDATE clipboard_items SET is_favorite=$favorite WHERE id=$id"; cmd.Parameters.AddWithValue("$favorite", favorite ? 1 : 0); cmd.Parameters.AddWithValue("$id", itemId); cmd.ExecuteNonQuery(); } }
    public void RemoveFromAllFolders(long itemId) { lock (_gate) { using var tx = _connection.BeginTransaction(); using (var delete = _connection.CreateCommand()) { delete.Transaction = tx; delete.CommandText = "DELETE FROM folder_items WHERE item_id=$item"; delete.Parameters.AddWithValue("$item", itemId); delete.ExecuteNonQuery(); } using (var favorite = _connection.CreateCommand()) { favorite.Transaction = tx; favorite.CommandText = "UPDATE clipboard_items SET is_favorite=0 WHERE id=$item"; favorite.Parameters.AddWithValue("$item", itemId); favorite.ExecuteNonQuery(); } tx.Commit(); } }
    public void DeleteItem(long itemId) { lock (_gate) { using var tx = _connection.BeginTransaction(); using (var links = _connection.CreateCommand()) { links.Transaction = tx; links.CommandText = "DELETE FROM folder_items WHERE item_id=$item"; links.Parameters.AddWithValue("$item", itemId); links.ExecuteNonQuery(); } using (var item = _connection.CreateCommand()) { item.Transaction = tx; item.CommandText = "DELETE FROM clipboard_items WHERE id=$item"; item.Parameters.AddWithValue("$item", itemId); item.ExecuteNonQuery(); } tx.Commit(); } }
    public void DeleteFolder(long folderId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using (var links = _connection.CreateCommand()) { links.Transaction = tx; links.CommandText = "DELETE FROM folder_items WHERE folder_id=$folder"; links.Parameters.AddWithValue("$folder", folderId); links.ExecuteNonQuery(); }
            using (var favorites = _connection.CreateCommand()) { favorites.Transaction = tx; favorites.CommandText = "UPDATE clipboard_items SET is_favorite=EXISTS(SELECT 1 FROM folder_items WHERE item_id=clipboard_items.id)"; favorites.ExecuteNonQuery(); }
            using (var folder = _connection.CreateCommand()) { folder.Transaction = tx; folder.CommandText = "DELETE FROM folders WHERE id=$folder"; folder.Parameters.AddWithValue("$folder", folderId); folder.ExecuteNonQuery(); }
            tx.Commit();
        }
    }
    public void AddToFolder(long itemId, long folderId)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow.ToString("O");
            using var tx = _connection.BeginTransaction();
            using (var link = _connection.CreateCommand()) { link.Transaction = tx; link.CommandText = "INSERT OR IGNORE INTO folder_items(folder_id,item_id,sort_order,created_at) VALUES($folder,$item,COALESCE((SELECT MAX(sort_order)+1 FROM folder_items WHERE folder_id=$folder),0),$created)"; link.Parameters.AddWithValue("$folder", folderId); link.Parameters.AddWithValue("$item", itemId); link.Parameters.AddWithValue("$created", now); link.ExecuteNonQuery(); }
            using (var folder = _connection.CreateCommand()) { folder.Transaction = tx; folder.CommandText = "UPDATE folders SET last_used_at=$created WHERE id=$folder"; folder.Parameters.AddWithValue("$folder", folderId); folder.Parameters.AddWithValue("$created", now); folder.ExecuteNonQuery(); }
            using (var favorite = _connection.CreateCommand()) { favorite.Transaction = tx; favorite.CommandText = "UPDATE clipboard_items SET is_favorite=1 WHERE id=$item"; favorite.Parameters.AddWithValue("$item", itemId); favorite.ExecuteNonQuery(); }
            tx.Commit();
        }
    }
    public void MarkFolderUsed(long folderId) { lock (_gate) { using var cmd = _connection.CreateCommand(); cmd.CommandText = "UPDATE folders SET last_used_at=$now WHERE id=$id"; cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id", folderId); cmd.ExecuteNonQuery(); } }
    public void RemoveFromFolder(long itemId, long folderId) { lock (_gate) { using var tx = _connection.BeginTransaction(); using (var cmd = _connection.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "DELETE FROM folder_items WHERE folder_id=$folder AND item_id=$item"; cmd.Parameters.AddWithValue("$folder", folderId); cmd.Parameters.AddWithValue("$item", itemId); cmd.ExecuteNonQuery(); } using (var favorite = _connection.CreateCommand()) { favorite.Transaction = tx; favorite.CommandText = "UPDATE clipboard_items SET is_favorite=EXISTS(SELECT 1 FROM folder_items WHERE item_id=$item) WHERE id=$item"; favorite.Parameters.AddWithValue("$item", itemId); favorite.ExecuteNonQuery(); } tx.Commit(); } }
    public void UpdateItem(ClipboardItem item) { lock (_gate) { using var cmd = _connection.CreateCommand(); cmd.CommandText = "UPDATE clipboard_items SET text_content=$text,display_text=$display WHERE id=$id"; cmd.Parameters.AddWithValue("$text", (object?)item.TextContent ?? DBNull.Value); cmd.Parameters.AddWithValue("$display", item.DisplayText); cmd.Parameters.AddWithValue("$id", item.Id); cmd.ExecuteNonQuery(); } }
    public int ClearHistory()
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using (var links = _connection.CreateCommand())
            {
                links.Transaction = tx;
                links.CommandText = "DELETE FROM folder_items WHERE item_id IN (SELECT id FROM clipboard_items WHERE is_favorite=0)";
                links.ExecuteNonQuery();
            }
            using var items = _connection.CreateCommand();
            items.Transaction = tx;
            items.CommandText = "DELETE FROM clipboard_items WHERE is_favorite=0";
            var deletedCount = items.ExecuteNonQuery();
            tx.Commit();
            return deletedCount;
        }
    }
    public void ReorderFolders(IReadOnlyList<long> ids) { lock (_gate) { using var tx = _connection.BeginTransaction(); for (var i=0;i<ids.Count;i++) { using var cmd=_connection.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="UPDATE folders SET sort_order=$order WHERE id=$id"; cmd.Parameters.AddWithValue("$order",i); cmd.Parameters.AddWithValue("$id",ids[i]); cmd.ExecuteNonQuery(); } tx.Commit(); } }
    public void ReorderFolderItems(long folderId, IReadOnlyList<long> ids)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            // Query IDs only; unloaded rows keep their positions in the full order.
            var existing = new List<long>();
            using (var query = _connection.CreateCommand())
            {
                query.Transaction = tx;
                query.CommandText = "SELECT item_id FROM folder_items WHERE folder_id=$folder ORDER BY sort_order DESC, item_id DESC";
                query.Parameters.AddWithValue("$folder", folderId);
                using var reader = query.ExecuteReader();
                while (reader.Read()) existing.Add(reader.GetInt64(0));
            }
            var existingIds = existing.ToHashSet();
            var supplied = ids.Where(existingIds.Contains).Distinct().ToList();
            var suppliedIds = supplied.ToHashSet();
            var next = 0;
            for (var i = 0; i < existing.Count; i++)
                if (suppliedIds.Contains(existing[i])) existing[i] = supplied[next++];
            // Normalize ranks to support legacy tied sort orders as well.
            for (var i = 0; i < existing.Count; i++)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE folder_items SET sort_order=$order WHERE folder_id=$folder AND item_id=$id";
                cmd.Parameters.AddWithValue("$order", existing.Count - 1 - i);
                cmd.Parameters.AddWithValue("$folder", folderId);
                cmd.Parameters.AddWithValue("$id", existing[i]);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void UpdateDisplayText(long itemId, string displayText)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE clipboard_items SET display_text=$display WHERE id=$id";
            cmd.Parameters.AddWithValue("$display", displayText);
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.ExecuteNonQuery();
        }
    }
    public List<string> GetExclusions() { lock (_gate) { using var cmd=_connection.CreateCommand(); cmd.CommandText="SELECT process_name FROM app_exclusions ORDER BY process_name"; using var r=cmd.ExecuteReader(); var list=new List<string>(); while(r.Read()) list.Add(r.GetString(0)); return list; } }
    public void SetExclusions(IEnumerable<string> names) { lock (_gate) { using var tx=_connection.BeginTransaction(); using(var clear=_connection.CreateCommand()){clear.Transaction=tx;clear.CommandText="DELETE FROM app_exclusions";clear.ExecuteNonQuery();} foreach(var name in names.Distinct(StringComparer.OrdinalIgnoreCase)){using var cmd=_connection.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO app_exclusions(process_name) VALUES($name)";cmd.Parameters.AddWithValue("$name",name);cmd.ExecuteNonQuery();} tx.Commit(); } }

    private void TrimHistory(int max)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand(); cmd.CommandText = "DELETE FROM clipboard_items WHERE is_favorite=0 AND id NOT IN (SELECT id FROM clipboard_items WHERE is_favorite=0 ORDER BY last_copied_at DESC LIMIT $max)"; cmd.Parameters.AddWithValue("$max", Math.Max(1, max)); cmd.ExecuteNonQuery();
        }
    }
    private static List<ClipboardItem> ReadSummaryItems(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<ClipboardItem>();
        while (reader.Read()) list.Add(new ClipboardItem
        {
            Id = reader.GetInt64(0),
            DisplayText = reader.GetString(1),
            CreatedAt = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LastCopiedAt = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            IsFavorite = reader.GetInt32(4) != 0,
            HasImage = reader.GetInt32(5) != 0
        });
        return list;
    }

    private static List<ClipboardItem> ReadFolderSummaryItems(SqliteCommand cmd)
    {
        var list = ReadSummaryItemsWithSortOrder(cmd);
        return list;
    }

    private static List<ClipboardItem> ReadSummaryItemsWithSortOrder(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<ClipboardItem>();
        while (reader.Read()) list.Add(new ClipboardItem
        {
            Id = reader.GetInt64(0),
            DisplayText = reader.GetString(1),
            CreatedAt = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LastCopiedAt = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            IsFavorite = reader.GetInt32(4) != 0,
            HasImage = reader.GetInt32(5) != 0,
            SortOrder = reader.GetInt32(6)
        });
        return list;
    }

    private static ClipboardItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        TextContent = reader.IsDBNull(1) ? null : reader.GetString(1),
        HtmlContent = reader.IsDBNull(2) ? null : reader.GetString(2),
        RtfContent = reader.IsDBNull(3) ? null : reader.GetString(3),
        ImagePng = reader.IsDBNull(4) ? null : (byte[])reader[4],
        HasImage = !reader.IsDBNull(4),
        ExtraFormatsJson = reader.IsDBNull(5) ? null : reader.GetString(5),
        DisplayText = reader.GetString(6),
        CreatedAt = DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        LastCopiedAt = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        IsFavorite = reader.GetInt32(9) != 0
    };

    private static List<ClipboardItem> ReadItems(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<ClipboardItem>();
        while (reader.Read()) list.Add(ReadItem(reader));
        return list;
    }
    public void Dispose() { lock (_gate) { _connection.Dispose(); } }
}
