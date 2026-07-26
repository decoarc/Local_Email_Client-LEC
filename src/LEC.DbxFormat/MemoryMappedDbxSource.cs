using System.IO.MemoryMappedFiles;

namespace LEC.DbxFormat;

public sealed class MemoryMappedDbxSource : IDbxByteSource
{
    private readonly FileStream _fileStream;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    public MemoryMappedDbxSource(string path)
    {
        _fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Length = _fileStream.Length;
        _mmf = MemoryMappedFile.CreateFromFile(
            _fileStream, mapName: null, capacity: 0,
            MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public long Length { get; }

    public byte ReadByte(int offset) => _accessor.ReadByte(offset);

    public short ReadInt16(int offset) => _accessor.ReadInt16(offset);

    public int ReadInt32(int offset) => _accessor.ReadInt32(offset);

    public long ReadInt64(int offset) => _accessor.ReadInt64(offset);

    public void ReadBytes(int offset, Span<byte> destination)
    {
        byte[] buffer = new byte[destination.Length];
        _accessor.ReadArray(offset, buffer, 0, buffer.Length);
        buffer.CopyTo(destination);
    }

    public byte[] ReadCStringBytes(int offset, int maxLength)
    {
        if (offset < 0 || offset >= Length)
            return Array.Empty<byte>();

        var bytes = new List<byte>();
        int limit = (int)Math.Min(Length, offset + (long)maxLength);
        for (int i = offset; i < limit; i++)
        {
            byte b = _accessor.ReadByte(i);
            if (b == 0)
                break;
            bytes.Add(b);
        }
        return bytes.ToArray();
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
        _fileStream.Dispose();
    }
}
