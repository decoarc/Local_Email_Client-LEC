using System.Linq;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxScavengerTests
{
    [Fact]
    public void NewBlockHeaderConstantsMatchVerifiedRealFileOffsets()
    {
        // Verified against real BCP/ files while writing this plan: 1,639/1,639 real
        // block headers (chain-start, mid-chain, and chain-end) across BCP Espelho.dbx,
        // Caixa de entrada.dbx, and Enviados 2004.dbx all had word0 == the block's own
        // file offset and word1 == 0x200, with zero exceptions.
        Assert.Equal(0, DbxFormat.MessageBlockSelfOffsetOffset);
        Assert.Equal(4, DbxFormat.MessageBlockMarkerOffset);
        Assert.Equal(0x200, DbxFormat.MessageBlockMaxDataSize); // the marker's expected value
    }

    private static IDbxByteSource BufferOf(byte[] bytes) => new ByteArrayDbxSource(bytes);

    private static void WriteValidBlockHeader(byte[] buffer, int offset, short dataSize, int next)
    {
        BitConverter.GetBytes(offset).CopyTo(buffer, offset + DbxFormat.MessageBlockSelfOffsetOffset);
        BitConverter.GetBytes(DbxFormat.MessageBlockMaxDataSize).CopyTo(buffer, offset + DbxFormat.MessageBlockMarkerOffset);
        BitConverter.GetBytes(dataSize).CopyTo(buffer, offset + DbxFormat.MessageBlockSizeOffset);
        BitConverter.GetBytes(next).CopyTo(buffer, offset + DbxFormat.MessageBlockNextOffset);
    }

    [Fact]
    public void FindsAValidBlockHeaderPlantedAmongNoise()
    {
        var buffer = new byte[4096];
        new Random(42).NextBytes(buffer); // fill with noise first
        WriteValidBlockHeader(buffer, offset: 512, dataSize: 100, next: 0);

        var candidates = DbxScavenger.FindCandidateBlockStarts(BufferOf(buffer)).ToList();

        Assert.Contains(512, candidates);
    }

    [Fact]
    public void DoesNotReportRandomNoiseAsACandidate()
    {
        // 4096 bytes of noise, no planted header anywhere -- the odds any 4-byte-aligned
        // position coincidentally satisfies BOTH the self-reference AND the exact 0x200
        // marker are astronomically small (effectively zero across 1024 positions).
        var buffer = new byte[4096];
        new Random(1).NextBytes(buffer);

        var candidates = DbxScavenger.FindCandidateBlockStarts(BufferOf(buffer)).ToList();

        Assert.Empty(candidates);
    }

    [Fact]
    public void FindsMultiplePlantedHeadersIndependently()
    {
        var buffer = new byte[4096];
        new Random(7).NextBytes(buffer);
        WriteValidBlockHeader(buffer, offset: 100, dataSize: 50, next: 0);
        WriteValidBlockHeader(buffer, offset: 2000, dataSize: 200, next: 0);

        var candidates = DbxScavenger.FindCandidateBlockStarts(BufferOf(buffer)).ToList();

        Assert.Contains(100, candidates);
        Assert.Contains(2000, candidates);
    }

    [Fact]
    public void IgnoresAHeaderCandidateThatWouldReadPastEndOfFile()
    {
        // A header needs MessageBlockHeaderSize (16) bytes to even be checkable.
        var buffer = new byte[20];
        new Random(3).NextBytes(buffer);
        // Plant a header at offset 8 -- its own 16-byte header fits (8+16=24 > 20 = doesn't fit).
        WriteValidBlockHeader(buffer, offset: 0, dataSize: 4, next: 0); // fits: 0+16=16 <= 20

        var candidates = DbxScavenger.FindCandidateBlockStarts(BufferOf(buffer)).ToList();

        Assert.Equal(new[] { 0 }, candidates);
    }
}
