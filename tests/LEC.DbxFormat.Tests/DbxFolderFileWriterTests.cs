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
    public void RebuildRoundTripsAFlagsValueThatNeedsPointerForm()
    {
        // 0x01ABCDEF doesn't fit in the packed record's 24-bit direct payload
        // (DbxFormat.AttributeValueMask) — it must be written pointer-form, exactly like
        // real BCP/Folders.dbx folders whose Flags already require it (e.g. 0x800010).
        string path = Path.Combine(Path.GetTempPath(), $"lec-folders-{Guid.NewGuid():N}.dbx");
        try
        {
            const int flagsNeedingPointerForm = 0x01ABCDEF;
            var entries = new[]
            {
                new DbxFolderWriteEntry(1, ParentId: -1, Name: "Pastas locais", FileName: null,
                    Flags: flagsNeedingPointerForm),
            };

            DbxFolderFileWriter.Rebuild(path, entries);

            using var file = DbxFolderFile.Open(path);
            Assert.Equal(StoreHealth.Ok, file.Health);
            var folder = file.Folders.Single(f => f.FolderId == 1);
            Assert.Equal(flagsNeedingPointerForm, folder.Flags);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildWithNoFoldersProducesAnEmptyButHealthyFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-folders-{Guid.NewGuid():N}.dbx");
        try
        {
            DbxFolderFileWriter.Rebuild(path, Array.Empty<DbxFolderWriteEntry>());

            using var file = DbxFolderFile.Open(path);
            Assert.Equal(StoreHealth.Ok, file.Health);
            Assert.Empty(file.Folders);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildThrowsWhenGivenMoreThan255Folders()
    {
        // The flat-node format's ptrCount is a single byte (up to 255 folders fit); writing
        // (byte)folders.Count for 256 would silently wrap to 0, making every folder invisible
        // on the very file that was just overwritten. Must fail loudly instead.
        var entries = Enumerable.Range(1, 256)
            .Select(i => new DbxFolderWriteEntry(i, ParentId: -1, Name: $"Pasta {i}", FileName: null))
            .ToArray();

        string path = Path.Combine(Path.GetTempPath(), $"lec-folders-{Guid.NewGuid():N}.dbx");
        Assert.Throws<ArgumentException>(() => DbxFolderFileWriter.Rebuild(path, entries));
        Assert.False(File.Exists(path));
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
