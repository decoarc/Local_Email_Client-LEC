// src/LEC.App/Services/MessageRenderer.cs
using System.IO;
using System.Linq;
using System.Net;
using MimeKit;

namespace LEC.App.Services;

public static class MessageRenderer
{
    public static async Task<RenderedMessage> RenderAsync(Stream rawMessage, CancellationToken ct = default)
    {
        var message = await MimeMessage.LoadAsync(rawMessage, ct);

        string html = message.HtmlBody
            ?? $"<pre>{WebUtility.HtmlEncode(message.TextBody ?? string.Empty)}</pre>";

        string? fromDisplay = message.From.Mailboxes.FirstOrDefault() is { } mailbox
            ? (string.IsNullOrEmpty(mailbox.Name) ? mailbox.Address : $"{mailbox.Name} <{mailbox.Address}>")
            : null;

        return new RenderedMessage(message.Subject, fromDisplay, message.Date, html);
    }
}
