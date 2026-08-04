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
