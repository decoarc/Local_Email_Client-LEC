# Mover pasta entre perfis — motor (sem UI) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a folder (with its whole subtree of subfolders and messages) be moved from one open profile into a different open profile — copying everything into the destination and only then hiding the source subtree via the already-shipped `DeleteFolderAsync`. Fully testable end to end without any UI.

**Architecture:** A single new `MainWindowViewModel.MoveFolderAsync(sourceProfile, sourceNode, destinationProfile, destinationParentId)` is the one entry point for every folder move. When source and destination are the same `ProfileViewModel`, it delegates straight to the already-shipped `ProfileViewModel.MoveFolderAsync` (cheap `ParentId` retarget, unchanged). When they differ, it walks the already-built `FolderNodeViewModel` tree top-down, calling `ProfileViewModel.CreateFolderAsync`/`IMailStore.AddMessageAsync` to recreate each folder and copy each non-recovered message into the destination, then calls `ProfileViewModel.DeleteFolderAsync` on the source root only after the whole subtree copied successfully. No new `IMailStore`/`DbxStore` member is added — every primitive this uses already exists and is already tested.

**Tech Stack:** .NET 9, C#, xUnit.

## Global Constraints

- Same-profile moves are unchanged — this plan only adds a *new* cross-profile path, dispatched from one new entry point that also still handles the existing same-profile case by delegation.
- Cross-profile copy is all-or-nothing on the source side: `sourceProfile.DeleteFolderAsync` is called only after every folder and message in the subtree has been created/copied into the destination without error. If any step throws, the exception propagates, nothing is hidden in the source, and whatever was already created in the destination stays there (no rollback).
- Recovered/scavenged items (`MessageHeader.RecoveredKind != null`) are never copied — only normal, index-backed messages.
- The special-folder guard is checked once, up front, against the whole subtree (`sourceNode` and every descendant in `sourceNode.Children`) — before any folder is created in the destination. This is stricter than the existing same-profile `DbxStore.MoveFolderAsync` (which only rejects if the folder being moved itself is special) — a known, accepted asymmetry: the cross-profile path must check the whole subtree upfront to avoid copying everything only to fail on the final `DeleteFolderAsync` call, which does check the whole subtree.
- `sourceNode.Children` is walked directly with plain recursion — no cycle-guard/visited-set is needed here, because `FolderNodeViewModel` trees are already built by `FolderTreeViewModel.Build`, which resolves any `ParentId` cycle by promoting the affected node to a root; a folder can only ever appear once, as one node's child, in the whole tree.
- Reference: `docs/superpowers/specs/2026-08-05-mover-pasta-entre-perfis-design.md` §3 (full design this plan implements).

---

## File Structure

```
src/LEC.App/ViewModels/
  MainWindowViewModel.cs         MODIFIED — Task 1: +MoveFolderAsync, +ContainsSpecial, +CopyFolderSubtreeAsync

tests/LEC.App.Tests/
  MainWindowViewModelTests.cs    MODIFIED — Task 1: +5 MoveFolderAsync tests
```

---

### Task 1: `MainWindowViewModel.MoveFolderAsync`

**Files:**
- Modify: `src/LEC.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/LEC.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `ProfileViewModel.MoveFolderAsync(int, int?)`, `ProfileViewModel.CreateFolderAsync(string, int?)`, `ProfileViewModel.DeleteFolderAsync(int)`, `ProfileViewModel.Store` (all existing), `IMailStore.EnumerateHeadersAsync`/`OpenMessageAsync`/`AddMessageAsync` (all existing), `FolderNodeViewModel.Folder`/`.Children`/`.DisplayName` (all existing).
- Produces: `MainWindowViewModel.MoveFolderAsync(ProfileViewModel sourceProfile, FolderNodeViewModel sourceNode, ProfileViewModel destinationProfile, int? destinationParentId, CancellationToken ct = default) : Task`. Consumed by the UI plan's Task 3 (`MainWindow.xaml.cs`'s `MoveFolder_Click`).

- [ ] **Step 1: Write the failing tests**

In `tests/LEC.App.Tests/MainWindowViewModelTests.cs`, add `using System.Linq;` as the first `using` line if it isn't already there, then add these 5 tests at the end of the class, before the last `}`:

```csharp
    [Fact]
    public async Task MoveFolderAsyncWithinTheSameProfileDelegatesToProfileViewModel()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { folderA, folderB });
        var profile = new ProfileViewModel("P", @"C:\perfis\P", store);

        var viewModel = new MainWindowViewModel();

        await viewModel.MoveFolderAsync(profile, profile.FolderTree.RootNodes.Single(n => n.Folder.Id == folderA.Id),
            profile, folderB.Id);

        Assert.Contains((folderA.Id, (int?)folderB.Id), store.MovedFolders);
        Assert.Empty(store.AddedMessages);
        Assert.Empty(store.DeletedFolderIds);
    }

    [Fact]
    public async Task MoveFolderAsyncAcrossProfilesCopiesTheWholeSubtreeExcludingRecoveredItemsAndHidesTheSource()
    {
        var rootFolder = new StoreFolder { Id = 1, ParentId = null, Name = "Notas", IsLoadable = true, HasBackingFile = true };
        var childFolder = new StoreFolder { Id = 2, ParentId = 1, Name = "2026", IsLoadable = true, HasBackingFile = true };

        var normalRootRef = new MessageRef(1, 100);
        var recoveredRootRef = new MessageRef(1, 101);
        var normalChildRef = new MessageRef(2, 200);

        var sourceStore = new FakeMailStore(
            folders: new[] { rootFolder, childFolder },
            headersByFolder: new()
            {
                [1] = new()
                {
                    new MessageHeader { Id = normalRootRef, Subject = "Oi", SenderName = "Ana", MessageId = "<m1>", Date = new DateTime(2004, 1, 1) },
                    new MessageHeader { Id = recoveredRootRef, Subject = "Fragmento", RecoveredKind = RecoveredMessageKind.Recovered },
                },
                [2] = new()
                {
                    new MessageHeader { Id = normalChildRef, Subject = "Sub" },
                },
            },
            // Deliberately no entry for recoveredRootRef: if the implementation ever tried to
            // copy it, OpenMessageAsync would throw "No raw bytes registered" and fail this test.
            rawByRef: new Dictionary<MessageRef, byte[]>
            {
                [normalRootRef] = "conteudo-raiz"u8.ToArray(),
                [normalChildRef] = "conteudo-filha"u8.ToArray(),
            });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destStore = new FakeMailStore(folders: Array.Empty<StoreFolder>());
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var viewModel = new MainWindowViewModel();
        var sourceNode = sourceProfile.FolderTree.RootNodes.Single();

        await viewModel.MoveFolderAsync(sourceProfile, sourceNode, destProfile, destinationParentId: null);

        var destNotas = Assert.Single(destStore.Folders, f => f.Name == "Notas");
        var dest2026 = Assert.Single(destStore.Folders, f => f.Name == "2026");
        Assert.Equal(destNotas.Id, dest2026.ParentId);

        Assert.Equal(2, destStore.AddedMessages.Count); // recovered item excluded
        Assert.Contains(destStore.AddedMessages, m => m.FolderId == destNotas.Id && m.Subject == "Oi");
        Assert.Contains(destStore.AddedMessages, m => m.FolderId == dest2026.Id && m.Subject == "Sub");

        Assert.Contains(rootFolder.Id, sourceStore.DeletedFolderIds);
    }

    [Fact]
    public async Task MoveFolderAsyncAcrossProfilesRejectsASpecialSourceFolderBeforeCopyingAnything()
    {
        var specialFolder = new StoreFolder { Id = 1, Name = "Caixa de entrada", SpecialFolderKind = SpecialFolderKind.Inbox, IsLoadable = true, HasBackingFile = true };
        var sourceStore = new FakeMailStore(folders: new[] { specialFolder });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destStore = new FakeMailStore(folders: Array.Empty<StoreFolder>());
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var viewModel = new MainWindowViewModel();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.MoveFolderAsync(sourceProfile, sourceProfile.FolderTree.RootNodes.Single(), destProfile, null));

        Assert.Empty(destStore.Folders);
        Assert.Empty(sourceStore.DeletedFolderIds);
    }

    [Fact]
    public async Task MoveFolderAsyncAcrossProfilesRejectsWhenASpecialFolderIsNestedInTheSubtree()
    {
        var rootFolder = new StoreFolder { Id = 1, ParentId = null, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var specialChild = new StoreFolder { Id = 2, ParentId = 1, Name = "Enviados", SpecialFolderKind = SpecialFolderKind.SentItems, IsLoadable = true, HasBackingFile = true };
        var sourceStore = new FakeMailStore(folders: new[] { rootFolder, specialChild });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destStore = new FakeMailStore(folders: Array.Empty<StoreFolder>());
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var viewModel = new MainWindowViewModel();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.MoveFolderAsync(sourceProfile, sourceProfile.FolderTree.RootNodes.Single(), destProfile, null));

        Assert.Empty(destStore.Folders);
        Assert.Empty(sourceStore.DeletedFolderIds);
    }

    [Fact]
    public async Task MoveFolderAsyncAcrossProfilesDoesNotHideTheSourceWhenACopyStepFailsAndLeavesThePartialCopyInPlace()
    {
        var rootFolder = new StoreFolder { Id = 1, ParentId = null, Name = "Notas", IsLoadable = true, HasBackingFile = true };
        var messageRef = new MessageRef(1, 100);
        var sourceStore = new FakeMailStore(
            folders: new[] { rootFolder },
            headersByFolder: new() { [1] = new() { new MessageHeader { Id = messageRef, Subject = "Oi" } } },
            rawByRef: new Dictionary<MessageRef, byte[]> { [messageRef] = "conteudo"u8.ToArray() });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destStore = new FakeMailStore(folders: Array.Empty<StoreFolder>())
        {
            ExceptionToThrowOnAddMessage = new InvalidOperationException("pasta de destino cheia"),
        };
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var viewModel = new MainWindowViewModel();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.MoveFolderAsync(sourceProfile, sourceProfile.FolderTree.RootNodes.Single(), destProfile, null));

        // The folder itself was created before the message copy failed -- it stays behind as
        // an accepted partial duplicate; only hiding the source is refused.
        Assert.Single(destStore.Folders);
        Assert.Empty(sourceStore.DeletedFolderIds);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter "MoveFolderAsyncWithinTheSameProfileDelegatesToProfileViewModel|MoveFolderAsyncAcrossProfilesCopiesTheWholeSubtreeExcludingRecoveredItemsAndHidesTheSource|MoveFolderAsyncAcrossProfilesRejectsASpecialSourceFolderBeforeCopyingAnything|MoveFolderAsyncAcrossProfilesRejectsWhenASpecialFolderIsNestedInTheSubtree|MoveFolderAsyncAcrossProfilesDoesNotHideTheSourceWhenACopyStepFailsAndLeavesThePartialCopyInPlace"`
Expected: FAIL to compile — `MainWindowViewModel.MoveFolderAsync` doesn't exist yet.

- [ ] **Step 3: Implement `MoveFolderAsync`**

In `src/LEC.App/ViewModels/MainWindowViewModel.cs`, add `using System.Linq;` to the top of the file if it isn't already there (alongside the existing `using System.Collections.ObjectModel;`/`using System.IO;`/etc.), then add this at the end of the class, right after `DeleteMessageAsync`, before the class's closing `}`:

```csharp
    /// <summary>
    /// Moves a folder — same-profile is the existing cheap ParentId retarget
    /// (ProfileViewModel.MoveFolderAsync); cross-profile copies the whole subtree (folders +
    /// non-recovered messages) into destinationProfile, then hides it in sourceProfile via
    /// the already-shipped DeleteFolderAsync — never touches sourceProfile's Folders.dbx or
    /// any message .dbx. Nothing is hidden in the source unless the ENTIRE subtree copied
    /// successfully first.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when sourceNode's folder, or any folder in its subtree, is special
    /// (SpecialFolderKind != None) — checked before any copying starts. For a cross-profile
    /// move, also propagates whatever CreateFolderAsync/AddMessageAsync throws on the
    /// destination profile (name collision, 255-item caps, damaged files) — the source is
    /// left completely untouched in every one of those cases, though whatever was already
    /// created in the destination before the failure is not rolled back.
    /// </exception>
    public async Task MoveFolderAsync(ProfileViewModel sourceProfile, FolderNodeViewModel sourceNode,
        ProfileViewModel destinationProfile, int? destinationParentId, CancellationToken ct = default)
    {
        if (ReferenceEquals(sourceProfile, destinationProfile))
        {
            await sourceProfile.MoveFolderAsync(sourceNode.Folder.Id, destinationParentId);
            return;
        }

        if (sourceNode.Folder.SpecialFolderKind != SpecialFolderKind.None || ContainsSpecial(sourceNode))
            throw new InvalidOperationException("Pastas especiais não podem ser movidas.");

        await CopyFolderSubtreeAsync(sourceProfile, sourceNode, destinationProfile, destinationParentId, ct);

        await sourceProfile.DeleteFolderAsync(sourceNode.Folder.Id);
    }

    private static bool ContainsSpecial(FolderNodeViewModel node) =>
        node.Children.Any(c => c.Folder.SpecialFolderKind != SpecialFolderKind.None || ContainsSpecial(c));

    // Recreates one folder in destinationProfile (under destinationParentId), copies every
    // non-recovered message from it, then recurses into every child using the newly created
    // folder's id as the next level's parent. sourceNode.Children can never contain a cycle
    // (FolderTreeViewModel.Build already resolved any at UI-tree-build time), so no
    // visited-set is needed here.
    private async Task CopyFolderSubtreeAsync(ProfileViewModel sourceProfile, FolderNodeViewModel sourceNode,
        ProfileViewModel destinationProfile, int? destinationParentId, CancellationToken ct)
    {
        var created = await destinationProfile.CreateFolderAsync(sourceNode.DisplayName, destinationParentId);

        if (sourceNode.Folder.HasBackingFile)
        {
            await foreach (var header in sourceProfile.Store.EnumerateHeadersAsync(sourceNode.Folder, ct))
            {
                if (header.RecoveredKind is not null)
                    continue;

                using var stream = await sourceProfile.Store.OpenMessageAsync(header.Id, ct);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);

                await destinationProfile.Store.AddMessageAsync(created.Id, ms.ToArray(),
                    header.Subject, header.SenderName, header.SenderAddress, header.MessageId, header.Date, ct);
            }
        }

        foreach (var child in sourceNode.Children)
            await CopyFolderSubtreeAsync(sourceProfile, child, destinationProfile, created.Id, ct);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter "MoveFolderAsyncWithinTheSameProfileDelegatesToProfileViewModel|MoveFolderAsyncAcrossProfilesCopiesTheWholeSubtreeExcludingRecoveredItemsAndHidesTheSource|MoveFolderAsyncAcrossProfilesRejectsASpecialSourceFolderBeforeCopyingAnything|MoveFolderAsyncAcrossProfilesRejectsWhenASpecialFolderIsNestedInTheSubtree|MoveFolderAsyncAcrossProfilesDoesNotHideTheSourceWhenACopyStepFailsAndLeavesThePartialCopyInPlace"`
Expected: PASS — all 5 new tests.

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/MainWindowViewModel.cs tests/LEC.App.Tests/MainWindowViewModelTests.cs
git commit -m "Add MainWindowViewModel.MoveFolderAsync: cross-profile folder move by copy-then-hide"
```

---

## Notes for the next slice (UI)

- `MoveFolderDialog` currently takes a single `ProfileViewModel` and returns only `SelectedFolderId`. The UI plan rewrites it to take `IEnumerable<ProfileViewModel> openProfiles` (mirroring `MoveMessageDialog`) and also return a `SelectedProfile`, so `MainWindow.xaml.cs`'s `MoveFolder_Click` can call this task's new `MainWindowViewModel.MoveFolderAsync(sourceProfile, node, dialog.SelectedProfile, dialog.SelectedFolderId)` directly, with no branching in the code-behind — the same-vs-different-profile decision is entirely internal to the method this task adds.
