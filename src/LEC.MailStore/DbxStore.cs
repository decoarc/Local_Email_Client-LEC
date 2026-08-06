// src/LEC.MailStore/DbxStore.cs
using System.Linq;
using System.Runtime.CompilerServices;
using LEC.DbxFormat;
using Microsoft.Data.Sqlite;

namespace LEC.MailStore;

public sealed class DbxStore : IMailStore
{
    private readonly string _profileDirectory;
    private readonly Dictionary<int, string?> _backingFileNames;
    private readonly Dictionary<int, DbxEmailFile> _openFolders = new();
    private readonly Dictionary<int, Dictionary<MessageRef, DbxMessageInfo>> _messagesByFolder = new();
    private readonly ScavengeCache _scavengeCache;
    private readonly Dictionary<int, ScavengeStatus> _scavengeStatusByFolder = new();
    private readonly Dictionary<int, IReadOnlyList<CachedRecoveredItem>> _recoveredItemsByFolder = new();
    private readonly Dictionary<int, Dictionary<MessageRef, int>> _recoveredByFolder = new();

    private readonly List<StoreFolder> _folders;
    public IReadOnlyList<StoreFolder> Folders => _folders;
    public StoreHealth Health { get; }

    private readonly MovedMessageCache _movedMessageCache;
    private readonly Dictionary<int, HashSet<int>> _hiddenOffsetsByFolder = new();

    private readonly DeletedMessageCache _deletedMessageCache;
    private readonly Dictionary<int, HashSet<int>> _deletedOffsetsByFolder = new();

    private readonly HiddenFolderCache _hiddenFolderCache;

    private readonly FileBackupStore _backupStore;

    private DbxStore(string profileDirectory, List<StoreFolder> folders, Dictionary<int, string?> backingFileNames, StoreHealth health, ScavengeCache scavengeCache, MovedMessageCache movedMessageCache, DeletedMessageCache deletedMessageCache, HiddenFolderCache hiddenFolderCache, FileBackupStore backupStore)
    {
        _profileDirectory = profileDirectory;
        _folders = folders;
        _backingFileNames = backingFileNames;
        Health = health;
        _scavengeCache = scavengeCache;
        _movedMessageCache = movedMessageCache;
        _deletedMessageCache = deletedMessageCache;
        _hiddenFolderCache = hiddenFolderCache;
        _backupStore = backupStore;
    }

    private static readonly char[] InvalidFileNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    // Windows redirects file I/O for these names (with or without an extension — "CON.txt"
    // is just as reserved as "CON") to a device instead of a real file, so a sanitized
    // folder name matching one of these, case-insensitively, must be rejected up front:
    // otherwise Folders.dbx would end up with an entry pointing at something that isn't a
    // real message store.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static bool IsReservedDeviceName(string name)
    {
        int dot = name.IndexOf('.');
        string stem = dot < 0 ? name : name[..dot];
        return ReservedDeviceNames.Contains(stem);
    }

    /// <summary>
    /// Creates a new folder: a "&lt;name&gt;.dbx" backing file plus an entry added into a
    /// from-scratch rewrite of Folders.dbx (see <see cref="DbxFolderFileWriter.Rebuild"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when: <paramref name="parentId"/> does not match any existing folder; the
    /// sanitized <paramref name="name"/> is blank; the sanitized name is a Windows-reserved
    /// device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9, case-insensitive); a sibling folder
    /// (same parent) already has this name (case-insensitive); the backing "&lt;name&gt;.dbx"
    /// file already exists in this profile; or Folders.dbx on disk is damaged
    /// (<c>Health != StoreHealth.Ok</c>), since rebuilding it in that state would silently
    /// discard whatever folders this reader couldn't parse.
    /// </exception>
    public Task<StoreFolder> CreateFolderAsync(string name, int? parentId, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (parentId is int pid && !_folders.Any(f => f.Id == pid))
                throw new InvalidOperationException($"Pasta-mãe {pid} não encontrada.");

            string sanitizedName = new string(name.Where(c => Array.IndexOf(InvalidFileNameChars, c) < 0).ToArray()).Trim();
            if (sanitizedName.Length == 0)
                throw new InvalidOperationException($"Nome de pasta inválido: \"{name}\".");

            if (IsReservedDeviceName(sanitizedName))
                throw new InvalidOperationException($"\"{sanitizedName}\" é um nome reservado do Windows e não pode ser usado como nome de pasta.");

            if (_folders.Any(f => f.ParentId == parentId && string.Equals(f.Name, sanitizedName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Já existe uma pasta chamada \"{sanitizedName}\" no mesmo nível.");

            string fileName = sanitizedName + ".dbx";
            string filePath = Path.Combine(_profileDirectory, fileName);
            if (File.Exists(filePath))
                throw new InvalidOperationException($"Já existe um arquivo \"{fileName}\" neste perfil.");

            string foldersDbxPath = Path.Combine(_profileDirectory, "Folders.dbx");

            List<DbxFolderWriteEntry> entries;
            using (var current = DbxFolderFile.Open(foldersDbxPath))
            {
                // Must be checked before touching current.Folders at all, and before any
                // file (including the new backing .dbx) is created: DbxFolderFile.Open
                // silently drops folders it couldn't parse when the file is damaged, so
                // rebuilding from a Partial-health read would permanently discard them.
                if (current.Health != StoreHealth.Ok)
                    throw new InvalidOperationException(
                        "Folders.dbx está danificado; criar pastas reescreveria o arquivo e descartaria as pastas ilegíveis.");

                entries = current.Folders
                    .Select(f => new DbxFolderWriteEntry(f.FolderId, f.ParentId, f.Name ?? "", f.FileName,
                        f.Flags, f.MessageCount, f.SpecialFolderType))
                    .ToList();
            }

            int newId = entries.Count == 0 ? 1 : entries.Max(e => e.FolderId) + 1;

            DbxEmailFileWriter.CreateEmpty(filePath);
            try
            {
                _backupStore.BeforeRewrite(_profileDirectory, "Folders.dbx");

                entries.Add(new DbxFolderWriteEntry(newId, parentId ?? -1, sanitizedName, fileName));
                DbxFolderFileWriter.Rebuild(foldersDbxPath, entries);

                var created = new StoreFolder
                {
                    Id = newId,
                    ParentId = parentId,
                    Name = sanitizedName,
                    SpecialFolderKind = SpecialFolderKind.None,
                    MessageCount = 0,
                    IsLoadable = true,
                    HasBackingFile = true,
                };
                // Not thread-safe: _folders/_backingFileNames are plain collections, not
                // synchronized. The current UI caller (MainWindow.CreateFolderAsync)
                // disables the affected tree for the duration of this call specifically to
                // prevent a concurrent read from a UI click racing this write — that
                // mitigation lives at the call site, not here, so any future caller of
                // CreateFolderAsync must provide the same guarantee (or this needs a real
                // lock around these two collections).
                _folders.Add(created);
                _backingFileNames[newId] = fileName;

                return created;
            }
            catch
            {
                // The backing .dbx file was created above; if anything after that point
                // failed, remove it so CreateFolderAsync's own File.Exists guard doesn't
                // permanently block re-creating a folder with this name.
                try { File.Delete(filePath); } catch { /* best-effort cleanup */ }
                throw;
            }
        }, ct);
    }

    /// <summary>
    /// Permanently deletes a folder — and every folder in its subtree — removing their
    /// entries from Folders.dbx (a from-scratch rewrite, same pattern as MoveFolderAsync)
    /// and deleting each folder's own backing .dbx file from disk.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown before any mutation when: <paramref name="folderId"/> does not match any
    /// existing (visible) folder; the folder, or any folder in its subtree, is a special
    /// folder (SpecialFolderKind != None) — special folders can never be deleted, including
    /// as a side effect of deleting an ancestor; or Folders.dbx on disk is damaged
    /// (Health != StoreHealth.Ok), since rebuilding it in that state would silently discard
    /// whatever folders this reader couldn't parse. Nothing is removed/rewritten if any of
    /// these fire.
    /// <para>
    /// Thrown <em>after</em> the folder has already been permanently removed (Folders.dbx
    /// already rewritten without it, in-memory state already updated) when one or more of
    /// its subtree's backing files could not be deleted from disk (e.g. locked by another
    /// process) — lists which files failed. <see cref="Folders"/> no longer contains the
    /// folder regardless of this exception.
    /// </para>
    /// </exception>
    /// <remarks>
    /// Not idempotent: calling this twice for the same id throws on the second call,
    /// because the first call already removed it from _folders.
    /// Not thread-safe, same as every other mutating DbxStore member.
    /// </remarks>
    public Task DeleteFolderAsync(int folderId, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_folders.Any(f => f.Id == folderId))
                throw new InvalidOperationException($"Pasta {folderId} não encontrada.");

            var subtreeIds = CollectSubtreeIds(folderId);

            if (subtreeIds.Any(id => _folders.Single(f => f.Id == id).SpecialFolderKind != SpecialFolderKind.None))
                throw new InvalidOperationException("Pastas especiais não podem ser apagadas.");

            string foldersDbxPath = Path.Combine(_profileDirectory, "Folders.dbx");

            List<DbxFolderWriteEntry> entries;
            using (var current = DbxFolderFile.Open(foldersDbxPath))
            {
                if (current.Health != StoreHealth.Ok)
                    throw new InvalidOperationException(
                        "Folders.dbx está danificado; apagar uma pasta reescreveria o arquivo e descartaria as pastas ilegíveis.");

                entries = current.Folders
                    .Where(f => !subtreeIds.Contains(f.FolderId))
                    .Select(f => new DbxFolderWriteEntry(f.FolderId, f.ParentId, f.Name ?? "", f.FileName,
                        f.Flags, f.MessageCount, f.SpecialFolderType))
                    .ToList();
            }

            string pristinePath = foldersDbxPath + ".original";
            if (!File.Exists(pristinePath))
                File.Copy(foldersDbxPath, pristinePath);
            File.Copy(foldersDbxPath, foldersDbxPath + ".bak", overwrite: true);

            DbxFolderFileWriter.Rebuild(foldersDbxPath, entries);
            // The folder is permanently gone from Folders.dbx from this point on -- every
            // step below is best-effort disk/cache cleanup around that already-final change.

            var fileNames = new List<string>();
            foreach (int id in subtreeIds)
            {
                if (_backingFileNames.TryGetValue(id, out string? fileName) && fileName is not null)
                    fileNames.Add(fileName);

                _folders.RemoveAll(f => f.Id == id);
                _backingFileNames.Remove(id);

                if (_openFolders.Remove(id, out var stale))
                    stale.Dispose();
                _messagesByFolder.Remove(id);
                _hiddenOffsetsByFolder.Remove(id);
                _deletedOffsetsByFolder.Remove(id);
                _scavengeStatusByFolder.Remove(id);
                _recoveredItemsByFolder.Remove(id);
                _recoveredByFolder.Remove(id);
            }

            var failures = new List<string>();
            foreach (string fileName in fileNames)
            {
                // These caches key on (profilePath, fileName), not FolderId -- without this,
                // a future folder that reuses the same sanitized name (same fileName) would
                // silently inherit this deleted folder's hidden/scavenged offsets.
                _scavengeCache.ClearScan(_profileDirectory, fileName);
                _movedMessageCache.ClearFolder(_profileDirectory, fileName);
                _deletedMessageCache.ClearFolder(_profileDirectory, fileName);

                // Best-effort: Folders.dbx has already been rewritten by this point, so the
                // folder is permanently gone from the tree either way. A file that can't be
                // deleted (e.g. locked by another process) becomes an orphan on disk instead
                // of blocking the rest of the subtree's cleanup.
                foreach (string candidate in new[] { fileName, fileName + ".bak", fileName + ".original" })
                {
                    try
                    {
                        File.Delete(Path.Combine(_profileDirectory, candidate)); // no-op if it never existed
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failures.Add($"{candidate} ({ex.Message})");
                    }
                }
            }

            if (failures.Count > 0)
                throw new InvalidOperationException(
                    $"Pasta apagada, mas não foi possível remover do disco: {string.Join(", ", failures)}.");
        }, ct);
    }

    // folderId plus every folder transitively reachable from it via ParentId. A HashSet
    // doubling as the visited-set makes this safe even against a pre-existing cyclic/dangling
    // ParentId chain (the same kind FolderTreeViewModel.Build already has to tolerate on the
    // UI side) -- Add returning false stops the walk from ever revisiting an id twice.
    private HashSet<int> CollectSubtreeIds(int rootId)
    {
        var result = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(rootId);

        while (stack.Count > 0)
        {
            int id = stack.Pop();
            if (!result.Add(id))
                continue;

            foreach (var child in _folders.Where(f => f.ParentId == id))
                stack.Push(child.Id);
        }

        return result;
    }

    /// <summary>
    /// Moves a folder to a new parent within this store — a from-scratch rewrite of
    /// Folders.dbx (see DbxFolderFileWriter.Rebuild) that changes only the moved folder's
    /// ParentId entry. Every descendant folder's own entry is untouched: since the tree is
    /// defined entirely by ParentId, the whole subtree "comes along" without needing any
    /// other entry to change.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when: <paramref name="folderId"/> does not match any existing folder; the
    /// folder is special (SpecialFolderKind != None); <paramref name="newParentId"/> is
    /// non-null and does not match any existing folder; <paramref name="newParentId"/> is
    /// the folder itself or one of its own descendants (would create a cycle); a sibling
    /// folder under <paramref name="newParentId"/> already has this folder's name
    /// (case-insensitive, same rule as CreateFolderAsync); or Folders.dbx on disk is
    /// damaged (Health != StoreHealth.Ok).
    /// </exception>
    /// <remarks>
    /// Not thread-safe, same as every other mutating DbxStore member.
    /// </remarks>
    public Task MoveFolderAsync(int folderId, int? newParentId, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var folder = _folders.FirstOrDefault(f => f.Id == folderId)
                ?? throw new InvalidOperationException($"Pasta {folderId} não encontrada.");

            if (folder.SpecialFolderKind != SpecialFolderKind.None)
                throw new InvalidOperationException("Pastas especiais não podem ser movidas.");

            if (newParentId is int pid && !_folders.Any(f => f.Id == pid))
                throw new InvalidOperationException($"Pasta-mãe {pid} não encontrada.");

            // Cycle guard: walk up from newParentId via ParentId; folderId must never appear.
            // The visited-set also protects against looping forever if the tree already has
            // an unrelated pre-existing cycle (same tolerance CollectSubtreeIds needs).
            var visited = new HashSet<int>();
            int? cursor = newParentId;
            while (cursor is int cursorId)
            {
                if (cursorId == folderId)
                    throw new InvalidOperationException(
                        "Não é possível mover uma pasta para dentro dela mesma ou de uma pasta descendente.");
                if (!visited.Add(cursorId))
                    break;
                cursor = _folders.FirstOrDefault(f => f.Id == cursorId)?.ParentId;
            }

            if (_folders.Any(f => f.Id != folderId && f.ParentId == newParentId
                    && string.Equals(f.Name, folder.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Já existe uma pasta chamada \"{folder.Name}\" no mesmo nível.");

            string foldersDbxPath = Path.Combine(_profileDirectory, "Folders.dbx");

            List<DbxFolderWriteEntry> entries;
            using (var current = DbxFolderFile.Open(foldersDbxPath))
            {
                if (current.Health != StoreHealth.Ok)
                    throw new InvalidOperationException(
                        "Folders.dbx está danificado; mover uma pasta reescreveria o arquivo e descartaria as pastas ilegíveis.");

                entries = current.Folders
                    .Select(f => new DbxFolderWriteEntry(
                        f.FolderId,
                        f.FolderId == folderId ? (newParentId ?? -1) : f.ParentId,
                        f.Name ?? "", f.FileName, f.Flags, f.MessageCount, f.SpecialFolderType))
                    .ToList();
            }

            _backupStore.BeforeRewrite(_profileDirectory, "Folders.dbx");

            DbxFolderFileWriter.Rebuild(foldersDbxPath, entries);

            int index = _folders.FindIndex(f => f.Id == folderId);
            _folders[index] = new StoreFolder
            {
                Id = folder.Id,
                ParentId = newParentId,
                Name = folder.Name,
                SpecialFolderKind = folder.SpecialFolderKind,
                MessageCount = folder.MessageCount,
                IsLoadable = folder.IsLoadable,
                HasBackingFile = folder.HasBackingFile,
            };
        }, ct);
    }

    /// <summary>
    /// Appends a message to a folder — a from-scratch rewrite of that folder's backing
    /// .dbx (see DbxEmailFileWriter.Rebuild), same "flat tree, ≤255 items" approach as
    /// CreateFolderAsync/Folders.dbx.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when: <paramref name="folderId"/> does not match any existing folder; that
    /// folder has no backing file; or the backing .dbx is damaged (<c>Health != StoreHealth.Ok</c>),
    /// since rebuilding it in that state would silently discard whatever messages this
    /// reader couldn't parse. If the folder already has 255 messages, the underlying
    /// <see cref="DbxEmailFileWriter.Rebuild"/> throws <see cref="ArgumentException"/> instead
    /// — not caught or rewrapped here.
    /// </exception>
    /// <remarks>
    /// Not thread-safe: this mutates shared, unsynchronized in-memory state (_openFolders,
    /// _messagesByFolder, _hiddenOffsetsByFolder, _deletedOffsetsByFolder,
    /// _scavengeStatusByFolder, _recoveredItemsByFolder, _recoveredByFolder) as well as the
    /// folder's .dbx on disk. Concurrent calls against the same DbxStore instance —
    /// including a concurrent EnumerateHeadersAsync/OpenMessageAsync on the same folder —
    /// must be serialized by the caller, exactly as CreateFolderAsync already requires.
    /// </remarks>
    public Task AddMessageAsync(int folderId, byte[] rawMessage, string? subject, string? senderName,
        string? senderAddress, string? messageId, DateTime? dateUtc, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_folders.Any(f => f.Id == folderId))
                throw new InvalidOperationException($"Pasta {folderId} não encontrada.");

            if (!_backingFileNames.TryGetValue(folderId, out var fileName) || fileName is null)
                throw new InvalidOperationException($"Pasta {folderId} não tem arquivo de apoio.");

            string filePath = Path.Combine(_profileDirectory, fileName);

            List<DbxMessageWriteEntry> entries;
            using (var current = DbxEmailFile.Open(filePath))
            {
                if (current.Health != StoreHealth.Ok)
                    throw new InvalidOperationException(
                        $"\"{fileName}\" está danificado; adicionar uma mensagem reescreveria o arquivo e descartaria as mensagens ilegíveis.");

                entries = current.Messages
                    .Select(info => new DbxMessageWriteEntry(
                        current.ReadMessageRaw(info, out _), info.Subject, info.SenderName, info.SenderAddress,
                        info.MessageId, info.SendTimeUtc, info.Flags,
                        info.ReceiverName, info.ReceiverAddress, info.ReceiveTimeUtc))
                    .ToList();
            }

            string pristinePath = filePath + ".original";
            if (!File.Exists(pristinePath))
                File.Copy(filePath, pristinePath);
            File.Copy(filePath, filePath + ".bak", overwrite: true);

            // EnumerateHeadersAsync caches an open DbxEmailFile (FileStream + memory-mapped
            // section) per folder in _openFolders, released only on Dispose(). If this folder
            // was ever browsed -- the normal path when the user clicks the destination before
            // moving into it -- that handle still locks the file and Rebuild's final File.Move
            // fails with UnauthorizedAccessException. Evict it (and the DbxMessageInfo map
            // keyed on the about-to-be-invalidated offsets) before rewriting the file.
            if (_openFolders.Remove(folderId, out var stale))
                stale.Dispose();
            _messagesByFolder.Remove(folderId);

            entries.Add(new DbxMessageWriteEntry(rawMessage, subject, senderName, senderAddress, messageId, dateUtc));
            DbxEmailFileWriter.Rebuild(filePath, entries);

            // Rebuild rewrote the file from scratch, so every byte offset inside it changed.
            // All three caches key on offsets into this exact file -- MovedMessageCache and
            // DeletedMessageCache on index_node_offset, ScavengeCache on
            // recovered_item.chain_offset -- so every cached row for this folder is now
            // meaningless, and a stale offset that coincidentally matches something in the
            // new layout is *silent* corruption (a wrong message hidden, a recovered item
            // pointing at unrelated bytes). Purge rather than try to preserve: losing "this
            // was moved/deleted" back into view is an accepted, visible, inspectable
            // tradeoff. Re-keying hiding on a rebuild-stable identity is a deliberately
            // deferred design change.
            _movedMessageCache.ClearFolder(_profileDirectory, fileName);
            _hiddenOffsetsByFolder.Remove(folderId);

            _deletedMessageCache.ClearFolder(_profileDirectory, fileName);
            _deletedOffsetsByFolder.Remove(folderId);

            _scavengeCache.ClearScan(_profileDirectory, fileName);
            _scavengeStatusByFolder.Remove(folderId);
            _recoveredItemsByFolder.Remove(folderId);
            _recoveredByFolder.Remove(folderId);
        }, ct);
    }

    public static DbxStore Open(string profileDirectoryPath, string? scavengeCacheDatabasePath = null, string? movedMessageCacheDatabasePath = null, string? deletedMessageCacheDatabasePath = null, string? hiddenFolderCacheDatabasePath = null, string? backupRootDirectory = null)
    {
        string foldersDbxPath = Path.Combine(profileDirectoryPath, "Folders.dbx");
        using var folderFile = DbxFolderFile.Open(foldersDbxPath);

        var folders = new List<StoreFolder>();
        var backingFileNames = new Dictionary<int, string?>();

        foreach (var f in folderFile.Folders)
        {
            string? fileName = string.IsNullOrEmpty(f.FileName) ? null : f.FileName;
            bool isLoadable = fileName is null || File.Exists(Path.Combine(profileDirectoryPath, fileName));

            folders.Add(new StoreFolder
            {
                Id = f.FolderId,
                // -1 is the verified real sentinel for "no parent" (see Global Constraints).
                // Every other value is passed through unresolved — orphan tolerance is the
                // tree-builder's job (LEC.App), not DbxStore's.
                ParentId = f.ParentId == -1 ? null : f.ParentId,
                Name = f.Name ?? string.Empty,
                SpecialFolderKind = ToSpecialFolderKind(f.SpecialFolderType),
                MessageCount = f.MessageCount,
                IsLoadable = isLoadable,
                HasBackingFile = fileName is not null,
            });
            backingFileNames[f.FolderId] = fileName;
        }

        var health = folderFile.Health; // read before the `using` disposes folderFile below
        var scavengeCache = OpenScavengeCacheWithFallback(scavengeCacheDatabasePath ?? ScavengeCache.DefaultDatabasePath);
        var movedMessageCache = OpenMovedMessageCacheWithFallback(movedMessageCacheDatabasePath ?? MovedMessageCache.DefaultDatabasePath);
        var deletedMessageCache = OpenDeletedMessageCacheWithFallback(deletedMessageCacheDatabasePath ?? DeletedMessageCache.DefaultDatabasePath);
        var hiddenFolderCache = OpenHiddenFolderCacheWithFallback(hiddenFolderCacheDatabasePath ?? HiddenFolderCache.DefaultDatabasePath);
        var backupStore = new FileBackupStore(backupRootDirectory ?? FileBackupStore.DefaultRoot);

        // Folders hidden by an old app version's DeleteFolderAsync (hide-only; today's
        // DeleteFolderAsync never writes to this cache — it permanently deletes instead) must
        // never re-enter Folders/backingFileNames: this is the ONLY place that filter is
        // applied, so every other DbxStore member (and every LEC.App consumer of Folders)
        // never has to know a hidden folder ever existed.
        var hiddenIds = hiddenFolderCache.GetHiddenFolderIds(profileDirectoryPath);
        if (hiddenIds.Count > 0)
        {
            folders.RemoveAll(f => hiddenIds.Contains(f.Id));
            foreach (int id in hiddenIds)
                backingFileNames.Remove(id);
        }

        return new DbxStore(profileDirectoryPath, folders, backingFileNames, health, scavengeCache, movedMessageCache, deletedMessageCache, hiddenFolderCache, backupStore);
    }

    // Global Constraint (design §8): cache unavailable (AppData not writable, disk full,
    // locked by another instance) must not block the app -- scavenging still works for
    // this session, it just won't survive a restart. SQLite's ":memory:" database is a
    // real, fully-functional SqliteConnection with the exact same schema/API, so falling
    // back to it needs no separate no-op/null implementation of ScavengeCache.
    private static ScavengeCache OpenScavengeCacheWithFallback(string databasePath)
    {
        try
        {
            return new ScavengeCache(databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // ScavengeCache's constructor wraps whatever path it's given as
            // "Data Source={path}" -- ":memory:" is itself a complete, valid connection
            // string value for SQLite's in-memory mode, so pass it bare, not pre-wrapped.
            return new ScavengeCache(":memory:");
        }
    }

    // Same fallback reasoning as OpenScavengeCacheWithFallback, for the moved-message cache.
    private static MovedMessageCache OpenMovedMessageCacheWithFallback(string databasePath)
    {
        try
        {
            return new MovedMessageCache(databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new MovedMessageCache(":memory:");
        }
    }

    // Same fallback reasoning as OpenScavengeCacheWithFallback, for the deleted-message cache.
    private static DeletedMessageCache OpenDeletedMessageCacheWithFallback(string databasePath)
    {
        try
        {
            return new DeletedMessageCache(databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new DeletedMessageCache(":memory:");
        }
    }

    // Same fallback reasoning as OpenScavengeCacheWithFallback, for the hidden-folder cache.
    private static HiddenFolderCache OpenHiddenFolderCacheWithFallback(string databasePath)
    {
        try
        {
            return new HiddenFolderCache(databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new HiddenFolderCache(":memory:");
        }
    }

    private static SpecialFolderKind ToSpecialFolderKind(DbxSpecialFolderType type) => type switch
    {
        DbxSpecialFolderType.Inbox => SpecialFolderKind.Inbox,
        DbxSpecialFolderType.Outbox => SpecialFolderKind.Outbox,
        DbxSpecialFolderType.SentItems => SpecialFolderKind.SentItems,
        DbxSpecialFolderType.DeletedItems => SpecialFolderKind.DeletedItems,
        DbxSpecialFolderType.Drafts => SpecialFolderKind.Drafts,
        _ => SpecialFolderKind.None,
    };

    public async IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var emailFile = EnsureFolderOpen(folder);
        if (emailFile is null)
            yield break;

        if (!_messagesByFolder.TryGetValue(folder.Id, out var infoByRef))
        {
            infoByRef = new Dictionary<MessageRef, DbxMessageInfo>();
            _messagesByFolder[folder.Id] = infoByRef;
        }

        EnsureMovedLoaded(folder);
        var hiddenOffsets = _hiddenOffsetsByFolder[folder.Id];

        EnsureDeletedLoaded(folder);
        var deletedOffsets = _deletedOffsetsByFolder[folder.Id];

        foreach (var info in emailFile.Messages)
        {
            ct.ThrowIfCancellationRequested();
            if (hiddenOffsets.Contains(info.IndexNodeOffset) || deletedOffsets.Contains(info.IndexNodeOffset))
                continue;

            var reference = new MessageRef(folder.Id, info.IndexNodeOffset);
            infoByRef[reference] = info;

            yield return new MessageHeader
            {
                Id = reference,
                Subject = info.Subject,
                SenderName = info.SenderName,
                SenderAddress = info.SenderAddress,
                Date = info.SendTimeUtc ?? info.ReceiveTimeUtc,
                Size = info.MessageSize,
                MessageId = info.MessageId,
                Health = info.HasOutOfRangeAttribute ? StoreHealth.Partial : StoreHealth.Ok,
            };
        }

        EnsureScavengeLoaded(folder);
        if (_scavengeStatusByFolder.TryGetValue(folder.Id, out var status) && status.IsVisible
            && _recoveredItemsByFolder.TryGetValue(folder.Id, out var recoveredItems))
        {
            if (!_recoveredByFolder.TryGetValue(folder.Id, out var offsetByRef))
            {
                offsetByRef = new Dictionary<MessageRef, int>();
                _recoveredByFolder[folder.Id] = offsetByRef;
            }

            foreach (var item in recoveredItems)
            {
                ct.ThrowIfCancellationRequested();
                if (hiddenOffsets.Contains(item.ChainOffset) || deletedOffsets.Contains(item.ChainOffset))
                    continue;

                // ChainOffset re-purposes MessageRef's IndexNodeOffset slot as an opaque
                // "wherever this is in the file" identifier -- a recovered item has no real
                // index node (that's exactly what the tree lost), but the two are always in
                // disjoint byte regions of the file (index/info-node region vs. data-block
                // region) so collisions are not a practical concern.
                var reference = new MessageRef(folder.Id, item.ChainOffset);
                offsetByRef[reference] = item.ChainOffset;

                yield return new MessageHeader
                {
                    Id = reference,
                    Subject = item.Subject,
                    SenderName = item.SenderName,
                    SenderAddress = item.SenderAddress,
                    Date = item.DateUtc,
                    Size = (uint)Math.Max(0, item.ByteLength),
                    MessageId = null,
                    Health = StoreHealth.Partial,
                    RecoveredKind = item.Kind,
                };
            }
        }

        await Task.CompletedTask; // keeps this an async iterator per IMailStore's contract
    }

    private DbxEmailFile? EnsureFolderOpen(StoreFolder folder)
    {
        if (_openFolders.TryGetValue(folder.Id, out var emailFile))
            return emailFile;

        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
            return null;

        var opened = TryOpenFolderFile(fileName);
        if (opened is null)
            return null;

        _openFolders[folder.Id] = opened;
        return opened;
    }

    public StoreHealth GetFolderHealth(StoreFolder folder) => EnsureFolderOpen(folder)?.Health ?? StoreHealth.Ok;

    public ScavengeStatus GetScavengeStatus(StoreFolder folder)
    {
        EnsureScavengeLoaded(folder);
        return _scavengeStatusByFolder.TryGetValue(folder.Id, out var status) ? status : ScavengeStatus.NotScanned;
    }

    public async Task<ScavengeStatus> ScavengeAsync(StoreFolder folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
            return ScavengeStatus.NotScanned;

        var emailFile = EnsureFolderOpen(folder);
        if (emailFile is null)
            return ScavengeStatus.NotScanned;

        var scavenged = await Task.Run(() => emailFile.Scavenge(progress, ct), ct);
        var classified = scavenged.Select(ScavengedMessageClassifier.Classify).ToList();

        _scavengeCache.SaveScan(_profileDirectory, fileName, classified);
        _recoveredItemsByFolder[folder.Id] = classified;

        var status = new ScavengeStatus(ScavengeState.Scanned, classified.Count, IsVisible: false);
        _scavengeStatusByFolder[folder.Id] = status;
        return status;
    }

    public void SetScavengeVisibility(StoreFolder folder, bool visible)
    {
        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
            return;

        _scavengeCache.SetVisibility(_profileDirectory, fileName, visible);

        if (_scavengeStatusByFolder.TryGetValue(folder.Id, out var status))
            _scavengeStatusByFolder[folder.Id] = status with { IsVisible = visible };
    }

    private void EnsureScavengeLoaded(StoreFolder folder)
    {
        if (_scavengeStatusByFolder.ContainsKey(folder.Id))
            return;

        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
        {
            _scavengeStatusByFolder[folder.Id] = ScavengeStatus.NotScanned;
            return;
        }

        var cached = _scavengeCache.GetScan(_profileDirectory, fileName);
        if (cached is null)
        {
            _scavengeStatusByFolder[folder.Id] = ScavengeStatus.NotScanned;
            return;
        }

        _recoveredItemsByFolder[folder.Id] = cached.Items;
        _scavengeStatusByFolder[folder.Id] = new ScavengeStatus(ScavengeState.Scanned, cached.Items.Count, cached.IsVisible);
    }

    private void EnsureMovedLoaded(StoreFolder folder)
    {
        if (_hiddenOffsetsByFolder.ContainsKey(folder.Id))
            return;

        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
        {
            _hiddenOffsetsByFolder[folder.Id] = new HashSet<int>();
            return;
        }

        _hiddenOffsetsByFolder[folder.Id] = _movedMessageCache.GetMovedOffsets(_profileDirectory, fileName);
    }

    /// <summary>
    /// Records that a message was moved away from its folder, so EnumerateHeadersAsync stops
    /// listing it — the source .dbx itself is never touched.
    /// </summary>
    /// <remarks>
    /// Not thread-safe: this mutates the unsynchronized _hiddenOffsetsByFolder dictionary
    /// (and writes to the shared MovedMessageCache connection). Concurrent calls against the
    /// same DbxStore instance must be serialized by the caller, exactly as CreateFolderAsync
    /// and AddMessageAsync already require.
    /// </remarks>
    public Task HideMovedMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_backingFileNames.TryGetValue(id.FolderId, out var fileName) || fileName is null)
                return;

            _movedMessageCache.MarkMoved(_profileDirectory, fileName, id.IndexNodeOffset);

            if (!_hiddenOffsetsByFolder.TryGetValue(id.FolderId, out var offsets))
            {
                offsets = new HashSet<int>();
                _hiddenOffsetsByFolder[id.FolderId] = offsets;
            }
            offsets.Add(id.IndexNodeOffset);
        }, ct);
    }

    private void EnsureDeletedLoaded(StoreFolder folder)
    {
        if (_deletedOffsetsByFolder.ContainsKey(folder.Id))
            return;

        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
        {
            _deletedOffsetsByFolder[folder.Id] = new HashSet<int>();
            return;
        }

        _deletedOffsetsByFolder[folder.Id] = _deletedMessageCache.GetDeletedOffsets(_profileDirectory, fileName);
    }

    /// <summary>
    /// Records that a message was deleted, so EnumerateHeadersAsync stops listing it — the
    /// source .dbx itself is never touched.
    /// </summary>
    /// <remarks>
    /// Not thread-safe: this mutates the unsynchronized _deletedOffsetsByFolder dictionary
    /// (and writes to the shared DeletedMessageCache connection). Concurrent calls against
    /// the same DbxStore instance must be serialized by the caller, exactly as
    /// HideMovedMessageAsync already requires.
    /// </remarks>
    public Task DeleteMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_backingFileNames.TryGetValue(id.FolderId, out var fileName) || fileName is null)
                return;

            _deletedMessageCache.MarkDeleted(_profileDirectory, fileName, id.IndexNodeOffset);

            if (!_deletedOffsetsByFolder.TryGetValue(id.FolderId, out var offsets))
            {
                offsets = new HashSet<int>();
                _deletedOffsetsByFolder[id.FolderId] = offsets;
            }
            offsets.Add(id.IndexNodeOffset);
        }, ct);
    }

    private DbxEmailFile? TryOpenFolderFile(string fileName)
    {
        string path = Path.Combine(_profileDirectory, fileName);
        try
        {
            return DbxEmailFile.Open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbxFormatException)
        {
            return null;
        }
    }

    public Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        if (_messagesByFolder.TryGetValue(id.FolderId, out var infoByRef) &&
            infoByRef.TryGetValue(id, out var info) &&
            _openFolders.TryGetValue(id.FolderId, out var normalEmailFile))
        {
            byte[] normalRaw = normalEmailFile.ReadMessageRaw(info, out _);
            return Task.FromResult<Stream>(new MemoryStream(normalRaw));
        }

        if (_recoveredByFolder.TryGetValue(id.FolderId, out var offsetByRef) &&
            offsetByRef.TryGetValue(id, out var chainOffset) &&
            _openFolders.TryGetValue(id.FolderId, out var recoveredEmailFile))
        {
            byte[] recoveredRaw = recoveredEmailFile.ReadRecoveredMessageRaw(chainOffset, out _);
            return Task.FromResult<Stream>(new MemoryStream(recoveredRaw));
        }

        throw new InvalidOperationException(
            $"Message {id} was not found. EnumerateHeadersAsync must be called for its folder before OpenMessageAsync.");
    }

    public void Dispose()
    {
        foreach (var file in _openFolders.Values)
            file.Dispose();
        _openFolders.Clear();
        _scavengeCache.Dispose();
        _movedMessageCache.Dispose();
        _deletedMessageCache.Dispose();
        _hiddenFolderCache.Dispose();
    }
}
