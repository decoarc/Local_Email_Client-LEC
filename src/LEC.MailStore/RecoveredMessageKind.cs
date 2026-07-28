// src/LEC.MailStore/RecoveredMessageKind.cs
namespace LEC.MailStore;

/// <summary>
/// Never "Excluída" (Deleted) -- per ARQUITETURA.md §7.5, we don't know why a block
/// ended up unreferenced by the index tree, only that it did.
/// </summary>
public enum RecoveredMessageKind
{
    Recovered, // "Recuperada": intact chain, headers parse as a real message.
    Fragment,  // "Fragmento": truncated chain, or headers don't parse.
}
