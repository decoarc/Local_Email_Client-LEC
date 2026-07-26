using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class MemoryMappedDbxSourceTests
{
    [Fact]
    public void ReadsMatchByteArraySourceOverSameBytes()
    {
        byte[] data = new byte[64];
        BitConverter.GetBytes(0x22334455).CopyTo(data, 10);
        string path = Path.Combine(Path.GetTempPath(), $"lec-mmtest-{Guid.NewGuid():N}.dbx");
        File.WriteAllBytes(path, data);

        try
        {
            using var source = new MemoryMappedDbxSource(path);
            Assert.Equal(64, source.Length);
            Assert.Equal(0x22334455, source.ReadInt32(10));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpensWithShareReadWriteWhileAnotherHandleHoldsTheFileOpen()
    {
        // ARQUITETURA.md §7.1: must open with FileShare.ReadWrite so a .dbx
        // that's mid-backup or open in another process can still be read.
        string path = Path.Combine(Path.GetTempPath(), $"lec-mmtest-{Guid.NewGuid():N}.dbx");
        File.WriteAllBytes(path, new byte[16]);

        try
        {
            using var externalHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var source = new MemoryMappedDbxSource(path);
            Assert.Equal(16, source.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
