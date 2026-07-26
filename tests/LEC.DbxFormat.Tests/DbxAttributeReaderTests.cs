// tests/LEC.DbxFormat.Tests/DbxAttributeReaderTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxAttributeReaderTests
{
    [Fact]
    public void DecodesDirectAndPointerFormAttributes()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        int infoNodeOffset = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = 0x11, Direct = true, DirectValue = 42 },
            new TestDbxBuilder.AttributeSpec { FieldId = 0x08, Direct = false, Data = Encoding.ASCII.GetBytes("Hello\0") },
        });

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var records = DbxAttributeReader.Read(source, infoNodeOffset);

        Assert.Equal(2, records.Count);

        Assert.Equal(0x11, records[0].FieldId);
        Assert.True(records[0].IsDirect);
        Assert.Equal(42u, records[0].Value);
        Assert.Null(records[0].DataOffset);

        Assert.Equal(0x08, records[1].FieldId);
        Assert.False(records[1].IsDirect);
        Assert.NotNull(records[1].DataOffset);
        Assert.Equal("Hello", Encoding.ASCII.GetString(source.ReadCStringBytes(records[1].DataOffset!.Value, 100)));
    }

    [Fact]
    public void ReturnsEmptyForAnInfoNodeOffsetPastEndOfFile()
    {
        using var source = new ByteArrayDbxSource(new byte[8]);
        var records = DbxAttributeReader.Read(source, infoNodeOffset: 100);
        Assert.Empty(records);
    }

    [Fact]
    public void ReturnsEmptyWhenAttributeCountClaimsMoreDataThanFitsInTheFile()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        int infoNodeOffset = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = 0x11, Direct = true, DirectValue = 42 },
        });

        // Get the buffer and corrupt the count byte
        byte[] buffer = builder.ToArray();

        // Corrupt the count byte at infoNodeOffset + 10 to claim 200 attributes.
        // This makes dataAreaStart = arrayStart + AttributeRecordSize * 200 far exceed the buffer length,
        // triggering the second bounds check (dataAreaStart > source.Length).
        buffer[infoNodeOffset + 10] = 200;

        using var source = new ByteArrayDbxSource(buffer);
        var records = DbxAttributeReader.Read(source, infoNodeOffset);

        Assert.Empty(records);
    }
}
