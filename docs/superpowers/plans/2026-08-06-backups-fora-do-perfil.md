# Backups (.bak/.original) fora da pasta do perfil Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move where `DbxStore` writes `.bak`/`.original` safety copies — from "next to the real file, inside the profile directory" to `%LOCALAPPDATA%\LEC\backups\<hash-of-profile-path>\` — so the profile directory itself only ever contains what Outlook Express would recognize (`Folders.dbx` + each folder's own `.dbx`).

**Architecture:** A new `FileBackupStore` class (same shape as the existing `ScavengeCache`/`MovedMessageCache`/etc.: `DefaultRoot` static + optional override threaded through `DbxStore.Open`) replaces the repeated `.original`/`.bak` `File.Copy` block that appears in `CreateFolderAsync`, `MoveFolderAsync`, `DeleteFolderAsync`, and `AddMessageAsync`. `DeleteFolderAsync`'s physical-cleanup loop also calls the new store's `Delete` to remove a permanently-deleted folder's backups.

**Tech Stack:** C# / .NET 9, xUnit — no SQLite involved this time (plain filesystem, unlike the other caches).

## Global Constraints

- No fallback-on-failure for `FileBackupStore.BeforeRewrite` — if the backup location isn't writable, the exception propagates and the whole calling operation (`CreateFolderAsync`/`MoveFolderAsync`/`DeleteFolderAsync`/`AddMessageAsync`) aborts, exactly like today's `File.Copy` failures do. Confirmed with the user: this is a deliberate choice, not an oversight — unlike the SQLite caches' `:memory:` fallback, degrading silently here would defeat the point of a safety net.
- `FileBackupStore.Delete` must never throw — always best-effort and silent. It's only ever called after the real file is already permanently gone (`DeleteFolderAsync`), so losing a leftover backup artifact is never user-visible data loss.
- `.original` is write-once (only copied if it doesn't already exist at the backup location); `.bak` is always overwritten. Same semantics as today, just relocated.
- Follow the existing pattern exactly: a `DefaultXxx` static property, a class constructor taking the root path directly, and an optional `Xxx = null` parameter on `DbxStore.Open` that falls back to the default — mirroring `scavengeCacheDatabasePath`/`movedMessageCacheDatabasePath`/`deletedMessageCacheDatabasePath`/`hiddenFolderCacheDatabasePath`.
- This plan does not migrate or delete pre-existing `.bak`/`.original` files already sitting inside profile directories from before this change — out of scope, confirmed with the user.

---

### Task 1: `FileBackupStore` class + its own tests

**Files:**
- Create: `src/LEC.MailStore/FileBackupStore.cs`
- Create: `tests/LEC.MailStore.Tests/FileBackupStoreTests.cs`

**Interfaces:**
- Produces: `FileBackupStore.DefaultRoot` (static `string`), `new FileBackupStore(string root)`, `void BeforeRewrite(string profileDirectory, string fileName)`, `void Delete(string profileDirectory, string fileName)`. Later tasks (2-5) construct this class in `DbxStore.Open` and call these two methods — don't change these two signatures once written, every later task depends on them exactly as specified here.

This task is fully self-contained — no dependency on `DbxStore`, testable in complete isolation with plain temp directories and plain text file contents (the class doesn't know or care about the `.dbx` format).

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.MailStore.Tests/FileBackupStoreTests.cs`:

```csharp
// tests/LEC.MailStore.Tests/FileBackupStoreTests.cs
using LEC.MailStore;
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
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "FileBackupStoreTests"`
Expected: FAIL with compile errors — `FileBackupStore` doesn't exist yet.

- [ ] **Step 3: Create `src/LEC.MailStore/FileBackupStore.cs`**

```csharp
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
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "FileBackupStoreTests"`
Expected: PASS (all 7 facts).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/FileBackupStore.cs tests/LEC.MailStore.Tests/FileBackupStoreTests.cs
git commit -m "Add FileBackupStore for keeping .bak/.original copies outside the profile directory"
```

---

### Task 2: Wire `FileBackupStore` into `DbxStore` + `CreateFolderAsync`

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs` (constructor, field, `Open`, `CreateFolderAsync`)
- Modify: `tests/LEC.MailStore.Tests/DbxStoreCreateFolderTests.cs`

**Interfaces:**
- Consumes: `FileBackupStore.DefaultRoot`, `new FileBackupStore(string root)`, `.BeforeRewrite(string profileDirectory, string fileName)` from Task 1.
- Produces: `DbxStore.Open(..., string? backupRootDirectory = null)` — every later task (3, 4, 5) and their tests use this same parameter name and default-`null` behavior. `_backupStore` field, available to every `DbxStore` instance method from this task onward.

- [ ] **Step 1: Add the failing test**

In `tests/LEC.MailStore.Tests/DbxStoreCreateFolderTests.cs`, replace the existing `CreateFolderAsyncCreatesABackupOfFoldersDbx` test (around line 70-89):

```csharp
    [Fact]
    public async Task CreateFolderAsyncCreatesABackupOfFoldersDbx()
    {
        string dir = BuildProfileWithOneFolder();
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);

            await store.CreateFolderAsync("Mail 2026", parentId: null);

            string backupPath = Assert.Single(Directory.GetFiles(backupRoot, "Folders.dbx.bak", SearchOption.AllDirectories));
            Assert.Equal(originalBytes, File.ReadAllBytes(backupPath));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
```

Also replace `CreateFolderAsyncWhenFoldersDbxIsDamagedThrowsAndLeavesEverythingUntouched` (around line 225-264) — same test body, but add a `backupRoot` variable and change the final two assertions:

```csharp
    [Fact]
    public async Task CreateFolderAsyncWhenFoldersDbxIsDamagedThrowsAndLeavesEverythingUntouched()
    {
        // Build a Folders.dbx whose header item count (5) doesn't match what the index tree
        // actually yields (1 real entry) -- the same mismatch DbxFolderFileTests uses to
        // exercise StoreHealth.Partial. DbxStore.Open only reads Health once at open time, so
        // reopen this exact file fresh inside CreateFolderAsync's own DbxFolderFile.Open call
        // to make sure it - not just DbxStore.Open - is what gets checked.
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);
        int root = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Pastas locais\0"u8.ToArray() },
        });
        int rootIndexOffset = folders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: root, entryNextTable: 0, entryIndexCount: 0) });
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 5); // claims 5, tree only yields 1
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, rootIndexOffset);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateFolderAsync("Mail 2026", parentId: null));
            Assert.Contains("danificado", ex.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Folders.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Mail 2026.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
            Assert.False(Directory.Exists(backupRoot));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
```

Also replace `CreateFolderAsyncKeepsThePristineBackupAcrossMultipleCreates` (around line 266-290):

```csharp
    [Fact]
    public async Task CreateFolderAsyncKeepsThePristineBackupAcrossMultipleCreates()
    {
        string dir = BuildProfileWithOneFolder();
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);

            await store.CreateFolderAsync("Mail 2026", parentId: null);
            await store.CreateFolderAsync("Mail 2027", parentId: null);

            string pristinePath = Assert.Single(Directory.GetFiles(backupRoot, "Folders.dbx.original", SearchOption.AllDirectories));
            Assert.Equal(originalBytes, File.ReadAllBytes(pristinePath));

            // The rolling .bak reflects the state just before the *last* create, so it must
            // no longer match the very first bytes once more than one folder was created.
            string bakPath = Assert.Single(Directory.GetFiles(backupRoot, "Folders.dbx.bak", SearchOption.AllDirectories));
            Assert.NotEqual(originalBytes, File.ReadAllBytes(bakPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreCreateFolderTests"`
Expected: FAIL with a compile error — `DbxStore.Open` doesn't have a `backupRootDirectory` parameter yet.

- [ ] **Step 3: Wire `FileBackupStore` into `DbxStore.cs`**

Add the field, next to the other cache fields (`src/LEC.MailStore/DbxStore.cs`, near line 30):

```csharp
    private readonly HiddenFolderCache _hiddenFolderCache;

    private readonly FileBackupStore _backupStore;
```

Update the private constructor (`src/LEC.MailStore/DbxStore.cs`, around line 32-42) — replace:

```csharp
    private DbxStore(string profileDirectory, List<StoreFolder> folders, Dictionary<int, string?> backingFileNames, StoreHealth health, ScavengeCache scavengeCache, MovedMessageCache movedMessageCache, DeletedMessageCache deletedMessageCache, HiddenFolderCache hiddenFolderCache)
    {
        _profileDirectory = profileDirectory;
        _folders = folders;
        _backingFileNames = backingFileNames;
        Health = health;
        _scavengeCache = scavengeCache;
        _movedMessageCache = movedMessageCache;
        _deletedMessageCache = deletedMessageCache;
        _hiddenFolderCache = hiddenFolderCache;
    }
```

with:

```csharp
    private DbxStore(string profileDirectory, List<StoreFolder> folders, Dictionary<int, string?> backingFileNames, StoreHealth health, ScavengeCache scavengeCache, MovedMessageCache movedMessageCache, DeletedMessageCache deletedMessageCache, HiddenFolderCache hiddenFolderCache, FileBackupStore backupStore)
    {
        _profileDirectory = profileDirectory;
        _folders = folders;
        _backingFileNames = backingFileNames;
        Health = health;
        _scavengeCache = scavengeCache;
        _movedMessageCache = movedMessageCache;
        _deletedMessageCache = deletedMessageCache;
        _hiddenFolderCache = hiddenFolderCache;
        _backupStore = backupStore;
    }
```

Update `Open` (`src/LEC.MailStore/DbxStore.cs`, around line 486): change the signature line from:

```csharp
    public static DbxStore Open(string profileDirectoryPath, string? scavengeCacheDatabasePath = null, string? movedMessageCacheDatabasePath = null, string? deletedMessageCacheDatabasePath = null, string? hiddenFolderCacheDatabasePath = null)
```

to:

```csharp
    public static DbxStore Open(string profileDirectoryPath, string? scavengeCacheDatabasePath = null, string? movedMessageCacheDatabasePath = null, string? deletedMessageCacheDatabasePath = null, string? hiddenFolderCacheDatabasePath = null, string? backupRootDirectory = null)
```

Inside `Open`, right after the existing `var hiddenFolderCache = OpenHiddenFolderCacheWithFallback(...)` line, add:

```csharp
        var backupStore = new FileBackupStore(backupRootDirectory ?? FileBackupStore.DefaultRoot);
```

And update the final `return new DbxStore(...)` line at the end of `Open` from:

```csharp
        return new DbxStore(profileDirectoryPath, folders, backingFileNames, health, scavengeCache, movedMessageCache, deletedMessageCache, hiddenFolderCache);
```

to:

```csharp
        return new DbxStore(profileDirectoryPath, folders, backingFileNames, health, scavengeCache, movedMessageCache, deletedMessageCache, hiddenFolderCache, backupStore);
```

- [ ] **Step 4: Wire it into `CreateFolderAsync`**

In `src/LEC.MailStore/DbxStore.cs`, inside `CreateFolderAsync` (around line 122-131), replace:

```csharp
            DbxEmailFileWriter.CreateEmpty(filePath);
            try
            {
                // Write-once permanent copy of the very first, pristine, real
                // Outlook-Express-authored Folders.dbx — never overwritten again, so it
                // survives even after many later folder creations roll the ".bak" copy.
                string pristinePath = foldersDbxPath + ".original";
                if (!File.Exists(pristinePath))
                    File.Copy(foldersDbxPath, pristinePath);

                File.Copy(foldersDbxPath, foldersDbxPath + ".bak", overwrite: true);

                entries.Add(new DbxFolderWriteEntry(newId, parentId ?? -1, sanitizedName, fileName));
```

with:

```csharp
            DbxEmailFileWriter.CreateEmpty(filePath);
            try
            {
                _backupStore.BeforeRewrite(_profileDirectory, "Folders.dbx");

                entries.Add(new DbxFolderWriteEntry(newId, parentId ?? -1, sanitizedName, fileName));
```

- [ ] **Step 5: Run the tests to see them pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreCreateFolderTests"`
Expected: PASS (all facts in the file).

- [ ] **Step 6: Build and run the full solution's test suite to check nothing else broke**

Run: `dotnet build`
Expected: build succeeds — `MoveFolderAsync`/`DeleteFolderAsync`/`AddMessageAsync` still compile since they don't reference `_backupStore` yet (they still have their own inline `.bak`/`.original` code, untouched by this task).

Run: `dotnet test`
Expected: PASS except for any pre-existing test in `DbxStoreMoveFolderTests.cs`/`DbxStoreDeleteFolderTests.cs`/`DbxStoreAddMessageTests.cs` that asserts `.bak`/`.original` file locations — those still check the OLD (profile-directory) location because Tasks 3-5 haven't run yet, and `MoveFolderAsync`/`DeleteFolderAsync`/`AddMessageAsync` haven't changed yet either, so those tests should still be passing exactly as before (this task doesn't touch those methods at all). If everything is green, that confirms this task didn't regress anything outside its own scope.

- [ ] **Step 7: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreCreateFolderTests.cs
git commit -m "Wire FileBackupStore into DbxStore and CreateFolderAsync"
```

---

### Task 3: Wire `FileBackupStore` into `MoveFolderAsync`

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs` (`MoveFolderAsync`)
- Modify: `tests/LEC.MailStore.Tests/DbxStoreMoveFolderTests.cs`

**Interfaces:**
- Consumes: `_backupStore` field and `DbxStore.Open(..., backupRootDirectory: ...)` from Task 2.
- Produces: nothing new — same `_backupStore.BeforeRewrite` call pattern Task 2 already established for `CreateFolderAsync`.

- [ ] **Step 1: Add the failing test**

In `tests/LEC.MailStore.Tests/DbxStoreMoveFolderTests.cs`, replace `MoveFolderAsyncUpdatesTheBackupFile` (around line 216-232):

```csharp
    [Fact]
    public async Task MoveFolderAsyncUpdatesTheBackupFile()
    {
        string dir = BuildProfile();
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        try
        {
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);

            await store.MoveFolderAsync(4, newParentId: null);

            Assert.NotEmpty(Directory.GetFiles(backupRoot, "Folders.dbx.bak", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
```

- [ ] **Step 2: Run the test to see it fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "MoveFolderAsyncUpdatesTheBackupFile"`
Expected: FAIL — `Directory.GetFiles(backupRoot, ...)` throws `DirectoryNotFoundException` (nothing was written to `backupRoot` yet, since `MoveFolderAsync` still writes `.bak` next to `Folders.dbx` in `dir`).

- [ ] **Step 3: Wire it into `MoveFolderAsync`**

In `src/LEC.MailStore/DbxStore.cs`, inside `MoveFolderAsync` (around line 375-378), replace:

```csharp
            string pristinePath = foldersDbxPath + ".original";
            if (!File.Exists(pristinePath))
                File.Copy(foldersDbxPath, pristinePath);
            File.Copy(foldersDbxPath, foldersDbxPath + ".bak", overwrite: true);

            DbxFolderFileWriter.Rebuild(foldersDbxPath, entries);
```

with:

```csharp
            _backupStore.BeforeRewrite(_profileDirectory, "Folders.dbx");

            DbxFolderFileWriter.Rebuild(foldersDbxPath, entries);
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreMoveFolderTests"`
Expected: PASS (all facts in the file).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreMoveFolderTests.cs
git commit -m "Wire FileBackupStore into MoveFolderAsync"
```

---

### Task 4: Wire `FileBackupStore` into `DeleteFolderAsync` (backup on rewrite + cleanup on permanent delete)

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs` (`DeleteFolderAsync`)
- Modify: `tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs`

**Interfaces:**
- Consumes: `_backupStore` field, `.BeforeRewrite(...)`, and `.Delete(string profileDirectory, string fileName)` from Tasks 1-2.
- Produces: nothing new.

This task has two distinct changes to the same method: (1) `DeleteFolderAsync` rewrites `Folders.dbx`, so it needs the same `BeforeRewrite` call every other rewrite site has; (2) `DeleteFolderAsync`'s physical-cleanup loop, which today also tries to delete `fileName + ".bak"`/`".original"` from the profile directory (since that's where they used to live), needs to instead call `_backupStore.Delete` to remove them from the new location, and stop trying to delete them from the profile directory (they're never created there anymore).

- [ ] **Step 1: Update the existing tests that assert `.bak`/`.original` locations, and add one new test**

In `tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs`, replace `DeleteFolderAsyncOnASpecialFolderThrowsAndChangesNothing` (around line 134-152):

```csharp
    [Fact]
    public async Task DeleteFolderAsyncOnASpecialFolderThrowsAndChangesNothing()
    {
        string dir = BuildProfile();
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        try
        {
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(1));
            Assert.Contains("especia", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(store.Folders, f => f.Id == 1);
            Assert.True(File.Exists(Path.Combine(dir, "Inbox.dbx")));
            Assert.False(Directory.Exists(backupRoot));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
```

Replace `DeleteFolderAsyncWhoseSubtreeContainsASpecialFolderThrowsAndDeletesNothing` (around line 154-173):

```csharp
    [Fact]
    public async Task DeleteFolderAsyncWhoseSubtreeContainsASpecialFolderThrowsAndDeletesNothing()
    {
        string dir = BuildProfile();
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        try
        {
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(10)); // Raiz -> child 11 is special
            Assert.Contains("especia", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(store.Folders, f => f.Id == 10);
            Assert.Contains(store.Folders, f => f.Id == 11);
            Assert.True(File.Exists(Path.Combine(dir, "EspecialInbox.dbx")));
            Assert.False(Directory.Exists(backupRoot));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
```

Replace `DeleteFolderAsyncRewritesFoldersDbxAndCreatesBackups` (around line 230-250):

```csharp
    [Fact]
    public async Task DeleteFolderAsyncRewritesFoldersDbxAndCreatesBackups()
    {
        string dir = BuildProfile();
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);

            await store.DeleteFolderAsync(4);

            Assert.NotEqual(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Folders.dbx")));
            Assert.NotEmpty(Directory.GetFiles(backupRoot, "Folders.dbx.bak", SearchOption.AllDirectories));
            string pristinePath = Assert.Single(Directory.GetFiles(backupRoot, "Folders.dbx.original", SearchOption.AllDirectories));
            Assert.Equal(originalBytes, File.ReadAllBytes(pristinePath));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.original")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
```

Replace `DeleteFolderAsyncWhenFoldersDbxIsDamagedThrowsAndDeletesNothing` (around line 252-295) — only the `dir`/backup-root setup and the final assertions change, the `TestDbxBuilder` setup at the top stays identical:

```csharp
    [Fact]
    public async Task DeleteFolderAsyncWhenFoldersDbxIsDamagedThrowsAndDeletesNothing()
    {
        // Same header/tree item-count mismatch trick as
        // DbxStoreCreateFolderTests.CreateFolderAsyncWhenFoldersDbxIsDamagedThrowsAndLeavesEverythingUntouched
        // (claims 5 entries, the index tree only yields 1) -- DbxStore.Open only reads Health
        // once at open time, so this makes sure DeleteFolderAsync's own fresh
        // DbxFolderFile.Open call -- not just DbxStore.Open -- is what gets checked.
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);
        int root = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Notas.dbx\0"u8.ToArray() },
        });
        int rootIndexOffset = folders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: root, entryNextTable: 0, entryIndexCount: 0) });
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 5); // claims 5, tree only yields 1
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, rootIndexOffset);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        var empty = new TestDbxBuilder();
        empty.WriteHeader(DbxFileType.Email);
        empty.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 0);
        File.WriteAllBytes(Path.Combine(dir, "Notas.dbx"), empty.ToArray());
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(1));
            Assert.Contains("danificado", ex.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Folders.dbx")));
            Assert.True(File.Exists(Path.Combine(dir, "Notas.dbx")));
            Assert.False(Directory.Exists(backupRoot));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
```

Add a new test proving a permanent delete also removes that folder's backups (place it after `DeleteFolderAsyncWhenABackingFileIsLockedStillDeletesTheFolderButThrowsListingIt`):

```csharp
    [Fact]
    public async Task DeleteFolderAsyncAlsoRemovesTheDeletedFoldersBackupFiles()
    {
        string dir = BuildProfile();
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        try
        {
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);

            // Give folder 4's own file (NotasQ1.dbx) a backup first, via an unrelated
            // rewrite (adding a message), so there's something for DeleteFolderAsync's
            // cleanup to actually remove.
            await store.AddMessageAsync(4, "conteudo"u8.ToArray(), "Oi", null, null, null, null);
            Assert.NotEmpty(Directory.GetFiles(backupRoot, "NotasQ1.dbx.bak", SearchOption.AllDirectories));

            await store.DeleteFolderAsync(4);

            Assert.Empty(Directory.GetFiles(backupRoot, "NotasQ1.dbx.bak", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(backupRoot, "NotasQ1.dbx.original", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreDeleteFolderTests"`
Expected: FAIL — the rewritten tests expect the new backup location, but `DeleteFolderAsync` still writes `.bak`/`.original` next to `Folders.dbx` in `dir`. The new `DeleteFolderAsyncAlsoRemovesTheDeletedFoldersBackupFiles` test fails because nothing is written to `backupRoot` yet.

- [ ] **Step 3: Wire it into `DeleteFolderAsync`**

In `src/LEC.MailStore/DbxStore.cs`, inside `DeleteFolderAsync` (around line 223-228), replace:

```csharp
            string pristinePath = foldersDbxPath + ".original";
            if (!File.Exists(pristinePath))
                File.Copy(foldersDbxPath, pristinePath);
            File.Copy(foldersDbxPath, foldersDbxPath + ".bak", overwrite: true);

            DbxFolderFileWriter.Rebuild(foldersDbxPath, entries);
```

with:

```csharp
            _backupStore.BeforeRewrite(_profileDirectory, "Folders.dbx");

            DbxFolderFileWriter.Rebuild(foldersDbxPath, entries);
```

Then, in the same method's cleanup loop (around line 251-276), replace:

```csharp
            var failures = new List<string>();
            foreach (string fileName in fileNames)
            {
                // These caches key on (profilePath, fileName), not FolderId -- without this,
                // a future folder that reuses the same sanitized name (same fileName) would
                // silently inherit this deleted folder's hidden/scavenged offsets.
                _scavengeCache.ClearScan(_profileDirectory, fileName);
                _movedMessageCache.ClearFolder(_profileDirectory, fileName);
                _deletedMessageCache.ClearFolder(_profileDirectory, fileName);

                // Best-effort: Folders.dbx has already been rewritten by this point, so the
                // folder is permanently gone from the tree either way. A file that can't be
                // deleted (e.g. locked by another process) becomes an orphan on disk instead
                // of blocking the rest of the subtree's cleanup.
                foreach (string candidate in new[] { fileName, fileName + ".bak", fileName + ".original" })
                {
                    try
                    {
                        File.Delete(Path.Combine(_profileDirectory, candidate)); // no-op if it never existed
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failures.Add($"{candidate} ({ex.Message})");
                    }
                }
            }
```

with:

```csharp
            var failures = new List<string>();
            foreach (string fileName in fileNames)
            {
                // These caches key on (profilePath, fileName), not FolderId -- without this,
                // a future folder that reuses the same sanitized name (same fileName) would
                // silently inherit this deleted folder's hidden/scavenged offsets.
                _scavengeCache.ClearScan(_profileDirectory, fileName);
                _movedMessageCache.ClearFolder(_profileDirectory, fileName);
                _deletedMessageCache.ClearFolder(_profileDirectory, fileName);
                _backupStore.Delete(_profileDirectory, fileName);

                // Best-effort: Folders.dbx has already been rewritten by this point, so the
                // folder is permanently gone from the tree either way. A file that can't be
                // deleted (e.g. locked by another process) becomes an orphan on disk instead
                // of blocking the rest of the subtree's cleanup. Its .bak/.original live in
                // FileBackupStore's own directory now, not here, so only the real .dbx is a
                // candidate for this loop.
                try
                {
                    File.Delete(Path.Combine(_profileDirectory, fileName)); // no-op if it never existed
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{fileName} ({ex.Message})");
                }
            }
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreDeleteFolderTests"`
Expected: PASS (all facts in the file, including the new one).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs
git commit -m "Wire FileBackupStore into DeleteFolderAsync, including cleanup on permanent delete"
```

---

### Task 5: Wire `FileBackupStore` into `AddMessageAsync`

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs` (`AddMessageAsync`)
- Modify: `tests/LEC.MailStore.Tests/DbxStoreAddMessageTests.cs`

**Interfaces:**
- Consumes: `_backupStore` field from Task 2.
- Produces: nothing new.

- [ ] **Step 1: Update the failing test**

In `tests/LEC.MailStore.Tests/DbxStoreAddMessageTests.cs`, replace `AddMessageAsyncCreatesBackupsOfTheDestinationFile` (around line 280-300):

```csharp
    [Fact]
    public async Task AddMessageAsyncCreatesBackupsOfTheDestinationFile()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        string backupRoot = Path.Combine(Path.GetTempPath(), $"lec-backups-{Guid.NewGuid():N}");
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Destino.dbx"));
            using var store = DbxStore.Open(dir, backupRootDirectory: backupRoot);
            int folderId = store.Folders.Single().Id;

            await store.AddMessageAsync(folderId, Encoding.UTF8.GetBytes("x"), "S", null, null, null, null);

            Assert.NotEmpty(Directory.GetFiles(backupRoot, "Destino.dbx.bak", SearchOption.AllDirectories));
            string pristinePath = Assert.Single(Directory.GetFiles(backupRoot, "Destino.dbx.original", SearchOption.AllDirectories));
            Assert.Equal(originalBytes, File.ReadAllBytes(pristinePath));
            Assert.False(File.Exists(Path.Combine(dir, "Destino.dbx.bak")));
            Assert.False(File.Exists(Path.Combine(dir, "Destino.dbx.original")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
    }
```

- [ ] **Step 2: Run the test to see it fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "AddMessageAsyncCreatesBackupsOfTheDestinationFile"`
Expected: FAIL — `AddMessageAsync` still writes `.bak`/`.original` next to `Destino.dbx` in `dir`.

- [ ] **Step 3: Wire it into `AddMessageAsync`**

In `src/LEC.MailStore/DbxStore.cs`, inside `AddMessageAsync` (around line 445-448), replace:

```csharp
            string pristinePath = filePath + ".original";
            if (!File.Exists(pristinePath))
                File.Copy(filePath, pristinePath);
            File.Copy(filePath, filePath + ".bak", overwrite: true);
```

with:

```csharp
            _backupStore.BeforeRewrite(_profileDirectory, fileName);
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreAddMessageTests"`
Expected: PASS (all facts in the file).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreAddMessageTests.cs
git commit -m "Wire FileBackupStore into AddMessageAsync"
```

---

### Task 6: Full verification

**Files:** none (verification only).

- [ ] **Step 1: Confirm no `.bak`/`.original` references remain in `DbxStore.cs` outside `FileBackupStore` usage**

Run: `grep -n "\.bak\|\.original" src/LEC.MailStore/DbxStore.cs`
Expected: no output (every direct `File.Copy(..., ".bak"/".original")` call has been replaced by a `_backupStore.BeforeRewrite`/`.Delete` call).

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test`
Expected: PASS, 0 failures, including every `FileBackupStoreTests`, `DbxStoreCreateFolderTests`, `DbxStoreMoveFolderTests`, `DbxStoreDeleteFolderTests`, and `DbxStoreAddMessageTests` fact.

- [ ] **Step 3: Build the whole solution**

Run: `dotnet build`
Expected: build succeeds with no new warnings.

- [ ] **Step 4: Manual sanity check (not automated)**

Run the app (`dotnet run --project src/LEC.App`) against a scratch copy of a profile directory, create a folder, add/move/delete a message or folder, then check with Explorer/`dir` that:
1. The profile directory contains only `Folders.dbx` and each folder's own `.dbx` — no `.bak`/`.original` files.
2. `%LOCALAPPDATA%\LEC\backups\` has a new subdirectory (named as a long hex string) containing `profile-path.txt` (matching the profile you used) plus the expected `.bak`/`.original` pairs.

- [ ] **Step 5: No commit for this task** (verification only — nothing to stage).
