namespace LEC.DbxFormat;

public sealed class DbxFolderInfo
{
    public required int IndexNodeOffset { get; init; }
    public int FolderId { get; init; }
    public int ParentId { get; init; }
    public string? Name { get; init; }
    public string? FileName { get; init; }
    public int Flags { get; init; }
    public int MessageCount { get; init; }
    public DbxSpecialFolderType SpecialFolderType { get; init; }

    /// <summary>
    /// True if any attribute in this folder's info node was pointer-form with a
    /// computed offset that landed outside the file (an invalid pointer, per
    /// ARQUITETURA.md 7.4) -- as opposed to a legitimately absent direct attribute.
    /// Callers should treat this as a corruption signal even though the folder
    /// itself is still returned with the affected field(s) defaulted.
    /// </summary>
    public bool HasOutOfRangeAttribute { get; init; }

    public static DbxFolderInfo Read(IDbxByteSource source, int indexNodeOffset)
    {
        var attrs = DbxAttributeReader.Read(source, indexNodeOffset);

        int folderId = 0, parentId = 0, flags = 0, messageCount = 0;
        string? name = null, fileName = null;
        var specialType = DbxSpecialFolderType.None;
        bool hasOutOfRangeAttribute = false;

        foreach (var a in attrs)
        {
            if (a.IsOffsetOutOfRange)
                hasOutOfRangeAttribute = true;

            switch ((DbxFolderFieldId)a.FieldId)
            {
                case DbxFolderFieldId.FolderId:
                    folderId = ReadInt(source, a);
                    break;
                case DbxFolderFieldId.ParentId:
                    parentId = ReadInt(source, a);
                    break;
                case DbxFolderFieldId.Name:
                    name = ReadString(source, a);
                    break;
                case DbxFolderFieldId.FileName:
                    fileName = ReadString(source, a);
                    break;
                case DbxFolderFieldId.Flags:
                    flags = ReadInt(source, a);
                    break;
                case DbxFolderFieldId.MessageCount:
                    messageCount = ReadInt(source, a);
                    break;
                case DbxFolderFieldId.SpecialFolderType:
                    int value = ReadInt(source, a);
                    specialType = Enum.IsDefined(typeof(DbxSpecialFolderType), value)
                        ? (DbxSpecialFolderType)value
                        : DbxSpecialFolderType.None;
                    break;
            }
        }

        return new DbxFolderInfo
        {
            IndexNodeOffset = indexNodeOffset,
            FolderId = folderId,
            ParentId = parentId,
            Name = name,
            FileName = fileName,
            Flags = flags,
            MessageCount = messageCount,
            SpecialFolderType = specialType,
            HasOutOfRangeAttribute = hasOutOfRangeAttribute,
        };
    }

    private static int ReadInt(IDbxByteSource source, DbxAttributeRecord a) =>
        a.IsDirect ? (int)a.Value : a.DataOffset is int off ? source.ReadInt32(off) : 0;

    private static string? ReadString(IDbxByteSource source, DbxAttributeRecord a) =>
        a.DataOffset is int off ? DbxStringDecoder.Decode(source.ReadCStringBytes(off, DbxSafetyLimits.MaxFolderStringBytes)) : null;
}
