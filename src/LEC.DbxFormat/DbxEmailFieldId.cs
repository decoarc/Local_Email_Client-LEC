namespace LEC.DbxFormat;

/// <summary>
/// Email info-node attribute IDs, verified against undbx's _dbx_read_info
/// switch (dbxread.c). Gaps (0x0F, 0x15-0x19, 0x1C+) are attributes undbx
/// itself does not decode — left unhandled here too, not guessed.
/// </summary>
public enum DbxEmailFieldId : byte
{
    MessageIndex = 0x00,
    Flags = 0x01,
    SendCreateTime = 0x02,
    BodyLines = 0x03,
    MessageBlockOffset = 0x04,
    OriginalSubject = 0x05,
    SaveTime = 0x06,
    MessageId = 0x07,
    Subject = 0x08,
    SenderAddressAndName = 0x09,
    InReplyToMessageId = 0x0A,
    ServerNewsgroupMessageNumber = 0x0B,
    Server = 0x0C,
    SenderName = 0x0D,
    SenderAddress = 0x0E,
    MessagePriority = 0x10,
    MessageSize = 0x11,
    ReceiveCreateTime = 0x12,
    ReceiverName = 0x13,
    ReceiverAddress = 0x14,
    AccountName = 0x1A,
    AccountRegistryKey = 0x1B,
}
