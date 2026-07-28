// src/LEC.MailStore/CachedRecoveredItem.cs
namespace LEC.MailStore;

/// <summary>
/// A scavenged message after classification -- what both ScavengeCache persists and
/// DbxStore turns into a MessageHeader for the UI.
/// </summary>
public sealed record CachedRecoveredItem(
    int ChainOffset,
    long ByteLength,
    RecoveredMessageKind Kind,
    string? Subject,
    string? SenderName,
    string? SenderAddress,
    DateTime? DateUtc);
