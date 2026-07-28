// src/LEC.MailStore/ScavengedMessageClassifier.cs
using System.Linq;
using LEC.DbxFormat;
using MimeKit;

namespace LEC.MailStore;

public static class ScavengedMessageClassifier
{
    public static CachedRecoveredItem Classify(ScavengedMessage scavenged)
    {
        MimeMessage? parsed;
        try
        {
            using var stream = new MemoryStream(scavenged.RawBytes);
            parsed = MimeMessage.Load(stream);
        }
        catch (FormatException)
        {
            parsed = null;
        }

        var kind = (!scavenged.Truncated && parsed is not null)
            ? RecoveredMessageKind.Recovered
            : RecoveredMessageKind.Fragment;

        var mailbox = parsed?.From.Mailboxes.FirstOrDefault();

        return new CachedRecoveredItem(
            ChainOffset: scavenged.ChainStartOffset,
            ByteLength: scavenged.RawBytes.Length,
            Kind: kind,
            Subject: parsed?.Subject,
            SenderName: mailbox?.Name,
            SenderAddress: mailbox?.Address,
            DateUtc: parsed?.Date.UtcDateTime);
    }
}
