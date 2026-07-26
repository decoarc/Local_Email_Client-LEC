// tests/LEC.DbxFormat.Tests/DbxEmailFileTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxEmailFileTests
{
    private static byte[] BuildSingleMessageDbx(out int itemCount)
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        byte[] body = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Hi\r\n\r\nBody");
        int blockOffset = builder.WriteMessageBlock(body, nextOffset: 0);

        int infoNodeOffset = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)blockOffset },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = Encoding.ASCII.GetBytes("Hi\0") },
        });

        int rootOffset = builder.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: infoNodeOffset, entryNextTable: 0, entryIndexCount: 0) });

        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, rootOffset);

        itemCount = 1;
        return builder.ToArray();
    }

    [Fact]
    public void OpensAWellFormedSyntheticFileAsHealthy()
    {
        byte[] data = BuildSingleMessageDbx(out _);
        using var file = DbxEmailFile.OpenFrom(new ByteArrayDbxSource(data), ownsSource: true);

        Assert.Equal(StoreHealth.Ok, file.Health);
        Assert.Single(file.Messages);
        Assert.Equal("Hi", file.Messages[0].Subject);

        byte[] raw = file.ReadMessageRaw(file.Messages[0], out bool truncated);
        Assert.False(truncated);
        Assert.Equal("From: a@b.com\r\nSubject: Hi\r\n\r\nBody", Encoding.ASCII.GetString(raw));
    }

    [Fact]
    public void ReportsPartialHealthWhenTheIndexRootIsOutOfRange()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, 999_999); // beyond EOF

        using var file = DbxEmailFile.OpenFrom(new ByteArrayDbxSource(builder.ToArray()), ownsSource: true);

        Assert.Equal(StoreHealth.Partial, file.Health);
        Assert.Empty(file.Messages);
    }

    [Fact]
    public void DoesNotThrowWhenAnAttributeOffsetIsOneByteFromEndOfFile()
    {
        // Regression test for the whole-branch-review Critical finding: a pointer-form
        // attribute whose computed DataOffset lands one byte before EOF (in bounds, but
        // with insufficient trailing bytes to complete a multi-byte read) must not crash
        // the parse. BitConverter.ToInt64 throws a plain ArgumentException (NOT
        // ArgumentOutOfRangeException) in that case, which previously escaped OpenFrom
        // entirely because the catch filter only listed ArgumentOutOfRangeException.
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        byte[] body = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Hi\r\n\r\nBody");
        int blockOffset = builder.WriteMessageBlock(body, nextOffset: 0);

        byte[] fileTimeBytes = BitConverter.GetBytes(132223104000000000L); // arbitrary valid FILETIME
        var attrs = new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)blockOffset },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.SendCreateTime, Data = fileTimeBytes }, // pointer-form, 8-byte read
        };
        int infoNodeOffset = builder.WriteInfoNode(attrs);

        int rootOffset = builder.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: infoNodeOffset, entryNextTable: 0, entryIndexCount: 0) });

        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, rootOffset);

        byte[] data = builder.ToArray();

        // Patch the SendCreateTime attribute record (index 1 of 2) so its computed
        // DataOffset is Length - 1: in bounds (so the Fix-1b null-out doesn't apply),
        // but with only 1 trailing byte where an 8-byte FILETIME read is attempted.
        int arrayStart = infoNodeOffset + DbxFormat.InfoNodeAttributeArrayOffset;
        int dataAreaStart = arrayStart + DbxFormat.AttributeRecordSize * attrs.Length;
        int recordPos = arrayStart + 1 * DbxFormat.AttributeRecordSize;

        uint rawRecord = BitConverter.ToUInt32(data, recordPos);
        byte typeByte = (byte)(rawRecord & 0xFF);
        uint newValue = (uint)((data.Length - 1) - dataAreaStart) & DbxFormat.AttributeValueMask;
        uint newRecord = typeByte | (newValue << DbxFormat.AttributeValueShift);
        BitConverter.GetBytes(newRecord).CopyTo(data, recordPos);

        using var file = DbxEmailFile.OpenFrom(new ByteArrayDbxSource(data), ownsSource: true);

        Assert.Equal(StoreHealth.Partial, file.Health);
        Assert.Empty(file.Messages); // the message that hit the bad offset is dropped, not the whole parse
    }

    [Fact]
    public void ReportsPartialHealthWhenHeaderItemCountDoesNotMatchTheTreeYield()
    {
        // The header's own item count is the strongest available integrity check --
        // a file whose header claims N entries but whose index tree only yields fewer
        // (or more) must not be reported StoreHealth.Ok.
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        byte[] body = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Hi\r\n\r\nBody");
        int blockOffset = builder.WriteMessageBlock(body, nextOffset: 0);

        int infoNodeOffset = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)blockOffset },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = Encoding.ASCII.GetBytes("Hi\0") },
        });

        // Only one real entry is wired into the index tree...
        int rootOffset = builder.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: infoNodeOffset, entryNextTable: 0, entryIndexCount: 0) });

        // ...but the header claims 5.
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 5);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, rootOffset);

        using var file = DbxEmailFile.OpenFrom(new ByteArrayDbxSource(builder.ToArray()), ownsSource: true);

        Assert.Equal(StoreHealth.Partial, file.Health);
        Assert.Single(file.Messages);
    }

    [Fact]
    public void ThrowsForAFileWithTheWrongSignature()
    {
        using var source = new ByteArrayDbxSource(new byte[256]);
        Assert.Throws<DbxFormatException>(() => DbxEmailFile.OpenFrom(source, ownsSource: true));
    }

    [Fact]
    public void ReportsCorruptHealthWhenTheHeaderIsTruncated()
    {
        // Create a buffer with valid Email signature (16 bytes) but truncated (20 bytes total).
        // This is shorter than DbxFormat.HeaderIndexPointerOffset + 4 (232 bytes),
        // so the file should be reported as Corrupt with an empty message list.
        var data = new byte[20];
        BitConverter.GetBytes(DbxFormat.SignatureWord0).CopyTo(data, 0);
        BitConverter.GetBytes(DbxFormat.SignatureWord1Email).CopyTo(data, 4);
        BitConverter.GetBytes(DbxFormat.SignatureWord2).CopyTo(data, 8);
        BitConverter.GetBytes(DbxFormat.SignatureWord3).CopyTo(data, 12);
        // Remaining 4 bytes (16..19) are padding (already zero)

        using var file = DbxEmailFile.OpenFrom(new ByteArrayDbxSource(data), ownsSource: true);

        Assert.Equal(StoreHealth.Corrupt, file.Health);
        Assert.Empty(file.Messages);
    }

    private static string? FindRealFixture(string relativePath)
    {
        // Walks up from the test's output directory to find the repo root (marker: LEC.sln),
        // then looks for BCP/<relativePath>. BCP/ is git-ignored and only present on the
        // developer's machine — tests skip gracefully (rather than fail) when it's absent.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LEC.sln")))
            dir = dir.Parent;

        if (dir is null)
            return null;

        string candidate = Path.Combine(dir.FullName, "BCP", relativePath);
        return File.Exists(candidate) ? candidate : null;
    }

    [Fact]
    public void RealFixture_RascunhosDbx_OpensHealthyWithReadableMessages()
    {
        string? path = FindRealFixture("Rascunhos.dbx");
        if (path is null)
            return; // BCP/ not present on this machine — soft skip

        using var file = DbxEmailFile.Open(path);

        Assert.NotEqual(StoreHealth.Corrupt, file.Health);
        Assert.NotEmpty(file.Messages);

        byte[] raw = file.ReadMessageRaw(file.Messages[0], out _);
        string text = Encoding.Latin1.GetString(raw);
        Assert.Contains("Subject:", text);
    }
}
