# Ordenação da lista de mensagens (De/Data) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deixar o usuário clicar no cabeçalho "De" pra ordenar a lista de mensagens alfabeticamente por remetente, e no cabeçalho "Data" pra ordenar por data — com um segundo clique invertendo a direção, uma seta no cabeçalho indicando coluna/direção ativas, e a escolha persistindo ao trocar de pasta.

**Architecture:** Todo o estado de ordenação (coluna ativa + direção) e a lógica de reordenar vivem em `MessageListViewModel`, testável sem UI. `MainWindow.xaml`/`MainWindow.xaml.cs` só encaminham o clique no cabeçalho da grade pro view-model e exibem o texto de cabeçalho que ele expõe — nenhuma lógica de ordenação no code-behind.

**Tech Stack:** C#/.NET, WPF (`ListView`/`GridView`), CommunityToolkit.Mvvm (`ObservableObject`, `[ObservableProperty]`), xUnit.

## Global Constraints

- Apenas as colunas "De" e "Data" ficam clicáveis — "!", "Origem" e "Assunto" continuam como estão, sem clique.
- Clicar na coluna já ativa inverte a direção; clicar numa coluna diferente sempre entra em ordem ascendente (De=A-Z, Data=mais antiga primeiro), independente da direção anterior daquela coluna.
- Padrão inicial (antes de qualquer clique) permanece `Date` descendente (mais recente primeiro) — não pode quebrar o teste existente `SortsMessagesNewestFirstByDate`.
- A ordenação escolhida persiste entre trocas de pasta (não é resetada em `LoadAsync`), mas só em memória — não é salva em disco entre sessões.
- Comparador de remetente: `StringComparer.CurrentCultureIgnoreCase` sobre `SenderName ?? string.Empty`. Comparador de data: `Date ?? DateTime.MinValue` (mesmo padrão já usado hoje).
- Referência: `docs/superpowers/specs/2026-08-05-ordenacao-lista-mensagens-design.md`.

---

## Task 1: Estado e lógica de ordenação em `MessageListViewModel`

**Files:**
- Modify: `src/LEC.App/ViewModels/MessageListViewModel.cs`
- Test: `tests/LEC.App.Tests/MessageListViewModelTests.cs`

**Interfaces:**
- Consumes: nada de tarefas anteriores (é a primeira tarefa).
- Produces (usado pela Task 2):
  - `public enum MessageSortColumn { Date, Sender }` (namespace `LEC.App.ViewModels`)
  - `MessageListViewModel.SortBySender()` — void, sem parâmetros
  - `MessageListViewModel.SortByDate()` — void, sem parâmetros
  - `MessageListViewModel.SenderColumnHeader` — `string`, observável (texto pronto pra exibir, já com seta quando ativo: `"De"`, `"De ▲"` ou `"De ▼"`)
  - `MessageListViewModel.DateColumnHeader` — `string`, observável, mesmo padrão (`"Data"`, `"Data ▲"`, `"Data ▼"`)

- [ ] **Step 1: Escrever o teste de estado inicial e ordenação por remetente**

Em `tests/LEC.App.Tests/MessageListViewModelTests.cs`, adicionar (após o teste `SortsMessagesNewestFirstByDate` existente):

```csharp
    [Fact]
    public async Task SortBySenderOrdersAlphabeticallyAscendingOnFirstClick()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var bravo = new MessageHeader { Id = new MessageRef(1, 1), Subject = "B", SenderName = "Bravo" };
        var alfa = new MessageHeader { Id = new MessageRef(1, 2), Subject = "A", SenderName = "alfa" };
        var charlie = new MessageHeader { Id = new MessageRef(1, 3), Subject = "C", SenderName = "Charlie" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { bravo, alfa, charlie } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SortBySender();

        Assert.Equal(new[] { "A", "B", "C" }, vm.Messages.Select(m => m.Subject));
        Assert.Equal("De ▲", vm.SenderColumnHeader);
        Assert.Equal("Data", vm.DateColumnHeader);
    }

    [Fact]
    public async Task SortBySenderTwiceReversesToDescending()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var alfa = new MessageHeader { Id = new MessageRef(1, 1), Subject = "A", SenderName = "Alfa" };
        var bravo = new MessageHeader { Id = new MessageRef(1, 2), Subject = "B", SenderName = "Bravo" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { alfa, bravo } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SortBySender();
        vm.SortBySender();

        Assert.Equal(new[] { "B", "A" }, vm.Messages.Select(m => m.Subject));
        Assert.Equal("De ▼", vm.SenderColumnHeader);
    }

    [Fact]
    public async Task SortByDateOnceFromDefaultReversesToOldestFirst()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var older = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Antigo", Date = new DateTime(2001, 1, 1) };
        var newer = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Novo", Date = new DateTime(2005, 1, 1) };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { older, newer } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder); // default: Date descendente (mais recente primeiro)

        vm.SortByDate(); // mesma coluna já ativa -> inverte

        Assert.Equal(new[] { "Antigo", "Novo" }, vm.Messages.Select(m => m.Subject));
        Assert.Equal("Data ▲", vm.DateColumnHeader);
        Assert.Equal("De", vm.SenderColumnHeader);
    }

    [Fact]
    public async Task SwitchingColumnAlwaysStartsAscendingRegardlessOfPriorDirection()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var older = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Antigo", Date = new DateTime(2001, 1, 1), SenderName = "Zeta" };
        var newer = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Novo", Date = new DateTime(2005, 1, 1), SenderName = "Alfa" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { older, newer } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SortByDate();   // Date -> ascendente (mais antiga primeiro)
        vm.SortByDate();   // Date -> descendente (mais recente primeiro)
        vm.SortBySender(); // troca de coluna -> sempre ascendente, mesmo vindo de Date descendente

        Assert.Equal(new[] { "Novo", "Antigo" }, vm.Messages.Select(m => m.Subject)); // Alfa, Zeta
        Assert.Equal("De ▲", vm.SenderColumnHeader);
        Assert.Equal("Data", vm.DateColumnHeader);
    }

    [Fact]
    public async Task SortChoicePersistsAcrossFolderReload()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(
            folders: new[] { folderA, folderB },
            headersByFolder: new()
            {
                [1] = new() { new MessageHeader { Id = new MessageRef(1, 1), Subject = "A1", SenderName = "Bravo" } },
                [2] = new()
                {
                    new MessageHeader { Id = new MessageRef(2, 1), Subject = "B-Bravo", SenderName = "Bravo" },
                    new MessageHeader { Id = new MessageRef(2, 2), Subject = "B-Alfa", SenderName = "Alfa" },
                },
            });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folderA);
        vm.SortBySender();

        await vm.LoadAsync(folderB);

        Assert.Equal(new[] { "B-Alfa", "B-Bravo" }, vm.Messages.Select(m => m.Subject));
        Assert.Equal("De ▲", vm.SenderColumnHeader);
    }
```

- [ ] **Step 2: Rodar os testes novos e confirmar que falham por falta de API**

Run: `dotnet test tests/LEC.App.Tests --filter "FullyQualifiedName~MessageListViewModelTests"`
Expected: erros de compilação — `SortBySender`, `SortByDate`, `SenderColumnHeader`, `DateColumnHeader` não existem em `MessageListViewModel`.

- [ ] **Step 3: Implementar o estado e a lógica de ordenação**

Substituir o conteúdo de `src/LEC.App/ViewModels/MessageListViewModel.cs` por:

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public enum MessageSortColumn { Date, Sender }

public partial class MessageListViewModel : ObservableObject
{
    private readonly IMailStore _store;

    private MessageSortColumn _sortColumn = MessageSortColumn.Date;
    private ListSortDirection _sortDirection = ListSortDirection.Descending;

    public ObservableCollection<MessageHeader> Messages { get; } = new();

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string _senderColumnHeader = "De";

    [ObservableProperty]
    private string _dateColumnHeader = "Data";

    public MessageListViewModel(IMailStore store) => _store = store;

    public async Task LoadAsync(StoreFolder folder, CancellationToken ct = default)
    {
        Messages.Clear();
        StatusText = folder.IsLoadable ? null : "Não foi possível carregar esta pasta.";

        var loaded = new List<MessageHeader>();
        await foreach (var header in _store.EnumerateHeadersAsync(folder, ct))
            loaded.Add(header);

        foreach (var header in Order(loaded))
            Messages.Add(header);
    }

    public void SortBySender() => ApplySortColumn(MessageSortColumn.Sender);

    public void SortByDate() => ApplySortColumn(MessageSortColumn.Date);

    private void ApplySortColumn(MessageSortColumn column)
    {
        _sortDirection = _sortColumn == column
            ? (_sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending)
            : ListSortDirection.Ascending;
        _sortColumn = column;

        var reordered = Order(Messages).ToList();
        Messages.Clear();
        foreach (var header in reordered)
            Messages.Add(header);

        UpdateColumnHeaders();
    }

    private IEnumerable<MessageHeader> Order(IEnumerable<MessageHeader> headers) => _sortColumn switch
    {
        MessageSortColumn.Sender => _sortDirection == ListSortDirection.Ascending
            ? headers.OrderBy(h => h.SenderName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            : headers.OrderByDescending(h => h.SenderName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase),
        _ => _sortDirection == ListSortDirection.Ascending
            ? headers.OrderBy(h => h.Date ?? DateTime.MinValue)
            : headers.OrderByDescending(h => h.Date ?? DateTime.MinValue),
    };

    private void UpdateColumnHeaders()
    {
        SenderColumnHeader = "De" + Arrow(MessageSortColumn.Sender);
        DateColumnHeader = "Data" + Arrow(MessageSortColumn.Date);
    }

    private string Arrow(MessageSortColumn column) =>
        _sortColumn != column ? string.Empty : _sortDirection == ListSortDirection.Ascending ? " ▲" : " ▼";
}
```

Nota: o teste `SortsMessagesNewestFirstByDate` já existente continua passando sem alteração — `Order` com o estado padrão (`Date`, `Descending`) produz a mesma ordem que o `OrderByDescending` fixo de antes.

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/LEC.App.Tests --filter "FullyQualifiedName~MessageListViewModelTests"`
Expected: PASS em todos, incluindo o `SortsMessagesNewestFirstByDate` pré-existente.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/MessageListViewModel.cs tests/LEC.App.Tests/MessageListViewModelTests.cs
git commit -m "Add sender/date column sorting to MessageListViewModel"
```

---

## Task 2: Cabeçalhos clicáveis em `MainWindow.xaml`

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml:158,160` (colunas "De" e "Data" dentro do `GridView` de `MessageListView`, linhas 106-163)
- Modify: `src/LEC.App/MainWindow.xaml.cs` (novo handler, perto de `MessageListView_SelectionChanged`, linha 567)

**Interfaces:**
- Consumes (da Task 1): `MessageListViewModel.SortBySender()`, `MessageListViewModel.SortByDate()`, `MessageListViewModel.SenderColumnHeader` (string), `MessageListViewModel.DateColumnHeader` (string) — acessados via `_viewModel.MessageList` no code-behind (`MainWindowViewModel.MessageList` já existe e é público, usado hoje em `LoadAsync`/binding `{Binding MessageList.Messages}`).
- Produces: nada consumido por tarefas futuras — esta é a última tarefa do plano.

- [ ] **Step 1: Dar nome às colunas e ligar o cabeçalho ao texto do view-model**

Em `src/LEC.App/MainWindow.xaml`, trocar as linhas 158 e 160 (dentro do `<GridView>` de `MessageListView`):

De:
```xml
                                <GridViewColumn Header="De" DisplayMemberBinding="{Binding SenderName}" Width="200"/>
                                <GridViewColumn Header="Assunto" DisplayMemberBinding="{Binding Subject}" Width="350"/>
                                <GridViewColumn Header="Data" DisplayMemberBinding="{Binding Date}" Width="140"/>
```

Para:
```xml
                                <GridViewColumn x:Name="SenderColumn" DisplayMemberBinding="{Binding SenderName}" Width="200">
                                    <GridViewColumn.Header>
                                        <TextBlock Text="{Binding DataContext.MessageList.SenderColumnHeader, ElementName=MessageListView}"/>
                                    </GridViewColumn.Header>
                                </GridViewColumn>
                                <GridViewColumn Header="Assunto" DisplayMemberBinding="{Binding Subject}" Width="350"/>
                                <GridViewColumn x:Name="DateColumn" DisplayMemberBinding="{Binding Date}" Width="140">
                                    <GridViewColumn.Header>
                                        <TextBlock Text="{Binding DataContext.MessageList.DateColumnHeader, ElementName=MessageListView}"/>
                                    </GridViewColumn.Header>
                                </GridViewColumn>
```

(`GridViewColumn` não herda `DataContext` da árvore visual porque não faz parte dela, por isso o binding usa `ElementName=MessageListView` pra alcançar o `DataContext` do `ListView`, que é a `MainWindowViewModel`.)

Na tag de abertura do `<ListView x:Name="MessageListView" ...>` (linha 106-112), adicionar o evento anexado de clique do cabeçalho:

```xml
                    <ListView x:Name="MessageListView"
                              ItemsSource="{Binding MessageList.Messages}"
                              SelectionMode="Extended"
                              SelectionChanged="MessageListView_SelectionChanged"
                              KeyDown="MessageListView_KeyDown"
                              GridViewColumnHeader.Click="MessageListView_ColumnHeaderClick"
                              VirtualizingPanel.IsVirtualizing="True"
                              VirtualizingPanel.VirtualizationMode="Recycling">
```

- [ ] **Step 2: Implementar o handler no code-behind**

Em `src/LEC.App/MainWindow.xaml.cs`, adicionar logo após `MessageListView_SelectionChanged` (depois da linha 594, antes do comentário `// Resolves which messages...` na linha 596):

```csharp
    private void MessageListView_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: not null } header) return;

        if (header.Column == SenderColumn)
            _viewModel.MessageList.SortBySender();
        else if (header.Column == DateColumn)
            _viewModel.MessageList.SortByDate();
    }
```

(`GridViewColumnHeader` já está disponível via `using System.Windows.Controls;`, existente no topo do arquivo — nenhum `using` novo necessário.)

- [ ] **Step 3: Compilar**

Run: `dotnet build src/LEC.App`
Expected: build sem erros (confirma que `SenderColumn`/`DateColumn` foram gerados como campos pelo XAML compiler, e que os bindings de `Header` resolvem em tempo de compilação).

- [ ] **Step 4: Rodar a suíte completa de testes**

Run: `dotnet test`
Expected: todos os testes passam, incluindo os novos da Task 1 (esta tarefa não adiciona testes automatizados — a interação de clique na grade não tem framework de teste no projeto hoje).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs
git commit -m "Wire clickable De/Data column headers to message list sorting"
```

- [ ] **Step 6: Checklist manual (GUI) — rodar o app e verificar**

Run: `dotnet run --project src/LEC.App`

1. Abrir um perfil e uma pasta com várias mensagens de remetentes e datas variados.
2. Clicar em "De": lista ordena A-Z, cabeçalho vira "De ▲".
3. Clicar em "De" de novo: lista ordena Z-A, cabeçalho vira "De ▼".
4. Clicar em "Data": lista ordena mais antiga primeiro, cabeçalho vira "Data ▲" e "De" volta a "De" (sem seta).
5. Clicar em "Data" de novo: mais recente primeiro, cabeçalho vira "Data ▼".
6. Com "De" ativo, trocar de pasta: a pasta nova já abre ordenada por "De".
7. Clicar em "Assunto"/"Origem"/"!": nada acontece (não são clicáveis).
8. Pasta com mensagem sem remetente ou sem data: aparece numa ponta da lista, sem exceção nem crash.

---

## Self-Review

**Cobertura da spec:** cabeçalhos clicáveis (Task 2, Step 1-2) ✓; toggle de direção ao clicar na mesma coluna (Task 1, `ApplySortColumn`) ✓; troca de coluna sempre ascendente (Task 1, `ApplySortColumn` + teste `SwitchingColumnAlwaysStartsAscendingRegardlessOfPriorDirection`) ✓; seta no cabeçalho (Task 1, `UpdateColumnHeaders`/`Arrow` + Task 2 binding) ✓; persistência entre pastas (Task 1, `LoadAsync` usa `Order` central + teste `SortChoicePersistsAcrossFolderReload`) ✓; padrão inicial inalterado (Task 1, estado inicial `Date`/`Descending` + teste pré-existente mantido) ✓; apenas De/Data clicáveis (Task 2, handler só reage a `SenderColumn`/`DateColumn`) ✓; checklist manual (Task 2, Step 6) ✓.

**Placeholders:** nenhum "TBD"/"similar to"/passo sem código — checado.

**Consistência de tipos:** `MessageSortColumn`, `SortBySender()`, `SortByDate()`, `SenderColumnHeader`, `DateColumnHeader` usados de forma idêntica entre Task 1 (definição) e Task 2 (consumo via `_viewModel.MessageList.*` e bindings XAML).
