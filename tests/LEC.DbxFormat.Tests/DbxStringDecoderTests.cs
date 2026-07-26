// tests/LEC.DbxFormat.Tests/DbxStringDecoderTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxStringDecoderTests
{
    [Fact]
    public void DecodesValidUtf8Directly()
    {
        byte[] raw = Encoding.UTF8.GetBytes("Café");
        Assert.Equal("Café", DbxStringDecoder.Decode(raw));
    }

    [Fact]
    public void FallsBackToWindows1252WhenNotValidUtf8()
    {
        // 0xE9 alone is not valid UTF-8, but is 'é' in windows-1252 —
        // the common case for old Brazilian OE index strings (ARQUITETURA.md §7.6).
        byte[] raw = { (byte)'C', (byte)'a', 0xE9 };
        Assert.Equal("Caé", DbxStringDecoder.Decode(raw));
    }

    [Fact]
    public void EmptyInputDecodesToEmptyString()
    {
        Assert.Equal(string.Empty, DbxStringDecoder.Decode(Array.Empty<byte>()));
    }
}
