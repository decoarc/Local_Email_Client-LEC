# Múltiplos perfis abertos simultaneamente — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Permitir abrir mais de um perfil `.dbx` ao mesmo tempo (ex.: `BCP` e `BCP2`), cada um com sua própria árvore de pastas visível lado a lado na coluna esquerda, enquanto lista de mensagens, painel de leitura e varredura (Fase 3) continuam únicos e sempre refletem a pasta clicada por último em qualquer árvore.

**Architecture:** `ProfileViewModel` (novo, `LEC.App`) agrupa `Store`/`FolderTree`/`DisplayName` de um perfil aberto. `MainWindowViewModel.OpenProfiles` (`ObservableCollection<ProfileViewModel>`) substitui o `Store` único de hoje — `OpenProfile` sempre adiciona, nunca substitui. `Store` vira uma propriedade computada a partir do "perfil ativo" (dono da última pasta selecionada); `MessageList`/`MessageReader` continuam settable via `[ObservableProperty]` (preserva a correção de binding do C1/Fase 2), só passam a ser recriados quando o perfil ativo muda, e reaproveitados ao trocar de pasta dentro do mesmo perfil. O `TreeView` único vira um `ItemsControl` com uma árvore por perfil aberto, cada uma identificando seu dono via `DataContext` nos handlers de evento — sem precisar de nomes fixos por árvore.

**Tech Stack:** .NET 9, C#, WPF, CommunityToolkit.Mvvm 8.4.2 (`[NotifyPropertyChangedFor]`), xUnit. Nenhuma dependência nova.

## Global Constraints

- `LEC.MailStore` e `LEC.DbxFormat` não mudam nesta fase — cada perfil já é uma instância independente de `IMailStore`; a novidade é inteiramente de orquestração na `LEC.App` (ver design §3).
- `OpenProfile` sempre adiciona um novo perfil a `OpenProfiles` — nunca substitui um já aberto (design §2, §5).
- Sem limite fixo de quantos perfis podem ficar abertos ao mesmo tempo (design §2).
- Abrir o mesmo diretório de perfil duas vezes é permitido, sem tratamento especial — é leitura, então é inofensivo (design §2).
- Cada árvore é identificada pelo nome do diretório do perfil (`DisplayName`), sem tratamento de duplicata (design §2).
- Fechar um perfil é feito por um botão "×" no cabeçalho da sua árvore (design §2, §5).
- Continua tudo somente-leitura — nenhuma escrita em `.dbx`/`.pst` de origem, em nenhuma circunstância (`ARQUITETURA.md` §2, reafirmado no design §1).
- Sem automação de teste de UI/XAML — mesmo padrão já estabelecido desde a Fase 2 (verificação manual apenas para o passo final).
- **`tests/LEC.App.Tests` referencia o projeto `LEC.App` inteiro** — qualquer erro de compilação em `MainWindow.xaml.cs` derruba a compilação de **todos** os testes desse projeto, não só os novos. Por isso `MainWindowViewModel` e `MainWindow.xaml`/`MainWindow.xaml.cs` mudam juntos, na mesma task — nunca deixe uma task terminada com a assinatura pública do `MainWindowViewModel` mudada mas `MainWindow.xaml.cs` ainda chamando a assinatura antiga.

---

## File Structure

```
src/LEC.App/
  ViewModels/
    ProfileViewModel.cs         Task 1 — NEW
    MainWindowViewModel.cs      Task 2 — MODIFIED
  MainWindow.xaml               Task 2 — MODIFIED
  MainWindow.xaml.cs            Task 2 — MODIFIED

tests/LEC.App.Tests/
  ProfileViewModelTests.cs      Task 1 — NEW
  FakeMailStore.cs              Task 2 — MODIFIED: +WasDisposed
  MainWindowViewModelTests.cs   Task 2 — MODIFIED
```

---

### Task 1: `ProfileViewModel`

**Files:**
- Create: `src/LEC.App/ViewModels/ProfileViewModel.cs`
- Test: `tests/LEC.App.Tests/ProfileViewModelTests.cs`

**Interfaces:**
- Consumes: `IMailStore` (`LEC.MailStore`, existente), `FolderTreeViewModel` (existente, `LEC.App.ViewModels`).
- Produces: `ProfileViewModel(string displayName, string profileDirectory, IMailStore store)` com propriedades `DisplayName`, `ProfileDirectory`, `Store`, `FolderTree` (já populada via `FolderTree.Build(store.Folders)` no construtor). Consumido pela Task 2.
- Este tipo é novo e não referenciado por nada existente ainda — pode ser adicionado isoladamente sem quebrar `MainWindow.xaml.cs` ou qualquer teste já existente.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LEC.App.Tests/ProfileViewModelTests.cs
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class ProfileViewModelTests
{
    [Fact]
    public void ConstructorBuildsTheFolderTreeFromTheStoresFolders()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true };
        var child = new StoreFolder { Id = 2, ParentId = 1, Name = "Filha", IsLoadable = true };
        var store = new FakeMailStore(folders: new[] { root, child });

        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        Assert.Equal("BCP", profile.DisplayName);
        Assert.Equal(@"C:\perfis\BCP", profile.ProfileDirectory);
        Assert.Same(store, profile.Store);
        var rootNode = Assert.Single(profile.FolderTree.RootNodes);
        Assert.Equal("Raiz", rootNode.DisplayName);
        Assert.Single(rootNode.Children);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LEC.App.Tests --filter ProfileViewModelTests`
Expected: FAIL to build — `ProfileViewModel` doesn't exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/LEC.App/ViewModels/ProfileViewModel.cs
using LEC.MailStore;

namespace LEC.App.ViewModels;

/// <summary>
/// One open profile: its store, its own folder tree, and the label shown above that tree
/// in the UI. MainWindowViewModel holds a collection of these — one per profile the user
/// has opened, none of them ever replacing another (see MainWindowViewModel.OpenProfile).
/// </summary>
public sealed class ProfileViewModel
{
    public string DisplayName { get; }
    public string ProfileDirectory { get; }
    public IMailStore Store { get; }
    public FolderTreeViewModel FolderTree { get; } = new();

    public ProfileViewModel(string displayName, string profileDirectory, IMailStore store)
    {
        DisplayName = displayName;
        ProfileDirectory = profileDirectory;
        Store = store;
        FolderTree.Build(store.Folders);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LEC.App.Tests --filter ProfileViewModelTests`
Expected: PASS (1 test)

Run: `dotnet test tests/LEC.App.Tests`
Expected: all pre-existing tests still pass — this task only adds a new, unreferenced type.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/ProfileViewModel.cs tests/LEC.App.Tests/ProfileViewModelTests.cs
git commit -m "Add ProfileViewModel: store + own folder tree + display name for one open profile"
```

---

### Task 2: `MainWindowViewModel` multi-profile API + `MainWindow` UI rewiring

**Files:**
- Modify: `src/LEC.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/LEC.App/MainWindow.xaml`
- Modify: `src/LEC.App/MainWindow.xaml.cs`
- Modify: `tests/LEC.App.Tests/FakeMailStore.cs`
- Modify: `tests/LEC.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `ProfileViewModel` (Task 1), `DbxStore.Open`/`IMailStore` (existente, `LEC.MailStore`), `MessageListViewModel`/`MessageReaderViewModel` (existente).
- Produces: `MainWindowViewModel.OpenProfiles : ObservableCollection<ProfileViewModel>`, `.ActiveProfile : ProfileViewModel?`, `.Store : IMailStore?` (computada de `ActiveProfile`), `.SelectFolderAsync(ProfileViewModel, FolderNodeViewModel, CancellationToken) : Task`, `.CloseProfile(ProfileViewModel) : void`, `.RunScavengeAsync(ProfileViewModel, FolderNodeViewModel, IProgress<double>?, CancellationToken) : Task`. `MainWindow.xaml`/`.xaml.cs` passam a consumir essa API nova diretamente.
- **Esta task muda a assinatura pública consumida por `MainWindow.xaml.cs` (ver Global Constraints) — o ViewModel e a UI mudam juntos, num só conjunto de edições, antes de rodar `dotnet build`/`dotnet test` pela primeira vez nesta task.**

- [ ] **Step 1: Write the failing tests**

Modifique `tests/LEC.App.Tests/FakeMailStore.cs` — troque o `Dispose()` no-op por um que sinaliza (precisa para o teste de `CloseProfile` abaixo):

```csharp
    public bool WasDisposed { get; private set; }

    public void Dispose() => WasDisposed = true;
```

(Substitui a linha `public void Dispose() { }` existente.)

Substitua o conteúdo inteiro de `tests/LEC.App.Tests/MainWindowViewModelTests.cs`:

```csharp
// tests/LEC.App.Tests/MainWindowViewModelTests.cs
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class MainWindowViewModelTests
{
    [Fact]
    public void OpenProfileWithANonExistentPathSetsAStatusMessageInsteadOfThrowing()
    {
        var vm = new MainWindowViewModel();

        vm.OpenProfile(Path.Combine(Path.GetTempPath(), $"lec-does-not-exist-{Guid.NewGuid():N}"));

        Assert.Null(vm.Store);
        Assert.Empty(vm.OpenProfiles);
        Assert.NotNull(vm.StatusText);
    }

    private static string? FindRealFixtureDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LEC.sln")))
            dir = dir.Parent;

        if (dir is null)
            return null;

        string candidate = Path.Combine(dir.FullName, "BCP");
        return Directory.Exists(candidate) ? candidate : null;
    }

    [Fact]
    public void RealFixture_OpeningTheSameProfileTwiceAddsTwoIndependentEntries()
    {
        string? profileDir = FindRealFixtureDirectory();
        if (profileDir is null)
            return; // BCP/ not present on this machine — soft skip

        var vm = new MainWindowViewModel();

        vm.OpenProfile(profileDir);
        vm.OpenProfile(profileDir);

        Assert.Equal(2, vm.OpenProfiles.Count);
        Assert.NotSame(vm.OpenProfiles[0].Store, vm.OpenProfiles[1].Store);

        foreach (var profile in vm.OpenProfiles)
            profile.Store.Dispose();
    }

    [Fact]
    public async Task SelectFolderAsyncMakesTheProfileActiveAndLoadsItsMessageList()
    {
        var folder = new StoreFolder { Id = 1, Name = "X", IsLoadable = true };
        var header = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Ola" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { header } });
        var profile = new ProfileViewModel("Perfil A", @"C:\perfil-a", store);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profile);

        await vm.SelectFolderAsync(profile, new FolderNodeViewModel(folder));

        Assert.Same(store, vm.Store);
        Assert.NotNull(vm.MessageList);
        Assert.Single(vm.MessageList!.Messages);
        Assert.Equal("Ola", vm.MessageList.Messages[0].Subject);
    }

    [Fact]
    public async Task SelectingAnotherFolderInTheSameProfileReusesTheSameMessageListInstance()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true };
        var store = new FakeMailStore(folders: new[] { folderA, folderB });
        var profile = new ProfileViewModel("Perfil", @"C:\perfil", store);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profile);

        await vm.SelectFolderAsync(profile, new FolderNodeViewModel(folderA));
        var firstMessageList = vm.MessageList;

        await vm.SelectFolderAsync(profile, new FolderNodeViewModel(folderB));

        Assert.Same(firstMessageList, vm.MessageList);
    }

    [Fact]
    public async Task SelectingAFolderInADifferentProfileSwitchesTheActiveStoreAndReplacesMessageListAndReader()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true };
        var storeA = new FakeMailStore(folders: new[] { folderA });
        var storeB = new FakeMailStore(folders: new[] { folderB });
        var profileA = new ProfileViewModel("Perfil A", @"C:\a", storeA);
        var profileB = new ProfileViewModel("Perfil B", @"C:\b", storeB);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profileA);
        vm.OpenProfiles.Add(profileB);

        await vm.SelectFolderAsync(profileA, new FolderNodeViewModel(folderA));
        var messageListA = vm.MessageList;
        var messageReaderA = vm.MessageReader;

        await vm.SelectFolderAsync(profileB, new FolderNodeViewModel(folderB));

        Assert.Same(storeB, vm.Store);
        Assert.NotSame(messageListA, vm.MessageList);
        Assert.NotSame(messageReaderA, vm.MessageReader);
    }

    [Fact]
    public async Task CloseProfileRemovesItFromOpenProfilesAndDisposesItsStore()
    {
        var folder = new StoreFolder { Id = 1, Name = "X", IsLoadable = true };
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfil", store);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profile);
        await vm.SelectFolderAsync(profile, new FolderNodeViewModel(folder));

        vm.CloseProfile(profile);

        Assert.DoesNotContain(profile, vm.OpenProfiles);
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task CloseProfileOnTheActiveProfileClearsMessageListReaderAndScavengeStatus()
    {
        var folder = new StoreFolder { Id = 1, Name = "X", IsLoadable = true };
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfil", store);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profile);
        await vm.SelectFolderAsync(profile, new FolderNodeViewModel(folder));

        vm.CloseProfile(profile);

        Assert.Null(vm.Store);
        Assert.Null(vm.MessageList);
        Assert.Null(vm.MessageReader);
        Assert.Null(vm.CurrentFolderScavengeStatus);
    }

    [Fact]
    public async Task CloseProfileOnANonActiveProfileLeavesTheActiveProfileUntouched()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true };
        var storeA = new FakeMailStore(folders: new[] { folderA });
        var storeB = new FakeMailStore(folders: new[] { folderB });
        var profileA = new ProfileViewModel("Perfil A", @"C:\a", storeA);
        var profileB = new ProfileViewModel("Perfil B", @"C:\b", storeB);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profileA);
        vm.OpenProfiles.Add(profileB);
        await vm.SelectFolderAsync(profileA, new FolderNodeViewModel(folderA));
        var messageListA = vm.MessageList;

        vm.CloseProfile(profileB);

        Assert.Same(storeA, vm.Store);
        Assert.Same(messageListA, vm.MessageList);
        Assert.Contains(profileA, vm.OpenProfiles);
        Assert.DoesNotContain(profileB, vm.OpenProfiles);
    }
}
```

(Isso substitui os dois testes antigos, incluindo `SelectFolderAsyncIsANoOpWhenNoProfileIsOpenYet` — sua premissa não existe mais: `SelectFolderAsync` passa a exigir um `ProfileViewModel` real, então "nenhum perfil aberto" deixa de ser um estado alcançável por esse método.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter MainWindowViewModelTests`
Expected: FAIL to build — `MainWindowViewModel.OpenProfiles`/`SelectFolderAsync(profile, node)`/`CloseProfile` não existem ainda.

- [ ] **Step 3: Implement `MainWindowViewModel`**

Substitua o conteúdo inteiro de `src/LEC.App/ViewModels/MainWindowViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.DbxFormat;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<ProfileViewModel> OpenProfiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Store))]
    private ProfileViewModel? _activeProfile;

    public IMailStore? Store => ActiveProfile?.Store;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private MessageListViewModel? _messageList;

    [ObservableProperty]
    private MessageReaderViewModel? _messageReader;

    [ObservableProperty]
    private ScavengeStatus? _currentFolderScavengeStatus;

    public void OpenProfile(string profileDirectory)
    {
        try
        {
            var store = DbxStore.Open(profileDirectory);
            string displayName = new DirectoryInfo(profileDirectory).Name;
            OpenProfiles.Add(new ProfileViewModel(displayName, profileDirectory, store));

            StatusText = store.Health == StoreHealth.Ok
                ? null
                : $"Perfil aberto com avisos (Health: {store.Health}).";
        }
        catch (Exception ex) when (ex is DbxFormatException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Não foi possível abrir o perfil: {ex.Message}";
        }
    }

    public void CloseProfile(ProfileViewModel profile)
    {
        bool wasActive = ReferenceEquals(ActiveProfile, profile);

        OpenProfiles.Remove(profile);
        profile.Store.Dispose();

        if (wasActive)
        {
            ActiveProfile = null;
            MessageList = null;
            MessageReader = null;
            CurrentFolderScavengeStatus = null;
        }
    }

    public async Task SelectFolderAsync(ProfileViewModel profile, FolderNodeViewModel node, CancellationToken ct = default)
    {
        if (!ReferenceEquals(ActiveProfile, profile))
        {
            ActiveProfile = profile;
            MessageList = new MessageListViewModel(profile.Store);
            MessageReader = new MessageReaderViewModel(profile.Store);
        }

        CurrentFolderScavengeStatus = profile.Store.GetScavengeStatus(node.Folder);
        await MessageList!.LoadAsync(node.Folder, ct);
    }

    public async Task SelectMessageAsync(MessageHeader header, CancellationToken ct = default)
    {
        if (MessageReader is not null)
            await MessageReader.LoadAsync(header.Id, ct);
    }

    public async Task RunScavengeAsync(ProfileViewModel profile, FolderNodeViewModel node, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        CurrentFolderScavengeStatus = await profile.Store.ScavengeAsync(node.Folder, progress, ct);
    }
}
```

(Isso remove a antiga propriedade `FolderTree` — só era usada pelo `TreeView` único, que o Step 4 abaixo substitui — e as antigas assinaturas de um argumento só de `SelectFolderAsync`/`RunScavengeAsync`. Neste ponto, `MainWindow.xaml`/`MainWindow.xaml.cs` ainda não compilam — normal, siga direto para o próximo step sem rodar build/test ainda.)

- [ ] **Step 4: Rewire `MainWindow.xaml`**

Em `src/LEC.App/MainWindow.xaml`, troque o bloco do `TreeView` único:

```xml
            <TreeView Grid.Column="0" x:Name="FolderTreeView"
                      ItemsSource="{Binding FolderTree.RootNodes}"
                      SelectedItemChanged="FolderTreeView_SelectedItemChanged">
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

por um `ItemsControl` com uma árvore por perfil aberto:

```xml
            <ScrollViewer Grid.Column="0" VerticalScrollBarVisibility="Auto">
                <ItemsControl ItemsSource="{Binding OpenProfiles}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <StackPanel Margin="0,0,0,8">
                                <DockPanel Background="#DCE3EA" Margin="0,0,0,2">
                                    <Button DockPanel.Dock="Right" Content="×" Width="22" Height="22" Margin="2"
                                            Click="CloseProfile_Click" ToolTip="Fechar perfil"/>
                                    <TextBlock Text="{Binding DisplayName}" FontWeight="Bold"
                                               VerticalAlignment="Center" Margin="4,0,0,0"/>
                                </DockPanel>
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
                            </StackPanel>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </ScrollViewer>
```

(A árvore interna desativa seu próprio scroll para que só exista o scroll do `ScrollViewer` externo, em volta de todas as árvores empilhadas — evita scrolls aninhados conflitantes. `x:Name="FolderTreeView"` some — cada árvore agora é uma instância anônima gerada pelo template; o Step 5 identifica o dono de cada evento via `DataContext`, não por nome.)

- [ ] **Step 5: Rewire `MainWindow.xaml.cs`**

Adicione dois campos, junto aos existentes (`_imageBlocker`, `_scavengeCts`):

```csharp
    private ProfileViewModel? _selectedProfile;
    private FolderNodeViewModel? _selectedNode;
```

Substitua `FolderTreeView_SelectedItemChanged` inteiro:

```csharp
    private async void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        try
        {
            if (sender is TreeView { DataContext: ProfileViewModel profile } && e.NewValue is FolderNodeViewModel node)
            {
                _selectedProfile = profile;
                _selectedNode = node;
                await _viewModel.SelectFolderAsync(profile, node);
                UpdateScavengeButtonVisibility(node);
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível carregar esta pasta: {ex.Message}";
        }
    }
```

(`UpdateScavengeButtonVisibility` e `HasPartialHealth` não mudam — continuam lendo `_viewModel.MessageList`/`_viewModel.Store`, que agora refletem o perfil ativo corretamente.)

Substitua `ToggleRecoverableButton_Click`:

```csharp
    private async void ToggleRecoverableButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is not { } profile || _selectedNode is not { } node)
            return;

        try
        {
            bool newVisibility = !(_viewModel.CurrentFolderScavengeStatus?.IsVisible ?? false);
            profile.Store.SetScavengeVisibility(node.Folder, newVisibility);
            await _viewModel.SelectFolderAsync(profile, node); // reload the list + refresh status/button state
            UpdateScavengeButtonVisibility(node);
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível alternar a exibição de recuperáveis: {ex.Message}";
        }
    }
```

Substitua `ScavengeButton_Click`:

```csharp
    private async void ScavengeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is not { } profile || _selectedNode is not { } node)
            return;

        _scavengeCts = new CancellationTokenSource();
        try
        {
            ScavengeButton.Visibility = Visibility.Collapsed;
            CancelScavengeButton.Visibility = Visibility.Visible;

            var progress = new Progress<double>(p => _viewModel.StatusText = $"Varrendo pasta... {p:P0}");
            await _viewModel.RunScavengeAsync(profile, node, progress, _scavengeCts.Token);

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
```

Adicione `CloseProfile_Click` (novo, logo após `OpenProfile_Click`):

```csharp
    private void CloseProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileViewModel profile)
            return;

        if (ReferenceEquals(_selectedProfile, profile))
        {
            _selectedProfile = null;
            _selectedNode = null;
            ScavengeButton.Visibility = Visibility.Collapsed;
            CancelScavengeButton.Visibility = Visibility.Collapsed;
            RecoverableBar.Visibility = Visibility.Collapsed;
        }

        _viewModel.CloseProfile(profile);
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet build`
Expected: `Build succeeded.`

Run: `dotnet test`
Expected: todos os testes passam nos três projetos, incluindo `MainWindowViewModelBindingTests` (que faz `vm.MessageList = messageList;` diretamente — continua funcionando porque `MessageList` permanece uma propriedade `[ObservableProperty]` com setter público, não foi tornada computada).

- [ ] **Step 7: Verificação manual (Layer 1)**

Run: `dotnet run --project src/LEC.App`, confirme que a janela abre sem `XamlParseException` e fecha normalmente (mesmo check de toda task de UI deste projeto). Verificação funcional completa (abrir dois perfis, alternar seleção, fechar um) fica para a Task 3.

- [ ] **Step 8: Commit**

```bash
git add src/LEC.App/ViewModels/MainWindowViewModel.cs src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs tests/LEC.App.Tests/FakeMailStore.cs tests/LEC.App.Tests/MainWindowViewModelTests.cs
git commit -m "Support multiple simultaneously open profiles: one folder tree each, shared list/reader/varredura"
```

---

### Task 3: Verificação manual contra perfis reais

**Files:** nenhum (só verificação — mesmo formato da Fase 3 Task 12).

- [ ] **Step 1: Rodar a suíte inteira**

Run: `dotnet test`
Expected: todos os testes passam nos três projetos.

- [ ] **Step 2: Abrir dois perfis reais lado a lado**

Run: `dotnet run --project src/LEC.App`. Use "Arquivo > Abrir Perfil..." para abrir `BCP/` (se existir na máquina — mesmo soft-skip dos testes automatizados) e, em seguida, abrir o mesmo diretório de novo (simulando "BCP2", já que só existe um perfil real disponível). Confirmar:
1. As duas árvores aparecem empilhadas na coluna esquerda, cada uma com seu próprio cabeçalho e botão "×".
2. Clicar numa pasta na primeira árvore carrega a lista de mensagens correspondente; clicar numa pasta na segunda árvore troca a lista para a pasta certa do segundo perfil.
3. A pasta "Caixa de entrada" tem `Health = Parcial` no perfil real (confirmado na Fase 3 Task 12): selecioná-la mostra o botão de varredura; rodar a varredura funciona igual à Fase 3; fechar o outro perfil (o "×" da árvore que não está com a pasta selecionada) não afeta o estado da varredura em andamento nem a lista visível.
4. Fechar o perfil cuja pasta está selecionada limpa a lista/leitura/barra de varredura, mas a outra árvore continua visível e clicável.

- [ ] **Step 3: Reportar e, se necessário, corrigir**

Se a verificação manual encontrar um bug real, corrigir e commitar com uma mensagem específica descrevendo o que foi corrigido — não uma mensagem genérica de "ajustes finais". Se tudo passar sem necessidade de mudança de código, reportar isso claramente e não criar um commit vazio/marcador.
