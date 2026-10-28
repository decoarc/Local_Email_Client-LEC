namespace LEC.DbxFormat;

/// <summary>
/// Folders.dbx info-node attribute IDs. NOT covered by undbx (it only
/// detects the folder signature and never parses folder content) — derived
/// empirically against a real Folders.dbx, by matching
/// decoded strings against the actual folder/file names on disk. See the
/// plan header's verification log for the full cross-reference.
/// </summary>
public enum DbxFolderFieldId : byte
{
    FolderId = 0x00,
    ParentId = 0x01,
    Name = 0x02,
    FileName = 0x03,
    Flags = 0x06,
    MessageCount = 0x07,
    UnreadCount = 0x08, // observed once; plausible, not independently confirmed
    SpecialFolderType = 0x09,
    NodeKind = 0x0A, // observed values 3 (regular) / 4 (root); meaning not fully confirmed
}
