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
}
