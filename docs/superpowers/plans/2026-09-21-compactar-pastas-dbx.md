# Compactar pastas `.dbx` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user reclaim disk space by rewriting message `.dbx` files without soft-deleted/moved message bodies, via per-folder and compact-all actions plus close prompts when work is still pending.

**Architecture:** Pending state is derived from existing `MovedMessageCache` / `DeletedMessageCache` (no new SQLite table). `DbxStore.CompactFolderAsync` rebuilds a folder’s `.dbx` with only live indexed messages (same `Rebuild` path as `AddMessageAsync`, but **without** `_backupStore.BeforeRewrite` — spec: no backup this slice), then purges those caches. UI uses a non-cancellable modal progress dialog; close-profile and close-app prompts call `CompactAllPendingAsync` for the relevant store scope.

**Tech Stack:** C# / .NET 9, WPF, `CommunityToolkit.Mvvm`, xUnit, existing `DbxEmailFileWriter.Rebuild`.

**Spec:** `docs/superpowers/specs/2026-09-21-compactar-pastas-dbx-design.md`

## Global Constraints

- Compact **message** `.dbx` files only — never delete orphan folder files / never rewrite `Folders.dbx` for this feature.
- **No** `_backupStore.BeforeRewrite` in compact (unlike `AddMessageAsync`).
- Stop `CompactAllPendingAsync` on first failure; already-compacted folders stay compacted; wrap the exception with how many succeeded.
- Pending = backing file exists **and** at least one offset in moved **or** deleted cache for that file.
- Estimated reclaimable bytes = sum of `MessageSize` on indexed messages whose offsets are in moved∪deleted (0 is allowed).
- Rename, tree badges, cancel-mid-compact, auto-compact, backup — out of scope.
- UI copy in Portuguese, matching existing MessageBox style.
- Every task’s requirements implicitly include this section.

## File structure

| File | Responsibility |
|---|---|
| `src/LEC.MailStore/CompactStatus.cs` | `CompactFolderStatus`, `CompactStatus`, `CompactProgress` records |
| `src/LEC.MailStore/IMailStore.cs` | New compact API members |
| `src/LEC.MailStore/DbxStore.cs` | Real implementations |
| `tests/LEC.MailStore.Tests/DbxStoreCompactTests.cs` | Motor tests |
| `tests/LEC.App.Tests/FakeMailStore.cs` | Test double stubs |
| `src/LEC.App/ViewModels/MainWindowViewModel.cs` | Thin orchestration + close helpers returning status text |
| `tests/LEC.App.Tests/MainWindowViewModelTests.cs` | VM tests |
| `src/LEC.App/CompactProgressDialog.xaml` (+ `.cs`) | Modal progress, no Cancel |
| `src/LEC.App/MainWindow.xaml` / `.xaml.cs` | Menus, close-profile, `Closing` |
| `src/LEC.App/CompactPrompt.cs` (static helpers) | Format `~X MB` + build MessageBox text (keeps code-behind thin; optional if inlined in MainWindow — prefer small static helper file) |

---

### Task 1: Compact status types

**Files:**
- Create: `src/LEC.MailStore/CompactStatus.cs`
- Test: none yet (types only; covered by Task 2)

**Interfaces:**
- Consumes: nothing
- Produces:
  - `CompactFolderStatus(int FolderId, string FolderName, long EstimatedReclaimableBytes)`
  - `CompactStatus(IReadOnlyList<CompactFolderStatus> PendingFolders)` with `PendingFolderCount` and `TotalEstimatedReclaimableBytes`
  - `CompactProgress(int CompletedCount, int TotalCount, string CurrentFolderName)` — `CompletedCount` is folders **finished** so far (0 before first, `TotalCount` after last); UI shows `(CompletedCount + 1)/TotalCount` while working on current, or `CompletedCount/TotalCount` after each Report — see Task 4 for exact Report timing

- [ ] **Step 1: Add the types**

```csharp
// src/LEC.MailStore/CompactStatus.cs
namespace LEC.MailStore;

public sealed record CompactFolderStatus(
    int FolderId,
    string FolderName,
    long EstimatedReclaimableBytes);

public sealed record CompactStatus(IReadOnlyList<CompactFolderStatus> PendingFolders)
{
    public int PendingFolderCount => PendingFolders.Count;

    public long TotalEstimatedReclaimableBytes
    {
        get
        {
            long total = 0;
            foreach (var f in PendingFolders)
                total += f.EstimatedReclaimableBytes;
            return total;
        }
    }
}

/// <summary>
/// Progress for CompactAllPendingAsync. Report once per folder, immediately BEFORE that
/// folder's CompactFolderAsync starts, with CompletedCount = how many folders already
/// finished successfully (so the first Report is CompletedCount=0, CurrentFolderName=first).
/// </summary>
public sealed record CompactProgress(
    int CompletedCount,
    int TotalCount,
    string CurrentFolderName);
```

- [ ] **Step 2: Commit**

```bash
rtk git add src/LEC.MailStore/CompactStatus.cs
rtk git commit -m "Add CompactStatus types for folder compaction"
```

---

### Task 2: `GetCompactStatusAsync` on `DbxStore` + interface

**Files:**
- Modify: `src/LEC.MailStore/IMailStore.cs` (append members at end of interface)
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Modify: `tests/LEC.App.Tests/FakeMailStore.cs` (stub returning empty / configurable)
- Create: `tests/LEC.MailStore.Tests/DbxStoreCompactTests.cs`

**Interfaces:**
- Consumes: `MovedMessageCache` / `DeletedMessageCache`, `EnsureMovedLoaded` / `EnsureDeletedLoaded`, `_backingFileNames`, `Folders`, `DbxEmailFile.Open` / `.Messages` / `MessageSize` / `IndexNodeOffset`
- Produces: `Task<CompactStatus> GetCompactStatusAsync(CancellationToken ct = default)` on `IMailStore` / `DbxStore`

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.MailStore.Tests/DbxStoreCompactTests.cs`. Reuse the same profile-building style as `DbxStoreAddMessageTests` / `DbxStoreTests` (two messages via `DbxEmailFileWriter.Rebuild`, then hide/delete one).

```csharp
// tests/LEC.MailStore.Tests/DbxStoreCompactTests.cs
using System.Linq;
using System.Text;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using LEC.MailStore;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreCompactTests
{
    private static string BuildProfileWithTwoMessages(string folderName = "Inbox", string fileName = "Inbox.dbx")
    {
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);
        int node = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = Encoding.UTF8.GetBytes(folderName + "\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = Encoding.UTF8.GetBytes(fileName + "\0") },
        });
        int root = folders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: node, entryNextTable: 0, entryIndexCount: 0) });
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        folders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        DbxEmailFileWriter.Rebuild(Path.Combine(dir, fileName), new[]
        {
            new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("From: a@x.com\r\nSubject: Viva\r\n\r\nA"), "Viva", null, null, null, null),
            new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("From: b@x.com\r\nSubject: Morta\r\n\r\nBBBBB"), "Morta", null, null, null, null),
        });
        return dir;
    }

    [Fact]
    public async Task GetCompactStatusAsyncIsEmptyWhenNothingIsHidden()
    {
        string dir = BuildProfileWithTwoMessages();
        try
        {
            using var store = DbxStore.Open(dir);
            var status = await store.GetCompactStatusAsync();
            Assert.Empty(status.PendingFolders);
            Assert.Equal(0, status.PendingFolderCount);
            Assert.Equal(0, status.TotalEstimatedReclaimableBytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetCompactStatusAsyncReportsDeletedAndMovedWaste()
    {
        string dir = BuildProfileWithTwoMessages();
        string movedCache = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-moved.db");
        string deletedCache = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-deleted.db");
        try
        {
            using (var store = DbxStore.Open(dir,
                movedMessageCacheDatabasePath: movedCache,
                deletedMessageCacheDatabasePath: deletedCache))
            {
                var folder = store.Folders.Single();
                var headers = new List<MessageHeader>();
                await foreach (var h in store.EnumerateHeadersAsync(folder))
                    headers.Add(h);

                await store.DeleteMessageAsync(headers.Single(h => h.Subject == "Morta").Id);

                var status = await store.GetCompactStatusAsync();
                var pending = Assert.Single(status.PendingFolders);
                Assert.Equal(folder.Id, pending.FolderId);
                Assert.Equal("Inbox", pending.FolderName);
                Assert.True(pending.EstimatedReclaimableBytes > 0);
                Assert.Equal(pending.EstimatedReclaimableBytes, status.TotalEstimatedReclaimableBytes);
            }
        }
        finally
        {
            File.Delete(movedCache);
            File.Delete(deletedCache);
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests — expect compile/fail**

```bash
rtk dotnet test tests/LEC.MailStore.Tests/LEC.MailStore.Tests.csproj --filter "FullyQualifiedName~DbxStoreCompactTests" --no-restore
```

Expected: compile error — `GetCompactStatusAsync` missing (or FAIL if stubbed empty incorrectly).

- [ ] **Step 3: Add to `IMailStore`**

```csharp
    /// <summary>
    /// Folders in this store that still have soft-deleted or moved message bytes in their
    /// backing .dbx (see MovedMessageCache / DeletedMessageCache). Empty when nothing to reclaim.
    /// </summary>
    Task<CompactStatus> GetCompactStatusAsync(CancellationToken ct = default);

    Task CompactFolderAsync(int folderId, CancellationToken ct = default);

    Task CompactAllPendingAsync(
        IProgress<CompactProgress>? progress = null,
        CancellationToken ct = default);
```

(Leave `CompactFolderAsync` / `CompactAllPendingAsync` throwing `NotImplementedException` in `DbxStore` until Tasks 3–4, **or** implement empty stubs that throw — FakeMailStore must compile. Prefer:

```csharp
public Task CompactFolderAsync(int folderId, CancellationToken ct = default)
    => throw new NotImplementedException();

public Task CompactAllPendingAsync(IProgress<CompactProgress>? progress = null, CancellationToken ct = default)
    => throw new NotImplementedException();
```

in `DbxStore` temporarily, and matching stubs in `FakeMailStore` that return completed tasks / empty status.)

- [ ] **Step 4: Implement `GetCompactStatusAsync` on `DbxStore`**

Logic (run on `Task.Run` like `AddMessageAsync` if preferred, or sync body wrapped in `Task.FromResult` — match local style; `AddMessageAsync` uses `Task.Run`):

```csharp
public Task<CompactStatus> GetCompactStatusAsync(CancellationToken ct = default)
{
    return Task.Run(() =>
    {
        var pending = new List<CompactFolderStatus>();
        foreach (var folder in _folders.OrderBy(f => f.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
                continue;

            string filePath = Path.Combine(_profileDirectory, fileName);
            if (!File.Exists(filePath))
                continue;

            EnsureMovedLoaded(folder);
            EnsureDeletedLoaded(folder);
            var wasteOffsets = new HashSet<int>(_hiddenOffsetsByFolder[folder.Id]);
            wasteOffsets.UnionWith(_deletedOffsetsByFolder[folder.Id]);
            if (wasteOffsets.Count == 0)
                continue;

            long bytes = 0;
            using (var emailFile = DbxEmailFile.Open(filePath))
            {
                foreach (var info in emailFile.Messages)
                {
                    if (wasteOffsets.Contains(info.IndexNodeOffset))
                        bytes += info.MessageSize;
                }
            }

            pending.Add(new CompactFolderStatus(folder.Id, folder.Name, bytes));
        }

        return new CompactStatus(pending);
    }, ct);
}
```

**Important:** `EnsureMovedLoaded` / `EnsureDeletedLoaded` expect a `StoreFolder` already in `_folders`. Do **not** leave an entry in `_openFolders` from this path — open with a local `using` as above, don’t call `EnsureFolderOpen` (that would cache a handle that blocks later Rebuild).

If `EnsureMovedLoaded` requires the folder to have been “opened” only via name — read existing private methods; they only need `folder.Id` + backing file name.

- [ ] **Step 5: Stub `FakeMailStore`**

```csharp
public CompactStatus CompactStatusToReturn { get; set; } = new(Array.Empty<CompactFolderStatus>());
public List<int> CompactedFolderIds { get; } = new();
public int? FolderIdToFailCompactOn { get; set; }

public Task<CompactStatus> GetCompactStatusAsync(CancellationToken ct = default)
    => Task.FromResult(CompactStatusToReturn);

public Task CompactFolderAsync(int folderId, CancellationToken ct = default)
{
    if (FolderIdToFailCompactOn == folderId)
        throw new InvalidOperationException("falha simulada de compactar pasta");
    CompactedFolderIds.Add(folderId);
    return Task.CompletedTask;
}

public Task CompactAllPendingAsync(IProgress<CompactProgress>? progress = null, CancellationToken ct = default)
{
    var pending = CompactStatusToReturn.PendingFolders;
    int completed = 0;
    foreach (var folder in pending)
    {
        progress?.Report(new CompactProgress(completed, pending.Count, folder.FolderName));
        CompactFolderAsync(folder.FolderId, ct).GetAwaiter().GetResult();
        completed++;
    }
    return Task.CompletedTask;
}
```

(Prefer `await CompactFolderAsync` in an async method body instead of `GetAwaiter().GetResult()`.)

- [ ] **Step 6: Run tests — expect PASS**

```bash
rtk dotnet test tests/LEC.MailStore.Tests/LEC.MailStore.Tests.csproj --filter "FullyQualifiedName~DbxStoreCompactTests" 
rtk dotnet test tests/LEC.App.Tests/LEC.App.Tests.csproj --no-restore
```

- [ ] **Step 7: Commit**

```bash
rtk git add src/LEC.MailStore/IMailStore.cs src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreCompactTests.cs tests/LEC.App.Tests/FakeMailStore.cs
rtk git commit -m "Add GetCompactStatusAsync for reclaimable .dbx waste"
```

---

### Task 3: `CompactFolderAsync`

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs` (replace NotImplemented)
- Modify: `tests/LEC.MailStore.Tests/DbxStoreCompactTests.cs`

**Interfaces:**
- Consumes: same rebuild/purge pattern as `AddMessageAsync` (lines ~405–468), **omitting** `_backupStore.BeforeRewrite`
- Produces: working `CompactFolderAsync(int folderId, CancellationToken ct = default)`

- [ ] **Step 1: Write failing tests** (append to `DbxStoreCompactTests.cs`)

```csharp
    [Fact]
    public async Task CompactFolderAsyncRemovesHiddenMessagesAndShrinksTheFile()
    {
        string dir = BuildProfileWithTwoMessages();
        string deletedCache = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-deleted.db");
        try
        {
            long sizeBefore;
            using (var store = DbxStore.Open(dir, deletedMessageCacheDatabasePath: deletedCache))
            {
                var folder = store.Folders.Single();
                var headers = new List<MessageHeader>();
                await foreach (var h in store.EnumerateHeadersAsync(folder))
                    headers.Add(h);

                await store.DeleteMessageAsync(headers.Single(h => h.Subject == "Morta").Id);
                sizeBefore = new FileInfo(Path.Combine(dir, "Inbox.dbx")).Length;

                await store.CompactFolderAsync(folder.Id);

                var after = new List<MessageHeader>();
                await foreach (var h in store.EnumerateHeadersAsync(folder))
                    after.Add(h);
                Assert.Equal("Viva", Assert.Single(after).Subject);

                long sizeAfter = new FileInfo(Path.Combine(dir, "Inbox.dbx")).Length;
                Assert.True(sizeAfter < sizeBefore, $"expected shrink {sizeBefore} -> {sizeAfter}");

                var status = await store.GetCompactStatusAsync();
                Assert.Empty(status.PendingFolders);
            }

            // Caches purged: reopen with same deleted DB — Morta must not stay hidden-by-stale-offset
            using (var reopened = DbxStore.Open(dir, deletedMessageCacheDatabasePath: deletedCache))
            {
                var folder = reopened.Folders.Single();
                var headers = new List<MessageHeader>();
                await foreach (var h in reopened.EnumerateHeadersAsync(folder))
                    headers.Add(h);
                Assert.Equal("Viva", Assert.Single(headers).Subject);
            }
        }
        finally
        {
            File.Delete(deletedCache);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CompactFolderAsyncIsNoOpWhenNothingPending()
    {
        string dir = BuildProfileWithTwoMessages();
        try
        {
            using var store = DbxStore.Open(dir);
            var folder = store.Folders.Single();
            long before = new FileInfo(Path.Combine(dir, "Inbox.dbx")).Length;
            await store.CompactFolderAsync(folder.Id);
            long after = new FileInfo(Path.Combine(dir, "Inbox.dbx")).Length;
            Assert.Equal(before, after);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CompactFolderAsyncSucceedsWhenFolderWasAlreadyEnumerated()
    {
        // Same lock race AddMessageAsync already covers: enumerate first, then compact.
        string dir = BuildProfileWithTwoMessages();
        string deletedCache = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-deleted.db");
        try
        {
            using var store = DbxStore.Open(dir, deletedMessageCacheDatabasePath: deletedCache);
            var folder = store.Folders.Single();
            var headers = new List<MessageHeader>();
            await foreach (var h in store.EnumerateHeadersAsync(folder))
                headers.Add(h);
            await store.DeleteMessageAsync(headers.Single(h => h.Subject == "Morta").Id);
            await store.CompactFolderAsync(folder.Id); // must not throw UnauthorizedAccessException
        }
        finally
        {
            File.Delete(deletedCache);
            Directory.Delete(dir, recursive: true);
        }
    }
```

- [ ] **Step 2: Run — expect FAIL** (`NotImplementedException` or missing behavior)

- [ ] **Step 3: Implement `CompactFolderAsync`**

Mirror `AddMessageAsync`, with these differences:

1. If no backing file → return (no-op).
2. `EnsureMovedLoaded` / `EnsureDeletedLoaded`; if both sets empty → return (no-op).
3. Open file, if `Health != Ok` throw the same style of message as `AddMessageAsync`.
4. Build `entries` from `current.Messages` **excluding** offsets in moved∪deleted.
5. **Do not** call `_backupStore.BeforeRewrite`.
6. Evict `_openFolders` / `_messagesByFolder` for that folder.
7. `DbxEmailFileWriter.Rebuild(filePath, entries)` — even if `entries` is empty (empty folder after deleting everything is valid; `Rebuild` must already support empty lists — if not, call `CreateEmpty` or check existing empty rebuild coverage and match it).
8. Purge moved + deleted + scavenge caches for that folder (same five clears as `AddMessageAsync` after rebuild).

```csharp
public Task CompactFolderAsync(int folderId, CancellationToken ct = default)
{
    return Task.Run(() =>
    {
        var folder = _folders.FirstOrDefault(f => f.Id == folderId)
            ?? throw new InvalidOperationException($"Pasta {folderId} não encontrada.");

        if (!_backingFileNames.TryGetValue(folderId, out var fileName) || fileName is null)
            return;

        string filePath = Path.Combine(_profileDirectory, fileName);
        if (!File.Exists(filePath))
            return;

        EnsureMovedLoaded(folder);
        EnsureDeletedLoaded(folder);
        var hidden = _hiddenOffsetsByFolder[folderId];
        var deleted = _deletedOffsetsByFolder[folderId];
        if (hidden.Count == 0 && deleted.Count == 0)
            return;

        List<DbxMessageWriteEntry> entries;
        using (var current = DbxEmailFile.Open(filePath))
        {
            if (current.Health != StoreHealth.Ok)
                throw new InvalidOperationException(
                    $"\"{fileName}\" está danificado; compactar reescreveria o arquivo e descartaria as mensagens ilegíveis.");

            entries = current.Messages
                .Where(info => !hidden.Contains(info.IndexNodeOffset) && !deleted.Contains(info.IndexNodeOffset))
                .Select(info => new DbxMessageWriteEntry(
                    current.ReadMessageRaw(info, out _), info.Subject, info.SenderName, info.SenderAddress,
                    info.MessageId, info.SendTimeUtc, info.Flags,
                    info.ReceiverName, info.ReceiverAddress, info.ReceiveTimeUtc))
                .ToList();
        }

        if (_openFolders.Remove(folderId, out var stale))
            stale.Dispose();
        _messagesByFolder.Remove(folderId);

        DbxEmailFileWriter.Rebuild(filePath, entries);

        _movedMessageCache.ClearFolder(_profileDirectory, fileName);
        _hiddenOffsetsByFolder.Remove(folderId);
        _deletedMessageCache.ClearFolder(_profileDirectory, fileName);
        _deletedOffsetsByFolder.Remove(folderId);
        _scavengeCache.ClearScan(_profileDirectory, fileName);
        _scavengeStatusByFolder.Remove(folderId);
        _recoveredItemsByFolder.Remove(folderId);
        _recoveredByFolder.Remove(folderId);
    }, ct);
}
```

- [ ] **Step 4: Run tests — PASS**

- [ ] **Step 5: Commit**

```bash
rtk git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreCompactTests.cs
rtk git commit -m "Compact a folder by rebuilding its .dbx without hidden messages"
```

---

### Task 4: `CompactAllPendingAsync`

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Modify: `tests/LEC.MailStore.Tests/DbxStoreCompactTests.cs`
- Modify: `tests/LEC.App.Tests/FakeMailStore.cs` (ensure stop-on-failure matches)

**Interfaces:**
- Consumes: `GetCompactStatusAsync`, `CompactFolderAsync`
- Produces: `CompactAllPendingAsync` with progress + stop-on-failure wrap

- [ ] **Step 1: Failing tests**

```csharp
    private static string BuildProfileWithTwoFolders()
    {
        // Folders.dbx with FolderId 1 -> A.dbx, FolderId 2 -> B.dbx; each with two messages.
        // Mirror patterns in DbxStoreMoveFolderTests / multi-folder fixtures.
        // ... implement with two WriteInfoNode entries and Rebuild both email files ...
    }

    [Fact]
    public async Task CompactAllPendingAsyncCompactsEveryPendingFolderAndReportsProgress()
    {
        // Delete one message in each folder; CompactAllPending; both pending gone;
        // progress reports CompletedCount 0 then 1 (TotalCount 2) before each folder.
    }

    [Fact]
    public async Task CompactAllPendingAsyncStopsOnFirstFailureLeavingLaterFoldersIntact()
    {
        // Two pending folders. After building, set the second .dbx to ReadOnly so Rebuild fails.
        // Assert: first folder no longer pending; second still pending; exception message contains
        // "1 pasta(s) já compactada(s)" (or the exact Portuguese string chosen below).
    }
```

Exact exception format (lock this string):

```text
Falha ao compactar "{name}". {succeeded} pasta(s) já compactada(s); as demais não foram alteradas.
```

- [ ] **Step 2: Implement**

```csharp
public async Task CompactAllPendingAsync(
    IProgress<CompactProgress>? progress = null,
    CancellationToken ct = default)
{
    var status = await GetCompactStatusAsync(ct);
    var pending = status.PendingFolders;
    int succeeded = 0;
    foreach (var folder in pending)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(new CompactProgress(succeeded, pending.Count, folder.FolderName));
        try
        {
            await CompactFolderAsync(folder.FolderId, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Falha ao compactar \"{folder.FolderName}\". {succeeded} pasta(s) já compactada(s); as demais não foram alteradas.",
                ex);
        }
        succeeded++;
    }
}
```

Update `FakeMailStore.CompactAllPendingAsync` to use the same exception wrap.

- [ ] **Step 3: Run tests — PASS**

- [ ] **Step 4: Commit**

```bash
rtk git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreCompactTests.cs tests/LEC.App.Tests/FakeMailStore.cs
rtk git commit -m "Compact all pending folders, stopping on the first failure"
```

---

### Task 5: `MainWindowViewModel` compact helpers

**Files:**
- Modify: `src/LEC.App/ViewModels/MainWindowViewModel.cs`
- Modify: `tests/LEC.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `IMailStore.GetCompactStatusAsync` / `CompactFolderAsync` / `CompactAllPendingAsync`
- Produces:
  - `Task CompactFolderAsync(ProfileViewModel profile, int folderId, CancellationToken ct = default)`
  - `Task CompactAllPendingAsync(ProfileViewModel profile, IProgress<CompactProgress>? progress = null, CancellationToken ct = default)`
  - `Task<CompactStatus> GetCompactStatusAsync(ProfileViewModel profile, CancellationToken ct = default)`
  - `Task<CompactStatus> GetAggregatedCompactStatusAsync(CancellationToken ct = default)` — unions all `OpenProfiles` (for app close)

- [ ] **Step 1: Failing VM tests**

```csharp
[Fact]
public async Task CompactFolderAsyncDelegatesToTheProfileStore()
{
    var folder = new StoreFolder { Id = 1, Name = "Inbox", IsLoadable = true, HasBackingFile = true };
    var store = new FakeMailStore(folders: new[] { folder });
    store.CompactStatusToReturn = new CompactStatus(new[]
    {
        new CompactFolderStatus(1, "Inbox", 1000),
    });
    var profile = new ProfileViewModel("P", @"C:\p", store);
    var vm = new MainWindowViewModel();

    await vm.CompactFolderAsync(profile, 1);

    Assert.Equal(new[] { 1 }, store.CompactedFolderIds);
}

[Fact]
public async Task GetAggregatedCompactStatusAsyncSumsOpenProfiles()
{
    var store1 = new FakeMailStore(folders: Array.Empty<StoreFolder>());
    store1.CompactStatusToReturn = new CompactStatus(new[]
    {
        new CompactFolderStatus(1, "A", 100),
    });
    var store2 = new FakeMailStore(folders: Array.Empty<StoreFolder>());
    store2.CompactStatusToReturn = new CompactStatus(new[]
    {
        new CompactFolderStatus(2, "B", 200),
        new CompactFolderStatus(3, "C", 50),
    });
    var vm = new MainWindowViewModel();
    vm.OpenProfiles.Add(new ProfileViewModel("P1", @"C:\p1", store1));
    vm.OpenProfiles.Add(new ProfileViewModel("P2", @"C:\p2", store2));

    var status = await vm.GetAggregatedCompactStatusAsync();

    Assert.Equal(3, status.PendingFolderCount);
    Assert.Equal(350, status.TotalEstimatedReclaimableBytes);
}
```

- [ ] **Step 2: Implement thin wrappers on `MainWindowViewModel`**

```csharp
public Task CompactFolderAsync(ProfileViewModel profile, int folderId, CancellationToken ct = default)
    => profile.Store.CompactFolderAsync(folderId, ct);

public Task CompactAllPendingAsync(ProfileViewModel profile, IProgress<CompactProgress>? progress = null, CancellationToken ct = default)
    => profile.Store.CompactAllPendingAsync(progress, ct);

public Task<CompactStatus> GetCompactStatusAsync(ProfileViewModel profile, CancellationToken ct = default)
    => profile.Store.GetCompactStatusAsync(ct);

public async Task<CompactStatus> GetAggregatedCompactStatusAsync(CancellationToken ct = default)
{
    var all = new List<CompactFolderStatus>();
    foreach (var profile in OpenProfiles)
    {
        var status = await profile.Store.GetCompactStatusAsync(ct);
        all.AddRange(status.PendingFolders);
    }
    return new CompactStatus(all);
}
```

- [ ] **Step 3: Run App tests — PASS**

- [ ] **Step 4: Commit**

```bash
rtk git add src/LEC.App/ViewModels/MainWindowViewModel.cs tests/LEC.App.Tests/MainWindowViewModelTests.cs
rtk git commit -m "Expose compact operations on MainWindowViewModel"
```

---

### Task 6: `CompactProgressDialog` (modal, no cancel)

**Files:**
- Create: `src/LEC.App/CompactProgressDialog.xaml`
- Create: `src/LEC.App/CompactProgressDialog.xaml.cs`
- Modify: `src/LEC.App/LEC.App.csproj` only if new files aren’t auto-included (SDK-style usually are)

**Interfaces:**
- Consumes: `MainWindowViewModel.CompactFolderAsync` / `CompactAllPendingAsync`
- Produces: dialog with `ShowDialog()`, `Failure` property, `DialogResult == true` on full success

- [ ] **Step 1: XAML** — copy `MoveMessagesProgressDialog.xaml` structure but **omit Cancel button**; only a hidden Close button for failure final state. Title: `Compactando pastas`. Disable title-bar close while running (`Closing` cancels close if `!_finished`, same as move dialog — but there is no cancel token; while running, X must be ignored / `e.Cancel = true` until finished).

- [ ] **Step 2: Code-behind constructors**

Two modes, one class:

```csharp
// Compact one folder
public CompactProgressDialog(MainWindowViewModel vm, ProfileViewModel profile, int folderId, string folderName)

// Compact all pending in one profile
public CompactProgressDialog(MainWindowViewModel vm, ProfileViewModel profile)

// Compact all pending across many profiles (app close) — sequential
public CompactProgressDialog(MainWindowViewModel vm, IReadOnlyList<ProfileViewModel> profiles)
```

On `Loaded`:
- Single: set status `Compactando "{name}"…`, indeterminate or 0→1 bar, await `CompactFolderAsync`, success → `DialogResult = true`.
- One profile all: subscribe to `IProgress<CompactProgress>`, text `Compactando "{Current}"… ({CompletedCount + 1}/{TotalCount})`, bar = `(CompletedCount + 1.0) / TotalCount` (or `CompletedCount / Total` before start — pick one and stick to it; recommend **display index = CompletedCount + 1** while working).
- Many profiles: for each profile, get status; if empty skip; else run that profile’s `CompactAllPendingAsync`, adjusting displayed totals (sum pending counts across profiles up front via `GetAggregatedCompactStatusAsync`, then report a running global completed counter).

On failure: set `Failure`, show message + Close button (do **not** set `DialogResult = true`).

- [ ] **Step 3: Manual smoke** (no automated UI test — same as move progress plan): build app, optional.

- [ ] **Step 4: Commit**

```bash
rtk git add src/LEC.App/CompactProgressDialog.xaml src/LEC.App/CompactProgressDialog.xaml.cs
rtk git commit -m "Add non-cancellable compact progress dialog"
```

---

### Task 7: Menus — compact this folder / compact all pending

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml` (Arquivo menu + folder `ContextMenu`)
- Modify: `src/LEC.App/MainWindow.xaml.cs`
- Create: `src/LEC.App/CompactPrompt.cs` (format helpers)

**Interfaces:**
- Consumes: Task 5–6 APIs, `_selectedProfile` / folder node DataContext
- Produces: working menu actions

- [ ] **Step 1: Helper**

```csharp
// src/LEC.App/CompactPrompt.cs
internal static class CompactPrompt
{
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "~0 MB";
        double mb = bytes / (1024.0 * 1024.0);
        if (mb < 0.1) return "~0,1 MB";
        return $"~{mb:0.#} MB".Replace('.', ','); // keep simple; or use invariant and replace
    }

    public static string BuildClosePrompt(int folderCount, long bytes, bool forSingleProfile)
    {
        string where = forSingleProfile ? " neste perfil" : "";
        return $"Há {folderCount} pasta(s) com espaço a recuperar ({FormatBytes(bytes)}){where}. Compactar agora?";
    }
}
```

- [ ] **Step 2: XAML**

Under `_Arquivo`:

```xml
<MenuItem x:Name="CompactAllPendingMenuItem" Header="Compactar tudo pendente..."
          Click="CompactAllPending_Click" IsEnabled="False"/>
```

In folder `TextBlock.ContextMenu` (alongside Nova subpasta / Mover / Apagar):

```xml
<MenuItem x:Name="CompactFolderMenuItem" Header="Compactar esta pasta..."
          Click="CompactFolder_Click"/>
```

Wire `ContextMenu.Opened` on the folder menu to enable/disable `CompactFolderMenuItem` by awaiting `GetCompactStatusAsync` for `_selectedProfile` and checking whether `PendingFolders` contains that node’s `Folder.Id`. Wire `Arquivo` `SubmenuOpened` (or main menu Loaded + after delete/move/compact) to set `CompactAllPendingMenuItem.IsEnabled` from active profile status.

- [ ] **Step 3: Click handlers**

`CompactFolder_Click`: resolve profile + folder; `ShowDialog` single-folder `CompactProgressDialog`; on success refresh current folder if it was the one compacted (`SelectFolderAsync`), set `StatusText = "Compactação concluída"`.

`CompactAllPending_Click`: require `_selectedProfile` or `ActiveProfile`; show all-pending dialog for that profile; on success refresh + status with count/bytes from pre-fetched status.

- [ ] **Step 4: Commit**

```bash
rtk git add src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs src/LEC.App/CompactPrompt.cs
rtk git commit -m "Add compact folder and compact-all menu actions"
```

---

### Task 8: Prompt when closing a profile

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml.cs` (`CloseProfile_Click`)

**Interfaces:**
- Consumes: `GetCompactStatusAsync(profile)`, `CompactProgressDialog` (one profile), `CloseProfile`
- Produces: Yes → compact then close; No → close; failure → keep open + MessageBox

- [ ] **Step 1: Change `CloseProfile_Click` to `async void`** (already fine for click handlers elsewhere) **and** insert before dispose:

```csharp
var status = await _viewModel.GetCompactStatusAsync(profile);
if (status.PendingFolderCount > 0)
{
    var answer = MessageBox.Show(
        CompactPrompt.BuildClosePrompt(status.PendingFolderCount, status.TotalEstimatedReclaimableBytes, forSingleProfile: true),
        "Compactar pastas",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question);
    if (answer == MessageBoxResult.Yes)
    {
        var dialog = new CompactProgressDialog(_viewModel, profile) { Owner = this };
        bool? ok = dialog.ShowDialog();
        if (ok != true)
        {
            MessageBox.Show(dialog.Failure?.Message ?? "A compactação não foi concluída.",
                "Compactar pastas", MessageBoxButton.OK, MessageBoxImage.Error);
            return; // profile stays open
        }
    }
}
// existing cancel-scavenge + CloseProfile logic...
```

- [ ] **Step 2: Manual check** — open profile, delete a message, close profile → prompt appears.

- [ ] **Step 3: Commit**

```bash
rtk git add src/LEC.App/MainWindow.xaml.cs
rtk git commit -m "Prompt to compact pending folders before closing a profile"
```

---

### Task 9: Prompt when closing the app

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml` — `Closing="MainWindow_Closing"`
- Modify: `src/LEC.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `GetAggregatedCompactStatusAsync`, multi-profile `CompactProgressDialog`
- Produces: Yes → compact all open profiles then allow close; No → close; failure → `e.Cancel = true`

- [ ] **Step 1: Implement `MainWindow_Closing`**

WPF `Closing` cannot easily `await`. Pattern used in many apps: if pending and user chooses Yes, set `e.Cancel = true`, run compact async, then `Close()` again with a `_forceClose` flag.

```csharp
private bool _forceClose;

private async void MainWindow_Closing(object sender, CancelEventArgs e)
{
    if (_forceClose) return;

    var status = await _viewModel.GetAggregatedCompactStatusAsync();
    if (status.PendingFolderCount == 0) return;

    e.Cancel = true;
    var answer = MessageBox.Show(
        CompactPrompt.BuildClosePrompt(status.PendingFolderCount, status.TotalEstimatedReclaimableBytes, forSingleProfile: false),
        "Compactar pastas",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question);

    if (answer == MessageBoxResult.No)
    {
        _forceClose = true;
        Close();
        return;
    }

    var dialog = new CompactProgressDialog(_viewModel, _viewModel.OpenProfiles.ToList()) { Owner = this };
    bool? ok = dialog.ShowDialog();
    if (ok == true)
    {
        _forceClose = true;
        Close();
    }
    else
    {
        MessageBox.Show(dialog.Failure?.Message ?? "A compactação não foi concluída.",
            "Compactar pastas", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
```

**Caveat:** `Closing` handler marked `async void` — the first `await` may run **after** the Closing event returns. To make `e.Cancel = true` reliable, **do not await before setting `e.Cancel`**. Fetch status synchronously via `.GetAwaiter().GetResult()` **or** always `e.Cancel = true` first when profiles are open and branch with a sync check:

Safer pattern:

```csharp
private void MainWindow_Closing(object sender, CancelEventArgs e)
{
    if (_forceClose) return;

    // Block Closing synchronously for the status read — stores are local disk, acceptable.
    var status = _viewModel.GetAggregatedCompactStatusAsync().GetAwaiter().GetResult();
    if (status.PendingFolderCount == 0) return;

    var answer = MessageBox.Show(...);
    if (answer == MessageBoxResult.No) return; // allow close

    e.Cancel = true;
    var dialog = new CompactProgressDialog(...);
    if (dialog.ShowDialog() == true)
    {
        _forceClose = true;
        Dispatcher.BeginInvoke(new Action(Close));
    }
    else
    {
        MessageBox.Show(...);
    }
}
```

Use this safer sync Closing pattern in the implementation.

- [ ] **Step 2: Manual** — two profiles with pending; Alt+F4 → aggregated N/MB; Yes compacts both; fail mid-way leaves app open.

- [ ] **Step 3: Commit**

```bash
rtk git add src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs
rtk git commit -m "Prompt to compact all pending folders before quitting"
```

---

### Task 10: Final verification

- [ ] **Step 1: Full test suites**

```bash
rtk dotnet test tests/LEC.MailStore.Tests/LEC.MailStore.Tests.csproj
rtk dotnet test tests/LEC.App.Tests/LEC.App.Tests.csproj
```

Expected: all PASS.

- [ ] **Step 2: Manual checklist**

1. Delete/move messages → “Compactar esta pasta…” enables on that folder.  
2. Compact one folder → file shrinks; menu disables.  
3. “Compactar tudo pendente…” on active profile.  
4. Close profile with pending → profile-scoped prompt.  
5. Close app with two profiles pending → aggregated prompt.  
6. Force a failure (read-only `.dbx`) mid “tudo” → error text with count; close aborted.

- [ ] **Step 3: Commit only if leftover fixes**; otherwise done.

---

## Spec coverage (self-review)

| Spec item | Task |
|---|---|
| Manual compact + pending from caches | 2–3 |
| Messages only | Global + Task 3 |
| Per folder + all pending | 4, 7 |
| No backup | Task 3 (no BeforeRewrite) |
| Modal progress no cancel | 6 |
| Context + Arquivo menus | 7 |
| Close always prompts | 8–9 |
| Stop on first failure | 4 |
| Close app = all profiles | 9 |
| Close profile = that profile | 8 |
| Estimate MessageSize | 2 |
| Rename / backup / tree badge | out of scope |

## Placeholder / type check

- `CompactProgress` Report timing: before each folder, `CompletedCount` = already finished.  
- Exception string locked in Task 4.  
- FakeMailStore and DbxStore share `IMailStore` compact members from Task 2 onward.
