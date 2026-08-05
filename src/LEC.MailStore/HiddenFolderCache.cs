// src/LEC.MailStore/HiddenFolderCache.cs
using Microsoft.Data.Sqlite;

namespace LEC.MailStore;

/// <summary>
/// Tracks folders "deleted" (hidden from DbxStore.Folders, never removed from Folders.dbx
/// or any message .dbx — see docs/superpowers/specs/2026-08-05-apagar-mover-pastas-motor-design.md
/// §3) so DbxStore.Open/DeleteFolderAsync can hide a whole subtree without ever touching a
/// file on disk. Same pattern as DeletedMessageCache/MovedMessageCache: lightweight SQLite
/// under %LOCALAPPDATA%\LEC\, never next to the source profile.
///
/// Unlike those two caches, this one is keyed by folder_id rather than an offset into a
/// file, and has no ClearFolder/purge method: CreateFolderAsync and MoveFolderAsync always
/// preserve every existing folder's FolderId when they rewrite Folders.dbx (they only add
/// or retarget one entry's ParentId), so a FolderId marked hidden here can never later end
/// up identifying a different folder the way a rebuilt message's index_node_offset can.
/// </summary>
public sealed class HiddenFolderCache : IDisposable
{
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LEC", "hidden-folder-cache.db");

    private readonly SqliteConnection _connection;

    public HiddenFolderCache(string databasePath)
    {
        string? dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS hidden_folder (
                profile_path TEXT    NOT NULL,
                folder_id    INTEGER NOT NULL,
                PRIMARY KEY (profile_path, folder_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public HashSet<int> GetHiddenFolderIds(string profilePath)
    {
        var ids = new HashSet<int>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT folder_id FROM hidden_folder WHERE profile_path = $p";
        cmd.Parameters.AddWithValue("$p", profilePath);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));

        return ids;
    }

    public void MarkHidden(string profilePath, int folderId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO hidden_folder (profile_path, folder_id)
            VALUES ($p, $id)
            """;
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
