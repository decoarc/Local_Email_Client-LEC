using LEC.DbxFormat;

if (args.Length < 2)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  lec dump <file.dbx>");
    Console.WriteLine("  lec extract <file.dbx> <outputDir>");
    return 1;
}

string command = args[0];
string path = args[1];

try
{
    using var source = new MemoryMappedDbxSource(path);
    if (!DbxHeader.TryDetectType(source, out var type))
    {
        Console.Error.WriteLine("Not a recognized .dbx file (signature mismatch).");
        return 2;
    }

    switch (command)
    {
        case "dump" when type == DbxFileType.Folder:
            DumpFolders(source);
            return 0;

        case "dump" when type == DbxFileType.Email:
            DumpMessages(source);
            return 0;

        case "extract" when type == DbxFileType.Email:
            if (args.Length < 3)
            {
                Console.Error.WriteLine("extract requires an output directory.");
                return 1;
            }
            ExtractMessages(source, args[2]);
            return 0;

        default:
            Console.Error.WriteLine($"Command '{command}' is not supported for file type {type}.");
            return 1;
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"Could not open '{path}': {ex.Message}");
    return 2;
}

static void DumpFolders(IDbxByteSource src)
{
    using var file = DbxFolderFile.OpenFrom(src, ownsSource: false);
    Console.WriteLine($"Health: {file.Health}  Folders: {file.Folders.Count}");
    foreach (var f in file.Folders)
        Console.WriteLine($"  [{f.FolderId,4}] parent={f.ParentId,-4} {f.SpecialFolderType,-12} {f.Name} -> {f.FileName} ({f.MessageCount} msgs)");
}

static void DumpMessages(IDbxByteSource src)
{
    using var file = DbxEmailFile.OpenFrom(src, ownsSource: false);
    Console.WriteLine($"Health: {file.Health}  Messages: {file.Messages.Count}");
    foreach (var m in file.Messages)
        Console.WriteLine($"  {m.SendTimeUtc:yyyy-MM-dd HH:mm} | {m.SenderName,-30} | {m.Subject}");
}

static void ExtractMessages(IDbxByteSource src, string outDir)
{
    Directory.CreateDirectory(outDir);
    using var file = DbxEmailFile.OpenFrom(src, ownsSource: false);
    Console.WriteLine($"Health: {file.Health}  Messages: {file.Messages.Count}");

    int i = 0;
    foreach (var m in file.Messages)
    {
        byte[] raw = file.ReadMessageRaw(m, out bool truncated);
        string name = $"{i:D6}{(truncated ? ".truncated" : "")}.eml";
        File.WriteAllBytes(Path.Combine(outDir, name), raw);
        i++;
    }

    Console.WriteLine($"Extracted {i} messages to {outDir}");
}
