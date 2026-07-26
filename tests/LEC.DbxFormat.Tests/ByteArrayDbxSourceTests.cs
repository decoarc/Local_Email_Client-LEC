using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class ByteArrayDbxSourceTests
{
    [Fact]
    public void ReadsPrimitivesAtGivenOffsets()
    {
        byte[] data = new byte[32];
        BitConverter.GetBytes((short)0x1234).CopyTo(data, 0);
        BitConverter.GetBytes(0x11223344).CopyTo(data, 4);
        BitConverter.GetBytes(0x1122334455667788L).CopyTo(data, 8);
        data[20] = 0xAB;

        using var source = new ByteArrayDbxSource(data);

        Assert.Equal((short)0x1234, source.ReadInt16(0));
        Assert.Equal(0x11223344, source.ReadInt32(4));
        Assert.Equal(0x1122334455667788L, source.ReadInt64(8));
        Assert.Equal((byte)0xAB, source.ReadByte(20));
        Assert.Equal(32, source.Length);
    }

    [Fact]
    public void ReadCStringBytesStopsAtNulOrMaxLength()
    {
        byte[] data = "hello\0world"u8.ToArray();
        using var source = new ByteArrayDbxSource(data);

        Assert.Equal("hello"u8.ToArray(), source.ReadCStringBytes(0, 100));
        Assert.Equal("wor"u8.ToArray(), source.ReadCStringBytes(6, 3));
        Assert.Empty(source.ReadCStringBytes(100, 10)); // past end of buffer
    }
}
