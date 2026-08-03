using System.Linq;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxFolderFileWriterTests
{
    [Fact]
    public void RebuildsAFolderFileReadableByDbxFolderFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-folders-{Guid.NewGuid():N}.dbx");
        try
        {
            var entries = new[]
            {
                new DbxFolderWriteEntry(1, ParentId: -1, Name: "Pastas locais", FileName: null),
                new DbxFolderWriteEntry(2, ParentId: 1, Name: "Caixa de entrada", FileName: "Inbox.dbx",
                    MessageCount: 5, SpecialFolderType: DbxSpecialFolderType.Inbox),
            };

            DbxFolderFileWriter.Rebuild(path, entries);

            using var file = DbxFolderFile.Open(path);
            Assert.Equal(StoreHealth.Ok, file.Health);
            Assert.Equal(2, file.Folders.Count);

            var root = file.Folders.Single(f => f.FolderId == 1);
            Assert.Equal(-1, root.ParentId);
            Assert.Equal("Pastas locais", root.Name);
            Assert.Null(root.FileName);

            var inbox = file.Folders.Single(f => f.FolderId == 2);
            Assert.Equal(1, inbox.ParentId);
            Assert.Equal("Caixa de entrada", inbox.Name);
            Assert.Equal("Inbox.dbx", inbox.FileName);
            Assert.Equal(5, inbox.MessageCount);
            Assert.Equal(DbxSpecialFolderType.Inbox, inbox.SpecialFolderType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildOverwritesAnExistingFileAndLeavesNoTempFileBehind()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lec-folders-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Folders.dbx");
        try
        {
            DbxFolderFileWriter.Rebuild(path, new[] { new DbxFolderWriteEntry(1, -1, "Primeira", null) });
            DbxFolderFileWriter.Rebuild(path, new[] { new DbxFolderWriteEntry(1, -1, "Primeira", null),
                                                       new DbxFolderWriteEntry(2, -1, "Segunda", null) });

            using var file = DbxFolderFile.Open(path);
            Assert.Equal(2, file.Folders.Count);
            Assert.Single(Directory.GetFiles(dir)); // only Folders.dbx — no leftover .tmp-* file
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
