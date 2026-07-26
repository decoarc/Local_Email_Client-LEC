namespace LEC.DbxFormat;

public sealed class DbxMessageInfo
{
    public required int IndexNodeOffset { get; init; }
    public int MessageBlockOffset { get; init; }
    public string? Subject { get; init; }
    public string? SenderName { get; init; }
    public string? SenderAddress { get; init; }
    public string? ReceiverName { get; init; }
    public string? ReceiverAddress { get; init; }
    public string? MessageId { get; init; }
    public DateTime? SendTimeUtc { get; init; }
    public DateTime? ReceiveTimeUtc { get; init; }
    public uint MessageSize { get; init; }
    public int Flags { get; init; }

    /// <summary>
    /// True if any attribute in this message's info node was pointer-form with a
    /// computed offset that landed outside the file (an invalid pointer, per
    /// ARQUITETURA.md 7.4) -- as opposed to a legitimately absent direct attribute.
    /// Callers should treat this as a corruption signal even though the message
    /// itself is still returned with the affected field(s) defaulted.
    /// </summary>
    public bool HasOutOfRangeAttribute { get; init; }

    public static DbxMessageInfo Read(IDbxByteSource source, int indexNodeOffset)
    {
        var attrs = DbxAttributeReader.Read(source, indexNodeOffset);

        int messageBlockOffset = 0;
        string? subject = null, senderName = null, senderAddress = null;
        string? receiverName = null, receiverAddress = null, messageId = null;
        DateTime? sendTime = null, receiveTime = null;
        uint messageSize = 0;
        int flags = 0;
        bool hasOutOfRangeAttribute = false;

        foreach (var a in attrs)
        {
            if (a.IsOffsetOutOfRange)
                hasOutOfRangeAttribute = true;

            switch ((DbxEmailFieldId)a.FieldId)
            {
                case DbxEmailFieldId.MessageBlockOffset:
                    messageBlockOffset = ReadInt(source, a);
                    break;
                case DbxEmailFieldId.Subject:
                    subject = ReadString(source, a);
                    break;
                case DbxEmailFieldId.SenderName:
                    senderName = ReadString(source, a);
                    break;
                case DbxEmailFieldId.SenderAddress:
                    senderAddress = ReadString(source, a);
                    break;
                case DbxEmailFieldId.ReceiverName:
                    receiverName = ReadString(source, a);
                    break;
                case DbxEmailFieldId.ReceiverAddress:
                    receiverAddress = ReadString(source, a);
                    break;
                case DbxEmailFieldId.MessageId:
                    messageId = ReadString(source, a);
                    break;
                case DbxEmailFieldId.SendCreateTime:
                    sendTime = ReadFileTime(source, a);
                    break;
                case DbxEmailFieldId.ReceiveCreateTime:
                    receiveTime = ReadFileTime(source, a);
                    break;
                case DbxEmailFieldId.MessageSize:
                    messageSize = (uint)ReadInt(source, a);
                    break;
                case DbxEmailFieldId.Flags:
                    flags = ReadInt(source, a);
                    break;
            }
        }

        return new DbxMessageInfo
        {
            IndexNodeOffset = indexNodeOffset,
            MessageBlockOffset = messageBlockOffset,
            Subject = subject,
            SenderName = senderName,
            SenderAddress = senderAddress,
            ReceiverName = receiverName,
            ReceiverAddress = receiverAddress,
            MessageId = messageId,
            SendTimeUtc = sendTime,
            ReceiveTimeUtc = receiveTime,
            MessageSize = messageSize,
            Flags = flags,
            HasOutOfRangeAttribute = hasOutOfRangeAttribute,
        };
    }

    private static int ReadInt(IDbxByteSource source, DbxAttributeRecord a) =>
        a.IsDirect ? (int)a.Value : a.DataOffset is int off ? source.ReadInt32(off) : 0;

    private static string? ReadString(IDbxByteSource source, DbxAttributeRecord a) =>
        a.DataOffset is int off ? DbxStringDecoder.Decode(source.ReadCStringBytes(off, DbxSafetyLimits.MaxEmailStringBytes)) : null;

    private static DateTime? ReadFileTime(IDbxByteSource source, DbxAttributeRecord a)
    {
        if (a.DataOffset is not int off)
            return null;

        long fileTime = source.ReadInt64(off);
        if (fileTime <= 0)
            return null;

        try
        {
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // corrupted timestamp — don't crash, just omit it
        }
    }
}
