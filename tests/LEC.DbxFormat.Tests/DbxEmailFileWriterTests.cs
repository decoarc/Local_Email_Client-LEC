using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxEmailFileWriterTests
{
    [Fact]
    public void CreateEmptyProducesAReadableEmptyMessageFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-email-{Guid.NewGuid():N}.dbx");
        try
        {
            DbxEmailFileWriter.CreateEmpty(path);

            using var file = DbxEmailFile.Open(path);

            Assert.Equal(StoreHealth.Ok, file.Health);
            Assert.Empty(file.Messages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateEmptyIsDetectedAsAnEmailTypeFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-email-{Guid.NewGuid():N}.dbx");
        try
        {
            DbxEmailFileWriter.CreateEmpty(path);

            using var source = new MemoryMappedDbxSource(path);
            Assert.True(DbxHeader.TryDetectType(source, out var type));
            Assert.Equal(DbxFileType.Email, type);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
