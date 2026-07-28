// tests/LEC.DbxFormat.Tests/DbxEmailFileScavengeTests.cs
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxEmailFileScavengeTests
{
    [Fact]
    public void ScavengeFindsAnOrphanBlockNotReferencedByTheNormalIndex()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        // One normal, indexed message.
        int normalBlock = builder.WriteMessageBlock("From: a@b.com\r\nSubject: Normal\r\n\r\nCorpo normal"u8.ToArray(), nextOffset: 0);
        int infoNode = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)normalBlock },
        });
        int indexRoot = builder.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: infoNode, entryNextTable: 0, entryIndexCount: 0) });
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, indexRoot);

        // A second, orphan message block -- written to the buffer but never referenced
        // by any info/index node. WriteMessageBlock alone is enough: it's a real,
        // self-referencing, 0x200-marked block, just not indexed.
        int orphanBlock = builder.WriteMessageBlock("From: c@d.com\r\nSubject: Orfa\r\n\r\nCorpo orfao"u8.ToArray(), nextOffset: 0);

        using var email = DbxEmailFile.OpenFrom(new ByteArrayDbxSource(builder.ToArray()), ownsSource: true);
        Assert.Single(email.Messages); // only the normal one, via the trusted index path

        var scavenged = email.Scavenge();

        var found = Assert.Single(scavenged);
        Assert.Equal(orphanBlock, found.ChainStartOffset);
        Assert.False(found.Truncated);
    }

    [Fact]
    public void ScavengeFindsNothingWhenEveryBlockIsAlreadyIndexed()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        int block = builder.WriteMessageBlock("From: a@b.com\r\nSubject: X\r\n\r\nY"u8.ToArray(), nextOffset: 0);
        int infoNode = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)block },
        });
        int indexRoot = builder.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: infoNode, entryNextTable: 0, entryIndexCount: 0) });
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, indexRoot);

        using var email = DbxEmailFile.OpenFrom(new ByteArrayDbxSource(builder.ToArray()), ownsSource: true);

        Assert.Empty(email.Scavenge());
    }

    [Fact]
    public void ReadRecoveredMessageRawReturnsTheOrphanBytesGivenItsChainOffset()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 0);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, 0);

        byte[] payload = "From: c@d.com\r\nSubject: Orfa\r\n\r\nCorpo orfao"u8.ToArray();
        int orphanBlock = builder.WriteMessageBlock(payload, nextOffset: 0);

        using var email = DbxEmailFile.OpenFrom(new ByteArrayDbxSource(builder.ToArray()), ownsSource: true);

        byte[] raw = email.ReadRecoveredMessageRaw(orphanBlock, out bool truncated);

        Assert.False(truncated);
        Assert.Equal(payload, raw);
    }
}
