namespace LEC.DbxFormat;

/// <summary>
/// One folder's worth of data to write into a rebuilt Folders.dbx. ParentId uses the
/// same -1-means-root sentinel DbxStore.Open already reads. Flags/MessageCount/
/// SpecialFolderType default to zero/None for a brand-new folder; callers rebuilding
/// Folders.dbx around an existing folder must pass its real values through unchanged,
/// or that folder silently loses its Inbox/Sent/Deleted classification and message count.
/// </summary>
public sealed record DbxFolderWriteEntry(
    int FolderId,
    int ParentId,
    string Name,
    string? FileName,
    int Flags = 0,
    int MessageCount = 0,
    DbxSpecialFolderType SpecialFolderType = DbxSpecialFolderType.None);

/// <summary>
/// Rewrites Folders.dbx from scratch as a single, flat index-tree node (no children —
/// ptrCount is one byte, so up to 255 folders fit; a real profile has ~26). This is
/// simpler and easier to get right than inserting into the existing tree in place, at
/// the cost of not preserving whatever undocumented header bytes the original OE-written
/// file had beyond the fields this project's reader actually uses.
/// </summary>
public static class DbxFolderFileWriter
{
    public static void Rebuild(string path, IReadOnlyList<DbxFolderWriteEntry> folders)
    {
        var bytes = new List<byte>(new byte[DbxFormat.HeaderIndexPointerOffset + 4]);

        WriteUInt32(bytes, 0, DbxFormat.SignatureWord0);
        WriteUInt32(bytes, 4, DbxFormat.SignatureWord1Folder);
        WriteUInt32(bytes, 8, DbxFormat.SignatureWord2);
        WriteUInt32(bytes, 12, DbxFormat.SignatureWord3);

        var infoNodeOffsets = new int[folders.Count];
        for (int i = 0; i < folders.Count; i++)
            infoNodeOffsets[i] = WriteInfoNode(bytes, folders[i]);

        int rootOffset = WriteIndexNode(bytes, infoNodeOffsets);

        WriteInt32(bytes, DbxFormat.HeaderItemCountOffset, folders.Count);
        WriteInt32(bytes, DbxFormat.HeaderIndexPointerOffset, rootOffset);

        string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(tempPath, bytes.ToArray());
        File.Move(tempPath, path, overwrite: true);
    }

    private static int WriteInfoNode(List<byte> bytes, DbxFolderWriteEntry folder)
    {
        int start = bytes.Count;

        var attrs = new List<(byte FieldId, bool Direct, uint DirectValue, byte[]? Data)>
        {
            ((byte)DbxFolderFieldId.FolderId, true, unchecked((uint)folder.FolderId), null),
            ((byte)DbxFolderFieldId.ParentId, false, 0, BitConverter.GetBytes(folder.ParentId)),
            ((byte)DbxFolderFieldId.Name, false, 0, EncodeString(folder.Name)),
        };
        if (folder.FileName is not null)
            attrs.Add(((byte)DbxFolderFieldId.FileName, false, 0, EncodeString(folder.FileName)));
        attrs.Add(((byte)DbxFolderFieldId.Flags, true, unchecked((uint)folder.Flags), null));
        attrs.Add(((byte)DbxFolderFieldId.MessageCount, true, unchecked((uint)folder.MessageCount), null));
        attrs.Add(((byte)DbxFolderFieldId.SpecialFolderType, true, unchecked((uint)folder.SpecialFolderType), null));

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
}
