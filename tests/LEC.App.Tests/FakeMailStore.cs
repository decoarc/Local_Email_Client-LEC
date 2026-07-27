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

    public void Dispose() { }
}
