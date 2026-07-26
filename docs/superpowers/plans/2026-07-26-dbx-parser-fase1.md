# Fase 1 — Parser `.dbx` verificado — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a verified, hardened, from-scratch reader for Outlook Express `.dbx` files (both message stores and `Folders.dbx`) as a .NET 9 class library, exercised by a command-line tool, with zero UI. This is Fase 1 of `ARQUITETURA.md` — the foundation every later phase depends on.

**Architecture:** A layered set of small, single-purpose readers — index B-tree traversal, generic attribute-record decoding, message block-chain reassembly — built on a testable `IDbxByteSource` abstraction (an in-memory `byte[]` for unit tests, a memory-mapped file for production). Two top-level orchestrators (`DbxEmailFile`, `DbxFolderFile`) combine the readers and report `StoreHealth` per §7.4: a parse never throws on malformed *content*, it collects what it can and marks the result `Partial`.

**Tech Stack:** .NET 9 (`net9.0`), C#, xUnit, `System.Text.Encoding.CodePages` (for the windows-1252 fallback in §7.6), `System.IO.MemoryMappedFiles` (BCL).

## Verification against real files

`ARQUITETURA.md` §7.2 explicitly forbids coding the `.dbx` parser from memorized constants — they must be checked against a real file first. This was done before writing this plan, against the user's own files in `BCP/`:

- **Signature** (`Folders.dbx`, `Caixa de entrada.dbx`): confirmed the 16-byte signature and the `0xC5`-email / `0xC6`-folder discriminator byte at offset 4, byte-for-byte, via hex dump.
- **Header fields** (`ITEM_COUNT` at `0xC4`, `INDEX_POINTER` at `0xE4`): confirmed against three different real files.
- **Index B-tree node layout** (24-byte header + 12-byte entries): confirmed by manually walking the root node of `Folders.dbx` at its real index pointer and cross-checking every field.
- **Reference implementation**: [`ZungBang/undbx`](https://github.com/ZungBang/undbx) (`dbxread.c`/`dbxread.h`, GPL-3.0, itself based on Ulrich Krebs' DbxConv and Arne Schloh's `oedbx` reverse-engineering docs — the same canonical source `ARQUITETURA.md` §7.2 names) was fetched and read in full. It gives the exact byte offsets for the email-message attribute table, the index tree walk, and the message block-chain format. **It does not parse `Folders.dbx` content at all** — it only detects the folder signature and stops.
- **Folder attribute IDs were therefore reverse-engineered independently**, empirically, against the real `BCP/Folders.dbx` (26 real folders): a throwaway probe dumped every attribute of every info node and the result was read against the real folder/file names on disk (`Caixa de entrada`, `Enviados 2002`, `Rascunhos`, ...). The mapping (field `0x00`=folder id, `0x01`=parent id, `0x02`=name, `0x03`=backing filename, `0x07`=message count, `0x09`=special-folder type) fell out unambiguously — e.g. field `0x09` is `1` exactly for the folder named "Caixa de entrada" (Inbox), `5` for "Rascunhos" (Drafts), etc.
- **End-to-end reassembly was verified**: the message-block-chain algorithm, run against a real entry in `BCP/Rascunhos.dbx`, reassembled 1,255,918 bytes that decoded cleanly as a real RFC-822 message (`From:`/`To:`/`Subject:`/`Date:` headers, correct accented sender name).

Every numeric constant in Task 3 is one of these verified values, not a memorized guess.

## Global Constraints

- Platform: Windows 10/11 x64, .NET 9 (`net9.0`), per `ARQUITETURA.md` decision #1/#2.
- **Original `.dbx`/`.pst` files are read-only, always** (`ARQUITETURA.md` §2). Every file open in this plan uses `FileAccess.Read` and `FileShare.ReadWrite` — never a write handle.
- No UI in this phase (`ARQUITETURA.md` §12, Fase 1) — command-line only.
- A parser must never throw or crash on a malformed/corrupt file; it collects what it can and reports `StoreHealth.Partial` (`ARQUITETURA.md` §7.4). Throwing is reserved for programmer errors (wrong file type entirely, signature mismatch).
- All magic numbers live in the named, commented `DbxFormat` class — "nenhum valor hexadecimal solto no meio do código" (`ARQUITETURA.md` §7.2).
- `BCP/` (the user's real, irreplaceable legacy mail archive, several GB) is **never** committed to git.
- Real-file integration tests must degrade gracefully (skip, not fail) when `BCP/` isn't present on the machine running the tests.

---

## File Structure

```
LEC.sln
.gitignore
docs/superpowers/plans/2026-07-26-dbx-parser-fase1.md   (this file)

src/LEC.DbxFormat/LEC.DbxFormat.csproj
src/LEC.DbxFormat/IDbxByteSource.cs        Task 2  — byte-access abstraction
src/LEC.DbxFormat/ByteArrayDbxSource.cs    Task 2  — in-memory implementation (tests)
src/LEC.DbxFormat/MemoryMappedDbxSource.cs Task 2  — production implementation
src/LEC.DbxFormat/DbxFormat.cs             Task 3  — every verified format constant
src/LEC.DbxFormat/DbxSafetyLimits.cs       Task 3  — our own defensive ceilings
src/LEC.DbxFormat/StoreHealth.cs           Task 3  — Ok | Partial | Corrupt
src/LEC.DbxFormat/DbxFileType.cs           Task 3  — Unknown | Email | Folder
src/LEC.DbxFormat/DbxFormatException.cs    Task 3  — thrown only for "not a .dbx at all"
src/LEC.DbxFormat/DbxHeader.cs             Task 3  — signature detection
src/LEC.DbxFormat/DbxIndexTreeReader.cs    Task 5  — B-tree traversal, hardened
src/LEC.DbxFormat/DbxAttributeRecord.cs    Task 6  — one decoded attribute record
src/LEC.DbxFormat/DbxAttributeReader.cs    Task 6  — generic attribute-array decoding, hardened
src/LEC.DbxFormat/DbxStringDecoder.cs      Task 7  — §7.6 utf8/windows-1252 policy
src/LEC.DbxFormat/DbxEmailFieldId.cs       Task 8  — email attribute IDs (verified via undbx)
src/LEC.DbxFormat/DbxMessageInfo.cs        Task 8  — email message metadata + mapping
src/LEC.DbxFormat/DbxFolderFieldId.cs      Task 9  — folder attribute IDs (verified empirically)
src/LEC.DbxFormat/DbxSpecialFolderType.cs  Task 9  — None/Inbox/Outbox/SentItems/DeletedItems/Drafts
src/LEC.DbxFormat/DbxFolderInfo.cs         Task 9  — folder metadata + mapping
src/LEC.DbxFormat/DbxMessageBlockReader.cs Task 10 — message block-chain reassembly, hardened
src/LEC.DbxFormat/DbxEmailFile.cs          Task 11 — top-level orchestrator for message .dbx
src/LEC.DbxFormat/DbxFolderFile.cs         Task 12 — top-level orchestrator for Folders.dbx

src/LEC.Cli/LEC.Cli.csproj
src/LEC.Cli/Program.cs                     Task 13 — `dump` / `extract` commands

tests/LEC.DbxFormat.Tests/LEC.DbxFormat.Tests.csproj
tests/LEC.DbxFormat.Tests/TestDbxBuilder.cs           Task 4
tests/LEC.DbxFormat.Tests/ByteArrayDbxSourceTests.cs  Task 2
tests/LEC.DbxFormat.Tests/MemoryMappedDbxSourceTests.cs Task 2
tests/LEC.DbxFormat.Tests/DbxHeaderTests.cs           Task 3
tests/LEC.DbxFormat.Tests/TestDbxBuilderTests.cs      Task 4
tests/LEC.DbxFormat.Tests/DbxIndexTreeReaderTests.cs  Task 5
tests/LEC.DbxFormat.Tests/DbxAttributeReaderTests.cs  Task 6
tests/LEC.DbxFormat.Tests/DbxStringDecoderTests.cs    Task 7
tests/LEC.DbxFormat.Tests/DbxMessageInfoTests.cs      Task 8
tests/LEC.DbxFormat.Tests/DbxFolderInfoTests.cs       Task 9
tests/LEC.DbxFormat.Tests/DbxMessageBlockReaderTests.cs Task 10
tests/LEC.DbxFormat.Tests/DbxEmailFileTests.cs        Task 11
tests/LEC.DbxFormat.Tests/DbxFolderFileTests.cs       Task 12
```

---

### Task 1: Solution & project scaffolding

**Files:**
- Create: `LEC.sln`, `.gitignore`
- Create: `src/LEC.DbxFormat/LEC.DbxFormat.csproj`
- Create: `src/LEC.Cli/LEC.Cli.csproj`
- Create: `tests/LEC.DbxFormat.Tests/LEC.DbxFormat.Tests.csproj`

**Interfaces:**
- Produces: three cross-referenced projects (`LEC.Cli` → `LEC.DbxFormat` ← `LEC.DbxFormat.Tests`), a git repo, a `.gitignore` that excludes `BCP/`.

- [ ] **Step 1: Initialize git and scaffold the projects**

```bash
git init
dotnet new sln -n LEC -o .
dotnet new classlib -n LEC.DbxFormat -o src/LEC.DbxFormat -f net9.0
dotnet new console -n LEC.Cli -o src/LEC.Cli -f net9.0
dotnet new xunit -n LEC.DbxFormat.Tests -o tests/LEC.DbxFormat.Tests -f net9.0
dotnet sln add src/LEC.DbxFormat/LEC.DbxFormat.csproj src/LEC.Cli/LEC.Cli.csproj tests/LEC.DbxFormat.Tests/LEC.DbxFormat.Tests.csproj
dotnet add src/LEC.Cli/LEC.Cli.csproj reference src/LEC.DbxFormat/LEC.DbxFormat.csproj
dotnet add tests/LEC.DbxFormat.Tests/LEC.DbxFormat.Tests.csproj reference src/LEC.DbxFormat/LEC.DbxFormat.csproj
dotnet add src/LEC.DbxFormat/LEC.DbxFormat.csproj package System.Text.Encoding.CodePages
```

- [ ] **Step 2: Write `.gitignore`**

```gitignore
# Build output
bin/
obj/
*.user
.vs/

# The user's real, irreplaceable legacy mail archive — never committed.
BCP/
```

- [ ] **Step 3: Verify the solution builds**

Run: `dotnet build`
Expected: `Build succeeded.` for all three projects.

- [ ] **Step 4: Commit**

```bash
git add LEC.sln .gitignore src ARQUITETURA.md tests
git commit -m "Scaffold LEC solution: DbxFormat library, CLI, test project"
```

---

### Task 2: `IDbxByteSource` — byte-access abstraction

**Files:**
- Create: `src/LEC.DbxFormat/IDbxByteSource.cs`
- Create: `src/LEC.DbxFormat/ByteArrayDbxSource.cs`
- Create: `src/LEC.DbxFormat/MemoryMappedDbxSource.cs`
- Test: `tests/LEC.DbxFormat.Tests/ByteArrayDbxSourceTests.cs`
- Test: `tests/LEC.DbxFormat.Tests/MemoryMappedDbxSourceTests.cs`

**Interfaces:**
- Produces: `IDbxByteSource` with `long Length`, `byte ReadByte(int)`, `short ReadInt16(int)`, `int ReadInt32(int)`, `long ReadInt64(int)`, `void ReadBytes(int, Span<byte>)`, `byte[] ReadCStringBytes(int, int maxLength)`. Every later reader (Tasks 5, 6, 8, 9, 10, 11, 12) consumes only this interface, never a file path directly.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LEC.DbxFormat.Tests/ByteArrayDbxSourceTests.cs
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class ByteArrayDbxSourceTests
{
    [Fact]
    public void ReadsPrimitivesAtGivenOffsets()
    {
        byte[] data = new byte[32];
        BitConverter.GetBytes((short)0x1234).CopyTo(data, 0);
        BitConverter.GetBytes(0x11223344).CopyTo(data, 4);
        BitConverter.GetBytes(0x1122334455667788L).CopyTo(data, 8);
        data[20] = 0xAB;

        using var source = new ByteArrayDbxSource(data);

        Assert.Equal((short)0x1234, source.ReadInt16(0));
        Assert.Equal(0x11223344, source.ReadInt32(4));
        Assert.Equal(0x1122334455667788L, source.ReadInt64(8));
        Assert.Equal((byte)0xAB, source.ReadByte(20));
        Assert.Equal(32, source.Length);
    }

    [Fact]
    public void ReadCStringBytesStopsAtNulOrMaxLength()
    {
        byte[] data = "hello\0world"u8.ToArray();
        using var source = new ByteArrayDbxSource(data);

        Assert.Equal("hello"u8.ToArray(), source.ReadCStringBytes(0, 100));
        Assert.Equal("wor"u8.ToArray(), source.ReadCStringBytes(6, 3));
        Assert.Empty(source.ReadCStringBytes(100, 10)); // past end of buffer
    }
}
```

```csharp
// tests/LEC.DbxFormat.Tests/MemoryMappedDbxSourceTests.cs
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class MemoryMappedDbxSourceTests
{
    [Fact]
    public void ReadsMatchByteArraySourceOverSameBytes()
    {
        byte[] data = new byte[64];
        BitConverter.GetBytes(0x22334455).CopyTo(data, 10);
        string path = Path.Combine(Path.GetTempPath(), $"lec-mmtest-{Guid.NewGuid():N}.dbx");
        File.WriteAllBytes(path, data);

        try
        {
            using var source = new MemoryMappedDbxSource(path);
            Assert.Equal(64, source.Length);
            Assert.Equal(0x22334455, source.ReadInt32(10));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpensWithShareReadWriteWhileAnotherHandleHoldsTheFileOpen()
    {
        // ARQUITETURA.md §7.1: must open with FileShare.ReadWrite so a .dbx
        // that's mid-backup or open in another process can still be read.
        string path = Path.Combine(Path.GetTempPath(), $"lec-mmtest-{Guid.NewGuid():N}.dbx");
        File.WriteAllBytes(path, new byte[16]);

        try
        {
            using var externalHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var source = new MemoryMappedDbxSource(path);
            Assert.Equal(16, source.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter "ByteArrayDbxSourceTests|MemoryMappedDbxSourceTests"`
Expected: FAIL — `ByteArrayDbxSource`/`MemoryMappedDbxSource` do not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.DbxFormat/IDbxByteSource.cs
namespace LEC.DbxFormat;

public interface IDbxByteSource : IDisposable
{
    long Length { get; }
    byte ReadByte(int offset);
    short ReadInt16(int offset);
    int ReadInt32(int offset);
    long ReadInt64(int offset);
    void ReadBytes(int offset, Span<byte> destination);
    byte[] ReadCStringBytes(int offset, int maxLength);
}
```

```csharp
// src/LEC.DbxFormat/ByteArrayDbxSource.cs
namespace LEC.DbxFormat;

public sealed class ByteArrayDbxSource : IDbxByteSource
{
    private readonly byte[] _data;

    public ByteArrayDbxSource(byte[] data) => _data = data;

    public long Length => _data.Length;

    public byte ReadByte(int offset) => _data[offset];

    public short ReadInt16(int offset) => BitConverter.ToInt16(_data, offset);

    public int ReadInt32(int offset) => BitConverter.ToInt32(_data, offset);

    public long ReadInt64(int offset) => BitConverter.ToInt64(_data, offset);

    public void ReadBytes(int offset, Span<byte> destination) =>
        _data.AsSpan(offset, destination.Length).CopyTo(destination);

    public byte[] ReadCStringBytes(int offset, int maxLength)
    {
        if (offset < 0 || offset >= _data.Length)
            return Array.Empty<byte>();

        int end = offset;
        int limit = Math.Min(_data.Length, offset + maxLength);
        while (end < limit && _data[end] != 0)
            end++;

        return _data.AsSpan(offset, end - offset).ToArray();
    }

    public void Dispose() { }
}
```

```csharp
// src/LEC.DbxFormat/MemoryMappedDbxSource.cs
using System.IO.MemoryMappedFiles;

namespace LEC.DbxFormat;

public sealed class MemoryMappedDbxSource : IDbxByteSource
{
    private readonly FileStream _fileStream;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    public MemoryMappedDbxSource(string path)
    {
        _fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Length = _fileStream.Length;
        _mmf = MemoryMappedFile.CreateFromFile(
            _fileStream, mapName: null, capacity: 0,
            MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public long Length { get; }

    public byte ReadByte(int offset) => _accessor.ReadByte(offset);

    public short ReadInt16(int offset) => _accessor.ReadInt16(offset);

    public int ReadInt32(int offset) => _accessor.ReadInt32(offset);

    public long ReadInt64(int offset) => _accessor.ReadInt64(offset);

    public void ReadBytes(int offset, Span<byte> destination)
    {
        byte[] buffer = new byte[destination.Length];
        _accessor.ReadArray(offset, buffer, 0, buffer.Length);
        buffer.CopyTo(destination);
    }

    public byte[] ReadCStringBytes(int offset, int maxLength)
    {
        if (offset < 0 || offset >= Length)
            return Array.Empty<byte>();

        var bytes = new List<byte>();
        int limit = (int)Math.Min(Length, offset + (long)maxLength);
        for (int i = offset; i < limit; i++)
        {
            byte b = _accessor.ReadByte(i);
            if (b == 0)
                break;
            bytes.Add(b);
        }
        return bytes.ToArray();
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
        _fileStream.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter "ByteArrayDbxSourceTests|MemoryMappedDbxSourceTests"`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/IDbxByteSource.cs src/LEC.DbxFormat/ByteArrayDbxSource.cs src/LEC.DbxFormat/MemoryMappedDbxSource.cs tests/LEC.DbxFormat.Tests/ByteArrayDbxSourceTests.cs tests/LEC.DbxFormat.Tests/MemoryMappedDbxSourceTests.cs
git commit -m "Add IDbxByteSource abstraction: in-memory and memory-mapped implementations"
```

---

### Task 3: Format constants, safety limits, health/type enums, header detection

**Files:**
- Create: `src/LEC.DbxFormat/DbxFormat.cs`
- Create: `src/LEC.DbxFormat/DbxSafetyLimits.cs`
- Create: `src/LEC.DbxFormat/StoreHealth.cs`
- Create: `src/LEC.DbxFormat/DbxFileType.cs`
- Create: `src/LEC.DbxFormat/DbxFormatException.cs`
- Create: `src/LEC.DbxFormat/DbxHeader.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxHeaderTests.cs`

**Interfaces:**
- Consumes: `IDbxByteSource` (Task 2).
- Produces: `DbxFormat` (every verified byte offset used by Tasks 5/6/8/9/10), `DbxSafetyLimits` (`MaxIndexTreeDepth`, `MaxMessageBlockFragments`, `MaxMessageSizeBytes`), `StoreHealth { Ok, Partial, Corrupt }`, `DbxFileType { Unknown, Email, Folder }`, `DbxHeader.TryDetectType(IDbxByteSource, out DbxFileType) : bool`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LEC.DbxFormat.Tests/DbxHeaderTests.cs
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxHeaderTests
{
    private static byte[] HeaderBytes(uint word1)
    {
        var data = new byte[0xE8 + 4];
        BitConverter.GetBytes(DbxFormat.SignatureWord0).CopyTo(data, 0);
        BitConverter.GetBytes(word1).CopyTo(data, 4);
        BitConverter.GetBytes(DbxFormat.SignatureWord2).CopyTo(data, 8);
        BitConverter.GetBytes(DbxFormat.SignatureWord3).CopyTo(data, 12);
        return data;
    }

    [Fact]
    public void DetectsEmailSignature()
    {
        using var source = new ByteArrayDbxSource(HeaderBytes(DbxFormat.SignatureWord1Email));
        Assert.True(DbxHeader.TryDetectType(source, out var type));
        Assert.Equal(DbxFileType.Email, type);
    }

    [Fact]
    public void DetectsFolderSignature()
    {
        using var source = new ByteArrayDbxSource(HeaderBytes(DbxFormat.SignatureWord1Folder));
        Assert.True(DbxHeader.TryDetectType(source, out var type));
        Assert.Equal(DbxFileType.Folder, type);
    }

    [Fact]
    public void RejectsUnrecognizedSignature()
    {
        using var source = new ByteArrayDbxSource(new byte[64]); // all zeros
        Assert.False(DbxHeader.TryDetectType(source, out var type));
        Assert.Equal(DbxFileType.Unknown, type);
    }

    [Fact]
    public void RejectsTooShortFile()
    {
        using var source = new ByteArrayDbxSource(new byte[4]);
        Assert.False(DbxHeader.TryDetectType(source, out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxHeaderTests`
Expected: FAIL — `DbxFormat`, `DbxFileType`, `DbxHeader` do not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.DbxFormat/DbxFormat.cs
namespace LEC.DbxFormat;

/// <summary>
/// Every verified byte-level constant of the Outlook Express .dbx format.
/// Header + index-tree + attribute-record constants were cross-checked against
/// the reverse-engineered undbx reader (github.com/ZungBang/undbx, dbxread.c,
/// GPL-3.0 — itself based on Ulrich Krebs' DbxConv and Arne Schloh's oedbx
/// documentation) and against real files in BCP/. The folder-attribute IDs
/// (marked below) are NOT in undbx — it only detects the folder signature and
/// never parses folder content — so those were derived empirically from
/// BCP/Folders.dbx by cross-referencing decoded strings against the real
/// folder/file names on disk. See the plan header for the full verification log.
/// </summary>
public static class DbxFormat
{
    // ---- File header (offsets from start of file) ----
    public const uint SignatureWord0 = 0xFE12ADCF;
    public const uint SignatureWord1Email = 0x6F74FDC5;
    public const uint SignatureWord1Folder = 0x6F74FDC6;
    public const uint SignatureWord2 = 0x11D1E366;
    public const uint SignatureWord3 = 0xC0004E9A;
    public const int HeaderSignatureLength = 16; // 4 verified 32-bit words
    public const int HeaderItemCountOffset = 0xC4;   // int32: total indexed entries
    public const int HeaderIndexPointerOffset = 0xE4; // int32: file offset of the root index-tree node

    // ---- Index B-tree node: 24-byte header + N x 12-byte entries ----
    public const int IndexNodeHeaderSize = 24;
    public const int IndexNodeNextTableOffset = 8;   // int32: child subtree processed before this node's entries
    public const int IndexNodePtrCountOffset = 17;   // byte: number of entries following the header
    public const int IndexNodeIndexCountOffset = 20; // int32: >0 means NextTable must be recursed into
    public const int IndexNodeEntrySize = 12;
    public const int IndexEntryIndexPtrOffset = 0;    // int32: offset of this entry's info node
    public const int IndexEntryNextTableOffset = 4;   // int32: this entry's own child subtree
    public const int IndexEntryIndexCountOffset = 8;  // int32: >0 means EntryNextTable must be recursed into

    // ---- Info node: variable-length attribute-record array ----
    public const int InfoNodeCountWordOffset = 8;      // int32 at this offset; only byte at +2 (bits 16-23) matters
    public const int InfoNodeCountMask = 0x00FF0000;
    public const int InfoNodeCountShift = 16;
    public const int InfoNodeAttributeArrayOffset = 12; // start of the N x 4-byte attribute records
    public const int AttributeRecordSize = 4;
    public const byte AttributeDirectFlag = 0x80;   // set on the type byte: value is stored inline, not via pointer
    public const byte AttributeFieldIdMask = 0x7F;  // mask off AttributeDirectFlag to get the field's real ID
    public const int AttributeValueShift = 8;
    public const uint AttributeValueMask = 0xFFFFFF; // 24-bit payload

    // ---- Message data block chain ----
    public const int MessageBlockHeaderSize = 16;
    public const int MessageBlockSizeOffset = 8;  // int16: bytes of real data in this block (1..MessageBlockMaxDataSize)
    public const int MessageBlockNextOffset = 12; // int32: file offset of the next block, or 0 if last
    public const int MessageBlockDataOffset = 16;
    public const int MessageBlockMaxDataSize = 0x200; // 512 bytes
}
```

```csharp
// src/LEC.DbxFormat/DbxSafetyLimits.cs
namespace LEC.DbxFormat;

/// <summary>
/// Defensive ceilings against a corrupted file, per ARQUITETURA.md §7.3.
/// These are our own choices, not part of the reverse-engineered format —
/// chosen generously so real (even large, decades-old) mailboxes parse fully.
/// </summary>
public static class DbxSafetyLimits
{
    public const int MaxIndexTreeDepth = 64;
    public const int MaxMessageBlockFragments = 200_000; // ~100 MB at 512 B/fragment
    public const long MaxMessageSizeBytes = 200L * 1024 * 1024; // 200 MB per reassembled message
}
```

```csharp
// src/LEC.DbxFormat/StoreHealth.cs
namespace LEC.DbxFormat;

public enum StoreHealth
{
    Ok,
    Partial,
    Corrupt,
}
```

```csharp
// src/LEC.DbxFormat/DbxFileType.cs
namespace LEC.DbxFormat;

public enum DbxFileType
{
    Unknown,
    Email,
    Folder,
}
```

```csharp
// src/LEC.DbxFormat/DbxFormatException.cs
namespace LEC.DbxFormat;

/// <summary>
/// Thrown only when a file isn't a .dbx at all (signature mismatch) — a
/// programmer/caller error, distinct from in-content corruption which is
/// reported via StoreHealth instead of an exception (ARQUITETURA.md §7.4).
/// </summary>
public sealed class DbxFormatException : Exception
{
    public DbxFormatException(string message) : base(message) { }
}
```

```csharp
// src/LEC.DbxFormat/DbxHeader.cs
namespace LEC.DbxFormat;

public static class DbxHeader
{
    public static bool TryDetectType(IDbxByteSource source, out DbxFileType type)
    {
        type = DbxFileType.Unknown;
        if (source.Length < DbxFormat.HeaderSignatureLength)
            return false;

        uint word0 = unchecked((uint)source.ReadInt32(0));
        uint word1 = unchecked((uint)source.ReadInt32(4));
        uint word2 = unchecked((uint)source.ReadInt32(8));
        uint word3 = unchecked((uint)source.ReadInt32(12));

        if (word0 != DbxFormat.SignatureWord0 || word2 != DbxFormat.SignatureWord2 || word3 != DbxFormat.SignatureWord3)
            return false;

        if (word1 == DbxFormat.SignatureWord1Email)
        {
            type = DbxFileType.Email;
            return true;
        }

        if (word1 == DbxFormat.SignatureWord1Folder)
        {
            type = DbxFileType.Folder;
            return true;
        }

        return false;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxHeaderTests`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxFormat.cs src/LEC.DbxFormat/DbxSafetyLimits.cs src/LEC.DbxFormat/StoreHealth.cs src/LEC.DbxFormat/DbxFileType.cs src/LEC.DbxFormat/DbxFormatException.cs src/LEC.DbxFormat/DbxHeader.cs tests/LEC.DbxFormat.Tests/DbxHeaderTests.cs
git commit -m "Add verified DbxFormat constants, safety limits, and signature detection"
```

---

### Task 4: `TestDbxBuilder` — synthetic `.dbx` byte-buffer builder for tests

**Files:**
- Create: `tests/LEC.DbxFormat.Tests/TestDbxBuilder.cs`
- Test: `tests/LEC.DbxFormat.Tests/TestDbxBuilderTests.cs`

**Interfaces:**
- Consumes: `DbxFormat` (Task 3).
- Produces: `TestDbxBuilder` with `WriteHeader(DbxFileType)`, `SetInt32At(int offset, int value)`, `WriteIndexNode(int nextTable, byte ptrCount, int indexCount, IReadOnlyList<(int indexPtr, int entryNextTable, int entryIndexCount)> entries) : int`, `WriteInfoNode(IReadOnlyList<TestDbxBuilder.AttributeSpec> attrs) : int`, `WriteMessageBlock(byte[] data, int nextOffset) : int`, `ToArray() : byte[]`. Reused by Tasks 5, 6, 8, 9, 10, 11, 12.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LEC.DbxFormat.Tests/TestDbxBuilderTests.cs
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class TestDbxBuilderTests
{
    [Fact]
    public void WriteHeaderProducesADetectableEmailSignature()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        using var source = new ByteArrayDbxSource(builder.ToArray());
        Assert.True(DbxHeader.TryDetectType(source, out var type));
        Assert.Equal(DbxFileType.Email, type);
    }

    [Fact]
    public void WriteHeaderProducesADetectableFolderSignature()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Folder);

        using var source = new ByteArrayDbxSource(builder.ToArray());
        Assert.True(DbxHeader.TryDetectType(source, out var type));
        Assert.Equal(DbxFileType.Folder, type);
    }

    [Fact]
    public void SetInt32AtOverwritesBytesInPlace()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 7);

        using var source = new ByteArrayDbxSource(builder.ToArray());
        Assert.Equal(7, source.ReadInt32(DbxFormat.HeaderItemCountOffset));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter TestDbxBuilderTests`
Expected: FAIL — `TestDbxBuilder` does not exist yet.

- [ ] **Step 3: Implement**

```csharp
// tests/LEC.DbxFormat.Tests/TestDbxBuilder.cs
using LEC.DbxFormat;

namespace LEC.DbxFormat.Tests;

/// <summary>
/// Assembles a synthetic, byte-exact .dbx buffer for tests, following the
/// verified layout in DbxFormat exactly. Not shipped code — test infrastructure.
/// </summary>
public sealed class TestDbxBuilder
{
    public sealed class AttributeSpec
    {
        public required byte FieldId { get; init; }
        public bool Direct { get; init; }
        public uint DirectValue { get; init; }
        public byte[]? Data { get; init; } // used when Direct == false
    }

    private readonly List<byte> _bytes = new();

    public int Position => _bytes.Count;

    public TestDbxBuilder WriteHeader(DbxFileType type)
    {
        uint word1 = type switch
        {
            DbxFileType.Email => DbxFormat.SignatureWord1Email,
            DbxFileType.Folder => DbxFormat.SignatureWord1Folder,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        WriteUInt32(DbxFormat.SignatureWord0);
        WriteUInt32(word1);
        WriteUInt32(DbxFormat.SignatureWord2);
        WriteUInt32(DbxFormat.SignatureWord3);

        while (_bytes.Count < DbxFormat.HeaderIndexPointerOffset + 4)
            _bytes.Add(0);

        return this;
    }

    public TestDbxBuilder SetInt32At(int offset, int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        for (int i = 0; i < 4; i++)
            _bytes[offset + i] = bytes[i];
        return this;
    }

    public int WriteIndexNode(int nextTable, byte ptrCount, int indexCount,
        IReadOnlyList<(int indexPtr, int entryNextTable, int entryIndexCount)> entries)
    {
        int start = _bytes.Count;
        WriteBytes(new byte[8]);       // 0..7: unused by the reader
        WriteInt32(nextTable);         // 8..11
        WriteBytes(new byte[5]);       // 12..16: unused
        _bytes.Add(ptrCount);          // 17
        WriteBytes(new byte[2]);       // 18..19: unused
        WriteInt32(indexCount);        // 20..23

        foreach (var entry in entries)
        {
            WriteInt32(entry.indexPtr);
            WriteInt32(entry.entryNextTable);
            WriteInt32(entry.entryIndexCount);
        }

        return start;
    }

    public int WriteInfoNode(IReadOnlyList<AttributeSpec> attrs)
    {
        int start = _bytes.Count;
        WriteBytes(new byte[8]);            // 0..7: unused ("size" etc., not read by our readers)
        _bytes.Add(0);                      // 8
        _bytes.Add(0);                      // 9
        _bytes.Add((byte)attrs.Count);      // 10: attribute count
        _bytes.Add(0);                      // 11

        int arrayStart = _bytes.Count;
        for (int i = 0; i < attrs.Count; i++)
            WriteBytes(new byte[4]); // reserve; back-patched below

        int dataAreaStart = _bytes.Count;
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
                int relativeOffset = _bytes.Count - dataAreaStart;
                recordValue = (uint)relativeOffset & DbxFormat.AttributeValueMask;
                WriteBytes(attr.Data ?? Array.Empty<byte>());
            }

            uint record = typeByte | (recordValue << DbxFormat.AttributeValueShift);
            byte[] recordBytes = BitConverter.GetBytes(record);
            for (int b = 0; b < 4; b++)
                _bytes[arrayStart + i * 4 + b] = recordBytes[b];
        }

        return start;
    }

    public int WriteMessageBlock(byte[] data, int nextOffset)
    {
        if (data.Length == 0 || data.Length > DbxFormat.MessageBlockMaxDataSize)
            throw new ArgumentException($"Block data must be 1..{DbxFormat.MessageBlockMaxDataSize} bytes.");

        int start = _bytes.Count;
        WriteBytes(new byte[8]);              // 0..7: unused by the reader
        WriteInt16((short)data.Length);       // 8..9
        WriteBytes(new byte[2]);              // 10..11: unused
        WriteInt32(nextOffset);               // 12..15
        WriteBytes(data);                     // 16..

        return start;
    }

    public byte[] ToArray() => _bytes.ToArray();

    private void WriteBytes(byte[] bytes) => _bytes.AddRange(bytes);

    private void WriteInt16(short value) => _bytes.AddRange(BitConverter.GetBytes(value));

    private void WriteInt32(int value) => _bytes.AddRange(BitConverter.GetBytes(value));

    private void WriteUInt32(uint value) => _bytes.AddRange(BitConverter.GetBytes(value));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter TestDbxBuilderTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add tests/LEC.DbxFormat.Tests/TestDbxBuilder.cs tests/LEC.DbxFormat.Tests/TestDbxBuilderTests.cs
git commit -m "Add TestDbxBuilder: synthetic .dbx buffer builder for tests"
```

---

### Task 5: `DbxIndexTreeReader` — B-tree traversal, hardened

**Files:**
- Create: `src/LEC.DbxFormat/DbxIndexTreeReader.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxIndexTreeReaderTests.cs`

**Interfaces:**
- Consumes: `IDbxByteSource` (Task 2), `DbxFormat`/`DbxSafetyLimits` (Task 3), `TestDbxBuilder` (Task 4, tests only).
- Produces: `DbxIndexTreeReader(IDbxByteSource)` with `IReadOnlyList<int> ReadIndexPointers(int rootOffset, out bool corruptionDetected)`. Consumed by Tasks 11 and 12.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LEC.DbxFormat.Tests/DbxIndexTreeReaderTests.cs
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxIndexTreeReaderTests
{
    [Fact]
    public void ReturnsAllEntryPointersFromALeafNode()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        int rootOffset = builder.WriteIndexNode(
            nextTable: 0, ptrCount: 2, indexCount: 0,
            entries: new[] { (indexPtr: 0x2000, entryNextTable: 0, entryIndexCount: 0),
                              (indexPtr: 0x3000, entryNextTable: 0, entryIndexCount: 0) });

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var reader = new DbxIndexTreeReader(source);

        var pointers = reader.ReadIndexPointers(rootOffset, out bool corrupt);

        Assert.False(corrupt);
        Assert.Equal(new[] { 0x2000, 0x3000 }, pointers);
    }

    [Fact]
    public void DoesNotHangOnASelfReferencingCycleAndReportsCorruption()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        int nodeOffset = builder.Position;
        // An entry whose own subtree (entryNextTable) points back at this same node,
        // with entryIndexCount > 0 so the reader is forced to recurse into it.
        builder.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: 0x2000, entryNextTable: nodeOffset, entryIndexCount: 1) });

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var reader = new DbxIndexTreeReader(source);

        var pointers = reader.ReadIndexPointers(nodeOffset, out bool corrupt);

        Assert.True(corrupt);
        Assert.Equal(new[] { 0x2000 }, pointers);
    }

    [Fact]
    public void ReportsCorruptionForAnOutOfRangeRootOffset()
    {
        using var source = new ByteArrayDbxSource(new byte[64]);
        var reader = new DbxIndexTreeReader(source);

        var pointers = reader.ReadIndexPointers(rootOffset: 10_000, out bool corrupt);

        Assert.True(corrupt);
        Assert.Empty(pointers);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxIndexTreeReaderTests`
Expected: FAIL — `DbxIndexTreeReader` does not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.DbxFormat/DbxIndexTreeReader.cs
namespace LEC.DbxFormat;

public sealed class DbxIndexTreeReader
{
    private readonly IDbxByteSource _source;

    public DbxIndexTreeReader(IDbxByteSource source) => _source = source;

    public IReadOnlyList<int> ReadIndexPointers(int rootOffset, out bool corruptionDetected)
    {
        var result = new List<int>();
        var visited = new HashSet<int>();
        corruptionDetected = !ReadNode(rootOffset, depth: 0, result, visited);
        return result;
    }

    private bool ReadNode(int nodeOffset, int depth, List<int> result, HashSet<int> visited)
    {
        if (depth > DbxSafetyLimits.MaxIndexTreeDepth)
            return false;

        if (nodeOffset <= 0 || (long)nodeOffset + DbxFormat.IndexNodeHeaderSize > _source.Length)
            return false;

        if (!visited.Add(nodeOffset))
            return false; // cycle

        int nextTable = _source.ReadInt32(nodeOffset + DbxFormat.IndexNodeNextTableOffset);
        byte ptrCount = _source.ReadByte(nodeOffset + DbxFormat.IndexNodePtrCountOffset);
        int indexCount = _source.ReadInt32(nodeOffset + DbxFormat.IndexNodeIndexCountOffset);

        bool ok = true;

        if (indexCount > 0)
            ok &= ReadNode(nextTable, depth + 1, result, visited);

        int entryPos = nodeOffset + DbxFormat.IndexNodeHeaderSize;
        for (int i = 0; i < ptrCount; i++)
        {
            if ((long)entryPos + DbxFormat.IndexNodeEntrySize > _source.Length)
            {
                ok = false;
                break;
            }

            int indexPtr = _source.ReadInt32(entryPos + DbxFormat.IndexEntryIndexPtrOffset);
            int entryNextTable = _source.ReadInt32(entryPos + DbxFormat.IndexEntryNextTableOffset);
            int entryIndexCount = _source.ReadInt32(entryPos + DbxFormat.IndexEntryIndexCountOffset);

            result.Add(indexPtr);
            entryPos += DbxFormat.IndexNodeEntrySize;

            if (entryIndexCount > 0)
                ok &= ReadNode(entryNextTable, depth + 1, result, visited);
        }

        return ok;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxIndexTreeReaderTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxIndexTreeReader.cs tests/LEC.DbxFormat.Tests/DbxIndexTreeReaderTests.cs
git commit -m "Add DbxIndexTreeReader: hardened B-tree traversal with cycle/depth/bounds guards"
```

---

### Task 6: `DbxAttributeReader` — generic attribute-record decoding, hardened

**Files:**
- Create: `src/LEC.DbxFormat/DbxAttributeRecord.cs`
- Create: `src/LEC.DbxFormat/DbxAttributeReader.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxAttributeReaderTests.cs`

**Interfaces:**
- Consumes: `IDbxByteSource`, `DbxFormat`, `TestDbxBuilder`.
- Produces: `DbxAttributeRecord { byte RawType, uint Value, int? DataOffset, byte FieldId, bool IsDirect }`, `DbxAttributeReader.Read(IDbxByteSource, int infoNodeOffset) : IReadOnlyList<DbxAttributeRecord>`. Consumed by Tasks 8 and 9.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LEC.DbxFormat.Tests/DbxAttributeReaderTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxAttributeReaderTests
{
    [Fact]
    public void DecodesDirectAndPointerFormAttributes()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        int infoNodeOffset = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = 0x11, Direct = true, DirectValue = 42 },
            new TestDbxBuilder.AttributeSpec { FieldId = 0x08, Direct = false, Data = Encoding.ASCII.GetBytes("Hello\0") },
        });

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var records = DbxAttributeReader.Read(source, infoNodeOffset);

        Assert.Equal(2, records.Count);

        Assert.Equal(0x11, records[0].FieldId);
        Assert.True(records[0].IsDirect);
        Assert.Equal(42u, records[0].Value);
        Assert.Null(records[0].DataOffset);

        Assert.Equal(0x08, records[1].FieldId);
        Assert.False(records[1].IsDirect);
        Assert.NotNull(records[1].DataOffset);
        Assert.Equal("Hello", Encoding.ASCII.GetString(source.ReadCStringBytes(records[1].DataOffset!.Value, 100)));
    }

    [Fact]
    public void ReturnsEmptyForAnInfoNodeOffsetPastEndOfFile()
    {
        using var source = new ByteArrayDbxSource(new byte[8]);
        var records = DbxAttributeReader.Read(source, infoNodeOffset: 100);
        Assert.Empty(records);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxAttributeReaderTests`
Expected: FAIL — `DbxAttributeRecord`/`DbxAttributeReader` do not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.DbxFormat/DbxAttributeRecord.cs
namespace LEC.DbxFormat;

public readonly record struct DbxAttributeRecord(byte RawType, uint Value, int? DataOffset)
{
    public byte FieldId => (byte)(RawType & DbxFormat.AttributeFieldIdMask);

    public bool IsDirect => (RawType & DbxFormat.AttributeDirectFlag) != 0;
}
```

```csharp
// src/LEC.DbxFormat/DbxAttributeReader.cs
namespace LEC.DbxFormat;

public static class DbxAttributeReader
{
    public static IReadOnlyList<DbxAttributeRecord> Read(IDbxByteSource source, int infoNodeOffset)
    {
        if (infoNodeOffset < 0 || (long)infoNodeOffset + DbxFormat.InfoNodeAttributeArrayOffset > source.Length)
            return Array.Empty<DbxAttributeRecord>();

        int countWord = source.ReadInt32(infoNodeOffset + DbxFormat.InfoNodeCountWordOffset);
        int count = (countWord & DbxFormat.InfoNodeCountMask) >> DbxFormat.InfoNodeCountShift;

        int arrayStart = infoNodeOffset + DbxFormat.InfoNodeAttributeArrayOffset;
        long dataAreaStart = arrayStart + (long)DbxFormat.AttributeRecordSize * count;

        if (dataAreaStart > source.Length)
            return Array.Empty<DbxAttributeRecord>(); // corrupt count would overread

        var records = new List<DbxAttributeRecord>(count);
        for (int i = 0; i < count; i++)
        {
            uint raw = unchecked((uint)source.ReadInt32(arrayStart + i * DbxFormat.AttributeRecordSize));
            byte rawType = (byte)(raw & 0xFF);
            uint value = (raw >> DbxFormat.AttributeValueShift) & DbxFormat.AttributeValueMask;

            int? dataOffset = (rawType & DbxFormat.AttributeDirectFlag) != 0
                ? null
                : (int)(dataAreaStart + value);

            records.Add(new DbxAttributeRecord(rawType, value, dataOffset));
        }

        return records;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxAttributeReaderTests`
Expected: PASS (2 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxAttributeRecord.cs src/LEC.DbxFormat/DbxAttributeReader.cs tests/LEC.DbxFormat.Tests/DbxAttributeReaderTests.cs
git commit -m "Add DbxAttributeReader: generic, hardened attribute-record decoding"
```

---

### Task 7: `DbxStringDecoder` — §7.6 charset policy

**Files:**
- Create: `src/LEC.DbxFormat/DbxStringDecoder.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxStringDecoderTests.cs`

**Interfaces:**
- Produces: `DbxStringDecoder.Decode(byte[] raw) : string`. Consumed by Tasks 8 and 9.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LEC.DbxFormat.Tests/DbxStringDecoderTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxStringDecoderTests
{
    [Fact]
    public void DecodesValidUtf8Directly()
    {
        byte[] raw = Encoding.UTF8.GetBytes("Café");
        Assert.Equal("Café", DbxStringDecoder.Decode(raw));
    }

    [Fact]
    public void FallsBackToWindows1252WhenNotValidUtf8()
    {
        // 0xE9 alone is not valid UTF-8, but is 'é' in windows-1252 —
        // the common case for old Brazilian OE index strings (ARQUITETURA.md §7.6).
        byte[] raw = { (byte)'C', (byte)'a', 0xE9 };
        Assert.Equal("Caé", DbxStringDecoder.Decode(raw));
    }

    [Fact]
    public void EmptyInputDecodesToEmptyString()
    {
        Assert.Equal(string.Empty, DbxStringDecoder.Decode(Array.Empty<byte>()));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxStringDecoderTests`
Expected: FAIL — `DbxStringDecoder` does not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.DbxFormat/DbxStringDecoder.cs
using System.Text;

namespace LEC.DbxFormat;

/// <summary>
/// ARQUITETURA.md §7.6: index-node strings carry no charset. Policy: try
/// UTF-8, fall back to windows-1252 (the typical Brazilian OE-era codepage).
/// </summary>
public static class DbxStringDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static DbxStringDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Decode(byte[] raw)
    {
        if (raw.Length == 0)
            return string.Empty;

        try
        {
            return StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(raw);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxStringDecoderTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxStringDecoder.cs tests/LEC.DbxFormat.Tests/DbxStringDecoderTests.cs
git commit -m "Add DbxStringDecoder: UTF-8 with windows-1252 fallback per ARQUITETURA.md 7.6"
```

---

### Task 8: Email attribute mapping — `DbxEmailFieldId` + `DbxMessageInfo`

**Files:**
- Create: `src/LEC.DbxFormat/DbxEmailFieldId.cs`
- Create: `src/LEC.DbxFormat/DbxMessageInfo.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxMessageInfoTests.cs`

**Interfaces:**
- Consumes: `DbxAttributeReader`/`DbxAttributeRecord` (Task 6), `DbxStringDecoder` (Task 7), `IDbxByteSource`, `TestDbxBuilder`.
- Produces: `DbxEmailFieldId` enum (verified against undbx's `_dbx_read_info` switch), `DbxMessageInfo { int IndexNodeOffset, int MessageBlockOffset, string? Subject, string? SenderName, string? SenderAddress, string? ReceiverName, string? ReceiverAddress, string? MessageId, DateTime? SendTimeUtc, DateTime? ReceiveTimeUtc, uint MessageSize, int Flags }` with `static DbxMessageInfo Read(IDbxByteSource, int indexNodeOffset)`. Consumed by Task 11.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LEC.DbxFormat.Tests/DbxMessageInfoTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxMessageInfoTests
{
    [Fact]
    public void ReadsEmailFieldsFromAnInfoNode()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        long sendFileTime = new DateTime(2005, 10, 21, 11, 11, 49, DateTimeKind.Utc).ToFileTimeUtc();

        int infoNodeOffset = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = 0x9000 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = Encoding.ASCII.GetBytes("Re: 13E03-B\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.SenderName, Data = Encoding.ASCII.GetBytes("BCP ENGENHARIA LTDA\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.SenderAddress, Data = Encoding.ASCII.GetBytes("bcp@bcpengenharia.com\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.SendCreateTime, Data = BitConverter.GetBytes(sendFileTime) },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageSize, Direct = true, DirectValue = 1_255_918 },
        });

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var info = DbxMessageInfo.Read(source, infoNodeOffset);

        Assert.Equal(0x9000, info.MessageBlockOffset);
        Assert.Equal("Re: 13E03-B", info.Subject);
        Assert.Equal("BCP ENGENHARIA LTDA", info.SenderName);
        Assert.Equal("bcp@bcpengenharia.com", info.SenderAddress);
        Assert.Equal(new DateTime(2005, 10, 21, 11, 11, 49, DateTimeKind.Utc), info.SendTimeUtc);
        Assert.Equal(1_255_918u, info.MessageSize);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxMessageInfoTests`
Expected: FAIL — `DbxEmailFieldId`/`DbxMessageInfo` do not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.DbxFormat/DbxEmailFieldId.cs
namespace LEC.DbxFormat;

/// <summary>
/// Email info-node attribute IDs, verified against undbx's _dbx_read_info
/// switch (dbxread.c). Gaps (0x0F, 0x15-0x19, 0x1C+) are attributes undbx
/// itself does not decode — left unhandled here too, not guessed.
/// </summary>
public enum DbxEmailFieldId : byte
{
    MessageIndex = 0x00,
    Flags = 0x01,
    SendCreateTime = 0x02,
    BodyLines = 0x03,
    MessageBlockOffset = 0x04,
    OriginalSubject = 0x05,
    SaveTime = 0x06,
    MessageId = 0x07,
    Subject = 0x08,
    SenderAddressAndName = 0x09,
    InReplyToMessageId = 0x0A,
    ServerNewsgroupMessageNumber = 0x0B,
    Server = 0x0C,
    SenderName = 0x0D,
    SenderAddress = 0x0E,
    MessagePriority = 0x10,
    MessageSize = 0x11,
    ReceiveCreateTime = 0x12,
    ReceiverName = 0x13,
    ReceiverAddress = 0x14,
    AccountName = 0x1A,
    AccountRegistryKey = 0x1B,
}
```

```csharp
// src/LEC.DbxFormat/DbxMessageInfo.cs
namespace LEC.DbxFormat;

public sealed class DbxMessageInfo
{
    public required int IndexNodeOffset { get; init; }
    public int MessageBlockOffset { get; init; }
    public string? Subject { get; init; }
    public string? SenderName { get; init; }
    public string? SenderAddress { get; init; }
    public string? ReceiverName { get; init; }
    public string? ReceiverAddress { get; init; }
    public string? MessageId { get; init; }
    public DateTime? SendTimeUtc { get; init; }
    public DateTime? ReceiveTimeUtc { get; init; }
    public uint MessageSize { get; init; }
    public int Flags { get; init; }

    public static DbxMessageInfo Read(IDbxByteSource source, int indexNodeOffset)
    {
        var attrs = DbxAttributeReader.Read(source, indexNodeOffset);

        int messageBlockOffset = 0;
        string? subject = null, senderName = null, senderAddress = null;
        string? receiverName = null, receiverAddress = null, messageId = null;
        DateTime? sendTime = null, receiveTime = null;
        uint messageSize = 0;
        int flags = 0;

        foreach (var a in attrs)
        {
            switch ((DbxEmailFieldId)a.FieldId)
            {
                case DbxEmailFieldId.MessageBlockOffset:
                    messageBlockOffset = ReadInt(source, a);
                    break;
                case DbxEmailFieldId.Subject:
                    subject = ReadString(source, a);
                    break;
                case DbxEmailFieldId.SenderName:
                    senderName = ReadString(source, a);
                    break;
                case DbxEmailFieldId.SenderAddress:
                    senderAddress = ReadString(source, a);
                    break;
                case DbxEmailFieldId.ReceiverName:
                    receiverName = ReadString(source, a);
                    break;
                case DbxEmailFieldId.ReceiverAddress:
                    receiverAddress = ReadString(source, a);
                    break;
                case DbxEmailFieldId.MessageId:
                    messageId = ReadString(source, a);
                    break;
                case DbxEmailFieldId.SendCreateTime:
                    sendTime = ReadFileTime(source, a);
                    break;
                case DbxEmailFieldId.ReceiveCreateTime:
                    receiveTime = ReadFileTime(source, a);
                    break;
                case DbxEmailFieldId.MessageSize:
                    messageSize = (uint)ReadInt(source, a);
                    break;
                case DbxEmailFieldId.Flags:
                    flags = ReadInt(source, a);
                    break;
            }
        }

        return new DbxMessageInfo
        {
            IndexNodeOffset = indexNodeOffset,
            MessageBlockOffset = messageBlockOffset,
            Subject = subject,
            SenderName = senderName,
            SenderAddress = senderAddress,
            ReceiverName = receiverName,
            ReceiverAddress = receiverAddress,
            MessageId = messageId,
            SendTimeUtc = sendTime,
            ReceiveTimeUtc = receiveTime,
            MessageSize = messageSize,
            Flags = flags,
        };
    }

    private static int ReadInt(IDbxByteSource source, DbxAttributeRecord a) =>
        a.IsDirect ? (int)a.Value : source.ReadInt32(a.DataOffset!.Value);

    private static string? ReadString(IDbxByteSource source, DbxAttributeRecord a) =>
        a.DataOffset is int off ? DbxStringDecoder.Decode(source.ReadCStringBytes(off, 4096)) : null;

    private static DateTime? ReadFileTime(IDbxByteSource source, DbxAttributeRecord a)
    {
        if (a.DataOffset is not int off)
            return null;

        long fileTime = source.ReadInt64(off);
        if (fileTime <= 0)
            return null;

        try
        {
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // corrupted timestamp — don't crash, just omit it
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxMessageInfoTests`
Expected: PASS (1 test)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxEmailFieldId.cs src/LEC.DbxFormat/DbxMessageInfo.cs tests/LEC.DbxFormat.Tests/DbxMessageInfoTests.cs
git commit -m "Add DbxEmailFieldId and DbxMessageInfo: verified email attribute mapping"
```

---

### Task 9: Folder attribute mapping — `DbxFolderFieldId` + `DbxSpecialFolderType` + `DbxFolderInfo`

**Files:**
- Create: `src/LEC.DbxFormat/DbxFolderFieldId.cs`
- Create: `src/LEC.DbxFormat/DbxSpecialFolderType.cs`
- Create: `src/LEC.DbxFormat/DbxFolderInfo.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxFolderInfoTests.cs`

**Interfaces:**
- Consumes: `DbxAttributeReader`/`DbxAttributeRecord` (Task 6), `DbxStringDecoder` (Task 7).
- Produces: `DbxFolderFieldId`, `DbxSpecialFolderType { None, Inbox, Outbox, SentItems, DeletedItems, Drafts }`, `DbxFolderInfo { int IndexNodeOffset, int FolderId, int ParentId, string? Name, string? FileName, int Flags, int MessageCount, DbxSpecialFolderType SpecialFolderType }` with `static DbxFolderInfo Read(IDbxByteSource, int indexNodeOffset)`. Consumed by Task 12.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LEC.DbxFormat.Tests/DbxFolderInfoTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxFolderInfoTests
{
    [Fact]
    public void ReadsFolderFieldsFromAnInfoNode()
    {
        // Mirrors the real BCP/Folders.dbx entry for "Caixa de entrada" (Inbox):
        // folder id=96, parent=1, backed by "Caixa de entrada (1).dbx", 2235 messages.
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Folder);

        int infoNodeOffset = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 96 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = Encoding.UTF8.GetBytes("Caixa de entrada\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = Encoding.UTF8.GetBytes("Caixa de entrada (1).dbx\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.MessageCount, Direct = true, DirectValue = 2235 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var info = DbxFolderInfo.Read(source, infoNodeOffset);

        Assert.Equal(96, info.FolderId);
        Assert.Equal(1, info.ParentId);
        Assert.Equal("Caixa de entrada", info.Name);
        Assert.Equal("Caixa de entrada (1).dbx", info.FileName);
        Assert.Equal(2235, info.MessageCount);
        Assert.Equal(DbxSpecialFolderType.Inbox, info.SpecialFolderType);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxFolderInfoTests`
Expected: FAIL — `DbxFolderFieldId`/`DbxSpecialFolderType`/`DbxFolderInfo` do not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.DbxFormat/DbxFolderFieldId.cs
namespace LEC.DbxFormat;

/// <summary>
/// Folders.dbx info-node attribute IDs. NOT covered by undbx (it only
/// detects the folder signature and never parses folder content) — derived
/// empirically against the real BCP/Folders.dbx (26 folders), by matching
/// decoded strings against the actual folder/file names on disk. See the
/// plan header's verification log for the full cross-reference.
/// </summary>
public enum DbxFolderFieldId : byte
{
    FolderId = 0x00,
    ParentId = 0x01,
    Name = 0x02,
    FileName = 0x03,
    Flags = 0x06,
    MessageCount = 0x07,
    UnreadCount = 0x08, // observed once; plausible, not independently confirmed
    SpecialFolderType = 0x09,
    NodeKind = 0x0A, // observed values 3 (regular) / 4 (root); meaning not fully confirmed
}
```

```csharp
// src/LEC.DbxFormat/DbxSpecialFolderType.cs
namespace LEC.DbxFormat;

/// <summary>
/// Verified against BCP/Folders.dbx: field 0x09 is exactly 1 for the folder
/// named "Caixa de entrada" (Inbox), 2 for "Caixa de saída" (Outbox),
/// 3 for "Itens enviados" (Sent Items), 4 for "Itens excluídos" (Deleted
/// Items), 5 for "Rascunhos" (Drafts).
/// </summary>
public enum DbxSpecialFolderType
{
    None = 0,
    Inbox = 1,
    Outbox = 2,
    SentItems = 3,
    DeletedItems = 4,
    Drafts = 5,
}
```

```csharp
// src/LEC.DbxFormat/DbxFolderInfo.cs
namespace LEC.DbxFormat;

public sealed class DbxFolderInfo
{
    public required int IndexNodeOffset { get; init; }
    public int FolderId { get; init; }
    public int ParentId { get; init; }
    public string? Name { get; init; }
    public string? FileName { get; init; }
    public int Flags { get; init; }
    public int MessageCount { get; init; }
    public DbxSpecialFolderType SpecialFolderType { get; init; }

    public static DbxFolderInfo Read(IDbxByteSource source, int indexNodeOffset)
    {
        var attrs = DbxAttributeReader.Read(source, indexNodeOffset);

        int folderId = 0, parentId = 0, flags = 0, messageCount = 0;
        string? name = null, fileName = null;
        var specialType = DbxSpecialFolderType.None;

        foreach (var a in attrs)
        {
            switch ((DbxFolderFieldId)a.FieldId)
            {
                case DbxFolderFieldId.FolderId:
                    folderId = ReadInt(source, a);
                    break;
                case DbxFolderFieldId.ParentId:
                    parentId = ReadInt(source, a);
                    break;
                case DbxFolderFieldId.Name:
                    name = ReadString(source, a);
                    break;
                case DbxFolderFieldId.FileName:
                    fileName = ReadString(source, a);
                    break;
                case DbxFolderFieldId.Flags:
                    flags = ReadInt(source, a);
                    break;
                case DbxFolderFieldId.MessageCount:
                    messageCount = ReadInt(source, a);
                    break;
                case DbxFolderFieldId.SpecialFolderType:
                    int value = ReadInt(source, a);
                    specialType = Enum.IsDefined(typeof(DbxSpecialFolderType), value)
                        ? (DbxSpecialFolderType)value
                        : DbxSpecialFolderType.None;
                    break;
            }
        }

        return new DbxFolderInfo
        {
            IndexNodeOffset = indexNodeOffset,
            FolderId = folderId,
            ParentId = parentId,
            Name = name,
            FileName = fileName,
            Flags = flags,
            MessageCount = messageCount,
            SpecialFolderType = specialType,
        };
    }

    private static int ReadInt(IDbxByteSource source, DbxAttributeRecord a) =>
        a.IsDirect ? (int)a.Value : source.ReadInt32(a.DataOffset!.Value);

    private static string? ReadString(IDbxByteSource source, DbxAttributeRecord a) =>
        a.DataOffset is int off ? DbxStringDecoder.Decode(source.ReadCStringBytes(off, 512)) : null;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxFolderInfoTests`
Expected: PASS (1 test)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxFolderFieldId.cs src/LEC.DbxFormat/DbxSpecialFolderType.cs src/LEC.DbxFormat/DbxFolderInfo.cs tests/LEC.DbxFormat.Tests/DbxFolderInfoTests.cs
git commit -m "Add DbxFolderFieldId and DbxFolderInfo: empirically-verified folder attribute mapping"
```

---

### Task 10: `DbxMessageBlockReader` — block-chain reassembly, hardened

**Files:**
- Create: `src/LEC.DbxFormat/DbxMessageBlockReader.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxMessageBlockReaderTests.cs`

**Interfaces:**
- Consumes: `IDbxByteSource`, `DbxFormat`, `DbxSafetyLimits`, `TestDbxBuilder`.
- Produces: `DbxMessageBlockReader.Result { byte[] Data, bool Truncated }`, `DbxMessageBlockReader.ReadChain(IDbxByteSource, int startOffset) : Result`. Consumed by Task 11.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LEC.DbxFormat.Tests/DbxMessageBlockReaderTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxMessageBlockReaderTests
{
    [Fact]
    public void ReassemblesAChainOfTwoBlocksInOrder()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        // Second block is written first so we know its offset up front.
        byte[] secondPart = Encoding.ASCII.GetBytes("World");
        int secondOffset = builder.WriteMessageBlock(secondPart, nextOffset: 0);

        byte[] firstPart = Encoding.ASCII.GetBytes("Hello");
        int firstOffset = builder.WriteMessageBlock(firstPart, nextOffset: secondOffset);

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var result = DbxMessageBlockReader.ReadChain(source, firstOffset);

        Assert.False(result.Truncated);
        Assert.Equal("HelloWorld", Encoding.ASCII.GetString(result.Data));
    }

    [Fact]
    public void DoesNotHangOnASelfReferencingBlockAndReportsTruncation()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        int offset = builder.Position;
        builder.WriteMessageBlock(Encoding.ASCII.GetBytes("loop"), nextOffset: offset);

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var result = DbxMessageBlockReader.ReadChain(source, offset);

        Assert.True(result.Truncated);
        Assert.Equal("loop", Encoding.ASCII.GetString(result.Data));
    }

    [Fact]
    public void TreatsAnOutOfRangeStartOffsetAsAnEmptyTruncatedMessage()
    {
        using var source = new ByteArrayDbxSource(new byte[16]);
        var result = DbxMessageBlockReader.ReadChain(source, startOffset: 9999);

        Assert.True(result.Truncated);
        Assert.Empty(result.Data);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxMessageBlockReaderTests`
Expected: FAIL — `DbxMessageBlockReader` does not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.DbxFormat/DbxMessageBlockReader.cs
namespace LEC.DbxFormat;

public static class DbxMessageBlockReader
{
    public sealed record Result(byte[] Data, bool Truncated);

    public static Result ReadChain(IDbxByteSource source, int startOffset)
    {
        var buffer = new List<byte>();
        var visited = new HashSet<int>();
        int offset = startOffset;
        bool truncated = false;

        while (offset != 0)
        {
            if (offset < 0 || (long)offset + DbxFormat.MessageBlockHeaderSize > source.Length)
            {
                truncated = true;
                break;
            }

            if (!visited.Add(offset))
            {
                truncated = true; // cycle
                break;
            }

            if (visited.Count > DbxSafetyLimits.MaxMessageBlockFragments)
            {
                truncated = true;
                break;
            }

            short blockSize = source.ReadInt16(offset + DbxFormat.MessageBlockSizeOffset);
            if (blockSize <= 0 || blockSize > DbxFormat.MessageBlockMaxDataSize)
            {
                truncated = true;
                break;
            }

            if (buffer.Count + blockSize > DbxSafetyLimits.MaxMessageSizeBytes)
            {
                truncated = true;
                break;
            }

            int dataStart = offset + DbxFormat.MessageBlockDataOffset;
            if ((long)dataStart + blockSize > source.Length)
            {
                truncated = true;
                break;
            }

            var chunk = new byte[blockSize];
            source.ReadBytes(dataStart, chunk);
            buffer.AddRange(chunk);

            offset = source.ReadInt32(offset + DbxFormat.MessageBlockNextOffset);
        }

        return new Result(buffer.ToArray(), truncated);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxMessageBlockReaderTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxMessageBlockReader.cs tests/LEC.DbxFormat.Tests/DbxMessageBlockReaderTests.cs
git commit -m "Add DbxMessageBlockReader: hardened block-chain reassembly"
```

---

### Task 11: `DbxEmailFile` — top-level orchestrator for message `.dbx`

**Files:**
- Create: `src/LEC.DbxFormat/DbxEmailFile.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxEmailFileTests.cs`

**Interfaces:**
- Consumes: `IDbxByteSource`, `MemoryMappedDbxSource`, `DbxHeader`, `DbxIndexTreeReader`, `DbxMessageInfo`, `DbxMessageBlockReader`, `DbxFormat`, `StoreHealth`, `DbxFileType`, `DbxFormatException`.
- Produces: `DbxEmailFile : IDisposable` with `StoreHealth Health`, `IReadOnlyList<DbxMessageInfo> Messages`, `static DbxEmailFile Open(string path)`, `static DbxEmailFile OpenFrom(IDbxByteSource source, bool ownsSource)`, `byte[] ReadMessageRaw(DbxMessageInfo info, out bool truncated)`. Consumed by Task 13 (CLI).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LEC.DbxFormat.Tests/DbxEmailFileTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxEmailFileTests
{
    private static byte[] BuildSingleMessageDbx(out int itemCount)
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        byte[] body = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Hi\r\n\r\nBody");
        int blockOffset = builder.WriteMessageBlock(body, nextOffset: 0);

        int infoNodeOffset = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = (uint)blockOffset },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.Subject, Data = Encoding.ASCII.GetBytes("Hi\0") },
        });

        int rootOffset = builder.WriteIndexNode(
            nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: infoNodeOffset, entryNextTable: 0, entryIndexCount: 0) });

        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, rootOffset);

        itemCount = 1;
        return builder.ToArray();
    }

    [Fact]
    public void OpensAWellFormedSyntheticFileAsHealthy()
    {
        byte[] data = BuildSingleMessageDbx(out _);
        using var file = DbxEmailFile.OpenFrom(new ByteArrayDbxSource(data), ownsSource: true);

        Assert.Equal(StoreHealth.Ok, file.Health);
        Assert.Single(file.Messages);
        Assert.Equal("Hi", file.Messages[0].Subject);

        byte[] raw = file.ReadMessageRaw(file.Messages[0], out bool truncated);
        Assert.False(truncated);
        Assert.Equal("From: a@b.com\r\nSubject: Hi\r\n\r\nBody", Encoding.ASCII.GetString(raw));
    }

    [Fact]
    public void ReportsPartialHealthWhenTheIndexRootIsOutOfRange()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, 999_999); // beyond EOF

        using var file = DbxEmailFile.OpenFrom(new ByteArrayDbxSource(builder.ToArray()), ownsSource: true);

        Assert.Equal(StoreHealth.Partial, file.Health);
        Assert.Empty(file.Messages);
    }

    [Fact]
    public void ThrowsForAFileWithTheWrongSignature()
    {
        using var source = new ByteArrayDbxSource(new byte[256]);
        Assert.Throws<DbxFormatException>(() => DbxEmailFile.OpenFrom(source, ownsSource: true));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxEmailFileTests`
Expected: FAIL — `DbxEmailFile` does not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.DbxFormat/DbxEmailFile.cs
namespace LEC.DbxFormat;

public sealed class DbxEmailFile : IDisposable
{
    private readonly IDbxByteSource _source;
    private readonly bool _ownsSource;

    public StoreHealth Health { get; }
    public IReadOnlyList<DbxMessageInfo> Messages { get; }

    private DbxEmailFile(IDbxByteSource source, bool ownsSource, StoreHealth health, IReadOnlyList<DbxMessageInfo> messages)
    {
        _source = source;
        _ownsSource = ownsSource;
        Health = health;
        Messages = messages;
    }

    public static DbxEmailFile Open(string path) =>
        OpenFrom(new MemoryMappedDbxSource(path), ownsSource: true);

    public static DbxEmailFile OpenFrom(IDbxByteSource source, bool ownsSource)
    {
        if (!DbxHeader.TryDetectType(source, out var type) || type != DbxFileType.Email)
            throw new DbxFormatException("Not a message-type .dbx file (signature mismatch).");

        if (source.Length < DbxFormat.HeaderItemCountOffset + 4 || source.Length < DbxFormat.HeaderIndexPointerOffset + 4)
            return new DbxEmailFile(source, ownsSource, StoreHealth.Corrupt, Array.Empty<DbxMessageInfo>());

        int itemCount = source.ReadInt32(DbxFormat.HeaderItemCountOffset);
        int rootOffset = source.ReadInt32(DbxFormat.HeaderIndexPointerOffset);

        var messages = new List<DbxMessageInfo>();
        bool corrupt = false;

        if (itemCount > 0)
        {
            var indexReader = new DbxIndexTreeReader(source);
            var pointers = indexReader.ReadIndexPointers(rootOffset, out bool indexCorrupt);
            corrupt |= indexCorrupt;

            foreach (int pointer in pointers)
            {
                if (pointer <= 0 || pointer >= source.Length)
                {
                    corrupt = true;
                    continue;
                }

                try
                {
                    messages.Add(DbxMessageInfo.Read(source, pointer));
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    corrupt = true;
                }
            }
        }

        var health = corrupt ? StoreHealth.Partial : StoreHealth.Ok;
        return new DbxEmailFile(source, ownsSource, health, messages);
    }

    public byte[] ReadMessageRaw(DbxMessageInfo info, out bool truncated)
    {
        var result = DbxMessageBlockReader.ReadChain(_source, info.MessageBlockOffset);
        truncated = result.Truncated;
        return result.Data;
    }

    public void Dispose()
    {
        if (_ownsSource)
            _source.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxEmailFileTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Add the real-fixture integration test**

```csharp
// Append to tests/LEC.DbxFormat.Tests/DbxEmailFileTests.cs, inside class DbxEmailFileTests

    private static string? FindRealFixture(string relativePath)
    {
        // Walks up from the test's output directory to find the repo root (marker: LEC.sln),
        // then looks for BCP/<relativePath>. BCP/ is git-ignored and only present on the
        // developer's machine — tests skip gracefully (rather than fail) when it's absent.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LEC.sln")))
            dir = dir.Parent;

        if (dir is null)
            return null;

        string candidate = Path.Combine(dir.FullName, "BCP", relativePath);
        return File.Exists(candidate) ? candidate : null;
    }

    [Fact]
    public void RealFixture_RascunhosDbx_OpensHealthyWithReadableMessages()
    {
        string? path = FindRealFixture("Rascunhos.dbx");
        if (path is null)
            return; // BCP/ not present on this machine — soft skip

        using var file = DbxEmailFile.Open(path);

        Assert.NotEqual(StoreHealth.Corrupt, file.Health);
        Assert.NotEmpty(file.Messages);

        byte[] raw = file.ReadMessageRaw(file.Messages[0], out _);
        string text = Encoding.Latin1.GetString(raw);
        Assert.Contains("Subject:", text);
    }
```

- [ ] **Step 6: Run the real-fixture test**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxEmailFileTests`
Expected: PASS (4 tests) — the real-fixture test runs for real since `BCP/Rascunhos.dbx` exists on this machine.

- [ ] **Step 7: Commit**

```bash
git add src/LEC.DbxFormat/DbxEmailFile.cs tests/LEC.DbxFormat.Tests/DbxEmailFileTests.cs
git commit -m "Add DbxEmailFile: top-level orchestrator with real-fixture verification"
```

---

### Task 12: `DbxFolderFile` — top-level orchestrator for `Folders.dbx`

**Files:**
- Create: `src/LEC.DbxFormat/DbxFolderFile.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxFolderFileTests.cs`

**Interfaces:**
- Consumes: `IDbxByteSource`, `MemoryMappedDbxSource`, `DbxHeader`, `DbxIndexTreeReader`, `DbxFolderInfo`, `DbxFormat`, `StoreHealth`, `DbxFileType`, `DbxFormatException`.
- Produces: `DbxFolderFile : IDisposable` with `StoreHealth Health`, `IReadOnlyList<DbxFolderInfo> Folders`, `static DbxFolderFile Open(string path)`, `static DbxFolderFile OpenFrom(IDbxByteSource source, bool ownsSource)`. Consumed by Task 13 (CLI).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LEC.DbxFormat.Tests/DbxFolderFileTests.cs
using System.Linq;
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxFolderFileTests
{
    [Fact]
    public void OpensASyntheticFolderTreeAndLinksParentIds()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Folder);

        int root = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = Encoding.ASCII.GetBytes("Pastas locais\0") },
        });

        int inbox = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 96 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = Encoding.ASCII.GetBytes("Caixa de entrada\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });

        int rootIndexOffset = builder.WriteIndexNode(
            nextTable: 0, ptrCount: 2, indexCount: 0,
            entries: new[] { (indexPtr: root, entryNextTable: 0, entryIndexCount: 0),
                              (indexPtr: inbox, entryNextTable: 0, entryIndexCount: 0) });

        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 2);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, rootIndexOffset);

        using var file = DbxFolderFile.OpenFrom(new ByteArrayDbxSource(builder.ToArray()), ownsSource: true);

        Assert.Equal(StoreHealth.Ok, file.Health);
        Assert.Equal(2, file.Folders.Count);

        var inboxFolder = file.Folders.Single(f => f.FolderId == 96);
        Assert.Equal(1, inboxFolder.ParentId);
        Assert.Equal("Caixa de entrada", inboxFolder.Name);
        Assert.Equal(DbxSpecialFolderType.Inbox, inboxFolder.SpecialFolderType);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxFolderFileTests`
Expected: FAIL — `DbxFolderFile` does not exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.DbxFormat/DbxFolderFile.cs
namespace LEC.DbxFormat;

public sealed class DbxFolderFile : IDisposable
{
    private readonly IDbxByteSource _source;
    private readonly bool _ownsSource;

    public StoreHealth Health { get; }
    public IReadOnlyList<DbxFolderInfo> Folders { get; }

    private DbxFolderFile(IDbxByteSource source, bool ownsSource, StoreHealth health, IReadOnlyList<DbxFolderInfo> folders)
    {
        _source = source;
        _ownsSource = ownsSource;
        Health = health;
        Folders = folders;
    }

    public static DbxFolderFile Open(string path) =>
        OpenFrom(new MemoryMappedDbxSource(path), ownsSource: true);

    public static DbxFolderFile OpenFrom(IDbxByteSource source, bool ownsSource)
    {
        if (!DbxHeader.TryDetectType(source, out var type) || type != DbxFileType.Folder)
            throw new DbxFormatException("Not a Folders.dbx file (signature mismatch).");

        if (source.Length < DbxFormat.HeaderItemCountOffset + 4 || source.Length < DbxFormat.HeaderIndexPointerOffset + 4)
            return new DbxFolderFile(source, ownsSource, StoreHealth.Corrupt, Array.Empty<DbxFolderInfo>());

        int itemCount = source.ReadInt32(DbxFormat.HeaderItemCountOffset);
        int rootOffset = source.ReadInt32(DbxFormat.HeaderIndexPointerOffset);

        var folders = new List<DbxFolderInfo>();
        bool corrupt = false;

        if (itemCount > 0)
        {
            var indexReader = new DbxIndexTreeReader(source);
            var pointers = indexReader.ReadIndexPointers(rootOffset, out bool indexCorrupt);
            corrupt |= indexCorrupt;

            foreach (int pointer in pointers)
            {
                if (pointer <= 0 || pointer >= source.Length)
                {
                    corrupt = true;
                    continue;
                }

                try
                {
                    folders.Add(DbxFolderInfo.Read(source, pointer));
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    corrupt = true;
                }
            }
        }

        var health = corrupt ? StoreHealth.Partial : StoreHealth.Ok;
        return new DbxFolderFile(source, ownsSource, health, folders);
    }

    public void Dispose()
    {
        if (_ownsSource)
            _source.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxFolderFileTests`
Expected: PASS (1 test)

- [ ] **Step 5: Add the real-fixture integration test**

```csharp
// Append to tests/LEC.DbxFormat.Tests/DbxFolderFileTests.cs, inside class DbxFolderFileTests

    private static string? FindRealFixture(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LEC.sln")))
            dir = dir.Parent;

        if (dir is null)
            return null;

        string candidate = Path.Combine(dir.FullName, "BCP", relativePath);
        return File.Exists(candidate) ? candidate : null;
    }

    [Fact]
    public void RealFixture_FoldersDbx_MatchesTheVerifiedRealTree()
    {
        string? path = FindRealFixture("Folders.dbx");
        if (path is null)
            return; // BCP/ not present on this machine — soft skip

        using var file = DbxFolderFile.Open(path);

        Assert.NotEqual(StoreHealth.Corrupt, file.Health);
        // Verified by direct hex/attribute inspection before this plan was written:
        // BCP/Folders.dbx has exactly 26 entries.
        Assert.Equal(26, file.Folders.Count);

        var inbox = file.Folders.Single(f => f.SpecialFolderType == DbxSpecialFolderType.Inbox);
        Assert.Equal("Caixa de entrada", inbox.Name);
        Assert.Equal("Caixa de entrada (1).dbx", inbox.FileName);
    }
```

- [ ] **Step 6: Run the real-fixture test**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxFolderFileTests`
Expected: PASS (2 tests)

- [ ] **Step 7: Commit**

```bash
git add src/LEC.DbxFormat/DbxFolderFile.cs tests/LEC.DbxFormat.Tests/DbxFolderFileTests.cs
git commit -m "Add DbxFolderFile: top-level orchestrator with real-fixture verification"
```

---

### Task 13: `LEC.Cli` — `dump` and `extract` commands

**Files:**
- Modify: `src/LEC.Cli/Program.cs`

**Interfaces:**
- Consumes: `MemoryMappedDbxSource`, `DbxHeader`, `DbxFileType`, `DbxEmailFile`, `DbxFolderFile` (all from `LEC.DbxFormat`).
- Produces: an executable with two commands, verified manually against real files (a thin presentation layer over already-unit-tested logic; no new automated test project for Phase 1, per YAGNI).

- [ ] **Step 1: Implement**

```csharp
// src/LEC.Cli/Program.cs
using LEC.DbxFormat;

if (args.Length < 2)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  lec dump <file.dbx>");
    Console.WriteLine("  lec extract <file.dbx> <outputDir>");
    return 1;
}

string command = args[0];
string path = args[1];

using var source = new MemoryMappedDbxSource(path);
if (!DbxHeader.TryDetectType(source, out var type))
{
    Console.Error.WriteLine("Not a recognized .dbx file (signature mismatch).");
    return 2;
}

switch (command)
{
    case "dump" when type == DbxFileType.Folder:
        DumpFolders(source);
        return 0;

    case "dump" when type == DbxFileType.Email:
        DumpMessages(source);
        return 0;

    case "extract" when type == DbxFileType.Email:
        if (args.Length < 3)
        {
            Console.Error.WriteLine("extract requires an output directory.");
            return 1;
        }
        ExtractMessages(source, args[2]);
        return 0;

    default:
        Console.Error.WriteLine($"Command '{command}' is not supported for file type {type}.");
        return 1;
}

static void DumpFolders(IDbxByteSource src)
{
    using var file = DbxFolderFile.OpenFrom(src, ownsSource: false);
    Console.WriteLine($"Health: {file.Health}  Folders: {file.Folders.Count}");
    foreach (var f in file.Folders)
        Console.WriteLine($"  [{f.FolderId,4}] parent={f.ParentId,-4} {f.SpecialFolderType,-12} {f.Name} -> {f.FileName} ({f.MessageCount} msgs)");
}

static void DumpMessages(IDbxByteSource src)
{
    using var file = DbxEmailFile.OpenFrom(src, ownsSource: false);
    Console.WriteLine($"Health: {file.Health}  Messages: {file.Messages.Count}");
    foreach (var m in file.Messages)
        Console.WriteLine($"  {m.SendTimeUtc:yyyy-MM-dd HH:mm} | {m.SenderName,-30} | {m.Subject}");
}

static void ExtractMessages(IDbxByteSource src, string outDir)
{
    Directory.CreateDirectory(outDir);
    using var file = DbxEmailFile.OpenFrom(src, ownsSource: false);
    Console.WriteLine($"Health: {file.Health}  Messages: {file.Messages.Count}");

    int i = 0;
    foreach (var m in file.Messages)
    {
        byte[] raw = file.ReadMessageRaw(m, out bool truncated);
        string name = $"{i:D6}{(truncated ? ".truncated" : "")}.eml";
        File.WriteAllBytes(Path.Combine(outDir, name), raw);
        i++;
    }

    Console.WriteLine($"Extracted {i} messages to {outDir}");
}
```

- [ ] **Step 2: Manually verify against real files**

Run:
```bash
dotnet run --project src/LEC.Cli -- dump "BCP/Folders.dbx"
```
Expected: first line `Health: Ok  Folders: 26`, and a line for `Caixa de entrada` showing `Inbox` and `2235 msgs`.

Run:
```bash
dotnet run --project src/LEC.Cli -- dump "BCP/Rascunhos.dbx"
```
Expected: a `Health:` line followed by one row per message with a plausible date, sender, and subject.

Run (extract to a scratch directory outside the repo, not into `BCP/` or the repo tree):
```bash
dotnet run --project src/LEC.Cli -- extract "BCP/Rascunhos.dbx" "$TEMP/lec-extract-test"
```
Expected: `Extracted N messages to ...`, and the output directory contains `N` `.eml` files; opening one in a text editor shows real `From:`/`Subject:` headers.

- [ ] **Step 3: Commit**

```bash
git add src/LEC.Cli/Program.cs
git commit -m "Add LEC.Cli dump/extract commands"
```

---

### Task 14: Full verification pass and wrap-up

**Files:** none (verification only).

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test`
Expected: all tests pass, including both real-fixture tests (`RealFixture_RascunhosDbx_OpensHealthyWithReadableMessages`, `RealFixture_FoldersDbx_MatchesTheVerifiedRealTree`).

- [ ] **Step 2: Smoke-test the largest real files for crashes, hangs, and memory**

Run each of these and confirm it finishes (no exception, no multi-minute hang, no out-of-memory) and prints a `Health:` line:

```bash
dotnet run -c Release --project src/LEC.Cli -- dump "BCP/BCP Espelho.dbx"
dotnet run -c Release --project src/LEC.Cli -- dump "BCP/Recebidos 2004.dbx"
dotnet run -c Release --project src/LEC.Cli -- dump "BCP/Enviados 2004.dbx"
dotnet run -c Release --project src/LEC.Cli -- dump "BCP/Itens enviados.dbx"
```

If any of these reports `Health: Partial` or `Health: Corrupt`, that is expected and fine — real 20-year-old files are exactly what §7.4 is for. What would **not** be fine is a crash, a hang, or unbounded memory growth; if one occurs, stop and treat it as a bug against the relevant reader (most likely `DbxMessageBlockReader` or `DbxIndexTreeReader`) rather than proceeding.

- [ ] **Step 3: Final commit**

```bash
git add -A
git commit -m "Fase 1 complete: verified, hardened .dbx parser with CLI"
```

---

## Out of scope for this plan (later Fases per ARQUITETURA.md §12)

- Fase 2: WPF three-panel shell, `IMailStore`/`DbxStore`/`PstStore`, MimeKit-based rendering.
- Fase 3: scavenge/recovery mode, `Recuperada`/`Fragmento` markers.
- Fase 4+: `.lecarchive` library, dedup, reorganization, export.
