namespace LEC.DbxFormat;

/// <summary>
/// One message's worth of data to write into a rebuilt message-store .dbx.
/// MessageSize is not a field here -- the writer always computes it as
/// RawBytes.Length, so it can never drift from the bytes actually written.
/// ReceiveTimeUtc is not carried either: DbxMessageInfo.Read already resolves
/// Date = SendTimeUtc ?? ReceiveTimeUtc, so one value is enough to round-trip it.
/// </summary>
public sealed record DbxMessageWriteEntry(
    byte[] RawBytes,
    string? Subject,
    string? SenderName,
    string? SenderAddress,
    string? MessageId,
    DateTime? SendTimeUtc,
    int Flags = 0);

/// <summary>
/// Writes a brand-new, empty message-store .dbx file — the backing file for a folder
/// that was just created and has no messages yet. itemCount = 0 means the reader
/// (DbxEmailFile.Open) never attempts to walk an index tree, so no tree needs to exist:
/// the header alone is a complete, valid, empty file.
///
/// Rebuild rewrites a message-store .dbx from scratch as a single, flat index-tree node
/// (no children — ptrCount is one byte, so up to 255 messages fit) — same approach as
/// DbxFolderFileWriter.Rebuild, and its low-level helpers are deliberately duplicated
/// here rather than shared (see the plan's File Structure note for why).
/// </summary>
public static class DbxEmailFileWriter
{
    public static void CreateEmpty(string path)
    {
        var bytes = new byte[DbxFormat.HeaderIndexPointerOffset + 4];

        BitConverter.GetBytes(DbxFormat.SignatureWord0).CopyTo(bytes, 0);
        BitConverter.GetBytes(DbxFormat.SignatureWord1Email).CopyTo(bytes, 4);
        BitConverter.GetBytes(DbxFormat.SignatureWord2).CopyTo(bytes, 8);
        BitConverter.GetBytes(DbxFormat.SignatureWord3).CopyTo(bytes, 12);
        BitConverter.GetBytes(0).CopyTo(bytes, DbxFormat.HeaderItemCountOffset);

        File.WriteAllBytes(path, bytes);
    }

    public static void Rebuild(string path, IReadOnlyList<DbxMessageWriteEntry> messages)
    {
        if (messages.Count > byte.MaxValue)
            throw new ArgumentException($"Message-store .dbx flat-node format holds at most {byte.MaxValue} messages; got {messages.Count}.", nameof(messages));

        foreach (var message in messages)
        {
            if (message.RawBytes.Length == 0)
                throw new ArgumentException("A message's raw bytes cannot be empty.", nameof(messages));
        }

        var bytes = new List<byte>(new byte[DbxFormat.HeaderIndexPointerOffset + 4]);

        WriteUInt32(bytes, 0, DbxFormat.SignatureWord0);
        WriteUInt32(bytes, 4, DbxFormat.SignatureWord1Email);
        WriteUInt32(bytes, 8, DbxFormat.SignatureWord2);
        WriteUInt32(bytes, 12, DbxFormat.SignatureWord3);

        var infoNodeOffsets = new int[messages.Count];
        for (int i = 0; i < messages.Count; i++)
        {
            int blockOffset = WriteMessageBlockChain(bytes, messages[i].RawBytes);
            infoNodeOffsets[i] = WriteInfoNode(bytes, messages[i], blockOffset);
        }

        int rootOffset = WriteIndexNode(bytes, infoNodeOffsets);

        WriteInt32(bytes, DbxFormat.HeaderItemCountOffset, messages.Count);
        WriteInt32(bytes, DbxFormat.HeaderIndexPointerOffset, rootOffset);

        string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(tempPath, bytes.ToArray());
        File.Move(tempPath, path, overwrite: true);
    }

    // Splits rawBytes into DbxFormat.MessageBlockMaxDataSize-byte blocks, writes each with
    // NextOffset = 0, then back-patches every block but the last to point at the one after
    // it -- same "reserve then patch" technique as WriteInfoNode's attribute array, just at
    // the granularity of whole blocks instead of individual attribute records. Returns the
    // first block's own offset (what the info-node's MessageBlockOffset attribute points to).
    private static int WriteMessageBlockChain(List<byte> bytes, byte[] rawBytes)
    {
        var blockStarts = new List<int>();
        int offset = 0;
        while (offset < rawBytes.Length)
        {
            int chunkSize = Math.Min(DbxFormat.MessageBlockMaxDataSize, rawBytes.Length - offset);
            byte[] chunk = rawBytes.AsSpan(offset, chunkSize).ToArray();
            blockStarts.Add(WriteOneMessageBlock(bytes, chunk));
            offset += chunkSize;
        }

        for (int i = 0; i < blockStarts.Count - 1; i++)
            WriteInt32(bytes, blockStarts[i] + DbxFormat.MessageBlockNextOffset, blockStarts[i + 1]);

        return blockStarts[0];
    }

    private static int WriteOneMessageBlock(List<byte> bytes, byte[] data)
    {
        int start = bytes.Count;
        AddZeros(bytes, DbxFormat.MessageBlockHeaderSize); // reserved: self-offset, marker, size, next (all patched below except next, left 0)

        WriteInt32(bytes, start + DbxFormat.MessageBlockSelfOffsetOffset, start);
        WriteInt32(bytes, start + DbxFormat.MessageBlockMarkerOffset, DbxFormat.MessageBlockMaxDataSize);
        WriteInt16(bytes, start + DbxFormat.MessageBlockSizeOffset, (short)data.Length);
        // NextOffset (start + MessageBlockNextOffset) stays 0 here; WriteMessageBlockChain
        // patches it once the following block's start offset is known, or leaves it 0 if
        // this is the chain's last block.

        bytes.AddRange(data);
        return start;
    }

    private static int WriteInfoNode(List<byte> bytes, DbxMessageWriteEntry message, int blockOffset)
    {
        int start = bytes.Count;

        var attrs = new List<(byte FieldId, bool Direct, uint DirectValue, byte[]? Data)>
        {
            DirectOrPointer(DbxEmailFieldId.MessageBlockOffset, blockOffset),
        };
        if (message.Subject is not null)
            attrs.Add(((byte)DbxEmailFieldId.Subject, false, 0, EncodeString(message.Subject)));
        if (message.SenderName is not null)
            attrs.Add(((byte)DbxEmailFieldId.SenderName, false, 0, EncodeString(message.SenderName)));
        if (message.SenderAddress is not null)
            attrs.Add(((byte)DbxEmailFieldId.SenderAddress, false, 0, EncodeString(message.SenderAddress)));
        if (message.MessageId is not null)
            attrs.Add(((byte)DbxEmailFieldId.MessageId, false, 0, EncodeString(message.MessageId)));
        if (message.SendTimeUtc is { } sendTime)
        {
            // Always pointer-form: the reader takes this field through ReadInt64 (an 8-byte
            // Windows FILETIME), which can never fit the packed record's 24-bit direct payload
            // regardless of value -- same reasoning DbxFolderFileWriter already applies to
            // ParentId, just for a wider field.
            attrs.Add(((byte)DbxEmailFieldId.SendCreateTime, false, 0, BitConverter.GetBytes(sendTime.ToFileTimeUtc())));
        }
        attrs.Add(DirectOrPointer(DbxEmailFieldId.MessageSize, (uint)message.RawBytes.Length));
        attrs.Add(DirectOrPointer(DbxEmailFieldId.Flags, message.Flags));

        // Info-node header: 8 unused bytes, then a count word at +8 whose byte at +2 (i.e.
        // absolute offset +10) is the attribute count — DbxFormat.InfoNodeCountWordOffset/
        // InfoNodeCountMask/InfoNodeCountShift read exactly this layout.
        AddZeros(bytes, 8);
        bytes.Add(0);
        bytes.Add(0);
        bytes.Add((byte)attrs.Count);
        bytes.Add(0);

        int arrayStart = bytes.Count;
        for (int i = 0; i < attrs.Count; i++)
            AddZeros(bytes, DbxFormat.AttributeRecordSize); // reserved, back-patched below

        int dataAreaStart = bytes.Count;
        for (int i = 0; i < attrs.Count; i++)
        {
            var attr = attrs[i];
            byte typeByte;
            uint recordValue;

            if (attr.Direct)
            {
                typeByte = (byte)(attr.FieldId | DbxFormat.AttributeDirectFlag);
                recordValue = attr.DirectValue & DbxFormat.AttributeValueMask;
            }
            else
            {
                typeByte = attr.FieldId;
                recordValue = (uint)(bytes.Count - dataAreaStart) & DbxFormat.AttributeValueMask;
                bytes.AddRange(attr.Data!);
            }

            uint record = (uint)typeByte | (recordValue << DbxFormat.AttributeValueShift);
            byte[] recordBytes = BitConverter.GetBytes(record);
            for (int b = 0; b < 4; b++)
                bytes[arrayStart + i * DbxFormat.AttributeRecordSize + b] = recordBytes[b];
        }

        return start;
    }

    private static int WriteIndexNode(List<byte> bytes, IReadOnlyList<int> infoNodeOffsets)
    {
        int start = bytes.Count;

        AddZeros(bytes, 8);                                   // 0..7: unused by the reader
        AddZeros(bytes, 4);                                   // 8..11: nextTable (unused — indexCount = 0)
        AddZeros(bytes, 5);                                   // 12..16: unused
        bytes.Add((byte)infoNodeOffsets.Count);                // 17: ptrCount
        AddZeros(bytes, 2);                                   // 18..19: unused
        bytes.AddRange(BitConverter.GetBytes(0));              // 20..23: indexCount = 0 — flat, no children

        foreach (int offset in infoNodeOffsets)
        {
            bytes.AddRange(BitConverter.GetBytes(offset)); // indexPtr
            AddZeros(bytes, 4);                             // entryNextTable = 0
            AddZeros(bytes, 4);                             // entryIndexCount = 0
        }

        return start;
    }

    // Direct-form embeds the value in the packed record's 24-bit payload
    // (DbxFormat.AttributeValueMask) — anything wider (or negative, for the int overload)
    // must go pointer-form instead, or it silently truncates. Same lesson DbxFolderFileWriter
    // already learned for Flags/FolderId/MessageCount, applied here from the start.
    private static bool FitsDirect(int value) => value >= 0 && (value & ~(int)DbxFormat.AttributeValueMask) == 0;
    private static bool FitsDirect(uint value) => (value & ~DbxFormat.AttributeValueMask) == 0;

    private static (byte FieldId, bool Direct, uint DirectValue, byte[]? Data) DirectOrPointer(DbxEmailFieldId fieldId, int value) =>
        FitsDirect(value)
            ? ((byte)fieldId, true, unchecked((uint)value), null)
            : ((byte)fieldId, false, 0, BitConverter.GetBytes(value));

    private static (byte FieldId, bool Direct, uint DirectValue, byte[]? Data) DirectOrPointer(DbxEmailFieldId fieldId, uint value) =>
        FitsDirect(value)
            ? ((byte)fieldId, true, value, null)
            : ((byte)fieldId, false, 0, BitConverter.GetBytes(value));

    private static byte[] EncodeString(string value) => System.Text.Encoding.UTF8.GetBytes(value + "\0");

    private static void AddZeros(List<byte> bytes, int count) => bytes.AddRange(new byte[count]);

    private static void WriteUInt32(List<byte> bytes, int offset, uint value)
    {
        byte[] b = BitConverter.GetBytes(value);
        for (int i = 0; i < 4; i++) bytes[offset + i] = b[i];
    }

    private static void WriteInt32(List<byte> bytes, int offset, int value)
    {
        byte[] b = BitConverter.GetBytes(value);
        for (int i = 0; i < 4; i++) bytes[offset + i] = b[i];
    }

    private static void WriteInt16(List<byte> bytes, int offset, short value)
    {
        byte[] b = BitConverter.GetBytes(value);
        bytes[offset] = b[0];
        bytes[offset + 1] = b[1];
    }
}
