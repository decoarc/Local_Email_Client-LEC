// tests/LEC.DbxFormat.Tests/DbxMessageInfoTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxMessageInfoTests
{
    [Fact]
    public void ReadsEmailFieldsFromAnInfoNode()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        long sendFileTime = new DateTime(2005, 10, 21, 11, 11, 49, DateTimeKind.Utc).ToFileTimeUtc();

        int infoNodeOffset = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = 0x9000 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = Encoding.ASCII.GetBytes("Re: 13E03-B\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.SenderName, Data = Encoding.ASCII.GetBytes("BCP ENGENHARIA LTDA\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.SenderAddress, Data = Encoding.ASCII.GetBytes("bcp@bcpengenharia.com\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.SendCreateTime, Data = BitConverter.GetBytes(sendFileTime) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageSize, Direct = true, DirectValue = 1_255_918 },
        });

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var info = DbxMessageInfo.Read(source, infoNodeOffset);

        Assert.Equal(0x9000, info.MessageBlockOffset);
        Assert.Equal("Re: 13E03-B", info.Subject);
        Assert.Equal("BCP ENGENHARIA LTDA", info.SenderName);
        Assert.Equal("bcp@bcpengenharia.com", info.SenderAddress);
        Assert.Equal(new DateTime(2005, 10, 21, 11, 11, 49, DateTimeKind.Utc), info.SendTimeUtc);
        Assert.Equal(1_255_918u, info.MessageSize);
    }
}
