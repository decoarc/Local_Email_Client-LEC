// tests/LEC.MailStore.Tests/TestProfileBuilder.cs
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
}
