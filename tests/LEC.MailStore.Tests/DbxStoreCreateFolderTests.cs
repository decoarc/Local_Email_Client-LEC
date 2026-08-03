using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using LEC.MailStore;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreCreateFolderTests
{
    private static string BuildProfileWithOneFolder()
    {
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);
        int inboxNode = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.MessageCount, Direct = true, DirectValue = 5 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });
        int root = folders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        var emptyInbox = new TestDbxBuilder();
        emptyInbox.WriteHeader(DbxFileType.Email);
        emptyInbox.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 0);
        File.WriteAllBytes(Path.Combine(dir, "Inbox.dbx"), emptyInbox.ToArray());
        return dir;
    }

    [Fact]
    public async Task CreateFolderAsyncAddsATopLevelFolderVisibleAfterReopening()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            using (var store = DbxStore.Open(dir))
            {
                var created = await store.CreateFolderAsync("Mail 2026", parentId: null);

                Assert.Equal("Mail 2026", created.Name);
                Assert.Null(created.ParentId);
                Assert.True(File.Exists(Path.Combine(dir, "Mail 2026.dbx")));
                Assert.Contains(store.Folders, f => f.Id == created.Id);
            }

            using var reopened = DbxStore.Open(dir);
            Assert.Equal(2, reopened.Folders.Count);

            var inbox = reopened.Folders.Single(f => f.Name == "Caixa de entrada");
            Assert.Equal(SpecialFolderKind.Inbox, inbox.SpecialFolderKind);
            Assert.Equal(5, inbox.MessageCount);
            Assert.Contains(reopened.Folders, f => f.Name == "Mail 2026");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncCreatesABackupOfFoldersDbx()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir);

            await store.CreateFolderAsync("Mail 2026", parentId: null);

            string backupPath = Path.Combine(dir, "Folders.dbx.bak");
            Assert.True(File.Exists(backupPath));
            Assert.Equal(originalBytes, File.ReadAllBytes(backupPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncAsASubfolderSetsParentId()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            using var store = DbxStore.Open(dir);
            int parentId = store.Folders.Single(f => f.Name == "Caixa de entrada").Id;

            var created = await store.CreateFolderAsync("2026", parentId);

            Assert.Equal(parentId, created.ParentId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncOnANonexistentParentThrows()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateFolderAsync("X", parentId: 999));
            Assert.Contains("não encontrada", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncCollidingWithASiblingNameThrows()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateFolderAsync("Caixa de entrada", parentId: null));
            Assert.Contains("já existe", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncWithABlankNameThrows()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateFolderAsync("???", parentId: null));
            Assert.Contains("inválido", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncWhenTheBackingFileAlreadyExistsThrows()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Mail 2026.dbx"), new byte[] { 0 });
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateFolderAsync("Mail 2026", parentId: null));
            Assert.Contains("já existe", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
