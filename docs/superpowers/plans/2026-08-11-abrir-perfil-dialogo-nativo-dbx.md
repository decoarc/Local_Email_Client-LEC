# Abrir perfil: voltar ao diálogo nativo do Windows, filtrado para .dbx — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the custom `OpenProfileDialog` used by "Abrir perfil" with the native Windows file-open dialog (`Microsoft.Win32.OpenFileDialog`), filtered to `*.dbx`, so the user gets the fully native Explorer look while still seeing `.dbx` files as they browse.

**Architecture:** `MainWindow.xaml.cs`'s `OpenProfile_Click` swaps `OpenProfileDialog` for a `Microsoft.Win32.OpenFileDialog` filtered to `*.dbx`; the profile folder becomes `Path.GetDirectoryName(dialog.FileName)`. `_viewModel.OpenProfile(...)` is unchanged. The now-obsolete `OpenProfileDialog` window and `ProfileFolderBrowserViewModel` (and its tests) are deleted.

**Tech Stack:** WPF (.NET), `Microsoft.Win32.OpenFileDialog` (already used elsewhere in `MainWindow.xaml.cs` for `SaveFileDialog`).

## Global Constraints

- Opening the profile itself must remain byte-for-byte the same call: `_viewModel.OpenProfile(path)` (per spec §1, §3).
- No custom UI, no custom navigation logic — everything comes from the native `OpenFileDialog` (per spec §2).
- No validation that the selected `.dbx` is specifically `Folders.dbx` — any `.dbx` in the profile folder is accepted, downstream behavior for a missing `Folders.dbx` is unchanged (per spec §5).

---

### Task 1: Revert `OpenProfile_Click` to the native file dialog and remove the obsolete custom dialog

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml.cs` (`OpenProfile_Click` method, currently around line 172-177)
- Delete: `src/LEC.App/OpenProfileDialog.xaml`
- Delete: `src/LEC.App/OpenProfileDialog.xaml.cs`
- Delete: `src/LEC.App/ViewModels/ProfileFolderBrowserViewModel.cs`
- Delete: `tests/LEC.App.Tests/ProfileFolderBrowserViewModelTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Win32.OpenFileDialog` (already available — `using Microsoft.Win32;` and `using System.IO;` are already present at the top of `MainWindow.xaml.cs`, no new usings needed).
- Produces: nothing consumed by later tasks — this plan has one task.

No automated test is possible or needed for this task: `OpenProfile_Click` only constructs and shows a native dialog, with no non-trivial logic to unit test (same limitation already documented for WPF window code in the superseded design). Verification is build success, full test suite passing (proving no leftover references to the deleted files), and the manual GUI checklist in Step 6.

- [ ] **Step 1: Replace `OpenProfile_Click`**

In `src/LEC.App/MainWindow.xaml.cs`, replace:

```csharp
    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenProfileDialog { Owner = this };
        if (dialog.ShowDialog() == true)
            _viewModel.OpenProfile(dialog.SelectedPath!);
    }
```

with:

```csharp
    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecione um arquivo .dbx do perfil (ex: Folders.dbx)",
            Filter = "Arquivos .dbx (*.dbx)|*.dbx",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
            _viewModel.OpenProfile(Path.GetDirectoryName(dialog.FileName)!);
    }
```

- [ ] **Step 2: Delete the obsolete custom dialog and view model**

```bash
git rm src/LEC.App/OpenProfileDialog.xaml src/LEC.App/OpenProfileDialog.xaml.cs src/LEC.App/ViewModels/ProfileFolderBrowserViewModel.cs tests/LEC.App.Tests/ProfileFolderBrowserViewModelTests.cs
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/LEC.App/LEC.App.csproj`
Expected: Build succeeds, 0 errors, 0 warnings. No lingering reference to `OpenProfileDialog` or `ProfileFolderBrowserViewModel` anywhere in the project (the deleted files were their only definitions).

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test`
Expected: All tests pass. The `ProfileFolderBrowserViewModelTests` suite is gone (deleted in Step 2), so the total test count drops by however many tests it had; no other test references `OpenProfile_Click`'s dialog type.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/MainWindow.xaml.cs
git commit -m "Revert Abrir perfil to the native Windows file dialog, filtered to .dbx"
```

(The deletions from Step 2 are already staged by `git rm` — this commit includes them together with the `MainWindow.xaml.cs` change, since they're one logical change.)

- [ ] **Step 6: Manual GUI checklist**

Run the app (`dotnet run --project src/LEC.App`) and verify:

- Click "Abrir perfil": the native Windows Explorer file-open dialog opens, filter already set to "Arquivos .dbx (*.dbx)".
- Navigate to a real profile folder (one containing `Folders.dbx` and per-folder `.dbx` files like `Inbox.dbx`): the file listing shows those `.dbx` names, exactly like any filtered file-open dialog.
- Navigate to a folder with no `.dbx` files: the listing is empty ("Nenhum item corresponde à pesquisa" or equivalent native empty state).
- Select a `.dbx` file (e.g. `Folders.dbx`) and click "Abrir": the dialog closes and the profile opens exactly as it does today (folder tree populates, etc.).
- Click "Abrir perfil" again, then "Cancelar": no new profile is opened, previously open profile(s) are untouched.
- Open two different profiles in sequence using the dialog: both remain open simultaneously (no regression to multi-profile support).
