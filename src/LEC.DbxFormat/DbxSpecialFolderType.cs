namespace LEC.DbxFormat;

/// <summary>
/// Verified against BCP/Folders.dbx: field 0x09 is exactly 1 for the folder
/// named "Caixa de entrada" (Inbox), 2 for "Caixa de saída" (Outbox),
/// 3 for "Itens enviados" (Sent Items), 4 for "Itens excluídos" (Deleted
/// Items), 5 for "Rascunhos" (Drafts).
/// </summary>
public enum DbxSpecialFolderType
{
    None = 0,
    Inbox = 1,
    Outbox = 2,
    SentItems = 3,
    DeletedItems = 4,
    Drafts = 5,
}
