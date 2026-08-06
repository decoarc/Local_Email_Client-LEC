// src/LEC.MailStore/FileBackupStore.cs
using System.Security.Cryptography;
using System.Text;

namespace LEC.MailStore;

/// <summary>
/// Keeps a ".bak" (always overwritten) and ".original" (write-once) copy of every file
/// DbxStore rewrites from scratch (Folders.dbx, or a folder's own message .dbx), stored
/// under %LOCALAPPDATA%\LEC\backups\&lt;hash-of-profile-path&gt;\ instead of next to the real
/// file inside the profile directory -- so the profile directory itself only ever contains
/// what Outlook Express would recognize. See
/// docs/superpowers/specs/2026-08-06-backups-fora-do-perfil-design.md.
/// </summary>
public sealed class FileBackupStore
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LEC", "backups");

    private readonly string _root;

    public FileBackupStore(string root) => _root = root;

    /// <summary>
    /// Copies profileDirectory/fileName into this profile's backup directory as
    /// "&lt;fileName&gt;.original" (only if it doesn't already exist) and "&lt;fileName&gt;.bak"
    /// (always overwritten). Throws whatever Directory.CreateDirectory/File.Copy throws if
    /// the backup location itself isn't writable -- deliberately not best-effort, since the
    /// whole point is guaranteeing every rewrite has a backup.
    /// </summary>
    public void BeforeRewrite(string profileDirectory, string fileName)
    {
        string backupDir = ProfileBackupDirectory(profileDirectory);
        Directory.CreateDirectory(backupDir);
        WriteProfilePathMarker(backupDir, profileDirectory);

        string sourcePath = Path.Combine(profileDirectory, fileName);

        string pristinePath = Path.Combine(backupDir, fileName + ".original");
        if (!File.Exists(pristinePath))
            File.Copy(sourcePath, pristinePath);

        File.Copy(sourcePath, Path.Combine(backupDir, fileName + ".bak"), overwrite: true);
    }

    /// <summary>
    /// Removes a file's ".bak"/".original" from this profile's backup directory, if present.
    /// Best-effort and silent -- called only when the real file was already permanently
    /// deleted (DeleteFolderAsync), so losing a leftover backup artifact here is never
    /// user-visible data loss and must never block or surface as a failure.
    /// </summary>
    public void Delete(string profileDirectory, string fileName)
    {
        string backupDir = ProfileBackupDirectory(profileDirectory);
        foreach (string candidate in new[] { fileName + ".bak", fileName + ".original" })
        {
            try
            {
                File.Delete(Path.Combine(backupDir, candidate)); // no-op if it never existed
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: see remarks above.
            }
        }
    }

    private string ProfileBackupDirectory(string profileDirectory) =>
        Path.Combine(_root, HashProfilePath(profileDirectory));

    private static string HashProfilePath(string profileDirectory)
    {
        string normalized = Path.GetFullPath(profileDirectory);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash);
    }

    private static void WriteProfilePathMarker(string backupDir, string profileDirectory)
    {
        string markerPath = Path.Combine(backupDir, "profile-path.txt");
        if (!File.Exists(markerPath))
            File.WriteAllText(markerPath, Path.GetFullPath(profileDirectory));
    }
}
