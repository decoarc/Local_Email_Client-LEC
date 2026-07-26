// tests/LEC.DbxFormat.Tests/TestDbxBuilderTests.cs
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class TestDbxBuilderTests
{
    [Fact]
    public void WriteHeaderProducesADetectableEmailSignature()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        using var source = new ByteArrayDbxSource(builder.ToArray());
        Assert.True(DbxHeader.TryDetectType(source, out var type));
        Assert.Equal(DbxFileType.Email, type);
    }

    [Fact]
    public void WriteHeaderProducesADetectableFolderSignature()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Folder);

        using var source = new ByteArrayDbxSource(builder.ToArray());
        Assert.True(DbxHeader.TryDetectType(source, out var type));
        Assert.Equal(DbxFileType.Folder, type);
    }

    [Fact]
    public void SetInt32AtOverwritesBytesInPlace()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 7);

        using var source = new ByteArrayDbxSource(builder.ToArray());
        Assert.Equal(7, source.ReadInt32(DbxFormat.HeaderItemCountOffset));
    }
}
