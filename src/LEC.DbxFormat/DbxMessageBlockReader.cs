namespace LEC.DbxFormat;

public static class DbxMessageBlockReader
{
    public sealed record Result(byte[] Data, bool Truncated, IReadOnlySet<int> VisitedOffsets);

    public static Result ReadChain(IDbxByteSource source, int startOffset)
    {
        var buffer = new List<byte>();
        var visited = new HashSet<int>();
        int offset = startOffset;
        bool truncated = false;

        while (offset != 0)
        {
            if (offset < 0 || (long)offset + DbxFormat.MessageBlockHeaderSize > source.Length)
            {
                truncated = true;
                break;
            }

            if (!visited.Add(offset))
            {
                truncated = true; // cycle
                break;
            }

            if (visited.Count > DbxSafetyLimits.MaxMessageBlockFragments)
            {
                truncated = true;
                break;
            }

            short blockSize = source.ReadInt16(offset + DbxFormat.MessageBlockSizeOffset);
            if (blockSize <= 0 || blockSize > DbxFormat.MessageBlockMaxDataSize)
            {
                truncated = true;
                break;
            }

            if (buffer.Count + blockSize > DbxSafetyLimits.MaxMessageSizeBytes)
            {
                truncated = true;
                break;
            }

            int dataStart = offset + DbxFormat.MessageBlockDataOffset;
            if ((long)dataStart + blockSize > source.Length)
            {
                truncated = true;
                break;
            }

            var chunk = new byte[blockSize];
            source.ReadBytes(dataStart, chunk);
            buffer.AddRange(chunk);

            offset = source.ReadInt32(offset + DbxFormat.MessageBlockNextOffset);
        }

        return new Result(buffer.ToArray(), truncated, visited);
    }
}
