# Mover mensagem — motor (sem UI) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a message be copied from any folder into another folder (writing a real, byte-accurate `.dbx` file for the destination) and hidden from its original folder afterward — without ever writing to the source `.dbx`. Fully testable end to end (including across two different open profiles) without any UI.

**Architecture:** `DbxEmailFileWriter` gains a `Rebuild` method mirroring `DbxFolderFileWriter.Rebuild` (flat single-node B-tree, ≤255 items, atomic write) plus new logic to write a message's raw bytes as a block chain before its info-node. `IMailStore` gains `AddMessageAsync` (writes into a folder of *this* store — the destination side) and `HideMovedMessageAsync` (marks a message as moved-away for *this* store — the source side, backed by a new `MovedMessageCache`, same SQLite-in-AppData pattern as the existing `ScavengeCache`). `MainWindowViewModel.MoveMessageAsync` orchestrates both calls across two independent `IMailStore` instances (same profile or different ones) — read raw bytes from source, add to destination, only then hide from source.

**Tech Stack:** .NET 9, C#, Microsoft.Data.Sqlite, xUnit.

## Global Constraints

- "Mover" never deletes or rewrites anything in the *source* `.dbx` — confirmed decision, given real message-store files run into hundreds of MB (`BCP/Caixa de entrada (1).dbx` = 521 MB) and surgical B-tree deletion is out of scope. The only write to an original file in this whole feature is the *destination* folder's backing `.dbx` (already an accepted exception, same as folder creation).
- Destination folder is capped at 255 messages, same flat-tree limitation already accepted for `Folders.dbx`/folder creation. Above that, `AddMessageAsync` fails loudly (via `DbxEmailFileWriter.Rebuild`'s own guard) rather than risk corruption.
- "Hiding" a moved message from its source folder is done via a new local cache (`MovedMessageCache`, SQLite under `%LOCALAPPDATA%\LEC\`) — never a write to any `.dbx`. Same architecture as the existing `ScavengeCache` (`src/LEC.MailStore/ScavengeCache.cs`): profile-path + folder-file keyed, fallback to `:memory:` if the real path can't be opened.
- `AddMessageAsync` and `HideMovedMessageAsync` do not know about each other or about "moving" — they are two independent, single-store primitives. The cross-store "move" orchestration lives only in `MainWindowViewModel.MoveMessageAsync` (`LEC.App`), which is the only place that ever calls both.
- Order of operations in `MoveMessageAsync` matters: hide-from-source only happens *after* add-to-destination succeeds. If the destination write fails, nothing changes anywhere and the exception propagates to the caller.
- Not in scope: updating `Folders.dbx`'s cached `MessageCount`; moving into a destination folder with ≥255 messages; undoing/restoring a hidden message. All explicitly deferred per the design doc.

---

## File Structure

```
src/LEC.DbxFormat/
  DbxEmailFileWriter.cs         MODIFIED — Task 1: +DbxMessageWriteEntry, +Rebuild

src/LEC.MailStore/
  MovedMessageCache.cs           NEW — Task 2: same pattern as ScavengeCache.cs
  IMailStore.cs                  MODIFIED — Task 3: +AddMessageAsync; Task 4: +HideMovedMessageAsync
  DbxStore.cs                    MODIFIED — Task 3: +AddMessageAsync; Task 4: +HideMovedMessageAsync, +movedMessageCache plumbing, +EnumerateHeadersAsync filter

src/LEC.App/ViewModels/
  MainWindowViewModel.cs         MODIFIED — Task 5: +MoveMessageAsync

tests/LEC.DbxFormat.Tests/
  DbxEmailFileWriterTests.cs     MODIFIED — Task 1: +Rebuild tests

tests/LEC.MailStore.Tests/
  MovedMessageCacheTests.cs      NEW — Task 2
  DbxStoreAddMessageTests.cs     NEW — Task 3
  DbxStoreTests.cs               MODIFIED — Task 4: +HideMovedMessageAsync filtering test

tests/LEC.App.Tests/
  FakeMailStore.cs               MODIFIED — Task 5: +AddMessageAsync/HideMovedMessageAsync in-memory
  MainWindowViewModelTests.cs    MODIFIED — Task 5: +MoveMessageAsync tests
```

`DbxFolderFileWriter.cs` is **not** touched by this plan — its low-level helpers (`WriteUInt32`/`WriteInt32`/`AddZeros`/`EncodeString`/`FitsDirect`/`DirectOrPointer`/`WriteIndexNode`) are deliberately duplicated into `DbxEmailFileWriter.cs` rather than shared, matching the project's existing precedent (the final review of the folder-creation slice explicitly called the equivalent duplication between the two writers "genuinely optional" to remove, not a defect) and keeping this plan from re-touching an already-shipped, already-reviewed file for a tangential reason.

---

### Task 1: `DbxEmailFileWriter.Rebuild`

**Files:**
- Modify: `src/LEC.DbxFormat/DbxEmailFileWriter.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxEmailFileWriterTests.cs`

**Interfaces:**
- Consumes: `DbxFormat.*` constants (existing, including `MessageBlockHeaderSize`/`MessageBlockMaxDataSize`/`MessageBlockSizeOffset`/`MessageBlockNextOffset`/`MessageBlockSelfOffsetOffset`/`MessageBlockMarkerOffset`/`MessageBlockDataOffset`), `DbxEmailFieldId` (existing), `DbxEmailFile.Open`/`.Messages`/`.ReadMessageRaw` (existing, used only by tests).
- Produces: `DbxMessageWriteEntry(byte[] RawBytes, string? Subject, string? SenderName, string? SenderAddress, string? MessageId, DateTime? SendTimeUtc, int Flags = 0)` (record) and `DbxEmailFileWriter.Rebuild(string path, IReadOnlyList<DbxMessageWriteEntry> messages) : void`. Both consumed by Task 3.

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.DbxFormat.Tests/DbxEmailFileWriterTests.cs` in full (this replaces the existing file, which currently only has `CreateEmpty` tests — keep those two tests, add the ones below):

```csharp
using System.Linq;
using System.Text;
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

    [Fact]
    public void RebuildRoundTripsASingleSmallMessage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-messages-{Guid.NewGuid():N}.dbx");
        try
        {
            byte[] raw = Encoding.UTF8.GetBytes("From: a@x.com\r\nSubject: Oi\r\n\r\nCorpo da mensagem.");
            var entry = new DbxMessageWriteEntry(raw, "Oi", "Ana", "ana@x.com", "<msg-1@x.com>",
                new DateTime(2004, 3, 1, 10, 0, 0, DateTimeKind.Utc));

            DbxEmailFileWriter.Rebuild(path, new[] { entry });

            using var file = DbxEmailFile.Open(path);
            Assert.Equal(StoreHealth.Ok, file.Health);
            var info = Assert.Single(file.Messages);

            Assert.Equal("Oi", info.Subject);
            Assert.Equal("Ana", info.SenderName);
            Assert.Equal("ana@x.com", info.SenderAddress);
            Assert.Equal("<msg-1@x.com>", info.MessageId);
            Assert.Equal(new DateTime(2004, 3, 1, 10, 0, 0, DateTimeKind.Utc), info.SendTimeUtc);
            Assert.Equal((uint)raw.Length, info.MessageSize);

            byte[] rawBack = file.ReadMessageRaw(info, out bool truncated);
            Assert.False(truncated);
            Assert.Equal(raw, rawBack);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildSplitsALargeMessageAcrossMultipleBlocksAndReadsItBackWhole()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-messages-{Guid.NewGuid():N}.dbx");
        try
        {
            // 3 full 512-byte blocks plus a partial 4th -- forces the writer's chaining path.
            byte[] raw = Enumerable.Range(0, DbxFormat.MessageBlockMaxDataSize * 3 + 100)
                .Select(i => (byte)(i % 251)).ToArray();
            var entry = new DbxMessageWriteEntry(raw, "Anexo grande", null, null, null, null);

            DbxEmailFileWriter.Rebuild(path, new[] { entry });

            using var file = DbxEmailFile.Open(path);
            var info = Assert.Single(file.Messages);
            byte[] rawBack = file.ReadMessageRaw(info, out bool truncated);

            Assert.False(truncated);
            Assert.Equal(raw, rawBack);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildRoundTripsMultipleMessagesPreservingOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-messages-{Guid.NewGuid():N}.dbx");
        try
        {
            var entries = new[]
            {
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("um"), "Um", null, null, null, null),
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("dois"), "Dois", null, null, null, null),
            };

            DbxEmailFileWriter.Rebuild(path, entries);

            using var file = DbxEmailFile.Open(path);
            Assert.Equal(2, file.Messages.Count);
            Assert.Contains(file.Messages, m => m.Subject == "Um");
            Assert.Contains(file.Messages, m => m.Subject == "Dois");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildThrowsWhenGivenMoreThan255Messages()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lec-messages-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Messages.dbx");
        try
        {
            var entries = Enumerable.Range(0, 256)
                .Select(i => new DbxMessageWriteEntry(Encoding.UTF8.GetBytes($"m{i}"), $"S{i}", null, null, null, null))
                .ToArray();

            Assert.Throws<ArgumentException>(() => DbxEmailFileWriter.Rebuild(path, entries));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RebuildOverwritesAnExistingFileAndLeavesNoTempFileBehind()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lec-messages-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Messages.dbx");
        try
        {
            DbxEmailFileWriter.Rebuild(path, new[] { new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("um"), "Um", null, null, null, null) });
            DbxEmailFileWriter.Rebuild(path, new[]
            {
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("um"), "Um", null, null, null, null),
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("dois"), "Dois", null, null, null, null),
            });

            using var file = DbxEmailFile.Open(path);
            Assert.Equal(2, file.Messages.Count);
            Assert.Single(Directory.GetFiles(dir)); // only Messages.dbx — no leftover .tmp-* file
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RebuildThrowsOnAnEmptyRawMessage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-messages-{Guid.NewGuid():N}.dbx");

        var entry = new DbxMessageWriteEntry(Array.Empty<byte>(), "Vazio", null, null, null, null);

        Assert.Throws<ArgumentException>(() => DbxEmailFileWriter.Rebuild(path, new[] { entry }));
        Assert.False(File.Exists(path));
    }
}
```

- [ ] **Step 2: Run tests to verify the new ones fail**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter "RebuildRoundTripsASingleSmallMessage|RebuildSplitsALargeMessageAcrossMultipleBlocksAndReadsItBackWhole|RebuildRoundTripsMultipleMessagesPreservingOrder|RebuildThrowsWhenGivenMoreThan255Messages|RebuildOverwritesAnExistingFileAndLeavesNoTempFileBehind|RebuildThrowsOnAnEmptyRawMessage"`
Expected: FAIL to compile — `DbxMessageWriteEntry`/`DbxEmailFileWriter.Rebuild` don't exist yet. (The two `CreateEmpty` tests keep passing once compilation succeeds — they're unchanged.)

- [ ] **Step 3: Implement `DbxMessageWriteEntry` and `DbxEmailFileWriter.Rebuild`**

Replace `src/LEC.DbxFormat/DbxEmailFileWriter.cs` in full:

```csharp
namespace LEC.DbxFormat;

/// <summary>
/// One message's worth of data to write into a rebuilt message-store .dbx.
/// MessageSize is not a field here -- the writer always computes it as
/// RawBytes.Length, so it can never drift from the bytes actually written.
/// ReceiveTimeUtc is not carried either: DbxMessageInfo.Read already resolves
/// Date = SendTimeUtc ?? ReceiveTimeUtc, so one value is enough to round-trip it.
/// </summary>
public sealed record DbxMessageWriteEntry(
    byte[] RawBytes,
    string? Subject,
    string? SenderName,
    string? SenderAddress,
    string? MessageId,
    DateTime? SendTimeUtc,
    int Flags = 0);

/// <summary>
/// Writes a brand-new, empty message-store .dbx file — the backing file for a folder
/// that was just created and has no messages yet. itemCount = 0 means the reader
/// (DbxEmailFile.Open) never attempts to walk an index tree, so no tree needs to exist:
/// the header alone is a complete, valid, empty file.
///
/// Rebuild rewrites a message-store .dbx from scratch as a single, flat index-tree node
/// (no children — ptrCount is one byte, so up to 255 messages fit) — same approach as
/// DbxFolderFileWriter.Rebuild, and its low-level helpers are deliberately duplicated
/// here rather than shared (see the plan's File Structure note for why).
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

    public static void Rebuild(string path, IReadOnlyList<DbxMessageWriteEntry> messages)
    {
        if (messages.Count > byte.MaxValue)
            throw new ArgumentException($"Message-store .dbx flat-node format holds at most {byte.MaxValue} messages; got {messages.Count}.", nameof(messages));

        foreach (var message in messages)
        {
            if (message.RawBytes.Length == 0)
                throw new ArgumentException("A message's raw bytes cannot be empty.", nameof(messages));
        }

        var bytes = new List<byte>(new byte[DbxFormat.HeaderIndexPointerOffset + 4]);

        WriteUInt32(bytes, 0, DbxFormat.SignatureWord0);
        WriteUInt32(bytes, 4, DbxFormat.SignatureWord1Email);
        WriteUInt32(bytes, 8, DbxFormat.SignatureWord2);
        WriteUInt32(bytes, 12, DbxFormat.SignatureWord3);

        var infoNodeOffsets = new int[messages.Count];
        for (int i = 0; i < messages.Count; i++)
        {
            int blockOffset = WriteMessageBlockChain(bytes, messages[i].RawBytes);
            infoNodeOffsets[i] = WriteInfoNode(bytes, messages[i], blockOffset);
        }

        int rootOffset = WriteIndexNode(bytes, infoNodeOffsets);

        WriteInt32(bytes, DbxFormat.HeaderItemCountOffset, messages.Count);
        WriteInt32(bytes, DbxFormat.HeaderIndexPointerOffset, rootOffset);

        string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(tempPath, bytes.ToArray());
        File.Move(tempPath, path, overwrite: true);
    }

    // Splits rawBytes into DbxFormat.MessageBlockMaxDataSize-byte blocks, writes each with
    // NextOffset = 0, then back-patches every block but the last to point at the one after
    // it -- same "reserve then patch" technique as WriteInfoNode's attribute array, just at
    // the granularity of whole blocks instead of individual attribute records. Returns the
    // first block's own offset (what the info-node's MessageBlockOffset attribute points to).
    private static int WriteMessageBlockChain(List<byte> bytes, byte[] rawBytes)
    {
        var blockStarts = new List<int>();
        int offset = 0;
        while (offset < rawBytes.Length)
        {
            int chunkSize = Math.Min(DbxFormat.MessageBlockMaxDataSize, rawBytes.Length - offset);
            byte[] chunk = rawBytes.AsSpan(offset, chunkSize).ToArray();
            blockStarts.Add(WriteOneMessageBlock(bytes, chunk));
            offset += chunkSize;
        }

        for (int i = 0; i < blockStarts.Count - 1; i++)
            WriteInt32(bytes, blockStarts[i] + DbxFormat.MessageBlockNextOffset, blockStarts[i + 1]);

        return blockStarts[0];
    }

    private static int WriteOneMessageBlock(List<byte> bytes, byte[] data)
    {
        int start = bytes.Count;
        AddZeros(bytes, DbxFormat.MessageBlockHeaderSize); // reserved: self-offset, marker, size, next (all patched below except next, left 0)

        WriteInt32(bytes, start + DbxFormat.MessageBlockSelfOffsetOffset, start);
        WriteInt32(bytes, start + DbxFormat.MessageBlockMarkerOffset, DbxFormat.MessageBlockMaxDataSize);
        WriteInt16(bytes, start + DbxFormat.MessageBlockSizeOffset, (short)data.Length);
        // NextOffset (start + MessageBlockNextOffset) stays 0 here; WriteMessageBlockChain
        // patches it once the following block's start offset is known, or leaves it 0 if
        // this is the chain's last block.

        bytes.AddRange(data);
        return start;
    }

    private static int WriteInfoNode(List<byte> bytes, DbxMessageWriteEntry message, int blockOffset)
    {
        int start = bytes.Count;

        var attrs = new List<(byte FieldId, bool Direct, uint DirectValue, byte[]? Data)>
        {
            DirectOrPointer(DbxEmailFieldId.MessageBlockOffset, blockOffset),
        };
        if (message.Subject is not null)
            attrs.Add(((byte)DbxEmailFieldId.Subject, false, 0, EncodeString(message.Subject)));
        if (message.SenderName is not null)
            attrs.Add(((byte)DbxEmailFieldId.SenderName, false, 0, EncodeString(message.SenderName)));
        if (message.SenderAddress is not null)
            attrs.Add(((byte)DbxEmailFieldId.SenderAddress, false, 0, EncodeString(message.SenderAddress)));
        if (message.MessageId is not null)
            attrs.Add(((byte)DbxEmailFieldId.MessageId, false, 0, EncodeString(message.MessageId)));
        if (message.SendTimeUtc is { } sendTime)
        {
            // Always pointer-form: the reader takes this field through ReadInt64 (an 8-byte
            // Windows FILETIME), which can never fit the packed record's 24-bit direct payload
            // regardless of value -- same reasoning DbxFolderFileWriter already applies to
            // ParentId, just for a wider field.
            attrs.Add(((byte)DbxEmailFieldId.SendCreateTime, false, 0, BitConverter.GetBytes(sendTime.ToFileTimeUtc())));
        }
        attrs.Add(DirectOrPointer(DbxEmailFieldId.MessageSize, (uint)message.RawBytes.Length));
        attrs.Add(DirectOrPointer(DbxEmailFieldId.Flags, message.Flags));

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

    // Direct-form embeds the value in the packed record's 24-bit payload
    // (DbxFormat.AttributeValueMask) — anything wider (or negative, for the int overload)
    // must go pointer-form instead, or it silently truncates. Same lesson DbxFolderFileWriter
    // already learned for Flags/FolderId/MessageCount, applied here from the start.
    private static bool FitsDirect(int value) => value >= 0 && (value & ~(int)DbxFormat.AttributeValueMask) == 0;
    private static bool FitsDirect(uint value) => (value & ~DbxFormat.AttributeValueMask) == 0;

    private static (byte FieldId, bool Direct, uint DirectValue, byte[]? Data) DirectOrPointer(DbxEmailFieldId fieldId, int value) =>
        FitsDirect(value)
            ? ((byte)fieldId, true, unchecked((uint)value), null)
            : ((byte)fieldId, false, 0, BitConverter.GetBytes(value));

    private static (byte FieldId, bool Direct, uint DirectValue, byte[]? Data) DirectOrPointer(DbxEmailFieldId fieldId, uint value) =>
        FitsDirect(value)
            ? ((byte)fieldId, true, value, null)
            : ((byte)fieldId, false, 0, BitConverter.GetBytes(value));

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

    private static void WriteInt16(List<byte> bytes, int offset, short value)
    {
        byte[] b = BitConverter.GetBytes(value);
        bytes[offset] = b[0];
        bytes[offset + 1] = b[1];
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter "DbxEmailFileWriterTests"`
Expected: PASS — all 8 tests (2 existing `CreateEmpty` + 6 new `Rebuild`).

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxEmailFileWriter.cs tests/LEC.DbxFormat.Tests/DbxEmailFileWriterTests.cs
git commit -m "Add DbxEmailFileWriter.Rebuild: writes a message-store .dbx with a real block chain per message"
```

---

### Task 2: `MovedMessageCache`

**Files:**
- Create: `src/LEC.MailStore/MovedMessageCache.cs`
- Test: `tests/LEC.MailStore.Tests/MovedMessageCacheTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Data.Sqlite` (existing dependency, already used by `ScavengeCache`).
- Produces: `MovedMessageCache(string databasePath)`, `.DefaultDatabasePath` (static), `.GetMovedOffsets(string profilePath, string folderFile) : HashSet<int>`, `.MarkMoved(string profilePath, string folderFile, int indexNodeOffset) : void`, `.Dispose()`. Consumed by Task 4.

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.MailStore.Tests/MovedMessageCacheTests.cs`:

```csharp
// tests/LEC.MailStore.Tests/MovedMessageCacheTests.cs
using Xunit;

namespace LEC.MailStore.Tests;

public class MovedMessageCacheTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lec-moved-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void GetMovedOffsetsReturnsEmptyWhenNothingMarked()
    {
        using var cache = new MovedMessageCache(TempDbPath());

        var offsets = cache.GetMovedOffsets(@"C:\profiles\BCP", "Caixa de entrada.dbx");

        Assert.Empty(offsets);
    }

    [Fact]
    public void MarkMovedThenGetMovedOffsetsRoundTrips()
    {
        string dbPath = TempDbPath();

        using (var cache = new MovedMessageCache(dbPath))
            cache.MarkMoved(@"C:\profiles\BCP", "Caixa de entrada.dbx", 500);

        using (var reopened = new MovedMessageCache(dbPath))
        {
            var offsets = reopened.GetMovedOffsets(@"C:\profiles\BCP", "Caixa de entrada.dbx");
            Assert.Contains(500, offsets);
        }
    }

    [Fact]
    public void MarkMovedTwiceForTheSameOffsetIsIdempotent()
    {
        using var cache = new MovedMessageCache(TempDbPath());

        cache.MarkMoved(@"C:\p", "f.dbx", 100);
        cache.MarkMoved(@"C:\p", "f.dbx", 100); // must not throw

        Assert.Single(cache.GetMovedOffsets(@"C:\p", "f.dbx"));
    }

    [Fact]
    public void DifferentProfilesWithTheSameFolderFileNameDoNotCollide()
    {
        using var cache = new MovedMessageCache(TempDbPath());

        cache.MarkMoved(@"C:\profileA", "Caixa de entrada.dbx", 1);
        cache.MarkMoved(@"C:\profileB", "Caixa de entrada.dbx", 2);

        Assert.Equal(new[] { 1 }, cache.GetMovedOffsets(@"C:\profileA", "Caixa de entrada.dbx"));
        Assert.Equal(new[] { 2 }, cache.GetMovedOffsets(@"C:\profileB", "Caixa de entrada.dbx"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "MovedMessageCacheTests"`
Expected: FAIL to compile — `MovedMessageCache` doesn't exist yet.

- [ ] **Step 3: Implement `MovedMessageCache`**

Create `src/LEC.MailStore/MovedMessageCache.cs`:

```csharp
// src/LEC.MailStore/MovedMessageCache.cs
using Microsoft.Data.Sqlite;

namespace LEC.MailStore;

/// <summary>
/// Tracks messages "moved" (copied elsewhere, never deleted from their original .dbx —
/// see docs/superpowers/specs/2026-08-04-mover-mensagem-motor-design.md §2) so
/// DbxStore.EnumerateHeadersAsync can hide them from their original folder without ever
/// touching the source .dbx. Same pattern as ScavengeCache: lightweight SQLite under
/// %LOCALAPPDATA%\LEC\, never next to the source profile.
/// </summary>
public sealed class MovedMessageCache : IDisposable
{
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LEC", "moved-cache.db");

    private readonly SqliteConnection _connection;

    public MovedMessageCache(string databasePath)
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
            CREATE TABLE IF NOT EXISTS moved_message (
                profile_path      TEXT    NOT NULL,
                folder_file       TEXT    NOT NULL,
                index_node_offset INTEGER NOT NULL,
                PRIMARY KEY (profile_path, folder_file, index_node_offset)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public HashSet<int> GetMovedOffsets(string profilePath, string folderFile)
    {
        var offsets = new HashSet<int>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT index_node_offset FROM moved_message WHERE profile_path = $p AND folder_file = $f";
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            offsets.Add(reader.GetInt32(0));

        return offsets;
    }

    public void MarkMoved(string profilePath, string folderFile, int indexNodeOffset)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO moved_message (profile_path, folder_file, index_node_offset)
            VALUES ($p, $f, $offset)
            """;
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);
        cmd.Parameters.AddWithValue("$offset", indexNodeOffset);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "MovedMessageCacheTests"`
Expected: PASS — all 4 tests.

Run: `dotnet test`
Expected: PASS — no regression.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/MovedMessageCache.cs tests/LEC.MailStore.Tests/MovedMessageCacheTests.cs
git commit -m "Add MovedMessageCache: local, profile-scoped record of messages hidden after a move"
```

---

### Task 3: `IMailStore.AddMessageAsync`

**Files:**
- Modify: `src/LEC.MailStore/IMailStore.cs`
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Test: `tests/LEC.MailStore.Tests/DbxStoreAddMessageTests.cs`

**Interfaces:**
- Consumes: `DbxEmailFileWriter.Rebuild`/`DbxMessageWriteEntry` (Task 1), `DbxEmailFile.Open`/`.Messages`/`.ReadMessageRaw` (existing).
- Produces: `IMailStore.AddMessageAsync(int folderId, byte[] rawMessage, string? subject, string? senderName, string? senderAddress, string? messageId, DateTime? dateUtc, CancellationToken ct = default) : Task`. Consumed by Task 5.

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.MailStore.Tests/DbxStoreAddMessageTests.cs`:

```csharp
// tests/LEC.MailStore.Tests/DbxStoreAddMessageTests.cs
using System.Linq;
using System.Text;
using LEC.DbxFormat;
using LEC.DbxFormat.Tests;
using LEC.MailStore;
using Xunit;

namespace LEC.MailStore.Tests;

public class DbxStoreAddMessageTests
{
    private static string BuildProfileWithOneEmptyFolder()
    {
        var folders = new TestDbxBuilder();
        folders.WriteHeader(DbxFileType.Folder);
        int node = folders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Data = BitConverter.GetBytes(-1) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Destino\0"u8.ToArray() },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = "Destino.dbx\0"u8.ToArray() },
        });
        int root = folders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: node, entryNextTable: 0, entryIndexCount: 0) });
        folders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        folders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", folders));
        DbxEmailFileWriter.CreateEmpty(Path.Combine(dir, "Destino.dbx"));
        return dir;
    }

    [Fact]
    public async Task AddMessageAsyncAppendsToAnEmptyFolderAndPersistsAfterReopen()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        try
        {
            byte[] raw = Encoding.UTF8.GetBytes("From: a@x.com\r\nSubject: Oi\r\n\r\nCorpo.");

            using (var store = DbxStore.Open(dir))
            {
                int folderId = store.Folders.Single().Id;
                await store.AddMessageAsync(folderId, raw, "Oi", "Ana", "ana@x.com", "<m1@x.com>",
                    new DateTime(2004, 3, 1, 10, 0, 0, DateTimeKind.Utc));
            }

            using var reopened = DbxStore.Open(dir);
            var folder = reopened.Folders.Single();
            var headers = await CollectAsync(reopened.EnumerateHeadersAsync(folder));

            var header = Assert.Single(headers);
            Assert.Equal("Oi", header.Subject);
            Assert.Equal("Ana", header.SenderName);

            using var stream = await reopened.OpenMessageAsync(header.Id);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(raw, ms.ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddMessageAsyncOnANonexistentFolderThrows()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.AddMessageAsync(999, Encoding.UTF8.GetBytes("x"), null, null, null, null, null));
            Assert.Contains("não encontrada", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddMessageAsyncOnAFolderWithNoBackingFileThrows()
    {
        var localFolders = new TestDbxBuilder();
        localFolders.WriteHeader(DbxFileType.Folder);
        int node = localFolders.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = "Pastas locais\0"u8.ToArray() },
        });
        int root = localFolders.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: node, entryNextTable: 0, entryIndexCount: 0) });
        localFolders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, root);

        string dir = TestProfileBuilder.WriteProfile(("Folders.dbx", localFolders));
        try
        {
            using var store = DbxStore.Open(dir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.AddMessageAsync(1, Encoding.UTF8.GetBytes("x"), null, null, null, null, null));
            Assert.Contains("arquivo de apoio", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddMessageAsyncWhenTheDestinationFileIsDamagedThrows()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        try
        {
            // itemCount = 1 but no message actually written -- DbxEmailFile.Open flags this Partial.
            File.WriteAllBytes(Path.Combine(dir, "Destino.dbx"), BuildDamagedEmailDbx());

            using var store = DbxStore.Open(dir);
            int folderId = store.Folders.Single().Id;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.AddMessageAsync(folderId, Encoding.UTF8.GetBytes("x"), null, null, null, null, null));
            Assert.Contains("danificado", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddMessageAsyncCreatesBackupsOfTheDestinationFile()
    {
        string dir = BuildProfileWithOneEmptyFolder();
        try
        {
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(dir, "Destino.dbx"));
            using var store = DbxStore.Open(dir);
            int folderId = store.Folders.Single().Id;

            await store.AddMessageAsync(folderId, Encoding.UTF8.GetBytes("x"), "S", null, null, null, null);

            Assert.True(File.Exists(Path.Combine(dir, "Destino.dbx.bak")));
            Assert.True(File.Exists(Path.Combine(dir, "Destino.dbx.original")));
            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(dir, "Destino.dbx.original")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] BuildDamagedEmailDbx()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, 999999); // points nowhere real
        return builder.ToArray();
    }

    private static async Task<List<MessageHeader>> CollectAsync(IAsyncEnumerable<MessageHeader> source)
    {
        var list = new List<MessageHeader>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreAddMessageTests"`
Expected: FAIL to compile — `DbxStore.AddMessageAsync`/`IMailStore.AddMessageAsync` don't exist yet.

- [ ] **Step 3: Add `AddMessageAsync` to `IMailStore`**

In `src/LEC.MailStore/IMailStore.cs`, replace:

```csharp
    /// <summary>
    /// Creates a folder — a real backing file plus a new Folders.dbx entry for DbxStore.
    /// See DbxStore.CreateFolderAsync for the full contract (validation, backups,
    /// exceptions thrown).
    /// </summary>
    Task<StoreFolder> CreateFolderAsync(string name, int? parentId, CancellationToken ct = default);
}
```

with:

```csharp
    /// <summary>
    /// Creates a folder — a real backing file plus a new Folders.dbx entry for DbxStore.
    /// See DbxStore.CreateFolderAsync for the full contract (validation, backups,
    /// exceptions thrown).
    /// </summary>
    Task<StoreFolder> CreateFolderAsync(string name, int? parentId, CancellationToken ct = default);

    /// <summary>
    /// Appends a message to a folder in THIS store — a from-scratch rewrite of that
    /// folder's backing .dbx file. Has no idea where the raw bytes came from; a
    /// cross-store "move message" orchestration (spanning two IMailStore instances) is
    /// responsible for reading them from wherever the message currently lives first.
    /// See DbxStore.AddMessageAsync for the full contract.
    /// </summary>
    Task AddMessageAsync(int folderId, byte[] rawMessage, string? subject, string? senderName,
        string? senderAddress, string? messageId, DateTime? dateUtc, CancellationToken ct = default);
}
```

- [ ] **Step 4: Add `AddMessageAsync` to `DbxStore`**

In `src/LEC.MailStore/DbxStore.cs`, add this method right after `CreateFolderAsync` (before `public static DbxStore Open(...)`):

```csharp
    /// <summary>
    /// Appends a message to a folder — a from-scratch rewrite of that folder's backing
    /// .dbx (see DbxEmailFileWriter.Rebuild), same "flat tree, ≤255 items" approach as
    /// CreateFolderAsync/Folders.dbx.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when: <paramref name="folderId"/> does not match any existing folder; that
    /// folder has no backing file; or the backing .dbx is damaged (<c>Health != StoreHealth.Ok</c>),
    /// since rebuilding it in that state would silently discard whatever messages this
    /// reader couldn't parse. If the folder already has 255 messages, the underlying
    /// <see cref="DbxEmailFileWriter.Rebuild"/> throws <see cref="ArgumentException"/> instead
    /// — not caught or rewrapped here.
    /// </exception>
    public Task AddMessageAsync(int folderId, byte[] rawMessage, string? subject, string? senderName,
        string? senderAddress, string? messageId, DateTime? dateUtc, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_folders.Any(f => f.Id == folderId))
                throw new InvalidOperationException($"Pasta {folderId} não encontrada.");

            if (!_backingFileNames.TryGetValue(folderId, out var fileName) || fileName is null)
                throw new InvalidOperationException($"Pasta {folderId} não tem arquivo de apoio.");

            string filePath = Path.Combine(_profileDirectory, fileName);

            List<DbxMessageWriteEntry> entries;
            using (var current = DbxEmailFile.Open(filePath))
            {
                if (current.Health != StoreHealth.Ok)
                    throw new InvalidOperationException(
                        $"\"{fileName}\" está danificado; adicionar uma mensagem reescreveria o arquivo e descartaria as mensagens ilegíveis.");

                entries = current.Messages
                    .Select(info => new DbxMessageWriteEntry(
                        current.ReadMessageRaw(info, out _), info.Subject, info.SenderName, info.SenderAddress,
                        info.MessageId, info.SendTimeUtc ?? info.ReceiveTimeUtc, info.Flags))
                    .ToList();
            }

            string pristinePath = filePath + ".original";
            if (!File.Exists(pristinePath))
                File.Copy(filePath, pristinePath);
            File.Copy(filePath, filePath + ".bak", overwrite: true);

            entries.Add(new DbxMessageWriteEntry(rawMessage, subject, senderName, senderAddress, messageId, dateUtc));
            DbxEmailFileWriter.Rebuild(filePath, entries);
        }, ct);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "DbxStoreAddMessageTests"`
Expected: PASS — all 5 tests.

Run: `dotnet test`
Expected: PASS — no regression in any project (this includes `FakeMailStore` in `LEC.App.Tests`, which does **not** yet implement `IMailStore.AddMessageAsync` — Task 5 adds that; if this step fails to compile because of `FakeMailStore`, stop and re-read Task 5 before proceeding, since Tasks are meant to be applied in order and Task 5 depends on this one already being done).

- [ ] **Step 6: Commit**

```bash
git add src/LEC.MailStore/IMailStore.cs src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreAddMessageTests.cs
git commit -m "Add DbxStore.AddMessageAsync: appends a message to a folder, rebuilding its backing .dbx"
```

---

### Task 4: `IMailStore.HideMovedMessageAsync` + `EnumerateHeadersAsync` filtering

**Files:**
- Modify: `src/LEC.MailStore/IMailStore.cs`
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Modify: `tests/LEC.MailStore.Tests/DbxStoreTests.cs`

**Interfaces:**
- Consumes: `MovedMessageCache` (Task 2).
- Produces: `IMailStore.HideMovedMessageAsync(MessageRef id, CancellationToken ct = default) : Task`. Consumed by Task 5. `EnumerateHeadersAsync` (existing, modified) now skips hidden messages for every caller, including Task 5's orchestration and the existing UI.

- [ ] **Step 1: Write the failing test**

In `tests/LEC.MailStore.Tests/DbxStoreTests.cs`, add at the end of the class, before the last `}`:

```csharp
    [Fact]
    public async Task HideMovedMessageAsyncRemovesTheMessageFromEnumerateHeadersAsyncAndPersistsAcrossReopen()
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
        localFolders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, root);

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
        inboxMessages.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        inboxMessages.SetInt32At(DbxFormat.HeaderIndexPointerOffset, msgRoot);
        profile.WriteEmailDbx("Inbox.dbx", inboxMessages.ToArray());

        string movedCacheDbPath = Path.Combine(Path.GetTempPath(), $"lec-moved-{Guid.NewGuid():N}.db");
        try
        {
            StoreFolder folder;
            using (var store = DbxStore.Open(profile.ProfileDirectory, movedMessageCacheDatabasePath: movedCacheDbPath))
            {
                folder = store.Folders.Single();

                var headersBefore = await CollectAsync(store.EnumerateHeadersAsync(folder));
                var header = Assert.Single(headersBefore);

                await store.HideMovedMessageAsync(header.Id);

                var headersAfterSameSession = await CollectAsync(store.EnumerateHeadersAsync(folder));
                Assert.Empty(headersAfterSameSession);
            }

            using var reopened = DbxStore.Open(profile.ProfileDirectory, movedMessageCacheDatabasePath: movedCacheDbPath);
            var headersAfterReopen = await CollectAsync(reopened.EnumerateHeadersAsync(folder));
            Assert.Empty(headersAfterReopen);
        }
        finally
        {
            File.Delete(movedCacheDbPath);
        }
    }

    private static async Task<List<MessageHeader>> CollectAsync(IAsyncEnumerable<MessageHeader> source)
    {
        var list = new List<MessageHeader>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
```

(If `DbxStoreTests.cs` already has a private `CollectAsync` helper from an earlier test, don't add a second one — reuse it. Check the file before adding.)

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "HideMovedMessageAsyncRemovesTheMessageFromEnumerateHeadersAsyncAndPersistsAcrossReopen"`
Expected: FAIL to compile — `HideMovedMessageAsync` and the `movedMessageCacheDatabasePath` parameter on `DbxStore.Open` don't exist yet.

- [ ] **Step 3: Add `HideMovedMessageAsync` to `IMailStore`**

In `src/LEC.MailStore/IMailStore.cs`, add after the `AddMessageAsync` member added in Task 3 (still before the interface's closing `}`):

```csharp

    /// <summary>
    /// Marks a message as moved away from its folder — EnumerateHeadersAsync stops
    /// returning it from now on. Never writes to the backing .dbx; this is purely a
    /// local, app-side "hide" marker (see MovedMessageCache). A no-op, not an error, if
    /// called twice for the same message.
    /// </summary>
    Task HideMovedMessageAsync(MessageRef id, CancellationToken ct = default);
```

- [ ] **Step 4: Wire `MovedMessageCache` into `DbxStore` and add `HideMovedMessageAsync`**

In `src/LEC.MailStore/DbxStore.cs`, replace the field declarations and constructor:

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

with:

```csharp
    private readonly List<StoreFolder> _folders;
    public IReadOnlyList<StoreFolder> Folders => _folders;
    public StoreHealth Health { get; }

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

Replace the `Open` method and its `OpenScavengeCacheWithFallback` neighbor:

```csharp
    public static DbxStore Open(string profileDirectoryPath, string? scavengeCacheDatabasePath = null)
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
        var scavengeCache = OpenScavengeCacheWithFallback(scavengeCacheDatabasePath ?? ScavengeCache.DefaultDatabasePath);
        return new DbxStore(profileDirectoryPath, folders, backingFileNames, health, scavengeCache);
    }

    // Global Constraint (design §8): cache unavailable (AppData not writable, disk full,
    // locked by another instance) must not block the app -- scavenging still works for
    // this session, it just won't survive a restart. SQLite's ":memory:" database is a
    // real, fully-functional SqliteConnection with the exact same schema/API, so falling
    // back to it needs no separate no-op/null implementation of ScavengeCache.
    private static ScavengeCache OpenScavengeCacheWithFallback(string databasePath)
    {
        try
        {
            return new ScavengeCache(databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // ScavengeCache's constructor wraps whatever path it's given as
            // "Data Source={path}" -- ":memory:" is itself a complete, valid connection
            // string value for SQLite's in-memory mode, so pass it bare, not pre-wrapped.
            return new ScavengeCache(":memory:");
        }
    }
```

with:

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
            });
            backingFileNames[f.FolderId] = fileName;
        }

        var health = folderFile.Health; // read before the `using` disposes folderFile below
        var scavengeCache = OpenScavengeCacheWithFallback(scavengeCacheDatabasePath ?? ScavengeCache.DefaultDatabasePath);
        var movedMessageCache = OpenMovedMessageCacheWithFallback(movedMessageCacheDatabasePath ?? MovedMessageCache.DefaultDatabasePath);
        return new DbxStore(profileDirectoryPath, folders, backingFileNames, health, scavengeCache, movedMessageCache);
    }

    // Global Constraint (design §8): cache unavailable (AppData not writable, disk full,
    // locked by another instance) must not block the app -- scavenging still works for
    // this session, it just won't survive a restart. SQLite's ":memory:" database is a
    // real, fully-functional SqliteConnection with the exact same schema/API, so falling
    // back to it needs no separate no-op/null implementation of ScavengeCache.
    private static ScavengeCache OpenScavengeCacheWithFallback(string databasePath)
    {
        try
        {
            return new ScavengeCache(databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // ScavengeCache's constructor wraps whatever path it's given as
            // "Data Source={path}" -- ":memory:" is itself a complete, valid connection
            // string value for SQLite's in-memory mode, so pass it bare, not pre-wrapped.
            return new ScavengeCache(":memory:");
        }
    }

    // Same fallback reasoning as OpenScavengeCacheWithFallback, for the moved-message cache.
    private static MovedMessageCache OpenMovedMessageCacheWithFallback(string databasePath)
    {
        try
        {
            return new MovedMessageCache(databasePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new MovedMessageCache(":memory:");
        }
    }
```

Replace `EnumerateHeadersAsync`'s start:

```csharp
    public async IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var emailFile = EnsureFolderOpen(folder);
        if (emailFile is null)
            yield break;

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
```

with:

```csharp
    public async IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var emailFile = EnsureFolderOpen(folder);
        if (emailFile is null)
            yield break;

        if (!_messagesByFolder.TryGetValue(folder.Id, out var infoByRef))
        {
            infoByRef = new Dictionary<MessageRef, DbxMessageInfo>();
            _messagesByFolder[folder.Id] = infoByRef;
        }

        EnsureMovedLoaded(folder);
        var hiddenOffsets = _hiddenOffsetsByFolder[folder.Id];

        foreach (var info in emailFile.Messages)
        {
            ct.ThrowIfCancellationRequested();
            if (hiddenOffsets.Contains(info.IndexNodeOffset))
                continue;

            var reference = new MessageRef(folder.Id, info.IndexNodeOffset);
            infoByRef[reference] = info;

            yield return new MessageHeader
```

(The rest of `EnumerateHeadersAsync` — the recovered-items block after the main loop — is unchanged.)

Add `EnsureMovedLoaded` and `HideMovedMessageAsync` right after the existing `EnsureScavengeLoaded` method:

```csharp
    private void EnsureMovedLoaded(StoreFolder folder)
    {
        if (_hiddenOffsetsByFolder.ContainsKey(folder.Id))
            return;

        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
        {
            _hiddenOffsetsByFolder[folder.Id] = new HashSet<int>();
            return;
        }

        _hiddenOffsetsByFolder[folder.Id] = _movedMessageCache.GetMovedOffsets(_profileDirectory, fileName);
    }

    public Task HideMovedMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!_backingFileNames.TryGetValue(id.FolderId, out var fileName) || fileName is null)
                return;

            _movedMessageCache.MarkMoved(_profileDirectory, fileName, id.IndexNodeOffset);

            if (!_hiddenOffsetsByFolder.TryGetValue(id.FolderId, out var offsets))
            {
                offsets = new HashSet<int>();
                _hiddenOffsetsByFolder[id.FolderId] = offsets;
            }
            offsets.Add(id.IndexNodeOffset);
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
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "HideMovedMessageAsyncRemovesTheMessageFromEnumerateHeadersAsyncAndPersistsAcrossReopen"`
Expected: PASS.

Run: `dotnet test`
Expected: PASS — no regression (again, `LEC.App.Tests`' `FakeMailStore` doesn't implement `HideMovedMessageAsync` yet; that's Task 5).

- [ ] **Step 6: Commit**

```bash
git add src/LEC.MailStore/IMailStore.cs src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreTests.cs
git commit -m "Add DbxStore.HideMovedMessageAsync: hides a message from EnumerateHeadersAsync via MovedMessageCache"
```

---

### Task 5: `MainWindowViewModel.MoveMessageAsync`

**Files:**
- Modify: `src/LEC.App/ViewModels/MainWindowViewModel.cs`
- Modify: `tests/LEC.App.Tests/FakeMailStore.cs`
- Modify: `tests/LEC.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `IMailStore.OpenMessageAsync` (existing), `IMailStore.AddMessageAsync` (Task 3), `IMailStore.HideMovedMessageAsync` (Task 4), `ProfileViewModel.Store` (existing).
- Produces: `MainWindowViewModel.MoveMessageAsync(ProfileViewModel sourceProfile, MessageHeader message, ProfileViewModel destinationProfile, int destinationFolderId, CancellationToken ct = default) : Task`. Last method of this slice — the UI slice (fatia 2, not part of this plan) will call it from a click handler.

- [ ] **Step 1: Write the failing tests**

First, `FakeMailStore` needs to implement the two new `IMailStore` members before it compiles at all (it already fails to compile against `IMailStore` after Tasks 3-4 — this step fixes that as part of adding the test infrastructure this task's own tests need). In `tests/LEC.App.Tests/FakeMailStore.cs`, add right after the existing `CreateFolderAsync` method (before `public bool WasDisposed`):

```csharp

    public Exception? ExceptionToThrowOnAddMessage { get; set; }
    public List<(int FolderId, byte[] RawMessage, string? Subject, string? SenderName, string? SenderAddress, string? MessageId, DateTime? DateUtc)> AddedMessages { get; } = new();

    public Task AddMessageAsync(int folderId, byte[] rawMessage, string? subject, string? senderName,
        string? senderAddress, string? messageId, DateTime? dateUtc, CancellationToken ct = default)
    {
        if (ExceptionToThrowOnAddMessage is { } ex)
            throw ex;

        AddedMessages.Add((folderId, rawMessage, subject, senderName, senderAddress, messageId, dateUtc));
        return Task.CompletedTask;
    }

    public HashSet<MessageRef> HiddenMessageRefs { get; } = new();

    public Task HideMovedMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        HiddenMessageRefs.Add(id);
        return Task.CompletedTask;
    }
```

Then, in `tests/LEC.App.Tests/MainWindowViewModelTests.cs`, add at the end of the class, before the last `}` (check the file's existing top-of-class helpers first — reuse any existing `SampleFolder`/similar builder instead of duplicating one):

```csharp
    [Fact]
    public async Task MoveMessageAsyncCopiesToDestinationAndHidesFromSource()
    {
        var sourceFolder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true };
        byte[] raw = "conteudo"u8.ToArray();
        var messageRef = new MessageRef(sourceFolder.Id, 500);
        var sourceStore = new FakeMailStore(
            folders: new[] { sourceFolder },
            rawByRef: new Dictionary<MessageRef, byte[]> { [messageRef] = raw });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destFolder = new StoreFolder { Id = 10, Name = "Destino", IsLoadable = true };
        var destStore = new FakeMailStore(folders: new[] { destFolder });
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var header = new MessageHeader { Id = messageRef, Subject = "Assunto", SenderName = "Ana", SenderAddress = "ana@x.com", MessageId = "<m1>", Date = new DateTime(2004, 1, 1) };
        var viewModel = new MainWindowViewModel();

        await viewModel.MoveMessageAsync(sourceProfile, header, destProfile, destFolder.Id);

        var added = Assert.Single(destStore.AddedMessages);
        Assert.Equal(destFolder.Id, added.FolderId);
        Assert.Equal(raw, added.RawMessage);
        Assert.Equal("Assunto", added.Subject);
        Assert.Equal("Ana", added.SenderName);

        Assert.Contains(messageRef, sourceStore.HiddenMessageRefs);
    }

    [Fact]
    public async Task MoveMessageAsyncWorksWhenSourceAndDestinationAreTheSameProfile()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true };
        var messageRef = new MessageRef(folderA.Id, 500);
        var store = new FakeMailStore(
            folders: new[] { folderA, folderB },
            rawByRef: new Dictionary<MessageRef, byte[]> { [messageRef] = "x"u8.ToArray() });
        var profile = new ProfileViewModel("P", @"C:\perfis\P", store);

        var header = new MessageHeader { Id = messageRef, Subject = "S" };
        var viewModel = new MainWindowViewModel();

        await viewModel.MoveMessageAsync(profile, header, profile, folderB.Id);

        Assert.Single(store.AddedMessages);
        Assert.Contains(messageRef, store.HiddenMessageRefs);
    }

    [Fact]
    public async Task MoveMessageAsyncDoesNotHideFromSourceWhenTheDestinationAddFails()
    {
        var sourceFolder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true };
        var messageRef = new MessageRef(sourceFolder.Id, 500);
        var sourceStore = new FakeMailStore(
            folders: new[] { sourceFolder },
            rawByRef: new Dictionary<MessageRef, byte[]> { [messageRef] = "x"u8.ToArray() });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destFolder = new StoreFolder { Id = 10, Name = "Destino", IsLoadable = true };
        var destStore = new FakeMailStore(folders: new[] { destFolder })
        {
            ExceptionToThrowOnAddMessage = new InvalidOperationException("destino cheio"),
        };
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var header = new MessageHeader { Id = messageRef, Subject = "S" };
        var viewModel = new MainWindowViewModel();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.MoveMessageAsync(sourceProfile, header, destProfile, destFolder.Id));

        Assert.Empty(sourceStore.HiddenMessageRefs);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter "MoveMessageAsyncCopiesToDestinationAndHidesFromSource|MoveMessageAsyncWorksWhenSourceAndDestinationAreTheSameProfile|MoveMessageAsyncDoesNotHideFromSourceWhenTheDestinationAddFails"`
Expected: FAIL to compile — `MainWindowViewModel.MoveMessageAsync` doesn't exist yet.

- [ ] **Step 3: Add `MoveMessageAsync` to `MainWindowViewModel`**

In `src/LEC.App/ViewModels/MainWindowViewModel.cs`, add at the end of the class, right after `RunScavengeAsync` (before the class's closing `}`):

```csharp

    /// <summary>
    /// Copies a message into another folder (possibly in a different open profile) and
    /// hides it from its source folder — never deletes or rewrites the source .dbx. Order
    /// matters: the source is only hidden after the destination write succeeds, so a
    /// failed add leaves both sides exactly as they were.
    /// </summary>
    public async Task MoveMessageAsync(ProfileViewModel sourceProfile, MessageHeader message,
        ProfileViewModel destinationProfile, int destinationFolderId, CancellationToken ct = default)
    {
        using var stream = await sourceProfile.Store.OpenMessageAsync(message.Id, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);

        await destinationProfile.Store.AddMessageAsync(destinationFolderId, ms.ToArray(),
            message.Subject, message.SenderName, message.SenderAddress, message.MessageId, message.Date, ct);

        await sourceProfile.Store.HideMovedMessageAsync(message.Id, ct);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter "MoveMessageAsyncCopiesToDestinationAndHidesFromSource|MoveMessageAsyncWorksWhenSourceAndDestinationAreTheSameProfile|MoveMessageAsyncDoesNotHideFromSourceWhenTheDestinationAddFails"`
Expected: PASS — all 3 new tests.

Run: `dotnet test`
Expected: PASS — the full solution, no regressions anywhere.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/MainWindowViewModel.cs tests/LEC.App.Tests/FakeMailStore.cs tests/LEC.App.Tests/MainWindowViewModelTests.cs
git commit -m "Add MainWindowViewModel.MoveMessageAsync: copies a message to another folder/profile, hides it from the source"
```

---

## Self-Review Notes

- **Cobertura do design doc:** §2 (decisões de escopo: nunca apaga da origem, destino ≤255, esconder via cache local, funciona entre perfis) → refletido em todas as tasks e nos Global Constraints. §3 (`Rebuild`, cadeia de blocos, datas por ponteiro de 8 bytes) → Task 1. §4 (`AddMessageAsync`) → Task 3. §5 (`MovedMessageCache`/`HideMovedMessageAsync`) → Tasks 2 e 4. §6 (`MoveMessageAsync`, ordem add-depois-hide) → Task 5. §7 (erros) → testes de guarda nas Tasks 3-5. §8 (testes) → cada teste do design tem um `[Fact]` correspondente.
- **Consistência de tipos:** `DbxMessageWriteEntry(byte[] RawBytes, string? Subject, string? SenderName, string? SenderAddress, string? MessageId, DateTime? SendTimeUtc, int Flags = 0)` (Task 1) é o mesmo record usado em `DbxStore.AddMessageAsync` (Task 3) pra ler mensagens existentes E pra empacotar a nova. `IMailStore.AddMessageAsync`/`HideMovedMessageAsync` (Tasks 3-4) têm a mesma assinatura em `DbxStore` e em `FakeMailStore` (Task 5). `MainWindowViewModel.MoveMessageAsync` (Task 5) usa exatamente os parâmetros de `AddMessageAsync`/`HideMovedMessageAsync` já definidos.
- **Ordem de dependências:** Task 1 e Task 2 não dependem de nada (podem ser feitas em qualquer ordem entre si). Task 3 depende só da Task 1. Task 4 depende só da Task 2. Task 5 depende das Tasks 3 e 4 (precisa dos dois métodos em `IMailStore` pra `FakeMailStore` compilar, e pra `MoveMessageAsync` chamar ambos). Isso é uma dependência cruzada documentada: rodar as tasks fora de ordem quebra a compilação de `LEC.App.Tests` (mencionado explicitamente no Step 5 das Tasks 3 e 4).
- **Fora de escopo confirmado:** nenhuma task apaga ou reescreve um arquivo de origem; nenhuma task atualiza `Folders.dbx`/`MessageCount`; nenhuma task toca em `MainWindow.xaml`/`.xaml.cs` — UI é a fatia 2, não coberta por este plano.
