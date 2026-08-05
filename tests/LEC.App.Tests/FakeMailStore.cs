using System.Linq;
using System.Runtime.CompilerServices;
using LEC.DbxFormat;
using LEC.MailStore;

namespace LEC.App.Tests;

/// <summary>In-memory IMailStore test double — no .dbx files involved.</summary>
internal sealed class FakeMailStore : IMailStore
{
    private readonly Dictionary<int, List<MessageHeader>> _headersByFolder;
    private readonly Dictionary<MessageRef, byte[]> _rawByRef;

    private readonly List<StoreFolder> _folders;

    public FakeMailStore(
        IReadOnlyList<StoreFolder> folders,
        Dictionary<int, List<MessageHeader>>? headersByFolder = null,
        Dictionary<MessageRef, byte[]>? rawByRef = null,
        StoreHealth health = StoreHealth.Ok)
    {
        _folders = folders.ToList();
        _headersByFolder = headersByFolder ?? new Dictionary<int, List<MessageHeader>>();
        _rawByRef = rawByRef ?? new Dictionary<MessageRef, byte[]>();
        Health = health;
    }

    public IReadOnlyList<StoreFolder> Folders => _folders;
    public StoreHealth Health { get; }

    public Exception? ExceptionToThrowOnCreateFolder { get; set; }

    public async IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_headersByFolder.TryGetValue(folder.Id, out var headers))
            foreach (var h in headers)
                yield return h;

        await Task.CompletedTask;
    }

    public Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        if (!_rawByRef.TryGetValue(id, out var raw))
            throw new InvalidOperationException($"No raw bytes registered for {id}.");

        return Task.FromResult<Stream>(new MemoryStream(raw));
    }

    public StoreHealth FolderHealthToReturn { get; set; } = StoreHealth.Ok;
    public ScavengeStatus ScavengeStatusToReturn { get; set; } = ScavengeStatus.NotScanned;
    private bool _visibility;

    public StoreHealth GetFolderHealth(StoreFolder folder) => FolderHealthToReturn;

    public ScavengeStatus GetScavengeStatus(StoreFolder folder) => ScavengeStatusToReturn with { IsVisible = _visibility };

    public Task<ScavengeStatus> ScavengeAsync(StoreFolder folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(1.0);
        return Task.FromResult(ScavengeStatusToReturn);
    }

    public void SetScavengeVisibility(StoreFolder folder, bool visible) => _visibility = visible;

    public Task<StoreFolder> CreateFolderAsync(string name, int? parentId, CancellationToken ct = default)
    {
        if (ExceptionToThrowOnCreateFolder is { } ex)
            throw ex;

        var created = new StoreFolder
        {
            Id = _folders.Count == 0 ? 1 : _folders.Max(f => f.Id) + 1,
            ParentId = parentId,
            Name = name,
            IsLoadable = true,
            HasBackingFile = true,
        };
        _folders.Add(created);
        return Task.FromResult(created);
    }

    public Exception? ExceptionToThrowOnAddMessage { get; set; }
    public List<(int FolderId, byte[] RawMessage, string? Subject, string? SenderName, string? SenderAddress, string? MessageId, DateTime? DateUtc)> AddedMessages { get; } = new();

    public Task AddMessageAsync(int folderId, byte[] rawMessage, string? subject, string? senderName,
        string? senderAddress, string? messageId, DateTime? dateUtc, CancellationToken ct = default)
    {
        if (ExceptionToThrowOnAddMessage is { } ex)
            throw ex;

        AddedMessages.Add((folderId, rawMessage, subject, senderName, senderAddress, messageId, dateUtc));
        return Task.CompletedTask;
    }

    public HashSet<MessageRef> HiddenMessageRefs { get; } = new();

    public Task HideMovedMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        HiddenMessageRefs.Add(id);
        return Task.CompletedTask;
    }

    public HashSet<MessageRef> DeletedMessageRefs { get; } = new();

    public Task DeleteMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        DeletedMessageRefs.Add(id);
        return Task.CompletedTask;
    }

    public Exception? ExceptionToThrowOnDeleteFolder { get; set; }
    public List<int> DeletedFolderIds { get; } = new();

    public Task DeleteFolderAsync(int folderId, CancellationToken ct = default)
    {
        if (ExceptionToThrowOnDeleteFolder is { } ex)
            throw ex;

        DeletedFolderIds.Add(folderId);
        _folders.RemoveAll(f => f.Id == folderId);
        return Task.CompletedTask;
    }

    public Exception? ExceptionToThrowOnMoveFolder { get; set; }
    public List<(int FolderId, int? NewParentId)> MovedFolders { get; } = new();

    public Task MoveFolderAsync(int folderId, int? newParentId, CancellationToken ct = default)
    {
        if (ExceptionToThrowOnMoveFolder is { } ex)
            throw ex;

        int index = _folders.FindIndex(f => f.Id == folderId);
        if (index < 0)
            throw new InvalidOperationException($"Pasta {folderId} não encontrada.");

        MovedFolders.Add((folderId, newParentId));

        var current = _folders[index];
        _folders[index] = new StoreFolder
        {
            Id = current.Id,
            ParentId = newParentId,
            Name = current.Name,
            SpecialFolderKind = current.SpecialFolderKind,
            MessageCount = current.MessageCount,
            IsLoadable = current.IsLoadable,
            HasBackingFile = current.HasBackingFile,
        };
        return Task.CompletedTask;
    }

    public bool WasDisposed { get; private set; }

    public void Dispose() => WasDisposed = true;
}
