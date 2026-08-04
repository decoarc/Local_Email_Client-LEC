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

    private DbxStore(string profileDirectory, List<StoreFolder> folders, Dictionary<int, string?> backingFileNames, StoreHealth health, ScavengeCache scavengeCache, MovedMessageCache movedMessageCache)
    {
        _profileDirectory = profileDirectory;
        _folders = folders;
        _backingFileNames = backingFileNames;
        Health = health;
        _scavengeCache = scavengeCache;
        _movedMessageCache = movedMessageCache;
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
                // Write-once permanent copy of the very first, pristine, real
                // Outlook-Express-authored Folders.dbx — never overwritten again, so it
                // survives even after many later folder creations roll the ".bak" copy.
                string pristinePath = foldersDbxPath + ".original";
                if (!File.Exists(pristinePath))
                    File.Copy(foldersDbxPath, pristinePath);

                File.Copy(foldersDbxPath, foldersDbxPath + ".bak", overwrite: true);

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
                        info.MessageId, info.SendTimeUtc ?? info.ReceiveTimeUtc, info.Flags))
                    .ToList();
            }

            string pristinePath = filePath + ".original";
            if (!File.Exists(pristinePath))
                File.Copy(filePath, pristinePath);
            File.Copy(filePath, filePath + ".bak", overwrite: true);

            entries.Add(new DbxMessageWriteEntry(rawMessage, subject, senderName, senderAddress, messageId, dateUtc));
            DbxEmailFileWriter.Rebuild(filePath, entries);
        }, ct);
    }

    public static DbxStore Open(string profileDirectoryPath, string? scavengeCacheDatabasePath = null, string? movedMessageCacheDatabasePath = null)
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
            });
            backingFileNames[f.FolderId] = fileName;
        }

        var health = folderFile.Health; // read before the `using` disposes folderFile below
        var scavengeCache = OpenScavengeCacheWithFallback(scavengeCacheDatabasePath ?? ScavengeCache.DefaultDatabasePath);
        var movedMessageCache = OpenMovedMessageCacheWithFallback(movedMessageCacheDatabasePath ?? MovedMessageCache.DefaultDatabasePath);
        return new DbxStore(profileDirectoryPath, folders, backingFileNames, health, scavengeCache, movedMessageCache);
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

        foreach (var info in emailFile.Messages)
        {
            ct.ThrowIfCancellationRequested();
            if (hiddenOffsets.Contains(info.IndexNodeOffset))
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
    }
}
