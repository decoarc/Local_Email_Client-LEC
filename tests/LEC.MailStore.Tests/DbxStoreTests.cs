// tests/LEC.MailStore.Tests/DbxStoreTests.cs
using System.Linq;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using LEC.MailStore;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreTests
{
    [Fact]
    public void OpensAProfileAndMapsFolders()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int localNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            // -1 cannot be encoded as a direct value (direct storage is a 24-bit payload,
            // per DbxFormat.AttributeValueMask) — this is exactly why the real file stores
            // this specific sentinel as a pointer-form 4-byte int rather than inline.
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Pastas locais\0"u8.ToArray() },
        });
        int inboxNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 2 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.MessageCount, Direct = true, DirectValue = 5 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });
        int root = localFolders.WriteIndexNode(
            nextTable: 0, ptrCount: 2, indexCount: 0,
            entries: new[] { (indexPtr: localNode, entryNextTable: 0, entryIndexCount: 0),
                              (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 2);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());
        // No Inbox.dbx written — deliberately, to also prove IsLoadable == false below.

        using var store = DbxStore.Open(profile.ProfileDirectory);

        Assert.Equal(2, store.Folders.Count);

        var pastasLocais = store.Folders.Single(f => f.Id == 1);
        Assert.Null(pastasLocais.ParentId); // -1 sentinel maps to null
        Assert.Equal("Pastas locais", pastasLocais.Name);
        Assert.True(pastasLocais.IsLoadable); // no backing file at all — trivially "loadable" (nothing to load)

        var inbox = store.Folders.Single(f => f.Id == 2);
        Assert.Equal(1, inbox.ParentId);
        Assert.Equal("Caixa de entrada", inbox.Name);
        Assert.Equal(SpecialFolderKind.Inbox, inbox.SpecialFolderKind);
        Assert.Equal(5, inbox.MessageCount);
        Assert.False(inbox.IsLoadable); // Inbox.dbx does not exist on disk
    }

    [Fact]
    public void PassesThroughADanglingParentIdUnresolved()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int orphanNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 73 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Direct = true, DirectValue = 53 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "seg\0"u8.ToArray() },
        });
        int root = localFolders.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: orphanNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());

        using var store = DbxStore.Open(profile.ProfileDirectory);

        var orphan = store.Folders.Single();
        Assert.Equal(53, orphan.ParentId); // passed through as-is, not resolved/dropped here
    }
}
