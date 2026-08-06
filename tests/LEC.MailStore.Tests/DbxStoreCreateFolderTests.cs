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
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);

            await store.CreateFolderAsync("Mail 2026", parentId: null);

            string backupPath = Assert.Single(Directory.GetFiles(backupRoot, "Folders.dbx.bak", SearchOption.AllDirectories));
            Assert.Equal(originalBytes, File.ReadAllBytes(backupPath));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
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

    [Fact]
    public async Task CreateFolderAsyncCollidingWithASiblingNameIgnoringCaseThrows()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateFolderAsync("CAIXA DE ENTRADA", parentId: null));
            Assert.Contains("já existe", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("nul.txt")]
    public async Task CreateFolderAsyncWithAReservedDeviceNameThrows(string reservedName)
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateFolderAsync(reservedName, parentId: null));
            Assert.Contains("reservado", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(dir, reservedName + ".dbx")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncWhenFoldersDbxIsDamagedThrowsAndLeavesEverythingUntouched()
    {
        // Build a Folders.dbx whose header item count (5) doesn't match what the index tree
        // actually yields (1 real entry) -- the same mismatch DbxFolderFileTests uses to
        // exercise StoreHealth.Partial. DbxStore.Open only reads Health once at open time, so
        // reopen this exact file fresh inside CreateFolderAsync's own DbxFolderFile.Open call
        // to make sure it - not just DbxStore.Open - is what gets checked.
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);
        int root = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Pastas locais\0"u8.ToArray() },
        });
        int rootIndexOffset = folders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: root, entryNextTable: 0, entryIndexCount: 0) });
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 5); // claims 5, tree only yields 1
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, rootIndexOffset);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateFolderAsync("Mail 2026", parentId: null));
            Assert.Contains("danificado", ex.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Folders.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Mail 2026.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
            Assert.False(Directory.Exists(backupRoot));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncKeepsThePristineBackupAcrossMultipleCreates()
    {
        string dir = BuildProfileWithOneFolder();
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);

            await store.CreateFolderAsync("Mail 2026", parentId: null);
            await store.CreateFolderAsync("Mail 2027", parentId: null);

            string pristinePath = Assert.Single(Directory.GetFiles(backupRoot, "Folders.dbx.original", SearchOption.AllDirectories));
            Assert.Equal(originalBytes, File.ReadAllBytes(pristinePath));

            // The rolling .bak reflects the state just before the *last* create, so it must
            // no longer match the very first bytes once more than one folder was created.
            string bakPath = Assert.Single(Directory.GetFiles(backupRoot, "Folders.dbx.bak", SearchOption.AllDirectories));
            Assert.NotEqual(originalBytes, File.ReadAllBytes(bakPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
}
