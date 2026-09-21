namespace LEC.DbxFormat;

public sealed class DbxEmailFile : IDisposable
{
    private readonly IDbxByteSource _source;
    private readonly bool _ownsSource;

    public StoreHealth Health { get; }
    public IReadOnlyList<DbxMessageInfo> Messages { get; }

    private DbxEmailFile(IDbxByteSource source, bool ownsSource, StoreHealth health, IReadOnlyList<DbxMessageInfo> messages)
    {
        _source = source;
        _ownsSource = ownsSource;
        Health = health;
        Messages = messages;
    }

    public static DbxEmailFile Open(string path)
    {
        var source = new MemoryMappedDbxSource(path);
        try
        {
            return OpenFrom(source, ownsSource: true);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public static DbxEmailFile OpenFrom(IDbxByteSource source, bool ownsSource)
    {
        if (!DbxHeader.TryDetectType(source, out var type) || type != DbxFileType.Email)
            throw new DbxFormatException("Not a message-type .dbx file (signature mismatch).");

        if (source.Length < DbxFormat.HeaderItemCountOffset + 4 || source.Length < DbxFormat.HeaderIndexPointerOffset + 4)
            return new DbxEmailFile(source, ownsSource, StoreHealth.Corrupt, Array.Empty<DbxMessageInfo>());

        int itemCount = source.ReadInt32(DbxFormat.HeaderItemCountOffset);
        int rootOffset = source.ReadInt32(DbxFormat.HeaderIndexPointerOffset);

        var messages = new List<DbxMessageInfo>();
        bool corrupt = false;

        if (itemCount > 0)
        {
            var indexReader = new DbxIndexTreeReader(source);
            var pointers = indexReader.ReadIndexPointers(rootOffset, out bool indexCorrupt);
            corrupt |= indexCorrupt;

            foreach (int pointer in pointers)
            {
                if (pointer <= 0 || pointer >= source.Length)
                {
                    corrupt = true;
                    continue;
                }

                try
                {
                    var info = DbxMessageInfo.Read(source, pointer);
                    messages.Add(info);
                    if (info.HasOutOfRangeAttribute)
                        corrupt = true; // invalid attribute pointer (ARQUITETURA.md 7.4) -- keep the item, flag the file
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
                {
                    corrupt = true;
                }
            }
        }

        if (messages.Count != itemCount)
            corrupt = true;

        var health = corrupt ? StoreHealth.Partial : StoreHealth.Ok;
        return new DbxEmailFile(source, ownsSource, health, messages);
    }

    public byte[] ReadMessageRaw(DbxMessageInfo info, out bool truncated)
    {
        var result = DbxMessageBlockReader.ReadChain(_source, info.MessageBlockOffset);
        truncated = result.Truncated;
        return result.Data;
    }

    /// <summary>
    /// Scavenges this file for message chains the index tree does not reference.
    /// Builds the "already referenced" set by walking every already-indexed message's
    /// full chain (not just its start offset) -- every block in a chain independently
    /// satisfies the same self-reference+marker check a scavenge candidate does, so a
    /// mid-chain block of a perfectly normal message must not be reported as an orphan.
    /// </summary>
    public IReadOnlyList<ScavengedMessage> Scavenge(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var referenced = new HashSet<int>();
        foreach (var info in Messages)
        {
            if (info.MessageBlockOffset <= 0)
                continue;

            var chain = DbxMessageBlockReader.ReadChain(_source, info.MessageBlockOffset);
            foreach (int offset in chain.VisitedOffsets)
                referenced.Add(offset);
        }

        return DbxScavenger.Scan(_source, referenced, progress, ct);
    }

    /// <summary>
    /// Re-reads the raw bytes of a chain found by Scavenge, given the chain-start offset
    /// from its ScavengedMessage. Scavenge itself already has the bytes in memory at scan
    /// time, but they aren't persisted (only metadata is cached -- see LEC.MailStore's
    /// ScavengeCache) so opening a recovered message later re-walks the chain fresh.
    /// </summary>
    public byte[] ReadRecoveredMessageRaw(int chainStartOffset, out bool truncated)
    {
        var result = DbxMessageBlockReader.ReadChain(_source, chainStartOffset);
        truncated = result.Truncated;
        return result.Data;
    }

    public void Dispose()
    {
        if (_ownsSource)
            _source.Dispose();
    }
}
