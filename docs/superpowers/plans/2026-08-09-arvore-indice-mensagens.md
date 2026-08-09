# Árvore de índice multi-nível para mensagens .dbx — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the 255-message-per-folder cap in `DbxEmailFileWriter.Rebuild` by writing a real multi-level index tree instead of a single flat node — no fixed replacement limit, no reader or on-disk-format changes.

**Architecture:** `DbxEmailFileWriter.Rebuild` currently writes every message's info-node offset into one flat index node capped at 255 entries (`ptrCount` is a single byte). `DbxIndexTreeReader` (unchanged, already shipped) already knows how to follow a subtree hanging off any individual entry (`entryNextTable`/`entryIndexCount`) before continuing to the next one. This plan makes the writer use that existing capability: a new recursive `BuildIndexSubtree` takes up to 255 offsets as a node's own entries and, if more remain, splits the remainder into up to 255 evenly-sized groups — one per entry — recursing into each non-empty group before writing the node itself (children must exist on disk before the parent can reference their offset). `WriteIndexNode` gains a second parameter (per-entry child offsets) to wire this up.

**Tech Stack:** .NET 9, C#, xUnit.

## Global Constraints

- No change to `DbxIndexTreeReader`, `DbxFormat`, or any on-disk byte layout — this is a write-side-only change.
- No change to `DbxFolderFileWriter` (the analogous 255-folder cap) — out of scope, not the reported problem.
- No new fixed ceiling replaces the old one. The only limits left are `DbxSafetyLimits`, which exist for corrupted-file defense, not normal use.
- Reference: `docs/superpowers/specs/2026-08-09-arvore-indice-mensagens-design.md` (full design this plan implements).

---

## File Structure

```
src/LEC.DbxFormat/
  DbxEmailFileWriter.cs          MODIFIED — Task 1: class doc comment updated; Rebuild loses the
                                  byte.MaxValue guard and calls BuildIndexSubtree instead of
                                  WriteIndexNode directly; +BuildIndexSubtree, +SplitEvenly;
                                  WriteIndexNode gains a childOffsets parameter

tests/LEC.DbxFormat.Tests/
  DbxEmailFileWriterTests.cs     MODIFIED — Task 1: RebuildThrowsWhenGivenMoreThan255Messages
                                  removed, replaced with two round-trip tests
```

---

### Task 1: Multi-level index tree in `DbxEmailFileWriter`

**Files:**
- Modify: `src/LEC.DbxFormat/DbxEmailFileWriter.cs`
- Test: `tests/LEC.DbxFormat.Tests/DbxEmailFileWriterTests.cs`

**Interfaces:**
- Consumes: `DbxFormat.IndexNodeHeaderSize`/`IndexNodeNextTableOffset`/`IndexNodePtrCountOffset`/`IndexNodeIndexCountOffset`/`IndexNodeEntrySize`/`IndexEntryIndexPtrOffset`/`IndexEntryNextTableOffset`/`IndexEntryIndexCountOffset` (all existing constants, unchanged); `DbxEmailFile.Open`/`.Messages` (existing, used by the new tests to round-trip).
- Produces: `DbxEmailFileWriter.Rebuild(string path, IReadOnlyList<DbxMessageWriteEntry> messages)` keeps its existing signature — no caller anywhere in the codebase needs to change.

- [ ] **Step 1: Replace the old cap test with two round-trip tests**

In `tests/LEC.DbxFormat.Tests/DbxEmailFileWriterTests.cs`, delete the existing test:

```csharp
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
```

Replace it with:

```csharp
    [Fact]
    public void RebuildRoundTripsMoreThan255Messages()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lec-messages-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Messages.dbx");
        try
        {
            var entries = Enumerable.Range(0, 256)
                .Select(i => new DbxMessageWriteEntry(Encoding.UTF8.GetBytes($"m{i}"), $"S{i}", null, null, null, null))
                .ToArray();

            DbxEmailFileWriter.Rebuild(path, entries);

            using var file = DbxEmailFile.Open(path);
            Assert.Equal(256, file.Messages.Count);
            for (int i = 0; i < 256; i++)
                Assert.Contains(file.Messages, m => m.Subject == $"S{i}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RebuildRoundTripsEnoughMessagesToForceATwoLevelTree()
    {
        // 600 messages: the root node takes 255 as its own entries, leaving 345 to distribute
        // across up to 255 per-entry subtrees (some subtrees end up with 2 messages) --
        // exercises BuildIndexSubtree recursing more than one level deep, not just a single
        // extra node.
        string dir = Path.Combine(Path.GetTempPath(), $"lec-messages-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Messages.dbx");
        try
        {
            var entries = Enumerable.Range(0, 600)
                .Select(i => new DbxMessageWriteEntry(Encoding.UTF8.GetBytes($"m{i}"), $"S{i}", null, null, null, null))
                .ToArray();

            DbxEmailFileWriter.Rebuild(path, entries);

            using var file = DbxEmailFile.Open(path);
            Assert.Equal(600, file.Messages.Count);
            for (int i = 0; i < 600; i++)
                Assert.Contains(file.Messages, m => m.Subject == $"S{i}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter "RebuildRoundTripsMoreThan255Messages|RebuildRoundTripsEnoughMessagesToForceATwoLevelTree"`
Expected: FAIL — `Rebuild` still throws `ArgumentException` at the `byte.MaxValue` guard for both.

- [ ] **Step 3: Update the class doc comment**

In `src/LEC.DbxFormat/DbxEmailFileWriter.cs`, replace:

```csharp
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
```

with:

```csharp
/// <summary>
/// Writes a brand-new, empty message-store .dbx file — the backing file for a folder
/// that was just created and has no messages yet. itemCount = 0 means the reader
/// (DbxEmailFile.Open) never attempts to walk an index tree, so no tree needs to exist:
/// the header alone is a complete, valid, empty file.
///
/// Rebuild rewrites a message-store .dbx from scratch as a multi-level index tree when
/// there are more than 255 messages: each node holds up to 255 of its own entries
/// (ptrCount is one byte), and any entry can additionally point at a subtree of further
/// messages via that entry's own next-table pointer -- the same capability
/// DbxIndexTreeReader already understands for reading real, large Outlook Express
/// mailboxes. See BuildIndexSubtree. DbxFolderFileWriter keeps the single-flat-node
/// approach (folder counts never approach 255 in practice) and its low-level helpers are
/// deliberately duplicated here rather than shared (see the plan's File Structure note
/// for why).
/// </summary>
```

- [ ] **Step 4: Remove the message-count guard and switch `Rebuild` to the recursive builder**

In `src/LEC.DbxFormat/DbxEmailFileWriter.cs`, replace:

```csharp
    public static void Rebuild(string path, IReadOnlyList<DbxMessageWriteEntry> messages)
    {
        if (messages.Count > byte.MaxValue)
            throw new ArgumentException($"Message-store .dbx flat-node format holds at most {byte.MaxValue} messages; got {messages.Count}.", nameof(messages));

        foreach (var message in messages)
```

with:

```csharp
    public static void Rebuild(string path, IReadOnlyList<DbxMessageWriteEntry> messages)
    {
        foreach (var message in messages)
```

Then replace:

```csharp
        int rootOffset = WriteIndexNode(bytes, infoNodeOffsets);
```

with:

```csharp
        int rootOffset = BuildIndexSubtree(bytes, infoNodeOffsets);
```

- [ ] **Step 5: Add `BuildIndexSubtree` and `SplitEvenly`, and give `WriteIndexNode` a `childOffsets` parameter**

In `src/LEC.DbxFormat/DbxEmailFileWriter.cs`, replace the existing `WriteIndexNode` method:

```csharp
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
```

with:

```csharp
    // Writes the index-tree subtree holding every offset in `offsets`, recursing when there
    // are more than 255 (a single node's ptrCount is one byte, so 255 is the most entries any
    // one node can hold). Returns the offset of this subtree's root node. Children are always
    // written before their parent -- the parent's entries need to embed the child's offset,
    // which only exists once the child itself has been appended to `bytes`.
    private static int BuildIndexSubtree(List<byte> bytes, IReadOnlyList<int> offsets)
    {
        int ownCount = Math.Min(offsets.Count, byte.MaxValue);
        var ownOffsets = offsets.Take(ownCount).ToList();
        var remaining = offsets.Skip(ownCount).ToList();

        var childOffsets = new int?[ownCount];
        if (remaining.Count > 0)
        {
            // Spread the remainder across up to `ownCount` groups, one per entry, as evenly
            // as possible -- keeps the tree's fan-out at 255 per level (depth grows as
            // log_255(N)) instead of degenerating into a 255-messages-per-level linear chain
            // that would get needlessly deep for very large folders.
            var groups = SplitEvenly(remaining, ownCount);
            for (int i = 0; i < ownCount; i++)
            {
                if (groups[i].Count > 0)
                    childOffsets[i] = BuildIndexSubtree(bytes, groups[i]);
            }
        }

        return WriteIndexNode(bytes, ownOffsets, childOffsets);
    }

    // Splits `items` into `groupCount` lists whose sizes differ by at most one, preserving
    // order. Used by BuildIndexSubtree to spread leftover messages across sibling entries.
    private static List<List<int>> SplitEvenly(IReadOnlyList<int> items, int groupCount)
    {
        var result = new List<List<int>>(groupCount);
        int baseSize = items.Count / groupCount;
        int remainder = items.Count % groupCount;
        int pos = 0;
        for (int i = 0; i < groupCount; i++)
        {
            int size = baseSize + (i < remainder ? 1 : 0);
            result.Add(items.Skip(pos).Take(size).ToList());
            pos += size;
        }
        return result;
    }

    private static int WriteIndexNode(List<byte> bytes, IReadOnlyList<int> infoNodeOffsets, IReadOnlyList<int?> childOffsets)
    {
        int start = bytes.Count;

        AddZeros(bytes, 8);                                   // 0..7: unused by the reader
        AddZeros(bytes, 4);                                   // 8..11: nextTable (unused at this level — indexCount = 0)
        AddZeros(bytes, 5);                                   // 12..16: unused
        bytes.Add((byte)infoNodeOffsets.Count);                // 17: ptrCount
        AddZeros(bytes, 2);                                   // 18..19: unused
        bytes.AddRange(BitConverter.GetBytes(0));              // 20..23: indexCount = 0 (this node's own nextTable is unused)

        for (int i = 0; i < infoNodeOffsets.Count; i++)
        {
            bytes.AddRange(BitConverter.GetBytes(infoNodeOffsets[i])); // indexPtr

            int? child = childOffsets[i];
            bytes.AddRange(BitConverter.GetBytes(child ?? 0));             // entryNextTable
            bytes.AddRange(BitConverter.GetBytes(child.HasValue ? 1 : 0)); // entryIndexCount
        }

        return start;
    }
```

- [ ] **Step 6: Run the new tests to verify they pass**

Run: `dotnet test tests/LEC.DbxFormat.Tests --filter "RebuildRoundTripsMoreThan255Messages|RebuildRoundTripsEnoughMessagesToForceATwoLevelTree"`
Expected: PASS — both tests.

- [ ] **Step 7: Run the full test suite to check for regressions**

Run: `dotnet test`
Expected: PASS — no regression in any project (in particular, the pre-existing small-folder round-trip tests in `DbxEmailFileWriterTests.cs` must still pass unchanged, since `BuildIndexSubtree` with `offsets.Count <= 255` produces byte-for-byte the same node it always did).

- [ ] **Step 8: Commit**

```bash
git add src/LEC.DbxFormat/DbxEmailFileWriter.cs tests/LEC.DbxFormat.Tests/DbxEmailFileWriterTests.cs
git commit -m "Remove 255-message-per-folder cap via multi-level .dbx index tree"
```
