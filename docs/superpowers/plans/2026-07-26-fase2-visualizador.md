# Fase 2 — Visualizador — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A WPF three-panel shell (folder tree / message list / reading pane) that opens a real Outlook Express profile (a folder containing `Folders.dbx` plus the per-folder `.dbx` files) and lets the user browse and read real mail, built on top of the verified Fase 1 parser. "Espiar" only — no library, no import, no edit.

**Architecture:** A new `LEC.MailStore` library implements the `IMailStore` contract from `ARQUITETURA.md` §7.1 (`DbxStore`, wrapping Fase 1's `DbxFolderFile`/`DbxEmailFile`), keeping the UI ignorant of the `.dbx` format entirely. A new `LEC.App` WPF project (MVVM via `CommunityToolkit.Mvvm`) consumes only `IMailStore`. Message bodies are parsed with MimeKit and rendered in a WebView2 control with remote images blocked by default and links intercepted before opening.

**Tech Stack:** .NET 9 (`net9.0` for libraries, `net9.0-windows` for the WPF app), C#, WPF, `CommunityToolkit.Mvvm`, `MimeKit`, `Microsoft.Web.WebView2`, xUnit.

**Full design:** `docs/superpowers/specs/2026-07-26-fase2-visualizador-design.md` — read it once for context; this plan is the authoritative task breakdown and the design doc's decisions are restated here wherever a task needs them.

## Global Constraints

- Platform: Windows 10/11 x64, .NET 9. `LEC.MailStore` targets `net9.0` (portable); `LEC.App` targets `net9.0-windows` (WPF requirement).
- `IMailStore`'s shape is fixed exactly as `ARQUITETURA.md` §7.1 specifies: `Folders`, `EnumerateHeadersAsync`, `OpenMessageAsync`, `Health`. Do not add members to it in this plan.
- `StoreHealth` is reused from `LEC.DbxFormat` — never a duplicate enum.
- `PstStore` is out of scope for this plan (deferred to a future plan, once a real `.pst` exists to verify against).
- Opening a bare `.dbx` file without a profile is out of scope for this plan — only full-profile opening (`Folders.dbx` + backing files) is implemented.
- Orphan folders (a `ParentId` that doesn't resolve to any known folder) must never be dropped or crash tree construction — they become top-level nodes. This is not hypothetical: the real `BCP/Folders.dbx` has exactly one orphan (folder id 73, "seg", `ParentId = 53`, and no folder 53 exists).
- `-1` is the verified real sentinel Outlook Express writes for the account-root folder's own parent (folder id 0, "Outlook Express") — no folder has id `-1`. `DbxStore` maps this specific value to `null`; every other `ParentId` value (including a dangling one like `53`) is passed through as-is, and orphan resolution is the tree-builder's job, not `DbxStore`'s.
- `.dbx` files (and the profile directory) are read-only, always — this plan only ever calls `DbxFolderFile.Open`/`DbxEmailFile.Open` (Fase 1, already read-only/`FileShare.ReadWrite` by construction) and never writes into a profile directory.
- Links in a rendered message body never navigate directly — the destination is confirmed first.
- No automated test attempts to drive the actual WPF `Window`/XAML rendering or the WebView2 control's rendered output — those are verified manually by running the app. ViewModels (plain `ObservableObject`, no WPF dependency) and services (`MessageRenderer`, no WPF dependency) are unit-tested normally.

---

## File Structure

```
src/LEC.MailStore/LEC.MailStore.csproj
src/LEC.MailStore/SpecialFolderKind.cs      Task 2 — Inbox/Outbox/SentItems/DeletedItems/Drafts/None
src/LEC.MailStore/StoreFolder.cs            Task 2
src/LEC.MailStore/MessageHeader.cs          Task 2
src/LEC.MailStore/MessageRef.cs             Task 2
src/LEC.MailStore/IMailStore.cs             Task 2 — contract per ARQUITETURA.md §7.1
src/LEC.MailStore/DbxStore.cs               Tasks 4,5,6 — the IMailStore implementation

tests/LEC.MailStore.Tests/LEC.MailStore.Tests.csproj
tests/LEC.MailStore.Tests/TestProfileBuilder.cs   Task 3 — writes synthetic profiles to a temp dir
tests/LEC.MailStore.Tests/DbxStoreTests.cs        Tasks 4,5,6,7

src/LEC.App/LEC.App.csproj
src/LEC.App/App.xaml / App.xaml.cs                Task 8
src/LEC.App/MainWindow.xaml / MainWindow.xaml.cs   Tasks 8, 14, 15, 16
src/LEC.App/Converters/LoadableToBrushConverter.cs Task 14
src/LEC.App/ViewModels/FolderNodeViewModel.cs      Task 9
src/LEC.App/ViewModels/FolderTreeViewModel.cs      Task 9
src/LEC.App/ViewModels/MessageListViewModel.cs     Task 10
src/LEC.App/ViewModels/MessageReaderViewModel.cs   Task 12
src/LEC.App/ViewModels/MainWindowViewModel.cs      Task 13
src/LEC.App/Services/RenderedMessage.cs            Task 11
src/LEC.App/Services/MessageRenderer.cs            Task 11
src/LEC.App/Services/ImageBlockingWebView2Handler.cs Task 15

tests/LEC.App.Tests/LEC.App.Tests.csproj
tests/LEC.App.Tests/FakeMailStore.cs               Task 8
tests/LEC.App.Tests/FolderTreeViewModelTests.cs    Task 9
tests/LEC.App.Tests/MessageListViewModelTests.cs   Task 10
tests/LEC.App.Tests/MessageRendererTests.cs        Task 11
tests/LEC.App.Tests/MessageReaderViewModelTests.cs Task 12
tests/LEC.App.Tests/MainWindowViewModelTests.cs    Task 13
```

---

### Task 1: `LEC.MailStore` + test project scaffolding

**Files:**
- Create: `src/LEC.MailStore/LEC.MailStore.csproj`
- Create: `tests/LEC.MailStore.Tests/LEC.MailStore.Tests.csproj`

**Interfaces:**
- Produces: an empty `LEC.MailStore` class library referencing `LEC.DbxFormat`, added to `LEC.sln`, and an `LEC.MailStore.Tests` xUnit project referencing both `LEC.MailStore` and `LEC.DbxFormat.Tests` (the latter deliberately — Task 3 reuses Fase 1's already-verified `TestDbxBuilder` instead of re-deriving `.dbx` byte-layout logic a second time).

- [ ] **Step 1: Scaffold**

```bash
dotnet new classlib -n LEC.MailStore -o src/LEC.MailStore -f net9.0
dotnet new xunit -n LEC.MailStore.Tests -o tests/LEC.MailStore.Tests -f net9.0
dotnet sln add src/LEC.MailStore/LEC.MailStore.csproj tests/LEC.MailStore.Tests/LEC.MailStore.Tests.csproj
dotnet add src/LEC.MailStore/LEC.MailStore.csproj reference src/LEC.DbxFormat/LEC.DbxFormat.csproj
dotnet add tests/LEC.MailStore.Tests/LEC.MailStore.Tests.csproj reference src/LEC.MailStore/LEC.MailStore.csproj
dotnet add tests/LEC.MailStore.Tests/LEC.MailStore.Tests.csproj reference tests/LEC.DbxFormat.Tests/LEC.DbxFormat.Tests.csproj
rm src/LEC.MailStore/Class1.cs
rm tests/LEC.MailStore.Tests/UnitTest1.cs
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build`
Expected: `Build succeeded.` for all projects (no test files yet, that's fine).

- [ ] **Step 3: Commit**

```bash
git add LEC.sln src/LEC.MailStore tests/LEC.MailStore.Tests
git commit -m "Scaffold LEC.MailStore library and test project"
```

---

### Task 2: Data types and the `IMailStore` contract

**Files:**
- Create: `src/LEC.MailStore/SpecialFolderKind.cs`
- Create: `src/LEC.MailStore/StoreFolder.cs`
- Create: `src/LEC.MailStore/MessageHeader.cs`
- Create: `src/LEC.MailStore/MessageRef.cs`
- Create: `src/LEC.MailStore/IMailStore.cs`

**Interfaces:**
- Produces: every public type `DbxStore` (Tasks 4-6) and `LEC.App` (Tasks 9+) depend on. No tests in this task — these are plain data shapes exercised by later tasks' tests.

- [ ] **Step 1: Implement**

```csharp
// src/LEC.MailStore/SpecialFolderKind.cs
namespace LEC.MailStore;

/// <summary>
/// Store-agnostic special-folder classification — decoupled from
/// LEC.DbxFormat.DbxSpecialFolderType so a future PstStore can map its own
/// notion of special folders onto the same set without LEC.MailStore knowing
/// anything about either source format.
/// </summary>
public enum SpecialFolderKind
{
    None,
    Inbox,
    Outbox,
    SentItems,
    DeletedItems,
    Drafts,
}
```

```csharp
// src/LEC.MailStore/StoreFolder.cs
namespace LEC.MailStore;

public sealed class StoreFolder
{
    public required int Id { get; init; }

    /// <summary>
    /// Null means "no parent" (this is a root). A non-null value that doesn't
    /// match any other StoreFolder's Id is a real possibility (a dangling/orphan
    /// reference) — tree construction must tolerate it, not this type.
    /// </summary>
    public int? ParentId { get; init; }

    public required string Name { get; init; }
    public SpecialFolderKind SpecialFolderKind { get; init; }
    public int MessageCount { get; init; }

    /// <summary>
    /// Cheap, upfront signal (backing file exists on disk, or there is no
    /// backing file at all) — not a guarantee the file will actually parse
    /// successfully; EnumerateHeadersAsync degrades gracefully either way.
    /// </summary>
    public required bool IsLoadable { get; init; }
}
```

```csharp
// src/LEC.MailStore/MessageHeader.cs
using LEC.DbxFormat;

namespace LEC.MailStore;

public sealed class MessageHeader
{
    public required MessageRef Id { get; init; }
    public string? Subject { get; init; }
    public string? SenderName { get; init; }
    public string? SenderAddress { get; init; }
    public DateTime? Date { get; init; }
    public uint Size { get; init; }
    public string? MessageId { get; init; }
    public StoreHealth Health { get; init; }
}
```

```csharp
// src/LEC.MailStore/MessageRef.cs
namespace LEC.MailStore;

public sealed record MessageRef(int FolderId, int IndexNodeOffset);
```

```csharp
// src/LEC.MailStore/IMailStore.cs
using LEC.DbxFormat;

namespace LEC.MailStore;

/// <summary>Contract fixed by ARQUITETURA.md §7.1 — do not add members here.</summary>
public interface IMailStore : IDisposable
{
    IReadOnlyList<StoreFolder> Folders { get; }

    IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, CancellationToken ct = default);

    Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct = default);

    StoreHealth Health { get; }
}
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build src/LEC.MailStore`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/LEC.MailStore/SpecialFolderKind.cs src/LEC.MailStore/StoreFolder.cs src/LEC.MailStore/MessageHeader.cs src/LEC.MailStore/MessageRef.cs src/LEC.MailStore/IMailStore.cs
git commit -m "Add IMailStore contract and data types (StoreFolder, MessageHeader, MessageRef)"
```

---

### Task 3: `TestProfileBuilder` — synthetic on-disk profile helper for tests

**Files:**
- Create: `tests/LEC.MailStore.Tests/TestProfileBuilder.cs`

**Interfaces:**
- Consumes: `LEC.DbxFormat.Tests.TestDbxBuilder` (Fase 1, already verified — reused here rather than re-derived).
- Produces: `TestProfileBuilder : IDisposable` with `string ProfileDirectory`, `void WriteFoldersDbx(byte[])`, `void WriteEmailDbx(string fileName, byte[])`. Consumed by Tasks 4-7.

`DbxFolderFile.Open`/`DbxEmailFile.Open` take a file path (they memory-map real files) — unlike Fase 1's in-memory `ByteArrayDbxSource` tests, testing `DbxStore` means writing real files to a real temp directory and pointing `DbxStore.Open` at it.

- [ ] **Step 1: Implement**

```csharp
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
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build tests/LEC.MailStore.Tests`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add tests/LEC.MailStore.Tests/TestProfileBuilder.cs
git commit -m "Add TestProfileBuilder: synthetic on-disk profile helper for DbxStore tests"
```

---

### Task 4: `DbxStore.Open` — folder mapping, orphan sentinel, `IsLoadable`

**Files:**
- Create: `src/LEC.MailStore/DbxStore.cs`
- Test: `tests/LEC.MailStore.Tests/DbxStoreTests.cs`

**Interfaces:**
- Consumes: `DbxFolderFile`, `DbxFolderInfo`, `DbxSpecialFolderType`, `StoreHealth` (Fase 1); `TestDbxBuilder` (Fase 1 test helper); `TestProfileBuilder` (Task 3).
- Produces: `DbxStore : IMailStore` with `static DbxStore Open(string profileDirectoryPath)`, `IReadOnlyList<StoreFolder> Folders`, `StoreHealth Health`. `EnumerateHeadersAsync`/`OpenMessageAsync` are implemented in Tasks 5-6 (this task's `DbxStore.cs` includes stub bodies that throw `NotImplementedException` for those two members so the type compiles against `IMailStore`, replaced for real in Task 5/6).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LEC.MailStore.Tests/DbxStoreTests.cs
using System.Linq;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using LEC.MailStore;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreTests
{
    [Fact]
    public void OpensAProfileAndMapsFolders()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int localNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            // -1 cannot be encoded as a direct value (direct storage is a 24-bit payload,
            // per DbxFormat.AttributeValueMask) — this is exactly why the real file stores
            // this specific sentinel as a pointer-form 4-byte int rather than inline.
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Pastas locais\0"u8.ToArray() },
        });
        int inboxNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 2 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.MessageCount, Direct = true, DirectValue = 5 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });
        int root = localFolders.WriteIndexNode(
            nextTable: 0, ptrCount: 2, indexCount: 0,
            entries: new[] { (indexPtr: localNode, entryNextTable: 0, entryIndexCount: 0),
                              (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(DbxFormat.HeaderItemCountOffset, 2);
        localFolders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, root);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());
        // No Inbox.dbx written — deliberately, to also prove IsLoadable == false below.

        using var store = DbxStore.Open(profile.ProfileDirectory);

        Assert.Equal(2, store.Folders.Count);

        var pastasLocais = store.Folders.Single(f => f.Id == 1);
        Assert.Null(pastasLocais.ParentId); // -1 sentinel maps to null
        Assert.Equal("Pastas locais", pastasLocais.Name);
        Assert.True(pastasLocais.IsLoadable); // no backing file at all — trivially "loadable" (nothing to load)

        var inbox = store.Folders.Single(f => f.Id == 2);
        Assert.Equal(1, inbox.ParentId);
        Assert.Equal("Caixa de entrada", inbox.Name);
        Assert.Equal(SpecialFolderKind.Inbox, inbox.SpecialFolderKind);
        Assert.Equal(5, inbox.MessageCount);
        Assert.False(inbox.IsLoadable); // Inbox.dbx does not exist on disk
    }

    [Fact]
    public void PassesThroughADanglingParentIdUnresolved()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int orphanNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 73 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Direct = true, DirectValue = 53 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "seg\0"u8.ToArray() },
        });
        int root = localFolders.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: orphanNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, root);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());

        using var store = DbxStore.Open(profile.ProfileDirectory);

        var orphan = store.Folders.Single();
        Assert.Equal(53, orphan.ParentId); // passed through as-is, not resolved/dropped here
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter DbxStoreTests`
Expected: FAIL — `DbxStore` does not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.MailStore/DbxStore.cs
using System.Runtime.CompilerServices;
using LEC.DbxFormat;

namespace LEC.MailStore;

public sealed class DbxStore : IMailStore
{
    private readonly string _profileDirectory;
    private readonly Dictionary<int, string?> _backingFileNames;
    private readonly Dictionary<int, DbxEmailFile> _openFolders = new();
    private readonly Dictionary<int, Dictionary<MessageRef, DbxMessageInfo>> _messagesByFolder = new();

    public IReadOnlyList<StoreFolder> Folders { get; }
    public StoreHealth Health { get; }

    private DbxStore(string profileDirectory, IReadOnlyList<StoreFolder> folders, Dictionary<int, string?> backingFileNames, StoreHealth health)
    {
        _profileDirectory = profileDirectory;
        Folders = folders;
        _backingFileNames = backingFileNames;
        Health = health;
    }

    public static DbxStore Open(string profileDirectoryPath)
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
            });
            backingFileNames[f.FolderId] = fileName;
        }

        var health = folderFile.Health; // read before the `using` disposes folderFile below
        return new DbxStore(profileDirectoryPath, folders, backingFileNames, health);
    }

    private static SpecialFolderKind ToSpecialFolderKind(DbxSpecialFolderType type) => type switch
    {
        DbxSpecialFolderType.Inbox => SpecialFolderKind.Inbox,
        DbxSpecialFolderType.Outbox => SpecialFolderKind.Outbox,
        DbxSpecialFolderType.SentItems => SpecialFolderKind.SentItems,
        DbxSpecialFolderType.DeletedItems => SpecialFolderKind.DeletedItems,
        DbxSpecialFolderType.Drafts => SpecialFolderKind.Drafts,
        _ => SpecialFolderKind.None,
    };

    public IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, [EnumeratorCancellation] CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 5.");

    public Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 6.");

    public void Dispose()
    {
        foreach (var file in _openFolders.Values)
            file.Dispose();
        _openFolders.Clear();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter DbxStoreTests`
Expected: PASS (2 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreTests.cs
git commit -m "Add DbxStore.Open: folder mapping with verified -1 root sentinel and orphan pass-through"
```

---

### Task 5: `DbxStore.EnumerateHeadersAsync` — lazy per-folder open with degradation

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Modify: `tests/LEC.MailStore.Tests/DbxStoreTests.cs`

**Interfaces:**
- Consumes: `DbxEmailFile.Open` (Fase 1, throws `DbxFormatException`/`IOException`/`UnauthorizedAccessException` on failure), `DbxMessageInfo` (Fase 1).
- Produces: working `EnumerateHeadersAsync` on `DbxStore`. Consumed by Task 6 (shares the same opened-folder cache) and by `LEC.App`'s `MessageListViewModel` (Task 10).

- [ ] **Step 1: Write the failing tests**

Append to `tests/LEC.MailStore.Tests/DbxStoreTests.cs`:

```csharp
    [Fact]
    public async Task EnumeratesHeadersForALoadableFolder()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int inboxNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
        });
        int foldersRoot = localFolders.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, foldersRoot);

        var inboxMessages = new TestDbxBuilder();
        inboxMessages.WriteHeader(DbxFileType.Email);
        int messageInfoNode = inboxMessages.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = "Ola\0"u8.ToArray() },
        });
        int messagesRoot = inboxMessages.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: messageInfoNode, entryNextTable: 0, entryIndexCount: 0) });
        inboxMessages.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        inboxMessages.SetInt32At(DbxFormat.HeaderIndexPointerOffset, messagesRoot);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());
        profile.WriteEmailDbx("Inbox.dbx", inboxMessages.ToArray());

        using var store = DbxStore.Open(profile.ProfileDirectory);
        var folder = store.Folders.Single();
        Assert.True(folder.IsLoadable);

        var headers = new List<MessageHeader>();
        await foreach (var header in store.EnumerateHeadersAsync(folder))
            headers.Add(header);

        Assert.Single(headers);
        Assert.Equal("Ola", headers[0].Subject);
        Assert.Equal(folder.Id, headers[0].Id.FolderId);
    }

    [Fact]
    public async Task EnumerateHeadersReturnsEmptyForAFolderWithNoBackingFile()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int node = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Pastas locais\0"u8.ToArray() },
        });
        int root = localFolders.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: node, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, root);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());

        using var store = DbxStore.Open(profile.ProfileDirectory);
        var folder = store.Folders.Single();

        var headers = new List<MessageHeader>();
        await foreach (var header in store.EnumerateHeadersAsync(folder))
            headers.Add(header);

        Assert.Empty(headers);
    }

    [Fact]
    public async Task EnumerateHeadersReturnsEmptyWhenTheBackingFileExistsButIsNotAValidDbx()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int node = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Quebrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Quebrada.dbx\0"u8.ToArray() },
        });
        int root = localFolders.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: node, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, root);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());
        profile.WriteEmailDbx("Quebrada.dbx", new byte[64]); // exists, but not a valid .dbx signature

        using var store = DbxStore.Open(profile.ProfileDirectory);
        var folder = store.Folders.Single();
        Assert.True(folder.IsLoadable); // file exists — the cheap upfront check can't know it's garbage

        var headers = new List<MessageHeader>();
        await foreach (var header in store.EnumerateHeadersAsync(folder))
            headers.Add(header);

        Assert.Empty(headers); // degrades silently rather than throwing
    }
```

Add `using System.Threading.Tasks;` and `using System.Collections.Generic;` to the test file's `using` block if not already present via implicit usings (the `xunit` template enables `ImplicitUsings`, so these are almost certainly already implicit — no action needed, but verify the file compiles).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter DbxStoreTests`
Expected: FAIL — `EnumerateHeadersAsync` throws `NotImplementedException`.

- [ ] **Step 3: Implement**

Replace the `EnumerateHeadersAsync` stub in `src/LEC.MailStore/DbxStore.cs` with:

```csharp
    public async IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
            yield break;

        if (!_openFolders.TryGetValue(folder.Id, out var emailFile))
        {
            var opened = TryOpenFolderFile(fileName);
            if (opened is null)
                yield break;

            emailFile = opened;
            _openFolders[folder.Id] = emailFile;
        }

        if (!_messagesByFolder.TryGetValue(folder.Id, out var infoByRef))
        {
            infoByRef = new Dictionary<MessageRef, DbxMessageInfo>();
            _messagesByFolder[folder.Id] = infoByRef;
        }

        foreach (var info in emailFile.Messages)
        {
            ct.ThrowIfCancellationRequested();
            var reference = new MessageRef(folder.Id, info.IndexNodeOffset);
            infoByRef[reference] = info;

            yield return new MessageHeader
            {
                Id = reference,
                Subject = info.Subject,
                SenderName = info.SenderName,
                SenderAddress = info.SenderAddress,
                Date = info.SendTimeUtc ?? info.ReceiveTimeUtc,
                Size = info.MessageSize,
                MessageId = info.MessageId,
                Health = info.HasOutOfRangeAttribute ? StoreHealth.Partial : StoreHealth.Ok,
            };
        }

        await Task.CompletedTask; // keeps this an async iterator per IMailStore's contract
    }

    private DbxEmailFile? TryOpenFolderFile(string fileName)
    {
        string path = Path.Combine(_profileDirectory, fileName);
        try
        {
            return DbxEmailFile.Open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbxFormatException)
        {
            return null;
        }
    }
```

(`yield` cannot appear inside a `try`/`catch` block in C#, hence the fallible open is isolated in `TryOpenFolderFile`, a plain non-iterator method, and the iterator only branches on its `null`/non-`null` result.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter DbxStoreTests`
Expected: PASS (5 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreTests.cs
git commit -m "Add DbxStore.EnumerateHeadersAsync: lazy per-folder open with silent degradation"
```

---

### Task 6: `DbxStore.OpenMessageAsync`

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Modify: `tests/LEC.MailStore.Tests/DbxStoreTests.cs`

**Interfaces:**
- Consumes: `DbxEmailFile.ReadMessageRaw` (Fase 1).
- Produces: working `OpenMessageAsync` on `DbxStore`. Consumed by `LEC.App`'s `MessageReaderViewModel` (Task 12).

- [ ] **Step 1: Write the failing test**

Append to `tests/LEC.MailStore.Tests/DbxStoreTests.cs`:

```csharp
    [Fact]
    public async Task OpenMessageAsyncReturnsTheRawReassembledBytes()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int inboxNode = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
        });
        int foldersRoot = localFolders.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, foldersRoot);

        var inboxMessages = new TestDbxBuilder();
        inboxMessages.WriteHeader(DbxFileType.Email);
        byte[] body = "From: a@b.com\r\nSubject: Ola\r\n\r\nCorpo"u8.ToArray();
        int blockOffset = inboxMessages.WriteMessageBlock(body, nextOffset: 0);
        int messageInfoNode = inboxMessages.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)blockOffset },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = "Ola\0"u8.ToArray() },
        });
        int messagesRoot = inboxMessages.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: messageInfoNode, entryNextTable: 0, entryIndexCount: 0) });
        inboxMessages.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        inboxMessages.SetInt32At(DbxFormat.HeaderIndexPointerOffset, messagesRoot);

        using var profile = new TestProfileBuilder();
        profile.WriteFoldersDbx(localFolders.ToArray());
        profile.WriteEmailDbx("Inbox.dbx", inboxMessages.ToArray());

        using var store = DbxStore.Open(profile.ProfileDirectory);
        var folder = store.Folders.Single();

        MessageHeader? header = null;
        await foreach (var h in store.EnumerateHeadersAsync(folder))
            header = h;
        Assert.NotNull(header);

        await using var stream = await store.OpenMessageAsync(header!.Id);
        using var reader = new StreamReader(stream);
        string raw = await reader.ReadToEndAsync();

        Assert.Equal("From: a@b.com\r\nSubject: Ola\r\n\r\nCorpo", raw);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.MailStore.Tests --filter DbxStoreTests`
Expected: FAIL — `OpenMessageAsync` throws `NotImplementedException`.

- [ ] **Step 3: Implement**

Replace the `OpenMessageAsync` stub in `src/LEC.MailStore/DbxStore.cs` with:

```csharp
    public Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        if (!_messagesByFolder.TryGetValue(id.FolderId, out var infoByRef) ||
            !infoByRef.TryGetValue(id, out var info) ||
            !_openFolders.TryGetValue(id.FolderId, out var emailFile))
        {
            throw new InvalidOperationException(
                $"Message {id} was not found. EnumerateHeadersAsync must be called for its folder before OpenMessageAsync.");
        }

        byte[] raw = emailFile.ReadMessageRaw(info, out _);
        return Task.FromResult<Stream>(new MemoryStream(raw));
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LEC.MailStore.Tests --filter DbxStoreTests`
Expected: PASS (6 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreTests.cs
git commit -m "Add DbxStore.OpenMessageAsync: raw message bytes on demand"
```

---

### Task 7: Real-fixture test against the actual `BCP/` profile

**Files:**
- Modify: `tests/LEC.MailStore.Tests/DbxStoreTests.cs`

**Interfaces:**
- Consumes: `DbxStore` (Tasks 4-6).
- Produces: one guarded integration test proving the whole stack (Fase 1 + `DbxStore`) works against the user's real profile, including the known real orphan.

- [ ] **Step 1: Write and run the test**

Append to `tests/LEC.MailStore.Tests/DbxStoreTests.cs`:

```csharp
    private static string? FindRealFixtureDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LEC.sln")))
            dir = dir.Parent;

        if (dir is null)
            return null;

        string candidate = Path.Combine(dir.FullName, "BCP");
        return Directory.Exists(candidate) ? candidate : null;
    }

    [Fact]
    public void RealFixture_OpensTheRealProfileAndFindsTheKnownOrphan()
    {
        string? profileDir = FindRealFixtureDirectory();
        if (profileDir is null)
            return; // BCP/ not present on this machine — soft skip

        using var store = DbxStore.Open(profileDir);

        // Independently verified in Fase 1: BCP/Folders.dbx has exactly 26 entries.
        Assert.Equal(26, store.Folders.Count);

        var orphan = store.Folders.Single(f => f.Id == 73);
        Assert.Equal("seg", orphan.Name);
        Assert.Equal(53, orphan.ParentId);
        Assert.DoesNotContain(store.Folders, f => f.Id == 53); // confirms it's genuinely dangling

        var inbox = store.Folders.Single(f => f.SpecialFolderKind == SpecialFolderKind.Inbox);
        Assert.Equal("Caixa de entrada", inbox.Name);
        Assert.True(inbox.IsLoadable);
    }
```

Run: `dotnet test tests/LEC.MailStore.Tests --filter DbxStoreTests`
Expected: PASS (7 tests) — the real-fixture test runs for real since `BCP/` exists on this machine.

- [ ] **Step 2: Commit**

```bash
git add tests/LEC.MailStore.Tests/DbxStoreTests.cs
git commit -m "Add DbxStore real-fixture test confirming the known orphan folder and Inbox mapping"
```

---

### Task 8: `LEC.App` WPF scaffolding

**Files:**
- Create: `src/LEC.App/LEC.App.csproj`
- Create: `src/LEC.App/App.xaml`, `src/LEC.App/App.xaml.cs`
- Create: `src/LEC.App/MainWindow.xaml`, `src/LEC.App/MainWindow.xaml.cs`
- Create: `tests/LEC.App.Tests/LEC.App.Tests.csproj`
- Create: `tests/LEC.App.Tests/FakeMailStore.cs`

**Interfaces:**
- Produces: a WPF app that builds and launches (empty shell, replaced incrementally by Tasks 9-16), and `LEC.App.Tests`, which will host every ViewModel/service test from Tasks 9-13. `FakeMailStore` is a hand-written in-memory `IMailStore` test double so ViewModel tests never need real `.dbx` files.

- [ ] **Step 1: Scaffold**

```bash
dotnet new wpf -n LEC.App -o src/LEC.App -f net9.0-windows
dotnet new xunit -n LEC.App.Tests -o tests/LEC.App.Tests -f net9.0-windows
dotnet sln add src/LEC.App/LEC.App.csproj tests/LEC.App.Tests/LEC.App.Tests.csproj
dotnet add src/LEC.App/LEC.App.csproj reference src/LEC.MailStore/LEC.MailStore.csproj
dotnet add src/LEC.App/LEC.App.csproj package CommunityToolkit.Mvvm
dotnet add src/LEC.App/LEC.App.csproj package MimeKit
dotnet add src/LEC.App/LEC.App.csproj package Microsoft.Web.WebView2
dotnet add tests/LEC.App.Tests/LEC.App.Tests.csproj reference src/LEC.App/LEC.App.csproj
rm tests/LEC.App.Tests/UnitTest1.cs
```

- [ ] **Step 2: Replace the generated `MainWindow.xaml` with a minimal placeholder**

```xml
<!-- src/LEC.App/MainWindow.xaml -->
<Window x:Class="LEC.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="LEC — Local Email Client" Height="720" Width="1100">
    <Grid>
        <TextBlock Text="LEC" HorizontalAlignment="Center" VerticalAlignment="Center" FontSize="24"/>
    </Grid>
</Window>
```

```csharp
// src/LEC.App/MainWindow.xaml.cs
namespace LEC.App;

public partial class MainWindow : System.Windows.Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```

Leave `App.xaml`/`App.xaml.cs` as the template generated them (default `StartupUri="MainWindow.xaml"` is correct).

- [ ] **Step 3: Add the `FakeMailStore` test double**

```csharp
// tests/LEC.App.Tests/FakeMailStore.cs
using System.Runtime.CompilerServices;
using LEC.DbxFormat;
using LEC.MailStore;

namespace LEC.App.Tests;

/// <summary>In-memory IMailStore test double — no .dbx files involved.</summary>
internal sealed class FakeMailStore : IMailStore
{
    private readonly Dictionary<int, List<MessageHeader>> _headersByFolder;
    private readonly Dictionary<MessageRef, byte[]> _rawByRef;

    public FakeMailStore(
        IReadOnlyList<StoreFolder> folders,
        Dictionary<int, List<MessageHeader>>? headersByFolder = null,
        Dictionary<MessageRef, byte[]>? rawByRef = null,
        StoreHealth health = StoreHealth.Ok)
    {
        Folders = folders;
        _headersByFolder = headersByFolder ?? new Dictionary<int, List<MessageHeader>>();
        _rawByRef = rawByRef ?? new Dictionary<MessageRef, byte[]>();
        Health = health;
    }

    public IReadOnlyList<StoreFolder> Folders { get; }
    public StoreHealth Health { get; }

    public async IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_headersByFolder.TryGetValue(folder.Id, out var headers))
            foreach (var h in headers)
                yield return h;

        await Task.CompletedTask;
    }

    public Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        if (!_rawByRef.TryGetValue(id, out var raw))
            throw new InvalidOperationException($"No raw bytes registered for {id}.");

        return Task.FromResult<Stream>(new MemoryStream(raw));
    }

    public void Dispose() { }
}
```

- [ ] **Step 4: Verify the app builds and launches**

Run: `dotnet build`
Expected: `Build succeeded.` for all five projects.

Run: `dotnet run --project src/LEC.App` (launches a real window on this machine — let it sit for a couple of seconds, confirm no exception in the console, then close the window).
Expected: a window titled "LEC — Local Email Client" appears with centered "LEC" text; closing it ends the process cleanly with no unhandled-exception output.

Run: `dotnet test tests/LEC.App.Tests`
Expected: `0` tests found (no test files yet) — not a failure, just nothing to run.

- [ ] **Step 5: Commit**

```bash
git add LEC.sln src/LEC.App tests/LEC.App.Tests
git commit -m "Scaffold LEC.App WPF project and LEC.App.Tests, with FakeMailStore test double"
```

---

### Task 9: `FolderTreeViewModel` — orphan-tolerant tree construction

**Files:**
- Create: `src/LEC.App/ViewModels/FolderNodeViewModel.cs`
- Create: `src/LEC.App/ViewModels/FolderTreeViewModel.cs`
- Test: `tests/LEC.App.Tests/FolderTreeViewModelTests.cs`

**Interfaces:**
- Consumes: `StoreFolder` (`LEC.MailStore`).
- Produces: `FolderNodeViewModel { StoreFolder Folder, ObservableCollection<FolderNodeViewModel> Children, string DisplayName, bool IsLoadable, bool IsSpecial }`, `FolderTreeViewModel { ObservableCollection<FolderNodeViewModel> RootNodes, void Build(IReadOnlyList<StoreFolder>) }`. Consumed by Task 13 (`MainWindowViewModel`) and Task 14 (XAML binding — `IsSpecial` drives the bold styling that stands in for per-type icons, see Task 14).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LEC.App.Tests/FolderTreeViewModelTests.cs
using System.Linq;
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class FolderTreeViewModelTests
{
    private static StoreFolder Folder(int id, int? parentId, string name) => new()
    {
        Id = id,
        ParentId = parentId,
        Name = name,
        IsLoadable = true,
    };

    [Fact]
    public void BuildsANestedHierarchyFromAFlatList()
    {
        var folders = new[]
        {
            Folder(1, null, "Pastas locais"),
            Folder(2, 1, "Caixa de entrada"),
            Folder(3, 2, "Sub-pasta"),
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        var root = Assert.Single(vm.RootNodes);
        Assert.Equal("Pastas locais", root.DisplayName);
        var inbox = Assert.Single(root.Children);
        Assert.Equal("Caixa de entrada", inbox.DisplayName);
        var sub = Assert.Single(inbox.Children);
        Assert.Equal("Sub-pasta", sub.DisplayName);
    }

    [Fact]
    public void TreatsADanglingParentIdAsATopLevelNodeInsteadOfDroppingOrThrowing()
    {
        // Mirrors the real BCP/Folders.dbx: folder 73 ("seg") has ParentId = 53,
        // and no folder 53 exists.
        var folders = new[]
        {
            Folder(1, null, "Pastas locais"),
            Folder(73, 53, "seg"),
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        Assert.Equal(2, vm.RootNodes.Count);
        Assert.Contains(vm.RootNodes, n => n.DisplayName == "seg");
    }

    [Fact]
    public void FlagsSpecialFoldersButNotOrdinaryOnes()
    {
        var inbox = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, SpecialFolderKind = SpecialFolderKind.Inbox };
        var ordinary = new StoreFolder { Id = 2, Name = "Enviados 2004", IsLoadable = true };

        var vm = new FolderTreeViewModel();
        vm.Build(new[] { inbox, ordinary });

        Assert.True(vm.RootNodes.Single(n => n.DisplayName == "Caixa de entrada").IsSpecial);
        Assert.False(vm.RootNodes.Single(n => n.DisplayName == "Enviados 2004").IsSpecial);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.App.Tests --filter FolderTreeViewModelTests`
Expected: FAIL — `FolderNodeViewModel`/`FolderTreeViewModel` do not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.App/ViewModels/FolderNodeViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class FolderNodeViewModel : ObservableObject
{
    public StoreFolder Folder { get; }
    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    public FolderNodeViewModel(StoreFolder folder) => Folder = folder;

    public string DisplayName => Folder.Name;
    public bool IsLoadable => Folder.IsLoadable;
    public bool IsSpecial => Folder.SpecialFolderKind != SpecialFolderKind.None;
}
```

```csharp
// src/LEC.App/ViewModels/FolderTreeViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class FolderTreeViewModel : ObservableObject
{
    public ObservableCollection<FolderNodeViewModel> RootNodes { get; } = new();

    public void Build(IReadOnlyList<StoreFolder> folders)
    {
        RootNodes.Clear();

        var nodesById = new Dictionary<int, FolderNodeViewModel>();
        foreach (var folder in folders)
            nodesById[folder.Id] = new FolderNodeViewModel(folder);

        foreach (var folder in folders)
        {
            var node = nodesById[folder.Id];

            // A ParentId that doesn't resolve to a known folder (root sentinel already
            // mapped to null by DbxStore, or a genuine dangling reference like the real
            // profile's folder 73 -> 53) becomes a top-level node.
            if (folder.ParentId is int parentId && nodesById.TryGetValue(parentId, out var parentNode))
                parentNode.Children.Add(node);
            else
                RootNodes.Add(node);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LEC.App.Tests --filter FolderTreeViewModelTests`
Expected: PASS (2 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/FolderNodeViewModel.cs src/LEC.App/ViewModels/FolderTreeViewModel.cs tests/LEC.App.Tests/FolderTreeViewModelTests.cs
git commit -m "Add FolderTreeViewModel: orphan-tolerant tree construction from a flat folder list"
```

---

### Task 10: `MessageListViewModel`

**Files:**
- Create: `src/LEC.App/ViewModels/MessageListViewModel.cs`
- Test: `tests/LEC.App.Tests/MessageListViewModelTests.cs`

**Interfaces:**
- Consumes: `IMailStore`, `StoreFolder`, `MessageHeader` (`LEC.MailStore`); `FakeMailStore` (Task 8, tests only).
- Produces: `MessageListViewModel { ObservableCollection<MessageHeader> Messages, string? StatusText, Task LoadAsync(StoreFolder, CancellationToken) }`. Consumed by Task 13/14.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LEC.App.Tests/MessageListViewModelTests.cs
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class MessageListViewModelTests
{
    [Fact]
    public async Task LoadsHeadersFromTheStoreForTheSelectedFolder()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true };
        var header = new MessageHeader { Id = new MessageRef(1, 100), Subject = "Ola" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { header } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        Assert.Single(vm.Messages);
        Assert.Equal("Ola", vm.Messages[0].Subject);
        Assert.Null(vm.StatusText);
    }

    [Fact]
    public async Task SetsAStatusMessageForANonLoadableFolder()
    {
        var folder = new StoreFolder { Id = 1, Name = "Quebrada", IsLoadable = false };
        var store = new FakeMailStore(folders: new[] { folder });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        Assert.Empty(vm.Messages);
        Assert.NotNull(vm.StatusText);
    }

    [Fact]
    public async Task ClearsPreviousMessagesWhenLoadingADifferentFolder()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true };
        var store = new FakeMailStore(
            folders: new[] { folderA, folderB },
            headersByFolder: new()
            {
                [1] = new() { new MessageHeader { Id = new MessageRef(1, 1), Subject = "Da A" } },
            });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folderA);
        Assert.Single(vm.Messages);

        await vm.LoadAsync(folderB);
        Assert.Empty(vm.Messages);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter MessageListViewModelTests`
Expected: FAIL — `MessageListViewModel` does not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.App/ViewModels/MessageListViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class MessageListViewModel : ObservableObject
{
    private readonly IMailStore _store;

    public ObservableCollection<MessageHeader> Messages { get; } = new();

    [ObservableProperty]
    private string? _statusText;

    public MessageListViewModel(IMailStore store) => _store = store;

    public async Task LoadAsync(StoreFolder folder, CancellationToken ct = default)
    {
        Messages.Clear();
        StatusText = folder.IsLoadable ? null : "Não foi possível carregar esta pasta.";

        await foreach (var header in _store.EnumerateHeadersAsync(folder, ct))
            Messages.Add(header);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter MessageListViewModelTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/MessageListViewModel.cs tests/LEC.App.Tests/MessageListViewModelTests.cs
git commit -m "Add MessageListViewModel: loads headers for the selected folder"
```

---

### Task 11: `MessageRenderer` — MIME parsing to displayable HTML

**Files:**
- Create: `src/LEC.App/Services/RenderedMessage.cs`
- Create: `src/LEC.App/Services/MessageRenderer.cs`
- Test: `tests/LEC.App.Tests/MessageRendererTests.cs`

**Interfaces:**
- Consumes: `MimeKit.MimeMessage`.
- Produces: `RenderedMessage(string? Subject, string? FromDisplay, DateTimeOffset? Date, string Html)`, `MessageRenderer.RenderAsync(Stream, CancellationToken) : Task<RenderedMessage>`. Consumed by Task 12. Pure MIME parsing only — no WPF, no WebView2, no image blocking (that's Task 15, tied to the live `CoreWebView2` control and verified manually).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LEC.App.Tests/MessageRendererTests.cs
using System.Text;
using LEC.App.Services;
using Xunit;

namespace LEC.App.Tests;

public class MessageRendererTests
{
    private static Stream RawMessage(string raw) => new MemoryStream(Encoding.ASCII.GetBytes(raw));

    [Fact]
    public async Task RendersTheHtmlBodyWhenPresent()
    {
        string raw =
            "From: \"Ana\" <ana@example.com>\r\n" +
            "Subject: Ola\r\n" +
            "Date: Fri, 21 Oct 2005 11:11:49 -0300\r\n" +
            "Content-Type: text/html\r\n" +
            "\r\n" +
            "<p>Corpo em HTML</p>";

        var rendered = await MessageRenderer.RenderAsync(RawMessage(raw));

        Assert.Equal("Ola", rendered.Subject);
        Assert.Equal("Ana <ana@example.com>", rendered.FromDisplay);
        Assert.Contains("Corpo em HTML", rendered.Html);
    }

    [Fact]
    public async Task FallsBackToPreformattedTextWhenThereIsNoHtmlBody()
    {
        string raw =
            "From: ana@example.com\r\n" +
            "Subject: So texto\r\n" +
            "\r\n" +
            "Corpo em texto puro";

        var rendered = await MessageRenderer.RenderAsync(RawMessage(raw));

        Assert.Contains("<pre>", rendered.Html);
        Assert.Contains("Corpo em texto puro", rendered.Html);
    }

    [Fact]
    public async Task EscapesHtmlSpecialCharactersInThePlainTextFallback()
    {
        string raw =
            "From: ana@example.com\r\n" +
            "Subject: Perigoso\r\n" +
            "\r\n" +
            "<script>alert(1)</script>";

        var rendered = await MessageRenderer.RenderAsync(RawMessage(raw));

        Assert.DoesNotContain("<script>", rendered.Html);
        Assert.Contains("&lt;script&gt;", rendered.Html);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter MessageRendererTests`
Expected: FAIL — `MessageRenderer`/`RenderedMessage` do not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.App/Services/RenderedMessage.cs
namespace LEC.App.Services;

public sealed record RenderedMessage(string? Subject, string? FromDisplay, DateTimeOffset? Date, string Html);
```

```csharp
// src/LEC.App/Services/MessageRenderer.cs
using System.Net;
using MimeKit;

namespace LEC.App.Services;

public static class MessageRenderer
{
    public static async Task<RenderedMessage> RenderAsync(Stream rawMessage, CancellationToken ct = default)
    {
        var message = await MimeMessage.LoadAsync(rawMessage, ct);

        string html = message.HtmlBody
            ?? $"<pre>{WebUtility.HtmlEncode(message.TextBody ?? string.Empty)}</pre>";

        string? fromDisplay = message.From.Mailboxes.FirstOrDefault() is { } mailbox
            ? (string.IsNullOrEmpty(mailbox.Name) ? mailbox.Address : $"{mailbox.Name} <{mailbox.Address}>")
            : null;

        return new RenderedMessage(message.Subject, fromDisplay, message.Date, html);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter MessageRendererTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/Services/RenderedMessage.cs src/LEC.App/Services/MessageRenderer.cs tests/LEC.App.Tests/MessageRendererTests.cs
git commit -m "Add MessageRenderer: MimeKit parsing to displayable HTML with plain-text fallback"
```

---

### Task 12: `MessageReaderViewModel`

**Files:**
- Create: `src/LEC.App/ViewModels/MessageReaderViewModel.cs`
- Test: `tests/LEC.App.Tests/MessageReaderViewModelTests.cs`

**Interfaces:**
- Consumes: `IMailStore.OpenMessageAsync` (`LEC.MailStore`), `MessageRenderer` (Task 11).
- Produces: `MessageReaderViewModel { RenderedMessage? Current, Task LoadAsync(MessageRef, CancellationToken) }`. Consumed by Task 13/14.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LEC.App.Tests/MessageReaderViewModelTests.cs
using System.Text;
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class MessageReaderViewModelTests
{
    [Fact]
    public async Task LoadsAndRendersTheSelectedMessage()
    {
        var reference = new MessageRef(1, 100);
        byte[] raw = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Ola\r\n\r\nCorpo");
        var store = new FakeMailStore(
            folders: Array.Empty<StoreFolder>(),
            rawByRef: new() { [reference] = raw });

        var vm = new MessageReaderViewModel(store);
        await vm.LoadAsync(reference);

        Assert.NotNull(vm.Current);
        Assert.Equal("Ola", vm.Current!.Subject);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.App.Tests --filter MessageReaderViewModelTests`
Expected: FAIL — `MessageReaderViewModel` does not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.App/ViewModels/MessageReaderViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.App.Services;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class MessageReaderViewModel : ObservableObject
{
    private readonly IMailStore _store;

    [ObservableProperty]
    private RenderedMessage? _current;

    public MessageReaderViewModel(IMailStore store) => _store = store;

    public async Task LoadAsync(MessageRef id, CancellationToken ct = default)
    {
        var stream = await _store.OpenMessageAsync(id, ct);
        await using (stream)
        {
            Current = await MessageRenderer.RenderAsync(stream, ct);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LEC.App.Tests --filter MessageReaderViewModelTests`
Expected: PASS (1 test)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/MessageReaderViewModel.cs tests/LEC.App.Tests/MessageReaderViewModelTests.cs
git commit -m "Add MessageReaderViewModel: opens and renders the selected message"
```

---

### Task 13: `MainWindowViewModel` — ties the three panels together

**Files:**
- Create: `src/LEC.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/LEC.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `DbxStore.Open` (`LEC.MailStore`), `FolderTreeViewModel` (Task 9), `MessageListViewModel` (Task 10), `MessageReaderViewModel` (Task 12).
- Produces: `MainWindowViewModel { IMailStore? Store, FolderTreeViewModel FolderTree, MessageListViewModel? MessageList, MessageReaderViewModel? MessageReader, string? StatusText, void OpenProfile(string), Task SelectFolderAsync(FolderNodeViewModel), Task SelectMessageAsync(MessageHeader) }`. Consumed by Task 14 (XAML/code-behind).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LEC.App.Tests/MainWindowViewModelTests.cs
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class MainWindowViewModelTests
{
    [Fact]
    public void OpenProfileWithANonExistentPathSetsAStatusMessageInsteadOfThrowing()
    {
        var vm = new MainWindowViewModel();

        vm.OpenProfile(Path.Combine(Path.GetTempPath(), $"lec-does-not-exist-{Guid.NewGuid():N}"));

        Assert.Null(vm.Store);
        Assert.NotNull(vm.StatusText);
    }

    [Fact]
    public async Task SelectFolderAsyncIsANoOpWhenNoProfileIsOpenYet()
    {
        var vm = new MainWindowViewModel();
        var node = new FolderNodeViewModel(new StoreFolder { Id = 1, Name = "X", IsLoadable = true });

        await vm.SelectFolderAsync(node); // must not throw

        Assert.Null(vm.MessageList);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter MainWindowViewModelTests`
Expected: FAIL — `MainWindowViewModel` does not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.App/ViewModels/MainWindowViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.DbxFormat;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private IMailStore? _store;

    [ObservableProperty]
    private string? _statusText;

    public FolderTreeViewModel FolderTree { get; } = new();

    public MessageListViewModel? MessageList { get; private set; }
    public MessageReaderViewModel? MessageReader { get; private set; }

    public void OpenProfile(string profileDirectory)
    {
        try
        {
            var newStore = DbxStore.Open(profileDirectory);

            Store?.Dispose();
            Store = newStore;
            FolderTree.Build(Store.Folders);
            MessageList = new MessageListViewModel(Store);
            MessageReader = new MessageReaderViewModel(Store);
            StatusText = Store.Health == StoreHealth.Ok
                ? null
                : $"Perfil aberto com avisos (Health: {Store.Health}).";
        }
        catch (Exception ex) when (ex is DbxFormatException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Não foi possível abrir o perfil: {ex.Message}";
        }
    }

    public async Task SelectFolderAsync(FolderNodeViewModel node, CancellationToken ct = default)
    {
        if (MessageList is not null)
            await MessageList.LoadAsync(node.Folder, ct);
    }

    public async Task SelectMessageAsync(MessageHeader header, CancellationToken ct = default)
    {
        if (MessageReader is not null)
            await MessageReader.LoadAsync(header.Id, ct);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter MainWindowViewModelTests`
Expected: PASS (2 tests)

- [ ] **Step 5: Run the full test suite so far**

Run: `dotnet test`
Expected: all tests across `LEC.DbxFormat.Tests`, `LEC.MailStore.Tests`, and `LEC.App.Tests` pass.

- [ ] **Step 6: Commit**

```bash
git add src/LEC.App/ViewModels/MainWindowViewModel.cs tests/LEC.App.Tests/MainWindowViewModelTests.cs
git commit -m "Add MainWindowViewModel: opens a profile and coordinates the three panels"
```

---

### Task 14: Three-panel `MainWindow` UI wiring

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml`
- Modify: `src/LEC.App/MainWindow.xaml.cs`
- Create: `src/LEC.App/Converters/LoadableToBrushConverter.cs`
- Create: `src/LEC.App/Converters/SpecialToFontWeightConverter.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel` (Task 13), `FolderNodeViewModel`/`FolderTreeViewModel` (Task 9), `MessageListViewModel` (Task 10), `Microsoft.Win32.OpenFolderDialog` (.NET 8+ WPF, no extra package).
- Produces: a working three-panel window. No automated test (WPF View — manual verification only, per Global Constraints).

- [ ] **Step 1: Implement the value converter**

```csharp
// src/LEC.App/Converters/LoadableToBrushConverter.cs
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LEC.App.Converters;

public sealed class LoadableToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Brushes.Black : Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

Special folders (Inbox, Drafts, etc.) are visually distinguished with bold text rather than a per-type icon glyph — a real icon set (or exact Segoe MDL2 Assets glyph codepoints) isn't something to guess at plan-writing time the way `DbxFormat`'s byte offsets couldn't be guessed in Fase 1; bold weight is a correct, unambiguous, zero-dependency stand-in that satisfies the same "special folders are visually distinguished" requirement from the design doc.

```csharp
// src/LEC.App/Converters/SpecialToFontWeightConverter.cs
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LEC.App.Converters;

public sealed class SpecialToFontWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontWeights.Bold : FontWeights.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 2: Implement `MainWindow.xaml`**

```xml
<!-- src/LEC.App/MainWindow.xaml -->
<Window x:Class="LEC.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:wv2="clr-namespace:Microsoft.Web.WebView2.Wpf;assembly=Microsoft.Web.WebView2.Wpf"
        xmlns:conv="clr-namespace:LEC.App.Converters"
        Title="LEC — Local Email Client" Height="720" Width="1100">
    <Window.Resources>
        <conv:LoadableToBrushConverter x:Key="LoadableToBrushConverter"/>
        <conv:SpecialToFontWeightConverter x:Key="SpecialToFontWeightConverter"/>
    </Window.Resources>
    <DockPanel>
        <Menu DockPanel.Dock="Top">
            <MenuItem Header="_Arquivo">
                <MenuItem Header="_Abrir Perfil..." Click="OpenProfile_Click"/>
            </MenuItem>
        </Menu>
        <StatusBar DockPanel.Dock="Bottom">
            <StatusBarItem Content="{Binding StatusText}"/>
        </StatusBar>
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="220"/>
                <ColumnDefinition Width="5"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <TreeView Grid.Column="0" x:Name="FolderTreeView"
                      ItemsSource="{Binding FolderTree.RootNodes}"
                      SelectedItemChanged="FolderTreeView_SelectedItemChanged">
                <TreeView.ItemTemplate>
                    <HierarchicalDataTemplate ItemsSource="{Binding Children}">
                        <TextBlock Text="{Binding DisplayName}"
                                   Foreground="{Binding IsLoadable, Converter={StaticResource LoadableToBrushConverter}}"
                                   FontWeight="{Binding IsSpecial, Converter={StaticResource SpecialToFontWeightConverter}}"/>
                    </HierarchicalDataTemplate>
                </TreeView.ItemTemplate>
            </TreeView>

            <GridSplitter Grid.Column="1" Width="5" HorizontalAlignment="Stretch"/>

            <Grid Grid.Column="2">
                <Grid.RowDefinitions>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="5"/>
                    <RowDefinition Height="2*"/>
                </Grid.RowDefinitions>

                <ListView Grid.Row="0" x:Name="MessageListView"
                          ItemsSource="{Binding MessageList.Messages}"
                          SelectionChanged="MessageListView_SelectionChanged"
                          VirtualizingPanel.IsVirtualizing="True"
                          VirtualizingPanel.VirtualizationMode="Recycling">
                    <ListView.View>
                        <GridView>
                            <GridViewColumn Header="De" DisplayMemberBinding="{Binding SenderName}" Width="200"/>
                            <GridViewColumn Header="Assunto" DisplayMemberBinding="{Binding Subject}" Width="350"/>
                            <GridViewColumn Header="Data" DisplayMemberBinding="{Binding Date}" Width="140"/>
                        </GridView>
                    </ListView.View>
                </ListView>

                <GridSplitter Grid.Row="1" Height="5" HorizontalAlignment="Stretch"/>

                <DockPanel Grid.Row="2">
                    <wv2:WebView2 x:Name="ReaderWebView"/>
                </DockPanel>
            </Grid>
        </Grid>
    </DockPanel>
</Window>
```

(The image-blocked bar referenced in the design doc is added in Task 15, alongside the handler it controls — adding the XAML for it now, before the handler exists, would be dead UI.)

- [ ] **Step 3: Implement `MainWindow.xaml.cs`**

```csharp
// src/LEC.App/MainWindow.xaml.cs
using System.Windows;
using System.Windows.Controls;
using LEC.App.ViewModels;
using LEC.MailStore;
using Microsoft.Win32;

namespace LEC.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecione a pasta do perfil (contendo Folders.dbx)" };
        if (dialog.ShowDialog() == true)
            _viewModel.OpenProfile(dialog.FolderName);
    }

    private async void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeViewModel node)
            await _viewModel.SelectFolderAsync(node);
    }

    private async void MessageListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessageListView.SelectedItem is MessageHeader header)
        {
            await _viewModel.SelectMessageAsync(header);
            if (_viewModel.MessageReader?.Current is { } rendered)
                ReaderWebView.NavigateToString(rendered.Html);
        }
    }
}
```

- [ ] **Step 4: Verify it builds and runs**

Run: `dotnet build`
Expected: `Build succeeded.`

Run: `dotnet run --project src/LEC.App`
Expected: window opens with menu ("Arquivo > Abrir Perfil..."), empty tree/list/reading pane, status bar. Use **Arquivo > Abrir Perfil...**, pick the `BCP/` folder from the repo root. Confirm: the tree populates (26 folders, including "seg" appearing directly under the root next to "Pastas locais" — the orphan), special folders ("Caixa de entrada", "Rascunhos", "Caixa de saída", "Itens enviados", "Itens excluídos") render in bold, clicking "Caixa de entrada" populates the message list (2235 rows, virtualized — scrolling should stay smooth), clicking a message shows its HTML/text body in the reading pane. Close the window when done.

This is the first genuinely interactive step in this plan — take your time confirming it actually works end-to-end against the real profile rather than just that it builds.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs src/LEC.App/Converters/LoadableToBrushConverter.cs src/LEC.App/Converters/SpecialToFontWeightConverter.cs
git commit -m "Wire the three-panel MainWindow: folder tree, message list, reading pane"
```

---

### Task 15: Image blocking and link confirmation in the reading pane

**Files:**
- Create: `src/LEC.App/Services/ImageBlockingWebView2Handler.cs`
- Modify: `src/LEC.App/MainWindow.xaml`
- Modify: `src/LEC.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `Microsoft.Web.WebView2.Core.CoreWebView2` (remote-image interception via `WebResourceRequested`, link interception via `NavigationStarting`).
- Produces: `ImageBlockingWebView2Handler { bool BlockImages; }` wired into `MainWindow`. No automated test (tied to a live `CoreWebView2` instance — manual verification only, per Global Constraints).

- [ ] **Step 1: Implement the handler**

```csharp
// src/LEC.App/Services/ImageBlockingWebView2Handler.cs
using Microsoft.Web.WebView2.Core;

namespace LEC.App.Services;

/// <summary>
/// Blocks remote image requests in a WebView2 instance until explicitly
/// allowed. Registered once per CoreWebView2; toggling BlockImages does not
/// require re-navigating — in-flight and future image requests are simply
/// allowed or denied based on the current value.
/// </summary>
public sealed class ImageBlockingWebView2Handler
{
    private readonly CoreWebView2 _coreWebView;

    public bool BlockImages { get; set; } = true;

    public ImageBlockingWebView2Handler(CoreWebView2 coreWebView)
    {
        _coreWebView = coreWebView;
        _coreWebView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
        _coreWebView.WebResourceRequested += OnWebResourceRequested;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!BlockImages)
            return;

        e.Response = _coreWebView.Environment.CreateWebResourceResponse(
            Content: null, StatusCode: 403, ReasonPhrase: "Blocked by LEC", Headers: string.Empty);
    }
}
```

- [ ] **Step 2: Add the "Exibir imagens" bar to `MainWindow.xaml`**

Add this `Border`, inside the reading-pane `DockPanel` from Task 14, directly above the `<wv2:WebView2 .../>` element (`DockPanel.Dock="Top"` so it stacks above the browser surface):

```xml
                <DockPanel Grid.Row="2">
                    <Border DockPanel.Dock="Top" x:Name="ImagesBlockedBar" Background="#FFF3CD" Padding="6" Visibility="Collapsed">
                        <DockPanel>
                            <Button DockPanel.Dock="Right" Content="Exibir imagens" Click="ShowImages_Click"/>
                            <TextBlock Text="Imagens remotas foram bloqueadas por padrão." VerticalAlignment="Center"/>
                        </DockPanel>
                    </Border>
                    <wv2:WebView2 x:Name="ReaderWebView"/>
                </DockPanel>
```

(Replace the existing `<DockPanel Grid.Row="2">...</DockPanel>` block from Task 14 with this one — same element, new child added above `ReaderWebView`.)

- [ ] **Step 3: Wire it up in `MainWindow.xaml.cs`**

Add a field, initialize the handler once the WebView2 control's `CoreWebView2` is ready, and reset `ImagesBlockedBar`'s visibility per message:

```csharp
// Add to the top of the class, alongside `_viewModel`:
    private LEC.App.Services.ImageBlockingWebView2Handler? _imageBlocker;

// Add to the constructor, after InitializeComponent()/DataContext assignment:
    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ReaderWebView.EnsureCoreWebView2Async();
        _imageBlocker = new LEC.App.Services.ImageBlockingWebView2Handler(ReaderWebView.CoreWebView2);
        ReaderWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
    }

    private void CoreWebView2_NavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        // NavigateToString's own content loads through a data: URI — never intercept that.
        if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;
        var result = MessageBox.Show($"Abrir este link no navegador?\n\n{e.Uri}", "Confirmar link",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
    }

    private void ShowImages_Click(object sender, RoutedEventArgs e)
    {
        if (_imageBlocker is null)
            return;

        _imageBlocker.BlockImages = false;
        ImagesBlockedBar.Visibility = Visibility.Collapsed;
        ReaderWebView.Reload();
    }
```

Update `MessageListView_SelectionChanged` to reset the bar to visible (images blocked again) each time a *different* message is opened — a message with no remote images simply never triggers the handler, so this doesn't need to detect image presence up front:

```csharp
    private async void MessageListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessageListView.SelectedItem is MessageHeader header)
        {
            if (_imageBlocker is not null)
            {
                _imageBlocker.BlockImages = true;
                ImagesBlockedBar.Visibility = Visibility.Visible;
            }

            await _viewModel.SelectMessageAsync(header);
            if (_viewModel.MessageReader?.Current is { } rendered)
                ReaderWebView.NavigateToString(rendered.Html);
        }
    }
```

- [ ] **Step 4: Verify manually**

Run: `dotnet build` — expect success.

Run: `dotnet run --project src/LEC.App`, open the `BCP/` profile, and find a message whose body contains an `<img src="http...">` tag (many of the older marketing/newsletter-style emails in a real archive have these — check a few messages in `Recebidos 2003.dbx` or similar via the reading pane). Confirm: the image does not load, the yellow bar appears; clicking "Exibir imagens" makes the image load and the bar disappear; selecting a different message re-blocks and re-shows the bar. Click a real link in an HTML message body and confirm the confirmation dialog appears with the real destination URL before anything opens.

If `NavigationStarting`'s URI check for `NavigateToString`'s own load needs adjusting (the exact URI scheme WebView2 uses for `NavigateToString` content can vary by runtime version) — observe what actually happens when you first select a message (does the body fail to render because the dialog intercepted its own load?) and adjust the `StartsWith` check accordingly based on what you observe. This is exactly the kind of thing Global Constraints flags as manually-verified-and-adjusted, not pinned in advance.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/Services/ImageBlockingWebView2Handler.cs src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs
git commit -m "Block remote images by default and confirm link destinations before opening"
```

---

### Task 16: Health and degradation indicators in the UI

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `MainWindowViewModel.StatusText` (already populated by Task 13's `OpenProfile` for non-`Ok` health and open failures), `MessageHeader.Health` (Task 2), `FolderNodeViewModel.IsLoadable` (Task 9, already bound via `LoadableToBrushConverter` since Task 14).
- Produces: visible, non-modal health signals. No automated test (visual — manual verification).

- [ ] **Step 1: Add a `Health` column to the message list**

In `MainWindow.xaml`, add a column to the existing `<GridView>` from Task 14 (insert before the "De" column so a `Partial` row is noticeable at a glance):

```xml
                        <GridView>
                            <GridViewColumn Header="!" Width="24">
                                <GridViewColumn.CellTemplate>
                                    <DataTemplate>
                                        <TextBlock Text="{Binding Health, StringFormat={}{0}}"
                                                   Visibility="{Binding Health, Converter={StaticResource HealthToVisibilityConverter}}"
                                                   Foreground="DarkOrange" FontWeight="Bold" ToolTip="Mensagem com atributo fora do intervalo esperado — dado parcialmente recuperado."/>
                                    </DataTemplate>
                                </GridViewColumn.CellTemplate>
                            </GridViewColumn>
                            <GridViewColumn Header="De" DisplayMemberBinding="{Binding SenderName}" Width="200"/>
                            <GridViewColumn Header="Assunto" DisplayMemberBinding="{Binding Subject}" Width="350"/>
                            <GridViewColumn Header="Data" DisplayMemberBinding="{Binding Date}" Width="140"/>
                        </GridView>
```

This needs a second converter, added to `Window.Resources` alongside `LoadableToBrushConverter`:

```csharp
// src/LEC.App/Converters/HealthToVisibilityConverter.cs
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using LEC.DbxFormat;

namespace LEC.App.Converters;

public sealed class HealthToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is StoreHealth.Ok or null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

```xml
    <Window.Resources>
        <conv:LoadableToBrushConverter x:Key="LoadableToBrushConverter"/>
        <conv:HealthToVisibilityConverter x:Key="HealthToVisibilityConverter"/>
    </Window.Resources>
```

- [ ] **Step 2: Add a tooltip to non-loadable folders**

In the `TreeView.ItemTemplate`'s `TextBlock` (Task 14), add a `ToolTip` bound to loadability:

```xml
                        <TextBlock Text="{Binding DisplayName}"
                                   Foreground="{Binding IsLoadable, Converter={StaticResource LoadableToBrushConverter}}"
                                   ToolTip="{Binding IsLoadable, Converter={StaticResource NotLoadableTooltipConverter}}"/>
```

```csharp
// src/LEC.App/Converters/NotLoadableTooltipConverter.cs
using System.Globalization;
using System.Windows.Data;

namespace LEC.App.Converters;

public sealed class NotLoadableTooltipConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is false ? "Arquivo desta pasta não foi encontrado ou não pôde ser lido." : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

```xml
    <Window.Resources>
        <conv:LoadableToBrushConverter x:Key="LoadableToBrushConverter"/>
        <conv:HealthToVisibilityConverter x:Key="HealthToVisibilityConverter"/>
        <conv:NotLoadableTooltipConverter x:Key="NotLoadableTooltipConverter"/>
    </Window.Resources>
```

- [ ] **Step 3: Verify manually**

Run: `dotnet build` — expect success.

Run: `dotnet run --project src/LEC.App`, open `BCP/`. The status bar should read `null`/empty (the real profile's `Folders.dbx` health is `Ok`). To see the degraded states without touching `BCP/`: temporarily copy `BCP/Folders.dbx` alone (read-only copy, into a scratch directory outside the repo — never write into `BCP/`) into a folder missing one of its referenced `.dbx` files, open that folder as a profile, and confirm the corresponding tree node is grayed out with a tooltip. Delete the scratch copy afterward.

- [ ] **Step 4: Commit**

```bash
git add src/LEC.App/MainWindow.xaml src/LEC.App/Converters/HealthToVisibilityConverter.cs src/LEC.App/Converters/NotLoadableTooltipConverter.cs
git commit -m "Add visible health/degradation indicators: per-message flag, non-loadable folder tooltip"
```

---

### Task 17: Full verification pass and wrap-up

**Files:** none (verification only).

- [ ] **Step 1: Run the full automated test suite**

Run: `dotnet test`
Expected: every test across `LEC.DbxFormat.Tests`, `LEC.MailStore.Tests`, and `LEC.App.Tests` passes, including both real-fixture tests (Fase 1's two, plus Task 7's `DbxStore` one).

- [ ] **Step 2: Full interactive walkthrough against the real profile**

Run: `dotnet build -c Release`, then `dotnet run -c Release --project src/LEC.App`. Open `BCP/` as the profile and walk through:
- Tree shows all 26 folders, "seg" appears as a top-level node (the orphan), special folders (Inbox, Drafts, Outbox, Sent Items, Deleted Items) are labeled correctly.
- Click through at least: "Caixa de entrada" (2235 messages — list should scroll smoothly, virtualization holding up), "Rascunhos" (1 message), and "BCP Espelho" (7498 messages, the largest folder — confirm the list populates without a long freeze).
- Open several messages across different folders/years, confirm HTML renders, confirm a plain-text-only message falls back to readable `<pre>` output.
- Find a message with a remote image, confirm it's blocked by default and "Exibir imagens" works.
- Click a real link in a message body, confirm the confirmation dialog shows the real URL before anything opens.
- Close and reopen the profile (Arquivo > Abrir Perfil... again) to confirm the previous `DbxStore` is disposed cleanly (no exception, no obviously leaked file handles — reopening the same profile a second time should work identically).

If anything in this walkthrough doesn't work as expected, treat it as a real bug against the relevant task rather than proceeding — this is the equivalent of Fase 1's Task 14 large-file smoke test, just interactive instead of scripted.

- [ ] **Step 3: Final commit**

```bash
git add -A
git commit -m "Fase 2 complete: three-panel visualizador reading the real profile end-to-end"
```

---

## Out of scope for this plan (later phases per ARQUITETURA.md §12, and this session's scoping decisions)

- `PstStore` (deferred — no real `.pst` to verify against yet).
- Opening a bare `.dbx` file without a profile.
- Fase 3: scavenge/recovery mode, `Recuperada`/`Fragmento` markers.
- Fase 4+: `.lecarchive` library, dedup, reorganization, export.
