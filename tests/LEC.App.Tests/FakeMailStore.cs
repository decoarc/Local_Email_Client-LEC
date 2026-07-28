using System.Runtime.CompilerServices;
using LEC.DbxFormat;
using LEC.MailStore;

namespace LEC.App.Tests;

/// <summary>In-memory IMailStore test double — no .dbx files involved.</summary>
internal sealed class FakeMailStore : IMailStore
{
    private readonly Dictionary<int, List<MessageHeader>> _headersByFolder;
    private readonly Dictionary<MessageRef, byte[]> _rawByRef;

    public FakeMailStore(
        IReadOnlyList<StoreFolder> folders,
        Dictionary<int, List<MessageHeader>>? headersByFolder = null,
        Dictionary<MessageRef, byte[]>? rawByRef = null,
        StoreHealth health = StoreHealth.Ok)
    {
        Folders = folders;
        _headersByFolder = headersByFolder ?? new Dictionary<int, List<MessageHeader>>();
        _rawByRef = rawByRef ?? new Dictionary<MessageRef, byte[]>();
        Health = health;
    }

    public IReadOnlyList<StoreFolder> Folders { get; }
    public StoreHealth Health { get; }

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

    public bool WasDisposed { get; private set; }

    public void Dispose() => WasDisposed = true;
}
