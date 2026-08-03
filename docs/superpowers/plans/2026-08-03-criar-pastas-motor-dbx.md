# Criar pastas — motor de escrita .dbx — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `LEC.DbxFormat`/`LEC.MailStore` the ability to create a real folder — a new empty message-store `.dbx` file plus a new entry in `Folders.dbx` — mirroring how Outlook Express itself created folders (one folder = one file). No UI in this slice.

**Architecture:** Two new writer types in `LEC.DbxFormat` (`DbxEmailFileWriter.CreateEmpty`, `DbxFolderFileWriter.Rebuild`) that produce byte-exact `.dbx` files matching the constants already verified in `DbxFormat.cs` and exercised by the existing reader (`DbxFolderFile`/`DbxEmailFile`). `DbxStore.CreateFolderAsync` orchestrates: validate, pick a new id, create the empty backing file, back up `Folders.dbx`, rebuild `Folders.dbx` from every folder currently on disk (preserving their real `Flags`/`MessageCount`/`SpecialFolderType`) plus the new entry, then update the in-memory folder list.

**Tech Stack:** .NET 9, C#, xUnit. No new dependencies.

## Global Constraints

- Original `.dbx`/`.pst` files stay read-only, always — **except** `Folders.dbx`, which this slice deliberately writes to (the one exception in the whole project, per the approved design doc).
- No UI in this slice — `DbxStore.CreateFolderAsync` is called directly by tests, nothing wires it into `FolderTreeViewModel`/`ProfileViewModel` yet.
- No moving messages into the new folder, no deleting/renaming folders — future slices.
- No round-trip verification after writing `Folders.dbx` (reopening it with the reader to confirm it parsed correctly before committing) — rejected by design; the only safety net is copying `Folders.dbx` to `Folders.dbx.bak` before overwriting it.
- `Folders.dbx`'s write is atomic (temp file + `File.Move(overwrite: true)`) — this is plain durability against a half-written file, not the content verification that was rejected.
- `PstStore` does not exist yet — only `DbxStore` implements folder creation.
- Existing folders' `Flags`/`MessageCount`/`SpecialFolderType`/`FileName` must be preserved exactly when `Folders.dbx` is rebuilt — only the new entry gets zero/defaults. Losing e.g. `SpecialFolderType` on every existing folder would silently break Inbox/Sent/Deleted classification app-wide.
- `ParentId` is always written **pointer-form** (a real 4-byte `int32` in the info-node's data area), never direct — the root sentinel `-1` does not fit the format's 24-bit direct-value payload (`DbxFormat.AttributeValueMask`).

---

## File Structure

```
src/LEC.DbxFormat/
  DbxEmailFileWriter.cs     NEW — Task 1: CreateEmpty(string path)
  DbxFolderFileWriter.cs    NEW — Task 2: DbxFolderWriteEntry record, Rebuild(string path, IReadOnlyList<DbxFolderWriteEntry> folders)

src/LEC.MailStore/
  DbxStore.cs               MODIFIED — Task 3: Folders backed by a mutable field instead of an init-only property; +CreateFolderAsync

tests/LEC.DbxFormat.Tests/
  DbxEmailFileWriterTests.cs    NEW — Task 1
  DbxFolderFileWriterTests.cs   NEW — Task 2

tests/LEC.MailStore.Tests/
  DbxStoreCreateFolderTests.cs  NEW — Task 3
```

---

### Task 1: `DbxEmailFileWriter.CreateEmpty`

**Files:**
- Create: `src/LEC.DbxFormat/DbxEmailFileWriter.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxEmailFileWriterTests.cs`

**Interfaces:**
- Consumes: `DbxFormat.SignatureWord0/SignatureWord1Email/SignatureWord2/SignatureWord3/HeaderItemCountOffset/HeaderIndexPointerOffset` (existing), `DbxEmailFile.Open(string path)` (existing, used only by the test).
- Produces: `DbxEmailFileWriter.CreateEmpty(string path) : void`. Consumed by Task 3.

- [ ] **Step 1: Write the failing test**

Create `tests/LEC.DbxFormat.Tests/DbxEmailFileWriterTests.cs`:

```csharp
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxEmailFileWriterTests
{
    [Fact]
    public void CreateEmptyProducesAReadableEmptyMessageFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-email-{Guid.NewGuid():N}.dbx");
        try
        {
            DbxEmailFileWriter.CreateEmpty(path);

            using var file = DbxEmailFile.Open(path);

            Assert.Equal(StoreHealth.Ok, file.Health);
            Assert.Empty(file.Messages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateEmptyIsDetectedAsAnEmailTypeFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-email-{Guid.NewGuid():N}.dbx");
        try
        {
            DbxEmailFileWriter.CreateEmpty(path);

            using var source = new MemoryMappedDbxSource(path);
            Assert.True(DbxHeader.TryDetectType(source, out var type));
            Assert.Equal(DbxFileType.Email, type);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter "CreateEmptyProducesAReadableEmptyMessageFile|CreateEmptyIsDetectedAsAnEmailTypeFile"`
Expected: FAIL to compile — `DbxEmailFileWriter` does not exist yet.

- [ ] **Step 3: Implement `DbxEmailFileWriter`**

Create `src/LEC.DbxFormat/DbxEmailFileWriter.cs`:

```csharp
namespace LEC.DbxFormat;

/// <summary>
/// Writes a brand-new, empty message-store .dbx file — the backing file for a folder
/// that was just created and has no messages yet. itemCount = 0 means the reader
/// (DbxEmailFile.Open) never attempts to walk an index tree, so no tree needs to exist:
/// the header alone is a complete, valid, empty file.
/// </summary>
public static class DbxEmailFileWriter
{
    public static void CreateEmpty(string path)
    {
        var bytes = new byte[DbxFormat.HeaderIndexPointerOffset + 4];

        BitConverter.GetBytes(DbxFormat.SignatureWord0).CopyTo(bytes, 0);
        BitConverter.GetBytes(DbxFormat.SignatureWord1Email).CopyTo(bytes, 4);
        BitConverter.GetBytes(DbxFormat.SignatureWord2).CopyTo(bytes, 8);
        BitConverter.GetBytes(DbxFormat.SignatureWord3).CopyTo(bytes, 12);
        BitConverter.GetBytes(0).CopyTo(bytes, DbxFormat.HeaderItemCountOffset);

        File.WriteAllBytes(path, bytes);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter "CreateEmptyProducesAReadableEmptyMessageFile|CreateEmptyIsDetectedAsAnEmailTypeFile"`
Expected: PASS — both new tests.

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxEmailFileWriter.cs tests/LEC.DbxFormat.Tests/DbxEmailFileWriterTests.cs
git commit -m "Add DbxEmailFileWriter.CreateEmpty: writes a new empty message-store .dbx file"
```

---

### Task 2: `DbxFolderFileWriter.Rebuild`

**Files:**
- Create: `src/LEC.DbxFormat/DbxFolderFileWriter.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxFolderFileWriterTests.cs`

**Interfaces:**
- Consumes: `DbxFormat.*` constants (existing), `DbxFolderFieldId` (existing), `DbxSpecialFolderType` (existing), `DbxFolderFile.Open(string path)` (existing, used only by the test).
- Produces: `DbxFolderWriteEntry(int FolderId, int ParentId, string Name, string? FileName, int Flags = 0, int MessageCount = 0, DbxSpecialFolderType SpecialFolderType = DbxSpecialFolderType.None)` (record) and `DbxFolderFileWriter.Rebuild(string path, IReadOnlyList<DbxFolderWriteEntry> folders) : void`. Both consumed by Task 3. `ParentId = -1` means root (same sentinel `DbxStore.Open` already reads).

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.DbxFormat.Tests/DbxFolderFileWriterTests.cs`:

```csharp
using System.Linq;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxFolderFileWriterTests
{
    [Fact]
    public void RebuildsAFolderFileReadableByDbxFolderFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-folders-{Guid.NewGuid():N}.dbx");
        try
        {
            var entries = new[]
            {
                new DbxFolderWriteEntry(1, ParentId: -1, Name: "Pastas locais", FileName: null),
                new DbxFolderWriteEntry(2, ParentId: 1, Name: "Caixa de entrada", FileName: "Inbox.dbx",
                    MessageCount: 5, SpecialFolderType: DbxSpecialFolderType.Inbox),
            };

            DbxFolderFileWriter.Rebuild(path, entries);

            using var file = DbxFolderFile.Open(path);
            Assert.Equal(StoreHealth.Ok, file.Health);
            Assert.Equal(2, file.Folders.Count);

            var root = file.Folders.Single(f => f.FolderId == 1);
            Assert.Equal(-1, root.ParentId);
            Assert.Equal("Pastas locais", root.Name);
            Assert.Null(root.FileName);

            var inbox = file.Folders.Single(f => f.FolderId == 2);
            Assert.Equal(1, inbox.ParentId);
            Assert.Equal("Caixa de entrada", inbox.Name);
            Assert.Equal("Inbox.dbx", inbox.FileName);
            Assert.Equal(5, inbox.MessageCount);
            Assert.Equal(DbxSpecialFolderType.Inbox, inbox.SpecialFolderType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildOverwritesAnExistingFileAndLeavesNoTempFileBehind()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lec-folders-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Folders.dbx");
        try
        {
            DbxFolderFileWriter.Rebuild(path, new[] { new DbxFolderWriteEntry(1, -1, "Primeira", null) });
            DbxFolderFileWriter.Rebuild(path, new[] { new DbxFolderWriteEntry(1, -1, "Primeira", null),
                                                       new DbxFolderWriteEntry(2, -1, "Segunda", null) });

            using var file = DbxFolderFile.Open(path);
            Assert.Equal(2, file.Folders.Count);
            Assert.Single(Directory.GetFiles(dir)); // only Folders.dbx — no leftover .tmp-* file
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter "RebuildsAFolderFileReadableByDbxFolderFile|RebuildOverwritesAnExistingFileAndLeavesNoTempFileBehind"`
Expected: FAIL to compile — `DbxFolderFileWriter`/`DbxFolderWriteEntry` do not exist yet.

- [ ] **Step 3: Implement `DbxFolderFileWriter`**

Create `src/LEC.DbxFormat/DbxFolderFileWriter.cs`:

```csharp
namespace LEC.DbxFormat;

/// <summary>
/// One folder's worth of data to write into a rebuilt Folders.dbx. ParentId uses the
/// same -1-means-root sentinel DbxStore.Open already reads. Flags/MessageCount/
/// SpecialFolderType default to zero/None for a brand-new folder; callers rebuilding
/// Folders.dbx around an existing folder must pass its real values through unchanged,
/// or that folder silently loses its Inbox/Sent/Deleted classification and message count.
/// </summary>
public sealed record DbxFolderWriteEntry(
    int FolderId,
    int ParentId,
    string Name,
    string? FileName,
    int Flags = 0,
    int MessageCount = 0,
    DbxSpecialFolderType SpecialFolderType = DbxSpecialFolderType.None);

/// <summary>
/// Rewrites Folders.dbx from scratch as a single, flat index-tree node (no children —
/// ptrCount is one byte, so up to 255 folders fit; a real profile has ~26). This is
/// simpler and easier to get right than inserting into the existing tree in place, at
/// the cost of not preserving whatever undocumented header bytes the original OE-written
/// file had beyond the fields this project's reader actually uses.
/// </summary>
public static class DbxFolderFileWriter
{
    public static void Rebuild(string path, IReadOnlyList<DbxFolderWriteEntry> folders)
    {
        var bytes = new List<byte>(new byte[DbxFormat.HeaderIndexPointerOffset + 4]);

        WriteUInt32(bytes, 0, DbxFormat.SignatureWord0);
        WriteUInt32(bytes, 4, DbxFormat.SignatureWord1Folder);
        WriteUInt32(bytes, 8, DbxFormat.SignatureWord2);
        WriteUInt32(bytes, 12, DbxFormat.SignatureWord3);

        var infoNodeOffsets = new int[folders.Count];
        for (int i = 0; i < folders.Count; i++)
            infoNodeOffsets[i] = WriteInfoNode(bytes, folders[i]);

        int rootOffset = WriteIndexNode(bytes, infoNodeOffsets);

        WriteInt32(bytes, DbxFormat.HeaderItemCountOffset, folders.Count);
        WriteInt32(bytes, DbxFormat.HeaderIndexPointerOffset, rootOffset);

        string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(tempPath, bytes.ToArray());
        File.Move(tempPath, path, overwrite: true);
    }

    private static int WriteInfoNode(List<byte> bytes, DbxFolderWriteEntry folder)
    {
        int start = bytes.Count;

        var attrs = new List<(byte FieldId, bool Direct, uint DirectValue, byte[]? Data)>
        {
            ((byte)DbxFolderFieldId.FolderId, true, unchecked((uint)folder.FolderId), null),
            ((byte)DbxFolderFieldId.ParentId, false, 0, BitConverter.GetBytes(folder.ParentId)),
            ((byte)DbxFolderFieldId.Name, false, 0, EncodeString(folder.Name)),
        };
        if (folder.FileName is not null)
            attrs.Add(((byte)DbxFolderFieldId.FileName, false, 0, EncodeString(folder.FileName)));
        attrs.Add(((byte)DbxFolderFieldId.Flags, true, unchecked((uint)folder.Flags), null));
        attrs.Add(((byte)DbxFolderFieldId.MessageCount, true, unchecked((uint)folder.MessageCount), null));
        attrs.Add(((byte)DbxFolderFieldId.SpecialFolderType, true, unchecked((uint)folder.SpecialFolderType), null));

        // Info-node header: 8 unused bytes, then a count word at +8 whose byte at +2 (i.e.
        // absolute offset +10) is the attribute count — DbxFormat.InfoNodeCountWordOffset/
        // InfoNodeCountMask/InfoNodeCountShift read exactly this layout.
        AddZeros(bytes, 8);
        bytes.Add(0);
        bytes.Add(0);
        bytes.Add((byte)attrs.Count);
        bytes.Add(0);

        int arrayStart = bytes.Count;
        for (int i = 0; i < attrs.Count; i++)
            AddZeros(bytes, DbxFormat.AttributeRecordSize); // reserved, back-patched below

        int dataAreaStart = bytes.Count;
        for (int i = 0; i < attrs.Count; i++)
        {
            var attr = attrs[i];
            byte typeByte;
            uint recordValue;

            if (attr.Direct)
            {
                typeByte = (byte)(attr.FieldId | DbxFormat.AttributeDirectFlag);
                recordValue = attr.DirectValue & DbxFormat.AttributeValueMask;
            }
            else
            {
                typeByte = attr.FieldId;
                recordValue = (uint)(bytes.Count - dataAreaStart) & DbxFormat.AttributeValueMask;
                bytes.AddRange(attr.Data!);
            }

            uint record = (uint)typeByte | (recordValue << DbxFormat.AttributeValueShift);
            byte[] recordBytes = BitConverter.GetBytes(record);
            for (int b = 0; b < 4; b++)
                bytes[arrayStart + i * DbxFormat.AttributeRecordSize + b] = recordBytes[b];
        }

        return start;
    }

    private static int WriteIndexNode(List<byte> bytes, IReadOnlyList<int> infoNodeOffsets)
    {
        int start = bytes.Count;

        AddZeros(bytes, 8);                                   // 0..7: unused by the reader
        AddZeros(bytes, 4);                                   // 8..11: nextTable (unused — indexCount = 0)
        AddZeros(bytes, 5);                                   // 12..16: unused
        bytes.Add((byte)infoNodeOffsets.Count);                // 17: ptrCount
        AddZeros(bytes, 2);                                   // 18..19: unused
        bytes.AddRange(BitConverter.GetBytes(0));              // 20..23: indexCount = 0 — flat, no children

        foreach (int offset in infoNodeOffsets)
        {
            bytes.AddRange(BitConverter.GetBytes(offset)); // indexPtr
            AddZeros(bytes, 4);                             // entryNextTable = 0
            AddZeros(bytes, 4);                             // entryIndexCount = 0
        }

        return start;
    }

    private static byte[] EncodeString(string value) => System.Text.Encoding.UTF8.GetBytes(value + "\0");

    private static void AddZeros(List<byte> bytes, int count) => bytes.AddRange(new byte[count]);

    private static void WriteUInt32(List<byte> bytes, int offset, uint value)
    {
        byte[] b = BitConverter.GetBytes(value);
        for (int i = 0; i < 4; i++) bytes[offset + i] = b[i];
    }

    private static void WriteInt32(List<byte> bytes, int offset, int value)
    {
        byte[] b = BitConverter.GetBytes(value);
        for (int i = 0; i < 4; i++) bytes[offset + i] = b[i];
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter "RebuildsAFolderFileReadableByDbxFolderFile|RebuildOverwritesAnExistingFileAndLeavesNoTempFileBehind"`
Expected: PASS — both new tests.

Run: `dotnet test`
Expected: PASS — no regression.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxFolderFileWriter.cs tests/LEC.DbxFormat.Tests/DbxFolderFileWriterTests.cs
git commit -m "Add DbxFolderFileWriter.Rebuild: rewrites Folders.dbx as a flat single-node tree"
```

---

### Task 3: `DbxStore.CreateFolderAsync`

**Files:**
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Test: `tests/LEC.MailStore.Tests/DbxStoreCreateFolderTests.cs`

**Interfaces:**
- Consumes: `DbxEmailFileWriter.CreateEmpty` (Task 1), `DbxFolderWriteEntry`/`DbxFolderFileWriter.Rebuild` (Task 2), `DbxFolderFile.Open` (existing, to re-read the current on-disk folder list before rebuilding).
- Produces: `DbxStore.CreateFolderAsync(string name, int? parentId, CancellationToken ct = default) : Task<StoreFolder>`. Last method of this slice — nothing later consumes it (UI wiring is a future slice).

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.MailStore.Tests/DbxStoreCreateFolderTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using LEC.MailStore;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreCreateFolderTests
{
    private static string BuildProfileWithOneFolder()
    {
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);
        int inboxNode = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Caixa de entrada\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Inbox.dbx\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.MessageCount, Direct = true, DirectValue = 5 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });
        int root = folders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: inboxNode, entryNextTable: 0, entryIndexCount: 0) });
        folders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        folders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        var emptyInbox = new TestDbxBuilder();
        emptyInbox.WriteHeader(DbxFileType.Email);
        emptyInbox.SetInt32At(DbxFormat.HeaderItemCountOffset, 0);
        File.WriteAllBytes(Path.Combine(dir, "Inbox.dbx"), emptyInbox.ToArray());
        return dir;
    }

    [Fact]
    public async Task CreateFolderAsyncAddsATopLevelFolderVisibleAfterReopening()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            using (var store = DbxStore.Open(dir))
            {
                var created = await store.CreateFolderAsync("Mail 2026", parentId: null);

                Assert.Equal("Mail 2026", created.Name);
                Assert.Null(created.ParentId);
                Assert.True(File.Exists(Path.Combine(dir, "Mail 2026.dbx")));
                Assert.Contains(store.Folders, f => f.Id == created.Id);
            }

            using var reopened = DbxStore.Open(dir);
            Assert.Equal(2, reopened.Folders.Count);

            var inbox = reopened.Folders.Single(f => f.Name == "Caixa de entrada");
            Assert.Equal(SpecialFolderKind.Inbox, inbox.SpecialFolderKind);
            Assert.Equal(5, inbox.MessageCount);
            Assert.Contains(reopened.Folders, f => f.Name == "Mail 2026");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncCreatesABackupOfFoldersDbx()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Folders.dbx"));
            using var store = DbxStore.Open(dir);

            await store.CreateFolderAsync("Mail 2026", parentId: null);

            string backupPath = Path.Combine(dir, "Folders.dbx.bak");
            Assert.True(File.Exists(backupPath));
            Assert.Equal(originalBytes, File.ReadAllBytes(backupPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncAsASubfolderSetsParentId()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            using var store = DbxStore.Open(dir);
            int parentId = store.Folders.Single(f => f.Name == "Caixa de entrada").Id;

            var created = await store.CreateFolderAsync("2026", parentId);

            Assert.Equal(parentId, created.ParentId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncOnANonexistentParentThrows()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateFolderAsync("X", parentId: 999));
            Assert.Contains("não encontrada", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncCollidingWithASiblingNameThrows()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateFolderAsync("Caixa de entrada", parentId: null));
            Assert.Contains("já existe", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncWithABlankNameThrows()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateFolderAsync("???", parentId: null));
            Assert.Contains("inválido", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateFolderAsyncWhenTheBackingFileAlreadyExistsThrows()
    {
        string dir = BuildProfileWithOneFolder();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Mail 2026.dbx"), new byte[] { 0 });
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateFolderAsync("Mail 2026", parentId: null));
            Assert.Contains("já existe", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "CreateFolderAsync"`
Expected: FAIL to compile — `DbxStore.CreateFolderAsync` does not exist yet.

- [ ] **Step 3: Make `Folders` mutable and add `CreateFolderAsync` to `DbxStore`**

In `src/LEC.MailStore/DbxStore.cs`, change the field/property declarations. Replace:

```csharp
public IReadOnlyList<StoreFolder> Folders { get; }
public StoreHealth Health { get; }

private DbxStore(string profileDirectory, IReadOnlyList<StoreFolder> folders, Dictionary<int, string?> backingFileNames, StoreHealth health, ScavengeCache scavengeCache)
{
    _profileDirectory = profileDirectory;
    Folders = folders;
    _backingFileNames = backingFileNames;
    Health = health;
    _scavengeCache = scavengeCache;
}
```

with:

```csharp
private readonly List<StoreFolder> _folders;
public IReadOnlyList<StoreFolder> Folders => _folders;
public StoreHealth Health { get; }

private DbxStore(string profileDirectory, List<StoreFolder> folders, Dictionary<int, string?> backingFileNames, StoreHealth health, ScavengeCache scavengeCache)
{
    _profileDirectory = profileDirectory;
    _folders = folders;
    _backingFileNames = backingFileNames;
    Health = health;
    _scavengeCache = scavengeCache;
}
```

(The `folders` local variable built inside `Open` is already a `List<StoreFolder>` — only the constructor's parameter/field type changes, `Open` itself needs no edit.)

Then add, right after `Dispose` is declared or any other convenient point inside the `DbxStore` class body (e.g. immediately below the constructor):

```csharp
    private static readonly char[] InvalidFileNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    public Task<StoreFolder> CreateFolderAsync(string name, int? parentId, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (parentId is int pid && !_folders.Any(f => f.Id == pid))
                throw new InvalidOperationException($"Pasta-mãe {pid} não encontrada.");

            string sanitizedName = new string(name.Where(c => Array.IndexOf(InvalidFileNameChars, c) < 0).ToArray()).Trim();
            if (sanitizedName.Length == 0)
                throw new InvalidOperationException($"Nome de pasta inválido: \"{name}\".");

            if (_folders.Any(f => f.ParentId == parentId && f.Name == sanitizedName))
                throw new InvalidOperationException($"Já existe uma pasta chamada \"{sanitizedName}\" no mesmo nível.");

            string fileName = sanitizedName + ".dbx";
            string filePath = Path.Combine(_profileDirectory, fileName);
            if (File.Exists(filePath))
                throw new InvalidOperationException($"Já existe um arquivo \"{fileName}\" neste perfil.");

            int newId = _folders.Count == 0 ? 1 : _folders.Max(f => f.Id) + 1;

            DbxEmailFileWriter.CreateEmpty(filePath);

            string foldersDbxPath = Path.Combine(_profileDirectory, "Folders.dbx");
            File.Copy(foldersDbxPath, foldersDbxPath + ".bak", overwrite: true);

            List<DbxFolderWriteEntry> entries;
            using (var current = DbxFolderFile.Open(foldersDbxPath))
            {
                entries = current.Folders
                    .Select(f => new DbxFolderWriteEntry(f.FolderId, f.ParentId, f.Name ?? "", f.FileName,
                        f.Flags, f.MessageCount, f.SpecialFolderType))
                    .ToList();
            }
            entries.Add(new DbxFolderWriteEntry(newId, parentId ?? -1, sanitizedName, fileName));

            DbxFolderFileWriter.Rebuild(foldersDbxPath, entries);

            var created = new StoreFolder
            {
                Id = newId,
                ParentId = parentId,
                Name = sanitizedName,
                SpecialFolderKind = SpecialFolderKind.None,
                MessageCount = 0,
                IsLoadable = true,
            };
            _folders.Add(created);
            _backingFileNames[newId] = fileName;

            return created;
        }, ct);
    }
```

`SpecialFolderKind.None` is the enum's first member (`src/LEC.MailStore/SpecialFolderKind.cs`), so it's already used exactly as written above.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "CreateFolderAsync"`
Expected: PASS — all 7 new tests.

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreCreateFolderTests.cs
git commit -m "Add DbxStore.CreateFolderAsync: creates a real .dbx folder, backing up and rebuilding Folders.dbx"
```

---

## Self-Review Notes

- **Spec coverage:** §3.1 (empty `.dbx`) → Task 1. §3.2 (rebuilt `Folders.dbx`, flat tree, direct vs. pointer-form attributes, `ParentId` always pointer-form) → Task 2. §4 (`CreateFolderAsync` algorithm, backup, atomic write) → Task 3. §5 (errors) → guard tests in Task 3. §6 (test strategy) → matched 1:1 by the `[Fact]`s in each task.
- **Type consistency:** `DbxFolderWriteEntry` defined in Task 2 with fields `(FolderId, ParentId, Name, FileName, Flags, MessageCount, SpecialFolderType)` is used with the same field names/positions in Task 3's `CreateFolderAsync`. `DbxEmailFileWriter.CreateEmpty(string)` and `DbxFolderFileWriter.Rebuild(string, IReadOnlyList<DbxFolderWriteEntry>)` signatures match between Tasks 1/2 (definition) and Task 3 (call site).
- **Preserving existing folder data:** Task 3 re-reads `Folders.dbx` via `DbxFolderFile.Open` immediately before rebuilding (not from the in-memory `StoreFolder`/`SpecialFolderKind` list, which has already lost `Flags` and lossily re-mapped `SpecialFolderType`) — this is what keeps every existing folder's `MessageCount`/`SpecialFolderType`/`Flags` intact across the rebuild.
- **Out of scope confirmed:** no task touches `FolderTreeViewModel`, `ProfileViewModel`, or any `.xaml` — UI wiring is the next slice, not this one.
