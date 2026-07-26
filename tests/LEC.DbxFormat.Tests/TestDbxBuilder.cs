// tests/LEC.DbxFormat.Tests/TestDbxBuilder.cs
using LEC.DbxFormat;

namespace LEC.DbxFormat.Tests;

/// <summary>
/// Assembles a synthetic, byte-exact .dbx buffer for tests, following the
/// verified layout in DbxFormat exactly. Not shipped code — test infrastructure.
/// </summary>
public sealed class TestDbxBuilder
{
    public sealed class AttributeSpec
    {
        public required byte FieldId { get; init; }
        public bool Direct { get; init; }
        public uint DirectValue { get; init; }
        public byte[]? Data { get; init; } // used when Direct == false
    }

    private readonly List<byte> _bytes = new();

    public int Position => _bytes.Count;

    public TestDbxBuilder WriteHeader(DbxFileType type)
    {
        uint word1 = type switch
        {
            DbxFileType.Email => DbxFormat.SignatureWord1Email,
            DbxFileType.Folder => DbxFormat.SignatureWord1Folder,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        WriteUInt32(DbxFormat.SignatureWord0);
        WriteUInt32(word1);
        WriteUInt32(DbxFormat.SignatureWord2);
        WriteUInt32(DbxFormat.SignatureWord3);

        while (_bytes.Count < DbxFormat.HeaderIndexPointerOffset + 4)
            _bytes.Add(0);

        return this;
    }

    public TestDbxBuilder SetInt32At(int offset, int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        for (int i = 0; i < 4; i++)
            _bytes[offset + i] = bytes[i];
        return this;
    }

    public int WriteIndexNode(int nextTable, byte ptrCount, int indexCount,
        IReadOnlyList<(int indexPtr, int entryNextTable, int entryIndexCount)> entries)
    {
        int start = _bytes.Count;
        WriteBytes(new byte[8]);       // 0..7: unused by the reader
        WriteInt32(nextTable);         // 8..11
        WriteBytes(new byte[5]);       // 12..16: unused
        _bytes.Add(ptrCount);          // 17
        WriteBytes(new byte[2]);       // 18..19: unused
        WriteInt32(indexCount);        // 20..23

        foreach (var entry in entries)
        {
            WriteInt32(entry.indexPtr);
            WriteInt32(entry.entryNextTable);
            WriteInt32(entry.entryIndexCount);
        }

        return start;
    }

    public int WriteInfoNode(IReadOnlyList<AttributeSpec> attrs)
    {
        int start = _bytes.Count;
        WriteBytes(new byte[8]);            // 0..7: unused ("size" etc., not read by our readers)
        _bytes.Add(0);                      // 8
        _bytes.Add(0);                      // 9
        _bytes.Add((byte)attrs.Count);      // 10: attribute count
        _bytes.Add(0);                      // 11

        int arrayStart = _bytes.Count;
        for (int i = 0; i < attrs.Count; i++)
            WriteBytes(new byte[4]); // reserve; back-patched below

        int dataAreaStart = _bytes.Count;
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
                int relativeOffset = _bytes.Count - dataAreaStart;
                recordValue = (uint)relativeOffset & DbxFormat.AttributeValueMask;
                WriteBytes(attr.Data ?? Array.Empty<byte>());
            }

            uint record = typeByte | (recordValue << DbxFormat.AttributeValueShift);
            byte[] recordBytes = BitConverter.GetBytes(record);
            for (int b = 0; b < 4; b++)
                _bytes[arrayStart + i * 4 + b] = recordBytes[b];
        }

        return start;
    }

    public int WriteMessageBlock(byte[] data, int nextOffset)
    {
        if (data.Length == 0 || data.Length > DbxFormat.MessageBlockMaxDataSize)
            throw new ArgumentException($"Block data must be 1..{DbxFormat.MessageBlockMaxDataSize} bytes.");

        int start = _bytes.Count;
        WriteBytes(new byte[8]);              // 0..7: unused by the reader
        WriteInt16((short)data.Length);       // 8..9
        WriteBytes(new byte[2]);              // 10..11: unused
        WriteInt32(nextOffset);               // 12..15
        WriteBytes(data);                     // 16..

        return start;
    }

    public byte[] ToArray() => _bytes.ToArray();

    private void WriteBytes(byte[] bytes) => _bytes.AddRange(bytes);

    private void WriteInt16(short value) => _bytes.AddRange(BitConverter.GetBytes(value));

    private void WriteInt32(int value) => _bytes.AddRange(BitConverter.GetBytes(value));

    private void WriteUInt32(uint value) => _bytes.AddRange(BitConverter.GetBytes(value));
}
