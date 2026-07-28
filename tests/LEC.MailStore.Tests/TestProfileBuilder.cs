// tests/LEC.MailStore.Tests/TestProfileBuilder.cs
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;

namespace LEC.MailStore.Tests;

/// <summary>
/// Writes a synthetic Outlook Express profile (Folders.dbx + per-folder .dbx
/// files) to a real temporary directory, so DbxStore.Open — which memory-maps
/// real files via Fase 1's DbxFolderFile/DbxEmailFile.Open — can be tested
/// end-to-end without touching BCP/. Not shipped code — test infrastructure.
/// </summary>
internal sealed class TestProfileBuilder : IDisposable
{
    public string ProfileDirectory { get; }

    public TestProfileBuilder()
    {
        ProfileDirectory = Path.Combine(Path.GetTempPath(), $"lec-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ProfileDirectory);
    }

    public void WriteFoldersDbx(byte[] data) =>
        File.WriteAllBytes(Path.Combine(ProfileDirectory, "Folders.dbx"), data);

    public void WriteEmailDbx(string fileName, byte[] data) =>
        File.WriteAllBytes(Path.Combine(ProfileDirectory, fileName), data);

    public void Dispose()
    {
        try
        {
            Directory.Delete(ProfileDirectory, recursive: true);
        }
        catch
        {
            // best-effort cleanup of a throwaway temp directory
        }
    }

    /// <summary>
    /// A minimal profile with a single, backing-file-less folder — enough for tests
    /// that only need DbxStore.Open to succeed and Folders[0] to exist. Not disposed
    /// by the caller (a bare path is returned, not an IDisposable) — a throwaway temp
    /// directory like the cache-path helpers already scattered through these tests.
    /// </summary>
    public static string CreateEmptyProfile()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int node = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Pastas locais\0"u8.ToArray() },
        });
        int root = localFolders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: node, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        return WriteProfile(("Folders.dbx", localFolders));
    }

    /// <summary>
    /// Writes an arbitrary set of named .dbx files (built via TestDbxBuilder) into a
    /// fresh temp profile directory and returns its path.
    /// </summary>
    public static string WriteProfile(params (string FileName, TestDbxBuilder Builder)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lec-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        foreach (var (fileName, builder) in files)
            File.WriteAllBytes(Path.Combine(dir, fileName), builder.ToArray());

        return dir;
    }
}
