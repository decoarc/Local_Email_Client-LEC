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
    public void ThrowsForAFileWithTheWrongSignature()
    {
        using var source = new ByteArrayDbxSource(new byte[256]);
        Assert.Throws<DbxFormatException>(() => DbxEmailFile.OpenFrom(source, ownsSource: true));
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
