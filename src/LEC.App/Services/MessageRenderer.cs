// src/LEC.App/Services/MessageRenderer.cs
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using MimeKit;

namespace LEC.App.Services;

public static class MessageRenderer
{
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
            return new RenderedMessage(null, null, null, fallbackHtml);
        }

        string html = message.HtmlBody
            ?? BuildFallbackHtml(message.TextBody ?? string.Empty);

        string? fromDisplay = message.From.Mailboxes.FirstOrDefault() is { } mailbox
            ? (string.IsNullOrEmpty(mailbox.Name) ? mailbox.Address : $"{mailbox.Name} <{mailbox.Address}>")
            : null;

        return new RenderedMessage(message.Subject, fromDisplay, message.Date, html);
    }
}
