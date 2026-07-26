// src/LEC.DbxFormat/DbxStringDecoder.cs
using System.Text;

namespace LEC.DbxFormat;

/// <summary>
/// ARQUITETURA.md §7.6: index-node strings carry no charset. Policy: try
/// UTF-8, fall back to windows-1252 (the typical Brazilian OE-era codepage).
/// </summary>
public static class DbxStringDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static DbxStringDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Decode(byte[] raw)
    {
        if (raw.Length == 0)
            return string.Empty;

        try
        {
            return StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(raw);
        }
    }
}
