// tests/LEC.App.Tests/MessageRendererTests.cs
using System.Reflection;
using System.Text;
using LEC.App.Services;
using MimeKit;
using Xunit;

namespace LEC.App.Tests;

public class MessageRendererTests
{
    private static Stream RawMessage(string raw) => new MemoryStream(Encoding.ASCII.GetBytes(raw));

    // A stream that reports itself as readable+seekable (MimeContent's constructor throws
    // ArgumentException otherwise) but genuinely throws once something tries to read from it.
    // Used to force a real, uncaught exception out of MimeKit's own MimeContent.DecodeTo --
    // empirically verified (throwaway probe against MimeKit 4.17.0) that this is necessary:
    // malformed base64 bodies and truncated/no-closing-boundary attachments both decode
    // leniently without throwing, so DecodeTo can only be made to fail by making the
    // underlying stream itself fail, exactly as a truncated/corrupted disk read would.
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 100;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("simulated corrupt attachment stream");
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Builds real MimeKit multipart/related bytes (BodyBuilder -> WriteTo), not hand-written
    // raw MIME, so the test exercises MessageRenderer against exactly what MimeMessage.LoadAsync
    // would see for a real embedded-image message.
    private static Stream RawMultipartRelatedMessage(string htmlBody, byte[] imageBytes, string contentId)
    {
        var builder = new BodyBuilder { HtmlBody = htmlBody };
        var image = (MimePart)builder.LinkedResources.Add("logo.png", imageBytes, ContentType.Parse("image/png"));
        image.ContentId = contentId;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ana", "ana@example.com"));
        message.Subject = "Com imagem embutida";
        message.Body = builder.ToMessageBody();

        var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        return stream;
    }

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
        Assert.Empty(rendered.Attachments);
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

    [Fact]
    public async Task TruncatesAnOversizedNonParseableFallbackInsteadOfDumpingItAllIntoHtml()
    {
        // C2/I4: CoreWebView2.NavigateToString throws above ~2 MB. 142 of 143 real
        // over-limit BCP/ messages came from this exact fallback path (unparseable
        // headers) dumping the entire raw message -- MIME boundaries, headers, base64
        // attachments -- into one <pre> block; worst case observed was ~11 MB. The
        // renderer must cap its own output and say so visibly rather than truncating
        // silently or leaving it to the caller entirely.
        string headers = "X-Bad Header: present so MimeKit's strict parser rejects this\r\n\r\n";
        string hugeBody = new string('A', 500_000);
        string raw = headers + hugeBody;

        var rendered = await MessageRenderer.RenderAsync(RawMessage(raw));

        Assert.True(rendered.Html.Length < 300_000, $"Expected a bounded fallback, got {rendered.Html.Length} chars.");
        Assert.Contains("truncada", rendered.Html);
    }

    [Fact]
    public async Task TruncatesAnOversizedTextBodyFallbackInsteadOfDumpingItAllIntoHtml()
    {
        // Same cap applied to the "no HtmlBody, use TextBody" fallback for consistency,
        // even though a MimeKit-parsed TextBody is normally much smaller (it excludes
        // attachments) than the raw-bytes fallback above.
        string hugeBody = new string('B', 500_000);
        string raw =
            "From: ana@example.com\r\n" +
            "Subject: Grande\r\n" +
            "\r\n" +
            hugeBody;

        var rendered = await MessageRenderer.RenderAsync(RawMessage(raw));

        Assert.True(rendered.Html.Length < 300_000, $"Expected a bounded fallback, got {rendered.Html.Length} chars.");
        Assert.Contains("truncada", rendered.Html);
    }

    [Fact]
    public async Task ResolvesACidReferencedImageToAnInlineDataUri()
    {
        // Task 18: multipart/related HTML referencing cid:logo123, with a matching embedded
        // image MimePart carrying Content-Id: <logo123>. Verified empirically (throwaway
        // MimeKit round-trip probe) that MimeKit strips the angle brackets when parsing, so
        // MimePart.ContentId comes back as "logo123" -- the same bare form used after "cid:"
        // in the HTML -- and that MimeContent.DecodeTo gives back the exact original bytes.
        byte[] imageBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03, 0xFF };
        string html = "<p>Ola <img src=\"cid:logo123\"></p>";

        var rendered = await MessageRenderer.RenderAsync(RawMultipartRelatedMessage(html, imageBytes, "logo123"));

        string expectedDataUri = $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}";
        Assert.Contains($"src=\"{expectedDataUri}\"", rendered.Html);
        Assert.DoesNotContain("cid:logo123", rendered.Html);
    }

    [Fact]
    public async Task LeavesAnUnresolvedCidReferenceUntouchedWhenNoMatchingPartExists()
    {
        // The embedded part's Content-Id ("other-id") does not match the HTML's cid reference
        // ("missing-id") -- e.g. an attachment that was stripped, or a reference to a part that
        // was never actually attached. Must render exactly as it does today: unresolved, not
        // blanked out, not crashing.
        byte[] imageBytes = { 0x01, 0x02, 0x03 };
        string html = "<p>Ola <img src=\"cid:missing-id\"></p>";

        var rendered = await MessageRenderer.RenderAsync(RawMultipartRelatedMessage(html, imageBytes, "other-id"));

        Assert.Contains("<img src=\"cid:missing-id\">", rendered.Html);
        Assert.DoesNotContain("data:image/png", rendered.Html);
    }

    [Fact]
    public async Task ExtractsRealAttachmentsSeparatelyFromCidEmbeddedImages()
    {
        var builder = new BodyBuilder { HtmlBody = "<p>Ola <img src=\"cid:logo123\"></p>" };
        var embeddedImage = (MimePart)builder.LinkedResources.Add("logo.png", new byte[] { 1, 2, 3 }, ContentType.Parse("image/png"));
        embeddedImage.ContentId = "logo123";

        byte[] attachmentBytes = Encoding.ASCII.GetBytes("conteudo do anexo");
        builder.Attachments.Add("relatorio.txt", attachmentBytes, ContentType.Parse("text/plain"));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ana", "ana@example.com"));
        message.Subject = "Com anexo real";
        message.Body = builder.ToMessageBody();

        var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;

        var rendered = await MessageRenderer.RenderAsync(stream);

        var attachment = Assert.Single(rendered.Attachments);
        Assert.Equal("relatorio.txt", attachment.FileName);
        Assert.Equal("text/plain", attachment.MimeType);
        Assert.Equal(attachmentBytes.Length, attachment.Size);
        Assert.Equal(0, attachment.Index);
    }

    [Fact]
    public async Task ReturnsNoAttachmentsWhenTheMessageFailsToParse()
    {
        string raw =
            "X-Bad Header: present so MimeKit's strict parser rejects this\r\n" +
            "\r\n" +
            "Corpo";

        var rendered = await MessageRenderer.RenderAsync(RawMessage(raw));

        Assert.Empty(rendered.Attachments);
    }

    [Fact]
    public async Task ExtractAttachmentsDegradesGracefullyWhenContentDecodeFails()
    {
        // Regression test: ExtractAttachments must catch exceptions from part.Content.DecodeTo
        // and degrade gracefully (report size as 0) rather than propagating the exception and
        // crashing the entire message render. Critical for scavenged .dbx content where
        // attachment streams may be truncated/corrupted. Pattern mirrors ResolveCidImages,
        // which already catches DecodeTo exceptions with an identical try/catch structure.
        //
        // MessageRenderer.RenderAsync buffers the incoming stream into a byte[] up front and
        // then parses a fresh MemoryStream(raw) internally (see RenderAsync) -- there is no
        // seam for a caller to hand it an already-parsed MimeMessage with a sabotaged
        // MimePart.Content, and a plain MemoryStream never throws on read. So instead of going
        // through RenderAsync's public Stream entry point, this test: (1) builds and
        // round-trips a real message through WriteTo/LoadAsync -- exactly what RenderAsync
        // itself does -- so the resulting MimePart has the same real shape a parsed attachment
        // would; (2) replaces that attachment's Content with a MimeContent wrapping a
        // ThrowingStream, which genuinely throws IOException the moment DecodeTo tries to read
        // from it (empirically confirmed via a throwaway probe against MimeKit 4.17.0 -- see
        // task-1-report.md); (3) invokes the private ExtractAttachments method via reflection,
        // since it is the actual method containing the fix's try/catch and RenderAsync gives no
        // other way to reach it with a sabotaged part.
        var builder = new BodyBuilder { HtmlBody = "<p>Test Body</p>" };
        byte[] attachmentBytes = Encoding.ASCII.GetBytes("valid attachment content");
        builder.Attachments.Add("test.txt", attachmentBytes, ContentType.Parse("text/plain"));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Test User", "test@example.com"));
        message.Subject = "Test Message";
        message.Body = builder.ToMessageBody();

        var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        var parsed = await MimeMessage.LoadAsync(stream);

        var attachmentPart = Assert.IsAssignableFrom<MimePart>(Assert.Single(parsed.Attachments));
        attachmentPart.Content = new MimeContent(new ThrowingStream(), ContentEncoding.Default);

        MethodInfo? extractAttachments = typeof(MessageRenderer).GetMethod(
            "ExtractAttachments", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(extractAttachments);

        object? result = null;
        Exception? thrown = Record.Exception(() =>
            result = extractAttachments!.Invoke(null, new object[] { parsed }));

        Assert.Null(thrown);
        var attachments = Assert.IsAssignableFrom<IReadOnlyList<AttachmentInfo>>(result);
        var attachment = Assert.Single(attachments);
        Assert.Equal("test.txt", attachment.FileName);
        Assert.Equal("text/plain", attachment.MimeType);
        Assert.Equal(0, attachment.Size);
    }
}
