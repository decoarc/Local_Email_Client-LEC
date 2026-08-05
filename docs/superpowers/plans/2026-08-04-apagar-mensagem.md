# Apagar mensagem Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user delete a message from the main window — hides it from its folder via a local cache, never touches the source `.dbx` — via a "Apagar" context-menu item and the Delete key on the message list, with a confirmation prompt.

**Architecture:** `DeletedMessageCache` (new, `LEC.MailStore`) mirrors the existing `MovedMessageCache` byte-for-byte in structure — same SQLite-under-`%LOCALAPPDATA%\LEC\` pattern, its own table, kept deliberately separate from `MovedMessageCache` so "deleted" and "moved" never become indistinguishable in stored data. `IMailStore.DeleteMessageAsync` wires it into `DbxStore` the same way `HideMovedMessageAsync` already is: `EnumerateHeadersAsync` skips a message hidden by *either* cache, and `AddMessageAsync`'s existing post-`Rebuild` purge (which already clears `MovedMessageCache`/`ScavengeCache` state for a rebuilt folder) is extended to clear `DeletedMessageCache` state too, for the identical reason. `MainWindowViewModel.DeleteMessageAsync` is a thin pass-through (no cross-store orchestration needed, unlike `MoveMessageAsync` — delete never writes anywhere). UI adds "Apagar" to the message list's existing per-item context menu and a Delete-key handler, both behind a Yes/No confirmation.

**Tech Stack:** .NET 9, C#, WPF, Microsoft.Data.Sqlite, xUnit.

## Global Constraints

- Esconder direto, sem lixeira — nenhuma pasta de destino, nenhuma cópia, só esconde da pasta de origem via cache local. Nunca escreve no `.dbx`.
- `DeletedMessageCache` é separado de `MovedMessageCache` — mesma mecânica (esconder um offset via cache local), tabelas/caches distintos.
- `EnumerateHeadersAsync` pula uma mensagem escondida por *qualquer um* dos dois caches (movida OU apagada) — os dois conjuntos são consultados de forma independente.
- `AddMessageAsync`'s purga pós-`Rebuild` (já existe pra `MovedMessageCache`/`ScavengeCache`) precisa cobrir `DeletedMessageCache` também, pela mesma razão: `Rebuild` invalida todo `IndexNodeOffset` da pasta reconstruída.
- Gatilho: menu de contexto (mesmo item da lista já usado por "Mover para...") + tecla Delete.
- Com confirmação (`MessageBox` Sim/Não) antes de esconder.
- Seleção múltipla fora de escopo — lista continua seleção única.
- Apagar não lança normalmente (mesmo comportamento de `HideMovedMessageAsync` hoje — no-op silencioso pra pasta sem arquivo de apoio), mas a UI trata qualquer exceção defensivamente do mesmo jeito que "mover"/"criar pasta" já fazem.

---

## File Structure

```
src/LEC.MailStore/
  DeletedMessageCache.cs         NEW — Task 1: cópia do padrão de MovedMessageCache.cs
  IMailStore.cs                  MODIFIED — Task 2: +DeleteMessageAsync
  DbxStore.cs                    MODIFIED — Task 2: +DeleteMessageAsync, +filtro em EnumerateHeadersAsync, +purga em AddMessageAsync

src/LEC.App/ViewModels/
  MainWindowViewModel.cs         MODIFIED — Task 3: +DeleteMessageAsync

src/LEC.App/
  MainWindow.xaml                MODIFIED — Task 4: +"Apagar" no menu de contexto, +tecla Delete na lista
  MainWindow.xaml.cs             MODIFIED — Task 4: +DeleteMessage_Click, +MessageListView_KeyDown, +ConfirmAndDeleteAsync

tests/LEC.MailStore.Tests/
  DeletedMessageCacheTests.cs    NEW — Task 1: cópia do padrão de MovedMessageCacheTests.cs
  DbxStoreTests.cs               MODIFIED — Task 2: +teste de DeleteMessageAsync, +teste de independência entre os dois caches
  DbxStoreAddMessageTests.cs     MODIFIED — Task 2: +teste de purga do DeletedMessageCache

tests/LEC.App.Tests/
  FakeMailStore.cs               MODIFIED — Task 3: +DeleteMessageAsync
  MainWindowViewModelTests.cs    MODIFIED — Task 3: +teste de DeleteMessageAsync
```

`MainWindow.xaml`'s per-item `ContextMenu` (already holding "Mover para...") is extended with a second `MenuItem`, not a separate menu — same `EventSetter`-based event wiring already established for "Mover para..." (an inline `Click` on a `MenuItem` inside `ListView.ItemContainerStyle`'s `Setter.Value` doesn't compile-wire in WPF — see the "mover mensagem — UI" plan's Task 4 correction note for why).

---

### Task 1: `DeletedMessageCache`

**Files:**
- Create: `src/LEC.MailStore/DeletedMessageCache.cs`
- Test: `tests/LEC.MailStore.Tests/DeletedMessageCacheTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Data.Sqlite` (existing dependency, already used by `ScavengeCache`/`MovedMessageCache`).
- Produces: `DeletedMessageCache(string databasePath)`, `.DefaultDatabasePath` (static), `.GetDeletedOffsets(string profilePath, string folderFile) : HashSet<int>`, `.MarkDeleted(string profilePath, string folderFile, int indexNodeOffset) : void`, `.ClearFolder(string profilePath, string folderFile) : void`, `.Dispose()`. Consumed by Task 2.

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.MailStore.Tests/DeletedMessageCacheTests.cs`:

```csharp
// tests/LEC.MailStore.Tests/DeletedMessageCacheTests.cs
using Xunit;

namespace LEC.MailStore.Tests;

public class DeletedMessageCacheTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lec-deleted-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void GetDeletedOffsetsReturnsEmptyWhenNothingMarked()
    {
        using var cache = new DeletedMessageCache(TempDbPath());

        var offsets = cache.GetDeletedOffsets(@"C:\profiles\BCP", "Caixa de entrada.dbx");

        Assert.Empty(offsets);
    }

    [Fact]
    public void MarkDeletedThenGetDeletedOffsetsRoundTrips()
    {
        string dbPath = TempDbPath();

        using (var cache = new DeletedMessageCache(dbPath))
            cache.MarkDeleted(@"C:\profiles\BCP", "Caixa de entrada.dbx", 500);

        using (var reopened = new DeletedMessageCache(dbPath))
        {
            var offsets = reopened.GetDeletedOffsets(@"C:\profiles\BCP", "Caixa de entrada.dbx");
            Assert.Contains(500, offsets);
        }
    }

    [Fact]
    public void MarkDeletedTwiceForTheSameOffsetIsIdempotent()
    {
        using var cache = new DeletedMessageCache(TempDbPath());

        cache.MarkDeleted(@"C:\p", "f.dbx", 100);
        cache.MarkDeleted(@"C:\p", "f.dbx", 100); // must not throw

        Assert.Single(cache.GetDeletedOffsets(@"C:\p", "f.dbx"));
    }

    [Fact]
    public void ClearFolderRemovesEveryOffsetForThatFolderOnly()
    {
        using var cache = new DeletedMessageCache(TempDbPath());

        cache.MarkDeleted(@"C:\p", "alvo.dbx", 100);
        cache.MarkDeleted(@"C:\p", "alvo.dbx", 200);
        cache.MarkDeleted(@"C:\p", "outra.dbx", 300);
        cache.MarkDeleted(@"C:\outroPerfil", "alvo.dbx", 400);

        cache.ClearFolder(@"C:\p", "alvo.dbx");

        Assert.Empty(cache.GetDeletedOffsets(@"C:\p", "alvo.dbx"));
        Assert.Equal(new[] { 300 }, cache.GetDeletedOffsets(@"C:\p", "outra.dbx"));
        Assert.Equal(new[] { 400 }, cache.GetDeletedOffsets(@"C:\outroPerfil", "alvo.dbx"));
    }

    [Fact]
    public void ClearFolderOnAFolderThatWasNeverMarkedIsANoOp()
    {
        using var cache = new DeletedMessageCache(TempDbPath());

        cache.ClearFolder(@"C:\p", "nunca-tocada.dbx"); // must not throw

        Assert.Empty(cache.GetDeletedOffsets(@"C:\p", "nunca-tocada.dbx"));
    }

    [Fact]
    public void DifferentProfilesWithTheSameFolderFileNameDoNotCollide()
    {
        using var cache = new DeletedMessageCache(TempDbPath());

        cache.MarkDeleted(@"C:\profileA", "Caixa de entrada.dbx", 1);
        cache.MarkDeleted(@"C:\profileB", "Caixa de entrada.dbx", 2);

        Assert.Equal(new[] { 1 }, cache.GetDeletedOffsets(@"C:\profileA", "Caixa de entrada.dbx"));
        Assert.Equal(new[] { 2 }, cache.GetDeletedOffsets(@"C:\profileB", "Caixa de entrada.dbx"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DeletedMessageCacheTests"`
Expected: FAIL to compile — `DeletedMessageCache` doesn't exist yet.

- [ ] **Step 3: Implement `DeletedMessageCache`**

Create `src/LEC.MailStore/DeletedMessageCache.cs`:

```csharp
// src/LEC.MailStore/DeletedMessageCache.cs
using Microsoft.Data.Sqlite;

namespace LEC.MailStore;

/// <summary>
/// Tracks messages "deleted" (hidden from their folder, never removed from their original
/// .dbx — see docs/superpowers/specs/2026-08-04-apagar-mensagem-design.md §2) so
/// DbxStore.EnumerateHeadersAsync can hide them without ever touching the source .dbx.
/// Same pattern as MovedMessageCache/ScavengeCache: lightweight SQLite under
/// %LOCALAPPDATA%\LEC\, never next to the source profile. Kept deliberately separate from
/// MovedMessageCache even though the mechanics are identical -- "deleted" and "moved" stay
/// distinct concepts in the stored data, not just in the UI.
/// </summary>
public sealed class DeletedMessageCache : IDisposable
{
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LEC", "deleted-cache.db");

    private readonly SqliteConnection _connection;

    public DeletedMessageCache(string databasePath)
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
            CREATE TABLE IF NOT EXISTS deleted_message (
                profile_path      TEXT    NOT NULL,
                folder_file       TEXT    NOT NULL,
                index_node_offset INTEGER NOT NULL,
                PRIMARY KEY (profile_path, folder_file, index_node_offset)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public HashSet<int> GetDeletedOffsets(string profilePath, string folderFile)
    {
        var offsets = new HashSet<int>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT index_node_offset FROM deleted_message WHERE profile_path = $p AND folder_file = $f";
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            offsets.Add(reader.GetInt32(0));

        return offsets;
    }

    public void MarkDeleted(string profilePath, string folderFile, int indexNodeOffset)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO deleted_message (profile_path, folder_file, index_node_offset)
            VALUES ($p, $f, $offset)
            """;
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);
        cmd.Parameters.AddWithValue("$offset", indexNodeOffset);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Drops every hidden-message row for one folder. Called after that folder's .dbx is
    /// rebuilt from scratch (see <see cref="DbxStore.AddMessageAsync"/>): a rebuild renumbers
    /// every info-node offset in the file, so rows keyed on the old offsets no longer identify
    /// the messages they were recorded for — keeping them would silently hide whatever
    /// unrelated message happens to land on a matching offset in the new layout.
    /// </summary>
    public void ClearFolder(string profilePath, string folderFile)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM deleted_message WHERE profile_path = $p AND folder_file = $f";
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DeletedMessageCacheTests"`
Expected: PASS — all 6 tests.

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/DeletedMessageCache.cs tests/LEC.MailStore.Tests/DeletedMessageCacheTests.cs
git commit -m "Add DeletedMessageCache: local, profile-scoped record of messages hidden by delete"
```

---

### Task 2: `IMailStore.DeleteMessageAsync` + `DbxStore` wiring

**Files:**
- Modify: `src/LEC.MailStore/IMailStore.cs`
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Modify: `tests/LEC.MailStore.Tests/DbxStoreTests.cs`
- Modify: `tests/LEC.MailStore.Tests/DbxStoreAddMessageTests.cs`

**Interfaces:**
- Consumes: `DeletedMessageCache` (Task 1).
- Produces: `IMailStore.DeleteMessageAsync(MessageRef id, CancellationToken ct = default) : Task`. Consumed by Task 3. `EnumerateHeadersAsync` (existing, modified) now also skips deleted messages for every caller.

- [ ] **Step 1: Write the failing tests**

In `tests/LEC.MailStore.Tests/DbxStoreTests.cs`, add at the end of the class, right before the closing `}` (reuse the existing `CollectAsync` helper already in that file — don't add a second one):

```csharp
    [Fact]
    public async Task DeleteMessageAsyncRemovesTheMessageFromEnumerateHeadersAsyncAndPersistsAcrossReopen()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int inboxNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
        });
        int root = localFolders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());

        var inboxMessages = new TestDbxBuilder();
        inboxMessages.WriteHeader(DbxFileType.Email);
        int block = inboxMessages.WriteMessageBlock("conteudo"u8.ToArray(), nextOffset: 0);
        int msgNode = inboxMessages.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)block },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = "Assunto\0"u8.ToArray() },
        });
        int msgRoot = inboxMessages.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: msgNode, entryNextTable: 0, entryIndexCount: 0) });
        inboxMessages.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        inboxMessages.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, msgRoot);
        profile.WriteEmailDbx("Inbox.dbx", inboxMessages.ToArray());

        string deletedCacheDbPath = Path.Combine(Path.GetTempPath(), $"lec-deleted-{Guid.NewGuid():N}.db");
        try
        {
            StoreFolder folder;
            using (var store = DbxStore.Open(profile.ProfileDirectory, deletedMessageCacheDatabasePath: deletedCacheDbPath))
            {
                folder = store.Folders.Single();

                var headersBefore = await CollectAsync(store.EnumerateHeadersAsync(folder));
                var header = Assert.Single(headersBefore);

                await store.DeleteMessageAsync(header.Id);

                var headersAfterSameSession = await CollectAsync(store.EnumerateHeadersAsync(folder));
                Assert.Empty(headersAfterSameSession);
            }

            using var reopened = DbxStore.Open(profile.ProfileDirectory, deletedMessageCacheDatabasePath: deletedCacheDbPath);
            var headersAfterReopen = await CollectAsync(reopened.EnumerateHeadersAsync(folder));
            Assert.Empty(headersAfterReopen);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(deletedCacheDbPath);
        }
    }

    [Fact]
    public async Task DeleteMessageAsyncAndHideMovedMessageAsyncAreIndependent()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int inboxNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
        });
        int root = localFolders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, root);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());

        var inboxMessages = new TestDbxBuilder();
        inboxMessages.WriteHeader(DbxFileType.Email);
        int blockA = inboxMessages.WriteMessageBlock("um"u8.ToArray(), nextOffset: 0);
        int nodeA = inboxMessages.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)blockA },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = "Um\0"u8.ToArray() },
        });
        int blockB = inboxMessages.WriteMessageBlock("dois"u8.ToArray(), nextOffset: 0);
        int nodeB = inboxMessages.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)blockB },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = "Dois\0"u8.ToArray() },
        });
        int msgRoot = inboxMessages.WriteIndexNode(nextTable: 0, ptrCount: 2, indexCount: 0,
            entries: new[] { (indexPtr: nodeA, entryNextTable: 0, entryIndexCount: 0),
                              (indexPtr: nodeB, entryNextTable: 0, entryIndexCount: 0) });
        inboxMessages.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderItemCountOffset, 2);
        inboxMessages.SetInt32At(LEC.DbxFormat.DbxFormat.HeaderIndexPointerOffset, msgRoot);
        profile.WriteEmailDbx("Inbox.dbx", inboxMessages.ToArray());

        string movedCacheDbPath = Path.Combine(Path.GetTempPath(), $"lec-moved-{Guid.NewGuid():N}.db");
        string deletedCacheDbPath = Path.Combine(Path.GetTempPath(), $"lec-deleted-{Guid.NewGuid():N}.db");
        try
        {
            using var store = DbxStore.Open(profile.ProfileDirectory,
                movedMessageCacheDatabasePath: movedCacheDbPath, deletedMessageCacheDatabasePath: deletedCacheDbPath);
            var folder = store.Folders.Single();

            var before = await CollectAsync(store.EnumerateHeadersAsync(folder));
            var um = before.Single(h => h.Subject == "Um");
            var dois = before.Single(h => h.Subject == "Dois");

            await store.DeleteMessageAsync(um.Id);
            await store.HideMovedMessageAsync(dois.Id);

            // Both are hidden from the list -- but each only through its own cache.
            Assert.Empty(await CollectAsync(store.EnumerateHeadersAsync(folder)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(movedCacheDbPath);
            File.Delete(deletedCacheDbPath);
        }
    }
```

(If the class-level `CollectAsync` helper isn't already the last member of `DbxStoreTests`, add these two `[Fact]` methods anywhere before the closing `}`, just don't duplicate `CollectAsync`.)

In `tests/LEC.MailStore.Tests/DbxStoreAddMessageTests.cs`, add at the end of the class, right before the closing `}`:

```csharp
    [Fact]
    public async Task AddMessageAsyncPurgesStaleDeletedOffsetsWhenTheFolderIsAlsoADeleteTarget()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        string deletedCacheDbPath = Path.Combine(Path.GetTempPath(), $"lec-deleted-{Guid.NewGuid():N}.db");
        try
        {
            DbxEmailFileWriter.Rebuild(Path.Combine(dir, "Destino.dbx"), new[]
            {
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("um"), "Um", null, null, null, null),
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("dois"), "Dois", null, null, null, null),
            });

            StoreFolder folder;
            using (var store = DbxStore.Open(dir, deletedMessageCacheDatabasePath: deletedCacheDbPath))
            {
                folder = store.Folders.Single();

                var before = await CollectAsync(store.EnumerateHeadersAsync(folder));
                Assert.Equal(2, before.Count);

                await store.DeleteMessageAsync(before.Single(h => h.Subject == "Um").Id);
                Assert.Equal(new[] { "Dois" },
                    (await CollectAsync(store.EnumerateHeadersAsync(folder))).Select(h => h.Subject));

                await store.AddMessageAsync(folder.Id, Encoding.UTF8.GetBytes("tres"), "Tres", null, null, null, null);

                var after = await CollectAsync(store.EnumerateHeadersAsync(folder));
                Assert.Equal(new[] { "Um", "Dois", "Tres" }, after.Select(h => h.Subject));
            }

            using var reopened = DbxStore.Open(dir, deletedMessageCacheDatabasePath: deletedCacheDbPath);
            var afterReopen = await CollectAsync(reopened.EnumerateHeadersAsync(reopened.Folders.Single()));
            Assert.Equal(new[] { "Um", "Dois", "Tres" }, afterReopen.Select(h => h.Subject));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(deletedCacheDbPath);
            Directory.Delete(dir, recursive: true);
        }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DeleteMessageAsyncRemovesTheMessageFromEnumerateHeadersAsyncAndPersistsAcrossReopen|DeleteMessageAsyncAndHideMovedMessageAsyncAreIndependent|AddMessageAsyncPurgesStaleDeletedOffsetsWhenTheFolderIsAlsoADeleteTarget"`
Expected: FAIL to compile — `DeleteMessageAsync` and the `deletedMessageCacheDatabasePath` parameter on `DbxStore.Open` don't exist yet.

- [ ] **Step 3: Add `DeleteMessageAsync` to `IMailStore`**

In `src/LEC.MailStore/IMailStore.cs`, replace:

```csharp
    /// <summary>
    /// Marks a message as moved away from its folder — EnumerateHeadersAsync stops
    /// returning it from now on. Never writes to the backing .dbx; this is purely a
    /// local, app-side "hide" marker (see MovedMessageCache). A no-op, not an error, if
    /// called twice for the same message.
    /// </summary>
    Task HideMovedMessageAsync(MessageRef id, CancellationToken ct = default);
}
```

with:

```csharp
    /// <summary>
    /// Marks a message as moved away from its folder — EnumerateHeadersAsync stops
    /// returning it from now on. Never writes to the backing .dbx; this is purely a
    /// local, app-side "hide" marker (see MovedMessageCache). A no-op, not an error, if
    /// called twice for the same message.
    /// </summary>
    Task HideMovedMessageAsync(MessageRef id, CancellationToken ct = default);

    /// <summary>
    /// Marks a message as deleted — EnumerateHeadersAsync stops returning it from now on.
    /// Never writes to the backing .dbx; purely a local, app-side "hide" marker (see
    /// DeletedMessageCache), kept separate from HideMovedMessageAsync's marker even though
    /// the mechanism is identical. A no-op, not an error, if called twice for the same
    /// message.
    /// </summary>
    Task DeleteMessageAsync(MessageRef id, CancellationToken ct = default);
}
```

- [ ] **Step 4: Wire `DeletedMessageCache` into `DbxStore` and add `DeleteMessageAsync`**

In `src/LEC.MailStore/DbxStore.cs`, replace the field declarations and constructor:

```csharp
    private readonly MovedMessageCache _movedMessageCache;
    private readonly Dictionary<int, HashSet<int>> _hiddenOffsetsByFolder = new();

    private DbxStore(string profileDirectory, List<StoreFolder> folders, Dictionary<int, string?> backingFileNames, StoreHealth health, ScavengeCache scavengeCache, MovedMessageCache movedMessageCache)
    {
        _profileDirectory = profileDirectory;
        _folders = folders;
        _backingFileNames = backingFileNames;
        Health = health;
        _scavengeCache = scavengeCache;
        _movedMessageCache = movedMessageCache;
    }
```

with:

```csharp
    private readonly MovedMessageCache _movedMessageCache;
    private readonly Dictionary<int, HashSet<int>> _hiddenOffsetsByFolder = new();

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

Replace the `Open` method:

```csharp
    public static DbxStore Open(string profileDirectoryPath, string? scavengeCacheDatabasePath = null, string? movedMessageCacheDatabasePath = null)
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
        return new DbxStore(profileDirectoryPath, folders, backingFileNames, health, scavengeCache, movedMessageCache);
    }
```

with:

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

Add `OpenDeletedMessageCacheWithFallback` right after `OpenMovedMessageCacheWithFallback`:

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

Replace `EnumerateHeadersAsync`'s hidden-offset check:

```csharp
        EnsureMovedLoaded(folder);
        var hiddenOffsets = _hiddenOffsetsByFolder[folder.Id];

        foreach (var info in emailFile.Messages)
        {
            ct.ThrowIfCancellationRequested();
            if (hiddenOffsets.Contains(info.IndexNodeOffset))
                continue;
```

with:

```csharp
        EnsureMovedLoaded(folder);
        var hiddenOffsets = _hiddenOffsetsByFolder[folder.Id];

        EnsureDeletedLoaded(folder);
        var deletedOffsets = _deletedOffsetsByFolder[folder.Id];

        foreach (var info in emailFile.Messages)
        {
            ct.ThrowIfCancellationRequested();
            if (hiddenOffsets.Contains(info.IndexNodeOffset) || deletedOffsets.Contains(info.IndexNodeOffset))
                continue;
```

Add `EnsureDeletedLoaded` and `DeleteMessageAsync` right after the existing `EnsureMovedLoaded`/`HideMovedMessageAsync` pair:

```csharp
    private void EnsureDeletedLoaded(StoreFolder folder)
    {
        if (_deletedOffsetsByFolder.ContainsKey(folder.Id))
            return;

        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
        {
            _deletedOffsetsByFolder[folder.Id] = new HashSet<int>();
            return;
        }

        _deletedOffsetsByFolder[folder.Id] = _deletedMessageCache.GetDeletedOffsets(_profileDirectory, fileName);
    }

    /// <summary>
    /// Records that a message was deleted, so EnumerateHeadersAsync stops listing it — the
    /// source .dbx itself is never touched.
    /// </summary>
    /// <remarks>
    /// Not thread-safe: this mutates the unsynchronized _deletedOffsetsByFolder dictionary
    /// (and writes to the shared DeletedMessageCache connection). Concurrent calls against
    /// the same DbxStore instance must be serialized by the caller, exactly as
    /// HideMovedMessageAsync already requires.
    /// </remarks>
    public Task DeleteMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_backingFileNames.TryGetValue(id.FolderId, out var fileName) || fileName is null)
                return;

            _deletedMessageCache.MarkDeleted(_profileDirectory, fileName, id.IndexNodeOffset);

            if (!_deletedOffsetsByFolder.TryGetValue(id.FolderId, out var offsets))
            {
                offsets = new HashSet<int>();
                _deletedOffsetsByFolder[id.FolderId] = offsets;
            }
            offsets.Add(id.IndexNodeOffset);
        }, ct);
    }
```

Extend `AddMessageAsync`'s post-`Rebuild` purge — replace:

```csharp
            _movedMessageCache.ClearFolder(_profileDirectory, fileName);
            _hiddenOffsetsByFolder.Remove(folderId);

            _scavengeCache.ClearScan(_profileDirectory, fileName);
            _scavengeStatusByFolder.Remove(folderId);
            _recoveredItemsByFolder.Remove(folderId);
            _recoveredByFolder.Remove(folderId);
        }, ct);
    }
```

with:

```csharp
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

Finally, in `Dispose()`, replace:

```csharp
    public void Dispose()
    {
        foreach (var file in _openFolders.Values)
            file.Dispose();
        _openFolders.Clear();
        _scavengeCache.Dispose();
        _movedMessageCache.Dispose();
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
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DeleteMessageAsyncRemovesTheMessageFromEnumerateHeadersAsyncAndPersistsAcrossReopen|DeleteMessageAsyncAndHideMovedMessageAsyncAreIndependent|AddMessageAsyncPurgesStaleDeletedOffsetsWhenTheFolderIsAlsoADeleteTarget"`
Expected: PASS.

Run: `dotnet test`
Expected: PASS — no regression (`LEC.App.Tests`' `FakeMailStore` doesn't implement `IMailStore.DeleteMessageAsync` yet; that's Task 3, so `LEC.App.Tests` is expected to fail to compile at this step — if it does, stop and re-read Task 3 before proceeding rather than trying to fix `FakeMailStore` here).

- [ ] **Step 6: Commit**

```bash
git add src/LEC.MailStore/IMailStore.cs src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreTests.cs tests/LEC.MailStore.Tests/DbxStoreAddMessageTests.cs
git commit -m "Add DbxStore.DeleteMessageAsync: hides a message via DeletedMessageCache, independent of moved-message hiding"
```

---

### Task 3: `MainWindowViewModel.DeleteMessageAsync`

**Files:**
- Modify: `src/LEC.App/ViewModels/MainWindowViewModel.cs`
- Modify: `tests/LEC.App.Tests/FakeMailStore.cs`
- Modify: `tests/LEC.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `IMailStore.DeleteMessageAsync` (Task 2), `ProfileViewModel.Store` (existing).
- Produces: `MainWindowViewModel.DeleteMessageAsync(ProfileViewModel profile, MessageHeader message, CancellationToken ct = default) : Task`. Consumed by Task 4.

- [ ] **Step 1: Write the failing tests**

First, `FakeMailStore` needs to implement the new `IMailStore` member before it compiles at all (it already fails to compile against `IMailStore` after Task 2 — this step fixes that as part of adding the test infrastructure this task's own test needs). In `tests/LEC.App.Tests/FakeMailStore.cs`, add right after the existing `HideMovedMessageAsync` method (before `public bool WasDisposed`):

```csharp

    public HashSet<MessageRef> DeletedMessageRefs { get; } = new();

    public Task DeleteMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        DeletedMessageRefs.Add(id);
        return Task.CompletedTask;
    }
```

Then, in `tests/LEC.App.Tests/MainWindowViewModelTests.cs`, add at the end of the class, before the last `}` (check the file's existing top-of-class helpers first — reuse any existing folder/profile builder instead of duplicating one):

```csharp
    [Fact]
    public async Task DeleteMessageAsyncCallsTheStoresDeleteMessageAsync()
    {
        var folder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var messageRef = new MessageRef(folder.Id, 500);
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfis\Perfil", store);

        var header = new MessageHeader { Id = messageRef, Subject = "Assunto" };
        var viewModel = new MainWindowViewModel();

        await viewModel.DeleteMessageAsync(profile, header);

        Assert.Contains(messageRef, store.DeletedMessageRefs);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter "DeleteMessageAsyncCallsTheStoresDeleteMessageAsync"`
Expected: FAIL to compile — `MainWindowViewModel.DeleteMessageAsync` doesn't exist yet.

- [ ] **Step 3: Add `DeleteMessageAsync` to `MainWindowViewModel`**

In `src/LEC.App/ViewModels/MainWindowViewModel.cs`, add at the end of the class, right after `MoveMessageAsync` (before the class's closing `}`):

```csharp

    /// <summary>
    /// Deletes a message — hides it from its folder via IMailStore.DeleteMessageAsync,
    /// never writes to the backing .dbx. Thin pass-through: unlike MoveMessageAsync, there's
    /// no second store to orchestrate against.
    /// </summary>
    public async Task DeleteMessageAsync(ProfileViewModel profile, MessageHeader message, CancellationToken ct = default)
    {
        await profile.Store.DeleteMessageAsync(message.Id, ct);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter "DeleteMessageAsyncCallsTheStoresDeleteMessageAsync"`
Expected: PASS.

Run: `dotnet test`
Expected: PASS — the full solution, no regressions anywhere.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/MainWindowViewModel.cs tests/LEC.App.Tests/FakeMailStore.cs tests/LEC.App.Tests/MainWindowViewModelTests.cs
git commit -m "Add MainWindowViewModel.DeleteMessageAsync: hides a message from its folder"
```

---

### Task 4: Wire "Apagar" into the message list

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml`
- Modify: `src/LEC.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.DeleteMessageAsync` (Task 3), `MainWindowViewModel.SelectFolderAsync` (existing), `_selectedProfile`/`_selectedNode` (existing fields).
- Produces: nothing consumed by a later task — this is the last task of the plan.

There is no automated test for this task, matching the precedent already established by the "mover mensagem — UI" plan: none of `MainWindow.xaml.cs`'s other click/key handlers have dedicated tests. Step 4 below is a manual verification checklist instead — follow it after Step 3's build succeeds.

- [ ] **Step 1: Add "Apagar" to the message list's context menu, and a Delete-key handler**

In `src/LEC.App/MainWindow.xaml`, replace:

```xml
                    <ListView x:Name="MessageListView"
                              ItemsSource="{Binding MessageList.Messages}"
                              SelectionChanged="MessageListView_SelectionChanged"
                              VirtualizingPanel.IsVirtualizing="True"
                              VirtualizingPanel.VirtualizationMode="Recycling">
                        <ListView.ItemContainerStyle>
                            <Style TargetType="ListViewItem">
                                <Setter Property="Foreground" Value="{Binding RecoveredKind, Converter={StaticResource RecoveredKindToRowBrushConverter}}"/>
                                <Setter Property="ContextMenu">
                                    <Setter.Value>
                                        <ContextMenu>
                                            <MenuItem Header="Mover para...">
                                                <MenuItem.Style>
                                                    <Style TargetType="MenuItem">
                                                        <EventSetter Event="Click" Handler="MoveMessage_Click"/>
                                                    </Style>
                                                </MenuItem.Style>
                                            </MenuItem>
                                        </ContextMenu>
                                    </Setter.Value>
                                </Setter>
                            </Style>
                        </ListView.ItemContainerStyle>
```

with:

```xml
                    <ListView x:Name="MessageListView"
                              ItemsSource="{Binding MessageList.Messages}"
                              SelectionChanged="MessageListView_SelectionChanged"
                              KeyDown="MessageListView_KeyDown"
                              VirtualizingPanel.IsVirtualizing="True"
                              VirtualizingPanel.VirtualizationMode="Recycling">
                        <ListView.ItemContainerStyle>
                            <Style TargetType="ListViewItem">
                                <Setter Property="Foreground" Value="{Binding RecoveredKind, Converter={StaticResource RecoveredKindToRowBrushConverter}}"/>
                                <Setter Property="ContextMenu">
                                    <Setter.Value>
                                        <ContextMenu>
                                            <MenuItem Header="Mover para...">
                                                <MenuItem.Style>
                                                    <Style TargetType="MenuItem">
                                                        <EventSetter Event="Click" Handler="MoveMessage_Click"/>
                                                    </Style>
                                                </MenuItem.Style>
                                            </MenuItem>
                                            <MenuItem Header="Apagar">
                                                <MenuItem.Style>
                                                    <Style TargetType="MenuItem">
                                                        <EventSetter Event="Click" Handler="DeleteMessage_Click"/>
                                                    </Style>
                                                </MenuItem.Style>
                                            </MenuItem>
                                        </ContextMenu>
                                    </Setter.Value>
                                </Setter>
                            </Style>
                        </ListView.ItemContainerStyle>
```

(Same `EventSetter`-per-`MenuItem` pattern already used for "Mover para..." — an inline `Click` attribute on a `MenuItem` declared inside a `Style.Setter.Value` doesn't get compile-time event-wiring in WPF; see the "mover mensagem — UI" plan's Task 4 correction note.)

- [ ] **Step 2: Add the click handler, key handler, and shared confirm-and-delete logic**

In `src/LEC.App/MainWindow.xaml.cs`, add `using System.Windows.Input;` to the top of the file, alongside the existing `using` directives:

```csharp
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LEC.App.Services;
using LEC.App.ViewModels;
using LEC.DbxFormat;
using LEC.MailStore;
using Microsoft.Win32;
```

Then add these three methods right after `MoveMessage_Click` (before `NavigateToRenderedMessage`):

```csharp

    private async void DeleteMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not MessageHeader header)
            return;

        await ConfirmAndDeleteAsync(header);
    }

    private async void MessageListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;
        if (MessageListView.SelectedItem is not MessageHeader header)
            return;

        e.Handled = true;
        await ConfirmAndDeleteAsync(header);
    }

    private async Task ConfirmAndDeleteAsync(MessageHeader header)
    {
        if (_selectedProfile is not { } profile)
            return;

        var result = MessageBox.Show(this, "Apagar esta mensagem?", "LEC — Apagar mensagem",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            await _viewModel.DeleteMessageAsync(profile, header);

            // The deleted message is now hidden from the folder currently on screen --
            // reload that folder's list so it disappears, and clear the reading pane in
            // case it was showing the message that just got deleted (same visual reset
            // MoveMessage_Click/CloseProfile_Click already use for "nothing selected").
            if (_selectedNode is { } node)
                await _viewModel.SelectFolderAsync(profile, node);

            NavigateToRenderedMessage(string.Empty);
            ImagesBlockedBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LEC — Não foi possível apagar a mensagem", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: builds with no errors.

Run: `dotnet test`
Expected: PASS — no regression in any project (this task adds no new automated tests, per the plan's testing strategy — only confirms nothing existing broke).

- [ ] **Step 4: Manual verification**

Run the app (`dotnet run --project src/LEC.App`) against a real or test profile directory and confirm:

1. Select a folder with at least one message. Right-click a message → "Apagar" appears in the context menu, alongside "Mover para...".
2. Clicking "Apagar" shows a Yes/No confirmation. Clicking "Não" (or closing the dialog) leaves the message in the list untouched.
3. Clicking "Sim" makes the message disappear from the list; if that message was open in the reading pane, the reading pane clears.
4. Select a different message (left-click to select, not right-click) and press the `Delete` key — same confirmation, same result as step 2/3.
5. Reopen the profile (close and reopen, or restart the app) — the deleted message stays gone.
6. Confirm a message deleted from one folder doesn't affect a message previously *moved* from a different folder (or the same folder) — the two features don't interfere with each other.

If any step fails, fix the underlying code before considering this task done — there is no automated safety net for this task beyond the build and the full test suite.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs
git commit -m "Wire delete-message UI: context menu item + Delete key on the message list"
```

---

## Self-Review Notes

- **Cobertura do design doc:** §2 (decisões de escopo: esconder direto sem lixeira, cache separado, gatilho por menu + tecla Delete, com confirmação, seleção múltipla fora de escopo) → refletido nos Global Constraints e em cada task. §3 (`DeletedMessageCache`/`DeleteMessageAsync`) → Tasks 1 e 2. §4 (`MainWindowViewModel.DeleteMessageAsync`) → Task 3. §5 (UI) → Task 4. §6 (erros/casos de borda) → tratados nos testes de idempotência (Task 1) e no `catch` genérico da Task 4. §7 (testes) → cada teste do design tem um `[Fact]` correspondente, incluindo o teste de independência entre os dois caches e o teste de purga.
- **Consistência de tipos:** `DeletedMessageCache` (Task 1) tem exatamente a mesma forma de `MovedMessageCache` (nomes trocados: `GetMovedOffsets`→`GetDeletedOffsets`, `MarkMoved`→`MarkDeleted`, tabela `moved_message`→`deleted_message`), consumida por `DbxStore.DeleteMessageAsync` (Task 2) do mesmo jeito que `MovedMessageCache` já é consumida por `HideMovedMessageAsync`. `IMailStore.DeleteMessageAsync`/`DbxStore.DeleteMessageAsync` (Task 2) têm a mesma assinatura em `FakeMailStore` (Task 3). `MainWindowViewModel.DeleteMessageAsync` (Task 3) usa exatamente os parâmetros já definidos.
- **Ordem de dependências:** Task 1 não depende de nada. Task 2 depende só da Task 1. Task 3 depende só da Task 2 (precisa de `DeleteMessageAsync` em `IMailStore` pra `FakeMailStore` compilar). Task 4 depende só da Task 3. Cadeia estritamente linear, mesma estrutura das duas fatias anteriores.
- **Fora de escopo confirmado:** nenhuma task implementa lixeira, undo, ou purga em dois estágios (`ARQUITETURA.md` §8.7); nenhuma task adiciona seleção múltipla; nenhuma task toca em `MoveMessageAsync`/`AddMessageAsync`/`CreateFolderAsync` além do ponto de purga já existente, que só ganha mais uma chamada seguindo o padrão já estabelecido.
