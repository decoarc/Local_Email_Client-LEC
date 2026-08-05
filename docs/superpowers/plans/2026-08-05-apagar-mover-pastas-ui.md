# Apagar e mover pastas — UI — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user delete a whole folder (with everything inside it) and move a folder to a new parent, from the folder tree's context menu on the main screen — no code changes in `LEC.MailStore`/`LEC.DbxFormat` beyond promoting two already-shipped `DbxStore` methods onto `IMailStore`.

**Architecture:** `IMailStore` gains `DeleteFolderAsync`/`MoveFolderAsync` (already implemented on `DbxStore`, from the motor slice); `FakeMailStore` gets in-memory equivalents for tests. `ProfileViewModel` gets thin wrappers that call the store then rebuild `FolderTree`, mirroring `CreateFolderAsync`. A new `MoveFolderDestinationRules`/`MoveFolderDestinationValidityConverter` pair (pure logic + WPF adapter) flags a folder's own subtree as invalid destinations, reusing the already-loaded `FolderNodeViewModel` tree instead of re-deriving ancestry. A new `MoveFolderDialog` (single-profile `TreeView`, same shape as `MoveMessageDialog` but scoped to one profile) lets the user pick a destination. Two new context-menu items on the folder tree ("Apagar pasta", "Mover pasta...") — hidden for special folders — wire into `MainWindow.xaml.cs` handlers that confirm (for delete), call the `ProfileViewModel` method, and reset whatever screen state might now be stale.

**Tech Stack:** .NET 9, C#, WPF, xUnit.

## Global Constraints

- Special folders never show "Apagar pasta"/"Mover pasta..." in the context menu — the menu item's `Visibility` is bound to `FolderNodeViewModel.IsSpecial` (existing property), inverted.
- Deleting a folder asks for a Sim/Não confirmation naming the subtree; moving a folder does not (picking the destination and clicking "Mover" in the dialog is itself the confirmation) — same asymmetry already established between apagar-mensagem and mover-mensagem.
- `MoveFolderDialog` shows only the source folder's own profile (one `TreeView`, not one per open profile like `MoveMessageDialog`) — folder move is same-store only per the motor slice.
- The source folder and everything under it must be excluded as valid move destinations. `TreeViewItem.IsEnabled` is never used for this (it cascades to every descendant in WPF, which would make the source's siblings' own children unreachable if the wrong node were ever disabled) — same color-only trick already used by `MoveDestinationValidityConverter`/`MoveMessageDialog`.
- Reference: `docs/superpowers/specs/2026-08-05-apagar-mover-pastas-ui-design.md` (full design this plan implements) and `docs/superpowers/specs/2026-08-05-apagar-mover-pastas-motor-design.md` (the `DbxStore` methods this plan promotes onto `IMailStore`, already shipped).

---

## File Structure

```
src/LEC.MailStore/
  IMailStore.cs                       MODIFIED — Task 1: +DeleteFolderAsync, +MoveFolderAsync

src/LEC.App/ViewModels/
  ProfileViewModel.cs                 MODIFIED — Task 2: +DeleteFolderAsync, +MoveFolderAsync

src/LEC.App/Services/
  MoveFolderDestinationRules.cs       NEW — Task 3

src/LEC.App/Converters/
  MoveFolderDestinationValidityConverter.cs   NEW — Task 3

src/LEC.App/
  MoveFolderDialog.xaml               NEW — Task 4
  MoveFolderDialog.xaml.cs            NEW — Task 4
  MainWindow.xaml                     MODIFIED — Task 5: +2 MenuItems on the folder tree's TextBlock.ContextMenu, +SpecialToVisibilityConverter resource
  MainWindow.xaml.cs                  MODIFIED — Task 5: +DeleteFolder_Click, +ConfirmAndDeleteFolderAsync, +MoveFolder_Click

src/LEC.App/Converters/
  SpecialToVisibilityConverter.cs     NEW — Task 5

tests/LEC.App.Tests/
  FakeMailStore.cs                    MODIFIED — Task 1: +DeleteFolderAsync/MoveFolderAsync (in-memory) + tracking fields
  ProfileViewModelTests.cs            MODIFIED — Task 2: +DeleteFolderAsync/MoveFolderAsync tests
  MoveFolderDestinationRulesTests.cs  NEW — Task 3
```

`MainWindow.xaml.cs`'s existing handlers have no dedicated tests (same established precedent as `CreateFolderAsync`'s and `MoveMessageDialog`'s own click handlers) — Task 5 ends with a manual verification pass instead of new automated tests.

---

### Task 1: Promote `DeleteFolderAsync`/`MoveFolderAsync` onto `IMailStore`

**Files:**
- Modify: `src/LEC.MailStore/IMailStore.cs`
- Modify: `tests/LEC.App.Tests/FakeMailStore.cs`

**Interfaces:**
- Consumes: `DbxStore.DeleteFolderAsync`/`DbxStore.MoveFolderAsync` (already implemented, motor slice).
- Produces: `IMailStore.DeleteFolderAsync(int folderId, CancellationToken ct = default) : Task`, `IMailStore.MoveFolderAsync(int folderId, int? newParentId, CancellationToken ct = default) : Task`, now callable polymorphically. Consumed by Task 2 (`ProfileViewModel`) and by this task's own `FakeMailStore`, which must implement both.

- [ ] **Step 1: Add both members to `IMailStore`**

In `src/LEC.MailStore/IMailStore.cs`, replace:

```csharp
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

with:

```csharp
    /// <summary>
    /// Marks a message as deleted — EnumerateHeadersAsync stops returning it from now on.
    /// Never writes to the backing .dbx; purely a local, app-side "hide" marker (see
    /// DeletedMessageCache), kept separate from HideMovedMessageAsync's marker even though
    /// the mechanism is identical. A no-op, not an error, if called twice for the same
    /// message.
    /// </summary>
    Task DeleteMessageAsync(MessageRef id, CancellationToken ct = default);

    /// <summary>
    /// Hides a folder — and every folder in its subtree — from Folders. Never writes to
    /// Folders.dbx or any message .dbx. See DbxStore.DeleteFolderAsync for the full
    /// contract (special-folder guard, subtree collection, exceptions thrown). Unlike
    /// DeleteMessageAsync, calling this twice for the same id throws on the second call.
    /// </summary>
    Task DeleteFolderAsync(int folderId, CancellationToken ct = default);

    /// <summary>
    /// Moves a folder to a new parent within this store — a from-scratch rewrite of
    /// Folders.dbx that changes only the moved folder's ParentId entry. See
    /// DbxStore.MoveFolderAsync for the full contract (cycle guard, name-collision guard,
    /// exceptions thrown). Same-store only — no cross-profile move.
    /// </summary>
    Task MoveFolderAsync(int folderId, int? newParentId, CancellationToken ct = default);
}
```

- [ ] **Step 2: Run tests to verify `FakeMailStore` no longer compiles as an `IMailStore`**

Run: `dotnet test tests/LEC.App.Tests`
Expected: FAIL to compile — `FakeMailStore` does not implement `IMailStore.DeleteFolderAsync`/`MoveFolderAsync` yet.

- [ ] **Step 3: Make `FakeMailStore` implement both members**

In `tests/LEC.App.Tests/FakeMailStore.cs`, replace:

```csharp
    public HashSet<MessageRef> DeletedMessageRefs { get; } = new();

    public Task DeleteMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        DeletedMessageRefs.Add(id);
        return Task.CompletedTask;
    }

    public bool WasDisposed { get; private set; }
```

with:

```csharp
    public HashSet<MessageRef> DeletedMessageRefs { get; } = new();

    public Task DeleteMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        DeletedMessageRefs.Add(id);
        return Task.CompletedTask;
    }

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

    public Exception? ExceptionToThrowOnMoveFolder { get; set; }
    public List<(int FolderId, int? NewParentId)> MovedFolders { get; } = new();

    public Task MoveFolderAsync(int folderId, int? newParentId, CancellationToken ct = default)
    {
        if (ExceptionToThrowOnMoveFolder is { } ex)
            throw ex;

        MovedFolders.Add((folderId, newParentId));

        int index = _folders.FindIndex(f => f.Id == folderId);
        var current = _folders[index];
        _folders[index] = new StoreFolder
        {
            Id = current.Id,
            ParentId = newParentId,
            Name = current.Name,
            SpecialFolderKind = current.SpecialFolderKind,
            MessageCount = current.MessageCount,
            IsLoadable = current.IsLoadable,
            HasBackingFile = current.HasBackingFile,
        };
        return Task.CompletedTask;
    }

    public bool WasDisposed { get; private set; }
```

- [ ] **Step 4: Run tests to verify everything compiles and passes**

Run: `dotnet test`
Expected: PASS — no regression in any project (this only adds new members; nothing existing calls them yet).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.MailStore/IMailStore.cs tests/LEC.App.Tests/FakeMailStore.cs
git commit -m "Promote DeleteFolderAsync/MoveFolderAsync onto IMailStore"
```

---

### Task 2: `ProfileViewModel.DeleteFolderAsync`/`MoveFolderAsync`

**Files:**
- Modify: `src/LEC.App/ViewModels/ProfileViewModel.cs`
- Modify: `tests/LEC.App.Tests/ProfileViewModelTests.cs`

**Interfaces:**
- Consumes: `IMailStore.DeleteFolderAsync`/`IMailStore.MoveFolderAsync` (Task 1).
- Produces: `ProfileViewModel.DeleteFolderAsync(int folderId) : Task`, `ProfileViewModel.MoveFolderAsync(int folderId, int? newParentId) : Task`. Consumed by Task 5's `MainWindow.xaml.cs` handlers.

- [ ] **Step 1: Write the failing tests**

In `tests/LEC.App.Tests/ProfileViewModelTests.cs`, add at the end of the class, before the last `}`:

```csharp
    [Fact]
    public async Task DeleteFolderAsyncRemovesTheFolderFromTheTree()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var child = new StoreFolder { Id = 2, ParentId = 1, Name = "Filha", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { root, child });
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        await profile.DeleteFolderAsync(child.Id);

        Assert.Contains(child.Id, store.DeletedFolderIds);
        var rootNode = Assert.Single(profile.FolderTree.RootNodes);
        Assert.Empty(rootNode.Children);
    }

    [Fact]
    public async Task DeleteFolderAsyncPropagatesStoreExceptions()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { root })
        {
            ExceptionToThrowOnDeleteFolder = new InvalidOperationException("pasta especial"),
        };
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => profile.DeleteFolderAsync(root.Id));
        Assert.Equal("pasta especial", ex.Message);
    }

    [Fact]
    public async Task MoveFolderAsyncUpdatesTheTreeUnderTheNewParent()
    {
        var rootA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var rootB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
        var moved = new StoreFolder { Id = 3, ParentId = 1, Name = "Movida", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { rootA, rootB, moved });
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        await profile.MoveFolderAsync(moved.Id, newParentId: rootB.Id);

        Assert.Contains((moved.Id, (int?)rootB.Id), store.MovedFolders);
        var nodeB = profile.FolderTree.RootNodes.Single(n => n.Folder.Id == rootB.Id);
        Assert.Contains(nodeB.Children, n => n.Folder.Id == moved.Id);
    }

    [Fact]
    public async Task MoveFolderAsyncPropagatesStoreExceptions()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { root })
        {
            ExceptionToThrowOnMoveFolder = new InvalidOperationException("ciclo"),
        };
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => profile.MoveFolderAsync(root.Id, newParentId: null));
        Assert.Equal("ciclo", ex.Message);
    }
```

This requires `using System.Linq;` in the test file for `.Single(...)` — check the top of `tests/LEC.App.Tests/ProfileViewModelTests.cs`; if it's not already there, add it as the first `using` line.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter "ProfileViewModelTests"`
Expected: FAIL to compile — `ProfileViewModel.DeleteFolderAsync`/`MoveFolderAsync` don't exist yet.

- [ ] **Step 3: Implement both methods**

In `src/LEC.App/ViewModels/ProfileViewModel.cs`, replace:

```csharp
    public async Task<StoreFolder> CreateFolderAsync(string name, int? parentId)
    {
        var created = await Store.CreateFolderAsync(name, parentId);
        FolderTree.Build(Store.Folders);
        return created;
    }
}
```

with:

```csharp
    public async Task<StoreFolder> CreateFolderAsync(string name, int? parentId)
    {
        var created = await Store.CreateFolderAsync(name, parentId);
        FolderTree.Build(Store.Folders);
        return created;
    }

    public async Task DeleteFolderAsync(int folderId)
    {
        await Store.DeleteFolderAsync(folderId);
        FolderTree.Build(Store.Folders);
    }

    public async Task MoveFolderAsync(int folderId, int? newParentId)
    {
        await Store.MoveFolderAsync(folderId, newParentId);
        FolderTree.Build(Store.Folders);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter "ProfileViewModelTests"`
Expected: PASS — all tests (4 new + existing).

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/ProfileViewModel.cs tests/LEC.App.Tests/ProfileViewModelTests.cs
git commit -m "Add ProfileViewModel.DeleteFolderAsync/MoveFolderAsync"
```

---

### Task 3: `MoveFolderDestinationRules` + `MoveFolderDestinationValidityConverter`

**Files:**
- Create: `src/LEC.App/Services/MoveFolderDestinationRules.cs`
- Create: `src/LEC.App/Converters/MoveFolderDestinationValidityConverter.cs`
- Test: `tests/LEC.App.Tests/MoveFolderDestinationRulesTests.cs`

**Interfaces:**
- Consumes: `FolderNodeViewModel.Children`/`.Folder` (existing).
- Produces: `MoveFolderDestinationRules.IsValidDestination(FolderNodeViewModel candidate, FolderNodeViewModel sourceNode) : bool`. Consumed by Task 4's `MoveFolderDialog` (via the converter, and directly in its code-behind's selection handler).

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.App.Tests/MoveFolderDestinationRulesTests.cs`:

```csharp
// tests/LEC.App.Tests/MoveFolderDestinationRulesTests.cs
using LEC.App.Services;
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class MoveFolderDestinationRulesTests
{
    private static FolderNodeViewModel Node(int id, string name = "F") =>
        new(new StoreFolder { Id = id, Name = name, IsLoadable = true, HasBackingFile = true });

    [Fact]
    public void TheSourceFolderItselfIsInvalid()
    {
        var source = Node(1);

        Assert.False(MoveFolderDestinationRules.IsValidDestination(source, source));
    }

    [Fact]
    public void ADirectChildOfTheSourceIsInvalid()
    {
        var source = Node(1);
        var child = Node(2);
        source.Children.Add(child);

        Assert.False(MoveFolderDestinationRules.IsValidDestination(child, source));
    }

    [Fact]
    public void AGrandchildOfTheSourceIsInvalid()
    {
        var source = Node(1);
        var child = Node(2);
        var grandchild = Node(3);
        child.Children.Add(grandchild);
        source.Children.Add(child);

        Assert.False(MoveFolderDestinationRules.IsValidDestination(grandchild, source));
    }

    [Fact]
    public void AnUnrelatedFolderIsValid()
    {
        var source = Node(1);
        var other = Node(2);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(other, source));
    }

    [Fact]
    public void TheSourcesCurrentParentIsValid()
    {
        // Moving a folder back under its own current parent is a harmless no-op reorder,
        // not a cycle -- the parent is not the source and not one of its descendants.
        var parent = Node(1);
        var source = Node(2);
        parent.Children.Add(source);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(parent, source));
    }

    [Fact]
    public void ASiblingOfTheSourceIsValid()
    {
        var parent = Node(1);
        var source = Node(2);
        var sibling = Node(3);
        parent.Children.Add(source);
        parent.Children.Add(sibling);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(sibling, source));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter "MoveFolderDestinationRulesTests"`
Expected: FAIL to compile — `MoveFolderDestinationRules` doesn't exist yet.

- [ ] **Step 3: Implement `MoveFolderDestinationRules`**

Create `src/LEC.App/Services/MoveFolderDestinationRules.cs`:

```csharp
// src/LEC.App/Services/MoveFolderDestinationRules.cs
using LEC.App.ViewModels;

namespace LEC.App.Services;

/// <summary>
/// Whether a folder node is a legal destination for MainWindowViewModel/ProfileViewModel's
/// MoveFolderAsync, checked upfront so MoveFolderDialog can gray out invalid folders
/// instead of letting the user pick one and fail after the fact. Walks the already-built
/// FolderNodeViewModel tree (not DbxStore.Folders' flat ParentId list) because the dialog
/// already has that tree in memory for the source profile -- no need to re-derive ancestry
/// the way DbxStore.MoveFolderAsync's own cycle guard does on the flat list.
/// This does NOT replace DbxStore.MoveFolderAsync's own guards -- a name collision at the
/// destination still isn't cheaply knowable here and surfaces as a normal error instead.
/// </summary>
public static class MoveFolderDestinationRules
{
    public static bool IsValidDestination(FolderNodeViewModel candidate, FolderNodeViewModel sourceNode)
    {
        if (ReferenceEquals(candidate, sourceNode))
            return false;

        return !ContainsDescendant(sourceNode, candidate);
    }

    private static bool ContainsDescendant(FolderNodeViewModel node, FolderNodeViewModel candidate)
    {
        foreach (var child in node.Children)
        {
            if (ReferenceEquals(child, candidate) || ContainsDescendant(child, candidate))
                return true;
        }
        return false;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter "MoveFolderDestinationRulesTests"`
Expected: PASS — all 6 tests.

- [ ] **Step 5: Implement `MoveFolderDestinationValidityConverter`**

Create `src/LEC.App/Converters/MoveFolderDestinationValidityConverter.cs`:

```csharp
// src/LEC.App/Converters/MoveFolderDestinationValidityConverter.cs
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LEC.App.Services;
using LEC.App.ViewModels;

namespace LEC.App.Converters;

/// <summary>
/// XAML-side adapter for MoveFolderDestinationRules.IsValidDestination -- grays a folder's
/// label instead of disabling its TreeViewItem container, same reasoning as
/// MoveDestinationValidityConverter (IsEnabled cascades to every descendant in WPF, so
/// disabling the source folder would also swallow its own valid siblings' subtrees if the
/// wrong node were ever picked). SourceNode is set once by MoveFolderDialog's constructor,
/// before its single TreeView first binds.
/// </summary>
public sealed class MoveFolderDestinationValidityConverter : IValueConverter
{
    public FolderNodeViewModel? SourceNode { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (SourceNode is null || value is not FolderNodeViewModel candidate)
            return Brushes.Gray;

        bool isValid = MoveFolderDestinationRules.IsValidDestination(candidate, SourceNode);
        return isValid ? Brushes.Black : Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS — no regression in any project.

```bash
git add src/LEC.App/Services/MoveFolderDestinationRules.cs src/LEC.App/Converters/MoveFolderDestinationValidityConverter.cs tests/LEC.App.Tests/MoveFolderDestinationRulesTests.cs
git commit -m "Add MoveFolderDestinationRules: excludes a folder's own subtree as a move destination"
```

---

### Task 4: `MoveFolderDialog`

**Files:**
- Create: `src/LEC.App/MoveFolderDialog.xaml`
- Create: `src/LEC.App/MoveFolderDialog.xaml.cs`

**Interfaces:**
- Consumes: `MoveFolderDestinationValidityConverter`/`MoveFolderDestinationRules` (Task 3), `ProfileViewModel.FolderTree` (existing), `FolderNodeViewModel` (existing).
- Produces: `MoveFolderDialog(ProfileViewModel profile, FolderNodeViewModel sourceNode)`, `.SelectedFolderId : int?` (property, `null` also legitimately means "move to root" once `DialogResult == true` — distinguish via `DialogResult`, same convention `MoveMessageDialog` uses for "nothing armed yet"). Consumed by Task 5's `MainWindow.xaml.cs`.

No dedicated test for this task — `MoveFolderDialog` is a thin WPF shell around already-tested `MoveFolderDestinationRules` (Task 3); manual verification happens at the end of Task 5, once it's wired to a real menu item.

- [ ] **Step 1: Create the XAML**

Create `src/LEC.App/MoveFolderDialog.xaml`:

```xml
<!-- src/LEC.App/MoveFolderDialog.xaml -->
<Window x:Class="LEC.App.MoveFolderDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:conv="clr-namespace:LEC.App.Converters"
        Title="Mover pasta" Width="360" Height="440"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False">
    <Window.Resources>
        <conv:MoveFolderDestinationValidityConverter x:Key="ValidityConverter"/>
    </Window.Resources>
    <DockPanel Margin="12">
        <TextBlock DockPanel.Dock="Top" Text="Escolha a nova pasta-mãe:" Margin="0,0,0,8"/>
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Cancelar" IsCancel="True" Width="80" Margin="0,0,8,0"/>
            <Button x:Name="MoveButton" Content="Mover" IsDefault="True" Width="80" IsEnabled="False" Click="MoveButton_Click"/>
        </StackPanel>
        <ScrollViewer VerticalScrollBarVisibility="Auto">
            <StackPanel>
                <TextBlock x:Name="RootLabel" Text="Pastas locais (raiz)" Margin="0,0,0,4" Cursor="Hand"
                           MouseLeftButtonUp="RootLabel_MouseLeftButtonUp"/>
                <TreeView x:Name="FolderTreeView"
                          SelectedItemChanged="TreeView_SelectedItemChanged"
                          BorderThickness="0"
                          ScrollViewer.VerticalScrollBarVisibility="Disabled"
                          ScrollViewer.HorizontalScrollBarVisibility="Disabled">
                    <TreeView.ItemTemplate>
                        <HierarchicalDataTemplate ItemsSource="{Binding Children}">
                            <TextBlock Text="{Binding DisplayName}"
                                       Foreground="{Binding Converter={StaticResource ValidityConverter}}"/>
                        </HierarchicalDataTemplate>
                    </TreeView.ItemTemplate>
                </TreeView>
            </StackPanel>
        </ScrollViewer>
    </DockPanel>
</Window>
```

- [ ] **Step 2: Create the code-behind**

Create `src/LEC.App/MoveFolderDialog.xaml.cs`:

```csharp
// src/LEC.App/MoveFolderDialog.xaml.cs
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LEC.App.Converters;
using LEC.App.Services;
using LEC.App.ViewModels;

namespace LEC.App;

public partial class MoveFolderDialog : Window
{
    private readonly FolderNodeViewModel _sourceNode;

    public int? SelectedFolderId { get; private set; }

    public MoveFolderDialog(ProfileViewModel profile, FolderNodeViewModel sourceNode)
    {
        InitializeComponent();

        _sourceNode = sourceNode;

        var converter = (MoveFolderDestinationValidityConverter)Resources["ValidityConverter"];
        converter.SourceNode = sourceNode;

        FolderTreeView.ItemsSource = profile.FolderTree.RootNodes;
    }

    // "Pastas locais (raiz)" is always a valid destination: moving a folder to the root
    // can never create a cycle (root is nobody's descendant), and DbxStore.MoveFolderAsync
    // treats a no-op "already at root" move as a harmless reorder rather than an error.
    // Clicking this label doesn't touch TreeView.SelectedItem, so there's no risk of
    // TreeView_SelectedItemChanged firing and overwriting this right after.
    private void RootLabel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SelectedFolderId = null;
        MoveButton.IsEnabled = true;
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FolderNodeViewModel node)
            return;

        bool isValid = MoveFolderDestinationRules.IsValidDestination(node, _sourceNode);
        SelectedFolderId = isValid ? node.Folder.Id : null;
        MoveButton.IsEnabled = isValid;
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
```

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet build`
Expected: builds cleanly (no test exercises this dialog directly, but it must compile).

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 4: Commit**

```bash
git add src/LEC.App/MoveFolderDialog.xaml src/LEC.App/MoveFolderDialog.xaml.cs
git commit -m "Add MoveFolderDialog: single-profile folder-tree picker excluding the source subtree"
```

---

### Task 5: Menu de contexto na árvore de pastas

**Files:**
- Create: `src/LEC.App/Converters/SpecialToVisibilityConverter.cs`
- Modify: `src/LEC.App/MainWindow.xaml`
- Modify: `src/LEC.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `ProfileViewModel.DeleteFolderAsync`/`MoveFolderAsync` (Task 2), `MoveFolderDialog` (Task 4), `FolderNodeViewModel.IsSpecial` (existing).
- Produces: user-visible "Apagar pasta"/"Mover pasta..." menu items on every non-special folder node.

No dedicated automated test — same established precedent as `NewSubfolder_Click`/`MoveMessage_Click` (`MainWindow.xaml.cs` has no test file). This task ends with a manual verification checklist instead.

- [ ] **Step 1: Add `SpecialToVisibilityConverter`**

Create `src/LEC.App/Converters/SpecialToVisibilityConverter.cs`:

```csharp
// src/LEC.App/Converters/SpecialToVisibilityConverter.cs
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LEC.App.Converters;

/// <summary>
/// Hides a folder-tree context menu item for special folders (FolderNodeViewModel.IsSpecial
/// == true) -- Inbox/Sent/Drafts/Outbox/Deleted Items can never be deleted or moved (see
/// DbxStore.DeleteFolderAsync/MoveFolderAsync), so the menu never offers the option rather
/// than letting the user pick it and see an error.
/// </summary>
public sealed class SpecialToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 2: Add the converter resource and the two menu items**

In `src/LEC.App/MainWindow.xaml`, replace:

```xml
        <conv:AttachmentListToVisibilityConverter x:Key="AttachmentListToVisibilityConverter"/>
    </Window.Resources>
```

with:

```xml
        <conv:AttachmentListToVisibilityConverter x:Key="AttachmentListToVisibilityConverter"/>
        <conv:SpecialToVisibilityConverter x:Key="SpecialToVisibilityConverter"/>
    </Window.Resources>
```

Then replace:

```xml
                                                <TextBlock.ContextMenu>
                                                    <ContextMenu>
                                                        <MenuItem Header="Nova subpasta aqui..." Click="NewSubfolder_Click"/>
                                                    </ContextMenu>
                                                </TextBlock.ContextMenu>
```

with:

```xml
                                                <TextBlock.ContextMenu>
                                                    <ContextMenu>
                                                        <MenuItem Header="Nova subpasta aqui..." Click="NewSubfolder_Click"/>
                                                        <MenuItem Header="Mover pasta..." Click="MoveFolder_Click"
                                                                  Visibility="{Binding IsSpecial, Converter={StaticResource SpecialToVisibilityConverter}}"/>
                                                        <MenuItem Header="Apagar pasta" Click="DeleteFolder_Click"
                                                                  Visibility="{Binding IsSpecial, Converter={StaticResource SpecialToVisibilityConverter}}"/>
                                                    </ContextMenu>
                                                </TextBlock.ContextMenu>
```

- [ ] **Step 3: Add the click handlers**

In `src/LEC.App/MainWindow.xaml.cs`, add these methods right after `CreateFolderAsync` (i.e. right before the `FindAncestor<T>` helper):

```csharp
    private void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not FolderNodeViewModel node)
            return;
        if ((sender as MenuItem)?.Parent is not ContextMenu { PlacementTarget: FrameworkElement placementTarget })
            return;
        if (FindAncestor<TreeView>(placementTarget) is not { DataContext: ProfileViewModel profile } treeView)
            return;

        _ = ConfirmAndDeleteFolderAsync(profile, node, treeView);
    }

    private async Task ConfirmAndDeleteFolderAsync(ProfileViewModel profile, FolderNodeViewModel node, TreeView treeView)
    {
        var result = MessageBox.Show(this,
            $"Apagar a pasta \"{node.DisplayName}\" e tudo dentro dela (subpastas e mensagens)?",
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

            if (selectionIsInsideDeletedSubtree)
            {
                _selectedNode = null;
                _selectedTreeView = null;
                ScavengeButton.Visibility = Visibility.Collapsed;
                RecoverableBar.Visibility = Visibility.Collapsed;
                NavigateToRenderedMessage(string.Empty);
                ImagesBlockedBar.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LEC — Não foi possível apagar a pasta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            treeView.IsEnabled = true;
        }
    }

    // True if `candidate` is `ancestor` itself or appears anywhere in its subtree. Used to
    // decide whether the currently-selected folder is about to be hidden by a delete two
    // levels up (e.g. selection is a grandchild of the folder the user right-clicked).
    private static bool IsDescendantOf(FolderNodeViewModel ancestor, FolderNodeViewModel candidate)
    {
        foreach (var child in ancestor.Children)
        {
            if (ReferenceEquals(child, candidate) || IsDescendantOf(child, candidate))
                return true;
        }
        return false;
    }

    private async void MoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not FolderNodeViewModel node)
            return;
        if ((sender as MenuItem)?.Parent is not ContextMenu { PlacementTarget: FrameworkElement placementTarget })
            return;
        if (FindAncestor<TreeView>(placementTarget) is not { DataContext: ProfileViewModel profile } treeView)
            return;

        try
        {
            var dialog = new MoveFolderDialog(profile, node) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            treeView.IsEnabled = false;
            try
            {
                await profile.MoveFolderAsync(node.Folder.Id, dialog.SelectedFolderId);
            }
            finally
            {
                treeView.IsEnabled = true;
            }

            // FolderTree.Build (inside profile.MoveFolderAsync) just replaced every node in
            // this tree -- reset unconditionally rather than replicating
            // ConfirmAndDeleteFolderAsync's subtree check: moving the folder that happens to
            // be selected is the single most common reason to open "Mover pasta..." in the
            // first place, so the check would almost always fire anyway.
            if (ReferenceEquals(_selectedProfile, profile))
            {
                _selectedNode = null;
                _selectedTreeView = null;
                ScavengeButton.Visibility = Visibility.Collapsed;
                RecoverableBar.Visibility = Visibility.Collapsed;
                NavigateToRenderedMessage(string.Empty);
                ImagesBlockedBar.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LEC — Não foi possível mover a pasta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
```

- [ ] **Step 4: Build and run the full suite**

Run: `dotnet build`
Expected: builds cleanly.

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 5: Manual verification**

Run the app (`dotnet run --project src/LEC.App`) against a real or test profile with at least one multi-level folder (a folder with a subfolder that itself has messages) and check:

1. Right-click a special folder (e.g. "Caixa de entrada") — confirm neither "Apagar pasta" nor "Mover pasta..." appears in the menu.
2. Right-click an ordinary folder with subfolders and messages, choose "Apagar pasta", click "Não" on the confirmation — confirm nothing changes.
3. Repeat, click "Sim" — confirm the folder and all its subfolders disappear from the tree, and (if it was the folder on screen) the message list and reading pane clear.
4. Close and reopen the profile (File → Abrir Perfil... on the same directory) — confirm the deleted folder stays gone.
5. Right-click an ordinary folder, choose "Mover pasta...", confirm the folder itself and its own subtree are grayed out in the dialog and never enable the "Mover" button, pick a valid destination, click "Mover" — confirm the folder (with its subfolders) now appears under the new parent.
6. Try moving a folder into one of its own descendants by picking a grayed-out node — confirm "Mover" never enables for it.
7. Choose "Pastas locais (raiz)" as a destination — confirm the folder becomes a top-level node.

- [ ] **Step 6: Commit**

```bash
git add src/LEC.App/Converters/SpecialToVisibilityConverter.cs src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs
git commit -m "Wire delete/move-folder UI: context menu items on the folder tree"
```

---

## Notes

- **Task ordering:** Task 1 has no prerequisite (it only touches `LEC.MailStore`/`LEC.App.Tests`, and the motor slice's `DbxStore` methods already exist). Task 2 depends on Task 1 (`IMailStore` must expose both methods before `ProfileViewModel` can call them polymorphically). Task 3 has no dependency on Tasks 1-2 (it only touches `FolderNodeViewModel`, already shipped) and could run in parallel with them, but is sequenced after for a simpler linear review. Task 4 depends on Task 3 (the dialog uses the converter). Task 5 depends on Task 2 (calls `ProfileViewModel.DeleteFolderAsync`/`MoveFolderAsync`) and Task 4 (opens `MoveFolderDialog`).
