// tests/LEC.MailStore.Tests/DbxStoreScavengeTests.cs
using System.Linq;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreScavengeTests
{
    [Fact]
    public void OpenFallsBackToAnInMemoryCacheWhenTheCachePathIsNotWritable()
    {
        string profileDir = TestProfileBuilder.CreateEmptyProfile();
        // A path whose parent directory cannot exist (a file used as a directory
        // component) reliably fails ScavengeCache's Directory.CreateDirectory/SQLite
        // open, without depending on real filesystem permissions being restricted in CI.
        string bogusFile = Path.Combine(Path.GetTempPath(), $"lec-not-a-directory-{Guid.NewGuid():N}");
        File.WriteAllText(bogusFile, "not a directory");
        string unwritableCachePath = Path.Combine(bogusFile, "scavenge-cache.db");

        // Must not throw -- falls back to an in-memory cache instead of crashing.
        using var store = DbxStore.Open(profileDir, unwritableCachePath);

        var status = store.GetScavengeStatus(store.Folders[0]);
        Assert.Equal(ScavengeState.NeverScanned, status.State); // works fine, just not persisted

        File.Delete(bogusFile);
    }

    [Fact]
    public void GetFolderHealthReflectsTheFolderFilesOwnHealthNotJustPerMessageHealth()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int inboxNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
        });
        int foldersRoot = localFolders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, foldersRoot);

        // A normal, perfectly healthy message -- but the header claims 2 items while the
        // index tree only actually holds 1, which is exactly what makes DbxEmailFile.Health
        // come back Partial (Fase 1) even though the one real message has Health.Ok.
        var inbox = new TestDbxBuilder();
        inbox.WriteHeader(DbxFileType.Email);
        int block = inbox.WriteMessageBlock("From: a@b.com\r\nSubject: Normal\r\n\r\nOk"u8.ToArray(), nextOffset: 0);
        int infoNode = inbox.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)block },
        });
        int indexRoot = inbox.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: infoNode, entryNextTable: 0, entryIndexCount: 0) });
        inbox.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 2); // claims 2, tree only has 1
        inbox.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, indexRoot);

        string profileDir = TestProfileBuilder.WriteProfile(("Folders.dbx", localFolders), ("Inbox.dbx", inbox));
        string cachePath = Path.Combine(Path.GetTempPath(), $"lec-test-cache-{Guid.NewGuid():N}.db");

        using var store = DbxStore.Open(profileDir, cachePath);
        var folder = store.Folders.Single(f => f.Name == "Caixa de entrada");

        Assert.Equal(StoreHealth.Partial, store.GetFolderHealth(folder));
    }

    [Fact]
    public void GetScavengeStatusIsNeverScannedForAFreshlyOpenedProfile()
    {
        string profileDir = TestProfileBuilder.CreateEmptyProfile();
        string cachePath = Path.Combine(Path.GetTempPath(), $"lec-test-cache-{Guid.NewGuid():N}.db");

        using var store = DbxStore.Open(profileDir, cachePath);
        var folder = store.Folders[0];

        var status = store.GetScavengeStatus(folder);

        Assert.Equal(ScavengeState.NeverScanned, status.State);
        Assert.False(status.IsVisible);
        Assert.Equal(0, status.RecoveredCount);
    }

    [Fact]
    public async Task ScavengeAsyncFindsAnOrphanMessageAndUpdatesStatus()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int inboxNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
        });
        int foldersRoot = localFolders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, foldersRoot);

        var inbox = new TestDbxBuilder();
        inbox.WriteHeader(DbxFileType.Email);
        inbox.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 0); // no normal messages at all
        inbox.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, 0);
        inbox.WriteMessageBlock("From: c@d.com\r\nSubject: Orfa\r\n\r\nCorpo orfao"u8.ToArray(), nextOffset: 0);

        string profileDir = TestProfileBuilder.WriteProfile(("Folders.dbx", localFolders), ("Inbox.dbx", inbox));
        string cachePath = Path.Combine(Path.GetTempPath(), $"lec-test-cache-{Guid.NewGuid():N}.db");

        using var store = DbxStore.Open(profileDir, cachePath);
        var folder = store.Folders.Single(f => f.Name == "Caixa de entrada");

        var status = await store.ScavengeAsync(folder);

        Assert.Equal(ScavengeState.Scanned, status.State);
        Assert.Equal(1, status.RecoveredCount);
        Assert.False(status.IsVisible); // default: hidden until explicitly shown

        // A fresh GetScavengeStatus call (no re-scan) reflects the persisted result.
        var reread = store.GetScavengeStatus(folder);
        Assert.Equal(ScavengeState.Scanned, reread.State);
        Assert.Equal(1, reread.RecoveredCount);
    }

    [Fact]
    public async Task SetScavengeVisibilityPersistsAndIsReflectedInStatus()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int inboxNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
        });
        int foldersRoot = localFolders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, foldersRoot);

        var inbox = new TestDbxBuilder();
        inbox.WriteHeader(DbxFileType.Email);
        inbox.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 0);
        inbox.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, 0);
        inbox.WriteMessageBlock("From: c@d.com\r\nSubject: Orfa\r\n\r\nCorpo"u8.ToArray(), nextOffset: 0);

        string profileDir = TestProfileBuilder.WriteProfile(("Folders.dbx", localFolders), ("Inbox.dbx", inbox));
        string cachePath = Path.Combine(Path.GetTempPath(), $"lec-test-cache-{Guid.NewGuid():N}.db");

        using var store = DbxStore.Open(profileDir, cachePath);
        var folder = store.Folders.Single(f => f.Name == "Caixa de entrada");
        await store.ScavengeAsync(folder);

        store.SetScavengeVisibility(folder, visible: true);

        var status = store.GetScavengeStatus(folder);
        Assert.True(status.IsVisible);
    }
}
