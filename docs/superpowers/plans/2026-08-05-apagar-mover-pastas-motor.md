# Apagar e mover pastas — motor (sem UI) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let `DbxStore` delete a whole folder subtree (hidden from `Folders`, nothing on disk touched) and move a folder to a new parent (a from-scratch rewrite of `Folders.dbx` that only changes the moved folder's `ParentId`, so its entire subtree comes along automatically). Fully testable end to end without any UI or `IMailStore` interface change.

**Architecture:** A new `HiddenFolderCache` (same SQLite-in-AppData shape as `DeletedMessageCache`, but keyed by `folder_id` instead of a message offset) records which folders are hidden per profile. `DbxStore.Open` filters hidden ids out of `Folders`/`_backingFileNames` before the store exists. `DbxStore.DeleteFolderAsync` walks the in-memory `ParentId` tree to collect a folder's full subtree, rejects the whole operation if any folder in it is special, then marks every id in the cache and removes it from memory. `DbxStore.MoveFolderAsync` reuses `DbxFolderFileWriter.Rebuild` (already shipped for `CreateFolderAsync`) to rewrite `Folders.dbx` with one entry's `ParentId` changed, after validating the target isn't the folder itself, isn't one of its own descendants, and doesn't collide with a sibling name. Neither method is added to `IMailStore` yet — both live as public methods directly on `DbxStore`, same sequencing already used for `CreateFolderAsync` (promoted onto the interface only in the UI slice, so this plan never has to touch `LEC.App`/`FakeMailStore`).

**Tech Stack:** .NET 9, C#, Microsoft.Data.Sqlite, xUnit.

## Global Constraints

- Special folders (`StoreFolder.SpecialFolderKind != SpecialFolderKind.None`) can never be deleted or moved — including when one shows up inside the subtree of a folder being deleted. The whole operation is rejected before anything is marked/rewritten; never a partial hide.
- Deleting a folder never touches `Folders.dbx` or any message `.dbx` — purely `HiddenFolderCache` + in-memory removal from `_folders`/`_backingFileNames`.
- `HiddenFolderCache` is keyed by `folder_id`, not an offset, and needs no `ClearFolder`/purge method: `CreateFolderAsync` and `MoveFolderAsync` both preserve every existing entry's `FolderId` when rewriting `Folders.dbx` (they only add or retarget one entry's `ParentId`), so a `FolderId` marked hidden can never later identify a different folder.
- Deleting the same folder twice is **not** idempotent (unlike `DeleteMessageAsync`): the second call throws `InvalidOperationException` because the id no longer exists in `_folders`. Deliberate — the first call already makes the folder disappear from the UI, so a second call for the same id can only happen from a bug, and a loud failure is preferred over silently swallowing it.
- Moving a folder is same-store only. `MoveFolderAsync(int folderId, int? newParentId, ...)` has no profile/store parameter — cross-profile folder moves are out of scope.
- Reference: `docs/superpowers/specs/2026-08-05-apagar-mover-pastas-motor-design.md` (full design this plan implements) and `docs/superpowers/specs/2026-08-03-criar-pastas-motor-dbx-design.md` (the `Folders.dbx` rebuild pattern this plan reuses).

---

## File Structure

```
src/LEC.MailStore/
  HiddenFolderCache.cs           NEW — Task 1: same shape as DeletedMessageCache.cs, keyed by folder_id
  DbxStore.cs                    MODIFIED — Task 2: +_hiddenFolderCache field/ctor param/Open wiring/Open filtering/Dispose, +DeleteFolderAsync, +CollectSubtreeIds
                                              Task 3: +MoveFolderAsync

tests/LEC.MailStore.Tests/
  HiddenFolderCacheTests.cs      NEW — Task 1
  DbxStoreDeleteFolderTests.cs   NEW — Task 2
  DbxStoreMoveFolderTests.cs     NEW — Task 3
```

---

### Task 1: `HiddenFolderCache`

**Files:**
- Create: `src/LEC.MailStore/HiddenFolderCache.cs`
- Test: `tests/LEC.MailStore.Tests/HiddenFolderCacheTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Data.Sqlite` (existing dependency, already used by `ScavengeCache`/`DeletedMessageCache`).
- Produces: `HiddenFolderCache(string databasePath)`, `.DefaultDatabasePath` (static), `.GetHiddenFolderIds(string profilePath) : HashSet<int>`, `.MarkHidden(string profilePath, int folderId) : void`, `.Dispose()`. Consumed by Task 2.

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.MailStore.Tests/HiddenFolderCacheTests.cs`:

```csharp
// tests/LEC.MailStore.Tests/HiddenFolderCacheTests.cs
using Xunit;

namespace LEC.MailStore.Tests;

public class HiddenFolderCacheTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lec-hidden-folder-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void GetHiddenFolderIdsReturnsEmptyWhenNothingMarked()
    {
        using var cache = new HiddenFolderCache(TempDbPath());

        var ids = cache.GetHiddenFolderIds(@"C:\profiles\BCP");

        Assert.Empty(ids);
    }

    [Fact]
    public void MarkHiddenThenGetHiddenFolderIdsRoundTrips()
    {
        string dbPath = TempDbPath();

        using (var cache = new HiddenFolderCache(dbPath))
            cache.MarkHidden(@"C:\profiles\BCP", 7);

        using (var reopened = new HiddenFolderCache(dbPath))
        {
            var ids = reopened.GetHiddenFolderIds(@"C:\profiles\BCP");
            Assert.Contains(7, ids);
        }
    }

    [Fact]
    public void MarkHiddenTwiceForTheSameFolderIsIdempotent()
    {
        using var cache = new HiddenFolderCache(TempDbPath());

        cache.MarkHidden(@"C:\p", 100);
        cache.MarkHidden(@"C:\p", 100); // must not throw

        Assert.Single(cache.GetHiddenFolderIds(@"C:\p"));
    }

    [Fact]
    public void MarkHiddenForMultipleFoldersInTheSameProfileAccumulates()
    {
        using var cache = new HiddenFolderCache(TempDbPath());

        cache.MarkHidden(@"C:\p", 1);
        cache.MarkHidden(@"C:\p", 2);
        cache.MarkHidden(@"C:\p", 3);

        Assert.Equal(new[] { 1, 2, 3 }, cache.GetHiddenFolderIds(@"C:\p").OrderBy(x => x));
    }

    [Fact]
    public void DifferentProfilesWithTheSameFolderIdDoNotCollide()
    {
        using var cache = new HiddenFolderCache(TempDbPath());

        cache.MarkHidden(@"C:\profileA", 5);
        cache.MarkHidden(@"C:\profileB", 5);

        Assert.Equal(new[] { 5 }, cache.GetHiddenFolderIds(@"C:\profileA"));
        Assert.Equal(new[] { 5 }, cache.GetHiddenFolderIds(@"C:\profileB"));

        // Hiding folder 5 in profile A must not affect profile B's independent record of it.
        using var otherCache = new HiddenFolderCache(TempDbPath());
        otherCache.MarkHidden(@"C:\profileA", 6);
        Assert.DoesNotContain(6, otherCache.GetHiddenFolderIds(@"C:\profileB"));
    }
}
```

Add `using System.Linq;` is not needed here since `OrderBy` is used only via LINQ on `HashSet<int>` — add `using System.Linq;` at the top of the file to be safe:

```csharp
// tests/LEC.MailStore.Tests/HiddenFolderCacheTests.cs
using System.Linq;
using Xunit;
```

(Replace the two-line `using` block at the top of the file above with this one, i.e. the final file starts with `using System.Linq;` then `using Xunit;` then the blank line and `namespace`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "HiddenFolderCacheTests"`
Expected: FAIL to compile — `HiddenFolderCache` doesn't exist yet.

- [ ] **Step 3: Implement `HiddenFolderCache`**

Create `src/LEC.MailStore/HiddenFolderCache.cs`:

```csharp
// src/LEC.MailStore/HiddenFolderCache.cs
using Microsoft.Data.Sqlite;

namespace LEC.MailStore;

/// <summary>
/// Tracks folders "deleted" (hidden from DbxStore.Folders, never removed from Folders.dbx
/// or any message .dbx — see docs/superpowers/specs/2026-08-05-apagar-mover-pastas-motor-design.md
/// §3) so DbxStore.Open/DeleteFolderAsync can hide a whole subtree without ever touching a
/// file on disk. Same pattern as DeletedMessageCache/MovedMessageCache: lightweight SQLite
/// under %LOCALAPPDATA%\LEC\, never next to the source profile.
///
/// Unlike those two caches, this one is keyed by folder_id rather than an offset into a
/// file, and has no ClearFolder/purge method: CreateFolderAsync and MoveFolderAsync always
/// preserve every existing folder's FolderId when they rewrite Folders.dbx (they only add
/// or retarget one entry's ParentId), so a FolderId marked hidden here can never later end
/// up identifying a different folder the way a rebuilt message's index_node_offset can.
/// </summary>
public sealed class HiddenFolderCache : IDisposable
{
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LEC", "hidden-folder-cache.db");

    private readonly SqliteConnection _connection;

    public HiddenFolderCache(string databasePath)
    {
        string? dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS hidden_folder (
                profile_path TEXT    NOT NULL,
                folder_id    INTEGER NOT NULL,
                PRIMARY KEY (profile_path, folder_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public HashSet<int> GetHiddenFolderIds(string profilePath)
    {
        var ids = new HashSet<int>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT folder_id FROM hidden_folder WHERE profile_path = $p";
        cmd.Parameters.AddWithValue("$p", profilePath);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));

        return ids;
    }

    public void MarkHidden(string profilePath, int folderId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO hidden_folder (profile_path, folder_id)
            VALUES ($p, $id)
            """;
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "HiddenFolderCacheTests"`
Expected: PASS — all 5 tests.

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/HiddenFolderCache.cs tests/LEC.MailStore.Tests/HiddenFolderCacheTests.cs
git commit -m "Add HiddenFolderCache: local, profile-scoped record of folders hidden by id"
```

---

### Task 2: `DbxStore.DeleteFolderAsync`

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Test: `tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs`

**Interfaces:**
- Consumes: `HiddenFolderCache` (Task 1).
- Produces: `DbxStore.Open(string profileDirectoryPath, string? scavengeCacheDatabasePath = null, string? movedMessageCacheDatabasePath = null, string? deletedMessageCacheDatabasePath = null, string? hiddenFolderCacheDatabasePath = null) : DbxStore` (new optional parameter, existing callers unaffected), `DbxStore.DeleteFolderAsync(int folderId, CancellationToken ct = default) : Task`. Consumed by Task 3 (shares `_folders`/cache-eviction plumbing) and by the UI plan's Task 1 (promotes this method onto `IMailStore`).

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs`:

```csharp
// tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs
using System.Linq;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using LEC.MailStore;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreDeleteFolderTests
{
    // Builds a profile with:
    //   1  Caixa de entrada   (special: Inbox, root)            Inbox.dbx
    //   2  Notas              (root)                             Notas.dbx
    //   3  Notas 2026         (child of 2)                       Notas2026.dbx
    //   4  Notas 2026 Q1      (child of 3)                       NotasQ1.dbx
    //  10  Raiz                (root)                            (no backing file)
    //  11  Especial            (special: Inbox, child of 10)     EspecialInbox.dbx
    private static string BuildProfile()
    {
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);

        int n1 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });
        int n2 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 2 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Notas.dbx\0"u8.ToArray() },
        });
        int n3 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 3 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(2) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas 2026\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Notas2026.dbx\0"u8.ToArray() },
        });
        int n4 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 4 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(3) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas 2026 Q1\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "NotasQ1.dbx\0"u8.ToArray() },
        });
        int n10 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 10 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Raiz\0"u8.ToArray() },
        });
        int n11 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 11 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(10) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Especial\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "EspecialInbox.dbx\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });

        int[] nodes = { n1, n2, n3, n4, n10, n11 };
        int root = folders.WriteIndexNode(nextTable: 0, ptrCount: (byte)nodes.Length, indexCount: 0,
            entries: nodes.Select(n => (indexPtr: n, entryNextTable: 0, entryIndexCount: 0)).ToArray());
        folders.SetInt32At(DbxFormat.HeaderItemCountOffset, nodes.Length);
        folders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        foreach (string fileName in new[] { "Inbox.dbx", "Notas.dbx", "Notas2026.dbx", "NotasQ1.dbx", "EspecialInbox.dbx" })
        {
            var empty = new TestDbxBuilder();
            empty.WriteHeader(DbxFileType.Email);
            empty.SetInt32At(DbxFormat.HeaderItemCountOffset, 0);
            File.WriteAllBytes(Path.Combine(dir, fileName), empty.ToArray());
        }
        return dir;
    }

    [Fact]
    public async Task DeleteFolderAsyncOnALeafFolderHidesOnlyThatFolder()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.DeleteFolderAsync(4);

            Assert.DoesNotContain(store.Folders, f => f.Id == 4);
            Assert.Contains(store.Folders, f => f.Id == 3);
            Assert.Contains(store.Folders, f => f.Id == 2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncHidesTheWholeSubtreeAtOnce()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.DeleteFolderAsync(2); // Notas -> also hides 3 (child) and 4 (grandchild)

            Assert.DoesNotContain(store.Folders, f => f.Id == 2);
            Assert.DoesNotContain(store.Folders, f => f.Id == 3);
            Assert.DoesNotContain(store.Folders, f => f.Id == 4);
            Assert.Contains(store.Folders, f => f.Id == 1); // Inbox untouched
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncOnASpecialFolderThrowsAndChangesNothing()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(1));
            Assert.Contains("especia", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(store.Folders, f => f.Id == 1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncWhoseSubtreeContainsASpecialFolderThrowsAndHidesNothing()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(10)); // Raiz -> child 11 is special
            Assert.Contains("especia", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(store.Folders, f => f.Id == 10);
            Assert.Contains(store.Folders, f => f.Id == 11);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncOnANonexistentFolderThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(999));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncCalledTwiceForTheSameIdThrowsOnTheSecondCall()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.DeleteFolderAsync(4);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(4));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncPersistsAcrossReopen()
    {
        string dir = BuildProfile();
        string hiddenCacheDbPath = Path.Combine(Path.GetTempPath(), $"lec-hidden-folder-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = DbxStore.Open(dir, hiddenFolderCacheDatabasePath: hiddenCacheDbPath))
                await store.DeleteFolderAsync(2);

            using var reopened = DbxStore.Open(dir, hiddenFolderCacheDatabasePath: hiddenCacheDbPath);
            Assert.DoesNotContain(reopened.Folders, f => f.Id == 2);
            Assert.DoesNotContain(reopened.Folders, f => f.Id == 3);
            Assert.DoesNotContain(reopened.Folders, f => f.Id == 4);
            Assert.Contains(reopened.Folders, f => f.Id == 1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            File.Delete(hiddenCacheDbPath);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncNeverTouchesFoldersDbxOnDisk()
    {
        string dir = BuildProfile();
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir);

            await store.DeleteFolderAsync(4);

            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Folders.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreDeleteFolderTests"`
Expected: FAIL to compile — `DbxStore.DeleteFolderAsync` and the `hiddenFolderCacheDatabasePath` parameter of `DbxStore.Open` don't exist yet.

- [ ] **Step 3: Wire `HiddenFolderCache` into `DbxStore`'s fields, constructor, and `Open`**

In `src/LEC.MailStore/DbxStore.cs`, replace:

```csharp
    private readonly DeletedMessageCache _deletedMessageCache;
    private readonly Dictionary<int, HashSet<int>> _deletedOffsetsByFolder = new();

    private DbxStore(string profileDirectory, List<StoreFolder> folders, Dictionary<int, string?> backingFileNames, StoreHealth health, ScavengeCache scavengeCache, MovedMessageCache movedMessageCache, DeletedMessageCache deletedMessageCache)
    {
        _profileDirectory = profileDirectory;
        _folders = folders;
        _backingFileNames = backingFileNames;
        Health = health;
        _scavengeCache = scavengeCache;
        _movedMessageCache = movedMessageCache;
        _deletedMessageCache = deletedMessageCache;
    }
```

with:

```csharp
    private readonly DeletedMessageCache _deletedMessageCache;
    private readonly Dictionary<int, HashSet<int>> _deletedOffsetsByFolder = new();

    private readonly HiddenFolderCache _hiddenFolderCache;

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

Then replace `Open` in full:

```csharp
    public static DbxStore Open(string profileDirectoryPath, string? scavengeCacheDatabasePath = null, string? movedMessageCacheDatabasePath = null, string? deletedMessageCacheDatabasePath = null)
    {
        string foldersDbxPath = Path.Combine(profileDirectoryPath, "Folders.dbx");
        using var folderFile = DbxFolderFile.Open(foldersDbxPath);

        var folders = new List<StoreFolder>();
        var backingFileNames = new Dictionary<int, string?>();

        foreach (var f in folderFile.Folders)
        {
            string? fileName = string.IsNullOrEmpty(f.FileName) ? null : f.FileName;
            bool isLoadable = fileName is null || File.Exists(Path.Combine(profileDirectoryPath, fileName));

            folders.Add(new StoreFolder
            {
                Id = f.FolderId,
                // -1 is the verified real sentinel for "no parent" (see Global Constraints).
                // Every other value is passed through unresolved — orphan tolerance is the
                // tree-builder's job (LEC.App), not DbxStore's.
                ParentId = f.ParentId == -1 ? null : f.ParentId,
                Name = f.Name ?? string.Empty,
                SpecialFolderKind = ToSpecialFolderKind(f.SpecialFolderType),
                MessageCount = f.MessageCount,
                IsLoadable = isLoadable,
                HasBackingFile = fileName is not null,
            });
            backingFileNames[f.FolderId] = fileName;
        }

        var health = folderFile.Health; // read before the `using` disposes folderFile below
        var scavengeCache = OpenScavengeCacheWithFallback(scavengeCacheDatabasePath ?? ScavengeCache.DefaultDatabasePath);
        var movedMessageCache = OpenMovedMessageCacheWithFallback(movedMessageCacheDatabasePath ?? MovedMessageCache.DefaultDatabasePath);
        var deletedMessageCache = OpenDeletedMessageCacheWithFallback(deletedMessageCacheDatabasePath ?? DeletedMessageCache.DefaultDatabasePath);
        return new DbxStore(profileDirectoryPath, folders, backingFileNames, health, scavengeCache, movedMessageCache, deletedMessageCache);
    }
```

with:

```csharp
    public static DbxStore Open(string profileDirectoryPath, string? scavengeCacheDatabasePath = null, string? movedMessageCacheDatabasePath = null, string? deletedMessageCacheDatabasePath = null, string? hiddenFolderCacheDatabasePath = null)
    {
        string foldersDbxPath = Path.Combine(profileDirectoryPath, "Folders.dbx");
        using var folderFile = DbxFolderFile.Open(foldersDbxPath);

        var folders = new List<StoreFolder>();
        var backingFileNames = new Dictionary<int, string?>();

        foreach (var f in folderFile.Folders)
        {
            string? fileName = string.IsNullOrEmpty(f.FileName) ? null : f.FileName;
            bool isLoadable = fileName is null || File.Exists(Path.Combine(profileDirectoryPath, fileName));

            folders.Add(new StoreFolder
            {
                Id = f.FolderId,
                // -1 is the verified real sentinel for "no parent" (see Global Constraints).
                // Every other value is passed through unresolved — orphan tolerance is the
                // tree-builder's job (LEC.App), not DbxStore's.
                ParentId = f.ParentId == -1 ? null : f.ParentId,
                Name = f.Name ?? string.Empty,
                SpecialFolderKind = ToSpecialFolderKind(f.SpecialFolderType),
                MessageCount = f.MessageCount,
                IsLoadable = isLoadable,
                HasBackingFile = fileName is not null,
            });
            backingFileNames[f.FolderId] = fileName;
        }

        var health = folderFile.Health; // read before the `using` disposes folderFile below
        var scavengeCache = OpenScavengeCacheWithFallback(scavengeCacheDatabasePath ?? ScavengeCache.DefaultDatabasePath);
        var movedMessageCache = OpenMovedMessageCacheWithFallback(movedMessageCacheDatabasePath ?? MovedMessageCache.DefaultDatabasePath);
        var deletedMessageCache = OpenDeletedMessageCacheWithFallback(deletedMessageCacheDatabasePath ?? DeletedMessageCache.DefaultDatabasePath);
        var hiddenFolderCache = OpenHiddenFolderCacheWithFallback(hiddenFolderCacheDatabasePath ?? HiddenFolderCache.DefaultDatabasePath);

        // Folders hidden by a previous DeleteFolderAsync call (this session or an earlier
        // one — the cache is persistent) must never re-enter Folders/backingFileNames: this
        // is the ONLY place that filter is applied, so every other DbxStore member (and
        // every LEC.App consumer of Folders) never has to know a hidden folder ever existed.
        var hiddenIds = hiddenFolderCache.GetHiddenFolderIds(profileDirectoryPath);
        if (hiddenIds.Count > 0)
        {
            folders.RemoveAll(f => hiddenIds.Contains(f.Id));
            foreach (int id in hiddenIds)
                backingFileNames.Remove(id);
        }

        return new DbxStore(profileDirectoryPath, folders, backingFileNames, health, scavengeCache, movedMessageCache, deletedMessageCache, hiddenFolderCache);
    }
```

- [ ] **Step 4: Add the fallback opener and dispose the new cache**

In `src/LEC.MailStore/DbxStore.cs`, replace:

```csharp
    // Same fallback reasoning as OpenScavengeCacheWithFallback, for the deleted-message cache.
    private static DeletedMessageCache OpenDeletedMessageCacheWithFallback(string databasePath)
    {
        try
        {
            return new DeletedMessageCache(databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new DeletedMessageCache(":memory:");
        }
    }
```

with:

```csharp
    // Same fallback reasoning as OpenScavengeCacheWithFallback, for the deleted-message cache.
    private static DeletedMessageCache OpenDeletedMessageCacheWithFallback(string databasePath)
    {
        try
        {
            return new DeletedMessageCache(databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new DeletedMessageCache(":memory:");
        }
    }

    // Same fallback reasoning as OpenScavengeCacheWithFallback, for the hidden-folder cache.
    private static HiddenFolderCache OpenHiddenFolderCacheWithFallback(string databasePath)
    {
        try
        {
            return new HiddenFolderCache(databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new HiddenFolderCache(":memory:");
        }
    }
```

Then replace `Dispose`:

```csharp
    public void Dispose()
    {
        foreach (var file in _openFolders.Values)
            file.Dispose();
        _openFolders.Clear();
        _scavengeCache.Dispose();
        _movedMessageCache.Dispose();
        _deletedMessageCache.Dispose();
    }
```

with:

```csharp
    public void Dispose()
    {
        foreach (var file in _openFolders.Values)
            file.Dispose();
        _openFolders.Clear();
        _scavengeCache.Dispose();
        _movedMessageCache.Dispose();
        _deletedMessageCache.Dispose();
        _hiddenFolderCache.Dispose();
    }
```

- [ ] **Step 5: Implement `DeleteFolderAsync` and `CollectSubtreeIds`**

In `src/LEC.MailStore/DbxStore.cs`, add this right after `CreateFolderAsync` (before `AddMessageAsync`'s XML doc comment):

```csharp
    /// <summary>
    /// Hides a folder — and every folder in its subtree — from Folders, without touching
    /// Folders.dbx or any message .dbx. The folder's own backing file (and every
    /// descendant's) stays on disk untouched; only Folders/_backingFileNames and
    /// HiddenFolderCache change.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when: <paramref name="folderId"/> does not match any existing (visible)
    /// folder; or the folder, or any folder in its subtree, is a special folder
    /// (SpecialFolderKind != None) — special folders can never be deleted, including as a
    /// side effect of deleting an ancestor. Nothing is marked/removed if this is thrown —
    /// the whole subtree is validated before any of it is hidden.
    /// </exception>
    /// <remarks>
    /// Not idempotent: calling this twice for the same id throws on the second call,
    /// because the first call already removed it from _folders (see Global Constraints in
    /// the implementation plan for why this differs from DeleteMessageAsync).
    /// Not thread-safe, same as every other mutating DbxStore member.
    /// </remarks>
    public Task DeleteFolderAsync(int folderId, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_folders.Any(f => f.Id == folderId))
                throw new InvalidOperationException($"Pasta {folderId} não encontrada.");

            var subtreeIds = CollectSubtreeIds(folderId);

            if (subtreeIds.Any(id => _folders.Single(f => f.Id == id).SpecialFolderKind != SpecialFolderKind.None))
                throw new InvalidOperationException("Pastas especiais não podem ser apagadas.");

            foreach (int id in subtreeIds)
            {
                _hiddenFolderCache.MarkHidden(_profileDirectory, id);
                _folders.RemoveAll(f => f.Id == id);
                _backingFileNames.Remove(id);

                if (_openFolders.Remove(id, out var stale))
                    stale.Dispose();
                _messagesByFolder.Remove(id);
                _hiddenOffsetsByFolder.Remove(id);
                _deletedOffsetsByFolder.Remove(id);
                _scavengeStatusByFolder.Remove(id);
                _recoveredItemsByFolder.Remove(id);
                _recoveredByFolder.Remove(id);
            }
        }, ct);
    }

    // folderId plus every folder transitively reachable from it via ParentId. A HashSet
    // doubling as the visited-set makes this safe even against a pre-existing cyclic/dangling
    // ParentId chain (the same kind FolderTreeViewModel.Build already has to tolerate on the
    // UI side) -- Add returning false stops the walk from ever revisiting an id twice.
    private HashSet<int> CollectSubtreeIds(int rootId)
    {
        var result = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(rootId);

        while (stack.Count > 0)
        {
            int id = stack.Pop();
            if (!result.Add(id))
                continue;

            foreach (var child in _folders.Where(f => f.ParentId == id))
                stack.Push(child.Id);
        }

        return result;
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreDeleteFolderTests"`
Expected: PASS — all 8 tests.

Run: `dotnet test`
Expected: PASS — no regression in any project (no other project references `DbxStore.Open`'s new optional parameter or `DeleteFolderAsync`, so nothing else should be affected).

- [ ] **Step 7: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs
git commit -m "Add DbxStore.DeleteFolderAsync: hides a folder subtree via HiddenFolderCache"
```

---

### Task 3: `DbxStore.MoveFolderAsync`

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Test: `tests/LEC.MailStore.Tests/DbxStoreMoveFolderTests.cs`

**Interfaces:**
- Consumes: `DbxFolderFileWriter.Rebuild`/`DbxFolderWriteEntry` (existing, shipped with `CreateFolderAsync`).
- Produces: `DbxStore.MoveFolderAsync(int folderId, int? newParentId, CancellationToken ct = default) : Task`. Consumed by the UI plan's Task 1 (promotes this method onto `IMailStore`).

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.MailStore.Tests/DbxStoreMoveFolderTests.cs`:

```csharp
// tests/LEC.MailStore.Tests/DbxStoreMoveFolderTests.cs
using System.Linq;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using LEC.MailStore;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreMoveFolderTests
{
    // Builds a profile with:
    //   1  Caixa de entrada   (special: Inbox, root)            Inbox.dbx
    //   2  Notas              (root)                             Notas.dbx
    //   3  Notas 2026         (child of 2)                       Notas2026.dbx
    //   4  Notas 2026 Q1      (child of 3)                       NotasQ1.dbx
    //   5  Trabalho           (root)                             Trabalho.dbx
    //   6  Notas 2026         (child of 5 -- same name as 3, different parent) Trab2026.dbx
    private static string BuildProfile()
    {
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);

        int n1 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });
        int n2 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 2 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Notas.dbx\0"u8.ToArray() },
        });
        int n3 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 3 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(2) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas 2026\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Notas2026.dbx\0"u8.ToArray() },
        });
        int n4 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 4 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(3) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas 2026 Q1\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "NotasQ1.dbx\0"u8.ToArray() },
        });
        int n5 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 5 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Trabalho\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Trabalho.dbx\0"u8.ToArray() },
        });
        int n6 = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 6 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(5) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Notas 2026\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Trab2026.dbx\0"u8.ToArray() },
        });

        int[] nodes = { n1, n2, n3, n4, n5, n6 };
        int root = folders.WriteIndexNode(nextTable: 0, ptrCount: (byte)nodes.Length, indexCount: 0,
            entries: nodes.Select(n => (indexPtr: n, entryNextTable: 0, entryIndexCount: 0)).ToArray());
        folders.SetInt32At(DbxFormat.HeaderItemCountOffset, nodes.Length);
        folders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        foreach (string fileName in new[] { "Inbox.dbx", "Notas.dbx", "Notas2026.dbx", "NotasQ1.dbx", "Trabalho.dbx", "Trab2026.dbx" })
        {
            var empty = new TestDbxBuilder();
            empty.WriteHeader(DbxFileType.Email);
            empty.SetInt32At(DbxFormat.HeaderItemCountOffset, 0);
            File.WriteAllBytes(Path.Combine(dir, fileName), empty.ToArray());
        }
        return dir;
    }

    [Fact]
    public async Task MoveFolderAsyncChangesParentIdAndBringsTheSubtreeAlong()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.MoveFolderAsync(3, newParentId: 5); // Notas 2026 (with child 4) -> under Trabalho

            var moved = store.Folders.Single(f => f.Id == 3);
            Assert.Equal(5, moved.ParentId);

            // The child never had its own entry touched -- it still points at folder 3,
            // which is what makes it "come along" without any extra work.
            var child = store.Folders.Single(f => f.Id == 4);
            Assert.Equal(3, child.ParentId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncToRootSetsParentIdToNull()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.MoveFolderAsync(4, newParentId: null);

            Assert.Null(store.Folders.Single(f => f.Id == 4).ParentId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncIntoItselfThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveFolderAsync(3, newParentId: 3));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncIntoOwnDescendantThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveFolderAsync(2, newParentId: 4)); // 4 is a grandchild of 2
            Assert.Contains(store.Folders, f => f.Id == 2 && f.ParentId == null); // unchanged
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncOnASpecialFolderThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveFolderAsync(1, newParentId: 2));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncToANonexistentParentThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveFolderAsync(3, newParentId: 999));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncCollidingWithASiblingNameAtTheDestinationThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            // Folder 5 (Trabalho) already has a child (6) named "Notas 2026" -- moving folder
            // 3 (also named "Notas 2026") there must collide, same rule as CreateFolderAsync.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveFolderAsync(3, newParentId: 5));
            Assert.Contains("já existe", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncUpdatesTheBackupFile()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.MoveFolderAsync(4, newParentId: null);

            Assert.True(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncPersistsAcrossReopen()
    {
        string dir = BuildProfile();
        try
        {
            using (var store = DbxStore.Open(dir))
                await store.MoveFolderAsync(3, newParentId: null);

            using var reopened = DbxStore.Open(dir);
            Assert.Null(reopened.Folders.Single(f => f.Id == 3).ParentId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MoveFolderAsyncIntoAHiddenFolderThrows()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);
            await store.DeleteFolderAsync(5); // hides 5 (and its child 6)

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.MoveFolderAsync(3, newParentId: 5));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreMoveFolderTests"`
Expected: FAIL to compile — `DbxStore.MoveFolderAsync` doesn't exist yet.

- [ ] **Step 3: Implement `MoveFolderAsync`**

In `src/LEC.MailStore/DbxStore.cs`, add this right after `CollectSubtreeIds` (i.e. right after `DeleteFolderAsync`'s helper, before `AddMessageAsync`):

```csharp
    /// <summary>
    /// Moves a folder to a new parent within this store — a from-scratch rewrite of
    /// Folders.dbx (see DbxFolderFileWriter.Rebuild) that changes only the moved folder's
    /// ParentId entry. Every descendant folder's own entry is untouched: since the tree is
    /// defined entirely by ParentId, the whole subtree "comes along" without needing any
    /// other entry to change.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when: <paramref name="folderId"/> does not match any existing folder; the
    /// folder is special (SpecialFolderKind != None); <paramref name="newParentId"/> is
    /// non-null and does not match any existing folder; <paramref name="newParentId"/> is
    /// the folder itself or one of its own descendants (would create a cycle); a sibling
    /// folder under <paramref name="newParentId"/> already has this folder's name
    /// (case-insensitive, same rule as CreateFolderAsync); or Folders.dbx on disk is
    /// damaged (Health != StoreHealth.Ok).
    /// </exception>
    public Task MoveFolderAsync(int folderId, int? newParentId, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var folder = _folders.FirstOrDefault(f => f.Id == folderId)
                ?? throw new InvalidOperationException($"Pasta {folderId} não encontrada.");

            if (folder.SpecialFolderKind != SpecialFolderKind.None)
                throw new InvalidOperationException("Pastas especiais não podem ser movidas.");

            if (newParentId is int pid && !_folders.Any(f => f.Id == pid))
                throw new InvalidOperationException($"Pasta-mãe {pid} não encontrada.");

            // Cycle guard: walk up from newParentId via ParentId; folderId must never appear.
            // The visited-set also protects against looping forever if the tree already has
            // an unrelated pre-existing cycle (same tolerance CollectSubtreeIds needs).
            var visited = new HashSet<int>();
            int? cursor = newParentId;
            while (cursor is int cursorId)
            {
                if (cursorId == folderId)
                    throw new InvalidOperationException(
                        "Não é possível mover uma pasta para dentro dela mesma ou de uma pasta descendente.");
                if (!visited.Add(cursorId))
                    break;
                cursor = _folders.FirstOrDefault(f => f.Id == cursorId)?.ParentId;
            }

            if (_folders.Any(f => f.Id != folderId && f.ParentId == newParentId
                    && string.Equals(f.Name, folder.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Já existe uma pasta chamada \"{folder.Name}\" no mesmo nível.");

            string foldersDbxPath = Path.Combine(_profileDirectory, "Folders.dbx");

            List<DbxFolderWriteEntry> entries;
            using (var current = DbxFolderFile.Open(foldersDbxPath))
            {
                if (current.Health != StoreHealth.Ok)
                    throw new InvalidOperationException(
                        "Folders.dbx está danificado; mover uma pasta reescreveria o arquivo e descartaria as pastas ilegíveis.");

                entries = current.Folders
                    .Select(f => new DbxFolderWriteEntry(
                        f.FolderId,
                        f.FolderId == folderId ? (newParentId ?? -1) : f.ParentId,
                        f.Name ?? "", f.FileName, f.Flags, f.MessageCount, f.SpecialFolderType))
                    .ToList();
            }

            string pristinePath = foldersDbxPath + ".original";
            if (!File.Exists(pristinePath))
                File.Copy(foldersDbxPath, pristinePath);
            File.Copy(foldersDbxPath, foldersDbxPath + ".bak", overwrite: true);

            DbxFolderFileWriter.Rebuild(foldersDbxPath, entries);

            int index = _folders.FindIndex(f => f.Id == folderId);
            _folders[index] = new StoreFolder
            {
                Id = folder.Id,
                ParentId = newParentId,
                Name = folder.Name,
                SpecialFolderKind = folder.SpecialFolderKind,
                MessageCount = folder.MessageCount,
                IsLoadable = folder.IsLoadable,
                HasBackingFile = folder.HasBackingFile,
            };
        }, ct);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreMoveFolderTests"`
Expected: PASS — all 9 tests.

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreMoveFolderTests.cs
git commit -m "Add DbxStore.MoveFolderAsync: rewrites Folders.dbx to retarget one folder's ParentId"
```

---

## Notes for the next slice (UI)

- **Task ordering:** Task 1 has no prerequisite. Task 2 depends on Task 1 (`DeleteFolderAsync` calls `_hiddenFolderCache.MarkHidden`). Task 3 has no hard dependency on Task 2's new code paths, but both tasks touch the same file (`DbxStore.cs`) and Task 3's own test (`MoveFolderAsyncIntoAHiddenFolderThrows`) calls `DeleteFolderAsync`, so Task 3 must run after Task 2.
- Neither `DeleteFolderAsync` nor `MoveFolderAsync` is on `IMailStore` yet — the UI plan's first task promotes both onto the interface and updates `FakeMailStore` (`tests/LEC.App.Tests/FakeMailStore.cs`) in the same step, exactly as `docs/superpowers/plans/2026-08-03-criar-pastas-ui.md` Task 1 did for `CreateFolderAsync`.
