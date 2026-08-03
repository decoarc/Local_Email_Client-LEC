# Criar pastas — integração na UI — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user create a folder from the folder tree itself — right-click → "Nova pasta"/"Nova subpasta aqui..." → type a name → the folder appears in the tree — by wiring the already-working `DbxStore.CreateFolderAsync` (previous slice) into `ProfileViewModel` and `MainWindow`.

**Architecture:** `CreateFolderAsync` is promoted from `DbxStore` alone onto the `IMailStore` interface (zero-cost: `DbxStore` already implements the exact signature; `PstStore` doesn't exist). `ProfileViewModel.CreateFolderAsync` wraps the store call with a `FolderTree.Build` refresh, so the bound `TreeView` updates automatically. Two `ContextMenu`s in `MainWindow.xaml` (one per tree node, one on the `TreeView` itself) rely on WPF's built-in `ContextMenu`→`PlacementTarget` `DataContext` inheritance to reach the right `FolderNodeViewModel`/`ProfileViewModel` without any manual selection tracking. A new minimal modal `NewFolderDialog` collects the name; validation and error messages stay entirely in the already-tested lower layers (`DbxStore.CreateFolderAsync`), surfaced to the user via `MessageBox`.

**Tech Stack:** .NET 9, C#, WPF, xUnit.

## Global Constraints

- `PstStore` does not exist yet — only `DbxStore` implements `IMailStore`, so adding `CreateFolderAsync` to the interface has no other implementer to update.
- After creating a folder, the affected profile's tree fully rebuilds (`FolderTreeViewModel.Build` clears and re-adds every node) — this collapses any previously-expanded subtree back to default. This is accepted, existing behavior (the same method is already used this way on profile open), not a regression to fix in this slice.
- No automated tests for `MainWindow.xaml`/`MainWindow.xaml.cs` (context menus, dialog wiring) — this project has no WPF UI test infrastructure; verification for that layer is `dotnet build` succeeding plus the full test suite staying green. Automated coverage stops at `ProfileViewModel`.
- The dialog (`NewFolderDialog`) does no name validation itself — it only collects text. All validation (empty name, collisions, reserved device names, damaged `Folders.dbx`, etc.) already lives in `DbxStore.CreateFolderAsync` from the previous slice and must not be duplicated here.
- Error feedback for a failed folder creation is a `MessageBox` (not the `StatusText` status bar) — creating a folder is a deliberate, modal user action, distinct from the passive background-load failures that already use `StatusText` elsewhere in this file.

---

## File Structure

```
src/LEC.MailStore/
  IMailStore.cs                MODIFIED — Task 1: +CreateFolderAsync

src/LEC.App/ViewModels/
  ProfileViewModel.cs           MODIFIED — Task 2: +CreateFolderAsync

src/LEC.App/
  NewFolderDialog.xaml          NEW — Task 3: name-entry modal
  NewFolderDialog.xaml.cs       NEW — Task 3: code-behind, exposes FolderName
  MainWindow.xaml               MODIFIED — Task 3: +ContextMenu on the tree-node TextBlock and on the TreeView
  MainWindow.xaml.cs            MODIFIED — Task 3: +NewSubfolder_Click, +NewTopLevelFolder_Click, +CreateFolderAsync helper, +FindAncestor<T> helper

tests/LEC.App.Tests/
  FakeMailStore.cs              MODIFIED — Task 2: +CreateFolderAsync (in-memory), +ExceptionToThrowOnCreateFolder
  ProfileViewModelTests.cs      MODIFIED — Task 2: +tests for CreateFolderAsync
```

`FolderNodeViewModel`/`FolderTreeViewModel`/`StoreFolder` continue as they are — no changes. `DbxStore.cs` needs **no** edit in Task 1: its existing `CreateFolderAsync(string name, int? parentId, CancellationToken ct = default)` (from the previous slice) already matches the interface signature exactly, so C#'s implicit interface implementation picks it up with no code change.

---

### Task 1: Promote `CreateFolderAsync` onto `IMailStore`

**Files:**
- Modify: `src/LEC.MailStore/IMailStore.cs`

**Interfaces:**
- Consumes: nothing new — `DbxStore.CreateFolderAsync(string name, int? parentId, CancellationToken ct = default) : Task<StoreFolder>` already exists (previous slice) with this exact signature.
- Produces: `IMailStore.CreateFolderAsync(string name, int? parentId, CancellationToken ct = default) : Task<StoreFolder>`, now callable polymorphically. Consumed by Task 2 (`ProfileViewModel.CreateFolderAsync` calls `Store.CreateFolderAsync`) and by Task 2's `FakeMailStore`, which must now implement it too.

Sem TDD aqui — puro acréscimo de assinatura a uma interface já satisfeita pela única classe que a implementa (`DbxStore`); não há comportamento novo para testar nesta task (o comportamento de `CreateFolderAsync` em si já está coberto por `DbxStoreCreateFolderTests` da fatia anterior). A verificação é o build e a suíte completa passando (Step 3).

- [ ] **Step 1: Add the method to `IMailStore`**

Em `src/LEC.MailStore/IMailStore.cs`, troque:

```csharp
    /// <summary>Toggles whether a folder's already-scavenged items appear in EnumerateHeadersAsync.</summary>
    void SetScavengeVisibility(StoreFolder folder, bool visible);
}
```

por:

```csharp
    /// <summary>Toggles whether a folder's already-scavenged items appear in EnumerateHeadersAsync.</summary>
    void SetScavengeVisibility(StoreFolder folder, bool visible);

    /// <summary>
    /// Creates a folder — a real backing file plus a new Folders.dbx entry for DbxStore.
    /// See DbxStore.CreateFolderAsync for the full contract (validation, backups,
    /// exceptions thrown).
    /// </summary>
    Task<StoreFolder> CreateFolderAsync(string name, int? parentId, CancellationToken ct = default);
}
```

- [ ] **Step 2: Build to confirm `DbxStore` satisfies the interface with no code change**

Run: `dotnet build`
Expected: `Build succeeded.` — `DbxStore`'s existing `CreateFolderAsync` method (same file, unmodified) already matches the new interface member exactly, so this should compile with no other edits. If it does not compile, the mismatch is almost certainly a parameter-name or default-value difference between `IMailStore`'s new declaration and `DbxStore`'s existing method — check `src/LEC.MailStore/DbxStore.cs` around its `CreateFolderAsync` declaration and make the interface declaration match it exactly, not the other way around (the implementation is the one already tested and reviewed).

- [ ] **Step 3: Run the full test suite to confirm zero regressions**

Run: `dotnet test`
Expected: PASS — every existing test in every project, unchanged (this task adds no new test; it only changes what's reachable through an interface reference).

- [ ] **Step 4: Commit**

```bash
git add src/LEC.MailStore/IMailStore.cs
git commit -m "Promote DbxStore.CreateFolderAsync onto the IMailStore interface"
```

---

### Task 2: `ProfileViewModel.CreateFolderAsync`

**Files:**
- Modify: `src/LEC.App/ViewModels/ProfileViewModel.cs`
- Modify: `tests/LEC.App.Tests/FakeMailStore.cs`
- Modify: `tests/LEC.App.Tests/ProfileViewModelTests.cs`

**Interfaces:**
- Consumes: `IMailStore.CreateFolderAsync` (Task 1), `FolderTreeViewModel.Build(IReadOnlyList<StoreFolder>)` (existing), `ProfileViewModel.Store`/`ProfileViewModel.FolderTree` (existing).
- Produces: `ProfileViewModel.CreateFolderAsync(string name, int? parentId) : Task<StoreFolder>`. Consumed by Task 3's `MainWindow.xaml.cs` click handlers. Also produces `FakeMailStore.CreateFolderAsync` and `FakeMailStore.ExceptionToThrowOnCreateFolder` (test infrastructure only, used by this task's own tests).

- [ ] **Step 1: Write the failing tests**

Em `tests/LEC.App.Tests/ProfileViewModelTests.cs`, adicione ao final da classe, antes do último `}`:

```csharp
    [Fact]
    public async Task CreateFolderAsyncAddsTheNewFolderToTheTree()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true };
        var store = new FakeMailStore(folders: new[] { root });
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        var created = await profile.CreateFolderAsync("Nova", parentId: null);

        Assert.Null(created.ParentId);
        Assert.Contains(profile.FolderTree.RootNodes, n => n.Folder.Id == created.Id);
    }

    [Fact]
    public async Task CreateFolderAsyncAsASubfolderAddsItUnderTheParentNode()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true };
        var store = new FakeMailStore(folders: new[] { root });
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        var created = await profile.CreateFolderAsync("Filha", parentId: root.Id);

        Assert.Equal(root.Id, created.ParentId);
        var rootNode = Assert.Single(profile.FolderTree.RootNodes);
        Assert.Contains(rootNode.Children, n => n.Folder.Id == created.Id);
    }

    [Fact]
    public async Task CreateFolderAsyncPropagatesStoreExceptions()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true };
        var store = new FakeMailStore(folders: new[] { root })
        {
            ExceptionToThrowOnCreateFolder = new InvalidOperationException("nome inválido"),
        };
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => profile.CreateFolderAsync("X", parentId: null));
        Assert.Equal("nome inválido", ex.Message);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter "CreateFolderAsyncAddsTheNewFolderToTheTree|CreateFolderAsyncAsASubfolderAddsItUnderTheParentNode|CreateFolderAsyncPropagatesStoreExceptions"`
Expected: FAIL to compile — `ProfileViewModel.CreateFolderAsync` and `FakeMailStore.ExceptionToThrowOnCreateFolder` don't exist yet, and `FakeMailStore` doesn't implement the new `IMailStore.CreateFolderAsync` member yet (from Task 1) either, so `FakeMailStore` itself won't compile as an `IMailStore`.

- [ ] **Step 3: Make `FakeMailStore` implement `CreateFolderAsync`**

Em `tests/LEC.App.Tests/FakeMailStore.cs`, troque:

```csharp
    public FakeMailStore(
        IReadOnlyList<StoreFolder> folders,
        Dictionary<int, List<MessageHeader>>? headersByFolder = null,
        Dictionary<MessageRef, byte[]>? rawByRef = null,
        StoreHealth health = StoreHealth.Ok)
    {
        Folders = folders;
        _headersByFolder = headersByFolder ?? new Dictionary<int, List<MessageHeader>>();
        _rawByRef = rawByRef ?? new Dictionary<MessageRef, byte[]>();
        Health = health;
    }

    public IReadOnlyList<StoreFolder> Folders { get; }
    public StoreHealth Health { get; }
```

por:

```csharp
    private readonly List<StoreFolder> _folders;

    public FakeMailStore(
        IReadOnlyList<StoreFolder> folders,
        Dictionary<int, List<MessageHeader>>? headersByFolder = null,
        Dictionary<MessageRef, byte[]>? rawByRef = null,
        StoreHealth health = StoreHealth.Ok)
    {
        _folders = folders.ToList();
        _headersByFolder = headersByFolder ?? new Dictionary<int, List<MessageHeader>>();
        _rawByRef = rawByRef ?? new Dictionary<MessageRef, byte[]>();
        Health = health;
    }

    public IReadOnlyList<StoreFolder> Folders => _folders;
    public StoreHealth Health { get; }

    public Exception? ExceptionToThrowOnCreateFolder { get; set; }

    public Task<StoreFolder> CreateFolderAsync(string name, int? parentId, CancellationToken ct = default)
    {
        if (ExceptionToThrowOnCreateFolder is { } ex)
            throw ex;

        var created = new StoreFolder { Id = _folders.Count + 1, ParentId = parentId, Name = name, IsLoadable = true };
        _folders.Add(created);
        return Task.FromResult(created);
    }
```

(`.ToList()` and `Task`/`CancellationToken` need no new `using` — `System.Linq`, `System.Threading` and `System.Threading.Tasks` are already project-wide implicit usings, confirmed in `LEC.App.Tests`' generated `GlobalUsings.g.cs`.)

- [ ] **Step 4: Add `CreateFolderAsync` to `ProfileViewModel`**

Em `src/LEC.App/ViewModels/ProfileViewModel.cs`, troque:

```csharp
    public ProfileViewModel(string displayName, string profileDirectory, IMailStore store)
    {
        DisplayName = displayName;
        ProfileDirectory = profileDirectory;
        Store = store;
        FolderTree.Build(store.Folders);
    }
}
```

por:

```csharp
    public ProfileViewModel(string displayName, string profileDirectory, IMailStore store)
    {
        DisplayName = displayName;
        ProfileDirectory = profileDirectory;
        Store = store;
        FolderTree.Build(store.Folders);
    }

    public async Task<StoreFolder> CreateFolderAsync(string name, int? parentId)
    {
        var created = await Store.CreateFolderAsync(name, parentId);
        FolderTree.Build(Store.Folders);
        return created;
    }
}
```

(`Task<StoreFolder>` needs no new `using` in `ProfileViewModel.cs` either — same implicit-usings reasoning as above; `StoreFolder` already resolves via the existing `using LEC.MailStore;`.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter "CreateFolderAsyncAddsTheNewFolderToTheTree|CreateFolderAsyncAsASubfolderAddsItUnderTheParentNode|CreateFolderAsyncPropagatesStoreExceptions"`
Expected: PASS — the 3 new tests.

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 6: Commit**

```bash
git add src/LEC.App/ViewModels/ProfileViewModel.cs tests/LEC.App.Tests/FakeMailStore.cs tests/LEC.App.Tests/ProfileViewModelTests.cs
git commit -m "Add ProfileViewModel.CreateFolderAsync: creates a folder and refreshes the bound tree"
```

---

### Task 3: Menu de contexto, diálogo de nome e handlers

**Files:**
- Create: `src/LEC.App/NewFolderDialog.xaml`
- Create: `src/LEC.App/NewFolderDialog.xaml.cs`
- Modify: `src/LEC.App/MainWindow.xaml`
- Modify: `src/LEC.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `ProfileViewModel.CreateFolderAsync` (Task 2), `FolderNodeViewModel.Folder` (existing), `ProfileViewModel` (existing, as each `TreeView`'s `DataContext`).
- Produces: nothing consumed by later tasks — last task of this slice.

Sem TDD aqui — este projeto não tem infraestrutura de teste automatizado pra WPF (`MainWindow.xaml`/`.xaml.cs`, `NewFolderDialog`); toda a lógica testável (validação, orquestração store+árvore) já foi coberta na Task 2. A verificação desta task é o build e a suíte completa passando (Step 5).

- [ ] **Step 1: Create `src/LEC.App/NewFolderDialog.xaml`**

```xml
<!-- src/LEC.App/NewFolderDialog.xaml -->
<Window x:Class="LEC.App.NewFolderDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Nova pasta" Width="360" Height="150"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize" ShowInTaskbar="False">
    <StackPanel Margin="16">
        <TextBlock Text="Nome da pasta:" Margin="0,0,0,4"/>
        <TextBox x:Name="NameTextBox" KeyDown="NameTextBox_KeyDown"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="Cancelar" IsCancel="True" Width="80" Margin="0,0,8,0"/>
            <Button Content="Criar" IsDefault="True" Width="80" Click="Ok_Click"/>
        </StackPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 2: Create `src/LEC.App/NewFolderDialog.xaml.cs`**

```csharp
// src/LEC.App/NewFolderDialog.xaml.cs
using System.Windows;
using System.Windows.Input;

namespace LEC.App;

public partial class NewFolderDialog : Window
{
    public string FolderName { get; private set; } = "";

    public NewFolderDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameTextBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    private void NameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Accept();
    }

    private void Accept()
    {
        FolderName = NameTextBox.Text;
        DialogResult = true;
    }
}
```

- [ ] **Step 3: Add the two `ContextMenu`s to `MainWindow.xaml`**

Em `src/LEC.App/MainWindow.xaml`, troque:

```xml
                                <TreeView ItemsSource="{Binding FolderTree.RootNodes}"
                                          SelectedItemChanged="FolderTreeView_SelectedItemChanged"
                                          BorderThickness="0"
                                          ScrollViewer.VerticalScrollBarVisibility="Disabled"
                                          ScrollViewer.HorizontalScrollBarVisibility="Disabled">
                                    <TreeView.ItemTemplate>
                                        <HierarchicalDataTemplate ItemsSource="{Binding Children}">
                                            <TextBlock Text="{Binding DisplayName}"
                                                       Foreground="{Binding IsLoadable, Converter={StaticResource LoadableToBrushConverter}}"
                                                       FontWeight="{Binding IsSpecial, Converter={StaticResource SpecialToFontWeightConverter}}"
                                                       ToolTip="{Binding IsLoadable, Converter={StaticResource NotLoadableTooltipConverter}}"/>
                                        </HierarchicalDataTemplate>
                                    </TreeView.ItemTemplate>
                                </TreeView>
```

por:

```xml
                                <TreeView ItemsSource="{Binding FolderTree.RootNodes}"
                                          SelectedItemChanged="FolderTreeView_SelectedItemChanged"
                                          BorderThickness="0"
                                          ScrollViewer.VerticalScrollBarVisibility="Disabled"
                                          ScrollViewer.HorizontalScrollBarVisibility="Disabled">
                                    <TreeView.ContextMenu>
                                        <ContextMenu>
                                            <MenuItem Header="Nova pasta..." Click="NewTopLevelFolder_Click"/>
                                        </ContextMenu>
                                    </TreeView.ContextMenu>
                                    <TreeView.ItemTemplate>
                                        <HierarchicalDataTemplate ItemsSource="{Binding Children}">
                                            <TextBlock Text="{Binding DisplayName}"
                                                       Foreground="{Binding IsLoadable, Converter={StaticResource LoadableToBrushConverter}}"
                                                       FontWeight="{Binding IsSpecial, Converter={StaticResource SpecialToFontWeightConverter}}"
                                                       ToolTip="{Binding IsLoadable, Converter={StaticResource NotLoadableTooltipConverter}}">
                                                <TextBlock.ContextMenu>
                                                    <ContextMenu>
                                                        <MenuItem Header="Nova subpasta aqui..." Click="NewSubfolder_Click"/>
                                                    </ContextMenu>
                                                </TextBlock.ContextMenu>
                                            </TextBlock>
                                        </HierarchicalDataTemplate>
                                    </TreeView.ItemTemplate>
                                </TreeView>
```

- [ ] **Step 4: Add the click handlers and the ancestor-lookup helper to `MainWindow.xaml.cs`**

Em `src/LEC.App/MainWindow.xaml.cs`, troque a linha de usings:

```csharp
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LEC.App.Services;
using LEC.App.ViewModels;
using LEC.DbxFormat;
using LEC.MailStore;
using Microsoft.Win32;
```

por:

```csharp
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LEC.App.Services;
using LEC.App.ViewModels;
using LEC.DbxFormat;
using LEC.MailStore;
using Microsoft.Win32;
```

Depois, troque:

```csharp
        return null;
    }

    private void UpdateScavengeButtonVisibility(FolderNodeViewModel node)
```

por:

```csharp
        return null;
    }

    private void NewSubfolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not FolderNodeViewModel node)
            return;
        if ((sender as MenuItem)?.Parent is not ContextMenu { PlacementTarget: FrameworkElement placementTarget })
            return;
        if (FindAncestor<TreeView>(placementTarget) is not { DataContext: ProfileViewModel profile })
            return;

        _ = CreateFolderAsync(profile, node.Folder.Id);
    }

    private void NewTopLevelFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Parent is not ContextMenu { PlacementTarget: FrameworkElement { DataContext: ProfileViewModel profile } })
            return;

        _ = CreateFolderAsync(profile, parentId: null);
    }

    private async Task CreateFolderAsync(ProfileViewModel profile, int? parentId)
    {
        var dialog = new NewFolderDialog { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            await profile.CreateFolderAsync(dialog.FolderName, parentId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LEC — Não foi possível criar a pasta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Percorre a árvore visual a partir de `start` até achar um ancestral do tipo T --
    // necessário porque um ContextMenu não está na mesma árvore visual do elemento que
    // abriu ele (é uma Popup separada); PlacementTarget é o único elo de volta.
    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }
        return null;
    }

    private void UpdateScavengeButtonVisibility(FolderNodeViewModel node)
```

`NewSubfolder_Click`/`NewTopLevelFolder_Click` não são `async void`: um `RoutedEventHandler` não pode retornar `Task`, então eles disparam `CreateFolderAsync` (que é `async Task`) e descartam a referência deliberadamente (`_ = `) — toda exceção dentro dela já é tratada pelo próprio `try`/`catch` com `MessageBox`, então não sobra exceção não observada escapando do fire-and-forget.

- [ ] **Step 5: Build and run the full test suite to verify zero regressions**

Run: `dotnet build`
Expected: `Build succeeded.`

Run: `dotnet test`
Expected: PASS — todos os testes existentes de todos os projetos, sem nenhum novo (esta task não adiciona teste automatizado — ver Global Constraints).

- [ ] **Step 6: Commit**

```bash
git add src/LEC.App/NewFolderDialog.xaml src/LEC.App/NewFolderDialog.xaml.cs src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs
git commit -m "Wire folder creation into the tree: context menus, name dialog, DbxStore.CreateFolderAsync"
```

---

## Self-Review Notes

- **Cobertura do design doc:** §3 (`IMailStore.CreateFolderAsync`/`ProfileViewModel.CreateFolderAsync`) → Tasks 1-2. §4 (por que os menus de contexto funcionam sem rastrear seleção) → Task 3 Step 3 (XAML) e a explicação de `FindAncestor`/`PlacementTarget` em Step 4. §5 (handlers) → Task 3 Step 4, código idêntico ao do design. §6 (`NewFolderDialog`) → Task 3 Steps 1-2, código idêntico ao do design. §7 (testes) → Task 2 Steps 1-5, os 3 testes batem exatamente com os 3 do design.
- **Consistência de tipos:** `IMailStore.CreateFolderAsync(string name, int? parentId, CancellationToken ct = default) : Task<StoreFolder>` (Task 1) é a mesma assinatura chamada em `ProfileViewModel.CreateFolderAsync` (Task 2, via `Store.CreateFolderAsync`) e implementada por `FakeMailStore` (Task 2) com os mesmos nomes de parâmetro. `ProfileViewModel.CreateFolderAsync(string name, int? parentId) : Task<StoreFolder>` (Task 2) é chamado com a mesma assinatura em `MainWindow.xaml.cs`'s `CreateFolderAsync` helper (Task 3).
- **Ordem de dependências:** Task 1 não tem pré-requisito. Task 2 depende de Task 1 (`FakeMailStore` só compila como `IMailStore` depois que a interface ganha o novo membro — por isso o Step 2 da Task 2 espera falha de compilação, não só teste vermelho). Task 3 depende de Task 2 (`ProfileViewModel.CreateFolderAsync` precisa existir antes do handler chamá-lo).
- **Fora de escopo confirmado:** nenhuma task mexe em `DbxStore.cs` (Task 1 só adiciona à interface; a implementação já existe e já bate); nenhuma task adiciona validação de nome no diálogo (fica só em `DbxStore.CreateFolderAsync`, fatia anterior); nenhuma task tenta preservar o estado de expansão da árvore.
