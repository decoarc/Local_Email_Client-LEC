# Mover mensagem — UI — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `MainWindowViewModel.MoveMessageAsync`/`IMailStore.AddMessageAsync`/`IMailStore.HideMovedMessageAsync` (fatia "mover mensagem — motor", já mesclada em `fase4-simplificada`), `ProfileViewModel`/`FolderTreeViewModel`/`FolderNodeViewModel` (Fase 2), `NewFolderDialog` (fatia "criar pastas — UI", mesmo padrão de diálogo modal já mesclado).
**Referência:** `docs/superpowers/specs/2026-08-04-mover-mensagem-motor-design.md` (motor que esta fatia consome, sem nenhuma mudança).
**Contexto:** segunda fatia (UI) do sub-projeto "mover mensagem" — a primeira fatia (motor) já entrega `MoveMessageAsync` funcional e testado de ponta a ponta sem nenhuma tela; esta fatia só liga esse método a um gatilho na interface.

## 1. Objetivo

Deixar o usuário mover uma mensagem de uma pasta pra outra — inclusive entre perfis diferentes, se ambos estiverem abertos — a partir da tela principal, sem precisar de nenhum código novo em `LEC.MailStore`/`LEC.DbxFormat` além de um campo estrutural pequeno em `StoreFolder`.

## 2. Decisões de escopo (confirmadas com o usuário)

- **Gatilho: menu de contexto na lista de mensagens** ("Mover para..."), não drag-and-drop. Mesmo nível de fricção que criar pasta hoje; drag-and-drop fica pra decidir depois, se fizer falta.
- **Diálogo de destino mostra todos os perfis abertos**, não só o perfil da mensagem — usa o suporte a cross-profile que o motor já tem pronto.
- **Pastas inválidas como destino ficam desabilitadas/acinzentadas no diálogo** (sem arquivo de apoio, ou a própria pasta de origem da mensagem) — em vez de deixar selecionar e só falhar depois.
- **Sem diálogo de confirmação extra** — escolher a pasta e clicar em "Mover" no próprio diálogo já é a confirmação.
- **Painel de leitura é limpo depois de mover com sucesso** (a lista recarrega, e a mensagem movida não fica mais em exibição em lugar nenhum).
- **Seleção múltipla de mensagens fica fora de escopo** — a lista de mensagens já é seleção única hoje; esta fatia não muda isso.

## 3. `StoreFolder.HasBackingFile` (`LEC.MailStore`)

```csharp
public sealed class StoreFolder
{
    // ... campos existentes (Id, ParentId, Name, SpecialFolderKind, MessageCount, IsLoadable) ...

    /// <summary>
    /// True se esta pasta tem um arquivo .dbx de mensagens associado (mesmo que ele não
    /// exista/esteja danificado — isso é o que IsLoadable/Health já cobrem). Pastas
    /// puramente organizacionais (ex. "Pastas locais") não têm arquivo nenhum e nunca
    /// podem receber AddMessageAsync.
    /// </summary>
    public required bool HasBackingFile { get; init; }
}
```

Calculado em `DbxStore.Open`, mesmo lugar onde `IsLoadable` já é calculado hoje, a partir do mesmo `fileName is not null` que `AddMessageAsync` já checa (`_backingFileNames[folderId]`). Fato estático, conhecido desde a abertura do perfil — não muda como `Health`/status de scavenge mudam, por isso é um campo simples em vez de um método em `IMailStore` (mesmo raciocínio já aplicado a `IsLoadable`/`MessageCount`/`SpecialFolderKind`).

Todo ponto que constrói `StoreFolder` (produção em `DbxStore.Open`, e cada fixture de teste que hoje monta um `StoreFolder` manualmente) precisa passar esse campo novo.

## 4. `MoveMessageDialog` (`LEC.App`, novo)

Novo `Window` modal, mesmo padrão de `NewFolderDialog` (`Owner = this`, `WindowStartupLocation="CenterOwner"`).

**Entrada:** a coleção `OpenProfiles` (de `MainWindowViewModel`), o `ProfileViewModel` de origem, e o `FolderId` de origem da mensagem sendo movida.

**Conteúdo:** uma `TreeView` por perfil aberto, reaproveitando o mesmo `HierarchicalDataTemplate` já usado em `MainWindow.xaml` (mesmas cores/tooltip de `IsLoadable`). Cada nó fica desabilitado quando:

```csharp
bool isValidDestination = folder.HasBackingFile && folder.IsLoadable
    && !(ReferenceEquals(profile, sourceProfile) && folder.Id == sourceFolderId);
```

Esse cálculo vive num método pequeno e puro (não espalhado inline no XAML/code-behind), pra poder ser testado sem instanciar a `Window`.

**Saída:** `DialogResult = true` mais `SelectedProfile`/`SelectedFolderId`, só habilitado depois que o usuário clica num nó válido. `DialogResult = false`/fechar sem escolher = cancelar, nada acontece.

## 5. Menu de contexto na lista de mensagens (`MainWindow.xaml`/`.xaml.cs`)

`MessageListView` (hoje sem `ContextMenu`) ganha um `ContextMenu` por item, no `ItemContainerStyle` que já existe (mesmo ponto onde o `Foreground` de `RecoveredKind` já é definido) — assim o `DataContext` do menu é o `MessageHeader` daquele item específico, mesmo padrão do menu "Nova subpasta aqui..." por nó da árvore. Um item "Mover para...".

Handler (`MoveMessage_Click`, novo em `MainWindow.xaml.cs`):

1. Resolve o `MessageHeader` (via `DataContext` do `MenuItem`) e `_selectedProfile` (perfil de origem, já rastreado hoje pela seleção de pasta ativa).
2. Abre `MoveMessageDialog` com `_viewModel.OpenProfiles`, `_selectedProfile`, `header.Id.FolderId`.
3. Se `DialogResult != true`, retorna sem fazer nada.
4. `try`: `await _viewModel.MoveMessageAsync(_selectedProfile, header, dialog.SelectedProfile, dialog.SelectedFolderId)`.
5. Sucesso: recarrega a lista da pasta selecionada atualmente (`await _viewModel.SelectFolderAsync(_selectedProfile, _selectedNode)`, mesmo padrão já usado em `ToggleRecoverableButton_Click`) e limpa o painel de leitura (`NavigateToRenderedMessage(string.Empty)` + colapsar `ImagesBlockedBar`, mesmo reset visual de `CloseProfile_Click`).
6. Falha: `catch (Exception ex)` → `MessageBox.Show(this, ex.Message, "LEC — Não foi possível mover a mensagem", MessageBoxButton.OK, MessageBoxImage.Warning)`, mesmo padrão de `CreateFolderAsync`. Nada precisa ser desfeito na UI porque `MoveMessageAsync` já garante que nada mudou em lugar nenhum se falhar.

Nenhuma mudança em `MainWindowViewModel.MoveMessageAsync` — esta fatia só chama o que já existe.

## 6. Erros e casos de borda

- Pastas sem arquivo de apoio ou a pasta de origem: acinzentadas no diálogo, nunca chegam a `MoveMessageAsync`.
- Pasta de destino danificada (`Health != Ok`) ou já com 255 mensagens: não são pré-validadas no diálogo (custaria abrir cada `.dbx` só pra desenhar a tela) — aparecem como `MessageBox` de erro se acontecerem, igual a qualquer outra falha.
- Cancelar o diálogo: nenhuma chamada ao motor acontece, nenhum estado muda.

## 7. Estratégia de testes

`MainWindow.xaml.cs` hoje não tem nenhum teste dedicado — nem os handlers de criar pasta — só as ViewModels são testadas diretamente, e a integração da tela é verificada manualmente. Esta fatia segue o mesmo padrão:

- `StoreFolder.HasBackingFile`: teste em `LEC.MailStore.Tests` confirmando que uma pasta sem `FileName` tem `HasBackingFile = false` e uma pasta com `FileName` existente tem `HasBackingFile = true`.
- O método de validade de destino (item 4 acima): testado isoladamente, sem precisar instanciar `MoveMessageDialog`.
- Verificação manual do diálogo e do menu de contexto na tela — mesmo processo já usado pra validar a UI de criação de pastas.

## 8. Fora de escopo / decisões adiadas

- Drag-and-drop como gatilho alternativo.
- Seleção múltipla de mensagens pra mover em lote.
- Diálogo de confirmação extra além do próprio diálogo de destino.
- Pré-validar pasta de destino danificada/cheia antes de tentar mover.
- Desfazer/restaurar uma mensagem movida (já fora de escopo desde o motor).
