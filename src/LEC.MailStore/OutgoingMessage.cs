namespace LEC.MailStore;

/// <summary>
/// One message to append to a folder. New messages have no receiver fields and default flags;
/// messages already in the folder keep theirs when the file is rebuilt.
/// </summary>
public sealed record OutgoingMessage(
    byte[] RawBytes,
    string? Subject,
    string? SenderName,
    string? SenderAddress,
    string? MessageId,
    DateTime? DateUtc);
