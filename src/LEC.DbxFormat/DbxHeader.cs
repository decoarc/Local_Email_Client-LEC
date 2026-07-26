namespace LEC.DbxFormat;

public static class DbxHeader
{
    public static bool TryDetectType(IDbxByteSource source, out DbxFileType type)
    {
        type = DbxFileType.Unknown;
        if (source.Length < DbxFormat.HeaderSignatureLength)
            return false;

        uint word0 = unchecked((uint)source.ReadInt32(0));
        uint word1 = unchecked((uint)source.ReadInt32(4));
        uint word2 = unchecked((uint)source.ReadInt32(8));
        uint word3 = unchecked((uint)source.ReadInt32(12));

        if (word0 != DbxFormat.SignatureWord0 || word2 != DbxFormat.SignatureWord2 || word3 != DbxFormat.SignatureWord3)
            return false;

        if (word1 == DbxFormat.SignatureWord1Email)
        {
            type = DbxFileType.Email;
            return true;
        }

        if (word1 == DbxFormat.SignatureWord1Folder)
        {
            type = DbxFileType.Folder;
            return true;
        }

        return false;
    }
}
