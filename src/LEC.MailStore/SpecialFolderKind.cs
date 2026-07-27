namespace LEC.MailStore;

/// <summary>
/// Store-agnostic special-folder classification — decoupled from
/// LEC.DbxFormat.DbxSpecialFolderType so a future PstStore can map its own
/// notion of special folders onto the same set without LEC.MailStore knowing
/// anything about either source format.
/// </summary>
public enum SpecialFolderKind
{
    None,
    Inbox,
    Outbox,
    SentItems,
    DeletedItems,
    Drafts,
}
