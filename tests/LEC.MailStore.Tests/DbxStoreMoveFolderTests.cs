// tests/LEC.MailStore.Tests/DbxStoreMoveFolderTests.cs
using System.Linq;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using LEC.MailStore;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreMoveFolderTests
{
    // Builds a profile with:
    //   1  Caixa de entrada   (special: Inbox, root)            Inbox.dbx
    //   2  Notas              (root)                             Notas.dbx
    //   3  Notas 2026         (child of 2)                       Notas2026.dbx
    //   4  Notas 2026 Q1      (child of 3)                       NotasQ1.dbx
    //   5  Trabalho           (root)                             Trabalho.dbx
    //   6  Notas 2026         (child of 5 -- same name as 3, different parent) Trab2026.dbx
    private static string BuildProfile()
    {
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);

        int n1 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });
        int n2 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 2 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Notas.dbx\0"u8.ToArray() },
        });
        int n3 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 3 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(2) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas 2026\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Notas2026.dbx\0"u8.ToArray() },
        });
        int n4 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 4 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(3) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas 2026 Q1\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "NotasQ1.dbx\0"u8.ToArray() },
        });
        int n5 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 5 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Trabalho\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Trabalho.dbx\0"u8.ToArray() },
        });
        int n6 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 6 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(5) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas 2026\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Trab2026.dbx\0"u8.ToArray() },
        });

        int[] nodes = { n1, n2, n3, n4, n5, n6 };
        int root = folders.WriteIndexNode(nextTable: 0, ptrCount: (byte)nodes.Length, indexCount: 0,
            entries: nodes.Select(n => (indexPtr: n, entryNextTable: 0, entryIndexCount: 0)).ToArray());
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, nodes.Length);
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        foreach (string fileName in new[] { "Inbox.dbx", "Notas.dbx", "Notas2026.dbx", "NotasQ1.dbx", "Trabalho.dbx", "Trab2026.dbx" })
        {
            var empty = new TestDbxBuilder();
            empty.WriteHeader(DbxFileType.Email);
            empty.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 0);
            File.WriteAllBytes(Path.Combine(dir, fileName), empty.ToArray());
        }
        return dir;
    }

    [Fact]
    public async Task MoveFolderAsyncChangesParentIdAndBringsTheSubtreeAlong()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            // NOTE: folder 5 (Trabalho) already has a child (6) also named "Notas 2026",
            // so moving folder 3 there would collide (that exact collision is what
            // MoveFolderAsyncCollidingWithASiblingNameAtTheDestinationThrows below verifies
            // against this same fixture). To keep this test's own move collision-free, it
            // targets root instead.
            await store.MoveFolderAsync(3, newParentId: null); // Notas 2026 (with child 4) -> to root

            var moved = store.Folders.Single(f => f.Id == 3);
            Assert.Null(moved.ParentId);

            // The child never had its own entry touched -- it still points at folder 3,
            // which is what makes it "come along" without any extra work.
            var child = store.Folders.Single(f => f.Id == 4);
            Assert.Equal(3, child.ParentId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncToRootSetsParentIdToNull()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.MoveFolderAsync(4, newParentId: null);

            Assert.Null(store.Folders.Single(f => f.Id == 4).ParentId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncIntoItselfThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveFolderAsync(3, newParentId: 3));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncIntoOwnDescendantThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveFolderAsync(2, newParentId: 4)); // 4 is a grandchild of 2
            Assert.Contains(store.Folders, f => f.Id == 2 && f.ParentId == null); // unchanged
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncOnASpecialFolderThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveFolderAsync(1, newParentId: 2));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncToANonexistentParentThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveFolderAsync(3, newParentId: 999));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncCollidingWithASiblingNameAtTheDestinationThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            // Folder 5 (Trabalho) already has a child (6) named "Notas 2026" -- moving folder
            // 3 (also named "Notas 2026") there must collide, same rule as CreateFolderAsync.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveFolderAsync(3, newParentId: 5));
            Assert.Contains("já existe", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncUpdatesTheBackupFile()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.MoveFolderAsync(4, newParentId: null);

            Assert.True(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncPersistsAcrossReopen()
    {
        string dir = BuildProfile();
        try
        {
            using (var store = DbxStore.Open(dir))
                await store.MoveFolderAsync(3, newParentId: null);

            using var reopened = DbxStore.Open(dir);
            Assert.Null(reopened.Folders.Single(f => f.Id == 3).ParentId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncIntoAHiddenFolderThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);
            await store.DeleteFolderAsync(5); // hides 5 (and its child 6)

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveFolderAsync(3, newParentId: 5));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
