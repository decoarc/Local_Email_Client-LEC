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
