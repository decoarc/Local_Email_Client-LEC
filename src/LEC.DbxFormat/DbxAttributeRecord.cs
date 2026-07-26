namespace LEC.DbxFormat;

public readonly record struct DbxAttributeRecord(byte RawType, uint Value, int? DataOffset)
{
    public byte FieldId => (byte)(RawType & DbxFormat.AttributeFieldIdMask);

    public bool IsDirect => (RawType & DbxFormat.AttributeDirectFlag) != 0;
}
