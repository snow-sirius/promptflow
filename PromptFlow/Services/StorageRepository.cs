using System.Text.Json;
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

    private void Initialize()
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
CREATE TABLE IF NOT EXISTS app_exclusions (process_name TEXT PRIMARY KEY);
CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
INSERT INTO schema_version(version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);";
        cmd.ExecuteNonQuery();
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
                IsLocked = reader.GetInt32(3) != 0, LastUsedAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
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
            if (reader.Read()) { id = reader.GetInt64(0); created = DateTime.Parse(reader.GetString(1)); favorite = reader.GetInt32(2) != 0; display = reader.GetString(3); }
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
    public void MarkFolderUsed(long folderId) { using var cmd = _connection.CreateCommand(); cmd.CommandText = "UPDATE folders SET last_used_at=$now WHERE id=$id"; cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id", folderId); cmd.ExecuteNonQuery(); }
    public void RemoveFromFolder(long itemId, long folderId) { lock (_gate) { using var tx = _connection.BeginTransaction(); using (var cmd = _connection.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "DELETE FROM folder_items WHERE folder_id=$folder AND item_id=$item"; cmd.Parameters.AddWithValue("$folder", folderId); cmd.Parameters.AddWithValue("$item", itemId); cmd.ExecuteNonQuery(); } using (var favorite = _connection.CreateCommand()) { favorite.Transaction = tx; favorite.CommandText = "UPDATE clipboard_items SET is_favorite=EXISTS(SELECT 1 FROM folder_items WHERE item_id=$item) WHERE id=$item"; favorite.Parameters.AddWithValue("$item", itemId); favorite.ExecuteNonQuery(); } tx.Commit(); } }
    public void UpdateItem(ClipboardItem item) { using var cmd = _connection.CreateCommand(); cmd.CommandText = "UPDATE clipboard_items SET text_content=$text,display_text=$display WHERE id=$id"; cmd.Parameters.AddWithValue("$text", (object?)item.TextContent ?? DBNull.Value); cmd.Parameters.AddWithValue("$display", item.DisplayText); cmd.Parameters.AddWithValue("$id", item.Id); cmd.ExecuteNonQuery(); }
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
    public void ReorderFolders(IReadOnlyList<long> ids) { using var tx = _connection.BeginTransaction(); for (var i=0;i<ids.Count;i++) { using var cmd=_connection.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="UPDATE folders SET sort_order=$order WHERE id=$id"; cmd.Parameters.AddWithValue("$order",i); cmd.Parameters.AddWithValue("$id",ids[i]); cmd.ExecuteNonQuery(); } tx.Commit(); }
    public void ReorderFolderItems(long folderId, IReadOnlyList<long> ids) { using var tx = _connection.BeginTransaction(); for (var i=0;i<ids.Count;i++) { using var cmd=_connection.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="UPDATE folder_items SET sort_order=$order WHERE folder_id=$folder AND item_id=$id"; cmd.Parameters.AddWithValue("$order",ids.Count - 1 - i); cmd.Parameters.AddWithValue("$folder",folderId); cmd.Parameters.AddWithValue("$id",ids[i]); cmd.ExecuteNonQuery(); } tx.Commit(); }
    public List<string> GetExclusions() { using var cmd=_connection.CreateCommand(); cmd.CommandText="SELECT process_name FROM app_exclusions ORDER BY process_name"; using var r=cmd.ExecuteReader(); var list=new List<string>(); while(r.Read()) list.Add(r.GetString(0)); return list; }
    public void SetExclusions(IEnumerable<string> names) { using var tx=_connection.BeginTransaction(); using(var clear=_connection.CreateCommand()){clear.Transaction=tx;clear.CommandText="DELETE FROM app_exclusions";clear.ExecuteNonQuery();} foreach(var name in names.Distinct(StringComparer.OrdinalIgnoreCase)){using var cmd=_connection.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO app_exclusions(process_name) VALUES($name)";cmd.Parameters.AddWithValue("$name",name);cmd.ExecuteNonQuery();} tx.Commit(); }

    private void TrimHistory(int max)
    {
        using var cmd = _connection.CreateCommand(); cmd.CommandText = "DELETE FROM clipboard_items WHERE is_favorite=0 AND id NOT IN (SELECT id FROM clipboard_items WHERE is_favorite=0 ORDER BY last_copied_at DESC LIMIT $max)"; cmd.Parameters.AddWithValue("$max", Math.Max(1, max)); cmd.ExecuteNonQuery();
    }
    private static List<ClipboardItem> ReadItems(SqliteCommand cmd) { using var r=cmd.ExecuteReader(); var list=new List<ClipboardItem>(); while(r.Read()) list.Add(new ClipboardItem { Id=r.GetInt64(0), TextContent=r.IsDBNull(1)?null:r.GetString(1), HtmlContent=r.IsDBNull(2)?null:r.GetString(2), RtfContent=r.IsDBNull(3)?null:r.GetString(3), ImagePng=r.IsDBNull(4)?null:(byte[])r[4], ExtraFormatsJson=r.IsDBNull(5)?null:r.GetString(5), DisplayText=r.GetString(6), CreatedAt=DateTime.Parse(r.GetString(7)), LastCopiedAt=DateTime.Parse(r.GetString(8)), IsFavorite=r.GetInt32(9)!=0 }); return list; }
    public void Dispose() => _connection.Dispose();
}
