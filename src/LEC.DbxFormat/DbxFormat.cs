namespace LEC.DbxFormat;

/// <summary>
/// Every verified byte-level constant of the Outlook Express .dbx format.
/// Header + index-tree + attribute-record constants were cross-checked against
/// the reverse-engineered undbx reader (github.com/ZungBang/undbx, dbxread.c,
/// GPL-3.0 — itself based on Ulrich Krebs' DbxConv and Arne Schloh's oedbx
/// documentation) and against real files in BCP/. The folder-attribute IDs
/// (marked below) are NOT in undbx — it only detects the folder signature and
/// never parses folder content — so those were derived empirically from
/// BCP/Folders.dbx by cross-referencing decoded strings against the real
/// folder/file names on disk. See the plan header for the full verification log.
/// </summary>
public static class DbxFormat
{
    // ---- File header (offsets from start of file) ----
    public const uint SignatureWord0 = 0xFE12ADCF;
    public const uint SignatureWord1Email = 0x6F74FDC5;
    public const uint SignatureWord1Folder = 0x6F74FDC6;
    public const uint SignatureWord2 = 0x11D1E366;
    public const uint SignatureWord3 = 0xC0004E9A;
    public const int HeaderSignatureLength = 16; // 4 verified 32-bit words
    public const int HeaderItemCountOffset = 0xC4;   // int32: total indexed entries
    public const int HeaderIndexPointerOffset = 0xE4; // int32: file offset of the root index-tree node

    // ---- Index B-tree node: 24-byte header + N x 12-byte entries ----
    public const int IndexNodeHeaderSize = 24;
    public const int IndexNodeNextTableOffset = 8;   // int32: child subtree processed before this node's entries
    public const int IndexNodePtrCountOffset = 17;   // byte: number of entries following the header
    public const int IndexNodeIndexCountOffset = 20; // int32: >0 means NextTable must be recursed into
    public const int IndexNodeEntrySize = 12;
    public const int IndexEntryIndexPtrOffset = 0;    // int32: offset of this entry's info node
    public const int IndexEntryNextTableOffset = 4;   // int32: this entry's own child subtree
    public const int IndexEntryIndexCountOffset = 8;  // int32: >0 means EntryNextTable must be recursed into

    // ---- Info node: variable-length attribute-record array ----
    public const int InfoNodeCountWordOffset = 8;      // int32 at this offset; only byte at +2 (bits 16-23) matters
    public const int InfoNodeCountMask = 0x00FF0000;
    public const int InfoNodeCountShift = 16;
    public const int InfoNodeAttributeArrayOffset = 12; // start of the N x 4-byte attribute records
    public const int AttributeRecordSize = 4;
    public const byte AttributeDirectFlag = 0x80;   // set on the type byte: value is stored inline, not via pointer
    public const byte AttributeFieldIdMask = 0x7F;  // mask off AttributeDirectFlag to get the field's real ID
    public const uint AttributeTypeByteMask = 0xFF; // extracts the full raw type byte (incl. AttributeDirectFlag) from the packed 32-bit record, before the flag is separately checked
    public const int AttributeValueShift = 8;
    public const uint AttributeValueMask = 0xFFFFFF; // 24-bit payload

    // ---- Message data block chain ----
    public const int MessageBlockHeaderSize = 16;
    public const int MessageBlockSizeOffset = 8;  // int16: bytes of real data in this block (1..MessageBlockMaxDataSize)
    public const int MessageBlockNextOffset = 12; // int32: file offset of the next block, or 0 if last
    public const int MessageBlockDataOffset = 16;
    public const int MessageBlockMaxDataSize = 0x200; // 512 bytes

    // ---- Message block header fields used only for scavenging (Fase 3) ----
    // Fase 1 never read these -- it only ever followed pointers already trusted from
    // the index tree. For scavenging raw, untrusted bytes, they're the primary defense
    // against false positives: verified against 1,639 real block headers across 3 files
    // in BCP/ (100% match, including mid-chain and chain-end blocks, not just chain starts).
    public const int MessageBlockSelfOffsetOffset = 0; // int32: must equal the block's own file offset
    public const int MessageBlockMarkerOffset = 4;     // int32: fixed marker, always equals MessageBlockMaxDataSize
}
