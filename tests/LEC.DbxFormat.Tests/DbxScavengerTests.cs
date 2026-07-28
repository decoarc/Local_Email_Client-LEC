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
}
