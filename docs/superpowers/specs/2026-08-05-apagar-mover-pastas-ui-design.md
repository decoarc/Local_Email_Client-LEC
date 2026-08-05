# Apagar e mover pastas — UI — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `DbxStore.DeleteFolderAsync`/`DbxStore.MoveFolderAsync`/`IMailStore` (fatia "apagar e mover pastas — motor", ver `docs/superpowers/specs/2026-08-05-apagar-mover-pastas-motor-design.md`), `FolderTreeViewModel`/`FolderNodeViewModel`/`ProfileViewModel` (Fase 2, já estendida por "criar pastas — UI"), `MoveMessageDialog`/`MoveDestinationRules` (fatia "mover mensagem — UI", mesmo padrão de diálogo de árvore-destino), `NewFolderDialog`/menu de contexto da árvore (fatia "criar pastas — UI", mesmo ponto de wiring em `MainWindow.xaml.cs`).
**Referência:** `docs/superpowers/specs/2026-08-04-mover-mensagem-ui-design.md` (mesmo padrão de diálogo modal com árvore de destino); `docs/superpowers/specs/2026-08-04-apagar-mensagem-design.md` §5 (mesmo padrão de confirmação + reset de tela).
**Contexto:** segunda fatia (UI) do sub-projeto "apagar e mover pastas" — a fatia anterior (motor) já entrega `DeleteFolderAsync`/`MoveFolderAsync` funcionais e testados de ponta a ponta sem nenhuma tela; esta fatia só liga esses métodos a gatilhos na árvore de pastas.

## 1. Objetivo

Deixar o usuário apagar uma pasta inteira (com toda a subárvore) e mover uma pasta pra dentro de outra (dentro do mesmo perfil) a partir da árvore de pastas na tela principal, sem nenhum código novo em `LEC.MailStore`/`LEC.DbxFormat`.

## 2. Decisões de escopo (confirmadas com o usuário)

- **Gatilho: menu de contexto por nó da árvore** ("Apagar pasta", "Mover pasta..."), mesmo nó de menu já usado por "Nova subpasta aqui...". Sem drag-and-drop.
- **Pastas especiais não mostram essas opções no menu** — desabilitadas/ausentes, refletindo a validação que o motor já faz (evita o usuário tentar e só descobrir pelo erro).
- **Apagar pede confirmação** (`MessageBox` Sim/Não) informando que subpastas e mensagens dentro delas também serão ocultadas — igual em espírito à confirmação de apagar mensagem, mas o texto precisa deixar claro que é uma árvore inteira, não uma pasta isolada.
- **Mover não pede confirmação extra** — escolher o destino e clicar em "Mover" no próprio diálogo já é a confirmação, mesmo padrão de `MoveMessageDialog`.
- **Diálogo de destino restrito ao mesmo perfil da pasta sendo movida** (o motor não suporta cross-profile pra pastas) — diferente de `MoveMessageDialog`, que mostra todos os perfis abertos.
- **Pasta de origem e toda a sua subárvore ficam invalidadas como destino no diálogo** (não só a própria pasta, como em mover mensagem) — mostradas mas não selecionáveis, mesmo mecanismo visual (cor) já usado em `MoveMessageDialog`/`MoveDestinationValidityConverter`, porque `TreeViewItem.IsEnabled` cascateia pros filhos e quebraria a árvore inteira se aplicado ao nó errado (ver nota de `MoveDestinationRules`).
- **Seleção da árvore é resetada depois de qualquer uma das duas ações** — mesmo padrão já usado depois de criar pasta (`_selectedNode`/`_selectedTreeView` zerados quando a pasta selecionada pertence à árvore reconstruída).

## 3. Menu de contexto na árvore de pastas (`MainWindow.xaml`/`.xaml.cs`)

Dois novos `MenuItem` no `ContextMenu` do `HierarchicalDataTemplate` da árvore, ao lado de "Nova subpasta aqui...": **"Apagar pasta"** e **"Mover pasta..."**. Ambos com `Visibility` ligada a `!node.IsSpecial` (propriedade já existente em `FolderNodeViewModel`) via um `DataTrigger`/`BooleanToVisibilityConverter` no `MenuItem.Style` — mesmo mecanismo que qualquer toggle de visibilidade condicional já usado na árvore hoje, não um novo conceito.

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
```

`ConfirmAndDeleteFolderAsync` (novo, mesmo formato de `ConfirmAndDeleteAsync` já usado pra mensagem):

1. `MessageBox.Show(this, "Apagar a pasta \"{node.DisplayName}\" e tudo dentro dela (subpastas e mensagens)?", "LEC — Apagar pasta", MessageBoxButton.YesNo, MessageBoxImage.Question)`. Não confirmado → retorna sem chamar nada.
2. `treeView.IsEnabled = false` (mesma razão de segurança contra corrida já documentada em `CreateFolderAsync`: `DeleteFolderAsync` muta `_folders` num `Task.Run` sem lock).
3. `try`: `await profile.DeleteFolderAsync(node.Folder.Id)` (novo método em `ProfileViewModel`, ver §5) → reconstrói `FolderTree` internamente, mesmo padrão de `CreateFolderAsync`.
4. Sucesso: se a pasta apagada (ou qualquer uma da sua subárvore) era a pasta atualmente selecionada nesta janela, reseta a seleção — mesmo bloco já usado em `CreateFolderAsync` (`_selectedNode = null; _selectedTreeView = null; ScavengeButton.Visibility = Visibility.Collapsed; RecoverableBar.Visibility = Visibility.Collapsed;`) — mais limpar a lista de mensagens e o painel de leitura (`NavigateToRenderedMessage(string.Empty)` + colapsar `ImagesBlockedBar`), já que a pasta que estava sendo exibida pode ter deixado de existir.
5. Falha: `catch (Exception ex)` → `MessageBox.Show(this, ex.Message, "LEC — Não foi possível apagar a pasta", MessageBoxButton.OK, MessageBoxImage.Warning)`.
6. `finally`: `treeView.IsEnabled = true`.

**Detectar se a seleção atual estava dentro da subárvore apagada:** precisa de uma checagem simples e local (a pasta apagada é `node.Folder.Id`; percorrer `node.Children` recursivamente — a `FolderNodeViewModel` **antiga**, ainda em memória no momento do clique, antes do rebuild — coletando ids, e comparar com `_selectedNode?.Folder.Id`). Não precisa reimplementar a varredura de ciclo do motor: a árvore de UI já é uma árvore de verdade (`ObservableCollection<FolderNodeViewModel> Children`), sem o problema de `ParentId` pendente/cíclico que `FolderTreeViewModel.Build` precisa tolerar na leitura bruta do `DbxStore`.

## 4. `MoveFolderDialog` (`LEC.App`, novo)

Novo `Window` modal, mesmo padrão de `MoveMessageDialog` (`Owner = this`, `WindowStartupLocation="CenterOwner"`), mas com uma única `TreeView` (o perfil de origem, não uma lista de perfis abertos).

**Entrada:** o `ProfileViewModel` da pasta sendo movida, e o `FolderNodeViewModel` de origem (dá acesso tanto ao `Folder.Id` quanto à subárvore em memória, pra excluir descendentes sem duplicar a varredura do motor).

**Conteúdo:** uma `TreeView` ligada a `profile.FolderTree.RootNodes`, mesmo `HierarchicalDataTemplate` já usado em `MainWindow.xaml`.

```csharp
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

Mesmo mecanismo visual de `MoveMessageDialog`/`MoveDestinationValidityConverter` (cor do texto, não `IsEnabled`, pelo motivo de cascata já documentado lá) — um `MoveFolderDestinationValidityConverter` análogo, parametrizado pelo `sourceNode` em vez de `(sourceProfile, sourceFolderId)`. `TreeView_SelectedItemChanged` arma `SelectedFolderId`/habilita o botão "Mover" só quando `IsValidDestination` é verdadeiro pro nó clicado — mesmo formato de handler de `MoveMessageDialog.xaml.cs`, adaptado pra uma única árvore.

**Mover pra raiz:** um item fixo "Pastas locais (raiz)" no topo da `TreeView` (fora da coleção `RootNodes`, sempre válido a menos que a própria pasta de origem já seja um root sendo "movida pra raiz" — nesse caso, tratado como no-op válido pelo motor, não precisa ser bloqueado aqui) representando `newParentId = null`.

**Saída:** `DialogResult = true` mais `SelectedFolderId` (`int?`, `null` = mover pra raiz), só habilitado depois de um clique válido. `DialogResult = false`/fechar sem escolher = cancelar, nada acontece.

## 5. `ProfileViewModel.DeleteFolderAsync`/`MoveFolderAsync`

Mesmo formato fino de `ProfileViewModel.CreateFolderAsync` — chama o `Store`, depois reconstrói a árvore:

```csharp
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
```

## 6. `MoveFolder_Click` (`MainWindow.xaml.cs`)

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

        // Same reset as ConfirmAndDeleteFolderAsync: FolderTree.Build just replaced every
        // node in this tree, so any cached selection belonging to this profile is stale.
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

Reseta a seleção incondicionalmente (ao contrário de apagar, que só reseta se a seleção estava dentro da subárvore afetada) porque **toda** a árvore do perfil é reconstruída por `FolderTree.Build`, e a pasta movida pode ter sido justamente a que estava selecionada, mesmo sem ter sido apagada — mais simples do que replicar a checagem de subárvore de `ConfirmAndDeleteFolderAsync` pra um caso onde ela quase sempre vai disparar de qualquer forma (mover a pasta selecionada é o motivo mais comum de abrir "Mover pasta..." pra começo de conversa).

## 7. Erros e casos de borda

- Pasta de origem ou qualquer descendente dela: acinzentada no diálogo, nunca chega a `MoveFolderAsync`.
- Colisão de nome no destino, pasta especial, `Folders.dbx` danificado: não pré-validados no diálogo — aparecem como `MessageBox` de erro se acontecerem, igual a `MoveMessageDialog`.
- Cancelar qualquer um dos dois diálogos/confirmações: nenhuma chamada ao motor acontece, nenhum estado muda.
- Apagar/mover uma pasta especial: menu nem mostra a opção (item 3), então esse erro do motor só é alcançável se `DbxStore.DeleteFolderAsync`/`MoveFolderAsync` forem chamados fora do fluxo normal da UI — defensivo, não um caminho real.

## 8. Estratégia de testes

`MainWindow.xaml.cs` hoje não tem teste dedicado (mesmo precedente das fatias de mensagem) — verificação manual cobre o menu de contexto, a confirmação de apagar, e o diálogo de mover.

- `ProfileViewModelTests` (extensão, com fake `IMailStore`): `DeleteFolderAsync`/`MoveFolderAsync` chamam o `Store` correspondente e reconstroem `FolderTree`.
- `MoveFolderDestinationRulesTests`: pasta de origem inválida como destino; descendente direto inválido; neta inválida; pasta não relacionada válida; pasta-mãe atual da origem válida (reordenar dentro do mesmo pai é permitido).
- Verificação manual: apagar pasta com subpastas e mensagens (confirma que tudo some da árvore e da lista); cancelar a confirmação de apagar; mover pasta pra outro pai (confirma que subpastas continuam visíveis dentro dela no novo lugar); tentar mover pra dentro de si mesma/descendente (confirma que fica acinzentado, nunca habilita "Mover"); pastas especiais sem as opções no menu.

## 9. Fora de escopo / decisões adiadas

- Drag-and-drop como gatilho alternativo (mesma decisão já tomada pra mover mensagem).
- Mover pasta entre perfis diferentes (já fora de escopo desde o motor).
- Pré-validar colisão de nome/pasta especial/`Folders.dbx` danificado no diálogo antes de tentar mover.
- Restaurar uma pasta apagada pela UI (já fora de escopo desde o motor).
- Seleção múltipla de pastas.
