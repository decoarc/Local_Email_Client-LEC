// tests/LEC.MailStore.Tests/DbxStoreAddMessageTests.cs
using System.Linq;
using System.Text;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using LEC.MailStore;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreAddMessageTests
{
    private static string BuildProfileWithOneEmptyFolder()
    {
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);
        int node = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Destino\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Destino.dbx\0"u8.ToArray() },
        });
        int root = folders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: node, entryNextTable: 0, entryIndexCount: 0) });
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        DbxEmailFileWriter.CreateEmpty(Path.Combine(dir, "Destino.dbx"));
        return dir;
    }

    [Fact]
    public async Task AddMessageAsyncAppendsToAnEmptyFolderAndPersistsAfterReopen()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        try
        {
            byte[] raw = Encoding.UTF8.GetBytes("From: a@x.com\r\nSubject: Oi\r\n\r\nCorpo.");

            using (var store = DbxStore.Open(dir))
            {
                int folderId = store.Folders.Single().Id;
                await store.AddMessageAsync(folderId, raw, "Oi", "Ana", "ana@x.com", "<m1@x.com>",
                    new DateTime(2004, 3, 1, 10, 0, 0, DateTimeKind.Utc));
            }

            using var reopened = DbxStore.Open(dir);
            var folder = reopened.Folders.Single();
            var headers = await CollectAsync(reopened.EnumerateHeadersAsync(folder));

            var header = Assert.Single(headers);
            Assert.Equal("Oi", header.Subject);
            Assert.Equal("Ana", header.SenderName);

            using var stream = await reopened.OpenMessageAsync(header.Id);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(raw, ms.ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddMessageAsyncSucceedsWhenTheDestinationFolderWasAlreadyEnumerated()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        try
        {
            byte[] raw = Encoding.UTF8.GetBytes("From: a@x.com\r\nSubject: Oi\r\n\r\nCorpo.");

            using (var store = DbxStore.Open(dir))
            {
                var folder = store.Folders.Single();

                // Browsing the destination folder before moving something into it is the
                // normal UI path, and it leaves an open, file-locking DbxEmailFile cached
                // inside the store — AddMessageAsync must evict it before rewriting the file.
                Assert.Empty(await CollectAsync(store.EnumerateHeadersAsync(folder)));

                await store.AddMessageAsync(folder.Id, raw, "Oi", "Ana", "ana@x.com", "<m1@x.com>",
                    new DateTime(2004, 3, 1, 10, 0, 0, DateTimeKind.Utc));

                // The same store instance must see the rebuilt file, not the evicted stale one.
                var sameSession = await CollectAsync(store.EnumerateHeadersAsync(folder));
                Assert.Equal("Oi", Assert.Single(sameSession).Subject);
            }

            using var reopened = DbxStore.Open(dir);
            var reopenedFolder = reopened.Folders.Single();
            var headers = await CollectAsync(reopened.EnumerateHeadersAsync(reopenedFolder));

            var header = Assert.Single(headers);
            Assert.Equal("Oi", header.Subject);

            using var stream = await reopened.OpenMessageAsync(header.Id);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(raw, ms.ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A folder that is both a move source and a move destination: Rebuild renumbers every
    /// info-node offset in the file, so the offsets MovedMessageCache keyed its hidden rows on
    /// are meaningless afterwards -- a stale one either resurrects nothing or, worse, silently
    /// hides whichever unrelated message happens to land on that offset in the new layout.
    /// AddMessageAsync must therefore purge the folder's offset-keyed state after a rebuild.
    /// The property proven here is "no crash, nothing silently wrong-hidden" -- explicitly NOT
    /// "the previously-hidden message stays hidden"; re-keying hiding on a rebuild-stable
    /// identity is a deliberately deferred design change (see the final-review findings, Fix 2).
    /// </summary>
    [Fact]
    public async Task AddMessageAsyncPurgesStaleHiddenOffsetsWhenTheFolderIsAlsoAMoveSource()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        string movedCacheDbPath = Path.Combine(Path.GetTempPath(), $"lec-moved-{Guid.NewGuid():N}.db");
        try
        {
            DbxEmailFileWriter.Rebuild(Path.Combine(dir, "Destino.dbx"), new[]
            {
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("um"), "Um", null, null, null, null),
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("dois"), "Dois", null, null, null, null),
            });

            StoreFolder folder;
            using (var store = DbxStore.Open(dir, movedMessageCacheDatabasePath: movedCacheDbPath))
            {
                folder = store.Folders.Single();

                var before = await CollectAsync(store.EnumerateHeadersAsync(folder));
                Assert.Equal(2, before.Count);

                await store.HideMovedMessageAsync(before.Single(h => h.Subject == "Um").Id);
                Assert.Equal(new[] { "Dois" },
                    (await CollectAsync(store.EnumerateHeadersAsync(folder))).Select(h => h.Subject));

                await store.AddMessageAsync(folder.Id, Encoding.UTF8.GetBytes("tres"), "Tres", null, null, null, null);

                var after = await CollectAsync(store.EnumerateHeadersAsync(folder));
                Assert.Equal(new[] { "Um", "Dois", "Tres" }, after.Select(h => h.Subject));
            }

            // ... and the purge must have reached the SQLite rows too, not just the in-memory set.
            using var reopened = DbxStore.Open(dir, movedMessageCacheDatabasePath: movedCacheDbPath);
            var afterReopen = await CollectAsync(reopened.EnumerateHeadersAsync(reopened.Folders.Single()));
            Assert.Equal(new[] { "Um", "Dois", "Tres" }, afterReopen.Select(h => h.Subject));
        }
        finally
        {
            // Pooled native SQLite handles outlive Dispose() and keep an OS lock on the file.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(movedCacheDbPath);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddMessageAsyncPreservesTheReceiverFieldsOfMessagesAlreadyInTheFolder()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        try
        {
            var receiveTime = new DateTime(2004, 3, 2, 8, 30, 0, DateTimeKind.Utc);

            // Written with the test builder, not the writer under test, so this asserts a real
            // read-side -> write-side round trip rather than the writer agreeing with itself.
            var existing = new TestDbxBuilder();
            existing.WriteHeader(DbxFileType.Email);
            int block = existing.WriteMessageBlock("conteudo"u8.ToArray(), nextOffset: 0);
            int msgNode = existing.WriteInfoNode(new[]
            {
                new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)block },
                new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = "Antiga\0"u8.ToArray() },
                new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.ReceiverName, Data = "Bruno\0"u8.ToArray() },
                new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.ReceiverAddress, Data = "bruno@y.com\0"u8.ToArray() },
                new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.ReceiveCreateTime, Data = BitConverter.GetBytes(receiveTime.ToFileTimeUtc()) },
            });
            int msgRoot = existing.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
                entries: new[] { (indexPtr: msgNode, entryNextTable: 0, entryIndexCount: 0) });
            existing.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
            existing.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, msgRoot);
            File.WriteAllBytes(Path.Combine(dir, "Destino.dbx"), existing.ToArray());

            using (var store = DbxStore.Open(dir))
                await store.AddMessageAsync(store.Folders.Single().Id, Encoding.UTF8.GetBytes("nova"), "Nova", null, null, null, null);

            using var reopened = DbxEmailFile.Open(Path.Combine(dir, "Destino.dbx"));
            Assert.Equal(2, reopened.Messages.Count);

            var preserved = Assert.Single(reopened.Messages, m => m.Subject == "Antiga");
            Assert.Equal("Bruno", preserved.ReceiverName);
            Assert.Equal("bruno@y.com", preserved.ReceiverAddress);
            Assert.Equal(receiveTime, preserved.ReceiveTimeUtc);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddMessageAsyncOnANonexistentFolderThrows()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.AddMessageAsync(999, Encoding.UTF8.GetBytes("x"), null, null, null, null, null));
            Assert.Contains("não encontrada", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddMessageAsyncOnAFolderWithNoBackingFileThrows()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int node = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Pastas locais\0"u8.ToArray() },
        });
        int root = localFolders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: node, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", localFolders));
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.AddMessageAsync(1, Encoding.UTF8.GetBytes("x"), null, null, null, null, null));
            Assert.Contains("arquivo de apoio", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddMessageAsyncWhenTheDestinationFileIsDamagedThrows()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        try
        {
            // itemCount = 1 but no message actually written -- DbxEmailFile.Open flags this Partial.
            File.WriteAllBytes(Path.Combine(dir, "Destino.dbx"), BuildDamagedEmailDbx());

            using var store = DbxStore.Open(dir);
            int folderId = store.Folders.Single().Id;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.AddMessageAsync(folderId, Encoding.UTF8.GetBytes("x"), null, null, null, null, null));
            Assert.Contains("danificado", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddMessageAsyncCreatesBackupsOfTheDestinationFile()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Destino.dbx"));
            using var store = DbxStore.Open(dir);
            int folderId = store.Folders.Single().Id;

            await store.AddMessageAsync(folderId, Encoding.UTF8.GetBytes("x"), "S", null, null, null, null);

            Assert.True(File.Exists(Path.Combine(dir, "Destino.dbx.bak")));
            Assert.True(File.Exists(Path.Combine(dir, "Destino.dbx.original")));
            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Destino.dbx.original")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] BuildDamagedEmailDbx()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        builder.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        builder.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, 999999); // points nowhere real
        return builder.ToArray();
    }

    private static async Task<List<MessageHeader>> CollectAsync(IAsyncEnumerable<MessageHeader> source)
    {
        var list = new List<MessageHeader>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}
