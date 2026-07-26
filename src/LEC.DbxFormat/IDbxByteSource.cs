namespace LEC.DbxFormat;

public interface IDbxByteSource : IDisposable
{
    long Length { get; }
    byte ReadByte(int offset);
    short ReadInt16(int offset);
    int ReadInt32(int offset);
    long ReadInt64(int offset);
    void ReadBytes(int offset, Span<byte> destination);
    byte[] ReadCStringBytes(int offset, int maxLength);
}
