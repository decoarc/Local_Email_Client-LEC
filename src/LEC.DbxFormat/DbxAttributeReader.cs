namespace LEC.DbxFormat;

public static class DbxAttributeReader
{
    public static IReadOnlyList<DbxAttributeRecord> Read(IDbxByteSource source, int infoNodeOffset)
    {
        if (infoNodeOffset < 0 || (long)infoNodeOffset + DbxFormat.InfoNodeAttributeArrayOffset > source.Length)
            return Array.Empty<DbxAttributeRecord>();

        int countWord = source.ReadInt32(infoNodeOffset + DbxFormat.InfoNodeCountWordOffset);
        int count = (countWord & DbxFormat.InfoNodeCountMask) >> DbxFormat.InfoNodeCountShift;

        int arrayStart = infoNodeOffset + DbxFormat.InfoNodeAttributeArrayOffset;
        long dataAreaStart = arrayStart + (long)DbxFormat.AttributeRecordSize * count;

        if (dataAreaStart > source.Length)
            return Array.Empty<DbxAttributeRecord>(); // corrupt count would overread

        var records = new List<DbxAttributeRecord>(count);
        for (int i = 0; i < count; i++)
        {
            uint raw = unchecked((uint)source.ReadInt32(arrayStart + i * DbxFormat.AttributeRecordSize));
            byte rawType = (byte)(raw & DbxFormat.AttributeTypeByteMask);
            uint value = (raw >> DbxFormat.AttributeValueShift) & DbxFormat.AttributeValueMask;

            long candidateOffset = dataAreaStart + value;
            int? dataOffset = (rawType & DbxFormat.AttributeDirectFlag) != 0 || candidateOffset >= source.Length
                ? null
                : (int)candidateOffset;

            records.Add(new DbxAttributeRecord(rawType, value, dataOffset));
        }

        return records;
    }
}
