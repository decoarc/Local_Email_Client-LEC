// src/LEC.MailStore/ScavengeCache.cs
using Microsoft.Data.Sqlite;

namespace LEC.MailStore;

public sealed record ScavengeCacheEntry(IReadOnlyList<CachedRecoveredItem> Items, bool IsVisible);

/// <summary>
/// Lightweight SQLite cache for scavenge results, so they survive an app restart
/// without a full library (Fase 4). Always lives under %LOCALAPPDATA%\LEC\, never
/// next to the source profile -- see ARQUITETURA.md §2 and the Fase 3 design doc §6.
/// Schema deliberately minimal: Fase 4 can extend it without migrating any data.
/// </summary>
public sealed class ScavengeCache : IDisposable
{
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LEC", "scavenge-cache.db");

    private readonly SqliteConnection _connection;

    public ScavengeCache(string databasePath)
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
            CREATE TABLE IF NOT EXISTS scan (
                profile_path TEXT NOT NULL,
                folder_file  TEXT NOT NULL,
                scanned_at   INTEGER NOT NULL,
                is_visible   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (profile_path, folder_file)
            );
            CREATE TABLE IF NOT EXISTS recovered_item (
                profile_path   TEXT NOT NULL,
                folder_file    TEXT NOT NULL,
                chain_offset   INTEGER NOT NULL,
                byte_length    INTEGER NOT NULL,
                kind           TEXT NOT NULL,
                subject        TEXT,
                sender_name    TEXT,
                sender_address TEXT,
                date_utc       INTEGER,
                PRIMARY KEY (profile_path, folder_file, chain_offset)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public ScavengeCacheEntry? GetScan(string profilePath, string folderFile)
    {
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT is_visible FROM scan WHERE profile_path = $p AND folder_file = $f";
            cmd.Parameters.AddWithValue("$p", profilePath);
            cmd.Parameters.AddWithValue("$f", folderFile);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            bool isVisible = reader.GetInt64(0) != 0;
            return new ScavengeCacheEntry(GetItems(profilePath, folderFile), isVisible);
        }
    }

    private List<CachedRecoveredItem> GetItems(string profilePath, string folderFile)
    {
        var items = new List<CachedRecoveredItem>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT chain_offset, byte_length, kind, subject, sender_name, sender_address, date_utc
            FROM recovered_item WHERE profile_path = $p AND folder_file = $f
            """;
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new CachedRecoveredItem(
                ChainOffset: reader.GetInt32(0),
                ByteLength: reader.GetInt64(1),
                Kind: Enum.Parse<RecoveredMessageKind>(reader.GetString(2)),
                Subject: reader.IsDBNull(3) ? null : reader.GetString(3),
                SenderName: reader.IsDBNull(4) ? null : reader.GetString(4),
                SenderAddress: reader.IsDBNull(5) ? null : reader.GetString(5),
                DateUtc: reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)).UtcDateTime));
        }
        return items;
    }

    public void SaveScan(string profilePath, string folderFile, IReadOnlyList<CachedRecoveredItem> items)
    {
        using var transaction = _connection.BeginTransaction();

        using (var delItems = _connection.CreateCommand())
        {
            delItems.Transaction = transaction;
            delItems.CommandText = "DELETE FROM recovered_item WHERE profile_path = $p AND folder_file = $f";
            delItems.Parameters.AddWithValue("$p", profilePath);
            delItems.Parameters.AddWithValue("$f", folderFile);
            delItems.ExecuteNonQuery();
        }

        using (var upsertScan = _connection.CreateCommand())
        {
            upsertScan.Transaction = transaction;
            upsertScan.CommandText = """
                INSERT INTO scan (profile_path, folder_file, scanned_at, is_visible)
                VALUES ($p, $f, $t, 0)
                ON CONFLICT(profile_path, folder_file) DO UPDATE SET scanned_at = $t
                """;
            upsertScan.Parameters.AddWithValue("$p", profilePath);
            upsertScan.Parameters.AddWithValue("$f", folderFile);
            upsertScan.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            upsertScan.ExecuteNonQuery();
        }

        foreach (var item in items)
        {
            using var insertItem = _connection.CreateCommand();
            insertItem.Transaction = transaction;
            insertItem.CommandText = """
                INSERT INTO recovered_item
                    (profile_path, folder_file, chain_offset, byte_length, kind, subject, sender_name, sender_address, date_utc)
                VALUES ($p, $f, $offset, $len, $kind, $subject, $senderName, $senderAddr, $date)
                """;
            insertItem.Parameters.AddWithValue("$p", profilePath);
            insertItem.Parameters.AddWithValue("$f", folderFile);
            insertItem.Parameters.AddWithValue("$offset", item.ChainOffset);
            insertItem.Parameters.AddWithValue("$len", item.ByteLength);
            insertItem.Parameters.AddWithValue("$kind", item.Kind.ToString());
            insertItem.Parameters.AddWithValue("$subject", (object?)item.Subject ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("$senderName", (object?)item.SenderName ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("$senderAddr", (object?)item.SenderAddress ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("$date", item.DateUtc is { } d
                ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds()
                : (object)DBNull.Value);
            insertItem.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Drops a folder's whole cached scan — the scan row and every recovered item. Called
    /// after that folder's .dbx is rebuilt from scratch (see <see cref="DbxStore.AddMessageAsync"/>):
    /// recovered items are keyed on <c>chain_offset</c>, a raw file offset that a rebuild
    /// invalidates, so the cached scan no longer describes the bytes it was produced from.
    /// The folder simply reverts to "never scanned" and can be re-scavenged.
    /// </summary>
    public void ClearScan(string profilePath, string folderFile)
    {
        using var transaction = _connection.BeginTransaction();

        foreach (string table in new[] { "recovered_item", "scan" })
        {
            using var del = _connection.CreateCommand();
            del.Transaction = transaction;
            del.CommandText = $"DELETE FROM {table} WHERE profile_path = $p AND folder_file = $f";
            del.Parameters.AddWithValue("$p", profilePath);
            del.Parameters.AddWithValue("$f", folderFile);
            del.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SetVisibility(string profilePath, string folderFile, bool visible)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE scan SET is_visible = $v WHERE profile_path = $p AND folder_file = $f";
        cmd.Parameters.AddWithValue("$v", visible ? 1 : 0);
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
