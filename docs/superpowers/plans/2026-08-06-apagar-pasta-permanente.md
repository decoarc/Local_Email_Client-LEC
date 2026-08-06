# Apagar pasta — apagar de verdade (permanente) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Change `DbxStore.DeleteFolderAsync` from "hide the folder" to "permanently delete it" — remove its entry (and its subtree's) from `Folders.dbx`, and delete the backing `.dbx` message file(s) from disk — and update every caller/UI surface that assumed the old hide-only behavior.

**Architecture:** `DbxStore.DeleteFolderAsync` keeps its existing validation (folder exists, no special folder in the subtree) but replaces "mark hidden in `HiddenFolderCache`" with a from-scratch rewrite of `Folders.dbx` (same atomic backup+rebuild pattern already used by `MoveFolderAsync`/`CreateFolderAsync`) followed by best-effort physical deletion of each subtree folder's backing file and its cache state. `HiddenFolderCache` is no longer written to (folders hidden by old app versions stay hidden forever, unmigrated) but is still read at `DbxStore.Open` for backward compatibility. `ProfileViewModel`/`MainWindow.xaml.cs` are updated so the UI stays correct even in the new "deleted for real, but a file couldn't be removed" partial-failure case.

**Tech Stack:** C# / .NET 9, xUnit, `Microsoft.Data.Sqlite` (existing caches), WPF (UI layer, untested by convention in this repo).

## Global Constraints

- Portuguese-language exception messages and UI text, matching every existing string in this codebase.
- `InvalidOperationException` is the established error type for all `DbxStore` validation failures — keep using it, don't introduce a new exception type.
- Every mutating `DbxStore` method is documented as **not thread-safe** — new/changed XML doc comments must preserve that note where it already exists.
- No lixeira/undo — apagar pasta has never had one and this slice doesn't add one.
- Follow the existing `.bak`/`.original` backup convention exactly (`.original` created once, never overwritten; `.bak` overwritten every time) for any file this plan touches.

---

### Task 1: `DbxStore.DeleteFolderAsync` — core permanent delete (Folders.dbx rewrite + physical file removal)

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs:169-216` (the `DeleteFolderAsync` method and its XML doc)
- Modify: `src/LEC.MailStore/IMailStore.cs:80-86` (the interface's XML doc for the same method)
- Modify: `tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs` (rewrite every existing test for the new semantics, add a damaged-`Folders.dbx` test)

**Interfaces:**
- Consumes: `DbxFolderFile.Open(path)` / `.Health` / `.Folders` (each entry has `FolderId`, `ParentId`, `Name`, `FileName`, `Flags`, `MessageCount`, `SpecialFolderType`), `DbxFolderWriteEntry(int folderId, int parentId, string name, string? fileName, ...)`, `DbxFolderFileWriter.Rebuild(string path, IReadOnlyList<DbxFolderWriteEntry> folders)` — all already used identically by `MoveFolderAsync` in the same file.
- Produces: `DbxStore.DeleteFolderAsync(int folderId, CancellationToken ct = default)` now permanently deletes rather than hides. Later tasks (2 and 3) add cache-purging and partial-failure handling on top of the body written here — don't remove the `fileNames` list this task introduces, task 2 iterates it.

- [ ] **Step 1: Rewrite `tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs` for the new "apaga de verdade" semantics**

Replace the file's contents from the first `[Fact]` onward (keep `BuildProfile()` unchanged) with:

```csharp
    [Fact]
    public async Task DeleteFolderAsyncOnALeafFolderDeletesOnlyThatFolder()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.DeleteFolderAsync(4);

            Assert.DoesNotContain(store.Folders, f => f.Id == 4);
            Assert.Contains(store.Folders, f => f.Id == 3);
            Assert.Contains(store.Folders, f => f.Id == 2);
            Assert.False(File.Exists(Path.Combine(dir, "NotasQ1.dbx")));
            Assert.True(File.Exists(Path.Combine(dir, "Notas2026.dbx")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncDeletesTheWholeSubtreeAtOnce()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            await store.DeleteFolderAsync(2); // Notas -> also deletes 3 (child) and 4 (grandchild)

            Assert.DoesNotContain(store.Folders, f => f.Id == 2);
            Assert.DoesNotContain(store.Folders, f => f.Id == 3);
            Assert.DoesNotContain(store.Folders, f => f.Id == 4);
            Assert.Contains(store.Folders, f => f.Id == 1); // Inbox untouched

            Assert.False(File.Exists(Path.Combine(dir, "Notas.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Notas2026.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "NotasQ1.dbx")));
            Assert.True(File.Exists(Path.Combine(dir, "Inbox.dbx")));
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
            Assert.True(File.Exists(Path.Combine(dir, "Inbox.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncWhoseSubtreeContainsASpecialFolderThrowsAndDeletesNothing()
    {
        string dir = BuildProfile();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(10)); // Raiz -> child 11 is special
            Assert.Contains("especia", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(store.Folders, f => f.Id == 10);
            Assert.Contains(store.Folders, f => f.Id == 11);
            Assert.True(File.Exists(Path.Combine(dir, "EspecialInbox.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
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
        try
        {
            using (var store = DbxStore.Open(dir))
                await store.DeleteFolderAsync(2);

            using var reopened = DbxStore.Open(dir);
            Assert.DoesNotContain(reopened.Folders, f => f.Id == 2);
            Assert.DoesNotContain(reopened.Folders, f => f.Id == 3);
            Assert.DoesNotContain(reopened.Folders, f => f.Id == 4);
            Assert.Contains(reopened.Folders, f => f.Id == 1);
            Assert.False(File.Exists(Path.Combine(dir, "Notas.dbx")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteFolderAsyncRewritesFoldersDbxAndCreatesBackups()
    {
        string dir = BuildProfile();
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir);

            await store.DeleteFolderAsync(4);

            Assert.NotEqual(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Folders.dbx")));
            Assert.True(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
            Assert.True(File.Exists(Path.Combine(dir, "Folders.dbx.original")));
            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Folders.dbx.original")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

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
        var empty = new TestDbxBuilder();
        empty.WriteHeader(DbxFileType.Email);
        empty.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 0);
        File.WriteAllBytes(Path.Combine(dir, "Notas.dbx"), empty.ToArray());
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(1));
            Assert.Contains("danificado", ex.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Folders.dbx")));
            Assert.True(File.Exists(Path.Combine(dir, "Notas.dbx")));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.bak")));
            Assert.False(File.Exists(Path.Combine(dir, "Folders.dbx.original")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

(This drops the old `DeleteFolderAsyncNeverTouchesFoldersDbxOnDisk` test — it tested exactly the behavior being removed — and the old `hiddenCacheDbPath`-based reopen test, since persistence now comes from `Folders.dbx` itself, not `HiddenFolderCache`.)

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreDeleteFolderTests"`
Expected: Several FAIL — the old implementation still hides instead of deleting, so `File.Exists` assertions and the "damaged Folders.dbx" test (no existing guard) fail. `DeleteFolderAsyncOnALeafFolderDeletesOnlyThatFolder`/`DeleteFolderAsyncDeletesTheWholeSubtreeAtOnce`/`DeleteFolderAsyncPersistsAcrossReopen`/`DeleteFolderAsyncRewritesFoldersDbxAndCreatesBackups`/`DeleteFolderAsyncWhenFoldersDbxIsDamagedThrowsAndDeletesNothing` should all fail; the guard/nonexistent/twice tests should still pass (unchanged behavior).

- [ ] **Step 3: Replace `DeleteFolderAsync` in `src/LEC.MailStore/DbxStore.cs:169-216`**

Replace the whole method (including its XML doc) with:

```csharp
    /// <summary>
    /// Permanently deletes a folder — and every folder in its subtree — removing their
    /// entries from Folders.dbx (a from-scratch rewrite, same pattern as MoveFolderAsync)
    /// and deleting each folder's own backing .dbx file from disk.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown before any mutation when: <paramref name="folderId"/> does not match any
    /// existing (visible) folder; the folder, or any folder in its subtree, is a special
    /// folder (SpecialFolderKind != None) — special folders can never be deleted, including
    /// as a side effect of deleting an ancestor; or Folders.dbx on disk is damaged
    /// (Health != StoreHealth.Ok), since rebuilding it in that state would silently discard
    /// whatever folders this reader couldn't parse. Nothing is removed/rewritten if any of
    /// these fire.
    /// </exception>
    /// <remarks>
    /// Not idempotent: calling this twice for the same id throws on the second call,
    /// because the first call already removed it from _folders.
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

            string foldersDbxPath = Path.Combine(_profileDirectory, "Folders.dbx");

            List<DbxFolderWriteEntry> entries;
            using (var current = DbxFolderFile.Open(foldersDbxPath))
            {
                if (current.Health != StoreHealth.Ok)
                    throw new InvalidOperationException(
                        "Folders.dbx está danificado; apagar uma pasta reescreveria o arquivo e descartaria as pastas ilegíveis.");

                entries = current.Folders
                    .Where(f => !subtreeIds.Contains(f.FolderId))
                    .Select(f => new DbxFolderWriteEntry(f.FolderId, f.ParentId, f.Name ?? "", f.FileName,
                        f.Flags, f.MessageCount, f.SpecialFolderType))
                    .ToList();
            }

            string pristinePath = foldersDbxPath + ".original";
            if (!File.Exists(pristinePath))
                File.Copy(foldersDbxPath, pristinePath);
            File.Copy(foldersDbxPath, foldersDbxPath + ".bak", overwrite: true);

            DbxFolderFileWriter.Rebuild(foldersDbxPath, entries);
            // The folder is permanently gone from Folders.dbx from this point on -- every
            // step below is best-effort disk/cache cleanup around that already-final change.

            var fileNames = new List<string>();
            foreach (int id in subtreeIds)
            {
                if (_backingFileNames.TryGetValue(id, out string? fileName) && fileName is not null)
                    fileNames.Add(fileName);

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

            foreach (string fileName in fileNames)
                File.Delete(Path.Combine(_profileDirectory, fileName)); // no-op if it never existed
        }, ct);
    }
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreDeleteFolderTests"`
Expected: PASS (all facts in the rewritten file).

- [ ] **Step 5: Update `IMailStore.cs`'s XML doc for the same method**

In `src/LEC.MailStore/IMailStore.cs:80-86`, replace:

```csharp
    /// <summary>
    /// Hides a folder — and every folder in its subtree — from Folders. Never writes to
    /// Folders.dbx or any message .dbx. See DbxStore.DeleteFolderAsync for the full
    /// contract (special-folder guard, subtree collection, exceptions thrown). Unlike
    /// DeleteMessageAsync, calling this twice for the same id throws on the second call.
    /// </summary>
    Task DeleteFolderAsync(int folderId, CancellationToken ct = default);
```

with:

```csharp
    /// <summary>
    /// Permanently deletes a folder — and every folder in its subtree — from Folders.dbx
    /// and from disk. See DbxStore.DeleteFolderAsync for the full contract (special-folder
    /// guard, subtree collection, exceptions thrown). Unlike DeleteMessageAsync, calling
    /// this twice for the same id throws on the second call.
    /// </summary>
    Task DeleteFolderAsync(int folderId, CancellationToken ct = default);
```

- [ ] **Step 6: Build to confirm nothing else broke**

Run: `dotnet build`
Expected: Build succeeds (no other production code references the removed `HiddenFolderCache.MarkHidden` call inside this method — `HiddenFolderCache` itself is untouched and still compiles).

- [ ] **Step 7: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs src/LEC.MailStore/IMailStore.cs tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs
git commit -m "Make DeleteFolderAsync permanently delete folders instead of hiding them"
```

---

### Task 2: Purge Scavenge/Moved/Deleted message caches for the deleted folder's backing file

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs` (the `fileNames` loop added in Task 1)
- Modify: `tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs` (add one test, add `using System.Text;`)

**Interfaces:**
- Consumes: `_scavengeCache.ClearScan(string profilePath, string folderFile)`, `_movedMessageCache.ClearFolder(string profilePath, string folderFile)`, `_deletedMessageCache.ClearFolder(string profilePath, string folderFile)` — all three already exist and are used the same way by `AddMessageAsync` in the same file. `DbxEmailFileWriter.Rebuild(string path, IReadOnlyList<DbxMessageWriteEntry> messages)` and `DbxMessageWriteEntry(byte[] raw, string? subject, string? senderName, string? senderAddress, string? messageId, DateTime? dateUtc)` for seeding test messages (same signatures already used in `DbxStoreAddMessageTests.cs`).
- Produces: no new public API — this only changes what happens inside the `fileNames` loop from Task 1.

- [ ] **Step 1: Add the failing test**

Add `using System.Text;` to the top of `tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs` (alongside the existing `using System.Linq;`), then add this fact:

```csharp
    [Fact]
    public async Task DeleteFolderAsyncPurgesTheScavengeMovedAndDeletedMessageCachesForTheDeletedFolder()
    {
        string dir = BuildProfile();
        string scavengeDbPath = Path.Combine(Path.GetTempPath(), $"lec-scavenge-{Guid.NewGuid():N}.db");
        string movedDbPath = Path.Combine(Path.GetTempPath(), $"lec-moved-{Guid.NewGuid():N}.db");
        string deletedDbPath = Path.Combine(Path.GetTempPath(), $"lec-deleted-{Guid.NewGuid():N}.db");
        try
        {
            DbxEmailFileWriter.Rebuild(Path.Combine(dir, "Notas.dbx"), new[]
            {
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("um"), "Um", null, null, null, null),
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("dois"), "Dois", null, null, null, null),
            });

            using (var store = DbxStore.Open(dir,
                scavengeCacheDatabasePath: scavengeDbPath,
                movedMessageCacheDatabasePath: movedDbPath,
                deletedMessageCacheDatabasePath: deletedDbPath))
            {
                var folder = store.Folders.Single(f => f.Id == 2); // Notas
                var headers = new List<MessageHeader>();
                await foreach (var h in store.EnumerateHeadersAsync(folder))
                    headers.Add(h);

                await store.HideMovedMessageAsync(headers.Single(h => h.Subject == "Um").Id);
                await store.DeleteMessageAsync(headers.Single(h => h.Subject == "Dois").Id);
                await store.ScavengeAsync(folder);

                await store.DeleteFolderAsync(2);
            }

            using var scavengeCache = new ScavengeCache(scavengeDbPath);
            Assert.Null(scavengeCache.GetScan(dir, "Notas.dbx"));
            using var movedCache = new MovedMessageCache(movedDbPath);
            Assert.Empty(movedCache.GetMovedOffsets(dir, "Notas.dbx"));
            using var deletedCache = new DeletedMessageCache(deletedDbPath);
            Assert.Empty(deletedCache.GetDeletedOffsets(dir, "Notas.dbx"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
            File.Delete(scavengeDbPath);
            File.Delete(movedDbPath);
            File.Delete(deletedDbPath);
        }
    }
```

- [ ] **Step 2: Run the test to see it fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DeleteFolderAsyncPurgesTheScavengeMovedAndDeletedMessageCachesForTheDeletedFolder"`
Expected: FAIL — `scavengeCache.GetScan(dir, "Notas.dbx")` is not null, and/or the moved/deleted offset sets aren't empty, because nothing purges them yet.

- [ ] **Step 3: Add the purge calls in `DbxStore.cs`**

In the `foreach (string fileName in fileNames)` loop added by Task 1, change:

```csharp
            foreach (string fileName in fileNames)
                File.Delete(Path.Combine(_profileDirectory, fileName)); // no-op if it never existed
```

to:

```csharp
            foreach (string fileName in fileNames)
            {
                // These caches key on (profilePath, fileName), not FolderId -- without this,
                // a future folder that reuses the same sanitized name (same fileName) would
                // silently inherit this deleted folder's hidden/scavenged offsets.
                _scavengeCache.ClearScan(_profileDirectory, fileName);
                _movedMessageCache.ClearFolder(_profileDirectory, fileName);
                _deletedMessageCache.ClearFolder(_profileDirectory, fileName);

                File.Delete(Path.Combine(_profileDirectory, fileName)); // no-op if it never existed
            }
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreDeleteFolderTests"`
Expected: PASS (all facts, including the new one).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs
git commit -m "Purge scavenge/moved/deleted message caches when a folder is permanently deleted"
```

---

### Task 3: Best-effort deletion of locked backing files, surfaced as one aggregated error

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs` (the `fileNames` loop from Task 2)
- Modify: `tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs` (add one test)

**Interfaces:**
- Consumes: nothing new.
- Produces: `DeleteFolderAsync` now also throws `InvalidOperationException` *after* a successful `Folders.dbx` rewrite when one or more backing files couldn't be deleted — callers (Task 4/5) must treat that as "the folder is already gone" rather than "nothing happened".

- [ ] **Step 1: Add the failing test**

```csharp
    [Fact]
    public async Task DeleteFolderAsyncWhenABackingFileIsLockedStillDeletesTheFolderButThrowsListingIt()
    {
        string dir = BuildProfile();
        var lockingStream = new FileStream(Path.Combine(dir, "NotasQ1.dbx"), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFolderAsync(4));
            Assert.Contains("NotasQ1.dbx", ex.Message);

            // Folders.dbx was already rewritten without folder 4 -- the exception above is
            // only about the leftover physical file, not about the deletion itself.
            Assert.DoesNotContain(store.Folders, f => f.Id == 4);
        }
        finally
        {
            lockingStream.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }
```

- [ ] **Step 2: Run the test to see it fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DeleteFolderAsyncWhenABackingFileIsLockedStillDeletesTheFolderButThrowsListingIt"`
Expected: FAIL — `File.Delete` throws `IOException` uncaught (a different exception type/message than expected, or the test hangs on an unhandled exception from inside `Task.Run`), since nothing currently catches a locked-file failure.

- [ ] **Step 3: Make file deletion best-effort and aggregate failures**

Replace the loop from Task 2:

```csharp
            foreach (string fileName in fileNames)
            {
                // These caches key on (profilePath, fileName), not FolderId -- without this,
                // a future folder that reuses the same sanitized name (same fileName) would
                // silently inherit this deleted folder's hidden/scavenged offsets.
                _scavengeCache.ClearScan(_profileDirectory, fileName);
                _movedMessageCache.ClearFolder(_profileDirectory, fileName);
                _deletedMessageCache.ClearFolder(_profileDirectory, fileName);

                File.Delete(Path.Combine(_profileDirectory, fileName)); // no-op if it never existed
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

            if (failures.Count > 0)
                throw new InvalidOperationException(
                    $"Pasta apagada, mas não foi possível remover do disco: {string.Join(", ", failures)}.");
```

Also update the method's `<exception>` doc (added in Task 1) to add a second paragraph:

```csharp
    /// <exception cref="InvalidOperationException">
    /// Thrown before any mutation when: <paramref name="folderId"/> does not match any
    /// existing (visible) folder; the folder, or any folder in its subtree, is a special
    /// folder (SpecialFolderKind != None) — special folders can never be deleted, including
    /// as a side effect of deleting an ancestor; or Folders.dbx on disk is damaged
    /// (Health != StoreHealth.Ok), since rebuilding it in that state would silently discard
    /// whatever folders this reader couldn't parse. Nothing is removed/rewritten if any of
    /// these fire.
    /// <para>
    /// Thrown <em>after</em> the folder has already been permanently removed (Folders.dbx
    /// already rewritten without it, in-memory state already updated) when one or more of
    /// its subtree's backing files could not be deleted from disk (e.g. locked by another
    /// process) — lists which files failed. <see cref="Folders"/> no longer contains the
    /// folder regardless of this exception.
    /// </para>
    /// </exception>
```

- [ ] **Step 4: Run the tests to see them pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreDeleteFolderTests"`
Expected: PASS (all facts).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreDeleteFolderTests.cs
git commit -m "Make DeleteFolderAsync tolerate a locked backing file instead of crashing mid-cleanup"
```

---

### Task 4: `ProfileViewModel.DeleteFolderAsync` — rebuild the tree even on the new partial-failure exception

**Files:**
- Modify: `src/LEC.App/ViewModels/ProfileViewModel.cs:32-36`
- Modify: `tests/LEC.App.Tests/FakeMailStore.cs:127-138` (add a way to simulate "deleted, then throws")
- Modify: `tests/LEC.App.Tests/ProfileViewModelTests.cs` (add one test)

**Interfaces:**
- Consumes: nothing new.
- Produces: `FakeMailStore.ExceptionToThrowAfterDeletingFolder` (new settable property) — later UI-layer tests (if any are added) can use it too.

**Why this task exists:** `DbxStore.DeleteFolderAsync` can now throw *after* it already deleted the folder for real (Task 3's partial-failure case). `ProfileViewModel.DeleteFolderAsync` currently only calls `FolderTree.Build(Store.Folders)` on the success path — an exception skips it, leaving the UI's tree showing a folder that the store has already deleted.

- [ ] **Step 1: Add the failing test**

In `tests/LEC.App.Tests/ProfileViewModelTests.cs`, add (near the existing `DeleteFolderAsyncPropagatesStoreExceptions`):

```csharp
    [Fact]
    public async Task DeleteFolderAsyncRebuildsTheTreeEvenWhenTheStoreThrowsAfterAlreadyDeleting()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var child = new StoreFolder { Id = 2, ParentId = 1, Name = "Filha", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { root, child })
        {
            ExceptionToThrowAfterDeletingFolder = new InvalidOperationException(
                "Pasta apagada, mas não foi possível remover do disco: Filha.dbx (em uso por outro processo)."),
        };
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => profile.DeleteFolderAsync(child.Id));
        Assert.Contains("Filha.dbx", ex.Message);

        // The store already removed the folder before throwing -- FolderTree must reflect
        // that even though the call surfaced as a failure to the caller.
        var rootNode = Assert.Single(profile.FolderTree.RootNodes);
        Assert.Empty(rootNode.Children);
    }
```

- [ ] **Step 2: Run the test to see it fail**

Run: `dotnet test tests/LEC.App.Tests --filter "DeleteFolderAsyncRebuildsTheTreeEvenWhenTheStoreThrowsAfterAlreadyDeleting"`
Expected: FAIL — compile error first (`ExceptionToThrowAfterDeletingFolder` doesn't exist yet on `FakeMailStore`).

- [ ] **Step 3: Add the new capability to `FakeMailStore`**

In `tests/LEC.App.Tests/FakeMailStore.cs:127-138`, replace:

```csharp
    public Exception? ExceptionToThrowOnDeleteFolder { get; set; }
    public List<int> DeletedFolderIds { get; } = new();

    public Task DeleteFolderAsync(int folderId, CancellationToken ct = default)
    {
        if (ExceptionToThrowOnDeleteFolder is { } ex)
            throw ex;

        DeletedFolderIds.Add(folderId);
        _folders.RemoveAll(f => f.Id == folderId);
        return Task.CompletedTask;
    }
```

with:

```csharp
    public Exception? ExceptionToThrowOnDeleteFolder { get; set; }
    // Distinct from ExceptionToThrowOnDeleteFolder above: this one simulates DbxStore's
    // partial-failure case (Task 3 of the "apagar pasta permanente" plan) -- the folder is
    // removed from _folders FIRST, then the exception is thrown, so callers that only
    // handle "nothing happened" exceptions can be tested against "already happened, but
    // still failed" too.
    public Exception? ExceptionToThrowAfterDeletingFolder { get; set; }
    public List<int> DeletedFolderIds { get; } = new();

    public Task DeleteFolderAsync(int folderId, CancellationToken ct = default)
    {
        if (ExceptionToThrowOnDeleteFolder is { } ex)
            throw ex;

        DeletedFolderIds.Add(folderId);
        _folders.RemoveAll(f => f.Id == folderId);

        if (ExceptionToThrowAfterDeletingFolder is { } afterEx)
            throw afterEx;

        return Task.CompletedTask;
    }
```

- [ ] **Step 4: Run the test to see the real failure**

Run: `dotnet test tests/LEC.App.Tests --filter "DeleteFolderAsyncRebuildsTheTreeEvenWhenTheStoreThrowsAfterAlreadyDeleting"`
Expected: FAIL — now compiles, but `profile.FolderTree.RootNodes.Single().Children` is not empty, because `ProfileViewModel.DeleteFolderAsync` never reached the `FolderTree.Build` call.

- [ ] **Step 5: Fix `ProfileViewModel.DeleteFolderAsync`**

In `src/LEC.App/ViewModels/ProfileViewModel.cs:32-36`, replace:

```csharp
    public async Task DeleteFolderAsync(int folderId)
    {
        await Store.DeleteFolderAsync(folderId);
        FolderTree.Build(Store.Folders);
    }
```

with:

```csharp
    public async Task DeleteFolderAsync(int folderId)
    {
        // DbxStore.DeleteFolderAsync can throw AFTER already deleting the folder for real
        // (a backing file that couldn't be removed from disk) -- the tree must reflect that
        // even when the call ends in an exception, not only on the success path.
        try
        {
            await Store.DeleteFolderAsync(folderId);
        }
        finally
        {
            FolderTree.Build(Store.Folders);
        }
    }
```

- [ ] **Step 6: Run the tests to see them pass**

Run: `dotnet test tests/LEC.App.Tests --filter "DeleteFolderAsync"`
Expected: PASS (`DeleteFolderAsyncRemovesTheFolderFromTheTree`, `DeleteFolderAsyncPropagatesStoreExceptions`, and the new `DeleteFolderAsyncRebuildsTheTreeEvenWhenTheStoreThrowsAfterAlreadyDeleting`).

- [ ] **Step 7: Commit**

```bash
git add src/LEC.App/ViewModels/ProfileViewModel.cs tests/LEC.App.Tests/FakeMailStore.cs tests/LEC.App.Tests/ProfileViewModelTests.cs
git commit -m "Rebuild the folder tree even when DeleteFolderAsync fails after already deleting"
```

---

### Task 5: UI — permanent-delete confirmation text, correct selection-clearing, and doc updates

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml.cs:345-385` (`ConfirmAndDeleteFolderAsync`)
- Modify: `src/LEC.App/ViewModels/MainWindowViewModel.cs` (XML doc only, around the existing `MoveFolderAsync` cross-profile doc comment)

**Interfaces:**
- Consumes: `profile.Store.Folders` (already public, `IReadOnlyList<StoreFolder>`, already used elsewhere in this file) to check whether a folder still exists after the call, instead of relying on which branch (try vs. catch) executed.
- Produces: no new public API — code-behind and doc-comment changes only.

**Why the selection-clearing logic needs to change, not just the dialog text:** today, `ConfirmAndDeleteFolderAsync`'s selection-clearing block only runs inside the `try`, right after a successful `await`. With Task 3's partial-failure case, `DeleteFolderAsync` can now throw even though the folder is already gone — jumping straight to the `catch` and skipping that cleanup, leaving the message list/reader pointed at a folder that no longer exists. Checking `profile.Store.Folders` directly, after the try/catch, makes this correct in both outcomes.

This file's code-behind has no existing unit test coverage (WPF code-behind isn't tested elsewhere in this repo — see `tests/LEC.App.Tests/`, which only covers ViewModels), so this task is verified by build + the manual GUI checklist in Task 6, not an automated test.

- [ ] **Step 1: Update `ConfirmAndDeleteFolderAsync` in `src/LEC.App/MainWindow.xaml.cs:345-385`**

Replace the whole method with:

```csharp
    private async Task ConfirmAndDeleteFolderAsync(ProfileViewModel profile, FolderNodeViewModel node, TreeView treeView)
    {
        var result = MessageBox.Show(this,
            $"Apagar permanentemente a pasta \"{node.DisplayName}\" e tudo dentro dela (subpastas e mensagens)? Essa ação não pode ser desfeita.",
            "LEC — Apagar pasta", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        // The old tree (still in memory, pre-rebuild) is what tells us whether the current
        // selection lived inside the subtree about to disappear -- profile.DeleteFolderAsync
        // below replaces every node in profile.FolderTree with brand-new instances, so this
        // check has to happen against the node we already have, not anything read afterward.
        bool selectionIsInsideDeletedSubtree = ReferenceEquals(_selectedProfile, profile)
            && _selectedNode is { } current
            && (ReferenceEquals(current, node) || IsDescendantOf(node, current));

        treeView.IsEnabled = false;
        try
        {
            await profile.DeleteFolderAsync(node.Folder.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LEC — Não foi possível apagar a pasta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            treeView.IsEnabled = true;
        }

        // DeleteFolderAsync can now throw AFTER already deleting the folder for real (a
        // backing file that couldn't be removed from disk) -- checking the store directly,
        // instead of only reacting to the try block's success, keeps this correct either way.
        bool folderWasActuallyDeleted = !profile.Store.Folders.Any(f => f.Id == node.Folder.Id);
        if (selectionIsInsideDeletedSubtree && folderWasActuallyDeleted)
        {
            _selectedNode = null;
            _selectedTreeView = null;
            ScavengeButton.Visibility = Visibility.Collapsed;
            RecoverableBar.Visibility = Visibility.Collapsed;
            NavigateToRenderedMessage(string.Empty);
            ImagesBlockedBar.Visibility = Visibility.Collapsed;
            _viewModel.ClearCurrentFolder();
        }
    }
```

- [ ] **Step 2: Update the cross-profile move XML doc in `MainWindowViewModel.cs`**

Find the doc comment above `MoveFolderAsync` (currently reads "...then hides it in sourceProfile via the already-shipped DeleteFolderAsync — never touches sourceProfile's Folders.dbx or any message .dbx..."). Replace:

```csharp
    /// <summary>
    /// Moves a folder — same-profile is the existing cheap ParentId retarget
    /// (ProfileViewModel.MoveFolderAsync); cross-profile copies the whole subtree (folders +
    /// non-recovered messages) into destinationProfile, then hides it in sourceProfile via
    /// the already-shipped DeleteFolderAsync — never touches sourceProfile's Folders.dbx or
    /// any message .dbx. Nothing is hidden in the source unless the ENTIRE subtree copied
    /// successfully first.
    /// </summary>
```

with:

```csharp
    /// <summary>
    /// Moves a folder — same-profile is the existing cheap ParentId retarget
    /// (ProfileViewModel.MoveFolderAsync); cross-profile copies the whole subtree (folders +
    /// non-recovered messages) into destinationProfile, then permanently deletes it from
    /// sourceProfile via the already-shipped DeleteFolderAsync — sourceProfile's Folders.dbx
    /// is rewritten and its backing .dbx files are removed from disk. Nothing is deleted
    /// from the source unless the ENTIRE subtree copied successfully first.
    /// </summary>
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/LEC.App/MainWindow.xaml.cs src/LEC.App/ViewModels/MainWindowViewModel.cs
git commit -m "Warn that apagar pasta is now permanent and fix selection-clearing on partial failure"
```

---

### Task 6: Full verification

**Files:** none (verification only).

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test`
Expected: PASS, 0 failures, including every `DbxStoreDeleteFolderTests`, `ProfileViewModelTests`, and `MainWindowViewModelTests` fact (in particular `MoveFolderAsyncAcrossProfilesCopiesTheWholeSubtreeExcludingRecoveredItemsAndHidesTheSource`, which still passes unmodified — it only asserts against `FakeMailStore.DeletedFolderIds`, which doesn't distinguish hide from delete).

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: Build succeeds with no new warnings.

- [ ] **Step 3: Manual GUI checklist (not automated — WPF code-behind has no test coverage in this repo)**

Run the app (`dotnet run --project src/LEC.App`) against a scratch copy of a real or synthetic profile directory (never a real mailbox you care about — this is a permanent, unrecoverable delete) and confirm:
1. Right-click a folder with subfolders and messages → "Apagar pasta" → dialog now says "Apagar permanentemente... Essa ação não pode ser desfeita."
2. Confirm → folder and its subtree disappear from the tree; the `.dbx` files for all of them are gone from the profile directory on disk (check with Explorer/`dir`).
3. Delete a folder that was currently selected/open in the message list → message list and reader clear, no stale content or crash.
4. Try to delete a special folder (Caixa de entrada, etc., if the context menu allows selecting one) → still blocked with the existing "especiais" error, nothing happens.
5. Move a folder across profiles (drag between two open profiles) → confirm the source folder's `.dbx` file(s) are gone from the source profile's directory afterward, not just hidden.

**Note:** per this project's established pattern, GUI checklists tend to stack up unrun across features — if this one also goes unrun immediately after merge, flag it in the same way prior slices have (see project memory: `apagar-mover-pastas-merged`, `mover-pasta-entre-perfis-merged`, `apagar-mover-mensagens-em-lote-merged`, `ordenacao-lista-mensagens-merged`).

- [ ] **Step 4: No commit for this task** (verification only — nothing to stage).
