// src/LEC.MailStore/DbxStore.cs
using System.Runtime.CompilerServices;
using LEC.DbxFormat;

namespace LEC.MailStore;

public sealed class DbxStore : IMailStore
{
    private readonly string _profileDirectory;
    private readonly Dictionary<int, string?> _backingFileNames;
    private readonly Dictionary<int, DbxEmailFile> _openFolders = new();
    private readonly Dictionary<int, Dictionary<MessageRef, DbxMessageInfo>> _messagesByFolder = new();

    public IReadOnlyList<StoreFolder> Folders { get; }
    public StoreHealth Health { get; }

    private DbxStore(string profileDirectory, IReadOnlyList<StoreFolder> folders, Dictionary<int, string?> backingFileNames, StoreHealth health)
    {
        _profileDirectory = profileDirectory;
        Folders = folders;
        _backingFileNames = backingFileNames;
        Health = health;
    }

    public static DbxStore Open(string profileDirectoryPath)
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
        return new DbxStore(profileDirectoryPath, folders, backingFileNames, health);
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

    public IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, [EnumeratorCancellation] CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 5.");

    public Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 6.");

    public void Dispose()
    {
        foreach (var file in _openFolders.Values)
            file.Dispose();
        _openFolders.Clear();
    }
}
