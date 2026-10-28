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

    /// <summary>
    /// Full scavenge scan: finds every candidate block header, excludes anything already
    /// covered by <paramref name="alreadyReferencedOffsets"/> (the full set of offsets
    /// belonging to messages the index tree already found -- not just their chain starts;
    /// see DbxEmailFile.Scavenge for how that set is built), reconstructs each remaining
    /// chain, and reports one ScavengedMessage per chain.
    ///
    /// Known limitation, accepted for this plan: a chain whose "next" pointer loops
    /// backward into another independently-valid candidate can be reported twice, since
    /// candidates already visited earlier in the scan are only excluded from *later*
    /// candidates, not retroactively. Not observed in the real files checked for this. A fully correct
    /// fix needs a two-pass group-by-shared-offset algorithm this plan does not build.
    /// </summary>
    public static IReadOnlyList<ScavengedMessage> Scan(
        IDbxByteSource source,
        IReadOnlySet<int> alreadyReferencedOffsets,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<ScavengedMessage>();
        var consumed = new HashSet<int>(alreadyReferencedOffsets);

        var candidates = FindCandidateBlockStarts(source, ct).ToList();
        for (int i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((double)i / Math.Max(1, candidates.Count));

            int candidate = candidates[i];
            if (consumed.Contains(candidate))
                continue;

            var chain = DbxMessageBlockReader.ReadChain(source, candidate);
            foreach (int visited in chain.VisitedOffsets)
                consumed.Add(visited);

            if (chain.Data.Length == 0)
                continue; // header validated, but the chain itself yielded no usable data

            results.Add(new ScavengedMessage(candidate, chain.Data, chain.Truncated));
        }

        progress?.Report(1.0);
        return results;
    }
}
