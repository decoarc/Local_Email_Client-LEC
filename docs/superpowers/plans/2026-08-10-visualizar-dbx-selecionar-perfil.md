# Visualizar arquivos .dbx ao selecionar a pasta do perfil — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the native Windows folder picker used by "Abrir perfil" with an in-app dialog that shows, as the user browses folders, the `.dbx` files present in the currently selected folder.

**Architecture:** A new pure, testable navigation view-model (`ProfileFolderBrowserViewModel`) tracks the current path and lists its subfolders and `.dbx` files via `System.IO`. A new WPF window (`OpenProfileDialog`) hosts it: path textbox, two side-by-side lists (subfolders / `.dbx` files), and a confirm button. `MainWindow.xaml.cs`'s `OpenProfile_Click` swaps the native `OpenFolderDialog` for this new dialog; everything downstream (`MainWindowViewModel.OpenProfile`) is untouched.

**Tech Stack:** WPF (.NET), CommunityToolkit.Mvvm (`ObservableObject`, already used by `FolderTreeViewModel`), xunit for tests.

## Global Constraints

- Only file **names** are shown for `.dbx` files — no size/date/preview (per spec §2).
- `.dbx` listing is **not recursive** — only direct children of the current folder (per spec §2).
- Opening the profile itself must remain byte-for-byte the same call: `_viewModel.OpenProfile(path)` (per spec §1).
- Any I/O failure while navigating (permission denied, path vanished, invalid path) must fail silently and leave the browser at its previous state — no exceptions surfaced to the user (per spec §3).

---

### Task 1: `ProfileFolderBrowserViewModel` — folder navigation logic

**Files:**
- Create: `src/LEC.App/ViewModels/ProfileFolderBrowserViewModel.cs`
- Test: `tests/LEC.App.Tests/ProfileFolderBrowserViewModelTests.cs`

**Interfaces:**
- Produces (consumed by Task 2):
  - `class ProfileFolderBrowserViewModel : ObservableObject`
  - `string? CurrentPath { get; }` — `null` means "root/drives level"; raises `PropertyChanged` on change.
  - `ObservableCollection<string> Subfolders { get; }` — names only (not full paths) of subfolders of `CurrentPath`; when `CurrentPath is null`, holds drive roots instead (e.g. `"C:\\"`).
  - `ObservableCollection<string> DbxFiles { get; }` — names only of `*.dbx` files directly in `CurrentPath`; empty when `CurrentPath is null`.
  - `void NavigateToRoot()`
  - `void NavigateTo(string? path)`
  - `void NavigateUp()`
  - `void EnterSubfolder(string name)` — `name == ".."` delegates to `NavigateUp()`; otherwise combines with `CurrentPath` (or is used directly as a drive root when `CurrentPath is null`) and calls `NavigateTo`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LEC.App.Tests/ProfileFolderBrowserViewModelTests.cs
using System;
using System.IO;
using System.Linq;
using LEC.App.ViewModels;
using Xunit;

namespace LEC.App.Tests;

public class ProfileFolderBrowserViewModelTests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lec-browser-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void NavigateTo_ListsDbxFilesSeparatelyFromOtherFilesAndSubfolders()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Sub"));
        File.WriteAllText(Path.Combine(temp.Path, "Inbox.dbx"), "");
        File.WriteAllText(Path.Combine(temp.Path, "Folders.dbx"), "");
        File.WriteAllText(Path.Combine(temp.Path, "readme.txt"), "");

        var vm = new ProfileFolderBrowserViewModel();
        vm.NavigateTo(temp.Path);

        Assert.Equal(temp.Path, vm.CurrentPath);
        Assert.Equal(new[] { "Sub" }, vm.Subfolders);
        Assert.Equal(new[] { "Folders.dbx", "Inbox.dbx" }, vm.DbxFiles.OrderBy(n => n));
    }

    [Fact]
    public void EnterSubfolder_DescendsAndListsThatFoldersDbxFiles()
    {
        using var temp = new TempDirectory();
        var subPath = Path.Combine(temp.Path, "Sub");
        Directory.CreateDirectory(subPath);
        File.WriteAllText(Path.Combine(subPath, "Sent.dbx"), "");

        var vm = new ProfileFolderBrowserViewModel();
        vm.NavigateTo(temp.Path);
        vm.EnterSubfolder("Sub");

        Assert.Equal(subPath, vm.CurrentPath);
        Assert.Equal(new[] { "Sent.dbx" }, vm.DbxFiles);
    }

    [Fact]
    public void EnterSubfolderDotDot_GoesBackUpToTheParentListing()
    {
        using var temp = new TempDirectory();
        var subPath = Path.Combine(temp.Path, "Sub");
        Directory.CreateDirectory(subPath);
        File.WriteAllText(Path.Combine(temp.Path, "Inbox.dbx"), "");

        var vm = new ProfileFolderBrowserViewModel();
        vm.NavigateTo(temp.Path);
        vm.EnterSubfolder("Sub");
        vm.EnterSubfolder("..");

        Assert.Equal(temp.Path, vm.CurrentPath);
        Assert.Equal(new[] { "Inbox.dbx" }, vm.DbxFiles);
    }

    [Fact]
    public void NavigateTo_FolderWithNoDbxFiles_LeavesDbxFilesEmpty()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "readme.txt"), "");

        var vm = new ProfileFolderBrowserViewModel();
        vm.NavigateTo(temp.Path);

        Assert.Empty(vm.DbxFiles);
    }

    [Fact]
    public void NavigateTo_InvalidPath_LeavesStateUnchanged()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "Inbox.dbx"), "");

        var vm = new ProfileFolderBrowserViewModel();
        vm.NavigateTo(temp.Path);
        vm.NavigateTo(Path.Combine(temp.Path, "does-not-exist"));

        Assert.Equal(temp.Path, vm.CurrentPath);
        Assert.Equal(new[] { "Inbox.dbx" }, vm.DbxFiles);
    }

    [Fact]
    public void InitialState_IsRootLevelListingDrives()
    {
        var vm = new ProfileFolderBrowserViewModel();

        Assert.Null(vm.CurrentPath);
        Assert.Empty(vm.DbxFiles);
        Assert.NotEmpty(vm.Subfolders);
        Assert.Contains(vm.Subfolders, d => d.StartsWith("C:", StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter ProfileFolderBrowserViewModelTests`
Expected: FAIL to compile — `ProfileFolderBrowserViewModel` doesn't exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
// src/LEC.App/ViewModels/ProfileFolderBrowserViewModel.cs
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LEC.App.ViewModels;

public partial class ProfileFolderBrowserViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _currentPath;

    public ObservableCollection<string> Subfolders { get; } = new();
    public ObservableCollection<string> DbxFiles { get; } = new();

    public ProfileFolderBrowserViewModel() => NavigateToRoot();

    public void NavigateToRoot()
    {
        CurrentPath = null;

        Subfolders.Clear();
        foreach (var drive in Directory.GetLogicalDrives().OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            Subfolders.Add(drive);

        DbxFiles.Clear();
    }

    public void NavigateTo(string? path)
    {
        if (path is null)
        {
            NavigateToRoot();
            return;
        }

        string[] subfolderPaths;
        string[] dbxFilePaths;
        try
        {
            subfolderPaths = Directory.GetDirectories(path);
            dbxFilePaths = Directory.GetFiles(path, "*.dbx");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or System.Security.SecurityException)
        {
            // Navigation failed -- leave CurrentPath/Subfolders/DbxFiles exactly as they were.
            return;
        }

        CurrentPath = path;

        Subfolders.Clear();
        foreach (var name in subfolderPaths.Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            Subfolders.Add(name!);

        DbxFiles.Clear();
        foreach (var name in dbxFilePaths.Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            DbxFiles.Add(name!);
    }

    public void NavigateUp()
    {
        if (CurrentPath is null)
            return;

        NavigateTo(Directory.GetParent(CurrentPath)?.FullName);
    }

    public void EnterSubfolder(string name)
    {
        if (name == "..")
        {
            NavigateUp();
            return;
        }

        NavigateTo(CurrentPath is null ? name : Path.Combine(CurrentPath, name));
    }
}
```

Add `using System;` at the top alongside the others if the project's implicit-usings setting doesn't already cover it (check `src/LEC.App/LEC.App.csproj` for `<ImplicitUsings>enable</ImplicitUsings>` — if present, the explicit `using System;` isn't required, but leaving it out is fine either way since `Exception`/`ArgumentException` resolve via implicit usings in that case).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter ProfileFolderBrowserViewModelTests`
Expected: PASS, all 6 tests green.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/ProfileFolderBrowserViewModel.cs tests/LEC.App.Tests/ProfileFolderBrowserViewModelTests.cs
git commit -m "Add ProfileFolderBrowserViewModel for browsing folders and previewing .dbx files"
```

---

### Task 2: `OpenProfileDialog` window

**Files:**
- Create: `src/LEC.App/OpenProfileDialog.xaml`
- Create: `src/LEC.App/OpenProfileDialog.xaml.cs`

**Interfaces:**
- Consumes: `ProfileFolderBrowserViewModel` from Task 1 (`CurrentPath`, `Subfolders`, `DbxFiles`, `NavigateTo`, `NavigateUp`, `EnterSubfolder`, `PropertyChanged`).
- Produces (consumed by Task 3):
  - `class OpenProfileDialog : Window`
  - `public OpenProfileDialog()` — parameterless constructor.
  - `public string? SelectedPath { get; }` — set only when the user confirms; `Window.DialogResult == true` in that case.

This task has no automated test (WPF windows aren't unit-testable in this project — same limitation already noted in prior specs/plans). It's verified by building successfully; end-to-end behavior is covered by the manual checklist in Task 3.

- [ ] **Step 1: Create the XAML**

```xml
<!-- src/LEC.App/OpenProfileDialog.xaml -->
<Window x:Class="LEC.App.OpenProfileDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Abrir perfil" Width="640" Height="440"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False">
    <DockPanel Margin="12">
        <TextBlock DockPanel.Dock="Top" Text="Selecione a pasta do perfil (contendo Folders.dbx):" Margin="0,0,0,8"/>
        <TextBox x:Name="PathTextBox" DockPanel.Dock="Top" Margin="0,0,0,8" KeyDown="PathTextBox_KeyDown"/>
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Cancelar" IsCancel="True" Width="120" Margin="0,0,8,0"/>
            <Button x:Name="SelectButton" Content="Selecionar esta pasta" Width="150" IsDefault="True" Click="SelectButton_Click"/>
        </StackPanel>
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <DockPanel Grid.Column="0" Margin="0,0,6,0">
                <TextBlock DockPanel.Dock="Top" Text="Subpastas:" FontWeight="Bold" Margin="0,0,0,4"/>
                <ListBox x:Name="SubfoldersListBox" MouseDoubleClick="SubfoldersListBox_MouseDoubleClick"/>
            </DockPanel>
            <DockPanel Grid.Column="1" Margin="6,0,0,0">
                <TextBlock DockPanel.Dock="Top" Text="Arquivos .dbx nesta pasta:" FontWeight="Bold" Margin="0,0,0,4"/>
                <Grid>
                    <TextBlock x:Name="NoDbxMessage" Text="Nenhum arquivo .dbx nesta pasta" Foreground="Gray" Visibility="Collapsed"/>
                    <ListBox x:Name="DbxFilesListBox" IsHitTestVisible="False" Background="Transparent" BorderThickness="0"/>
                </Grid>
            </DockPanel>
        </Grid>
    </DockPanel>
</Window>
```

- [ ] **Step 2: Create the code-behind**

```csharp
// src/LEC.App/OpenProfileDialog.xaml.cs
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LEC.App.ViewModels;

namespace LEC.App;

public partial class OpenProfileDialog : Window
{
    private readonly ProfileFolderBrowserViewModel _browser = new();
    private readonly ObservableCollection<string> _subfoldersDisplay = new();

    public string? SelectedPath { get; private set; }

    public OpenProfileDialog()
    {
        InitializeComponent();

        SubfoldersListBox.ItemsSource = _subfoldersDisplay;
        DbxFilesListBox.ItemsSource = _browser.DbxFiles;

        _browser.Subfolders.CollectionChanged += (_, _) => RefreshSubfoldersDisplay();
        _browser.DbxFiles.CollectionChanged += (_, _) => RefreshNoDbxMessage();
        _browser.PropertyChanged += Browser_PropertyChanged;

        RefreshSubfoldersDisplay();
        RefreshNoDbxMessage();
        UpdateFromCurrentPath();
    }

    private void Browser_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileFolderBrowserViewModel.CurrentPath))
            UpdateFromCurrentPath();
    }

    private void UpdateFromCurrentPath()
    {
        PathTextBox.Text = _browser.CurrentPath ?? "";
        SelectButton.IsEnabled = _browser.CurrentPath != null;
    }

    private void RefreshSubfoldersDisplay()
    {
        _subfoldersDisplay.Clear();
        if (_browser.CurrentPath != null)
            _subfoldersDisplay.Add("..");
        foreach (var name in _browser.Subfolders)
            _subfoldersDisplay.Add(name);
    }

    private void RefreshNoDbxMessage()
    {
        NoDbxMessage.Visibility = _browser.DbxFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SubfoldersListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SubfoldersListBox, (DependencyObject)e.OriginalSource) is not ListBoxItem)
            return;

        if (SubfoldersListBox.SelectedItem is string name)
            _browser.EnterSubfolder(name);
    }

    private void PathTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _browser.NavigateTo(PathTextBox.Text);
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedPath = _browser.CurrentPath;
        DialogResult = true;
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/LEC.App/LEC.App.csproj`
Expected: Build succeeds, no XAML or C# errors.

- [ ] **Step 4: Commit**

```bash
git add src/LEC.App/OpenProfileDialog.xaml src/LEC.App/OpenProfileDialog.xaml.cs
git commit -m "Add OpenProfileDialog: folder browser with .dbx file preview"
```

---

### Task 3: Wire `OpenProfileDialog` into `MainWindow`

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml.cs:172-177` (the `OpenProfile_Click` method)

**Interfaces:**
- Consumes: `OpenProfileDialog` from Task 2 (`SelectedPath`, `DialogResult`).

- [ ] **Step 1: Replace the native folder picker with the new dialog**

In `src/LEC.App/MainWindow.xaml.cs`, replace:

```csharp
    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecione a pasta do perfil (contendo Folders.dbx)" };
        if (dialog.ShowDialog() == true)
            _viewModel.OpenProfile(dialog.FolderName);
    }
```

with:

```csharp
    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenProfileDialog { Owner = this };
        if (dialog.ShowDialog() == true)
            _viewModel.OpenProfile(dialog.SelectedPath!);
    }
```

Leave the `using Microsoft.Win32;` at the top of the file in place — it's still needed by `SaveFileDialog` in the attachment-save handler earlier in the same file.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LEC.App/LEC.App.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`
Expected: All tests pass (no existing test references `OpenProfile_Click`'s dialog type, so nothing else should be affected).

- [ ] **Step 4: Commit**

```bash
git add src/LEC.App/MainWindow.xaml.cs
git commit -m "Use OpenProfileDialog instead of the native folder picker when opening a profile"
```

- [ ] **Step 5: Manual GUI checklist**

Run the app (`dotnet run --project src/LEC.App`) and verify:

- Click "Abrir perfil": the new dialog opens, shows a list of drives (e.g. `C:\`) in the left list, right panel shows "Nenhum arquivo .dbx nesta pasta", "Selecionar esta pasta" is disabled.
- Double-click a drive, then navigate (double-click) down to a real profile folder (one containing `Folders.dbx` and per-folder `.dbx` files like `Inbox.dbx`): the right-hand list shows those file names.
- Navigate into a folder with no `.dbx` files: right panel shows the "Nenhum arquivo .dbx nesta pasta" message again.
- Double-click `..` at the top of the left list: goes back up one level, right panel updates to that folder's `.dbx` files.
- Paste/type a valid folder path into the top text box and press Enter: navigates there directly, both lists update.
- Type an invalid path into the text box and press Enter: nothing crashes, the lists stay on the last valid folder.
- With a valid profile folder selected, click "Selecionar esta pasta": dialog closes and the profile opens exactly as it does today (folder tree populates, etc.).
- Click "Abrir perfil" again, then "Cancelar": no new profile is opened, previously open profile(s) are untouched.
- Open two different profiles in sequence using the new dialog: both remain open simultaneously (no regression to multi-profile support).
