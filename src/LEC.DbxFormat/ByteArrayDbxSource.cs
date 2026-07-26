namespace LEC.DbxFormat;

public sealed class ByteArrayDbxSource : IDbxByteSource
{
    private readonly byte[] _data;

    public ByteArrayDbxSource(byte[] data) => _data = data;

    public long Length => _data.Length;

    public byte ReadByte(int offset) => _data[offset];

    public short ReadInt16(int offset) => BitConverter.ToInt16(_data, offset);

    public int ReadInt32(int offset) => BitConverter.ToInt32(_data, offset);

    public long ReadInt64(int offset) => BitConverter.ToInt64(_data, offset);

    public void ReadBytes(int offset, Span<byte> destination) =>
        _data.AsSpan(offset, destination.Length).CopyTo(destination);

    public byte[] ReadCStringBytes(int offset, int maxLength)
    {
        if (offset < 0 || offset >= _data.Length)
            return Array.Empty<byte>();

        int end = offset;
        int limit = Math.Min(_data.Length, offset + maxLength);
        while (end < limit && _data[end] != 0)
            end++;

        return _data.AsSpan(offset, end - offset).ToArray();
    }

    public void Dispose() { }
}
