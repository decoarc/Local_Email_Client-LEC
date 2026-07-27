using LEC.DbxFormat;

namespace LEC.MailStore;

/// <summary>Contract fixed by ARQUITETURA.md §7.1 — do not add members here.</summary>
public interface IMailStore : IDisposable
{
    IReadOnlyList<StoreFolder> Folders { get; }

    IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, CancellationToken ct = default);

    Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct = default);

    StoreHealth Health { get; }
}
