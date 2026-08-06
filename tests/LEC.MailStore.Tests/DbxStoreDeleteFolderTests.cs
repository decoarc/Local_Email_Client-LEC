// tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs
using System.Linq;
using System.Text;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using LEC.MailStore;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreDeleteFolderTests
{
    // Builds a profile with:
    //   1  Caixa de entrada   (special: Inbox, root)            Inbox.dbx
    //   2  Notas              (root)                             Notas.dbx
    //   3  Notas 2026         (child of 2)                       Notas2026.dbx
    //   4  Notas 2026 Q1      (child of 3)                       NotasQ1.dbx
    //  10  Raiz                (root)                            (no backing file)
    //  11  Especial            (special: Inbox, child of 10)     EspecialInbox.dbx
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
        int n10 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 10 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Raiz\0"u8.ToArray() },
        });
        int n11 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 11 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(10) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Especial\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "EspecialInbox.dbx\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });

        int[] nodes = { n1, n2, n3, n4, n10, n11 };
        int root = folders.WriteIndexNode(nextTable: 0, ptrCount: (byte)nodes.Length, indexCount: 0,
            entries: nodes.Select(n => (indexPtr: n, entryNextTable: 0, entryIndexCount: 0)).ToArray());
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, nodes.Length);
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        foreach (string fileName in new[] { "Inbox.dbx", "Notas.dbx", "Notas2026.dbx", "NotasQ1.dbx", "EspecialInbox.dbx" })
        {
            var empty = new TestDbxBuilder();
            empty.WriteHeader(DbxFileType.Email);
            empty.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 0);
            File.WriteAllBytes(Path.Combine(dir, fileName), empty.ToArray());
        }
        return dir;
    }

    [Fact]
    public async Task DeleteFolderAsyncOnALeafFolderDeletesOnlyThatFolder()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.DeleteFolderAsync(4);

            Assert.DoesNotContain(store.Folders, f => f.Id == 4);
            Assert.Contains(store.Folders, f => f.Id == 3);
            Assert.Contains(store.Folders, f => f.Id == 2);
            Assert.False(File.Exists(Path.Combine(dir, "NotasQ1.dbx")));
            Assert.True(File.Exists(Path.Combine(dir, "Notas2026.dbx")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncDeletesTheWholeSubtreeAtOnce()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.DeleteFolderAsync(2); // Notas -> also deletes 3 (child) and 4 (grandchild)

            Assert.DoesNotContain(store.Folders, f => f.Id == 2);
            Assert.DoesNotContain(store.Folders, f => f.Id == 3);
            Assert.DoesNotContain(store.Folders, f => f.Id == 4);
            Assert.Contains(store.Folders, f => f.Id == 1); // Inbox untouched

            Assert.False(File.Exists(Path.Combine(dir, "Notas.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Notas2026.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "NotasQ1.dbx")));
            Assert.True(File.Exists(Path.Combine(dir, "Inbox.dbx")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncOnASpecialFolderThrowsAndChangesNothing()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(1));
            Assert.Contains("especia", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(store.Folders, f => f.Id == 1);
            Assert.True(File.Exists(Path.Combine(dir, "Inbox.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncWhoseSubtreeContainsASpecialFolderThrowsAndDeletesNothing()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(10)); // Raiz -> child 11 is special
            Assert.Contains("especia", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(store.Folders, f => f.Id == 10);
            Assert.Contains(store.Folders, f => f.Id == 11);
            Assert.True(File.Exists(Path.Combine(dir, "EspecialInbox.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncOnANonexistentFolderThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(999));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncCalledTwiceForTheSameIdThrowsOnTheSecondCall()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.DeleteFolderAsync(4);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(4));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncPersistsAcrossReopen()
    {
        string dir = BuildProfile();
        try
        {
            using (var store = DbxStore.Open(dir))
                await store.DeleteFolderAsync(2);

            using var reopened = DbxStore.Open(dir);
            Assert.DoesNotContain(reopened.Folders, f => f.Id == 2);
            Assert.DoesNotContain(reopened.Folders, f => f.Id == 3);
            Assert.DoesNotContain(reopened.Folders, f => f.Id == 4);
            Assert.Contains(reopened.Folders, f => f.Id == 1);
            Assert.False(File.Exists(Path.Combine(dir, "Notas.dbx")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncRewritesFoldersDbxAndCreatesBackups()
    {
        string dir = BuildProfile();
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir);

            await store.DeleteFolderAsync(4);

            Assert.NotEqual(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Folders.dbx")));
            Assert.True(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
            Assert.True(File.Exists(Path.Combine(dir, "Folders.dbx.original")));
            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Folders.dbx.original")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncWhenFoldersDbxIsDamagedThrowsAndDeletesNothing()
    {
        // Same header/tree item-count mismatch trick as
        // DbxStoreCreateFolderTests.CreateFolderAsyncWhenFoldersDbxIsDamagedThrowsAndLeavesEverythingUntouched
        // (claims 5 entries, the index tree only yields 1) -- DbxStore.Open only reads Health
        // once at open time, so this makes sure DeleteFolderAsync's own fresh
        // DbxFolderFile.Open call -- not just DbxStore.Open -- is what gets checked.
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);
        int root = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Notas.dbx\0"u8.ToArray() },
        });
        int rootIndexOffset = folders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: root, entryNextTable: 0, entryIndexCount: 0) });
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 5); // claims 5, tree only yields 1
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, rootIndexOffset);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        var empty = new TestDbxBuilder();
        empty.WriteHeader(DbxFileType.Email);
        empty.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 0);
        File.WriteAllBytes(Path.Combine(dir, "Notas.dbx"), empty.ToArray());
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(1));
            Assert.Contains("danificado", ex.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Folders.dbx")));
            Assert.True(File.Exists(Path.Combine(dir, "Notas.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.original")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncPurgesTheScavengeMovedAndDeletedMessageCachesForTheDeletedFolder()
    {
        string dir = BuildProfile();
        string scavengeDbPath = Path.Combine(Path.GetTempPath(), $"lec-scavenge-{Guid.NewGuid():N}.db");
        string movedDbPath = Path.Combine(Path.GetTempPath(), $"lec-moved-{Guid.NewGuid():N}.db");
        string deletedDbPath = Path.Combine(Path.GetTempPath(), $"lec-deleted-{Guid.NewGuid():N}.db");
        try
        {
            DbxEmailFileWriter.Rebuild(Path.Combine(dir, "Notas.dbx"), new[]
            {
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("um"), "Um", null, null, null, null),
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("dois"), "Dois", null, null, null, null),
            });

            using (var store = DbxStore.Open(dir,
                scavengeCacheDatabasePath: scavengeDbPath,
                movedMessageCacheDatabasePath: movedDbPath,
                deletedMessageCacheDatabasePath: deletedDbPath))
            {
                var folder = store.Folders.Single(f => f.Id == 2); // Notas
                var headers = new List<MessageHeader>();
                await foreach (var h in store.EnumerateHeadersAsync(folder))
                    headers.Add(h);

                await store.HideMovedMessageAsync(headers.Single(h => h.Subject == "Um").Id);
                await store.DeleteMessageAsync(headers.Single(h => h.Subject == "Dois").Id);
                await store.ScavengeAsync(folder);

                await store.DeleteFolderAsync(2);
            }

            using var scavengeCache = new ScavengeCache(scavengeDbPath);
            Assert.Null(scavengeCache.GetScan(dir, "Notas.dbx"));
            using var movedCache = new MovedMessageCache(movedDbPath);
            Assert.Empty(movedCache.GetMovedOffsets(dir, "Notas.dbx"));
            using var deletedCache = new DeletedMessageCache(deletedDbPath);
            Assert.Empty(deletedCache.GetDeletedOffsets(dir, "Notas.dbx"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
            File.Delete(scavengeDbPath);
            File.Delete(movedDbPath);
            File.Delete(deletedDbPath);
        }
    }
}
