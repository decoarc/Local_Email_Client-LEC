// tests/LEC.App.Tests/CompactPromptTests.cs
using LEC.App;

namespace LEC.App.Tests;

public class CompactPromptTests
{
    [Fact]
    public void FormatBytesReturnsTildeZeroForNonPositive()
    {
        Assert.Equal("~0 MB", CompactPrompt.FormatBytes(0));
        Assert.Equal("~0 MB", CompactPrompt.FormatBytes(-1));
    }

    [Fact]
    public void FormatBytesReturnsMinimumTenthForTinySizes()
    {
        Assert.Equal("~0,1 MB", CompactPrompt.FormatBytes(1));
        Assert.Equal("~0,1 MB", CompactPrompt.FormatBytes(100_000));
    }

    [Fact]
    public void FormatBytesUsesCommaAsDecimalSeparator()
    {
        const long oneMb = 1024L * 1024L;
        Assert.Equal("~1 MB", CompactPrompt.FormatBytes(oneMb));
        Assert.Equal("~1,5 MB", CompactPrompt.FormatBytes((long)(1.5 * oneMb)));
        Assert.Equal("~10,3 MB", CompactPrompt.FormatBytes((long)(10.25 * oneMb)));
    }

    [Fact]
    public void BuildClosePromptAppendsNestePerfilForSingleProfile()
    {
        string text = CompactPrompt.BuildClosePrompt(2, 1024L * 1024L, forSingleProfile: true);

        Assert.Equal("Há 2 pasta(s) com espaço a recuperar (~1 MB) neste perfil. Compactar agora?", text);
    }

    [Fact]
    public void BuildClosePromptOmitsNestePerfilForAllProfiles()
    {
        string text = CompactPrompt.BuildClosePrompt(2, 1024L * 1024L, forSingleProfile: false);

        Assert.Equal("Há 2 pasta(s) com espaço a recuperar (~1 MB). Compactar agora?", text);
    }
}
