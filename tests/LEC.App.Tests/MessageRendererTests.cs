// tests/LEC.App.Tests/MessageRendererTests.cs
using System.Text;
using LEC.App.Services;
using Xunit;

namespace LEC.App.Tests;

public class MessageRendererTests
{
    private static Stream RawMessage(string raw) => new MemoryStream(Encoding.ASCII.GetBytes(raw));

    [Fact]
    public async Task RendersTheHtmlBodyWhenPresent()
    {
        string raw =
            "From: \"Ana\" <ana@example.com>\r\n" +
            "Subject: Ola\r\n" +
            "Date: Fri, 21 Oct 2005 11:11:49 -0300\r\n" +
            "Content-Type: text/html\r\n" +
            "\r\n" +
            "<p>Corpo em HTML</p>";

        var rendered = await MessageRenderer.RenderAsync(RawMessage(raw));

        Assert.Equal("Ola", rendered.Subject);
        Assert.Equal("Ana <ana@example.com>", rendered.FromDisplay);
        Assert.Contains("Corpo em HTML", rendered.Html);
    }

    [Fact]
    public async Task FallsBackToPreformattedTextWhenThereIsNoHtmlBody()
    {
        string raw =
            "From: ana@example.com\r\n" +
            "Subject: So texto\r\n" +
            "\r\n" +
            "Corpo em texto puro";

        var rendered = await MessageRenderer.RenderAsync(RawMessage(raw));

        Assert.Contains("<pre>", rendered.Html);
        Assert.Contains("Corpo em texto puro", rendered.Html);
    }

    [Fact]
    public async Task EscapesHtmlSpecialCharactersInThePlainTextFallback()
    {
        string raw =
            "From: ana@example.com\r\n" +
            "Subject: Perigoso\r\n" +
            "\r\n" +
            "<script>alert(1)</script>";

        var rendered = await MessageRenderer.RenderAsync(RawMessage(raw));

        Assert.DoesNotContain("<script>", rendered.Html);
        Assert.Contains("&lt;script&gt;", rendered.Html);
    }

    [Fact]
    public async Task FallsBackToRawTextInsteadOfThrowingWhenHeadersAreNotRfcCompliant()
    {
        // Real .dbx messages sometimes carry a header field name containing a space --
        // e.g. injected by a legacy antivirus gateway -- which MimeKit's strict parser
        // rejects with a FormatException. Discovered against the real BCP/ profile during
        // Task 17's verification pass (~9% of real messages in that profile hit this).
        string raw =
            "X-Symantec-Tempo limite da proteção: 0\r\n" +
            "From: ana@example.com\r\n" +
            "Subject: Assunto\r\n" +
            "\r\n" +
            "Corpo da mensagem";

        // Latin1, not ASCII: matches the encoding MessageRenderer's fallback path decodes
        // with, so the accented characters in the header above round-trip byte-for-byte.
        var stream = new MemoryStream(Encoding.Latin1.GetBytes(raw));
        var rendered = await MessageRenderer.RenderAsync(stream);

        // "ç"/"ã" go through WebUtility.HtmlEncode as numeric entities, same as any other
        // fallback body text -- assert on the ASCII-safe prefix rather than the raw glyphs.
        Assert.Contains("<pre>", rendered.Html);
        Assert.Contains("Corpo da mensagem", rendered.Html);
        Assert.Contains("X-Symantec-Tempo limite da prote", rendered.Html);
    }
}
