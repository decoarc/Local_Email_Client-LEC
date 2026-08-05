# Mover pasta entre perfis — UI — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user pick a destination folder in ANY open profile (not just the source folder's own profile) from the "Mover pasta..." dialog, and have that choice reach the already-shipped `MainWindowViewModel.MoveFolderAsync` (motor slice), which transparently does the cheap same-profile move or the new cross-profile copy-then-hide depending on what was picked.

**Architecture:** `MoveFolderDestinationRules.IsValidDestination` gains a profile parameter on both sides (candidate and source) — a folder in a different profile is always a valid destination; a folder in the same profile still follows the existing self/descendant exclusion. `MoveFolderDestinationValidityConverter` becomes an `IMultiValueConverter` (was single-value) so XAML can pass both the candidate node and its owning profile, mirroring the existing `MoveDestinationValidityConverter` used for messages. `MoveFolderDialog` is rewritten to show one `TreeView` per open profile (via an `ItemsControl`, same shape as `MoveMessageDialog`), each with its own "Pastas locais (raiz)" pseudo-destination, and now exposes both `SelectedProfile` and `SelectedFolderId`. `MainWindow.xaml.cs`'s `MoveFolder_Click` passes all open profiles into the dialog and calls the new `MainWindowViewModel.MoveFolderAsync` instead of `ProfileViewModel.MoveFolderAsync` directly — no branching logic added here, since the motor-slice method already picked the right path internally.

**Tech Stack:** .NET 9, C#, WPF, xUnit.

## Global Constraints

- A folder in a different profile than the source is *always* a valid destination, regardless of its position in that other profile's tree — the self/descendant exclusion only ever applies within the source's own profile.
- Each open profile's section in the dialog gets its own "Pastas locais (raiz)" label — not one global root option — since root-of-profile-A and root-of-profile-B are different destinations.
- `MoveFolder_Click`'s post-move selection reset must fire if `_selectedProfile` is either the source profile *or* the destination profile — a cross-profile move rebuilds both profiles' `FolderTree`s (the source's via `DeleteFolderAsync`, the destination's via one or more `CreateFolderAsync` calls), so a stale `_selectedNode` could belong to either one.
- No dedicated automated test for `MoveFolderDialog`'s code-behind or `MainWindow.xaml.cs`'s handler — same established precedent as every other `MainWindow.xaml.cs` handler. `MoveFolderDestinationRules` (pure logic) does get full test coverage.
- **`MoveFolderDestinationRules.IsValidDestination`'s signature and `MoveFolderDialog`'s constructor/call-site must change together, in the same task.** `MoveFolderDialog.xaml.cs` is the only caller of `IsValidDestination`, and `MoveFolderDialog.xaml`'s `MultiBinding` is the only consumer of the converter — splitting the rules/converter change from the dialog rewrite into separate tasks would leave the whole solution (including `LEC.App.Tests`, which depends on the `LEC.App` project building) failing to compile between them. Task 1 below does both at once for exactly this reason.
- Reference: `docs/superpowers/specs/2026-08-05-mover-pasta-entre-perfis-design.md` §4-5 (full design this plan implements) and `docs/superpowers/plans/2026-08-05-mover-pasta-entre-perfis-motor.md` (the `MainWindowViewModel.MoveFolderAsync` this plan's Task 2 wires up, already shipped).

---

## File Structure

```
src/LEC.App/Services/
  MoveFolderDestinationRules.cs           MODIFIED — Task 1: IsValidDestination gains profile params

src/LEC.App/Converters/
  MoveFolderDestinationValidityConverter.cs   MODIFIED — Task 1: IValueConverter -> IMultiValueConverter

src/LEC.App/
  MoveFolderDialog.xaml                   MODIFIED — Task 1: one TreeView+root-label per open profile
  MoveFolderDialog.xaml.cs                MODIFIED — Task 1: +SelectedProfile, ctor takes IEnumerable<ProfileViewModel>
  MainWindow.xaml.cs                      MODIFIED — Task 2: MoveFolder_Click passes OpenProfiles, calls MainWindowViewModel.MoveFolderAsync, reset checks both profiles

tests/LEC.App.Tests/
  MoveFolderDestinationRulesTests.cs       MODIFIED — Task 1: rewritten for the new signature + cross-profile cases
```

---

### Task 1: `MoveFolderDestinationRules`/converter/`MoveFolderDialog` (multi-profile)

**Files:**
- Modify: `src/LEC.App/Services/MoveFolderDestinationRules.cs`
- Modify: `src/LEC.App/Converters/MoveFolderDestinationValidityConverter.cs`
- Modify: `src/LEC.App/MoveFolderDialog.xaml`
- Modify: `src/LEC.App/MoveFolderDialog.xaml.cs`
- Modify: `tests/LEC.App.Tests/MoveFolderDestinationRulesTests.cs`

**Interfaces:**
- Consumes: `FolderNodeViewModel.Children`/`.Folder`/`.DisplayName` (existing), `ProfileViewModel.FolderTree` (existing, used both for reference-identity comparison and for binding each section's tree).
- Produces: `MoveFolderDestinationRules.IsValidDestination(FolderNodeViewModel candidate, ProfileViewModel candidateProfile, FolderNodeViewModel sourceNode, ProfileViewModel sourceProfile) : bool` (signature change — was `(candidate, sourceNode)`); `MoveFolderDialog(IEnumerable<ProfileViewModel> openProfiles, ProfileViewModel sourceProfile, FolderNodeViewModel sourceNode)` (signature change — was `(ProfileViewModel profile, FolderNodeViewModel sourceNode)`) with `.SelectedProfile : ProfileViewModel?` (new, alongside the existing `.SelectedFolderId : int?`). Both consumed by Task 2's `MainWindow.xaml.cs`.

- [ ] **Step 1: Rewrite the failing tests**

Replace `tests/LEC.App.Tests/MoveFolderDestinationRulesTests.cs` in full:

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

    private static ProfileViewModel MakeProfile(string name) =>
        new(name, $@"C:\perfis\{name}", new FakeMailStore(folders: Array.Empty<StoreFolder>()));

    [Fact]
    public void TheSourceFolderItselfIsInvalid()
    {
        var profile = MakeProfile("P");
        var source = Node(1);

        Assert.False(MoveFolderDestinationRules.IsValidDestination(source, profile, source, profile));
    }

    [Fact]
    public void ADirectChildOfTheSourceInTheSameProfileIsInvalid()
    {
        var profile = MakeProfile("P");
        var source = Node(1);
        var child = Node(2);
        source.Children.Add(child);

        Assert.False(MoveFolderDestinationRules.IsValidDestination(child, profile, source, profile));
    }

    [Fact]
    public void AGrandchildOfTheSourceInTheSameProfileIsInvalid()
    {
        var profile = MakeProfile("P");
        var source = Node(1);
        var child = Node(2);
        var grandchild = Node(3);
        child.Children.Add(grandchild);
        source.Children.Add(child);

        Assert.False(MoveFolderDestinationRules.IsValidDestination(grandchild, profile, source, profile));
    }

    [Fact]
    public void AnUnrelatedFolderInTheSameProfileIsValid()
    {
        var profile = MakeProfile("P");
        var source = Node(1);
        var other = Node(2);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(other, profile, source, profile));
    }

    [Fact]
    public void TheSourcesCurrentParentInTheSameProfileIsValid()
    {
        // Moving a folder back under its own current parent is a harmless no-op reorder,
        // not a cycle -- the parent is not the source and not one of its descendants.
        var profile = MakeProfile("P");
        var parent = Node(1);
        var source = Node(2);
        parent.Children.Add(source);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(parent, profile, source, profile));
    }

    [Fact]
    public void ASiblingOfTheSourceInTheSameProfileIsValid()
    {
        var profile = MakeProfile("P");
        var parent = Node(1);
        var source = Node(2);
        var sibling = Node(3);
        parent.Children.Add(source);
        parent.Children.Add(sibling);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(sibling, profile, source, profile));
    }

    [Fact]
    public void AFolderInADifferentProfileIsAlwaysValidEvenIfItWouldBeADescendantInThatProfilesOwnTree()
    {
        // candidate is a "child" of source in this test's tree structure -- but candidateProfile
        // differs from sourceProfile, so the two trees are unrelated and the self/descendant
        // check must never even run. A folder in a different profile is never part of the
        // source's subtree by definition, regardless of what its own local tree looks like.
        var sourceProfile = MakeProfile("Origem");
        var otherProfile = MakeProfile("Destino");
        var source = Node(1);
        var candidateThatLooksLikeAChild = Node(2);
        source.Children.Add(candidateThatLooksLikeAChild);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(candidateThatLooksLikeAChild, otherProfile, source, sourceProfile));
    }

    [Fact]
    public void TheSameNodeReferenceInADifferentProfileIsValid()
    {
        // Degenerate case: same FolderNodeViewModel reference as the source, but tagged with a
        // different profile in this call -- profile mismatch wins over reference equality,
        // since a real dialog can never actually produce this combination (a node only ever
        // renders inside one profile's tree), but the rule itself must not special-case it
        // into a false "self" match.
        var sourceProfile = MakeProfile("Origem");
        var otherProfile = MakeProfile("Destino");
        var source = Node(1);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(source, otherProfile, source, sourceProfile));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter "MoveFolderDestinationRulesTests"`
Expected: FAIL to compile — `MoveFolderDestinationRules.IsValidDestination` doesn't take profile parameters yet.

- [ ] **Step 3: Update `MoveFolderDestinationRules`**

Replace `src/LEC.App/Services/MoveFolderDestinationRules.cs` in full:

```csharp
// src/LEC.App/Services/MoveFolderDestinationRules.cs
using LEC.App.ViewModels;

namespace LEC.App.Services;

/// <summary>
/// Whether a folder node is a legal destination for MainWindowViewModel.MoveFolderAsync,
/// checked upfront so MoveFolderDialog can gray out invalid folders instead of letting the
/// user pick one and fail after the fact. A folder in a different profile than the source is
/// always valid -- it can never be part of the source's own subtree by definition, regardless
/// of its position in that other profile's tree. Within the source's own profile, the
/// existing self/descendant exclusion still applies, walking the already-built
/// FolderNodeViewModel tree (not DbxStore.Folders' flat ParentId list) because the dialog
/// already has that tree in memory for every open profile.
/// This does NOT replace the motor's own guards (DbxStore.MoveFolderAsync's cycle/name-
/// collision checks, MainWindowViewModel.MoveFolderAsync's special-folder guard) -- those
/// still run for real when the move actually happens and surface as normal errors here.
/// </summary>
public static class MoveFolderDestinationRules
{
    public static bool IsValidDestination(FolderNodeViewModel candidate, ProfileViewModel candidateProfile,
        FolderNodeViewModel sourceNode, ProfileViewModel sourceProfile)
    {
        if (!ReferenceEquals(candidateProfile, sourceProfile))
            return true;

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
Expected: FAIL to compile — this is expected and correct at this point: `MoveFolderDialog.xaml.cs` (Step 6 below) still calls the OLD 2-argument `IsValidDestination(node, _sourceNode)`, so the whole `LEC.App` project — and therefore `LEC.App.Tests`, which references it — won't build yet. Do not stop here or treat this as a failure; proceed straight to Step 5.

- [ ] **Step 5: Update `MoveFolderDestinationValidityConverter`**

Replace `src/LEC.App/Converters/MoveFolderDestinationValidityConverter.cs` in full:

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
/// MoveDestinationValidityConverter (IsEnabled cascades to every descendant in WPF). Now a
/// multi-value converter (was single-value) so XAML can supply both the candidate node and
/// the ProfileViewModel of the TreeView it renders in -- needed since validity now depends on
/// whether the candidate's profile matches the source's. SourceNode/SourceProfile are set
/// once by MoveFolderDialog's constructor, before any of its TreeViews first bind.
/// </summary>
public sealed class MoveFolderDestinationValidityConverter : IMultiValueConverter
{
    public FolderNodeViewModel? SourceNode { get; set; }
    public ProfileViewModel? SourceProfile { get; set; }

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (SourceNode is null || SourceProfile is null
            || values is not [FolderNodeViewModel candidate, ProfileViewModel candidateProfile])
        {
            return Brushes.Gray;
        }

        bool isValid = MoveFolderDestinationRules.IsValidDestination(candidate, candidateProfile, SourceNode, SourceProfile);
        return isValid ? Brushes.Black : Brushes.Gray;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 6: Rewrite `MoveFolderDialog.xaml`**

Replace `src/LEC.App/MoveFolderDialog.xaml` in full:

```xml
<!-- src/LEC.App/MoveFolderDialog.xaml -->
<Window x:Class="LEC.App.MoveFolderDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:conv="clr-namespace:LEC.App.Converters"
        Title="Mover pasta" Width="420" Height="480"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False">
    <Window.Resources>
        <conv:MoveFolderDestinationValidityConverter x:Key="ValidityConverter"/>
    </Window.Resources>
    <DockPanel Margin="12">
        <TextBlock DockPanel.Dock="Top" Text="Escolha a pasta de destino:" Margin="0,0,0,8"/>
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Cancelar" IsCancel="True" Width="80" Margin="0,0,8,0"/>
            <Button x:Name="MoveButton" Content="Mover" IsDefault="True" Width="80" IsEnabled="False" Click="MoveButton_Click"/>
        </StackPanel>
        <ScrollViewer VerticalScrollBarVisibility="Auto">
            <ItemsControl x:Name="ProfilesItemsControl">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <StackPanel Margin="0,0,0,8">
                            <TextBlock Text="{Binding DisplayName}" FontWeight="Bold" Margin="0,0,0,2"/>
                            <TextBlock Text="Pastas locais (raiz)" Margin="0,0,0,4" Cursor="Hand"
                                       MouseLeftButtonUp="RootLabel_MouseLeftButtonUp"/>
                            <TreeView ItemsSource="{Binding FolderTree.RootNodes}"
                                      SelectedItemChanged="TreeView_SelectedItemChanged"
                                      BorderThickness="0"
                                      ScrollViewer.VerticalScrollBarVisibility="Disabled"
                                      ScrollViewer.HorizontalScrollBarVisibility="Disabled">
                                <TreeView.ItemTemplate>
                                    <HierarchicalDataTemplate ItemsSource="{Binding Children}">
                                        <TextBlock Text="{Binding DisplayName}">
                                            <TextBlock.Foreground>
                                                <MultiBinding Converter="{StaticResource ValidityConverter}">
                                                    <Binding Path="."/>
                                                    <Binding Path="DataContext" RelativeSource="{RelativeSource AncestorType=TreeView}"/>
                                                </MultiBinding>
                                            </TextBlock.Foreground>
                                        </TextBlock>
                                    </HierarchicalDataTemplate>
                                </TreeView.ItemTemplate>
                            </TreeView>
                        </StackPanel>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>
    </DockPanel>
</Window>
```

- [ ] **Step 7: Rewrite `MoveFolderDialog.xaml.cs`**

Replace `src/LEC.App/MoveFolderDialog.xaml.cs` in full:

```csharp
// src/LEC.App/MoveFolderDialog.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LEC.App.Converters;
using LEC.App.Services;
using LEC.App.ViewModels;

namespace LEC.App;

public partial class MoveFolderDialog : Window
{
    private readonly ProfileViewModel _sourceProfile;
    private readonly FolderNodeViewModel _sourceNode;

    public ProfileViewModel? SelectedProfile { get; private set; }
    public int? SelectedFolderId { get; private set; }

    public MoveFolderDialog(IEnumerable<ProfileViewModel> openProfiles, ProfileViewModel sourceProfile, FolderNodeViewModel sourceNode)
    {
        InitializeComponent();

        _sourceProfile = sourceProfile;
        _sourceNode = sourceNode;

        var converter = (MoveFolderDestinationValidityConverter)Resources["ValidityConverter"];
        converter.SourceNode = sourceNode;
        converter.SourceProfile = sourceProfile;

        ProfilesItemsControl.ItemsSource = openProfiles;
    }

    // Each open profile's own "Pastas locais (raiz)" label is always a valid destination for
    // that profile: root is nobody's descendant, and a no-op "already at root, same profile"
    // move is a harmless reorder, not an error. Clicking this label doesn't touch any
    // TreeView.SelectedItem, so there's no risk of TreeView_SelectedItemChanged firing and
    // overwriting this choice right after.
    private void RootLabel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { DataContext: ProfileViewModel profile })
            return;

        SelectedProfile = profile;
        SelectedFolderId = null;
        MoveButton.IsEnabled = true;
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is not TreeView { DataContext: ProfileViewModel profile } || e.NewValue is not FolderNodeViewModel node)
            return;

        bool isValid = MoveFolderDestinationRules.IsValidDestination(node, profile, _sourceNode, _sourceProfile);
        if (isValid)
        {
            SelectedProfile = profile;
            SelectedFolderId = node.Folder.Id;
        }
        else
        {
            SelectedProfile = null;
            SelectedFolderId = null;
        }

        MoveButton.IsEnabled = isValid;
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter "MoveFolderDestinationRulesTests"`
Expected: PASS — all 8 tests. The solution now compiles again (Steps 3-7 together restored a consistent state).

- [ ] **Step 9: Run the full suite**

Run: `dotnet build`
Expected: builds cleanly — `MoveFolderDialog`'s only caller, `MainWindow.xaml.cs`'s `MoveFolder_Click`, still uses the OLD `(ProfileViewModel, FolderNodeViewModel)` constructor and treats the result as `int?`-only at this point in the plan. **This build is expected to FAIL in `MainWindow.xaml.cs` specifically** — that call site is fixed in Task 2, which must run immediately after this one. Confirm the *only* errors reported are in `MainWindow.xaml.cs` referencing `MoveFolderDialog`'s constructor/members; if any other file fails to build, stop and investigate before committing.

Run: `dotnet test tests/LEC.App.Tests --filter "MoveFolderDestinationRulesTests"`
Expected: PASS regardless of `MainWindow.xaml.cs`'s state — `LEC.App.Tests`' compiled output only needs the `LEC.App` *library* assembly, and `MainWindow.xaml.cs`'s executable entry point failing to build does not prevent the rest of `LEC.App`'s classes (including `MoveFolderDialog`/`MoveFolderDestinationRules`) from compiling into that assembly. If this does NOT pass, `MoveFolderDialog`/`MoveFolderDestinationRules` themselves have an error — fix that before proceeding, regardless of `MainWindow.xaml.cs`.

- [ ] **Step 10: Commit**

```bash
git add src/LEC.App/Services/MoveFolderDestinationRules.cs src/LEC.App/Converters/MoveFolderDestinationValidityConverter.cs src/LEC.App/MoveFolderDialog.xaml src/LEC.App/MoveFolderDialog.xaml.cs tests/LEC.App.Tests/MoveFolderDestinationRulesTests.cs
git commit -m "Rewrite MoveFolderDialog and its validity rules for cross-profile destinations"
```

---

### Task 2: `MoveFolder_Click` (multi-profile wiring)

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `MoveFolderDialog(IEnumerable<ProfileViewModel>, ProfileViewModel, FolderNodeViewModel)`/`.SelectedProfile`/`.SelectedFolderId` (Task 1), `MainWindowViewModel.MoveFolderAsync(ProfileViewModel, FolderNodeViewModel, ProfileViewModel, int?, CancellationToken)` (motor plan, already shipped), `MainWindowViewModel.ClearCurrentFolder()` (existing).
- Produces: user-visible cross-profile folder move via the existing "Mover pasta..." menu item — no new public members.

No dedicated automated test — same established precedent as every other `MainWindow.xaml.cs` handler. This task ends with a manual verification checklist.

- [ ] **Step 1: Update `MoveFolder_Click`**

In `src/LEC.App/MainWindow.xaml.cs`, replace the entire `MoveFolder_Click` method:

```csharp
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
            var dialog = new MoveFolderDialog(_viewModel.OpenProfiles, profile, node) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedProfile is not { } destinationProfile)
                return;

            treeView.IsEnabled = false;
            try
            {
                await _viewModel.MoveFolderAsync(profile, node, destinationProfile, dialog.SelectedFolderId);
            }
            finally
            {
                treeView.IsEnabled = true;
            }

            // A cross-profile move rebuilds BOTH profiles' FolderTrees (the source's via
            // DeleteFolderAsync, the destination's via CreateFolderAsync) -- reset unconditionally
            // whenever the currently displayed profile is either one, not just the source, since
            // a same-profile move already rebuilds only that one tree either way.
            if (ReferenceEquals(_selectedProfile, profile) || ReferenceEquals(_selectedProfile, destinationProfile))
            {
                _selectedNode = null;
                _selectedTreeView = null;
                ScavengeButton.Visibility = Visibility.Collapsed;
                RecoverableBar.Visibility = Visibility.Collapsed;
                NavigateToRenderedMessage(string.Empty);
                ImagesBlockedBar.Visibility = Visibility.Collapsed;
                _viewModel.ClearCurrentFolder();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LEC — Não foi possível mover a pasta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
```

This replaces the previous body, which called `profile.MoveFolderAsync(node.Folder.Id, dialog.SelectedFolderId)` directly and only checked `ReferenceEquals(_selectedProfile, profile)` for the reset. The new version calls `_viewModel.MoveFolderAsync` (the motor plan's new unified entry point, which internally delegates to the cheap same-profile path when `destinationProfile` equals `profile`) and checks both profiles for the reset, plus calls `_viewModel.ClearCurrentFolder()` (added because a cross-profile move can leave the message list showing folder contents from a profile whose tree just changed, same reasoning already applied to the delete-folder and same-profile move handlers in the previous slice).

- [ ] **Step 2: Build and run the full suite**

Run: `dotnet build`
Expected: builds cleanly now (this was the call site left broken at the end of Task 1).

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 3: Manual verification**

Run the app (`dotnet run --project src/LEC.App`) with at least two open profiles, one of them having a folder with a subfolder and at least one message, and check:

1. Right-click that folder, choose "Mover pasta..." — confirm the dialog now shows a section per open profile, each with its own "Pastas locais (raiz)" label and tree.
2. Confirm the source folder and its subtree are grayed out (never enable "Mover") in the source profile's own section, while every folder in the OTHER profile's section is selectable (not grayed out), including ones that would "look like" a descendant if compared naively.
3. Pick a destination in the other profile and click "Mover" — confirm the folder (with its subpasta e mensagem) now appears under that destination in the other profile's tree, and disappears from the original profile's tree.
4. If the source folder had a recoverable/scavenged item showing (from a prior "Procurar mensagens recuperáveis"), confirm it does NOT appear in the destination folder after the move.
5. Try the same move again into a folder that already has a same-named child in the destination profile — confirm it fails with a clear error and the source folder is still there, untouched.
6. Move a folder within the SAME profile (as before this change) — confirm it still works exactly as before (fast path, no duplicate created).

- [ ] **Step 4: Commit**

```bash
git add src/LEC.App/MainWindow.xaml.cs
git commit -m "Wire cross-profile folder move: MoveFolder_Click uses MainWindowViewModel.MoveFolderAsync"
```

---

## Notes

- **Task ordering:** Task 1 has no prerequisite, but its internal steps must run in the exact order given (rules → converter → dialog XAML → dialog code-behind) to keep `LEC.App.Tests` buildable at Step 8 despite `MainWindow.xaml.cs` being intentionally left broken until Task 2. Task 2 depends on Task 1 (`MoveFolderDialog`'s new constructor/properties) and on the motor plan's Task 1 (`MainWindowViewModel.MoveFolderAsync`), which is already shipped.
