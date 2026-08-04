# Mover mensagem — UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user move a message to another folder (same or a different open profile) from the main window, via a "Mover para..." context-menu item on the message list and a destination-picker dialog — wiring the already-built, already-tested `MainWindowViewModel.MoveMessageAsync` to the screen.

**Architecture:** `StoreFolder` gains a `HasBackingFile` field (same pattern as the existing `IsLoadable`), computed once in `DbxStore.Open`. A new pure static helper (`MoveDestinationRules.IsValidDestination`) decides whether a folder is a legal move target; a thin `IMultiValueConverter` (`MoveDestinationValidityConverter`) adapts that helper to XAML so a new `MoveMessageDialog` window can disable invalid `TreeViewItem`s via `ItemContainerStyle`, reusing the exact tree markup already in `MainWindow.xaml`. `MessageListView` gets a per-item context menu that opens the dialog and, on confirmation, calls the existing `MoveMessageAsync`.

**Tech Stack:** .NET 9, C#, WPF, xUnit.

## Global Constraints

- Menu de contexto na lista de mensagens é o único gatilho desta fatia (sem drag-and-drop).
- O diálogo de destino mostra as árvores de **todos** os perfis abertos (`MainWindowViewModel.OpenProfiles`), não só o do perfil de origem.
- Pastas inválidas como destino (sem arquivo de apoio, não carregável, ou a própria pasta de origem da mensagem) ficam **desabilitadas** no diálogo — nunca chegam a `MoveMessageAsync`.
- Pasta de destino danificada ou já com 255 mensagens **não** é pré-validada — só aparece como erro (`MessageBox`) se `MoveMessageAsync` lançar.
- Escolher a pasta e clicar em "Mover" no próprio diálogo já é a confirmação — sem popup extra de "tem certeza?".
- Sucesso: recarrega a lista da pasta atualmente selecionada e limpa o painel de leitura (mesmo reset de `CloseProfile_Click`: `NavigateToRenderedMessage(string.Empty)` + colapsar `ImagesBlockedBar`).
- Seleção múltipla de mensagens está fora de escopo — a lista continua seleção única.
- Nenhuma mudança em `MainWindowViewModel.MoveMessageAsync`/`IMailStore.AddMessageAsync`/`HideMovedMessageAsync` — essa lógica já existe e já foi testada de ponta a ponta na fatia do motor.

---

## File Structure

```
src/LEC.MailStore/
  StoreFolder.cs                 MODIFIED — Task 1: +HasBackingFile
  DbxStore.cs                    MODIFIED — Task 1: popula HasBackingFile em Open

src/LEC.App/Services/
  MoveDestinationRules.cs        NEW — Task 2: regra pura de destino válido

src/LEC.App/Converters/
  MoveDestinationValidityConverter.cs   NEW — Task 3: adapta a regra pra XAML

src/LEC.App/
  MoveMessageDialog.xaml         NEW — Task 3
  MoveMessageDialog.xaml.cs      NEW — Task 3
  MainWindow.xaml                MODIFIED — Task 4: menu de contexto por item na lista
  MainWindow.xaml.cs             MODIFIED — Task 4: MoveMessage_Click

tests/LEC.MailStore.Tests/
  DbxStoreTests.cs               MODIFIED — Task 1: +asserts de HasBackingFile

tests/LEC.App.Tests/
  FakeMailStore.cs                    MODIFIED — Task 1: +HasBackingFile nos StoreFolder existentes
  FolderTreeViewModelTests.cs         MODIFIED — Task 1
  MainWindowViewModelTests.cs         MODIFIED — Task 1
  MessageListViewModelTests.cs        MODIFIED — Task 1
  ProfileViewModelTests.cs            MODIFIED — Task 1
  MoveDestinationRulesTests.cs        NEW — Task 2
```

`MainWindow.xaml`'s `TreeView`/`HierarchicalDataTemplate` markup is duplicated (not shared) into `MoveMessageDialog.xaml` — same precedent already established between `DbxFolderFileWriter`/`DbxEmailFileWriter` in the motor slice (deliberate duplication over a shared control, since the two trees render different `ItemContainerStyle` behavior: one just colors text, the other disables selection).

---

### Task 1: `StoreFolder.HasBackingFile`

**Files:**
- Modify: `src/LEC.MailStore/StoreFolder.cs`
- Modify: `src/LEC.MailStore/DbxStore.cs`
- Modify: `tests/LEC.MailStore.Tests/DbxStoreTests.cs`
- Modify: `tests/LEC.App.Tests/FakeMailStore.cs`, `FolderTreeViewModelTests.cs`, `MainWindowViewModelTests.cs`, `MessageListViewModelTests.cs`, `ProfileViewModelTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `StoreFolder.HasBackingFile : bool` (required property). Consumed by Task 2.

- [ ] **Step 1: Write the failing test**

In `tests/LEC.MailStore.Tests/DbxStoreTests.cs`, the existing `OpensAProfileAndMapsFolders` test already builds one folder with no `FileName` attribute ("Pastas locais") and one folder with a `FileName` attribute that doesn't exist on disk ("Caixa de entrada" / `Inbox.dbx`) — exactly the two cases `HasBackingFile` needs to distinguish. Add two assertions to that existing test (don't write a new test method), right after the existing `IsLoadable` assertions:

```csharp
        var pastasLocais = store.Folders.Single(f => f.Id == 1);
        Assert.Null(pastasLocais.ParentId); // -1 sentinel maps to null
        Assert.Equal("Pastas locais", pastasLocais.Name);
        Assert.True(pastasLocais.IsLoadable); // no backing file at all — trivially "loadable" (nothing to load)
        Assert.False(pastasLocais.HasBackingFile); // no FileName attribute — a placeholder folder

        var inbox = store.Folders.Single(f => f.Id == 2);
        Assert.Equal(1, inbox.ParentId);
        Assert.Equal("Caixa de entrada", inbox.Name);
        Assert.Equal(SpecialFolderKind.Inbox, inbox.SpecialFolderKind);
        Assert.Equal(5, inbox.MessageCount);
        Assert.False(inbox.IsLoadable); // Inbox.dbx does not exist on disk
        Assert.True(inbox.HasBackingFile); // has a FileName attribute, even though the file is missing
```

The last two lines of each block above are the new assertions — `HasBackingFile` and `IsLoadable` disagree on the "Caixa de entrada" folder on purpose: it proves the two are independent signals (a folder can have a `FileName` attribute recorded, i.e. `HasBackingFile == true`, while the file itself is missing from disk, i.e. `IsLoadable == false`).

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "OpensAProfileAndMapsFolders"`
Expected: FAIL to compile — `StoreFolder.HasBackingFile` doesn't exist yet.

- [ ] **Step 3: Add `HasBackingFile` to `StoreFolder`**

In `src/LEC.MailStore/StoreFolder.cs`, replace:

```csharp
    /// <summary>
    /// Cheap, upfront signal (backing file exists on disk, or there is no
    /// backing file at all) — not a guarantee the file will actually parse
    /// successfully; EnumerateHeadersAsync degrades gracefully either way.
    /// </summary>
    public required bool IsLoadable { get; init; }
}
```

with:

```csharp
    /// <summary>
    /// Cheap, upfront signal (backing file exists on disk, or there is no
    /// backing file at all) — not a guarantee the file will actually parse
    /// successfully; EnumerateHeadersAsync degrades gracefully either way.
    /// </summary>
    public required bool IsLoadable { get; init; }

    /// <summary>
    /// True if this folder has a FileName attribute at all — independent of whether that
    /// file exists on disk (that's IsLoadable) or is healthy (that's DbxStore.GetFolderHealth).
    /// False for purely organizational placeholder folders (e.g. "Pastas locais"), which
    /// AddMessageAsync always rejects. Used by LEC.App to gray out invalid move destinations
    /// before the user can pick one, not just after AddMessageAsync throws.
    /// </summary>
    public required bool HasBackingFile { get; init; }
}
```

- [ ] **Step 4: Populate `HasBackingFile` in `DbxStore.Open`**

In `src/LEC.MailStore/DbxStore.cs`, replace:

```csharp
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
```

with:

```csharp
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
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/LEC.MailStore.Tests --filter "OpensAProfileAndMapsFolders"`
Expected: PASS.

- [ ] **Step 6: Fix every other `new StoreFolder { ... }` that now fails to compile**

`HasBackingFile` is `required`, so every other place that constructs a `StoreFolder` object-initializer-style now fails to compile. None of these tests are about the "no backing file" placeholder case — every one of them represents a folder with a real message file — so add `HasBackingFile = true,` right after the existing `IsLoadable = ...,` line in each of the following (search each file for `new StoreFolder {` to find every occurrence — there is more than one per file in most of these):

- `tests/LEC.App.Tests/FakeMailStore.cs`
- `tests/LEC.App.Tests/FolderTreeViewModelTests.cs`
- `tests/LEC.App.Tests/MainWindowViewModelTests.cs`
- `tests/LEC.App.Tests/MessageListViewModelTests.cs`
- `tests/LEC.App.Tests/ProfileViewModelTests.cs`

For example, in `tests/LEC.App.Tests/MessageListViewModelTests.cs`, a line like:

```csharp
        var folder = new StoreFolder { Id = 1, Name = "Quebrada", IsLoadable = false };
```

becomes:

```csharp
        var folder = new StoreFolder { Id = 1, Name = "Quebrada", IsLoadable = false, HasBackingFile = true };
```

(`IsLoadable = false` there means "the file is listed but missing from disk" — that folder still *has* a backing file on record, so `HasBackingFile` stays `true`; only `IsLoadable` is `false`.)

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS — every project compiles and passes, no regressions.

- [ ] **Step 8: Commit**

```bash
git add src/LEC.MailStore/StoreFolder.cs src/LEC.MailStore/DbxStore.cs tests/LEC.MailStore.Tests/DbxStoreTests.cs tests/LEC.App.Tests/FakeMailStore.cs tests/LEC.App.Tests/FolderTreeViewModelTests.cs tests/LEC.App.Tests/MainWindowViewModelTests.cs tests/LEC.App.Tests/MessageListViewModelTests.cs tests/LEC.App.Tests/ProfileViewModelTests.cs
git commit -m "Add StoreFolder.HasBackingFile: distinguishes placeholder folders from real message folders"
```

---

### Task 2: `MoveDestinationRules`

**Files:**
- Create: `src/LEC.App/Services/MoveDestinationRules.cs`
- Test: `tests/LEC.App.Tests/MoveDestinationRulesTests.cs`

**Interfaces:**
- Consumes: `StoreFolder.HasBackingFile`/`.IsLoadable`/`.Id` (Task 1), `ProfileViewModel` (existing).
- Produces: `MoveDestinationRules.IsValidDestination(StoreFolder folder, ProfileViewModel profile, ProfileViewModel sourceProfile, int sourceFolderId) : bool`. Consumed by Task 3.

- [ ] **Step 1: Write the failing tests**

Create `tests/LEC.App.Tests/MoveDestinationRulesTests.cs`:

```csharp
// tests/LEC.App.Tests/MoveDestinationRulesTests.cs
using LEC.App.Services;
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class MoveDestinationRulesTests
{
    private static ProfileViewModel MakeProfile(string name, params StoreFolder[] folders) =>
        new(name, $@"C:\perfis\{name}", new FakeMailStore(folders: folders));

    [Fact]
    public void FolderWithoutBackingFileIsInvalid()
    {
        var folder = new StoreFolder { Id = 1, Name = "Pastas locais", IsLoadable = true, HasBackingFile = false };
        var profile = MakeProfile("P", folder);
        var sourceProfile = MakeProfile("Origem", new StoreFolder { Id = 99, Name = "Origem", IsLoadable = true, HasBackingFile = true });

        Assert.False(MoveDestinationRules.IsValidDestination(folder, profile, sourceProfile, sourceFolderId: 99));
    }

    [Fact]
    public void FolderThatIsNotLoadableIsInvalid()
    {
        var folder = new StoreFolder { Id = 1, Name = "Quebrada", IsLoadable = false, HasBackingFile = true };
        var profile = MakeProfile("P", folder);
        var sourceProfile = MakeProfile("Origem", new StoreFolder { Id = 99, Name = "Origem", IsLoadable = true, HasBackingFile = true });

        Assert.False(MoveDestinationRules.IsValidDestination(folder, profile, sourceProfile, sourceFolderId: 99));
    }

    [Fact]
    public void TheMessagesOwnSourceFolderInTheSameProfileIsInvalid()
    {
        var sourceFolder = new StoreFolder { Id = 5, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var profile = MakeProfile("P", sourceFolder);

        Assert.False(MoveDestinationRules.IsValidDestination(sourceFolder, profile, profile, sourceFolderId: 5));
    }

    [Fact]
    public void AFolderWithTheSameIdInADifferentProfileIsValid()
    {
        // Folder IDs are only unique within one profile's Folders.dbx -- two different open
        // profiles can (and often will) both have a folder with Id == 5.
        var sourceFolder = new StoreFolder { Id = 5, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var sourceProfile = MakeProfile("Origem", sourceFolder);

        var otherFolder = new StoreFolder { Id = 5, Name = "Destino", IsLoadable = true, HasBackingFile = true };
        var otherProfile = MakeProfile("Destino", otherFolder);

        Assert.True(MoveDestinationRules.IsValidDestination(otherFolder, otherProfile, sourceProfile, sourceFolderId: 5));
    }

    [Fact]
    public void AnOrdinaryDifferentFolderInTheSameProfileIsValid()
    {
        var sourceFolder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var otherFolder = new StoreFolder { Id = 2, Name = "Destino", IsLoadable = true, HasBackingFile = true };
        var profile = MakeProfile("P", sourceFolder, otherFolder);

        Assert.True(MoveDestinationRules.IsValidDestination(otherFolder, profile, profile, sourceFolderId: 1));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter "MoveDestinationRulesTests"`
Expected: FAIL to compile — `MoveDestinationRules` doesn't exist yet.

- [ ] **Step 3: Implement `MoveDestinationRules`**

Create `src/LEC.App/Services/MoveDestinationRules.cs`:

```csharp
// src/LEC.App/Services/MoveDestinationRules.cs
using LEC.App.ViewModels;
using LEC.MailStore;

namespace LEC.App.Services;

/// <summary>
/// Whether a folder is a legal destination for MainWindowViewModel.MoveMessageAsync,
/// checked upfront so MoveMessageDialog can disable invalid folders instead of letting
/// the user pick one and fail after the fact. This does NOT replace
/// DbxStore.AddMessageAsync's own runtime guards -- a damaged or already-full (255
/// message) destination still isn't cheaply knowable here (it would mean opening every
/// folder's backing file just to draw the dialog) and surfaces as a normal error instead.
/// </summary>
public static class MoveDestinationRules
{
    public static bool IsValidDestination(StoreFolder folder, ProfileViewModel profile,
        ProfileViewModel sourceProfile, int sourceFolderId)
    {
        if (!folder.HasBackingFile || !folder.IsLoadable)
            return false;

        // Folder IDs are only unique within one profile -- the same Id in a different
        // profile is a completely different folder, and is a valid destination.
        return !ReferenceEquals(profile, sourceProfile) || folder.Id != sourceFolderId;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter "MoveDestinationRulesTests"`
Expected: PASS — all 5 tests.

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/Services/MoveDestinationRules.cs tests/LEC.App.Tests/MoveDestinationRulesTests.cs
git commit -m "Add MoveDestinationRules.IsValidDestination: pure rule for which folders can receive a moved message"
```

---

### Task 3: `MoveMessageDialog`

**Files:**
- Create: `src/LEC.App/Converters/MoveDestinationValidityConverter.cs`
- Create: `src/LEC.App/MoveMessageDialog.xaml`
- Create: `src/LEC.App/MoveMessageDialog.xaml.cs`

**Interfaces:**
- Consumes: `MoveDestinationRules.IsValidDestination` (Task 2), `ProfileViewModel.DisplayName`/`.FolderTree.RootNodes` (existing), `FolderNodeViewModel.Folder`/`.DisplayName`/`.Children` (existing).
- Produces: `MoveMessageDialog(IEnumerable<ProfileViewModel> openProfiles, ProfileViewModel sourceProfile, int sourceFolderId)`, `.SelectedProfile : ProfileViewModel?`, `.SelectedFolderId : int?`, standard `Window.ShowDialog()`/`.DialogResult`. Consumed by Task 4.

There is no automated test for this task — `MainWindow.xaml.cs`'s existing dialogs (`NewFolderDialog`) have none either; only the pure logic behind them (`MoveDestinationRules`, done in Task 2) is unit tested. This task is verified manually as part of Task 4's manual QA pass, once the dialog is actually reachable from the running app.

- [ ] **Step 1: Create the validity converter**

Create `src/LEC.App/Converters/MoveDestinationValidityConverter.cs`:

```csharp
// src/LEC.App/Converters/MoveDestinationValidityConverter.cs
using System.Globalization;
using System.Windows.Data;
using LEC.App.Services;
using LEC.App.ViewModels;
using LEC.MailStore;

namespace LEC.App.Converters;

/// <summary>
/// XAML-side adapter for MoveDestinationRules.IsValidDestination. SourceProfile/
/// SourceFolderId are set once by MoveMessageDialog's constructor, before its TreeViews
/// first bind; the two MultiBinding values (a node's StoreFolder, and the ProfileViewModel
/// of the TreeView it's in) are re-evaluated per node as each profile's tree renders.
/// </summary>
public sealed class MoveDestinationValidityConverter : IMultiValueConverter
{
    public ProfileViewModel? SourceProfile { get; set; }
    public int SourceFolderId { get; set; }

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (SourceProfile is null || values is not [StoreFolder folder, ProfileViewModel profile])
            return false;

        return MoveDestinationRules.IsValidDestination(folder, profile, SourceProfile, SourceFolderId);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 2: Create the dialog's XAML**

Create `src/LEC.App/MoveMessageDialog.xaml`:

```xml
<!-- src/LEC.App/MoveMessageDialog.xaml -->
<Window x:Class="LEC.App.MoveMessageDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:conv="clr-namespace:LEC.App.Converters"
        Title="Mover mensagem" Width="420" Height="480"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False">
    <Window.Resources>
        <conv:MoveDestinationValidityConverter x:Key="ValidityConverter"/>
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
                            <TreeView ItemsSource="{Binding FolderTree.RootNodes}"
                                      SelectedItemChanged="TreeView_SelectedItemChanged"
                                      BorderThickness="0"
                                      ScrollViewer.VerticalScrollBarVisibility="Disabled"
                                      ScrollViewer.HorizontalScrollBarVisibility="Disabled">
                                <TreeView.ItemContainerStyle>
                                    <Style TargetType="TreeViewItem">
                                        <Setter Property="IsEnabled">
                                            <Setter.Value>
                                                <MultiBinding Converter="{StaticResource ValidityConverter}">
                                                    <Binding Path="Folder"/>
                                                    <Binding Path="DataContext" RelativeSource="{RelativeSource AncestorType=TreeView}"/>
                                                </MultiBinding>
                                            </Setter.Value>
                                        </Setter>
                                    </Style>
                                </TreeView.ItemContainerStyle>
                                <TreeView.ItemTemplate>
                                    <HierarchicalDataTemplate ItemsSource="{Binding Children}">
                                        <TextBlock Text="{Binding DisplayName}"/>
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

(`IsEnabled` is set on the `TreeViewItem` container itself via `ItemContainerStyle`, not on the inner `TextBlock` — this is what makes WPF actually block selection/keyboard navigation into that node, not just gray its text. WPF's default `TreeViewItem` template already renders a disabled item visually grayed out, so no separate `Foreground` converter is needed here.)

- [ ] **Step 3: Create the dialog's code-behind**

Create `src/LEC.App/MoveMessageDialog.xaml.cs`:

```csharp
// src/LEC.App/MoveMessageDialog.xaml.cs
using System.Windows;
using LEC.App.Converters;
using LEC.App.ViewModels;

namespace LEC.App;

public partial class MoveMessageDialog : Window
{
    public ProfileViewModel? SelectedProfile { get; private set; }
    public int? SelectedFolderId { get; private set; }

    public MoveMessageDialog(IEnumerable<ProfileViewModel> openProfiles, ProfileViewModel sourceProfile, int sourceFolderId)
    {
        InitializeComponent();

        var converter = (MoveDestinationValidityConverter)Resources["ValidityConverter"];
        converter.SourceProfile = sourceProfile;
        converter.SourceFolderId = sourceFolderId;

        ProfilesItemsControl.ItemsSource = openProfiles;
    }

    // Each open profile has its own TreeView (mirrors MainWindow's per-profile trees), so
    // this fires independently per tree. Deliberately doesn't clear a previously-selected
    // node's highlight in another tree when the user picks a different one -- unlike
    // MainWindow's FolderTreeView_SelectedItemChanged, this dialog closes as soon as one
    // pick is confirmed, so a stale highlight in an abandoned tree is a harmless, one-time
    // cosmetic detail, not a functional bug.
    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is System.Windows.Controls.TreeView { DataContext: ProfileViewModel profile }
            && e.NewValue is FolderNodeViewModel node)
        {
            SelectedProfile = profile;
            SelectedFolderId = node.Folder.Id;
            MoveButton.IsEnabled = true;
        }
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
```

- [ ] **Step 4: Build and verify it compiles**

Run: `dotnet build`
Expected: builds with no errors (WPF `.xaml`/`.xaml.cs` pairs compile together; there is no unit test for this task specifically, so a clean build is the only automated check here).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/Converters/MoveDestinationValidityConverter.cs src/LEC.App/MoveMessageDialog.xaml src/LEC.App/MoveMessageDialog.xaml.cs
git commit -m "Add MoveMessageDialog: destination picker across all open profiles, invalid folders disabled"
```

---

### Task 4: Wire "Mover para..." into the message list

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml`
- Modify: `src/LEC.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `MoveMessageDialog` (Task 3), `MainWindowViewModel.MoveMessageAsync`/`.OpenProfiles`/`.SelectFolderAsync` (existing), `_selectedProfile`/`_selectedNode` (existing fields).
- Produces: nothing consumed by a later task — this is the last task of the plan.

There is no automated test for this task, matching the existing precedent: none of `MainWindow.xaml.cs`'s other click handlers (`NewSubfolder_Click`, `CreateFolderAsync`, etc.) have dedicated tests either. Step 4 below is a manual verification checklist instead — follow it after Step 3's build succeeds.

- [ ] **Step 1: Add a per-item context menu to the message list**

In `src/LEC.App/MainWindow.xaml`, replace:

```xml
                        <ListView.ItemContainerStyle>
                            <Style TargetType="ListViewItem">
                                <Setter Property="Foreground" Value="{Binding RecoveredKind, Converter={StaticResource RecoveredKindToRowBrushConverter}}"/>
                            </Style>
                        </ListView.ItemContainerStyle>
```

with:

```xml
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

(A `ContextMenu`'s `DataContext` inherits from its `PlacementTarget` — here, the `ListViewItem` being right-clicked — the same WPF behavior the existing "Nova subpasta aqui..." menu on each folder tree node already relies on. So `MenuItem.DataContext` inside this menu resolves to that row's bound `MessageHeader`.)

**Correction found during implementation:** an inline `Click="MoveMessage_Click"` attribute directly on a `MenuItem` that is itself declared inside a `Style.Setter.Value` does not get compiled event-wiring — WPF's XAML compiler only connects named events for elements it processes as part of the page's static content, not elements defined inside a `Style` that gets instantiated dynamically per item. The fix above wraps the handler in a nested `Style`/`EventSetter` instead, which IS evaluated at the point the style is applied and works correctly regardless of nesting depth. This is the standard, well-established WPF workaround for this exact situation — verified by a clean `dotnet build` and full `dotnet test` pass.

- [ ] **Step 2: Add the click handler**

In `src/LEC.App/MainWindow.xaml.cs`, add this method right after `MessageListView_SelectionChanged` (before `NavigateToRenderedMessage`):

```csharp

    private async void MoveMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not MessageHeader header)
            return;
        if (_selectedProfile is not { } sourceProfile)
            return;

        var dialog = new MoveMessageDialog(_viewModel.OpenProfiles, sourceProfile, header.Id.FolderId) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedProfile is not { } destinationProfile || dialog.SelectedFolderId is not { } destinationFolderId)
            return;

        try
        {
            await _viewModel.MoveMessageAsync(sourceProfile, header, destinationProfile, destinationFolderId);

            // The moved message is now hidden from the folder currently on screen (its
            // source) -- reload that folder's list so it disappears, and clear the reading
            // pane in case it was showing the message that just moved (same visual reset
            // CloseProfile_Click already uses for "nothing selected").
            if (_selectedProfile is { } profile && _selectedNode is { } node)
                await _viewModel.SelectFolderAsync(profile, node);

            NavigateToRenderedMessage(string.Empty);
            ImagesBlockedBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LEC — Não foi possível mover a mensagem", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: builds with no errors.

Run: `dotnet test`
Expected: PASS — no regression in any project (this task adds no new automated tests, per the design's testing strategy — only confirms nothing existing broke).

- [ ] **Step 4: Manual verification**

Run the app (`dotnet run --project src/LEC.App`) against a real or test profile directory (or two, opened side by side, to exercise the cross-profile path) and confirm:

1. Open one profile, select a folder with at least one message. Right-click a message in the list → "Mover para..." appears in the context menu.
2. Clicking it opens `MoveMessageDialog`, showing every open profile's folder tree.
3. The message's own source folder is visibly grayed out and cannot be clicked/selected.
4. A purely organizational folder with no backing file (if the test profile has one, e.g. "Pastas locais") is also grayed out and cannot be selected.
5. "Mover" starts disabled; clicking a valid folder enables it.
6. Clicking "Mover" closes the dialog, the message disappears from the list (moved), and if that message was open in the reading pane, the reading pane clears.
7. Open a second profile alongside the first; repeat the move picking a folder in the *other* profile's tree — confirm it works cross-profile and the destination profile's tree shows the moved message when you navigate to that folder there.
8. Cancel the dialog (button or window close) — confirm nothing changes: the message stays in the list, no error, no side effects.

If any step fails, fix the underlying code before considering this task done — there is no automated safety net for this task beyond the build and the full test suite.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs
git commit -m "Wire move-message UI: context menu on the message list opens MoveMessageDialog"
```

---

## Self-Review Notes

- **Cobertura do design doc:** §2 (decisões de escopo: menu de contexto, todos os perfis, pastas inválidas desabilitadas, sem confirmação extra, painel de leitura limpo, sem seleção múltipla) → refletido nos Global Constraints e em cada task. §3 (`StoreFolder.HasBackingFile`) → Task 1. §4 (`MoveMessageDialog`) → Task 3. §5 (menu de contexto + handler) → Task 4. §6 (erros e casos de borda) → Task 4's `catch` genérico + o próprio design de desabilitar nós inválidos na Task 3. §7 (estratégia de testes) → Task 1/2 têm testes automatizados; Task 3/4 seguem o precedente confirmado de `MainWindow.xaml.cs` não ter testes dedicados, com verificação manual explícita na Task 4.
- **Consistência de tipos:** `StoreFolder.HasBackingFile` (Task 1) é lido por `MoveDestinationRules.IsValidDestination` (Task 2) exatamente com esse nome. `MoveDestinationRules.IsValidDestination(StoreFolder, ProfileViewModel, ProfileViewModel, int)` (Task 2) é chamado com a mesma assinatura por `MoveDestinationValidityConverter` (Task 3). `MoveMessageDialog`'s construtor/`.SelectedProfile`/`.SelectedFolderId` (Task 3) são usados em `MainWindow.xaml.cs`'s `MoveMessage_Click` (Task 4) exatamente como definidos.
- **Ordem de dependências:** Task 1 não depende de nada. Task 2 depende só da Task 1 (`HasBackingFile`). Task 3 depende só da Task 2 (`MoveDestinationRules`). Task 4 depende só da Task 3 (`MoveMessageDialog`). Cadeia estritamente linear — sem dependências cruzadas fora de ordem como a fatia do motor teve.
- **Fora de escopo confirmado:** nenhuma task muda `MainWindowViewModel.MoveMessageAsync`/`IMailStore.AddMessageAsync`/`HideMovedMessageAsync`; nenhuma task adiciona drag-and-drop, seleção múltipla, confirmação extra, ou pré-validação de pasta danificada/cheia.
