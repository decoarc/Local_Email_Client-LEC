// tests/LEC.MailStore.Tests/DbxStoreCompactTests.cs
using System.Linq;
using System.Text;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using LEC.MailStore;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreCompactTests
{
    private static string BuildProfileWithTwoMessages(string folderName = "Inbox", string fileName = "Inbox.dbx")
    {
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);
        int node = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = Encoding.UTF8.GetBytes(folderName + "\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = Encoding.UTF8.GetBytes(fileName + "\0") },
        });
        int root = folders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: node, entryNextTable: 0, entryIndexCount: 0) });
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        DbxEmailFileWriter.Rebuild(Path.Combine(dir, fileName), new[]
        {
            new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("From: a@x.com\r\nSubject: Viva\r\n\r\nA"), "Viva", null, null, null, null),
            new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("From: b@x.com\r\nSubject: Morta\r\n\r\nBBBBB"), "Morta", null, null, null, null),
        });
        return dir;
    }

    [Fact]
    public async Task GetCompactStatusAsyncIsEmptyWhenNothingIsHidden()
    {
        string dir = BuildProfileWithTwoMessages();
        try
        {
            using var store = DbxStore.Open(dir);
            var status = await store.GetCompactStatusAsync();
            Assert.Empty(status.PendingFolders);
            Assert.Equal(0, status.PendingFolderCount);
            Assert.Equal(0, status.TotalEstimatedReclaimableBytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetCompactStatusAsyncReportsDeletedAndMovedWaste()
    {
        string dir = BuildProfileWithTwoMessages();
        string movedCache = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-moved.db");
        string deletedCache = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-deleted.db");
        try
        {
            using (var store = DbxStore.Open(dir,
                movedMessageCacheDatabasePath: movedCache,
                deletedMessageCacheDatabasePath: deletedCache))
            {
                var folder = store.Folders.Single();
                var headers = new List<MessageHeader>();
                await foreach (var h in store.EnumerateHeadersAsync(folder))
                    headers.Add(h);

                await store.DeleteMessageAsync(headers.Single(h => h.Subject == "Morta").Id);

                var status = await store.GetCompactStatusAsync();
                var pending = Assert.Single(status.PendingFolders);
                Assert.Equal(folder.Id, pending.FolderId);
                Assert.Equal("Inbox", pending.FolderName);
                Assert.True(pending.EstimatedReclaimableBytes > 0);
                Assert.Equal(pending.EstimatedReclaimableBytes, status.TotalEstimatedReclaimableBytes);
            }
        }
        finally
        {
            // Pooled native SQLite handles outlive Dispose() and keep an OS lock on the file.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(movedCache);
            File.Delete(deletedCache);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CompactFolderAsyncRemovesHiddenMessagesAndShrinksTheFile()
    {
        string dir = BuildProfileWithTwoMessages();
        string deletedCache = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-deleted.db");
        try
        {
            long sizeBefore;
            using (var store = DbxStore.Open(dir, deletedMessageCacheDatabasePath: deletedCache))
            {
                var folder = store.Folders.Single();
                var headers = new List<MessageHeader>();
                await foreach (var h in store.EnumerateHeadersAsync(folder))
                    headers.Add(h);

                await store.DeleteMessageAsync(headers.Single(h => h.Subject == "Morta").Id);
                sizeBefore = new FileInfo(Path.Combine(dir, "Inbox.dbx")).Length;

                await store.CompactFolderAsync(folder.Id);

                var after = new List<MessageHeader>();
                await foreach (var h in store.EnumerateHeadersAsync(folder))
                    after.Add(h);
                Assert.Equal("Viva", Assert.Single(after).Subject);

                long sizeAfter = new FileInfo(Path.Combine(dir, "Inbox.dbx")).Length;
                Assert.True(sizeAfter < sizeBefore, $"expected shrink {sizeBefore} -> {sizeAfter}");

                var status = await store.GetCompactStatusAsync();
                Assert.Empty(status.PendingFolders);
            }

            // Caches purged: reopen with same deleted DB — Morta must not stay hidden-by-stale-offset
            using (var reopened = DbxStore.Open(dir, deletedMessageCacheDatabasePath: deletedCache))
            {
                var folder = reopened.Folders.Single();
                var headers = new List<MessageHeader>();
                await foreach (var h in reopened.EnumerateHeadersAsync(folder))
                    headers.Add(h);
                Assert.Equal("Viva", Assert.Single(headers).Subject);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(deletedCache);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CompactFolderAsyncIsNoOpWhenNothingPending()
    {
        string dir = BuildProfileWithTwoMessages();
        try
        {
            using var store = DbxStore.Open(dir);
            var folder = store.Folders.Single();
            long before = new FileInfo(Path.Combine(dir, "Inbox.dbx")).Length;
            await store.CompactFolderAsync(folder.Id);
            long after = new FileInfo(Path.Combine(dir, "Inbox.dbx")).Length;
            Assert.Equal(before, after);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CompactFolderAsyncSucceedsWhenFolderWasAlreadyEnumerated()
    {
        // Same lock race AddMessageAsync already covers: enumerate first, then compact.
        string dir = BuildProfileWithTwoMessages();
        string deletedCache = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-deleted.db");
        try
        {
            using var store = DbxStore.Open(dir, deletedMessageCacheDatabasePath: deletedCache);
            var folder = store.Folders.Single();
            var headers = new List<MessageHeader>();
            await foreach (var h in store.EnumerateHeadersAsync(folder))
                headers.Add(h);
            await store.DeleteMessageAsync(headers.Single(h => h.Subject == "Morta").Id);
            await store.CompactFolderAsync(folder.Id); // must not throw UnauthorizedAccessException
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(deletedCache);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string BuildProfileWithTwoFolders()
    {
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);
        int n1 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = Encoding.UTF8.GetBytes("A\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = Encoding.UTF8.GetBytes("A.dbx\0") },
        });
        int n2 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 2 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = Encoding.UTF8.GetBytes("B\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = Encoding.UTF8.GetBytes("B.dbx\0") },
        });
        int root = folders.WriteIndexNode(nextTable: 0, ptrCount: 2, indexCount: 0,
            entries: new[]
            {
                (indexPtr: n1, entryNextTable: 0, entryIndexCount: 0),
                (indexPtr: n2, entryNextTable: 0, entryIndexCount: 0),
            });
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 2);
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        foreach (string fileName in new[] { "A.dbx", "B.dbx" })
        {
            DbxEmailFileWriter.Rebuild(Path.Combine(dir, fileName), new[]
            {
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("From: a@x.com\r\nSubject: Viva\r\n\r\nA"), "Viva", null, null, null, null),
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("From: b@x.com\r\nSubject: Morta\r\n\r\nBBBBB"), "Morta", null, null, null, null),
            });
        }
        return dir;
    }

    private static async Task DeleteMortaAsync(DbxStore store, StoreFolder folder)
    {
        var headers = new List<MessageHeader>();
        await foreach (var h in store.EnumerateHeadersAsync(folder))
            headers.Add(h);
        await store.DeleteMessageAsync(headers.Single(h => h.Subject == "Morta").Id);
    }

    private sealed class RecordingProgress : IProgress<CompactProgress>
    {
        public List<CompactProgress> Reports { get; } = new();
        public void Report(CompactProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task CompactAllPendingAsyncCompactsEveryPendingFolderAndReportsProgress()
    {
        string dir = BuildProfileWithTwoFolders();
        string deletedCache = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-deleted.db");
        try
        {
            using var store = DbxStore.Open(dir, deletedMessageCacheDatabasePath: deletedCache);
            var folderA = store.Folders.Single(f => f.Id == 1);
            var folderB = store.Folders.Single(f => f.Id == 2);
            await DeleteMortaAsync(store, folderA);
            await DeleteMortaAsync(store, folderB);

            var progress = new RecordingProgress();
            await store.CompactAllPendingAsync(progress);

            var status = await store.GetCompactStatusAsync();
            Assert.Empty(status.PendingFolders);

            Assert.Equal(2, progress.Reports.Count);
            Assert.Equal(0, progress.Reports[0].CompletedCount);
            Assert.Equal(2, progress.Reports[0].TotalCount);
            Assert.Equal("A", progress.Reports[0].CurrentFolderName);
            Assert.Equal(1, progress.Reports[1].CompletedCount);
            Assert.Equal(2, progress.Reports[1].TotalCount);
            Assert.Equal("B", progress.Reports[1].CurrentFolderName);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(deletedCache);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CompactAllPendingAsyncStopsOnFirstFailureLeavingLaterFoldersIntact()
    {
        string dir = BuildProfileWithTwoFolders();
        string deletedCache = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-deleted.db");
        string fileB = Path.Combine(dir, "B.dbx");
        try
        {
            using var store = DbxStore.Open(dir, deletedMessageCacheDatabasePath: deletedCache);
            var folderA = store.Folders.Single(f => f.Id == 1);
            var folderB = store.Folders.Single(f => f.Id == 2);
            await DeleteMortaAsync(store, folderA);
            await DeleteMortaAsync(store, folderB);

            File.SetAttributes(fileB, FileAttributes.ReadOnly);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CompactAllPendingAsync());
            Assert.Equal(
                "Falha ao compactar \"B\". 1 pasta(s) já compactada(s); as demais não foram alteradas.",
                ex.Message);
            Assert.NotNull(ex.InnerException);

            var status = await store.GetCompactStatusAsync();
            var pending = Assert.Single(status.PendingFolders);
            Assert.Equal(2, pending.FolderId);
            Assert.Equal("B", pending.FolderName);
        }
        finally
        {
            if (File.Exists(fileB))
                File.SetAttributes(fileB, FileAttributes.Normal);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(deletedCache);
            Directory.Delete(dir, recursive: true);
        }
    }
}
