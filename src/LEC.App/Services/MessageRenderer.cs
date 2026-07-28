// src/LEC.App/Services/MessageRenderer.cs
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace LEC.App.Services;

public static class MessageRenderer
{
    // Matches src="cid:XXX" / src='cid:XXX' (any whitespace around '=', either quote style).
    // MIME-generated HTML (signature-logo style embeds) reliably quotes the src attribute, so
    // the unquoted-attribute case isn't handled -- it doesn't show up in real MimeKit/Outlook-
    // Express-generated output and adding it would only widen the match surface for no benefit.
    // Negative lookbehind on "src" avoids matching other attributes that merely end in
    // "src" (e.g. "data-src", a lazy-load attribute some newsletter HTML carries alongside
    // a real src -- rewriting that one too would be silently wrong, not just unnecessary).
    private static readonly Regex CidImgSrcRegex = new(
        @"(?<![\w-])src\s*=\s*(?<q>[""'])cid:(?<cid>[^""']+)\k<q>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // WebView2's CoreWebView2.NavigateToString throws above ~2 MB of HTML. Both fallback
    // paths below can otherwise carry an entire raw message (MIME boundaries, headers,
    // base64 attachments) into a single <pre> block -- observed against the real BCP/
    // profile up to ~11 MB. Cap well under the WebView2 limit and say so visibly rather
    // than truncating silently.
    private const int MaxFallbackBytes = 200_000;

    private static string BuildFallbackHtml(byte[] raw)
    {
        bool truncated = raw.Length > MaxFallbackBytes;
        string shown = truncated
            ? Encoding.Latin1.GetString(raw, 0, MaxFallbackBytes)
            : Encoding.Latin1.GetString(raw);
        string notice = truncated
            ? $"\n\n[mensagem truncada para exibição — {raw.Length:N0} bytes no total]"
            : string.Empty;
        return $"<pre>{WebUtility.HtmlEncode(shown)}{WebUtility.HtmlEncode(notice)}</pre>";
    }

    private static string BuildFallbackHtml(string text)
    {
        bool truncated = text.Length > MaxFallbackBytes;
        string shown = truncated ? text[..MaxFallbackBytes] : text;
        string notice = truncated
            ? $"\n\n[mensagem truncada para exibição — {text.Length:N0} caracteres no total]"
            : string.Empty;
        return $"<pre>{WebUtility.HtmlEncode(shown)}{WebUtility.HtmlEncode(notice)}</pre>";
    }

    // Rewrites <img src="cid:...">/<img src='cid:...'> references to inline data: URIs by
    // matching against MimeMessage.BodyParts's ContentId. Empirically verified (real
    // MimeKit-constructed messages, MimeMessage.WriteTo -> MimeMessage.Load round trip):
    // MimeKit strips the angle brackets from the raw "Content-Id: <foo>" header, so a parsed
    // MimePart.ContentId is "foo" -- the same bare form that appears after "cid:" in HTML.
    // No bracket-stripping is needed on either side; a direct ordinal comparison is correct.
    // Unresolved cid: references (no matching part) are left byte-for-byte untouched -- same
    // degrade-gracefully-never-crash principle as the rest of this file.
    private static string ResolveCidImages(string html, MimeMessage message)
    {
        if (html.IndexOf("cid:", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return html;
        }

        Dictionary<string, MimePart>? partsByContentId = null;

        return CidImgSrcRegex.Replace(html, match =>
        {
            partsByContentId ??= BuildContentIdIndex(message);

            string cid = match.Groups["cid"].Value;
            if (!partsByContentId.TryGetValue(cid, out MimePart? part))
            {
                return match.Value;
            }

            if (part.Content is not { } partContent)
            {
                // No content to inline (e.g. a MimePart with no body) -- leave untouched
                // rather than emit a data: URI with no data.
                return match.Value;
            }

            try
            {
                using var content = new MemoryStream();
                partContent.DecodeTo(content);
                string base64 = Convert.ToBase64String(content.ToArray());
                string mimeType = part.ContentType.MimeType;
                char quote = match.Groups["q"].Value[0];
                return $"src={quote}data:{mimeType};base64,{base64}{quote}";
            }
            catch
            {
                // A malformed embedded part must never crash rendering -- same principle as
                // the FormatException fallback elsewhere in this file for the whole message.
                return match.Value;
            }
        });
    }

    private static Dictionary<string, MimePart> BuildContentIdIndex(MimeMessage message)
    {
        var index = new Dictionary<string, MimePart>(StringComparer.Ordinal);
        foreach (MimeEntity entity in message.BodyParts)
        {
            if (entity is MimePart part && !string.IsNullOrEmpty(part.ContentId) && !index.ContainsKey(part.ContentId))
            {
                index[part.ContentId] = part;
            }
        }

        return index;
    }

    public static async Task<RenderedMessage> RenderAsync(Stream rawMessage, CancellationToken ct = default)
    {
        // Buffer the raw bytes up front so a MimeKit parse failure can still fall back to
        // showing them as text -- some real .dbx messages carry headers MimeKit's strict
        // RFC 5322 parser rejects (e.g. legacy antivirus gateways injecting a field name
        // containing a space, such as "X-Symantec-Tempo limite da proteção: 0"). Per
        // ARQUITETURA.md's degradation policy, that must become a readable fallback, not an
        // unhandled exception that crashes the reading pane.
        byte[] raw;
        using (var buffer = new MemoryStream())
        {
            await rawMessage.CopyToAsync(buffer, ct);
            raw = buffer.ToArray();
        }

        MimeMessage? message;
        try
        {
            using var parseStream = new MemoryStream(raw);
            message = await MimeMessage.LoadAsync(parseStream, ct);
        }
        catch (FormatException)
        {
            message = null;
        }

        if (message is null)
        {
            string fallbackHtml = BuildFallbackHtml(raw);
            return new RenderedMessage(null, null, null, fallbackHtml, Array.Empty<AttachmentInfo>());
        }

        string html = message.HtmlBody is { } htmlBody
            ? ResolveCidImages(htmlBody, message)
            : BuildFallbackHtml(message.TextBody ?? string.Empty);

        string? fromDisplay = message.From.Mailboxes.FirstOrDefault() is { } mailbox
            ? (string.IsNullOrEmpty(mailbox.Name) ? mailbox.Address : $"{mailbox.Name} <{mailbox.Address}>")
            : null;

        return new RenderedMessage(message.Subject, fromDisplay, message.Date, html, ExtractAttachments(message));
    }

    // MimeMessage.Attachments já separa anexo de verdade de parte inline referenciada por
    // cid: (essas ficam só no corpo HTML, via ResolveCidImages/BuildContentIdIndex acima) --
    // verificado contra mensagens reais do BCP/ na Task 5 deste plano.
    private static IReadOnlyList<AttachmentInfo> ExtractAttachments(MimeMessage message)
    {
        var attachments = new List<AttachmentInfo>();
        int index = 0;

        foreach (var entity in message.Attachments)
        {
            if (entity is not MimePart part)
            {
                continue;
            }

            string fileName = string.IsNullOrEmpty(part.FileName) ? $"anexo-{index + 1}" : part.FileName;

            long size = 0;
            try
            {
                using var counting = new MemoryStream();
                part.Content?.DecodeTo(counting);
                size = counting.Length;
            }
            catch
            {
                // A malformed attachment must never crash rendering -- if decode fails,
                // degrade gracefully by treating the size as unknown (0) rather than
                // propagating the exception and crashing the whole message render.
                // This is especially important for scavenged messages where attachment
                // streams may be corrupted or truncated.
            }

            attachments.Add(new AttachmentInfo(fileName, part.ContentType.MimeType, size, index));
            index++;
        }

        return attachments;
    }
}
