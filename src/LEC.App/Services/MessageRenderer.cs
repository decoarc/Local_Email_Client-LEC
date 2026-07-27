// src/LEC.App/Services/MessageRenderer.cs
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using MimeKit;

namespace LEC.App.Services;

public static class MessageRenderer
{
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
            string fallbackHtml = $"<pre>{WebUtility.HtmlEncode(Encoding.Latin1.GetString(raw))}</pre>";
            return new RenderedMessage(null, null, null, fallbackHtml);
        }

        string html = message.HtmlBody
            ?? $"<pre>{WebUtility.HtmlEncode(message.TextBody ?? string.Empty)}</pre>";

        string? fromDisplay = message.From.Mailboxes.FirstOrDefault() is { } mailbox
            ? (string.IsNullOrEmpty(mailbox.Name) ? mailbox.Address : $"{mailbox.Name} <{mailbox.Address}>")
            : null;

        return new RenderedMessage(message.Subject, fromDisplay, message.Date, html);
    }
}
