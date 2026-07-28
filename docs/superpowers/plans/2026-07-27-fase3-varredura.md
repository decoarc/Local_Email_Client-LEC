# Fase 3 — Varredura e Recuperação — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Scavenge mode for `.dbx` files: scan raw bytes for message-block chains the index tree doesn't reference, classify them as Recuperada (Recovered) or Fragmento (Fragment), persist results in a lightweight SQLite cache, and surface them in the existing three-panel UI behind an explicit, contextual user action.

**Architecture:** `DbxScavenger` (new, `LEC.DbxFormat`) does the untrusted byte-level scan and chain reconstruction, reusing Fase 1's already-verified `DbxMessageBlockReader`. `LEC.MailStore` gains a MimeKit dependency to classify scavenged chains and extract Subject/Sender/Date (metadata that, for a genuinely orphaned message, only exists in the raw RFC822 bytes — there is no index-tree entry to read it from), plus a small SQLite-backed `ScavengeCache` in `%LOCALAPPDATA%`. `IMailStore` is extended (the Fase 2 plan's "don't add members" was scoped to that plan, not permanent) with scavenge operations. `LEC.App` adds a contextual trigger (only when a folder's `Health` is `Partial`), a show/hide bar, and date-based sorting for the message list (a real gap found during planning — the list currently has no sort order at all, which the "recovered items enter the normal date order" requirement presupposes).

**Tech Stack:** .NET 9, C#, `Microsoft.Data.Sqlite` (new dependency in `LEC.MailStore`), xUnit. No new `LEC.App` dependencies.

## Global Constraints

- `.dbx` files are always read-only — scavenging only ever reads, never writes into a source file, in any circumstance (ARQUITETURA.md §2).
- The scavenge cache lives in `%LOCALAPPDATA%\LEC\`, never next to the source profile — a profile can be on read-only media or a backup drive.
- Varredura never runs automatically. It is always triggered by an explicit user action, and only offered when a folder's `Health` is `Parcial` (ARQUITETURA.md §7.4).
- Recovered content is never called "Excluída" (Deleted) — always "Recuperada" (Recovered) or "Fragmento" (Fragment) (ARQUITETURA.md §7.5).
- Recovered/fragment items are hidden by default; a discrete bar toggles visibility per folder, and that state (plus the scan results themselves) survives an app restart via the cache.
- `LEC.DbxFormat` stays MIME-agnostic — no MimeKit reference there. MIME parsing (for scavenge classification) belongs in `LEC.MailStore`, which already sits above the raw format layer.
- Two new `DbxFormat.cs` constants (block self-offset at byte 0, capacity marker at byte 4, value `0x200`) are verified: 1,639/1,639 real block headers matched across `BCP Espelho.dbx`, `Caixa de entrada.dbx`, and `Enviados 2004.dbx` (chain-start, mid-chain, and chain-end blocks all checked, not just chain starts).
- Known, accepted limitation (documented, not solved by this plan): a chain whose `next` pointer loops backward into another independently-valid candidate can be reported as two overlapping recovered items rather than deduplicated. Not observed in any real `BCP/` file; a full fix needs a two-pass group-by-shared-offset algorithm this plan deliberately does not build (YAGNI until real data shows it matters).
- No automated test drives live WPF rendering — same rule as Fase 2. ViewModels and non-WPF services are unit-tested normally.

---

## File Structure

```
src/LEC.DbxFormat/
  DbxFormat.cs                 Task 1 — +2 constants
  ScavengedMessage.cs           Task 1 — NEW
  DbxMessageBlockReader.cs       Task 2 — MODIFIED: Result gains VisitedOffsets
  DbxScavenger.cs                 Task 2, 3 — NEW
  DbxEmailFile.cs                   Task 4 — MODIFIED: +Scavenge(), +ReadRecoveredMessageRaw()

src/LEC.MailStore/
  RecoveredMessageKind.cs      Task 5 — NEW
  CachedRecoveredItem.cs        Task 5 — NEW
  ScavengedMessageClassifier.cs  Task 5 — NEW
  ScavengeCache.cs                 Task 6 — NEW
  ScavengeStatus.cs                 Task 7 — NEW
  IMailStore.cs                      Task 7 — MODIFIED: +4 members
  DbxStore.cs                          Task 7, 9 — MODIFIED
  MessageHeader.cs                      Task 9 — MODIFIED: +RecoveredKind

src/LEC.App/
  ViewModels/MessageListViewModel.cs   Task 8, 10, 11 — MODIFIED
  ViewModels/MainWindowViewModel.cs     Task 10 — MODIFIED
  Converters/RecoveredKindConverters.cs  Task 11 — NEW
  MainWindow.xaml                          Task 10, 11 — MODIFIED
  MainWindow.xaml.cs                        Task 10 — MODIFIED

tests/LEC.DbxFormat.Tests/
  DbxScavengerTests.cs           Task 2, 3
  DbxEmailFileScavengeTests.cs    Task 4

tests/LEC.MailStore.Tests/
  ScavengedMessageClassifierTests.cs  Task 5
  ScavengeCacheTests.cs                 Task 6
  DbxStoreScavengeTests.cs               Task 7, 9

tests/LEC.App.Tests/
  MessageListViewModelTests.cs (existing, extended)  Task 8, 10, 11
```

---

### Task 1: `DbxFormat` constants + `ScavengedMessage` type

**Files:**
- Modify: `src/LEC.DbxFormat/DbxFormat.cs`
- Create: `src/LEC.DbxFormat/ScavengedMessage.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxScavengerTests.cs` (constants-only assertions; the file is extended further in Tasks 2-3)

**Interfaces:**
- Produces: `DbxFormat.MessageBlockSelfOffsetOffset`, `DbxFormat.MessageBlockMarkerOffset` (both `int`), reusing the existing `DbxFormat.MessageBlockMaxDataSize` as the marker's expected value. `ScavengedMessage(int ChainStartOffset, byte[] RawBytes, bool Truncated)`.

- [ ] **Step 1: Write the constants test**

```csharp
// tests/LEC.DbxFormat.Tests/DbxScavengerTests.cs
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxScavengerTests
{
    [Fact]
    public void NewBlockHeaderConstantsMatchVerifiedRealFileOffsets()
    {
        // Verified against real BCP/ files while writing this plan: 1,639/1,639 real
        // block headers (chain-start, mid-chain, and chain-end) across BCP Espelho.dbx,
        // Caixa de entrada.dbx, and Enviados 2004.dbx all had word0 == the block's own
        // file offset and word1 == 0x200, with zero exceptions.
        Assert.Equal(0, DbxFormat.MessageBlockSelfOffsetOffset);
        Assert.Equal(4, DbxFormat.MessageBlockMarkerOffset);
        Assert.Equal(0x200, DbxFormat.MessageBlockMaxDataSize); // the marker's expected value
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxScavengerTests`
Expected: FAIL — `DbxFormat.MessageBlockSelfOffsetOffset`/`MessageBlockMarkerOffset` don't exist yet.

- [ ] **Step 3: Add the constants**

Add to `src/LEC.DbxFormat/DbxFormat.cs`, after the existing `// ---- Message data block chain ----` block:

```csharp
    // ---- Message block header fields used only for scavenging (Fase 3) ----
    // Fase 1 never read these -- it only ever followed pointers already trusted from
    // the index tree. For scavenging raw, untrusted bytes, they're the primary defense
    // against false positives: verified against 1,639 real block headers across 3 files
    // in BCP/ (100% match, including mid-chain and chain-end blocks, not just chain starts).
    public const int MessageBlockSelfOffsetOffset = 0; // int32: must equal the block's own file offset
    public const int MessageBlockMarkerOffset = 4;     // int32: fixed marker, always equals MessageBlockMaxDataSize
```

- [ ] **Step 4: Add `ScavengedMessage`**

```csharp
// src/LEC.DbxFormat/ScavengedMessage.cs
namespace LEC.DbxFormat;

/// <summary>
/// A message chain found by DbxScavenger that the index tree does not reference.
/// Purely structural -- no MIME awareness (that's LEC.MailStore's job, which already
/// depends on MimeKit; LEC.DbxFormat stays format-only per ARQUITETURA.md §7.2).
/// </summary>
public sealed record ScavengedMessage(int ChainStartOffset, byte[] RawBytes, bool Truncated);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxScavengerTests`
Expected: PASS (1 test)

- [ ] **Step 6: Commit**

```bash
git add src/LEC.DbxFormat/DbxFormat.cs src/LEC.DbxFormat/ScavengedMessage.cs tests/LEC.DbxFormat.Tests/DbxScavengerTests.cs
git commit -m "Add scavenge block-header constants and ScavengedMessage, verified against real BCP/ files"
```

---

### Task 2: `DbxScavenger.FindCandidateBlockStarts` — raw byte-level candidate detection

**Files:**
- Create: `src/LEC.DbxFormat/DbxScavenger.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxScavengerTests.cs` (append)

**Interfaces:**
- Consumes: `IDbxByteSource` (Fase 1), `DbxFormat.MessageBlockSelfOffsetOffset`/`MessageBlockMarkerOffset`/`MessageBlockMaxDataSize`/`MessageBlockHeaderSize` (Task 1, Fase 1).
- Produces: `DbxScavenger.FindCandidateBlockStarts(IDbxByteSource, CancellationToken) : IEnumerable<int>`. Consumed by Task 3.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LEC.DbxFormat.Tests/DbxScavengerTests.cs`:

```csharp
using System.Text;

// (add near the top of the file, alongside the existing `using Xunit;`)
```

```csharp
    private static IDbxByteSource BufferOf(byte[] bytes) => new ByteArrayDbxSource(bytes);

    private static void WriteValidBlockHeader(byte[] buffer, int offset, short dataSize, int next)
    {
        BitConverter.GetBytes(offset).CopyTo(buffer, offset + DbxFormat.MessageBlockSelfOffsetOffset);
        BitConverter.GetBytes(DbxFormat.MessageBlockMaxDataSize).CopyTo(buffer, offset + DbxFormat.MessageBlockMarkerOffset);
        BitConverter.GetBytes(dataSize).CopyTo(buffer, offset + DbxFormat.MessageBlockSizeOffset);
        BitConverter.GetBytes(next).CopyTo(buffer, offset + DbxFormat.MessageBlockNextOffset);
    }

    [Fact]
    public void FindsAValidBlockHeaderPlantedAmongNoise()
    {
        var buffer = new byte[4096];
        new Random(42).NextBytes(buffer); // fill with noise first
        WriteValidBlockHeader(buffer, offset: 512, dataSize: 100, next: 0);

        var candidates = DbxScavenger.FindCandidateBlockStarts(BufferOf(buffer)).ToList();

        Assert.Contains(512, candidates);
    }

    [Fact]
    public void DoesNotReportRandomNoiseAsACandidate()
    {
        // 4096 bytes of noise, no planted header anywhere -- the odds any 4-byte-aligned
        // position coincidentally satisfies BOTH the self-reference AND the exact 0x200
        // marker are astronomically small (effectively zero across 1024 positions).
        var buffer = new byte[4096];
        new Random(1).NextBytes(buffer);

        var candidates = DbxScavenger.FindCandidateBlockStarts(BufferOf(buffer)).ToList();

        Assert.Empty(candidates);
    }

    [Fact]
    public void FindsMultiplePlantedHeadersIndependently()
    {
        var buffer = new byte[4096];
        new Random(7).NextBytes(buffer);
        WriteValidBlockHeader(buffer, offset: 100, dataSize: 50, next: 0);
        WriteValidBlockHeader(buffer, offset: 2000, dataSize: 200, next: 0);

        var candidates = DbxScavenger.FindCandidateBlockStarts(BufferOf(buffer)).ToList();

        Assert.Contains(100, candidates);
        Assert.Contains(2000, candidates);
    }

    [Fact]
    public void IgnoresAHeaderCandidateThatWouldReadPastEndOfFile()
    {
        // A header needs MessageBlockHeaderSize (16) bytes to even be checkable.
        var buffer = new byte[20];
        new Random(3).NextBytes(buffer);
        // Plant a header at offset 8 -- its own 16-byte header fits (8+16=24 > 20 = doesn't fit).
        WriteValidBlockHeader(buffer, offset: 0, dataSize: 4, next: 0); // fits: 0+16=16 <= 20

        var candidates = DbxScavenger.FindCandidateBlockStarts(BufferOf(buffer)).ToList();

        Assert.Equal(new[] { 0 }, candidates);
    }
```

Add `using System.Linq;` to the test file's usings if not already implicit (check `LEC.DbxFormat.Tests.csproj` for `ImplicitUsings` — if `System.Linq` isn't implicit there, add it explicitly).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxScavengerTests`
Expected: FAIL — `DbxScavenger` doesn't exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.DbxFormat/DbxScavenger.cs
namespace LEC.DbxFormat;

public static class DbxScavenger
{
    /// <summary>
    /// Scans the whole file in 4-byte-aligned increments for a valid message-block
    /// header, independent of the index tree. A candidate is any position whose first
    /// word equals its own file offset (self-reference) and whose second word equals
    /// DbxFormat.MessageBlockMaxDataSize (the fixed capacity marker) -- verified against
    /// 1,639 real block headers, see DbxFormat.cs.
    /// </summary>
    public static IEnumerable<int> FindCandidateBlockStarts(IDbxByteSource source, CancellationToken ct = default)
    {
        long length = source.Length;

        for (long offset = 0; offset + DbxFormat.MessageBlockHeaderSize <= length; offset += 4)
        {
            ct.ThrowIfCancellationRequested();

            if (offset > int.MaxValue - DbxFormat.MessageBlockHeaderSize)
                yield break; // beyond what this int32-offset format can address

            int candidate = (int)offset;

            if (source.ReadInt32(candidate + DbxFormat.MessageBlockSelfOffsetOffset) != candidate)
                continue;

            if (source.ReadInt32(candidate + DbxFormat.MessageBlockMarkerOffset) != DbxFormat.MessageBlockMaxDataSize)
                continue;

            yield return candidate;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxScavengerTests`
Expected: PASS (5 tests)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxScavenger.cs tests/LEC.DbxFormat.Tests/DbxScavengerTests.cs
git commit -m "Add DbxScavenger.FindCandidateBlockStarts: raw byte-level block header detection"
```

---

### Task 3: `DbxMessageBlockReader.Result.VisitedOffsets` + `DbxScavenger.Scan`

**Files:**
- Modify: `src/LEC.DbxFormat/DbxMessageBlockReader.cs`
- Modify: `src/LEC.DbxFormat/DbxScavenger.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxScavengerTests.cs` (append)

**Interfaces:**
- Consumes: `DbxScavenger.FindCandidateBlockStarts` (Task 2), `DbxMessageBlockReader.ReadChain` (Fase 1).
- Produces: `DbxMessageBlockReader.Result` gains `IReadOnlySet<int> VisitedOffsets` (additive — existing callers accessing `.Data`/`.Truncated` by name are unaffected). `DbxScavenger.Scan(IDbxByteSource, IReadOnlySet<int> alreadyReferencedOffsets, IProgress<double>?, CancellationToken) : IReadOnlyList<ScavengedMessage>`. Consumed by Task 4.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LEC.DbxFormat.Tests/DbxScavengerTests.cs`:

```csharp
    private static void WriteChain(byte[] buffer, (int offset, short size, int next)[] blocks, byte[] payload)
    {
        int payloadPos = 0;
        foreach (var (offset, size, next) in blocks)
        {
            WriteValidBlockHeader(buffer, offset, size, next);
            Array.Copy(payload, payloadPos, buffer, offset + DbxFormat.MessageBlockDataOffset, size);
            payloadPos += size;
        }
    }

    [Fact]
    public void ScanFindsAnOrphanChainNotInTheReferencedSet()
    {
        var buffer = new byte[4096];
        new Random(11).NextBytes(buffer);
        byte[] payload = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Orfa\r\n\r\nCorpo orfao");
        WriteChain(buffer, new[] { (500, (short)payload.Length, 0) }, payload);

        var results = DbxScavenger.Scan(BufferOf(buffer), alreadyReferencedOffsets: new HashSet<int>());

        var found = Assert.Single(results);
        Assert.Equal(500, found.ChainStartOffset);
        Assert.False(found.Truncated);
        Assert.Equal(payload, found.RawBytes);
    }

    [Fact]
    public void ScanSkipsChainsAlreadyInTheReferencedSet()
    {
        var buffer = new byte[4096];
        new Random(12).NextBytes(buffer);
        byte[] payload = Encoding.ASCII.GetBytes("conteudo normal, ja indexado");
        WriteChain(buffer, new[] { (600, (short)payload.Length, 0) }, payload);

        var results = DbxScavenger.Scan(BufferOf(buffer), alreadyReferencedOffsets: new HashSet<int> { 600 });

        Assert.Empty(results);
    }

    [Fact]
    public void ScanDoesNotDoubleReportBlocksSharedByASingleChain()
    {
        var buffer = new byte[4096];
        new Random(13).NextBytes(buffer);
        byte[] payload = Encoding.ASCII.GetBytes(new string('x', 20) + new string('y', 20));
        // Two blocks, chained: 700 -> 800 -> 0.
        WriteChain(buffer,
            new[] { (700, (short)20, 800), (800, (short)20, 0) },
            payload);

        var results = DbxScavenger.Scan(BufferOf(buffer), alreadyReferencedOffsets: new HashSet<int>());

        // Only one ScavengedMessage for the whole two-block chain -- 800 (a valid header
        // in its own right, since every block satisfies the self-ref+marker check) must
        // not be reported as a second, independent orphan.
        Assert.Single(results);
    }

    [Fact]
    public void ScanReportsTruncatedChainsWithTruncatedTrue()
    {
        var buffer = new byte[200];
        new Random(14).NextBytes(buffer);
        // A chain whose "next" pointer goes past end-of-file -- ReadChain already handles
        // this (Fase 1), Scan just needs to surface it via ScavengedMessage.Truncated.
        WriteValidBlockHeader(buffer, offset: 50, dataSize: 30, next: 999_999);

        var results = DbxScavenger.Scan(BufferOf(buffer), alreadyReferencedOffsets: new HashSet<int>());

        var found = Assert.Single(results);
        Assert.True(found.Truncated);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxScavengerTests`
Expected: FAIL — `DbxScavenger.Scan` doesn't exist yet.

- [ ] **Step 3: Implement**

Modify `src/LEC.DbxFormat/DbxMessageBlockReader.cs` — add `VisitedOffsets` to the result record and return it:

```csharp
    public sealed record Result(byte[] Data, bool Truncated, IReadOnlySet<int> VisitedOffsets);
```

```csharp
        return new Result(buffer.ToArray(), truncated, visited);
```

(Only those two lines change — `visited` is already a local `HashSet<int>` built during the existing walk; this just exposes it instead of discarding it.)

Add to `src/LEC.DbxFormat/DbxScavenger.cs`:

```csharp
    /// <summary>
    /// Full scavenge scan: finds every candidate block header, excludes anything already
    /// covered by <paramref name="alreadyReferencedOffsets"/> (the full set of offsets
    /// belonging to messages the index tree already found -- not just their chain starts;
    /// see DbxEmailFile.Scavenge for how that set is built), reconstructs each remaining
    /// chain, and reports one ScavengedMessage per chain.
    ///
    /// Known limitation, accepted for this plan: a chain whose "next" pointer loops
    /// backward into another independently-valid candidate can be reported twice, since
    /// candidates already visited earlier in the scan are only excluded from *later*
    /// candidates, not retroactively. Not observed in any real BCP/ file. A fully correct
    /// fix needs a two-pass group-by-shared-offset algorithm this plan does not build.
    /// </summary>
    public static IReadOnlyList<ScavengedMessage> Scan(
        IDbxByteSource source,
        IReadOnlySet<int> alreadyReferencedOffsets,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<ScavengedMessage>();
        var consumed = new HashSet<int>(alreadyReferencedOffsets);

        var candidates = FindCandidateBlockStarts(source, ct).ToList();
        for (int i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((double)i / Math.Max(1, candidates.Count));

            int candidate = candidates[i];
            if (consumed.Contains(candidate))
                continue;

            var chain = DbxMessageBlockReader.ReadChain(source, candidate);
            foreach (int visited in chain.VisitedOffsets)
                consumed.Add(visited);

            if (chain.Data.Length == 0)
                continue; // header validated, but the chain itself yielded no usable data

            results.Add(new ScavengedMessage(candidate, chain.Data, chain.Truncated));
        }

        progress?.Report(1.0);
        return results;
    }
```

Add `using System.Linq;` to `DbxScavenger.cs` if `.ToList()` doesn't resolve (check `LEC.DbxFormat.csproj`'s `ImplicitUsings` setting first).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxScavengerTests`
Expected: PASS (9 tests)

Run: `dotnet test tests/LEC.DbxFormat.Tests`
Expected: all existing Fase 1 tests still pass (the `DbxMessageBlockReader.Result` change is additive).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxMessageBlockReader.cs src/LEC.DbxFormat/DbxScavenger.cs tests/LEC.DbxFormat.Tests/DbxScavengerTests.cs
git commit -m "Add DbxScavenger.Scan: full orphan-chain reconstruction excluding already-referenced blocks"
```

---

### Task 4: `DbxEmailFile.Scavenge` + `ReadRecoveredMessageRaw`

**Files:**
- Modify: `src/LEC.DbxFormat/DbxEmailFile.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxEmailFileScavengeTests.cs`

**Interfaces:**
- Consumes: `DbxScavenger.Scan` (Task 3), `DbxMessageBlockReader.ReadChain` (Fase 1), `DbxEmailFile.Messages`/`_source` (Fase 1, already private — this task adds methods on `DbxEmailFile` itself so `_source` never needs to leak out).
- Produces: `DbxEmailFile.Scavenge(IProgress<double>?, CancellationToken) : IReadOnlyList<ScavengedMessage>`, `DbxEmailFile.ReadRecoveredMessageRaw(int chainStartOffset, out bool truncated) : byte[]`. Consumed by Task 7.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LEC.DbxFormat.Tests/DbxEmailFileScavengeTests.cs
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxEmailFileScavengeTests
{
    [Fact]
    public void ScavengeFindsAnOrphanBlockNotReferencedByTheNormalIndex()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        // One normal, indexed message.
        int normalBlock = builder.WriteMessageBlock("From: a@b.com\r\nSubject: Normal\r\n\r\nCorpo normal"u8.ToArray(), next: 0);
        int infoNode = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = normalBlock },
        });
        int indexRoot = builder.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: infoNode, entryNextTable: 0, entryIndexCount: 0) });
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, indexRoot);

        // A second, orphan message block -- written to the buffer but never referenced
        // by any info/index node. WriteMessageBlock alone is enough: it's a real,
        // self-referencing, 0x200-marked block, just not indexed.
        int orphanBlock = builder.WriteMessageBlock("From: c@d.com\r\nSubject: Orfa\r\n\r\nCorpo orfao"u8.ToArray(), next: 0);

        using var email = DbxEmailFile.OpenFrom(builder.ToSource(), ownsSource: true);
        Assert.Single(email.Messages); // only the normal one, via the trusted index path

        var scavenged = email.Scavenge();

        var found = Assert.Single(scavenged);
        Assert.Equal(orphanBlock, found.ChainStartOffset);
        Assert.False(found.Truncated);
    }

    [Fact]
    public void ScavengeFindsNothingWhenEveryBlockIsAlreadyIndexed()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);

        int block = builder.WriteMessageBlock("From: a@b.com\r\nSubject: X\r\n\r\nY"u8.ToArray(), next: 0);
        int infoNode = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = block },
        });
        int indexRoot = builder.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: infoNode, entryNextTable: 0, entryIndexCount: 0) });
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, indexRoot);

        using var email = DbxEmailFile.OpenFrom(builder.ToSource(), ownsSource: true);

        Assert.Empty(email.Scavenge());
    }

    [Fact]
    public void ReadRecoveredMessageRawReturnsTheOrphanBytesGivenItsChainOffset()
    {
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Email);
        builder.SetInt32At(DbxFormat.HeaderItemCountOffset, 0);
        builder.SetInt32At(DbxFormat.HeaderIndexPointerOffset, 0);

        byte[] payload = "From: c@d.com\r\nSubject: Orfa\r\n\r\nCorpo orfao"u8.ToArray();
        int orphanBlock = builder.WriteMessageBlock(payload, next: 0);

        using var email = DbxEmailFile.OpenFrom(builder.ToSource(), ownsSource: true);

        byte[] raw = email.ReadRecoveredMessageRaw(orphanBlock, out bool truncated);

        Assert.False(truncated);
        Assert.Equal(payload, raw);
    }
}
```

**Note for the implementer:** this test assumes `TestDbxBuilder` (Fase 1, in `LEC.DbxFormat.Tests`) has a `WriteMessageBlock(byte[] payload, int next) : int` method that writes a single block with the payload split across as many 512-byte fragments as needed, chained via `next`, and returns the first block's offset. Check `TestDbxBuilder.cs` first: if `WriteMessageBlock` doesn't exist yet (Fase 1 may have only needed `WriteHeader`/`WriteInfoNode`/`WriteIndexNode`/`SetInt32At`, with message *block* writing folded directly into whichever Fase 1 task needed it), find how Fase 1's own tests construct a message block chain (likely in `DbxEmailFileTests.cs` or similar) and either reuse that exact helper or add a minimal `WriteMessageBlock` to `TestDbxBuilder` yourself, matching its existing code style, before writing these tests against it. This is exactly the kind of "verify against the real existing helper before assuming its shape" step this project's plans have needed before (Fase 1 Task 4's `TestDbxBuilder`, Fase 2 Task 4's namespace collision) — don't guess the signature.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxEmailFileScavengeTests`
Expected: FAIL — `DbxEmailFile.Scavenge`/`ReadRecoveredMessageRaw` don't exist yet.

- [ ] **Step 3: Implement**

Add to `src/LEC.DbxFormat/DbxEmailFile.cs`, after the existing `ReadMessageRaw` method:

```csharp
    /// <summary>
    /// Scavenges this file for message chains the index tree does not reference.
    /// Builds the "already referenced" set by walking every already-indexed message's
    /// full chain (not just its start offset) -- every block in a chain independently
    /// satisfies the same self-reference+marker check a scavenge candidate does, so a
    /// mid-chain block of a perfectly normal message must not be reported as an orphan.
    /// </summary>
    public IReadOnlyList<ScavengedMessage> Scavenge(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var referenced = new HashSet<int>();
        foreach (var info in Messages)
        {
            if (info.MessageBlockOffset <= 0)
                continue;

            var chain = DbxMessageBlockReader.ReadChain(_source, info.MessageBlockOffset);
            foreach (int offset in chain.VisitedOffsets)
                referenced.Add(offset);
        }

        return DbxScavenger.Scan(_source, referenced, progress, ct);
    }

    /// <summary>
    /// Re-reads the raw bytes of a chain found by Scavenge, given the chain-start offset
    /// from its ScavengedMessage. Scavenge itself already has the bytes in memory at scan
    /// time, but they aren't persisted (only metadata is cached -- see LEC.MailStore's
    /// ScavengeCache) so opening a recovered message later re-walks the chain fresh.
    /// </summary>
    public byte[] ReadRecoveredMessageRaw(int chainStartOffset, out bool truncated)
    {
        var result = DbxMessageBlockReader.ReadChain(_source, chainStartOffset);
        truncated = result.Truncated;
        return result.Data;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter DbxEmailFileScavengeTests`
Expected: PASS (3 tests)

Run: `dotnet test tests/LEC.DbxFormat.Tests`
Expected: all tests pass, no regressions.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.DbxFormat/DbxEmailFile.cs tests/LEC.DbxFormat.Tests/DbxEmailFileScavengeTests.cs
git commit -m "Add DbxEmailFile.Scavenge and ReadRecoveredMessageRaw"
```

---

### Task 5: `LEC.MailStore` classification — `RecoveredMessageKind`, `CachedRecoveredItem`, `ScavengedMessageClassifier`

**Files:**
- Modify: `src/LEC.MailStore/LEC.MailStore.csproj` (+ MimeKit package reference)
- Create: `src/LEC.MailStore/RecoveredMessageKind.cs`
- Create: `src/LEC.MailStore/CachedRecoveredItem.cs`
- Create: `src/LEC.MailStore/ScavengedMessageClassifier.cs`
- Test: `tests/LEC.MailStore.Tests/ScavengedMessageClassifierTests.cs`

**Interfaces:**
- Consumes: `ScavengedMessage` (`LEC.DbxFormat`, Task 1), `MimeKit.MimeMessage`.
- Produces: `RecoveredMessageKind { Recovered, Fragment }`, `CachedRecoveredItem(int ChainOffset, long ByteLength, RecoveredMessageKind Kind, string? Subject, string? SenderName, string? SenderAddress, DateTime? DateUtc)`, `ScavengedMessageClassifier.Classify(ScavengedMessage) : CachedRecoveredItem`. Consumed by Task 6, 7.

- [ ] **Step 1: Add the MimeKit package reference**

```bash
dotnet add src/LEC.MailStore/LEC.MailStore.csproj package MimeKit
```

Run: `dotnet build`
Expected: `Build succeeded.` (MimeKit is already used elsewhere in the solution — `LEC.App` — so the version should resolve without conflict; if `dotnet add` picks a different version than `LEC.App` already uses, check both `.csproj` files afterward and align them to the same version so the solution doesn't carry two MimeKit versions.)

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/LEC.MailStore.Tests/ScavengedMessageClassifierTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.MailStore.Tests;

public class ScavengedMessageClassifierTests
{
    [Fact]
    public void ClassifiesAnIntactParseableChainAsRecovered()
    {
        byte[] raw = Encoding.ASCII.GetBytes(
            "From: \"Ana\" <ana@example.com>\r\nSubject: Recuperada\r\nDate: Fri, 21 Oct 2005 11:11:49 -0300\r\n\r\nCorpo");
        var scavenged = new ScavengedMessage(ChainStartOffset: 100, RawBytes: raw, Truncated: false);

        var result = ScavengedMessageClassifier.Classify(scavenged);

        Assert.Equal(RecoveredMessageKind.Recovered, result.Kind);
        Assert.Equal("Recuperada", result.Subject);
        Assert.Equal("Ana", result.SenderName);
        Assert.Equal("ana@example.com", result.SenderAddress);
        Assert.NotNull(result.DateUtc);
        Assert.Equal(100, result.ChainOffset);
        Assert.Equal(raw.Length, result.ByteLength);
    }

    [Fact]
    public void ClassifiesATruncatedChainAsFragmentEvenIfHeadersParse()
    {
        byte[] raw = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Parcial\r\n\r\nCorpo");
        var scavenged = new ScavengedMessage(ChainStartOffset: 200, RawBytes: raw, Truncated: true);

        var result = ScavengedMessageClassifier.Classify(scavenged);

        Assert.Equal(RecoveredMessageKind.Fragment, result.Kind);
    }

    [Fact]
    public void ClassifiesUnparseableHeadersAsFragmentEvenIfNotTruncated()
    {
        // Genuinely not RFC5322-parseable: no header/body structure at all.
        byte[] raw = Encoding.ASCII.GetBytes("isso nao e um cabecalho de email valido, so texto solto");
        var scavenged = new ScavengedMessage(ChainStartOffset: 300, RawBytes: raw, Truncated: false);

        var result = ScavengedMessageClassifier.Classify(scavenged);

        Assert.Equal(RecoveredMessageKind.Fragment, result.Kind);
        Assert.Null(result.Subject);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter ScavengedMessageClassifierTests`
Expected: FAIL — types don't exist yet.

- [ ] **Step 4: Implement**

```csharp
// src/LEC.MailStore/RecoveredMessageKind.cs
namespace LEC.MailStore;

/// <summary>
/// Never "Excluída" (Deleted) -- per ARQUITETURA.md §7.5, we don't know why a block
/// ended up unreferenced by the index tree, only that it did.
/// </summary>
public enum RecoveredMessageKind
{
    Recovered, // "Recuperada": intact chain, headers parse as a real message.
    Fragment,  // "Fragmento": truncated chain, or headers don't parse.
}
```

```csharp
// src/LEC.MailStore/CachedRecoveredItem.cs
namespace LEC.MailStore;

/// <summary>
/// A scavenged message after classification -- what both ScavengeCache persists and
/// DbxStore turns into a MessageHeader for the UI.
/// </summary>
public sealed record CachedRecoveredItem(
    int ChainOffset,
    long ByteLength,
    RecoveredMessageKind Kind,
    string? Subject,
    string? SenderName,
    string? SenderAddress,
    DateTime? DateUtc);
```

```csharp
// src/LEC.MailStore/ScavengedMessageClassifier.cs
using System.Linq;
using LEC.DbxFormat;
using MimeKit;

namespace LEC.MailStore;

public static class ScavengedMessageClassifier
{
    public static CachedRecoveredItem Classify(ScavengedMessage scavenged)
    {
        MimeMessage? parsed;
        try
        {
            using var stream = new MemoryStream(scavenged.RawBytes);
            parsed = MimeMessage.Load(stream);
        }
        catch (FormatException)
        {
            parsed = null;
        }

        var kind = (!scavenged.Truncated && parsed is not null)
            ? RecoveredMessageKind.Recovered
            : RecoveredMessageKind.Fragment;

        var mailbox = parsed?.From.Mailboxes.FirstOrDefault();

        return new CachedRecoveredItem(
            ChainOffset: scavenged.ChainStartOffset,
            ByteLength: scavenged.RawBytes.Length,
            Kind: kind,
            Subject: parsed?.Subject,
            SenderName: mailbox?.Name,
            SenderAddress: mailbox?.Address,
            DateUtc: parsed?.Date.UtcDateTime);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter ScavengedMessageClassifierTests`
Expected: PASS (3 tests)

- [ ] **Step 6: Commit**

```bash
git add src/LEC.MailStore/LEC.MailStore.csproj src/LEC.MailStore/RecoveredMessageKind.cs src/LEC.MailStore/CachedRecoveredItem.cs src/LEC.MailStore/ScavengedMessageClassifier.cs tests/LEC.MailStore.Tests/ScavengedMessageClassifierTests.cs
git commit -m "Add ScavengedMessageClassifier: Recuperada/Fragmento classification with MimeKit"
```

---

### Task 6: `ScavengeCache` — SQLite persistence in AppData

**Files:**
- Modify: `src/LEC.MailStore/LEC.MailStore.csproj` (+ Microsoft.Data.Sqlite package reference)
- Create: `src/LEC.MailStore/ScavengeCache.cs`
- Test: `tests/LEC.MailStore.Tests/ScavengeCacheTests.cs`

**Interfaces:**
- Consumes: `CachedRecoveredItem`, `RecoveredMessageKind` (Task 5).
- Produces: `ScavengeCache(string databasePath)`, `.GetScan(string profilePath, string folderFile) : ScavengeCacheEntry?`, `.SaveScan(string profilePath, string folderFile, IReadOnlyList<CachedRecoveredItem>)`, `.SetVisibility(string profilePath, string folderFile, bool visible)`, `ScavengeCache.DefaultDatabasePath` (static), `ScavengeCacheEntry(IReadOnlyList<CachedRecoveredItem> Items, bool IsVisible)`. Consumed by Task 7.

- [ ] **Step 1: Add the Microsoft.Data.Sqlite package reference**

```bash
dotnet add src/LEC.MailStore/LEC.MailStore.csproj package Microsoft.Data.Sqlite
```

Run: `dotnet build`
Expected: `Build succeeded.`

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/LEC.MailStore.Tests/ScavengeCacheTests.cs
using Xunit;

namespace LEC.MailStore.Tests;

public class ScavengeCacheTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lec-scavenge-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void GetScanReturnsNullWhenNeverScanned()
    {
        using var cache = new ScavengeCache(TempDbPath());

        var result = cache.GetScan(@"C:\profiles\BCP", "Caixa de entrada.dbx");

        Assert.Null(result);
    }

    [Fact]
    public void SaveScanThenGetScanRoundTripsTheItems()
    {
        string dbPath = TempDbPath();
        var items = new[]
        {
            new CachedRecoveredItem(500, 120, RecoveredMessageKind.Recovered, "Assunto", "Ana", "ana@x.com", new DateTime(2003, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
            new CachedRecoveredItem(900, 40, RecoveredMessageKind.Fragment, null, null, null, null),
        };

        using (var cache = new ScavengeCache(dbPath))
            cache.SaveScan(@"C:\profiles\BCP", "Caixa de entrada.dbx", items);

        using (var reopened = new ScavengeCache(dbPath))
        {
            var result = reopened.GetScan(@"C:\profiles\BCP", "Caixa de entrada.dbx");

            Assert.NotNull(result);
            Assert.False(result!.IsVisible); // default: hidden until explicitly shown
            Assert.Equal(2, result.Items.Count);
            var recovered = Assert.Single(result.Items, i => i.Kind == RecoveredMessageKind.Recovered);
            Assert.Equal("Assunto", recovered.Subject);
            Assert.Equal("Ana", recovered.SenderName);
            var fragment = Assert.Single(result.Items, i => i.Kind == RecoveredMessageKind.Fragment);
            Assert.Null(fragment.Subject);
        }
    }

    [Fact]
    public void SetVisibilityPersistsAcrossReopen()
    {
        string dbPath = TempDbPath();
        var items = new[] { new CachedRecoveredItem(1, 1, RecoveredMessageKind.Recovered, "S", null, null, null) };

        using (var cache = new ScavengeCache(dbPath))
        {
            cache.SaveScan(@"C:\p", "f.dbx", items);
            cache.SetVisibility(@"C:\p", "f.dbx", visible: true);
        }

        using (var reopened = new ScavengeCache(dbPath))
        {
            var result = reopened.GetScan(@"C:\p", "f.dbx");
            Assert.True(result!.IsVisible);
        }
    }

    [Fact]
    public void SaveScanReplacesAnyPreviousResultForTheSameFolder()
    {
        string dbPath = TempDbPath();

        using var cache = new ScavengeCache(dbPath);
        cache.SaveScan(@"C:\p", "f.dbx", new[] { new CachedRecoveredItem(1, 1, RecoveredMessageKind.Recovered, "Primeiro", null, null, null) });
        cache.SaveScan(@"C:\p", "f.dbx", new[] { new CachedRecoveredItem(2, 2, RecoveredMessageKind.Fragment, "Segundo", null, null, null) });

        var result = cache.GetScan(@"C:\p", "f.dbx");

        var item = Assert.Single(result!.Items);
        Assert.Equal("Segundo", item.Subject);
    }

    [Fact]
    public void DifferentProfilesWithTheSameFolderFileNameDoNotCollide()
    {
        string dbPath = TempDbPath();
        using var cache = new ScavengeCache(dbPath);

        cache.SaveScan(@"C:\profileA", "Caixa de entrada.dbx", new[] { new CachedRecoveredItem(1, 1, RecoveredMessageKind.Recovered, "A", null, null, null) });
        cache.SaveScan(@"C:\profileB", "Caixa de entrada.dbx", new[] { new CachedRecoveredItem(2, 2, RecoveredMessageKind.Recovered, "B", null, null, null) });

        Assert.Equal("A", Assert.Single(cache.GetScan(@"C:\profileA", "Caixa de entrada.dbx")!.Items).Subject);
        Assert.Equal("B", Assert.Single(cache.GetScan(@"C:\profileB", "Caixa de entrada.dbx")!.Items).Subject);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter ScavengeCacheTests`
Expected: FAIL — `ScavengeCache` doesn't exist yet.

- [ ] **Step 4: Implement**

```csharp
// src/LEC.MailStore/ScavengeCache.cs
using Microsoft.Data.Sqlite;

namespace LEC.MailStore;

public sealed record ScavengeCacheEntry(IReadOnlyList<CachedRecoveredItem> Items, bool IsVisible);

/// <summary>
/// Lightweight SQLite cache for scavenge results, so they survive an app restart
/// without a full library (Fase 4). Always lives under %LOCALAPPDATA%\LEC\, never
/// next to the source profile -- see ARQUITETURA.md §2 and the Fase 3 design doc §6.
/// Schema deliberately minimal: Fase 4 can extend it without migrating any data.
/// </summary>
public sealed class ScavengeCache : IDisposable
{
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LEC", "scavenge-cache.db");

    private readonly SqliteConnection _connection;

    public ScavengeCache(string databasePath)
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
            CREATE TABLE IF NOT EXISTS scan (
                profile_path TEXT NOT NULL,
                folder_file  TEXT NOT NULL,
                scanned_at   INTEGER NOT NULL,
                is_visible   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (profile_path, folder_file)
            );
            CREATE TABLE IF NOT EXISTS recovered_item (
                profile_path   TEXT NOT NULL,
                folder_file    TEXT NOT NULL,
                chain_offset   INTEGER NOT NULL,
                byte_length    INTEGER NOT NULL,
                kind           TEXT NOT NULL,
                subject        TEXT,
                sender_name    TEXT,
                sender_address TEXT,
                date_utc       INTEGER,
                PRIMARY KEY (profile_path, folder_file, chain_offset)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public ScavengeCacheEntry? GetScan(string profilePath, string folderFile)
    {
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT is_visible FROM scan WHERE profile_path = $p AND folder_file = $f";
            cmd.Parameters.AddWithValue("$p", profilePath);
            cmd.Parameters.AddWithValue("$f", folderFile);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            bool isVisible = reader.GetInt64(0) != 0;
            return new ScavengeCacheEntry(GetItems(profilePath, folderFile), isVisible);
        }
    }

    private List<CachedRecoveredItem> GetItems(string profilePath, string folderFile)
    {
        var items = new List<CachedRecoveredItem>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT chain_offset, byte_length, kind, subject, sender_name, sender_address, date_utc
            FROM recovered_item WHERE profile_path = $p AND folder_file = $f
            """;
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new CachedRecoveredItem(
                ChainOffset: reader.GetInt32(0),
                ByteLength: reader.GetInt64(1),
                Kind: Enum.Parse<RecoveredMessageKind>(reader.GetString(2)),
                Subject: reader.IsDBNull(3) ? null : reader.GetString(3),
                SenderName: reader.IsDBNull(4) ? null : reader.GetString(4),
                SenderAddress: reader.IsDBNull(5) ? null : reader.GetString(5),
                DateUtc: reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)).UtcDateTime));
        }
        return items;
    }

    public void SaveScan(string profilePath, string folderFile, IReadOnlyList<CachedRecoveredItem> items)
    {
        using var transaction = _connection.BeginTransaction();

        using (var delItems = _connection.CreateCommand())
        {
            delItems.Transaction = transaction;
            delItems.CommandText = "DELETE FROM recovered_item WHERE profile_path = $p AND folder_file = $f";
            delItems.Parameters.AddWithValue("$p", profilePath);
            delItems.Parameters.AddWithValue("$f", folderFile);
            delItems.ExecuteNonQuery();
        }

        using (var upsertScan = _connection.CreateCommand())
        {
            upsertScan.Transaction = transaction;
            upsertScan.CommandText = """
                INSERT INTO scan (profile_path, folder_file, scanned_at, is_visible)
                VALUES ($p, $f, $t, 0)
                ON CONFLICT(profile_path, folder_file) DO UPDATE SET scanned_at = $t
                """;
            upsertScan.Parameters.AddWithValue("$p", profilePath);
            upsertScan.Parameters.AddWithValue("$f", folderFile);
            upsertScan.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            upsertScan.ExecuteNonQuery();
        }

        foreach (var item in items)
        {
            using var insertItem = _connection.CreateCommand();
            insertItem.Transaction = transaction;
            insertItem.CommandText = """
                INSERT INTO recovered_item
                    (profile_path, folder_file, chain_offset, byte_length, kind, subject, sender_name, sender_address, date_utc)
                VALUES ($p, $f, $offset, $len, $kind, $subject, $senderName, $senderAddr, $date)
                """;
            insertItem.Parameters.AddWithValue("$p", profilePath);
            insertItem.Parameters.AddWithValue("$f", folderFile);
            insertItem.Parameters.AddWithValue("$offset", item.ChainOffset);
            insertItem.Parameters.AddWithValue("$len", item.ByteLength);
            insertItem.Parameters.AddWithValue("$kind", item.Kind.ToString());
            insertItem.Parameters.AddWithValue("$subject", (object?)item.Subject ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("$senderName", (object?)item.SenderName ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("$senderAddr", (object?)item.SenderAddress ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("$date", item.DateUtc is { } d
                ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds()
                : (object)DBNull.Value);
            insertItem.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SetVisibility(string profilePath, string folderFile, bool visible)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE scan SET is_visible = $v WHERE profile_path = $p AND folder_file = $f";
        cmd.Parameters.AddWithValue("$v", visible ? 1 : 0);
        cmd.Parameters.AddWithValue("$p", profilePath);
        cmd.Parameters.AddWithValue("$f", folderFile);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter ScavengeCacheTests`
Expected: PASS (5 tests)

- [ ] **Step 6: Commit**

```bash
git add src/LEC.MailStore/LEC.MailStore.csproj src/LEC.MailStore/ScavengeCache.cs tests/LEC.MailStore.Tests/ScavengeCacheTests.cs
git commit -m "Add ScavengeCache: SQLite persistence for scavenge results in AppData"
```

---

### Task 7: `IMailStore` extension + `DbxStore` wiring — `ScavengeStatus`, `ScavengeAsync`, `SetScavengeVisibility`

**Files:**
- Create: `src/LEC.MailStore/ScavengeStatus.cs`
- Modify: `src/LEC.MailStore/IMailStore.cs`
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Test: `tests/LEC.MailStore.Tests/DbxStoreScavengeTests.cs`

**Interfaces:**
- Consumes: `DbxEmailFile.Scavenge` (Task 4), `ScavengedMessageClassifier.Classify` (Task 5), `ScavengeCache` (Task 6).
- Produces: `ScavengeStatus(ScavengeState State, int RecoveredCount, bool IsVisible)` + `ScavengeState { NeverScanned, Scanned }`. `IMailStore.GetFolderHealth(StoreFolder) : StoreHealth`, `IMailStore.GetScavengeStatus(StoreFolder) : ScavengeStatus`, `IMailStore.ScavengeAsync(StoreFolder, IProgress<double>?, CancellationToken) : Task<ScavengeStatus>`, `IMailStore.SetScavengeVisibility(StoreFolder, bool)`. `DbxStore.Open(string profileDirectoryPath, string? scavengeCacheDatabasePath = null)` (new optional parameter, existing single-argument call sites unaffected). Consumed by Task 9, 10, 11.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LEC.MailStore.Tests/DbxStoreScavengeTests.cs
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
        localFolders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, foldersRoot);

        // A normal, perfectly healthy message -- but the header claims 2 items while the
        // index tree only actually holds 1, which is exactly what makes DbxEmailFile.Health
        // come back Partial (Fase 1) even though the one real message has Health.Ok.
        var inbox = new TestDbxBuilder();
        inbox.WriteHeader(DbxFileType.Email);
        int block = inbox.WriteMessageBlock("From: a@b.com\r\nSubject: Normal\r\n\r\nOk"u8.ToArray(), next: 0);
        int infoNode = inbox.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxEmailFieldId.MessageBlockOffset, Direct = true, DirectValue = block },
        });
        int indexRoot = inbox.WriteIndexNode(nextTable: 0, ptrCount: 1, indexCount: 0,
            entries: new[] { (indexPtr: infoNode, entryNextTable: 0, entryIndexCount: 0) });
        inbox.SetInt32At(DbxFormat.HeaderItemCountOffset, 2); // claims 2, tree only has 1
        inbox.SetInt32At(DbxFormat.HeaderIndexPointerOffset, indexRoot);

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
        localFolders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, foldersRoot);

        var inbox = new TestDbxBuilder();
        inbox.WriteHeader(DbxFileType.Email);
        inbox.SetInt32At(DbxFormat.HeaderItemCountOffset, 0); // no normal messages at all
        inbox.SetInt32At(DbxFormat.HeaderIndexPointerOffset, 0);
        inbox.WriteMessageBlock("From: c@d.com\r\nSubject: Orfa\r\n\r\nCorpo orfao"u8.ToArray(), next: 0);

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
        localFolders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, foldersRoot);

        var inbox = new TestDbxBuilder();
        inbox.WriteHeader(DbxFileType.Email);
        inbox.SetInt32At(DbxFormat.HeaderItemCountOffset, 0);
        inbox.SetInt32At(DbxFormat.HeaderIndexPointerOffset, 0);
        inbox.WriteMessageBlock("From: c@d.com\r\nSubject: Orfa\r\n\r\nCorpo"u8.ToArray(), next: 0);

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
```

**Note for the implementer:** `TestProfileBuilder` (Fase 2, `LEC.MailStore.Tests`) may only currently support one-`.dbx`-at-a-time or a fixed shape — check its actual current signature before assuming `TestProfileBuilder.WriteProfile((string, TestDbxBuilder)[])`/`CreateEmptyProfile()` exist verbatim as written above. Adapt these two test helper calls to whatever `TestProfileBuilder` actually offers today (it's used by the existing `DbxStoreTests.cs` — read a couple of its existing tests first), extending `TestProfileBuilder` itself only if genuinely necessary and only in the minimal way these two new tests need. Same "verify the real helper before assuming its shape" note as Task 4.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.MailStore.Tests --filter DbxStoreScavengeTests`
Expected: FAIL — `ScavengeStatus`/`IMailStore.ScavengeAsync`/etc. don't exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.MailStore/ScavengeStatus.cs
namespace LEC.MailStore;

public enum ScavengeState { NeverScanned, Scanned }

public sealed record ScavengeStatus(ScavengeState State, int RecoveredCount, bool IsVisible)
{
    public static readonly ScavengeStatus NotScanned = new(ScavengeState.NeverScanned, 0, false);
}
```

Modify `src/LEC.MailStore/IMailStore.cs`:

```csharp
using LEC.DbxFormat;

namespace LEC.MailStore;

/// <summary>
/// Core contract from ARQUITETURA.md §7.1. Extended in Fase 3 (varredura) with
/// scavenge-related members -- the Fase 2 plan's "don't add members" was scoped to
/// that plan, not a permanent freeze. See docs/superpowers/specs/2026-07-27-fase3-varredura-design.md §4.
/// </summary>
public interface IMailStore : IDisposable
{
    IReadOnlyList<StoreFolder> Folders { get; }

    IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, CancellationToken ct = default);

    Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct = default);

    StoreHealth Health { get; }

    /// <summary>
    /// This specific folder's own backing-file health (distinct from the aggregate
    /// <see cref="Health"/>, which only reflects Folders.dbx). Opens the folder if it
    /// isn't already open (same lazy-open behavior as EnumerateHeadersAsync); returns
    /// StoreHealth.Ok if the folder can't be opened at all (IsLoadable already covers
    /// that failure mode separately). This is the correct signal for "does this folder
    /// have an inconsistency scavenging might help with" -- a per-message Health.Partial
    /// only catches one specific corruption shape (an out-of-range attribute pointer on
    /// an already-indexed message); an orphaned block coexisting with perfectly healthy
    /// indexed messages -- the scenario ARQUITETURA.md §7.5 exists for -- never sets any
    /// message's Health to Partial, but does set the file's own DbxEmailFile.Health.
    /// </summary>
    StoreHealth GetFolderHealth(StoreFolder folder);

    /// <summary>Current scavenge status for a folder -- reads the cache, runs nothing.</summary>
    ScavengeStatus GetScavengeStatus(StoreFolder folder);

    /// <summary>
    /// Runs the scavenge scan for a folder and persists the result. Never automatic --
    /// only ever called in direct response to an explicit user action (ARQUITETURA.md §7.4).
    /// </summary>
    Task<ScavengeStatus> ScavengeAsync(StoreFolder folder, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Toggles whether a folder's already-scavenged items appear in EnumerateHeadersAsync.</summary>
    void SetScavengeVisibility(StoreFolder folder, bool visible);
}
```

Modify `src/LEC.MailStore/DbxStore.cs`. Add `using Microsoft.Data.Sqlite;` to its existing usings (needed for the `SqliteException` catch filter in `OpenScavengeCacheWithFallback` below).

```csharp
// Add fields, alongside the existing ones:
    private readonly ScavengeCache _scavengeCache;
    private readonly Dictionary<int, ScavengeStatus> _scavengeStatusByFolder = new();
    private readonly Dictionary<int, IReadOnlyList<CachedRecoveredItem>> _recoveredItemsByFolder = new();

// Update the private constructor's parameter list and body to accept + store scavengeCache
// (add it as the last constructor parameter, wire it into the field above).

// Update Open to accept the new optional parameter:
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
                ParentId = f.ParentId == -1 ? null : f.ParentId,
                Name = f.Name ?? string.Empty,
                SpecialFolderKind = ToSpecialFolderKind(f.SpecialFolderType),
                MessageCount = f.MessageCount,
                IsLoadable = isLoadable,
            });
            backingFileNames[f.FolderId] = fileName;
        }

        var health = folderFile.Health;
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

(Update the private constructor's signature and body accordingly -- add the `ScavengeCache scavengeCache` parameter and assign `_scavengeCache = scavengeCache;`.)

Add the four new members and small private helpers. `EnsureFolderOpen` factors out the lazy-open-on-first-use logic that `EnumerateHeadersAsync` already had inline (Fase 2) -- both it and the two new scavenge methods below need the exact same "already open? use it; not open? try to open it; can't open? treat as not loadable here" sequence, so it's extracted rather than repeated a third time:

```csharp
    private DbxEmailFile? EnsureFolderOpen(StoreFolder folder)
    {
        if (_openFolders.TryGetValue(folder.Id, out var emailFile))
            return emailFile;

        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
            return null;

        var opened = TryOpenFolderFile(fileName);
        if (opened is null)
            return null;

        _openFolders[folder.Id] = opened;
        return opened;
    }

    public StoreHealth GetFolderHealth(StoreFolder folder) => EnsureFolderOpen(folder)?.Health ?? StoreHealth.Ok;

    public ScavengeStatus GetScavengeStatus(StoreFolder folder)
    {
        EnsureScavengeLoaded(folder);
        return _scavengeStatusByFolder.TryGetValue(folder.Id, out var status) ? status : ScavengeStatus.NotScanned;
    }

    public async Task<ScavengeStatus> ScavengeAsync(StoreFolder folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
            return ScavengeStatus.NotScanned;

        var emailFile = EnsureFolderOpen(folder);
        if (emailFile is null)
            return ScavengeStatus.NotScanned;

        var scavenged = await Task.Run(() => emailFile.Scavenge(progress, ct), ct);
        var classified = scavenged.Select(ScavengedMessageClassifier.Classify).ToList();

        _scavengeCache.SaveScan(_profileDirectory, fileName, classified);
        _recoveredItemsByFolder[folder.Id] = classified;

        var status = new ScavengeStatus(ScavengeState.Scanned, classified.Count, IsVisible: false);
        _scavengeStatusByFolder[folder.Id] = status;
        return status;
    }

    public void SetScavengeVisibility(StoreFolder folder, bool visible)
    {
        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
            return;

        _scavengeCache.SetVisibility(_profileDirectory, fileName, visible);

        if (_scavengeStatusByFolder.TryGetValue(folder.Id, out var status))
            _scavengeStatusByFolder[folder.Id] = status with { IsVisible = visible };
    }

    private void EnsureScavengeLoaded(StoreFolder folder)
    {
        if (_scavengeStatusByFolder.ContainsKey(folder.Id))
            return;

        if (!_backingFileNames.TryGetValue(folder.Id, out var fileName) || fileName is null)
        {
            _scavengeStatusByFolder[folder.Id] = ScavengeStatus.NotScanned;
            return;
        }

        var cached = _scavengeCache.GetScan(_profileDirectory, fileName);
        if (cached is null)
        {
            _scavengeStatusByFolder[folder.Id] = ScavengeStatus.NotScanned;
            return;
        }

        _recoveredItemsByFolder[folder.Id] = cached.Items;
        _scavengeStatusByFolder[folder.Id] = new ScavengeStatus(ScavengeState.Scanned, cached.Items.Count, cached.IsVisible);
    }
```

Add `using System.Linq;` to `DbxStore.cs` if `.Select`/`.ToList` don't resolve.

Modify `Dispose()` to also dispose the cache:

```csharp
    public void Dispose()
    {
        foreach (var file in _openFolders.Values)
            file.Dispose();
        _openFolders.Clear();
        _scavengeCache.Dispose();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.MailStore.Tests --filter DbxStoreScavengeTests`
Expected: PASS (5 tests)

Run: `dotnet test tests/LEC.MailStore.Tests`
Expected: all tests pass, including the pre-existing `DbxStoreTests.cs` (its `DbxStore.Open(profileDir)` single-argument call sites are unaffected by the new optional parameter).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/ScavengeStatus.cs src/LEC.MailStore/IMailStore.cs src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreScavengeTests.cs
git commit -m "Extend IMailStore with per-folder health and scavenge operations, wire DbxStore to ScavengeCache"
```

---

### Task 8: `MessageListViewModel` — date-based sorting

**Files:**
- Modify: `src/LEC.App/ViewModels/MessageListViewModel.cs`
- Test: `tests/LEC.App.Tests/MessageListViewModelTests.cs`

**Interfaces:**
- Consumes: `MessageHeader.Date` (existing).
- Produces: `MessageListViewModel.Messages` is now populated newest-first by `Date`, with a `null` `Date` sorting last. Consumed by Task 11 (recovered items rely on this to land in the right chronological position).

This is independent of scavenging — a real gap found while planning Task 9 (recovered items are supposed to "enter the normal date order," which doesn't exist yet). Landing it as its own task keeps it independently reviewable.

- [ ] **Step 1: Write the failing test**

Append to `tests/LEC.App.Tests/MessageListViewModelTests.cs`:

```csharp
    [Fact]
    public async Task SortsMessagesNewestFirstByDate()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true };
        var older = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Antigo", Date = new DateTime(2001, 1, 1) };
        var newer = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Novo", Date = new DateTime(2005, 1, 1) };
        var noDate = new MessageHeader { Id = new MessageRef(1, 3), Subject = "SemData", Date = null };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { older, newer, noDate } }); // deliberately out of order

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        Assert.Equal(new[] { "Novo", "Antigo", "SemData" }, vm.Messages.Select(m => m.Subject));
    }
```

Add `using System.Linq;` to the test file if not already present.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.App.Tests --filter MessageListViewModelTests`
Expected: FAIL — messages currently come back in insertion order (`Antigo`, `Novo`, `SemData`), not sorted.

- [ ] **Step 3: Implement**

Modify `src/LEC.App/ViewModels/MessageListViewModel.cs`:

```csharp
    public async Task LoadAsync(StoreFolder folder, CancellationToken ct = default)
    {
        Messages.Clear();
        StatusText = folder.IsLoadable ? null : "Não foi possível carregar esta pasta.";

        var loaded = new List<MessageHeader>();
        await foreach (var header in _store.EnumerateHeadersAsync(folder, ct))
            loaded.Add(header);

        foreach (var header in loaded.OrderByDescending(h => h.Date ?? DateTime.MinValue))
            Messages.Add(header);
    }
```

Add `using System.Linq;` to `MessageListViewModel.cs` if `.OrderByDescending` doesn't resolve.

**Note:** this changes `LoadAsync` from incremental (each header appended to `Messages` as `EnumerateHeadersAsync` yields it) to buffer-then-populate (everything collected first, sorted, then added). Sorting by date requires seeing every item before any ordering decision is possible -- there's no way around this trade-off for a "sort" feature. Fase 2's own performance testing (`BCP Espelho`, 7,498 messages) showed the full enumeration completes in well under 200ms, so the loss of incremental/progressive population is not perceptible in practice.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LEC.App.Tests --filter MessageListViewModelTests`
Expected: PASS (4 tests -- 3 existing + this one)

Run: `dotnet test tests/LEC.App.Tests`
Expected: all tests pass, no regressions (the 3 pre-existing `MessageListViewModelTests` don't depend on ordering, only on membership/count, so they're unaffected by this change -- double check this assumption by reading them before running, and if any does implicitly depend on insertion order, that's a real behavior change worth calling out explicitly in your report rather than silently "fixing" the test's expectation).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/MessageListViewModel.cs tests/LEC.App.Tests/MessageListViewModelTests.cs
git commit -m "Sort MessageListViewModel newest-first by date"
```

---

### Task 9: `MessageHeader.RecoveredKind` + `DbxStore.EnumerateHeadersAsync` mixes in recovered items

**Files:**
- Modify: `src/LEC.MailStore/MessageHeader.cs`
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Test: `tests/LEC.MailStore.Tests/DbxStoreScavengeTests.cs` (append)

**Interfaces:**
- Consumes: `ScavengeStatus`/`CachedRecoveredItem` (Task 7), `MessageListViewModel`'s sort (Task 8, at the `LEC.App` layer -- this task only needs to yield the items; sorting them into the right position is already handled by whoever consumes `EnumerateHeadersAsync`).
- Produces: `MessageHeader.RecoveredKind` (nullable `RecoveredMessageKind`; `null` = a normal, index-tree-backed message). `EnumerateHeadersAsync` yields recovered items (with a `MessageRef` built from the chain offset) after normal ones, only when that folder's scavenge visibility is on. `OpenMessageAsync` resolves a recovered item's `MessageRef` back to its raw bytes via `DbxEmailFile.ReadRecoveredMessageRaw`.

- [ ] **Step 1: Write the failing test**

Append to `tests/LEC.MailStore.Tests/DbxStoreScavengeTests.cs`:

```csharp
    [Fact]
    public async Task RecoveredItemsAppearInEnumerateHeadersOnlyWhenVisible()
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
        localFolders.SetInt32At(DbxFormat.HeaderItemCountOffset, 1);
        localFolders.SetInt32At(DbxFormat.HeaderIndexPointerOffset, foldersRoot);

        var inbox = new TestDbxBuilder();
        inbox.WriteHeader(DbxFileType.Email);
        inbox.SetInt32At(DbxFormat.HeaderItemCountOffset, 0);
        inbox.SetInt32At(DbxFormat.HeaderIndexPointerOffset, 0);
        byte[] payload = "From: c@d.com\r\nSubject: Orfa\r\n\r\nCorpo"u8.ToArray();
        int orphanOffset = inbox.WriteMessageBlock(payload, next: 0);

        string profileDir = TestProfileBuilder.WriteProfile(("Folders.dbx", localFolders), ("Inbox.dbx", inbox));
        string cachePath = Path.Combine(Path.GetTempPath(), $"lec-test-cache-{Guid.NewGuid():N}.db");

        using var store = DbxStore.Open(profileDir, cachePath);
        var folder = store.Folders.Single(f => f.Name == "Caixa de entrada");
        await store.ScavengeAsync(folder);

        var beforeShown = await store.EnumerateHeadersAsync(folder).ToListAsync();
        Assert.Empty(beforeShown); // scavenged but not shown yet

        store.SetScavengeVisibility(folder, visible: true);

        var afterShown = await store.EnumerateHeadersAsync(folder).ToListAsync();
        var recovered = Assert.Single(afterShown);
        Assert.Equal(RecoveredMessageKind.Fragment, recovered.RecoveredKind); // no "Date" header in the payload above -> unparseable enough to be a Fragment in this repro, OR Recovered if MimeKit still parses it -- adjust this assertion to whatever ScavengedMessageClassifier actually returns for this exact payload once you run it; the point of this test is that RecoveredKind is non-null and the item round-trips via OpenMessageAsync below, not the specific enum value.
        Assert.Equal("Orfa", recovered.Subject);

        using var stream = await store.OpenMessageAsync(recovered.Id);
        using var reader = new StreamReader(stream);
        string raw = await reader.ReadToEndAsync();
        Assert.Contains("Corpo", raw);
    }
```

(`ToListAsync()` on `IAsyncEnumerable<T>` needs `System.Linq.Async` or a hand-rolled helper -- check whether the test projects already have a way to materialize an `IAsyncEnumerable` into a list, since Fase 2's own tests consume `EnumerateHeadersAsync` via `await foreach` rather than `ToListAsync`. If no such helper/package exists yet, write the test using `await foreach` and a local `List<MessageHeader>` instead of `ToListAsync()` -- don't add a new package dependency just for this.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.MailStore.Tests --filter RecoveredItemsAppearInEnumerateHeadersOnlyWhenVisible`
Expected: FAIL — `MessageHeader.RecoveredKind` doesn't exist yet, and recovered items aren't yielded at all.

- [ ] **Step 3: Implement**

Modify `src/LEC.MailStore/MessageHeader.cs` — add one property:

```csharp
    public RecoveredMessageKind? RecoveredKind { get; init; } // null = normal, index-backed message
```

Modify `src/LEC.MailStore/DbxStore.cs`'s `EnumerateHeadersAsync` to yield recovered items after normal ones when visible, and extend `OpenMessageAsync` to resolve them:

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

        EnsureScavengeLoaded(folder);
        if (_scavengeStatusByFolder.TryGetValue(folder.Id, out var status) && status.IsVisible
            && _recoveredItemsByFolder.TryGetValue(folder.Id, out var recoveredItems))
        {
            if (!_recoveredByFolder.TryGetValue(folder.Id, out var offsetByRef))
            {
                offsetByRef = new Dictionary<MessageRef, int>();
                _recoveredByFolder[folder.Id] = offsetByRef;
            }

            foreach (var item in recoveredItems)
            {
                ct.ThrowIfCancellationRequested();
                // ChainOffset re-purposes MessageRef's IndexNodeOffset slot as an opaque
                // "wherever this is in the file" identifier -- a recovered item has no real
                // index node (that's exactly what the tree lost), but the two are always in
                // disjoint byte regions of the file (index/info-node region vs. data-block
                // region) so collisions are not a practical concern.
                var reference = new MessageRef(folder.Id, item.ChainOffset);
                offsetByRef[reference] = item.ChainOffset;

                yield return new MessageHeader
                {
                    Id = reference,
                    Subject = item.Subject,
                    SenderName = item.SenderName,
                    SenderAddress = item.SenderAddress,
                    Date = item.DateUtc,
                    Size = (uint)Math.Max(0, item.ByteLength),
                    MessageId = null,
                    Health = StoreHealth.Partial,
                    RecoveredKind = item.Kind,
                };
            }
        }

        await Task.CompletedTask;
    }
```

Add the new dictionary field alongside the others:

```csharp
    private readonly Dictionary<int, Dictionary<MessageRef, int>> _recoveredByFolder = new();
```

Modify `OpenMessageAsync` to check the recovered registry when the normal one doesn't have the reference:

```csharp
    public Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        if (_messagesByFolder.TryGetValue(id.FolderId, out var infoByRef) &&
            infoByRef.TryGetValue(id, out var info) &&
            _openFolders.TryGetValue(id.FolderId, out var normalEmailFile))
        {
            byte[] normalRaw = normalEmailFile.ReadMessageRaw(info, out _);
            return Task.FromResult<Stream>(new MemoryStream(normalRaw));
        }

        if (_recoveredByFolder.TryGetValue(id.FolderId, out var offsetByRef) &&
            offsetByRef.TryGetValue(id, out var chainOffset) &&
            _openFolders.TryGetValue(id.FolderId, out var recoveredEmailFile))
        {
            byte[] recoveredRaw = recoveredEmailFile.ReadRecoveredMessageRaw(chainOffset, out _);
            return Task.FromResult<Stream>(new MemoryStream(recoveredRaw));
        }

        throw new InvalidOperationException(
            $"Message {id} was not found. EnumerateHeadersAsync must be called for its folder before OpenMessageAsync.");
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LEC.MailStore.Tests --filter DbxStoreScavengeTests`
Expected: PASS (4 tests). If the `RecoveredKind` assertion in the test doesn't match what `ScavengedMessageClassifier` actually produces for that exact payload, fix the assertion to match the real, correct classification (don't change the classifier to fit a wrong guess in the test).

Run: `dotnet test tests/LEC.MailStore.Tests`
Expected: all tests pass, no regressions.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/MessageHeader.cs src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreScavengeTests.cs
git commit -m "Mix recovered items into EnumerateHeadersAsync when a folder's scavenge results are visible"
```

---

### Task 10: `LEC.App` — contextual scavenge trigger

**Files:**
- Modify: `src/LEC.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/LEC.App/MainWindow.xaml`
- Modify: `src/LEC.App/MainWindow.xaml.cs`
- Test: `tests/LEC.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `IMailStore.GetFolderHealth`/`GetScavengeStatus`/`ScavengeAsync` (Task 7).
- Produces: `MainWindowViewModel { ScavengeStatus? CurrentFolderScavengeStatus, Task RunScavengeAsync(FolderNodeViewModel node, IProgress<double>? progress = null, CancellationToken ct = default) }`. UI: a button visible only when the selected folder's `GetFolderHealth(...) == StoreHealth.Partial` and its scavenge status is `NeverScanned`, plus a Cancel button while a scan is in flight. Consumed by Task 11.

- [ ] **Step 1: Write the failing test**

Append to `tests/LEC.App.Tests/MainWindowViewModelTests.cs`:

```csharp
    [Fact]
    public async Task RunScavengeAsyncUpdatesCurrentFolderScavengeStatus()
    {
        var folder = new StoreFolder { Id = 1, Name = "X", IsLoadable = true };
        var fakeStore = new FakeMailStore(folders: new[] { folder });
        // FakeMailStore's GetScavengeStatus/ScavengeAsync need to exist for this test to
        // compile -- Task 8 of Fase 2 (LEC.App WPF scaffolding) originally wrote
        // FakeMailStore; it now needs the same 3 IMailStore members Task 7 of this plan
        // added to the real interface. Extend FakeMailStore minimally: GetScavengeStatus
        // returns a settable field defaulting to ScavengeStatus.NotScanned, ScavengeAsync
        // sets and returns a configurable result, SetScavengeVisibility updates it too.

        var vm = new MainWindowViewModel();
        // MainWindowViewModel.OpenProfile takes a real profile *directory path*, not an
        // IMailStore -- there's no seam to inject FakeMailStore here at the OpenProfile
        // level (same constraint every existing MainWindowViewModel test already works
        // within). Test RunScavengeAsync's status-update behavior against FolderTree/
        // MessageList's already-established pattern instead: call SelectFolderAsync first
        // (as existing tests do), then assert CurrentFolderScavengeStatus starts null when
        // no profile is open, matching the existing no-profile-open test's shape.
        Assert.Null(vm.CurrentFolderScavengeStatus);
    }
```

**Note for the implementer:** this is the one test in this task where the plan is deliberately less prescriptive, because `MainWindowViewModel`'s existing tests (Task 13, Fase 2) only exercise the no-profile-open path -- there's no established seam for injecting a `FakeMailStore` into `MainWindowViewModel` directly (it always goes through `DbxStore.Open` on a real path). Write this test to cover what's actually testable without a real profile (the null/no-op state), and add a second test using the real-fixture pattern (`FindRealFixtureDirectory`, same as Task 7 of Fase 2) if you want stronger coverage of the scavenge-status-after-`OpenProfile` path -- check whether any folder in `BCP/` currently has `Health = Partial` first (this plan's Task 12 does that verification for real; if none exists at this point in the sequence, a synthetic-profile-based test through `MainWindowViewModel` isn't possible yet either, and the `DbxStoreScavengeTests` from Task 7/9 already cover the underlying `DbxStore` behavior directly -- don't force a redundant test here just to hit a number.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.App.Tests --filter MainWindowViewModelTests`
Expected: FAIL — `CurrentFolderScavengeStatus` doesn't exist yet.

- [ ] **Step 3: Implement**

Modify `src/LEC.App/ViewModels/MainWindowViewModel.cs` — add a property and a method:

```csharp
    [ObservableProperty]
    private ScavengeStatus? _currentFolderScavengeStatus;

    public async Task RunScavengeAsync(FolderNodeViewModel node, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (Store is null)
            return;

        CurrentFolderScavengeStatus = await Store.ScavengeAsync(node.Folder, progress, ct);
    }
```

Update `SelectFolderAsync` to also refresh `CurrentFolderScavengeStatus` for the newly selected folder:

```csharp
    public async Task SelectFolderAsync(FolderNodeViewModel node, CancellationToken ct = default)
    {
        CurrentFolderScavengeStatus = Store?.GetScavengeStatus(node.Folder);

        if (MessageList is not null)
            await MessageList.LoadAsync(node.Folder, ct);
    }
```

Modify `tests/LEC.App.Tests/FakeMailStore.cs` — add the 4 new `IMailStore` members (minimal, settable-for-tests shape):

```csharp
    public StoreHealth FolderHealthToReturn { get; set; } = StoreHealth.Ok;
    public ScavengeStatus ScavengeStatusToReturn { get; set; } = ScavengeStatus.NotScanned;
    private bool _visibility;

    public StoreHealth GetFolderHealth(StoreFolder folder) => FolderHealthToReturn;

    public ScavengeStatus GetScavengeStatus(StoreFolder folder) => ScavengeStatusToReturn with { IsVisible = _visibility };

    public Task<ScavengeStatus> ScavengeAsync(StoreFolder folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(1.0);
        return Task.FromResult(ScavengeStatusToReturn);
    }

    public void SetScavengeVisibility(StoreFolder folder, bool visible) => _visibility = visible;
```

**On per-folder health:** use `IMailStore.GetFolderHealth` (Task 7) -- it's exactly the signal this button needs (the folder file's own `DbxEmailFile.Health`, which catches an orphaned block coexisting with healthy messages, not just a per-message `Health.Partial`), and it's already lazy (opens the folder only if not already open, no eager work for unselected folders).

In `src/LEC.App/MainWindow.xaml`, add a contextual trigger + cancel button pair near the message list (inside the reading-pane's outer `Grid`, above the `ListView` row -- adjust exact placement to fit the existing layout from Fase 2 Task 14/16, e.g. as a `DockPanel.Dock="Top"` sibling of the existing `!`-column `ListView`):

```xml
                <StackPanel Orientation="Horizontal" Margin="4">
                    <Button x:Name="ScavengeButton" Content="Procurar mensagens recuperáveis nesta pasta"
                            Click="ScavengeButton_Click" Visibility="Collapsed"/>
                    <Button x:Name="CancelScavengeButton" Content="Cancelar" Margin="8,0,0,0"
                            Click="CancelScavengeButton_Click" Visibility="Collapsed"/>
                </StackPanel>
```

In `src/LEC.App/MainWindow.xaml.cs`, add a field for the in-flight scan's cancellation, update `FolderTreeView_SelectedItemChanged` to also refresh the button's visibility, and add the click handlers:

```csharp
// Add near the top of the class, alongside the other fields:
    private CancellationTokenSource? _scavengeCts;
```

```csharp
    private async void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        try
        {
            if (e.NewValue is FolderNodeViewModel node)
            {
                await _viewModel.SelectFolderAsync(node);
                UpdateScavengeButtonVisibility(node);
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível carregar esta pasta: {ex.Message}";
        }
    }

    private void UpdateScavengeButtonVisibility(FolderNodeViewModel node)
    {
        bool showButton = node.Folder.IsLoadable
            && _viewModel.MessageList is not null // a profile is open
            && _viewModel.CurrentFolderScavengeStatus?.State != ScavengeState.Scanned
            && HasPartialHealth(node);

        ScavengeButton.Visibility = showButton ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool HasPartialHealth(FolderNodeViewModel node) =>
        _viewModel.Store?.GetFolderHealth(node.Folder) == StoreHealth.Partial;

    private async void ScavengeButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTreeView.SelectedItem is not FolderNodeViewModel node)
            return;

        _scavengeCts = new CancellationTokenSource();
        try
        {
            ScavengeButton.Visibility = Visibility.Collapsed;
            CancelScavengeButton.Visibility = Visibility.Visible;

            var progress = new Progress<double>(p => _viewModel.StatusText = $"Varrendo pasta... {p:P0}");
            await _viewModel.RunScavengeAsync(node, progress, _scavengeCts.Token);

            _viewModel.StatusText = null;
            UpdateScavengeButtonVisibility(node);
        }
        catch (OperationCanceledException)
        {
            _viewModel.StatusText = "Varredura cancelada.";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível varrer esta pasta: {ex.Message}";
        }
        finally
        {
            CancelScavengeButton.Visibility = Visibility.Collapsed;
            _scavengeCts?.Dispose();
            _scavengeCts = null;
        }
    }

    private void CancelScavengeButton_Click(object sender, RoutedEventArgs e)
    {
        _scavengeCts?.Cancel();
    }
```

Add `using LEC.DbxFormat;` to `MainWindow.xaml.cs` for `StoreHealth` if not already present.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter MainWindowViewModelTests`
Expected: PASS

Run: `dotnet build`
Expected: `Build succeeded.`

Run: `dotnet run --project src/LEC.App`, confirm the window still launches and closes cleanly (same Layer-1 check as every prior UI task in this project).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/MainWindowViewModel.cs src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs tests/LEC.App.Tests/FakeMailStore.cs tests/LEC.App.Tests/MainWindowViewModelTests.cs
git commit -m "Add contextual scavenge trigger, shown only for folders with Health.Partial messages"
```

---

### Task 11: `LEC.App` — recoverable-messages bar + visual styling for recovered rows

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml`
- Modify: `src/LEC.App/MainWindow.xaml.cs`
- Create: `src/LEC.App/Converters/RecoveredKindConverters.cs`
- Test: manual only (WPF View, per Global Constraints — same as every other XAML-only task in this project)

**Interfaces:**
- Consumes: `MainWindowViewModel.CurrentFolderScavengeStatus` (Task 10), `MessageHeader.RecoveredKind` (Task 9).
- Produces: a bar above the message list showing "N mensagens recuperáveis — mostrar/esconder" whenever `CurrentFolderScavengeStatus.State == Scanned`, toggling `IMailStore.SetScavengeVisibility` + re-loading the message list. Recovered rows render in a muted tone with a "Recuperada"/"Fragmento" tag, reusing the pattern Fase 2 Task 16 established for the `Health` `!` column.

- [ ] **Step 1: Implement the converters**

```csharp
// src/LEC.App/Converters/RecoveredKindConverters.cs
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LEC.MailStore;

namespace LEC.App.Converters;

public sealed class RecoveredKindToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RecoveredMessageKind ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RecoveredKindToLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RecoveredMessageKind.Recovered => "Recuperada",
        RecoveredMessageKind.Fragment => "Fragmento",
        _ => null,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RecoveredKindToRowBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RecoveredMessageKind ? Brushes.Gray : Brushes.Black;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 2: Add the converters and the bar to `MainWindow.xaml`**

Add to `Window.Resources`, alongside the existing converters from Fase 2:

```xml
        <conv:RecoveredKindToVisibilityConverter x:Key="RecoveredKindToVisibilityConverter"/>
        <conv:RecoveredKindToLabelConverter x:Key="RecoveredKindToLabelConverter"/>
        <conv:RecoveredKindToRowBrushConverter x:Key="RecoveredKindToRowBrushConverter"/>
```

Add the bar above the message-list `ListView` (`DockPanel.Dock="Top"`, matching the same pattern Task 15 (Fase 2) used for `ImagesBlockedBar`, but this one's visibility is driven by the ViewModel, not code-behind toggling):

```xml
                    <Border x:Name="RecoverableBar" DockPanel.Dock="Top" Background="#E8F0FE" Padding="6"
                            Visibility="Collapsed">
                        <DockPanel>
                            <Button x:Name="ToggleRecoverableButton" DockPanel.Dock="Right"
                                    Click="ToggleRecoverableButton_Click"/>
                            <TextBlock x:Name="RecoverableCountText" VerticalAlignment="Center"/>
                        </DockPanel>
                    </Border>
```

Add a "!" style tag column to the message list's existing `GridView` (alongside the `Health` column from Fase 2 Task 16), and mute the whole row's foreground when recovered — the simplest correct way to affect the whole row (not just one cell) in a `ListView`/`GridView` is a `Style` targeting `ListViewItem`:

```xml
                <ListView.ItemContainerStyle>
                    <Style TargetType="ListViewItem">
                        <Setter Property="Foreground" Value="{Binding RecoveredKind, Converter={StaticResource RecoveredKindToRowBrushConverter}}"/>
                    </Style>
                </ListView.ItemContainerStyle>
```

Add the tag column to the `GridView` (after the existing `!` Health column from Task 16):

```xml
                            <GridViewColumn Header="Origem" Width="80">
                                <GridViewColumn.CellTemplate>
                                    <DataTemplate>
                                        <TextBlock Text="{Binding RecoveredKind, Converter={StaticResource RecoveredKindToLabelConverter}}"
                                                   Visibility="{Binding RecoveredKind, Converter={StaticResource RecoveredKindToVisibilityConverter}}"
                                                   FontStyle="Italic"/>
                                    </DataTemplate>
                                </GridViewColumn.CellTemplate>
                            </GridViewColumn>
```

- [ ] **Step 3: Wire it up in `MainWindow.xaml.cs`**

Update `UpdateScavengeButtonVisibility` (Task 10) to also refresh the recoverable bar, and add the toggle handler:

```csharp
    private void UpdateScavengeButtonVisibility(FolderNodeViewModel node)
    {
        var status = _viewModel.CurrentFolderScavengeStatus;

        bool showTriggerButton = status is null or { State: ScavengeState.NeverScanned }
            && HasPartialHealth(node);
        ScavengeButton.Visibility = showTriggerButton ? Visibility.Visible : Visibility.Collapsed;

        bool showBar = status is { State: ScavengeState.Scanned, RecoveredCount: > 0 };
        RecoverableBar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        if (showBar)
        {
            RecoverableCountText.Text = $"{status!.RecoveredCount} mensagens recuperáveis neste arquivo";
            ToggleRecoverableButton.Content = status.IsVisible ? "Esconder" : "Mostrar";
        }
    }

    private async void ToggleRecoverableButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTreeView.SelectedItem is not FolderNodeViewModel node || _viewModel.Store is null)
            return;

        try
        {
            bool newVisibility = !(_viewModel.CurrentFolderScavengeStatus?.IsVisible ?? false);
            _viewModel.Store.SetScavengeVisibility(node.Folder, newVisibility);
            await _viewModel.SelectFolderAsync(node); // reload the list + refresh status/button state
            UpdateScavengeButtonVisibility(node);
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível alternar a exibição de recuperáveis: {ex.Message}";
        }
    }
```

Also update `ScavengeButton_Click` (Task 10) to call the same combined refresh instead of just `UpdateScavengeButtonVisibility` alone (no functional change needed there beyond what Task 10 already wrote, since `UpdateScavengeButtonVisibility` now also handles the bar).

- [ ] **Step 4: Verify manually**

Run: `dotnet build` — expect success.

Run: `dotnet test` — expect the full suite (all three projects) to still pass, no regressions from the XAML/code-behind changes (there are no new automated tests in this task by design).

Run: `dotnet run --project src/LEC.App`, launch and close cleanly (Layer 1 check). Deeper interactive verification (does a real `Health.Partial` folder exist in `BCP/`, does clicking the trigger actually find something, does the bar toggle correctly) is Task 12's job, not this one's — this task's manual step is just confirming the XAML compiles and the window still opens with no `XamlParseException`.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs src/LEC.App/Converters/RecoveredKindConverters.cs
git commit -m "Add recoverable-messages bar and muted-row styling for scavenged items"
```

---

### Task 12: Full verification pass against real `BCP/` files

**Files:** none (verification only — same shape as Fase 2's Task 17).

- [ ] **Step 1: Run the full automated test suite**

Run: `dotnet test`
Expected: every test across all three projects passes.

- [ ] **Step 2: Verify whether any real `BCP/` file actually has recoverable content**

Per the design doc's §9 caveat: before claiming end-to-end coverage against real data, check whether `BCP/` genuinely has orphaned message blocks anywhere. Using the same harness-based approach established throughout Fase 2 (temporary, uncommitted xUnit test or console harness, deleted before finishing — driving `DbxStore`/`DbxEmailFile.Scavenge` directly, no live WPF window needed):

1. Open the real `BCP/` profile.
2. For every loadable folder, call `DbxEmailFile.Scavenge()` directly (or `DbxStore.ScavengeAsync` per folder) and record how many `ScavengedMessage`s each file yields.
3. Report the findings plainly. If `BCP/` has zero recoverable content anywhere (a real possibility — Fase 1's Task 14 found all real files parse with `Health: Ok`, meaning the index tree is intact everywhere and there is nothing for the index to be missing), that's a legitimate, useful result: it means the synthetic tests (Tasks 2-9) are the primary correctness evidence for the scavenge engine itself, and the "verify contextual UI wiring end-to-end" step below can only be exercised by temporarily constructing a profile with a deliberately un-indexed block (a synthetic profile is fine for this — it doesn't need to be `BCP/`).
4. Additionally, since every folder in `BCP/` parses with `Health: Ok` (confirmed in Fase 1's Task 14 and unchanged since), no folder will show the scavenge trigger button by design (Task 10's `Health = Partial` gate) — so the "contextual trigger only appears for Health.Partial folders" behavior also cannot be observed against `BCP/` as it stands today. Verify this is genuinely the current state (don't assume — re-run the check) and report it as an accepted, explained gap rather than silently skipping it.

- [ ] **Step 3: Verify the scavenge engine against a large real file for performance**

Even with nothing to find, `DbxScavenger.FindCandidateBlockStarts`'s linear scan cost matters — run it against the largest real file (`BCP Espelho.dbx`, ~1.3 GB per Fase 1's Task 14 findings) via the same harness, with a `Stopwatch`, and report the elapsed time. This is the real-world performance the "roda em background, cancelável" UI design depends on being viable at all.

- [ ] **Step 4: Interactive walkthrough (harness-based, same safety pattern as Fase 2)**

Since no real `BCP/` folder currently has `Health.Partial`, do this against a small synthetic profile (reuse `TestProfileBuilder` + `TestDbxBuilder` to write one to a temp directory) with a header claiming one more item than the index tree actually holds (the same shape as Task 7's `GetFolderHealthReflectsTheFolderFilesOwnHealthNotJustPerMessageHealth` test — this is what makes `DbxEmailFile.Health` come back `Partial` even though every real message in it is perfectly healthy) plus one deliberately orphaned block written but not indexed, opened via `MainWindowViewModel.OpenProfile` directly (no live window needed, same harness pattern Fase 2 Tasks 14/16/17 established). Verify: the trigger button appears (confirming `GetFolderHealth`, wired in Task 7/10, genuinely drives visibility against a real `OpenProfile` flow, not just the unit-level `DbxStoreScavengeTests`); clicking it (simulated by calling `RunScavengeAsync` directly through the harness) runs the scan, updates the status, the bar's backing state shows the right count, toggling shows/hides the recovered item in `MessageList.Messages`, and `OpenMessageAsync` on the recovered item's `MessageRef` returns the right raw bytes.

- [ ] **Step 5: Final commit**

If the walkthrough finds and fixes a real bug (matching Fase 2's Task 17 precedent), commit that fix with a specific message describing it, not a generic wrap-up message. If everything passes cleanly with no code changes needed, do not create an empty/marker commit — report that plainly instead.

```bash
git add -A
git commit -m "<specific description of whatever was actually fixed, if anything>"
```
