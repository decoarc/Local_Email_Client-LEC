// src/LEC.MailStore/DeletedMessageCache.cs
using Microsoft.Data.Sqlite;

namespace LEC.MailStore;

/// <summary>
/// Tracks messages "deleted" (hidden from their folder, never removed from their original
/// .dbx — see docs/superpowers/specs/2026-08-04-apagar-mensagem-design.md §2) so
/// DbxStore.EnumerateHeadersAsync can hide them without ever touching the source .dbx.
/// Same pattern as MovedMessageCache/ScavengeCache: lightweight SQLite under
/// %LOCALAPPDATA%\LEC\, never next to the source profile. Kept deliberately separate from
/// MovedMessageCache even though the mechanics are identical -- "deleted" and "moved" stay
/// distinct concepts in the stored data, not just in the UI.
/// </summary>
public sealed class DeletedMessageCache : IDisposable
{
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LEC", "deleted-cache.db");

    private readonly SqliteConnection _connection;

    public DeletedMessageCache(string databasePath)
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
            CREATE TABLE IF NOT EXISTS deleted_message (
                profile_path      TEXT    NOT NULL,
                folder_file       TEXT    NOT NULL,
                index_node_offset INTEGER NOT NULL,
                PRIMARY KEY (profile_path, folder_file, index_node_offset)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public HashSet<int> GetDeletedOffsets(string profilePath, string folderFile)
    {
        var offsets = new HashSet<int>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT index_node_offset FROM deleted_message WHERE profile_path = $p AND folder_file = $f";
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            offsets.Add(reader.GetInt32(0));

        return offsets;
    }

    public void MarkDeleted(string profilePath, string folderFile, int indexNodeOffset)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO deleted_message (profile_path, folder_file, index_node_offset)
            VALUES ($p, $f, $offset)
            """;
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);
        cmd.Parameters.AddWithValue("$offset", indexNodeOffset);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Drops every hidden-message row for one folder. Called after that folder's .dbx is
    /// rebuilt from scratch (see <see cref="DbxStore.AddMessageAsync"/>): a rebuild renumbers
    /// every info-node offset in the file, so rows keyed on the old offsets no longer identify
    /// the messages they were recorded for — keeping them would silently hide whatever
    /// unrelated message happens to land on a matching offset in the new layout.
    /// </summary>
    public void ClearFolder(string profilePath, string folderFile)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM deleted_message WHERE profile_path = $p AND folder_file = $f";
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
