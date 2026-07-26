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

    public static DbxEmailFile Open(string path) =>
        OpenFrom(new MemoryMappedDbxSource(path), ownsSource: true);

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
                    messages.Add(DbxMessageInfo.Read(source, pointer));
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
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

    public void Dispose()
    {
        if (_ownsSource)
            _source.Dispose();
    }
}
