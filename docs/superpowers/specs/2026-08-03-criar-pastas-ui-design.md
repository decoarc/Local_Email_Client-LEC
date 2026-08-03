# Criar pastas — integração na UI — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `LEC.MailStore.DbxStore.CreateFolderAsync` (fatia 1, branch `criar-pastas-motor-dbx`, commit `f0d578a` — cria um `.dbx` vazio + registra a pasta em `Folders.dbx`, com backup e checagem de saúde). `LEC.App.ViewModels.ProfileViewModel`/`FolderTreeViewModel`/`FolderNodeViewModel` (existentes, Fase 2).
**Referência:** `docs/superpowers/specs/2026-08-03-criar-pastas-motor-dbx-design.md` (fatia 1 — motor de escrita, sem UI).
**Contexto:** segundo sub-projeto do replaneio do projeto (branch `criar-pastas-motor-dbx`, a partir de `fase4-simplificada`). A fatia 1 entregou `CreateFolderAsync` testável só por chamada direta; esta fatia liga isso à árvore visível na tela.

## 1. Objetivo

Deixar o usuário criar uma pasta nova (top-level ou subpasta) direto pela árvore: clique direito → "Nova pasta"/"Nova subpasta aqui" → digitar nome → pasta aparece na árvore.

## 2. Escopo

**Dentro:**
- `IMailStore.CreateFolderAsync(string name, int? parentId, CancellationToken ct = default) : Task<StoreFolder>` — promovido de `DbxStore` (fatia 1) pra interface, pra UI chamar de forma polimórfica. Custo zero hoje (só `DbxStore` implementa `IMailStore`; `PstStore` não existe).
- `ProfileViewModel.CreateFolderAsync(string name, int? parentId) : Task<StoreFolder>` — chama `Store.CreateFolderAsync`, depois `FolderTree.Build(Store.Folders)` pra atualizar a árvore vinculada.
- Menu de contexto na árvore: clique direito numa pasta existente → "Nova subpasta aqui..."; clique direito em área vazia da árvore → "Nova pasta..." (cria no topo, `parentId = null`).
- `NewFolderDialog` — janela modal nova: campo de texto + botões Criar/Cancelar.
- Erro de `CreateFolderAsync` (nome duplicado, `Folders.dbx` danificado, nome reservado do Windows, etc.) → `MessageBox` com a mensagem em português já produzida pela camada de baixo.
- `FakeMailStore` (test double) ganha uma implementação em memória de `CreateFolderAsync`, pra `ProfileViewModelTests` poder testar sem tocar em arquivo real.

**Fora:**
- Mover mensagens, apagar/renomear pasta — fatias futuras.
- Preservar o estado de expansão/colapso da árvore ao atualizar após criar uma pasta — decisão explícita: aceitável a árvore recolher (mesmo comportamento que `FolderTreeViewModel.Build` já tem hoje, usado hoje só na abertura do perfil).
- Testes automatizados de `MainWindow.xaml.cs`/menus de contexto — sem infraestrutura de teste de UI neste projeto; a cobertura para na camada de `ProfileViewModel`.
- `PstStore` continua não implementado.

## 3. `IMailStore.CreateFolderAsync` e `ProfileViewModel.CreateFolderAsync`

```csharp
// IMailStore.cs
Task<StoreFolder> CreateFolderAsync(string name, int? parentId, CancellationToken ct = default);
```

`DbxStore` já implementa exatamente essa assinatura (fatia 1) — só precisa declarar que satisfaz a interface (sem mudança de corpo).

```csharp
// ProfileViewModel.cs
public async Task<StoreFolder> CreateFolderAsync(string name, int? parentId)
{
    var created = await Store.CreateFolderAsync(name, parentId);
    FolderTree.Build(Store.Folders);
    return created;
}
```

Propaga qualquer exceção de `Store.CreateFolderAsync` sem capturar — quem trata é o code-behind (§5).

## 4. Menus de contexto — por que funcionam sem rastrear seleção

Um `ContextMenu` do WPF herda automaticamente o `DataContext` do seu `PlacementTarget` (comportamento nativo). Colocando um `ContextMenu` no `TextBlock` de cada nó (dentro do `HierarchicalDataTemplate` de `MainWindow.xaml`), cada `MenuItem` dentro dele já enxerga aquele nó específico como `DataContext` (`FolderNodeViewModel`). Colocando outro `ContextMenu` na própria `TreeView` (cujo `DataContext` é o `ProfileViewModel`), o clique numa área vazia (sem bater em nenhum item) abre esse outro menu — o roteamento de eventos do WPF resolve isso sozinho: o menu do elemento mais interno clicado sempre tem prioridade sobre o mais externo. Nenhuma lógica de seleção manual é necessária.

```xml
<!-- Por nó, dentro do HierarchicalDataTemplate existente -->
<TextBlock ...>
    <TextBlock.ContextMenu>
        <ContextMenu>
            <MenuItem Header="Nova subpasta aqui..." Click="NewSubfolder_Click"/>
        </ContextMenu>
    </TextBlock.ContextMenu>
</TextBlock>

<!-- Na TreeView, dentro do DataTemplate do ItemsControl de OpenProfiles -->
<TreeView ...>
    <TreeView.ContextMenu>
        <ContextMenu>
            <MenuItem Header="Nova pasta..." Click="NewTopLevelFolder_Click"/>
        </ContextMenu>
    </TreeView.ContextMenu>
</TreeView>
```

## 5. Handlers no code-behind

```csharp
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

// Percorre a árvore visual a partir de `start` até achar um ancestral do tipo T —
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
```

`NewSubfolder_Click`/`NewTopLevelFolder_Click` não são `async void` — disparam `CreateFolderAsync` (a `Task`) e descartam deliberadamente (`_ = `), porque um `RoutedEventHandler` não pode ser `async Task`; exceções dentro de `CreateFolderAsync` já são tratadas ali dentro (try/catch com `MessageBox`), então não há exceção não observada escapando do fire-and-forget.

## 6. `NewFolderDialog`

```xml
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

```csharp
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

`IsCancel="True"` no botão Cancelar já faz Esc fechar com `DialogResult = false` (recurso nativo do WPF, sem código extra). Sem validação de nome no diálogo — ele só coleta o texto puro; quem valida (nome vazio, colisão, caractere inválido, nome reservado) é `CreateFolderAsync` (fatia 1), e o erro volta como `MessageBox` (§5).

## 7. Testes

`FakeMailStore` ganha uma implementação em memória de `CreateFolderAsync`:

```csharp
private readonly List<StoreFolder> _folders; // trocar o campo Folders (hoje IReadOnlyList<StoreFolder> vindo direto do parâmetro) por uma List<StoreFolder> mutável, como em DbxStore

public Task<StoreFolder> CreateFolderAsync(string name, int? parentId, CancellationToken ct = default)
{
    if (ExceptionToThrowOnCreateFolder is { } ex)
        throw ex;

    var created = new StoreFolder { Id = _folders.Count + 1, ParentId = parentId, Name = name, IsLoadable = true };
    _folders.Add(created);
    return Task.FromResult(created);
}

public Exception? ExceptionToThrowOnCreateFolder { get; set; }
```

`tests/LEC.App.Tests/ProfileViewModelTests.cs`:
- `CreateFolderAsyncAddsTheNewFolderToTheTree` — cria pasta raiz (`parentId: null`), confirma que aparece em `FolderTree.RootNodes`.
- `CreateFolderAsyncAsASubfolderAddsItUnderTheParentNode` — cria como filha de um nó existente, confirma que aparece em `Children` do nó pai certo.
- `CreateFolderAsyncPropagatesStoreExceptions` — `FakeMailStore.ExceptionToThrowOnCreateFolder` configurado, confirma que `ProfileViewModel.CreateFolderAsync` propaga a mesma exceção (não engole).

## 8. Fora de escopo / decisões adiadas

- Preservar expansão/colapso da árvore no refresh — decisão explícita, aceitável por ora.
- Testes de `MainWindow.xaml.cs`/menus de contexto — sem infraestrutura de teste de UI no projeto.
- Mover mensagens, apagar/renomear pasta — fatias futuras da Fase 5.
- `PstStore` — não existe ainda.
