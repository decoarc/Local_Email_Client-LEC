namespace LEC.DbxFormat;

public sealed class DbxFolderFile : IDisposable
{
    private readonly IDbxByteSource _source;
    private readonly bool _ownsSource;

    public StoreHealth Health { get; }
    public IReadOnlyList<DbxFolderInfo> Folders { get; }

    private DbxFolderFile(IDbxByteSource source, bool ownsSource, StoreHealth health, IReadOnlyList<DbxFolderInfo> folders)
    {
        _source = source;
        _ownsSource = ownsSource;
        Health = health;
        Folders = folders;
    }

    public static DbxFolderFile Open(string path) =>
        OpenFrom(new MemoryMappedDbxSource(path), ownsSource: true);

    public static DbxFolderFile OpenFrom(IDbxByteSource source, bool ownsSource)
    {
        if (!DbxHeader.TryDetectType(source, out var type) || type != DbxFileType.Folder)
            throw new DbxFormatException("Not a Folders.dbx file (signature mismatch).");

        if (source.Length < DbxFormat.HeaderItemCountOffset + 4 || source.Length < DbxFormat.HeaderIndexPointerOffset + 4)
            return new DbxFolderFile(source, ownsSource, StoreHealth.Corrupt, Array.Empty<DbxFolderInfo>());

        int itemCount = source.ReadInt32(DbxFormat.HeaderItemCountOffset);
        int rootOffset = source.ReadInt32(DbxFormat.HeaderIndexPointerOffset);

        var folders = new List<DbxFolderInfo>();
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
                    folders.Add(DbxFolderInfo.Read(source, pointer));
                }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
                {
                    corrupt = true;
                }
            }
        }

        if (folders.Count != itemCount)
            corrupt = true;

        var health = corrupt ? StoreHealth.Partial : StoreHealth.Ok;
        return new DbxFolderFile(source, ownsSource, health, folders);
    }

    public void Dispose()
    {
        if (_ownsSource)
            _source.Dispose();
    }
}
