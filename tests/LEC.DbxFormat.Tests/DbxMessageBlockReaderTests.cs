// tests/LEC.DbxFormat.Tests/DbxMessageBlockReaderTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxMessageBlockReaderTests
{
    [Fact]
    public void ReassemblesAChainOfTwoBlocksInOrder()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        // Second block is written first so we know its offset up front.
        byte[] secondPart = Encoding.ASCII.GetBytes("World");
        int secondOffset = builder.WriteMessageBlock(secondPart, nextOffset: 0);

        byte[] firstPart = Encoding.ASCII.GetBytes("Hello");
        int firstOffset = builder.WriteMessageBlock(firstPart, nextOffset: secondOffset);

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var result = DbxMessageBlockReader.ReadChain(source, firstOffset);

        Assert.False(result.Truncated);
        Assert.Equal("HelloWorld", Encoding.ASCII.GetString(result.Data));
    }

    [Fact]
    public void DoesNotHangOnASelfReferencingBlockAndReportsTruncation()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        int offset = builder.Position;
        builder.WriteMessageBlock(Encoding.ASCII.GetBytes("loop"), nextOffset: offset);

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var result = DbxMessageBlockReader.ReadChain(source, offset);

        Assert.True(result.Truncated);
        Assert.Equal("loop", Encoding.ASCII.GetString(result.Data));
    }

    [Fact]
    public void TreatsAnOutOfRangeStartOffsetAsAnEmptyTruncatedMessage()
    {
        using var source = new ByteArrayDbxSource(new byte[16]);
        var result = DbxMessageBlockReader.ReadChain(source, startOffset: 9999);

        Assert.True(result.Truncated);
        Assert.Empty(result.Data);
    }
}
