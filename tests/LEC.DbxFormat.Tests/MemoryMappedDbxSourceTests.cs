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

    [Fact]
    public void ThrowsInsteadOfSilentlyReadingPastTheRealFileLengthIntoThePageRounding()
    {
        // MemoryMappedViewAccessor's capacity is rounded up to the OS page size (typically
        // 4096 bytes), not the real file length. Without an explicit bounds check, a read at
        // an offset between the real length and the page boundary succeeds silently and
        // returns garbage/zero-padded bytes instead of throwing -- diverging from
        // ByteArrayDbxSource (BitConverter-backed), which throws for the same out-of-range
        // read. This test proves the real file length (100 bytes, not a multiple of 4096)
        // is what's enforced, not the rounded-up page capacity.
        byte[] data = new byte[100];
        string path = Path.Combine(Path.GetTempPath(), $"lec-mmtest-{Guid.NewGuid():N}.dbx");
        File.WriteAllBytes(path, data);

        try
        {
            using var source = new MemoryMappedDbxSource(path);
            Assert.Equal(100, source.Length);
            Assert.Throws<ArgumentOutOfRangeException>(() => source.ReadInt32(98));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ConstructorDisposesFileHandleIfMemoryMappedFileCreationFails()
    {
        // Verify that if MemoryMappedFile.CreateFromFile or _mmf.CreateViewAccessor throws
        // (e.g. on a 0-byte file), the already-opened FileStream is disposed and no handle leaks.
        string path = Path.Combine(Path.GetTempPath(), $"lec-mmtest-{Guid.NewGuid():N}.dbx");
        File.WriteAllBytes(path, Array.Empty<byte>()); // 0-byte file will cause MemoryMappedFile.CreateFromFile to throw

        try
        {
            Assert.ThrowsAny<Exception>(() => new MemoryMappedDbxSource(path));

            // If the constructor failed to dispose the FileStream, this delete will throw IOException on Windows.
            // On success, the file deletes cleanly (no handle leak).
            File.Delete(path);
        }
        finally
        {
            // Ensure cleanup even if the delete assertion fails.
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
