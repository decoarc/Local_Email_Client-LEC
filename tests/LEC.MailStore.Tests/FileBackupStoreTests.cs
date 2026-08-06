// tests/LEC.MailStore.Tests/FileBackupStoreTests.cs
using Xunit;

namespace LEC.MailStore.Tests;

public class FileBackupStoreTests
{
    [Fact]
    public void BeforeRewriteCopiesTheFileAsBothBakAndOriginal()
    {
        string profileDir = Path.Combine(Path.GetTempPath(), $"lec-profile-{Guid.NewGuid():N}");
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileDir);
        try
        {
            File.WriteAllText(Path.Combine(profileDir, "Folders.dbx"), "conteudo original");
            var store = new FileBackupStore(backupRoot);

            store.BeforeRewrite(profileDir, "Folders.dbx");

            string backupDir = Assert.Single(Directory.GetDirectories(backupRoot));
            Assert.Equal("conteudo original", File.ReadAllText(Path.Combine(backupDir, "Folders.dbx.bak")));
            Assert.Equal("conteudo original", File.ReadAllText(Path.Combine(backupDir, "Folders.dbx.original")));
        }
        finally
        {
            Directory.Delete(profileDir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }

    [Fact]
    public void BeforeRewriteWritesAProfilePathMarker()
    {
        string profileDir = Path.Combine(Path.GetTempPath(), $"lec-profile-{Guid.NewGuid():N}");
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileDir);
        try
        {
            File.WriteAllText(Path.Combine(profileDir, "Folders.dbx"), "x");
            var store = new FileBackupStore(backupRoot);

            store.BeforeRewrite(profileDir, "Folders.dbx");

            string backupDir = Assert.Single(Directory.GetDirectories(backupRoot));
            Assert.Equal(Path.GetFullPath(profileDir), File.ReadAllText(Path.Combine(backupDir, "profile-path.txt")));
        }
        finally
        {
            Directory.Delete(profileDir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }

    [Fact]
    public void BeforeRewriteNeverOverwritesTheOriginalButAlwaysRollsTheBak()
    {
        string profileDir = Path.Combine(Path.GetTempPath(), $"lec-profile-{Guid.NewGuid():N}");
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileDir);
        try
        {
            string filePath = Path.Combine(profileDir, "Folders.dbx");
            File.WriteAllText(filePath, "versao 1");
            var store = new FileBackupStore(backupRoot);

            store.BeforeRewrite(profileDir, "Folders.dbx");

            File.WriteAllText(filePath, "versao 2");
            store.BeforeRewrite(profileDir, "Folders.dbx");

            string backupDir = Assert.Single(Directory.GetDirectories(backupRoot));
            Assert.Equal("versao 1", File.ReadAllText(Path.Combine(backupDir, "Folders.dbx.original")));
            Assert.Equal("versao 2", File.ReadAllText(Path.Combine(backupDir, "Folders.dbx.bak")));
        }
        finally
        {
            Directory.Delete(profileDir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }

    [Fact]
    public void DifferentProfilesGetDifferentBackupDirectories()
    {
        string profileDirA = Path.Combine(Path.GetTempPath(), $"lec-profile-a-{Guid.NewGuid():N}");
        string profileDirB = Path.Combine(Path.GetTempPath(), $"lec-profile-b-{Guid.NewGuid():N}");
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileDirA);
        Directory.CreateDirectory(profileDirB);
        try
        {
            File.WriteAllText(Path.Combine(profileDirA, "Folders.dbx"), "a");
            File.WriteAllText(Path.Combine(profileDirB, "Folders.dbx"), "b");
            var store = new FileBackupStore(backupRoot);

            store.BeforeRewrite(profileDirA, "Folders.dbx");
            store.BeforeRewrite(profileDirB, "Folders.dbx");

            Assert.Equal(2, Directory.GetDirectories(backupRoot).Length);
        }
        finally
        {
            Directory.Delete(profileDirA, recursive: true);
            Directory.Delete(profileDirB, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }

    [Fact]
    public void TheSameProfileAlwaysResolvesToTheSameBackupDirectory()
    {
        string profileDir = Path.Combine(Path.GetTempPath(), $"lec-profile-{Guid.NewGuid():N}");
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileDir);
        try
        {
            File.WriteAllText(Path.Combine(profileDir, "Folders.dbx"), "x");
            File.WriteAllText(Path.Combine(profileDir, "Notas.dbx"), "y");
            var store = new FileBackupStore(backupRoot);

            // Two different calls for the same profile (different fileName) must land under
            // the same per-profile backup directory, not create a second one.
            store.BeforeRewrite(profileDir, "Folders.dbx");
            store.BeforeRewrite(profileDir, "Notas.dbx");

            string backupDir = Assert.Single(Directory.GetDirectories(backupRoot));
            Assert.True(File.Exists(Path.Combine(backupDir, "Folders.dbx.bak")));
            Assert.True(File.Exists(Path.Combine(backupDir, "Notas.dbx.bak")));
        }
        finally
        {
            Directory.Delete(profileDir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }

    [Fact]
    public void DeleteRemovesBothBackupFilesForAGivenFileName()
    {
        string profileDir = Path.Combine(Path.GetTempPath(), $"lec-profile-{Guid.NewGuid():N}");
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileDir);
        try
        {
            File.WriteAllText(Path.Combine(profileDir, "Notas.dbx"), "x");
            var store = new FileBackupStore(backupRoot);
            store.BeforeRewrite(profileDir, "Notas.dbx");

            store.Delete(profileDir, "Notas.dbx");

            string backupDir = Assert.Single(Directory.GetDirectories(backupRoot));
            Assert.False(File.Exists(Path.Combine(backupDir, "Notas.dbx.bak")));
            Assert.False(File.Exists(Path.Combine(backupDir, "Notas.dbx.original")));
        }
        finally
        {
            Directory.Delete(profileDir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }

    [Fact]
    public void DeleteOnAFileThatWasNeverBackedUpDoesNotThrow()
    {
        string profileDir = Path.Combine(Path.GetTempPath(), $"lec-profile-{Guid.NewGuid():N}");
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileDir);
        try
        {
            var store = new FileBackupStore(backupRoot);

            var ex = Record.Exception(() => store.Delete(profileDir, "NuncaExistiu.dbx"));

            Assert.Null(ex);
        }
        finally
        {
            Directory.Delete(profileDir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }

    [Fact]
    public void BeforeRewriteThrowsWhenTheBackupRootIsNotWritable()
    {
        // Global Constraint #1 (see backups-fora-do-perfil design doc): unlike the SQLite
        // caches' silent :memory: fallback, a backup failure must propagate and abort the
        // caller -- never be swallowed. Force that failure deterministically by pointing the
        // backup root at a path *under* an existing plain file: Directory.CreateDirectory
        // can never turn a file into a directory, so it reliably throws.
        string profileDir = Path.Combine(Path.GetTempPath(), $"lec-profile-{Guid.NewGuid():N}");
        string blockingFile = Path.Combine(Path.GetTempPath(), $"lec-blocker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(blockingFile, "not a directory");
        try
        {
            File.WriteAllText(Path.Combine(profileDir, "Folders.dbx"), "x");
            // A path *under* an existing plain file can never become a real directory.
            var store = new FileBackupStore(Path.Combine(blockingFile, "backups"));

            Assert.ThrowsAny<IOException>(() => store.BeforeRewrite(profileDir, "Folders.dbx"));
        }
        finally
        {
            Directory.Delete(profileDir, recursive: true);
            File.Delete(blockingFile);
        }
    }
}
