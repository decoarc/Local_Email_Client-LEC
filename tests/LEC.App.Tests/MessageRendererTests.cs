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
}
