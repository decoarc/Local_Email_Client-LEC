namespace LEC.DbxFormat;

/// <summary>
/// A message chain found by DbxScavenger that the index tree does not reference.
/// Purely structural -- no MIME awareness (that's LEC.MailStore's job, which already
/// depends on MimeKit; LEC.DbxFormat stays format-only per ARQUITETURA.md §7.2).
/// </summary>
public sealed record ScavengedMessage(int ChainStartOffset, byte[] RawBytes, bool Truncated);
