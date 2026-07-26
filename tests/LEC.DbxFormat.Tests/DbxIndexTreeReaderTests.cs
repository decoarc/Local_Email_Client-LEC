// tests/LEC.DbxFormat.Tests/DbxIndexTreeReaderTests.cs
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxIndexTreeReaderTests
{
    [Fact]
    public void ReturnsAllEntryPointersFromALeafNode()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        int rootOffset = builder.WriteIndexNode(
            nextTable: 0, ptrCount: 2, indexCount: 0,
            entries: new[] { (indexPtr: 0x2000, entryNextTable: 0, entryIndexCount: 0),
                              (indexPtr: 0x3000, entryNextTable: 0, entryIndexCount: 0) });

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var reader = new DbxIndexTreeReader(source);

        var pointers = reader.ReadIndexPointers(rootOffset, out bool corrupt);

        Assert.False(corrupt);
        Assert.Equal(new[] { 0x2000, 0x3000 }, pointers);
    }

    [Fact]
    public void DoesNotHangOnASelfReferencingCycleAndReportsCorruption()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        int nodeOffset = builder.Position;
        // An entry whose own subtree (entryNextTable) points back at this same node,
        // with entryIndexCount > 0 so the reader is forced to recurse into it.
        builder.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: 0x2000, entryNextTable: nodeOffset, entryIndexCount: 1) });

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var reader = new DbxIndexTreeReader(source);

        var pointers = reader.ReadIndexPointers(nodeOffset, out bool corrupt);

        Assert.True(corrupt);
        Assert.Equal(new[] { 0x2000 }, pointers);
    }

    [Fact]
    public void ReportsCorruptionForAnOutOfRangeRootOffset()
    {
        using var source = new ByteArrayDbxSource(new byte[64]);
        var reader = new DbxIndexTreeReader(source);

        var pointers = reader.ReadIndexPointers(rootOffset: 10_000, out bool corrupt);

        Assert.True(corrupt);
        Assert.Empty(pointers);
    }
}
