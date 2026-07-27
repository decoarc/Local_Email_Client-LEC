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

    [Fact]
    public async Task EnumeratesHeadersForALoadableFolder()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int inboxNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
        });
        int foldersRoot = localFolders.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, foldersRoot);

        var inboxMessages = new TestDbxBuilder();
        inboxMessages.WriteHeader(DbxFileType.Email);
        int messageInfoNode = inboxMessages.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = "Ola\0"u8.ToArray() },
        });
        int messagesRoot = inboxMessages.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: messageInfoNode, entryNextTable: 0, entryIndexCount: 0) });
        inboxMessages.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        inboxMessages.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, messagesRoot);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());
        profile.WriteEmailDbx("Inbox.dbx", inboxMessages.ToArray());

        using var store = DbxStore.Open(profile.ProfileDirectory);
        var folder = store.Folders.Single();
        Assert.True(folder.IsLoadable);

        var headers = new List<MessageHeader>();
        await foreach (var header in store.EnumerateHeadersAsync(folder))
            headers.Add(header);

        Assert.Single(headers);
        Assert.Equal("Ola", headers[0].Subject);
        Assert.Equal(folder.Id, headers[0].Id.FolderId);
    }

    [Fact]
    public async Task EnumerateHeadersReturnsEmptyForAFolderWithNoBackingFile()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int node = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Pastas locais\0"u8.ToArray() },
        });
        int root = localFolders.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: node, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());

        using var store = DbxStore.Open(profile.ProfileDirectory);
        var folder = store.Folders.Single();

        var headers = new List<MessageHeader>();
        await foreach (var header in store.EnumerateHeadersAsync(folder))
            headers.Add(header);

        Assert.Empty(headers);
    }

    [Fact]
    public async Task EnumerateHeadersReturnsEmptyWhenTheBackingFileExistsButIsNotAValidDbx()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int node = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Quebrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Quebrada.dbx\0"u8.ToArray() },
        });
        int root = localFolders.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: node, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());
        profile.WriteEmailDbx("Quebrada.dbx", new byte[64]); // exists, but not a valid .dbx signature

        using var store = DbxStore.Open(profile.ProfileDirectory);
        var folder = store.Folders.Single();
        Assert.True(folder.IsLoadable); // file exists — the cheap upfront check can't know it's garbage

        var headers = new List<MessageHeader>();
        await foreach (var header in store.EnumerateHeadersAsync(folder))
            headers.Add(header);

        Assert.Empty(headers); // degrades silently rather than throwing
    }

    [Fact]
    public async Task OpenMessageAsyncReturnsTheRawReassembledBytes()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int inboxNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
        });
        int foldersRoot = localFolders.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, foldersRoot);

        var inboxMessages = new TestDbxBuilder();
        inboxMessages.WriteHeader(DbxFileType.Email);
        byte[] body = "From: a@b.com\r\nSubject: Ola\r\n\r\nCorpo"u8.ToArray();
        int blockOffset = inboxMessages.WriteMessageBlock(body, nextOffset: 0);
        int messageInfoNode = inboxMessages.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)blockOffset },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = "Ola\0"u8.ToArray() },
        });
        int messagesRoot = inboxMessages.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: messageInfoNode, entryNextTable: 0, entryIndexCount: 0) });
        inboxMessages.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        inboxMessages.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, messagesRoot);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());
        profile.WriteEmailDbx("Inbox.dbx", inboxMessages.ToArray());

        using var store = DbxStore.Open(profile.ProfileDirectory);
        var folder = store.Folders.Single();

        MessageHeader? header = null;
        await foreach (var h in store.EnumerateHeadersAsync(folder))
            header = h;
        Assert.NotNull(header);

        await using var stream = await store.OpenMessageAsync(header!.Id);
        using var reader = new StreamReader(stream);
        string raw = await reader.ReadToEndAsync();

        Assert.Equal("From: a@b.com\r\nSubject: Ola\r\n\r\nCorpo", raw);
    }
}
