# Ordenação da lista de mensagens (por remetente e por data) — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `MessageListViewModel` (existe hoje, `src/LEC.App/ViewModels/MessageListViewModel.cs`), `MainWindow.xaml` (grade `MessageListView`/`GridView`) — nenhum contrato novo em `IMailStore`/`DbxStore`.
**Contexto:** feature nova pedida diretamente pelo usuário — hoje a lista de mensagens de uma pasta vem sempre ordenada por data, mais recente primeiro, sem nenhuma forma de mudar isso pela UI. O usuário quer poder clicar no cabeçalho da coluna "De" pra ordenar alfabeticamente, e no cabeçalho da coluna "Data" pra ordenar da mensagem mais antiga pra mais nova.

## 1. Objetivo

Tornar as colunas "De" e "Data" da lista de mensagens clicáveis: clicar em "De" ordena a lista pelo remetente (`SenderName`), clicar em "Data" ordena pela data (`Date`). Um segundo clique na mesma coluna inverte a direção. As demais colunas ("!", "Origem", "Assunto") continuam não-clicáveis — fora do escopo pedido.

## 2. Decisões de escopo (confirmadas com o usuário)

- **Clicar de novo na mesma coluna inverte a direção** (como Explorer/qualquer grade do Windows), em vez de cada coluna ter uma ordem fixa e imutável.
- **Cabeçalho mostra uma seta (▲/▼) indicando coluna e direção ativas.**
- **A ordenação escolhida persiste ao trocar de pasta** — se o usuário ordenou por "De", abrir outra pasta já carrega ordenado por "De" também, em vez de sempre resetar pro padrão.
- **Padrão inicial (antes de qualquer clique) continua exatamente como hoje**: mais recente primeiro (`Date`, descendente) — não muda o comportamento/teste existente (`SortsMessagesNewestFirstByDate`).
- **Trocar para uma coluna diferente da ativa sempre começa em ordem "ascendente"** — "De" começa em A-Z, "Data" começa em mais antiga primeiro — independente de qual era a direção anterior naquela coluna (o view-model não guarda histórico por coluna, só o par coluna+direção atual). Isso cobre o caso citado pelo usuário: como "Data" já é a coluna ativa por padrão (descendente), o primeiro clique em "Data" simplesmente inverte pra ascendente (mais antiga primeiro) — exatamente o pedido.
- **Apenas "De" e "Data" ficam clicáveis.** "!", "Origem" e "Assunto" não fazem parte deste pedido.
- **Sem persistência em disco.** A ordenação escolhida vive só em memória, no `MessageListViewModel`, durante a sessão — fecha o app, volta ao padrão. Não há pedido do usuário para lembrar entre sessões.

## 3. `MessageListViewModel` — novo estado e API

```csharp
public enum MessageSortColumn { Date, Sender }
```

Novo estado privado, inicializado pro padrão atual:

```csharp
private MessageSortColumn _sortColumn = MessageSortColumn.Date;
private ListSortDirection _sortDirection = ListSortDirection.Descending;
```

Novas propriedades observáveis só para o texto do cabeçalho (é o que a XAML liga; a seta é montada aqui, não na view):

```csharp
[ObservableProperty] private string _senderColumnHeader = "De";
[ObservableProperty] private string _dateColumnHeader = "Data";
```

Novos métodos públicos, chamados pelo code-behind ao clicar num cabeçalho:

```csharp
public void SortBySender() => ApplySortColumn(MessageSortColumn.Sender);
public void SortByDate() => ApplySortColumn(MessageSortColumn.Date);
```

`ApplySortColumn`: se a coluna pedida já é a ativa, inverte `_sortDirection`; senão, troca `_sortColumn` e força `_sortDirection = Ascending`. Depois, reordena `Messages` in-place (clear + re-add na nova ordem, mesmo padrão já usado em `LoadAsync`) e atualiza `SenderColumnHeader`/`DateColumnHeader` com a seta certa.

`LoadAsync` deixa de ordenar com `OrderByDescending(Date)` fixo e passa a usar a mesma ordenação central (coluna + direção atuais), pra persistir a escolha entre trocas de pasta.

Comparadores:
- `Sender`: `StringComparer.CurrentCultureIgnoreCase` sobre `SenderName ?? string.Empty` (mesmo espírito do `?? DateTime.MinValue` já usado pra `Date` — remetente vazio entra no fim/começo da lista dependendo da direção, sem tratamento especial).
- `Date`: `Date ?? DateTime.MinValue`, como já é hoje.

## 4. `MainWindow.xaml` / code-behind

- As colunas "De" e "Data" ganham `x:Name` (`SenderColumn`, `DateColumn`) pra poderem ser comparadas por referência no handler de clique.
- `Header` das duas colunas passa a ser bindado ao texto exposto pelo view-model (`{Binding DataContext.MessageList.SenderColumnHeader, ElementName=MessageListView}` e o equivalente pra `DateColumnHeader}`) — `GridViewColumn` não herda `DataContext` da árvore visual, por isso o `ElementName` explícito.
- O `ListView` ganha o evento anexado `GridViewColumnHeader.Click` (borbulha do cabeçalho gerado automaticamente pelo `GridView` até o `ListView`, sem precisar de `Style`/`EventSetter`), tratado por um novo handler no code-behind:

```csharp
private void MessageListView_ColumnHeaderClick(object sender, RoutedEventArgs e)
{
    if (e.OriginalSource is not GridViewColumnHeader { Column: not null } header) return;
    if (header.Column == SenderColumn) _viewModel.MessageList.SortBySender();
    else if (header.Column == DateColumn) _viewModel.MessageList.SortByDate();
}
```

Cliques nas demais colunas (`Column` não bate com nenhuma das duas nomeadas) não fazem nada.

## 5. Testes

Em `MessageListViewModelTests.cs`, cobrindo o view-model (a parte testável sem UI):
- `SortBySender()` ordena por `SenderName` alfabeticamente (A-Z), ignorando maiúsculas/minúsculas.
- Chamar `SortBySender()` duas vezes seguidas inverte pra Z-A.
- Chamar `SortByDate()` uma vez a partir do estado padrão (recém-carregado) inverte pra mais antiga primeiro — cobre o caso citado pelo usuário.
- Trocar de coluna (`SortBySender()` depois `SortByDate()`) sempre entra na nova coluna em ordem ascendente, não herda a direção da chamada anterior.
- A ordenação escolhida sobrevive a um novo `LoadAsync` de outra pasta (persistência entre pastas).
- `SenderColumnHeader`/`DateColumnHeader` refletem a seta certa depois de cada chamada.

Sem teste de UI/XAML automatizado (não há framework de teste de interação de grade no projeto hoje) — checklist manual cobre o clique real nos cabeçalhos.

## 6. Checklist manual (GUI)

- Abrir uma pasta com várias mensagens de remetentes e datas variados.
- Clicar em "De": lista ordena A-Z, seta ▲ aparece em "De".
- Clicar em "De" de novo: lista ordena Z-A, seta vira ▼.
- Clicar em "Data": lista ordena mais antiga primeiro, seta ▲ aparece em "Data" (seta de "De" some).
- Clicar em "Data" de novo: mais recente primeiro, seta ▼.
- Trocar de pasta com "De" ativo: pasta nova já abre ordenada por "De".
- Pasta com mensagem sem remetente/data: aparece sem quebrar a ordenação (fica numa ponta da lista, sem crash).
