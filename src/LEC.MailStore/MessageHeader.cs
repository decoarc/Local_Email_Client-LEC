using LEC.DbxFormat;

namespace LEC.MailStore;

public sealed class MessageHeader
{
    public required MessageRef Id { get; init; }
    public string? Subject { get; init; }
    public string? SenderName { get; init; }
    public string? SenderAddress { get; init; }
    public DateTime? Date { get; init; }
    public uint Size { get; init; }
    public string? MessageId { get; init; }
    public StoreHealth Health { get; init; }
}
