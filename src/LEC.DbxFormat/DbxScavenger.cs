namespace LEC.DbxFormat;

public static class DbxScavenger
{
    /// <summary>
    /// Scans the whole file in 4-byte-aligned increments for a valid message-block
    /// header, independent of the index tree. A candidate is any position whose first
    /// word equals its own file offset (self-reference) and whose second word equals
    /// DbxFormat.MessageBlockMaxDataSize (the fixed capacity marker) -- verified against
    /// 1,639 real block headers, see DbxFormat.cs.
    /// </summary>
    public static IEnumerable<int> FindCandidateBlockStarts(IDbxByteSource source, CancellationToken ct = default)
    {
        long length = source.Length;

        for (long offset = 0; offset + DbxFormat.MessageBlockHeaderSize <= length; offset += 4)
        {
            ct.ThrowIfCancellationRequested();

            if (offset > int.MaxValue - DbxFormat.MessageBlockHeaderSize)
                yield break; // beyond what this int32-offset format can address

            int candidate = (int)offset;

            if (source.ReadInt32(candidate + DbxFormat.MessageBlockSelfOffsetOffset) != candidate)
                continue;

            if (source.ReadInt32(candidate + DbxFormat.MessageBlockMarkerOffset) != DbxFormat.MessageBlockMaxDataSize)
                continue;

            yield return candidate;
        }
    }
}
