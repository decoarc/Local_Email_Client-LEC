// tests/LEC.DbxFormat.Tests/DbxHeaderTests.cs
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxHeaderTests
{
    private static byte[] HeaderBytes(uint word1)
    {
        var data = new byte[0xE8 + 4];
        BitConverter.GetBytes(DbxFormat.SignatureWord0).CopyTo(data, 0);
        BitConverter.GetBytes(word1).CopyTo(data, 4);
        BitConverter.GetBytes(DbxFormat.SignatureWord2).CopyTo(data, 8);
        BitConverter.GetBytes(DbxFormat.SignatureWord3).CopyTo(data, 12);
        return data;
    }

    [Fact]
    public void DetectsEmailSignature()
    {
        using var source = new ByteArrayDbxSource(HeaderBytes(DbxFormat.SignatureWord1Email));
        Assert.True(DbxHeader.TryDetectType(source, out var type));
        Assert.Equal(DbxFileType.Email, type);
    }

    [Fact]
    public void DetectsFolderSignature()
    {
        using var source = new ByteArrayDbxSource(HeaderBytes(DbxFormat.SignatureWord1Folder));
        Assert.True(DbxHeader.TryDetectType(source, out var type));
        Assert.Equal(DbxFileType.Folder, type);
    }

    [Fact]
    public void RejectsUnrecognizedSignature()
    {
        using var source = new ByteArrayDbxSource(new byte[64]); // all zeros
        Assert.False(DbxHeader.TryDetectType(source, out var type));
        Assert.Equal(DbxFileType.Unknown, type);
    }

    [Fact]
    public void RejectsTooShortFile()
    {
        using var source = new ByteArrayDbxSource(new byte[4]);
        Assert.False(DbxHeader.TryDetectType(source, out _));
    }
}
