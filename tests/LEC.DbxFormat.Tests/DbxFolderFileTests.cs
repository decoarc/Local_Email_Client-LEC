using System.Linq;
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxFolderFileTests
{
    [Fact]
    public void OpensASyntheticFolderTreeAndLinksParentIds()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Folder);

        int root = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = Encoding.ASCII.GetBytes("Pastas locais\0") },
        });

        int inbox = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 96 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = Encoding.ASCII.GetBytes("Caixa de entrada\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });

        int rootIndexOffset = builder.WriteIndexNode(
            nextTable: 0, ptrCount: 2, indexCount: 0,
            entries: new[] { (indexPtr: root, entryNextTable: 0, entryIndexCount: 0),
                              (indexPtr: inbox, entryNextTable: 0, entryIndexCount: 0) });

        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 2);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, rootIndexOffset);

        using var file = DbxFolderFile.OpenFrom(new ByteArrayDbxSource(builder.ToArray()), ownsSource: true);

        Assert.Equal(StoreHealth.Ok, file.Health);
        Assert.Equal(2, file.Folders.Count);

        var inboxFolder = file.Folders.Single(f => f.FolderId == 96);
        Assert.Equal(1, inboxFolder.ParentId);
        Assert.Equal("Caixa de entrada", inboxFolder.Name);
        Assert.Equal(DbxSpecialFolderType.Inbox, inboxFolder.SpecialFolderType);
    }

    [Fact]
    public void ReportsPartialHealthWhenHeaderItemCountDoesNotMatchTheTreeYield()
    {
        // The header's own item count is the strongest available integrity check --
        // a file whose header claims N entries but whose index tree only yields fewer
        // (or more) must not be reported StoreHealth.Ok.
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Folder);

        int root = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = Encoding.ASCII.GetBytes("Pastas locais\0") },
        });

        // Only one real entry is wired into the index tree...
        int rootIndexOffset = builder.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: root, entryNextTable: 0, entryIndexCount: 0) });

        // ...but the header claims 5.
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 5);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, rootIndexOffset);

        using var file = DbxFolderFile.OpenFrom(new ByteArrayDbxSource(builder.ToArray()), ownsSource: true);

        Assert.Equal(StoreHealth.Partial, file.Health);
        Assert.Single(file.Folders);
    }

    [Fact]
    public void DoesNotThrowWhenAPointerFormIntegerAttributeOffsetIsOutOfRange()
    {
        // Regression test mirroring DbxEmailFileTests' equivalent for DbxFolderInfo's ReadInt
        // path: a corrupted pointer-form MessageCount attribute whose computed offset lands
        // past EOF must resolve to DataOffset = null (per the prior DbxAttributeReader fix),
        // and the fixed ReadInt must treat that as 0 rather than throwing
        // InvalidOperationException via `a.DataOffset!.Value`.
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Folder);

        byte[] messageCountBytes = BitConverter.GetBytes(7); // arbitrary valid int32, pointer-form (4-byte read)
        var attrs = new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.MessageCount, Data = messageCountBytes }, // pointer-form, exercises ReadInt's ReadInt32 path
        };
        int infoNodeOffset = builder.WriteInfoNode(attrs);

        int rootIndexOffset = builder.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: infoNodeOffset, entryNextTable: 0, entryIndexCount: 0) });

        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, rootIndexOffset);

        byte[] data = builder.ToArray();

        // Patch the MessageCount attribute record (index 1 of 2) so its computed candidate
        // offset lands well past EOF, reproducing the null-DataOffset case.
        int arrayStart = infoNodeOffset + DbxFormat.InfoNodeAttributeArrayOffset;
        int dataAreaStart = arrayStart + DbxFormat.AttributeRecordSize * attrs.Length;
        int recordPos = arrayStart + 1 * DbxFormat.AttributeRecordSize;

        uint rawRecord = BitConverter.ToUInt32(data, recordPos);
        byte typeByte = (byte)(rawRecord & 0xFF);
        uint newValue = (uint)(data.Length - dataAreaStart + 1000) & DbxFormat.AttributeValueMask; // well past EOF
        uint newRecord = typeByte | (newValue << DbxFormat.AttributeValueShift);
        BitConverter.GetBytes(newRecord).CopyTo(data, recordPos);

        using var file = DbxFolderFile.OpenFrom(new ByteArrayDbxSource(data), ownsSource: true);

        Assert.Equal(StoreHealth.Ok, file.Health);
        Assert.Single(file.Folders);
        Assert.Equal(0, file.Folders[0].MessageCount);
    }

    [Fact]
    public void ReportsCorruptHealthWhenTheHeaderIsTruncated()
    {
        // Valid Folder signature (first 16 bytes) but far shorter than the 232-byte
        // header region DbxFormat.HeaderIndexPointerOffset+4 requires.
        var data = new byte[20];
        BitConverter.GetBytes(DbxFormat.SignatureWord0).CopyTo(data, 0);
        BitConverter.GetBytes(DbxFormat.SignatureWord1Folder).CopyTo(data, 4);
        BitConverter.GetBytes(DbxFormat.SignatureWord2).CopyTo(data, 8);
        BitConverter.GetBytes(DbxFormat.SignatureWord3).CopyTo(data, 12);

        using var file = DbxFolderFile.OpenFrom(new ByteArrayDbxSource(data), ownsSource: true);

        Assert.Equal(StoreHealth.Corrupt, file.Health);
        Assert.Empty(file.Folders);
    }

    private static string? FindRealFixture(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LEC.sln")))
            dir = dir.Parent;

        if (dir is null)
            return null;

        string candidate = Path.Combine(dir.FullName, "BCP", relativePath);
        return File.Exists(candidate) ? candidate : null;
    }

    [Fact]
    public void RealFixture_FoldersDbx_MatchesTheVerifiedRealTree()
    {
        string? path = FindRealFixture("Folders.dbx");
        if (path is null)
            return; // BCP/ not present on this machine — soft skip

        using var file = DbxFolderFile.Open(path);

        Assert.NotEqual(StoreHealth.Corrupt, file.Health);
        // Verified by direct hex/attribute inspection before this plan was written:
        // BCP/Folders.dbx has exactly 26 entries.
        Assert.Equal(26, file.Folders.Count);

        var inbox = file.Folders.Single(f => f.SpecialFolderType == DbxSpecialFolderType.Inbox);
        Assert.Equal("Caixa de entrada", inbox.Name);
        Assert.Equal("Caixa de entrada (1).dbx", inbox.FileName);
    }
}
