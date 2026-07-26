namespace LEC.DbxFormat;

public sealed class DbxIndexTreeReader
{
    private readonly IDbxByteSource _source;

    public DbxIndexTreeReader(IDbxByteSource source) => _source = source;

    public IReadOnlyList<int> ReadIndexPointers(int rootOffset, out bool corruptionDetected)
    {
        var result = new List<int>();
        var visited = new HashSet<int>();
        corruptionDetected = !ReadNode(rootOffset, depth: 0, result, visited);
        return result;
    }

    private bool ReadNode(int nodeOffset, int depth, List<int> result, HashSet<int> visited)
    {
        if (depth > DbxSafetyLimits.MaxIndexTreeDepth)
            return false;

        if (nodeOffset <= 0 || (long)nodeOffset + DbxFormat.IndexNodeHeaderSize > _source.Length)
            return false;

        if (!visited.Add(nodeOffset))
            return false; // cycle

        int nextTable = _source.ReadInt32(nodeOffset + DbxFormat.IndexNodeNextTableOffset);
        byte ptrCount = _source.ReadByte(nodeOffset + DbxFormat.IndexNodePtrCountOffset);
        int indexCount = _source.ReadInt32(nodeOffset + DbxFormat.IndexNodeIndexCountOffset);

        bool ok = true;

        if (indexCount > 0)
            ok &= ReadNode(nextTable, depth + 1, result, visited);

        int entryPos = nodeOffset + DbxFormat.IndexNodeHeaderSize;
        for (int i = 0; i < ptrCount; i++)
        {
            if ((long)entryPos + DbxFormat.IndexNodeEntrySize > _source.Length)
            {
                ok = false;
                break;
            }

            int indexPtr = _source.ReadInt32(entryPos + DbxFormat.IndexEntryIndexPtrOffset);
            int entryNextTable = _source.ReadInt32(entryPos + DbxFormat.IndexEntryNextTableOffset);
            int entryIndexCount = _source.ReadInt32(entryPos + DbxFormat.IndexEntryIndexCountOffset);

            result.Add(indexPtr);
            entryPos += DbxFormat.IndexNodeEntrySize;

            if (entryIndexCount > 0)
                ok &= ReadNode(entryNextTable, depth + 1, result, visited);
        }

        return ok;
    }
}
