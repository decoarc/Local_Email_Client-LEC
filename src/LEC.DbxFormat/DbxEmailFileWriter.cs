namespace LEC.DbxFormat;

/// <summary>
/// Writes a brand-new, empty message-store .dbx file — the backing file for a folder
/// that was just created and has no messages yet. itemCount = 0 means the reader
/// (DbxEmailFile.Open) never attempts to walk an index tree, so no tree needs to exist:
/// the header alone is a complete, valid, empty file.
/// </summary>
public static class DbxEmailFileWriter
{
    public static void CreateEmpty(string path)
    {
        var bytes = new byte[DbxFormat.HeaderIndexPointerOffset + 4];

        BitConverter.GetBytes(DbxFormat.SignatureWord0).CopyTo(bytes, 0);
        BitConverter.GetBytes(DbxFormat.SignatureWord1Email).CopyTo(bytes, 4);
        BitConverter.GetBytes(DbxFormat.SignatureWord2).CopyTo(bytes, 8);
        BitConverter.GetBytes(DbxFormat.SignatureWord3).CopyTo(bytes, 12);
        BitConverter.GetBytes(0).CopyTo(bytes, DbxFormat.HeaderItemCountOffset);

        File.WriteAllBytes(path, bytes);
    }
}
