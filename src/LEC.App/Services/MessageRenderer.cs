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

    // Bufferiza os bytes brutos antes de tentar parsear -- assim, RenderAsync e
    // ExtractListMetadataAsync conseguem cair pro texto bruto como fallback mesmo quando o
    // parse falha, sem precisar reler o stream (que pode não ser buscável/reutilizável).
    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    // Tenta parsear como MIME; retorna null (nunca lança) em vez de propagar a exceção --
    // alguns .dbx reais carregam headers que o parser estrito do MimeKit rejeita (ex.: campo
    // de header injetado por gateway de antivírus legado contendo espaço, como
    // "X-Symantec-Tempo limite da proteção: 0"). Por política de degradação do ARQUITETURA.md,
    // isso deve virar um fallback legível, não uma exceção não tratada.
    private static async Task<MimeMessage?> TryParseAsync(byte[] raw, CancellationToken ct)
    {
        try
        {
            using var parseStream = new MemoryStream(raw);
            return await MimeMessage.LoadAsync(parseStream, ct);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static async Task<RenderedMessage> RenderAsync(Stream rawMessage, CancellationToken ct = default)
    {
        byte[] raw = await ReadAllBytesAsync(rawMessage, ct);
        MimeMessage? message = await TryParseAsync(raw, ct);

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

    // Regex simples pra remover tags ao extrair texto pesquisável de um HtmlBody -- não
    // precisa ser um parser HTML de verdade (como o restante deste arquivo faz pra
    // renderização real), só precisa produzir texto razoável pra comparar substring contra o
    // termo buscado.
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// Extrai, num único parse MIME, o texto pesquisável do corpo (pra busca por "Conteúdo") e
    /// se a mensagem tem pelo menos um anexo real (pra coluna de anexo da lista) --
    /// MessageListViewModel.PreloadContentAsync usa os dois de uma vez, pra não parsear a
    /// mesma mensagem duas vezes. Nunca lança: mensagem que falha ao parsear cai pro mesmo
    /// fallback de bytes brutos que RenderAsync usa pra exibição, com HasAttachment = false
    /// (não dá pra saber -- mesma política de "trata como não sabido" do resto do app).
    /// </summary>
    public static async Task<(string SearchableText, bool HasAttachment)> ExtractListMetadataAsync(
        Stream rawMessage, CancellationToken ct = default)
    {
        byte[] raw = await ReadAllBytesAsync(rawMessage, ct);
        MimeMessage? message = await TryParseAsync(raw, ct);

        if (message is null)
            return (Encoding.Latin1.GetString(raw), false);

        string text = message.TextBody is { } plain
            ? plain
            : message.HtmlBody is { } html
                ? WebUtility.HtmlDecode(HtmlTagRegex.Replace(html, " "))
                : string.Empty;

        // Mesmo critério (ContentDisposition?.IsAttachment via MimeMessage.Attachments) e mesmo
        // filtro OfType<MimePart> que ExtractAttachments já usa pro painel de leitura, logo
        // abaixo neste arquivo -- mesmas lacunas conhecidas documentadas ali, nenhuma heurística nova.
        bool hasAttachment = message.Attachments.OfType<MimePart>().Any();

        return (text, hasAttachment);
    }

    // MimeMessage.Attachments é puramente baseado em ContentDisposition?.IsAttachment -- não
    // tem nenhuma noção de referência cid: (verificado contra a implementação real do MimeKit
    // 4.17.0). Funciona corretamente pro caso comum observado em dados reais do BCP/ (parte
    // cid: com Content-Disposition: inline ou sem disposition nenhuma), mas tem duas lacunas
    // conhecidas, não corrigidas nesta fase (ver design doc §9):
    //   (a) uma parte referenciada por cid: que também carrega Content-Disposition: attachment
    //       fica listada duas vezes -- inline no HTML E como anexo baixável;
    //   (b) um anexo de verdade sem nenhum header Content-Disposition (alguns remetentes
    //       antigos só setam Content-Type: ...; name=...) é silenciosamente excluído da lista.
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

    /// <summary>
    /// Relê e reparseia a mensagem inteira do zero e escreve só os bytes decodificados do
    /// anexo pedido no disco. Deliberadamente não compartilha nada com RenderAsync/
    /// ExtractAttachments -- o painel de leitura pode estar mostrando uma mensagem por
    /// minutos antes do usuário decidir salvar um anexo, e reparsear a cada vez evita manter
    /// qualquer parser ou stream do MimeKit "vivo" por esse tempo todo.
    /// </summary>
    public static async Task SaveAttachmentAsync(Stream rawMessage, int attachmentIndex, string destinationPath, CancellationToken ct = default)
    {
        var message = await MimeMessage.LoadAsync(rawMessage, ct);
        var attachments = message.Attachments.OfType<MimePart>().ToList();

        if (attachmentIndex < 0 || attachmentIndex >= attachments.Count)
        {
            throw new InvalidOperationException($"Anexo não encontrado nesta mensagem (índice {attachmentIndex}).");
        }

        if (attachments[attachmentIndex].Content is not { } content)
        {
            throw new InvalidOperationException("Este anexo não tem conteúdo para salvar.");
        }

        using var destination = File.Create(destinationPath);
        content.DecodeTo(destination);
    }
}
