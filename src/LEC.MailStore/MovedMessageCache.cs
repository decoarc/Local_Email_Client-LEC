// src/LEC.MailStore/MovedMessageCache.cs
using Microsoft.Data.Sqlite;

namespace LEC.MailStore;

/// <summary>
/// Tracks messages "moved" (copied elsewhere, never deleted from their original .dbx —
/// see docs/superpowers/specs/2026-08-04-mover-mensagem-motor-design.md §2) so
/// DbxStore.EnumerateHeadersAsync can hide them from their original folder without ever
/// touching the source .dbx. Same pattern as ScavengeCache: lightweight SQLite under
/// %LOCALAPPDATA%\LEC\, never next to the source profile.
/// </summary>
public sealed class MovedMessageCache : IDisposable
{
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LEC", "moved-cache.db");

    private readonly SqliteConnection _connection;

    public MovedMessageCache(string databasePath)
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
            CREATE TABLE IF NOT EXISTS moved_message (
                profile_path      TEXT    NOT NULL,
                folder_file       TEXT    NOT NULL,
                index_node_offset INTEGER NOT NULL,
                PRIMARY KEY (profile_path, folder_file, index_node_offset)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public HashSet<int> GetMovedOffsets(string profilePath, string folderFile)
    {
        var offsets = new HashSet<int>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT index_node_offset FROM moved_message WHERE profile_path = $p AND folder_file = $f";
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            offsets.Add(reader.GetInt32(0));

        return offsets;
    }

    public void MarkMoved(string profilePath, string folderFile, int indexNodeOffset)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO moved_message (profile_path, folder_file, index_node_offset)
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
        cmd.CommandText = "DELETE FROM moved_message WHERE profile_path = $p AND folder_file = $f";
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
