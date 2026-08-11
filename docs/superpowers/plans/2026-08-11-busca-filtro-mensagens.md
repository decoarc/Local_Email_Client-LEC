# Busca/filtro de mensagens (Remetente/Assunto/Conteúdo) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Adicionar, acima da lista de mensagens, um campo de busca com 3 toggles mutuamente exclusivos (Remetente/Assunto/Conteúdo) que filtram dinamicamente a lista exibida, case-insensitive, sem alterar dado nenhum.

**Architecture:** Todo o estado de busca (texto, toggle ativo, corpo pré-carregado das mensagens) e a lógica de filtragem vivem em `MessageListViewModel`, testável sem UI — `Messages` (a coleção ligada à `ListView`) passa a ser uma projeção filtrada de uma lista interna `_headers`, em vez de ser ela mesma o armazenamento. A busca por "Conteúdo" precisa do corpo de cada mensagem (que `MessageHeader` não carrega), então `MessageListViewModel` pré-carrega o corpo de todas as mensagens da pasta em background ao carregá-la, via um novo método `MessageRenderer.ExtractSearchableTextAsync`. `MainWindow.xaml`/`.xaml.cs` só expõem os controles (campo de texto + 3 `RadioButton`) e encaminham pro view-model — nenhuma lógica de filtragem no code-behind.

**Tech Stack:** C#/.NET, WPF (`TextBox`, `RadioButton`, `ListView`/`GridView`), MimeKit, CommunityToolkit.Mvvm (`ObservableObject`, `[ObservableProperty]`), xUnit.

## Global Constraints

- Apenas um toggle selecionado por vez — usar `RadioButton` com `GroupName` compartilhado (exclusividade nativa do WPF), não lógica manual.
- Busca é substring, case-insensitive (`StringComparison.OrdinalIgnoreCase`); texto vazio = sem filtro (lista completa).
- Trocar o toggle reaplica o filtro imediatamente com o texto já digitado.
- "Remetente" compara com `SenderName` (o texto exibido na coluna "De"), não `SenderAddress`.
- Toggle padrão ao abrir/recarregar uma pasta: Assunto (`MessageSearchField.Subject`). Texto e toggle resetam em toda chamada de `LoadAsync` (troca de pasta ou recarregamento da mesma pasta após mover/apagar/scavenge).
- Busca por Conteúdo pré-carrega o corpo de **todas** as mensagens da pasta ao carregá-la, sempre, em background — sem bloquear a exibição imediata dos headers. Mensagem cujo corpo falhar ao carregar/parsear nunca aparece em busca por Conteúdo, mas continua normal em Remetente/Assunto/sem filtro (nunca lança exceção nem trava a UI).
- Sem persistência em disco — todo o estado de busca vive em memória, durante a sessão.
- Referência: `docs/superpowers/specs/2026-08-11-busca-filtro-mensagens-design.md`.

---

## Task 1: `MessageRenderer.ExtractSearchableTextAsync`

**Files:**
- Modify: `src/LEC.App/Services/MessageRenderer.cs:119-160` (método `RenderAsync` — refatorado em dois helpers privados reaproveitados pelo novo método, sem mudar seu comportamento)
- Test: `tests/LEC.App.Tests/MessageRendererTests.cs`

**Interfaces:**
- Consumes: nada de tarefas anteriores (é a primeira tarefa).
- Produces (usado pela Task 3):
  - `MessageRenderer.ExtractSearchableTextAsync(Stream rawMessage, CancellationToken ct = default)` — `Task<string>`, nunca lança exceção (mensagens ilegíveis retornam os bytes brutos como texto, mesmo fallback usado por `RenderAsync` para exibição).

- [ ] **Step 1: Escrever os testes do novo método**

Em `tests/LEC.App.Tests/MessageRendererTests.cs`, adicionar estes três `[Fact]` logo antes do fechamento da classe (depois do último teste existente, `SaveAttachmentAsyncThrowsForAnOutOfRangeIndex`):

```csharp
    [Fact]
    public async Task ExtractSearchableTextAsyncReturnsThePlainTextBodyWhenPresent()
    {
        string raw =
            "From: ana@example.com\r\n" +
            "Subject: Ola\r\n" +
            "\r\n" +
            "Corpo em texto puro";

        string text = await MessageRenderer.ExtractSearchableTextAsync(RawMessage(raw));

        Assert.Contains("Corpo em texto puro", text);
    }

    [Fact]
    public async Task ExtractSearchableTextAsyncStripsHtmlTagsWhenThereIsNoTextBody()
    {
        string raw =
            "From: ana@example.com\r\n" +
            "Subject: Ola\r\n" +
            "Content-Type: text/html\r\n" +
            "\r\n" +
            "<p>Corpo <b>em HTML</b></p>";

        string text = await MessageRenderer.ExtractSearchableTextAsync(RawMessage(raw));

        Assert.Contains("Corpo", text);
        Assert.Contains("em HTML", text);
        Assert.DoesNotContain("<p>", text);
        Assert.DoesNotContain("<b>", text);
    }

    [Fact]
    public async Task ExtractSearchableTextAsyncFallsBackToRawBytesWhenTheMessageFailsToParse()
    {
        // Mesmo cabeçalho usado por FallsBackToRawTextInsteadOfThrowingWhenHeadersAreNotRfcCompliant
        // acima -- header com espaço no nome do campo, que o parser estrito do MimeKit rejeita.
        string raw =
            "X-Bad Header: present so MimeKit's strict parser rejects this\r\n" +
            "\r\n" +
            "Corpo da mensagem sem parse";

        string text = await MessageRenderer.ExtractSearchableTextAsync(RawMessage(raw));

        Assert.Contains("Corpo da mensagem sem parse", text);
    }
```

- [ ] **Step 2: Rodar os testes novos e confirmar que falham por falta de API**

Run: `dotnet test tests/LEC.App.Tests --filter "FullyQualifiedName~MessageRendererTests"`
Expected: erro de compilação — `MessageRenderer.ExtractSearchableTextAsync` não existe.

- [ ] **Step 3: Extrair helpers compartilhados e implementar `ExtractSearchableTextAsync`**

Em `src/LEC.App/Services/MessageRenderer.cs`, substituir o método `RenderAsync` inteiro (linhas 119-160 — do `public static async Task<RenderedMessage> RenderAsync` até o `}` que fecha o método, incluindo o comentário logo no início do corpo) por:

```csharp
    // Bufferiza os bytes brutos antes de tentar parsear -- assim, RenderAsync e
    // ExtractSearchableTextAsync conseguem cair pro texto bruto como fallback mesmo quando o
    // parse falha, sem precisar reler o stream (que pode não ser buscável/reutilizável).
    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    // Tenta parsear como MIME; retorna null (nunca lança) em vez de propagar a exceção --
    // alguns .dbx reais carregam headers que o parser estrito do MimeKit rejeita (ex.: campo
    // de header injetado por gateway de antivírus legado contendo espaço, como
    // "X-Symantec-Tempo limite da proteção: 0"). Por política de degradação do ARQUITETURA.md,
    // isso deve virar um fallback legível, não uma exceção não tratada.
    private static async Task<MimeMessage?> TryParseAsync(byte[] raw, CancellationToken ct)
    {
        try
        {
            using var parseStream = new MemoryStream(raw);
            return await MimeMessage.LoadAsync(parseStream, ct);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static async Task<RenderedMessage> RenderAsync(Stream rawMessage, CancellationToken ct = default)
    {
        byte[] raw = await ReadAllBytesAsync(rawMessage, ct);
        MimeMessage? message = await TryParseAsync(raw, ct);

        if (message is null)
        {
            string fallbackHtml = BuildFallbackHtml(raw);
            return new RenderedMessage(null, null, null, fallbackHtml, Array.Empty<AttachmentInfo>());
        }

        string html = message.HtmlBody is { } htmlBody
            ? ResolveCidImages(htmlBody, message)
            : BuildFallbackHtml(message.TextBody ?? string.Empty);

        string? fromDisplay = message.From.Mailboxes.FirstOrDefault() is { } mailbox
            ? (string.IsNullOrEmpty(mailbox.Name) ? mailbox.Address : $"{mailbox.Name} <{mailbox.Address}>")
            : null;

        return new RenderedMessage(message.Subject, fromDisplay, message.Date, html, ExtractAttachments(message));
    }

    // Regex simples pra remover tags ao extrair texto pesquisável de um HtmlBody -- não
    // precisa ser um parser HTML de verdade (como o restante deste arquivo faz pra
    // renderização real), só precisa produzir texto razoável pra comparar substring contra o
    // termo buscado.
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// Extrai um texto pesquisável do corpo da mensagem, pra uso da busca por "Conteúdo" em
    /// MessageListViewModel -- nunca lança: mensagem que falha ao parsear cai pro mesmo
    /// fallback de bytes brutos que RenderAsync usa pra exibição.
    /// </summary>
    public static async Task<string> ExtractSearchableTextAsync(Stream rawMessage, CancellationToken ct = default)
    {
        byte[] raw = await ReadAllBytesAsync(rawMessage, ct);
        MimeMessage? message = await TryParseAsync(raw, ct);

        if (message is null)
            return Encoding.Latin1.GetString(raw);

        if (message.TextBody is { } text)
            return text;

        if (message.HtmlBody is { } html)
            return WebUtility.HtmlDecode(HtmlTagRegex.Replace(html, " "));

        return string.Empty;
    }
```

Nenhum `using` novo é necessário — `Encoding`, `WebUtility` e `Regex` já estão importados no topo do arquivo (`System.Text`, `System.Net`, `System.Text.RegularExpressions`).

- [ ] **Step 4: Rodar todos os testes de `MessageRenderer` e confirmar que passam**

Run: `dotnet test tests/LEC.App.Tests --filter "FullyQualifiedName~MessageRendererTests"`
Expected: PASS em todos, incluindo os testes de `RenderAsync` já existentes (a refatoração não muda o comportamento deles) e os 3 novos de `ExtractSearchableTextAsync`.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/Services/MessageRenderer.cs tests/LEC.App.Tests/MessageRendererTests.cs
git commit -m "Add MessageRenderer.ExtractSearchableTextAsync for message body search"
```

---

## Task 2: Filtro por Remetente/Assunto em `MessageListViewModel`

**Files:**
- Modify: `src/LEC.App/ViewModels/MessageListViewModel.cs` (arquivo inteiro reescrito)
- Test: `tests/LEC.App.Tests/MessageListViewModelTests.cs`

**Interfaces:**
- Consumes: nada de tarefas anteriores.
- Produces (usado pela Task 3 e Task 5):
  - `public enum MessageSearchField { Sender, Subject, Content }` (namespace `LEC.App.ViewModels`)
  - `MessageListViewModel.SearchText` — `string`, propriedade observável, default `string.Empty`
  - `MessageListViewModel.SearchField` — `MessageSearchField`, propriedade observável, default `MessageSearchField.Subject`
  - `MessageListViewModel.Messages` — `ObservableCollection<MessageHeader>` (já existe; comportamento muda: agora é a projeção filtrada, não mais o armazenamento bruto)

- [ ] **Step 1: Escrever os testes de filtro por Remetente/Assunto**

Em `tests/LEC.App.Tests/MessageListViewModelTests.cs`, adicionar estes `[Fact]` no final da classe, antes do `}` de fechamento:

```csharp
    [Fact]
    public async Task FiltersBySenderNameCaseInsensitively()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var joao = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Assunto A", SenderName = "Joao Silva" };
        var maria = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Assunto B", SenderName = "Maria Souza" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { joao, maria } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SearchField = MessageSearchField.Sender;
        vm.SearchText = "JOAO";

        Assert.Equal(new[] { "Assunto A" }, vm.Messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task FiltersBySubjectCaseInsensitively()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var relatorio = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Relatorio mensal" };
        var outro = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Reuniao" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { relatorio, outro } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder); // SearchField já começa em Subject por padrão

        vm.SearchText = "RELATORIO";

        Assert.Equal(new[] { "Relatorio mensal" }, vm.Messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task EmptySearchTextShowsAllMessages()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var a = new MessageHeader { Id = new MessageRef(1, 1), Subject = "A", SenderName = "Alfa" };
        var b = new MessageHeader { Id = new MessageRef(1, 2), Subject = "B", SenderName = "Bravo" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { a, b } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SearchField = MessageSearchField.Sender;
        vm.SearchText = "Alfa";
        Assert.Single(vm.Messages);

        vm.SearchText = string.Empty;
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public async Task SwitchingSearchFieldReappliesImmediatelyWithExistingText()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        // "Ana" aparece no remetente da primeira mensagem e no assunto da segunda -- prova que
        // é o toggle, não o texto, que decide o que é comparado.
        var bySender = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Assunto qualquer", SenderName = "Ana Paula" };
        var bySubject = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Fotos da Ana", SenderName = "Carlos" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { bySender, bySubject } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);
        vm.SearchText = "Ana";

        vm.SearchField = MessageSearchField.Sender;
        Assert.Equal(new[] { "Assunto qualquer" }, vm.Messages.Select(m => m.Subject));

        vm.SearchField = MessageSearchField.Subject;
        Assert.Equal(new[] { "Fotos da Ana" }, vm.Messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task LoadAsyncResetsSearchTextAndFieldToDefaults()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(
            folders: new[] { folderA, folderB },
            headersByFolder: new()
            {
                [1] = new() { new MessageHeader { Id = new MessageRef(1, 1), Subject = "A1", SenderName = "Alfa" } },
                [2] = new() { new MessageHeader { Id = new MessageRef(2, 1), Subject = "B1", SenderName = "Bravo" } },
            });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folderA);
        vm.SearchField = MessageSearchField.Sender;
        vm.SearchText = "Alfa";

        await vm.LoadAsync(folderB);

        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Equal(MessageSearchField.Subject, vm.SearchField);
        Assert.Single(vm.Messages); // sem filtro, mostra a única mensagem da pasta B
    }

    [Fact]
    public async Task FilterComposesWithSort()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var bravo = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Relatorio B", SenderName = "Bravo" };
        var alfa = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Relatorio A", SenderName = "Alfa" };
        var outro = new MessageHeader { Id = new MessageRef(1, 3), Subject = "Outro assunto", SenderName = "Charlie" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { bravo, alfa, outro } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);
        vm.SearchText = "Relatorio"; // SearchField padrão = Subject

        vm.SortBySender();

        Assert.Equal(new[] { "Relatorio A", "Relatorio B" }, vm.Messages.Select(m => m.Subject));
    }
```

- [ ] **Step 2: Rodar os testes novos e confirmar que falham por falta de API**

Run: `dotnet test tests/LEC.App.Tests --filter "FullyQualifiedName~MessageListViewModelTests"`
Expected: erro de compilação — `MessageSearchField`, `SearchText`, `SearchField` não existem em `MessageListViewModel`.

- [ ] **Step 3: Implementar o filtro**

Substituir o conteúdo inteiro de `src/LEC.App/ViewModels/MessageListViewModel.cs` por:

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public enum MessageSortColumn { Date, Sender }

public enum MessageSearchField { Sender, Subject, Content }

public partial class MessageListViewModel : ObservableObject
{
    private readonly IMailStore _store;

    private MessageSortColumn _sortColumn = MessageSortColumn.Date;
    private ListSortDirection _sortDirection = ListSortDirection.Descending;

    // Todos os headers da pasta atual, já na ordem de sort corrente -- Messages (abaixo) é a
    // projeção filtrada disso, não o armazenamento em si.
    private List<MessageHeader> _headers = new();

    public ObservableCollection<MessageHeader> Messages { get; } = new();

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string _senderColumnHeader = "De";

    [ObservableProperty]
    private string _dateColumnHeader = "Data";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private MessageSearchField _searchField = MessageSearchField.Subject;

    public MessageListViewModel(IMailStore store) => _store = store;

    public async Task LoadAsync(StoreFolder folder, CancellationToken ct = default)
    {
        _headers.Clear();
        SearchText = string.Empty;
        SearchField = MessageSearchField.Subject;
        StatusText = folder.IsLoadable ? null : "Não foi possível carregar esta pasta.";

        var loaded = new List<MessageHeader>();
        await foreach (var header in _store.EnumerateHeadersAsync(folder, ct))
            loaded.Add(header);

        _headers = Order(loaded).ToList();
        ApplyFilter();
    }

    public void SortBySender() => ApplySortColumn(MessageSortColumn.Sender);

    public void SortByDate() => ApplySortColumn(MessageSortColumn.Date);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSearchFieldChanged(MessageSearchField value) => ApplyFilter();

    private void ApplySortColumn(MessageSortColumn column)
    {
        _sortDirection = _sortColumn == column
            ? (_sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending)
            : ListSortDirection.Ascending;
        _sortColumn = column;

        _headers = Order(_headers).ToList();
        ApplyFilter();

        UpdateColumnHeaders();
    }

    private void ApplyFilter()
    {
        IEnumerable<MessageHeader> filtered = string.IsNullOrEmpty(SearchText)
            ? _headers
            : _headers.Where(MatchesSearch);

        Messages.Clear();
        foreach (var header in filtered)
            Messages.Add(header);
    }

    private bool MatchesSearch(MessageHeader header) => SearchField switch
    {
        MessageSearchField.Sender => Contains(header.SenderName),
        MessageSearchField.Subject => Contains(header.Subject),
        // MessageSearchField.Content é implementado na Task 3 -- por enquanto nunca casa.
        _ => false,
    };

    private bool Contains(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

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

Nota: `OnSearchTextChanged`/`OnSearchFieldChanged` são métodos parciais gerados pelo `[ObservableProperty]` do CommunityToolkit.Mvvm — chamados automaticamente depois que a propriedade muda de valor.

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/LEC.App.Tests --filter "FullyQualifiedName~MessageListViewModelTests"`
Expected: PASS em todos, incluindo os testes de ordenação já existentes (a ordenação em si não muda de comportamento) e os 6 novos de filtro.

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/MessageListViewModel.cs tests/LEC.App.Tests/MessageListViewModelTests.cs
git commit -m "Add sender/subject search filter to MessageListViewModel"
```

---

## Task 3: Busca por Conteúdo (pré-carregamento em background)

**Files:**
- Modify: `src/LEC.App/ViewModels/MessageListViewModel.cs` (arquivo inteiro reescrito, sobre o resultado da Task 2)
- Test: `tests/LEC.App.Tests/MessageListViewModelTests.cs`

**Interfaces:**
- Consumes (da Task 1): `MessageRenderer.ExtractSearchableTextAsync(Stream, CancellationToken) -> Task<string>`
- Consumes (da Task 2): `_headers`, `ApplyFilter()`, `SearchField`, `SearchText`, `MessageSearchField`, estrutura de `LoadAsync`/`ApplySortColumn` (todos modificados nesta tarefa, não apenas consumidos).
- Produces (usado pela Task 4 e pelos testes desta tarefa):
  - `MessageListViewModel.IsContentLoading` — `bool`, propriedade observável
  - `MessageListViewModel.ContentLoadTask` — `Task` público, default `Task.CompletedTask`; existe só para testes esperarem o pré-carregamento terminar antes de checar resultado (não é consumido pela UI).

- [ ] **Step 1: Escrever os testes de busca por conteúdo**

Em `tests/LEC.App.Tests/MessageListViewModelTests.cs`, adicionar `using System.Text;` no topo do arquivo (junto aos usings existentes) e estes `[Fact]` no final da classe:

```csharp
    [Fact]
    public async Task FiltersByContentAfterPreloadCompletes()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var withMatch = new MessageHeader { Id = new MessageRef(1, 1), Subject = "A" };
        var withoutMatch = new MessageHeader { Id = new MessageRef(1, 2), Subject = "B" };
        byte[] RawWithBody(string body) => Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: X\r\n\r\n" + body);

        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { withMatch, withoutMatch } },
            rawByRef: new()
            {
                [withMatch.Id] = RawWithBody("Este e-mail fala sobre laranjas"),
                [withoutMatch.Id] = RawWithBody("Este e-mail fala sobre maçãs"),
            });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);
        await vm.ContentLoadTask;

        vm.SearchField = MessageSearchField.Content;
        vm.SearchText = "LARANJAS";

        Assert.Equal(new[] { "A" }, vm.Messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task IsContentLoadingBecomesFalseAfterPreloadCompletes()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var header = new MessageHeader { Id = new MessageRef(1, 1), Subject = "A" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { header } },
            rawByRef: new() { [header.Id] = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: X\r\n\r\nCorpo") });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);
        await vm.ContentLoadTask;

        Assert.False(vm.IsContentLoading);
    }

    [Fact]
    public async Task ContentSearchIgnoresMessagesWhoseBodyFailsToLoad()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var unreadable = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Corrompida" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { unreadable } });
        // Nenhum rawByRef registrado para este MessageRef -- OpenMessageAsync do FakeMailStore
        // lança InvalidOperationException, simulando um corpo ilegível/corrompido.

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);
        await vm.ContentLoadTask;

        vm.SearchField = MessageSearchField.Content;
        vm.SearchText = "qualquer coisa";
        Assert.Empty(vm.Messages);

        vm.SearchField = MessageSearchField.Subject;
        vm.SearchText = "Corrompida";
        Assert.Single(vm.Messages);
    }
```

- [ ] **Step 2: Rodar os testes novos e confirmar que falham por falta de API**

Run: `dotnet test tests/LEC.App.Tests --filter "FullyQualifiedName~MessageListViewModelTests"`
Expected: erro de compilação — `ContentLoadTask`, `IsContentLoading` não existem, e `MessageSearchField.Content` nunca casa (o teste `FiltersByContentAfterPreloadCompletes` falha em runtime, não em compilação, já que o enum member já existe desde a Task 2).

- [ ] **Step 3: Implementar o pré-carregamento de conteúdo**

Substituir o conteúdo inteiro de `src/LEC.App/ViewModels/MessageListViewModel.cs` por:

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.App.Services;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public enum MessageSortColumn { Date, Sender }

public enum MessageSearchField { Sender, Subject, Content }

public partial class MessageListViewModel : ObservableObject
{
    // A cada quantas mensagens processadas o pré-carregamento reaplica o filtro (se a busca
    // por Conteúdo estiver ativa) -- evita recalcular a lista inteira a cada mensagem
    // individual em pastas grandes. Sempre há um refresh final ao término do pré-carregamento,
    // então pastas com menos mensagens que isso também funcionam corretamente.
    private const int ContentRefreshBatchSize = 25;

    private readonly IMailStore _store;

    private MessageSortColumn _sortColumn = MessageSortColumn.Date;
    private ListSortDirection _sortDirection = ListSortDirection.Descending;

    // Todos os headers da pasta atual, já na ordem de sort corrente -- Messages (abaixo) é a
    // projeção filtrada disso, não o armazenamento em si.
    private List<MessageHeader> _headers = new();

    // Corpo pesquisável de cada mensagem, populado em background por PreloadContentAsync.
    // Mensagem ausente daqui ainda não terminou de carregar (ou falhou) -- MatchesSearch trata
    // isso como "não casa" pra busca por Conteúdo, nunca como erro.
    private readonly Dictionary<MessageRef, string> _bodyByMessage = new();
    private CancellationTokenSource? _contentLoadCts;

    public ObservableCollection<MessageHeader> Messages { get; } = new();

    /// <summary>Exposto só para testes esperarem o pré-carregamento de conteúdo terminar antes de checar o resultado -- não é consumido pela UI.</summary>
    public Task ContentLoadTask { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string _senderColumnHeader = "De";

    [ObservableProperty]
    private string _dateColumnHeader = "Data";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private MessageSearchField _searchField = MessageSearchField.Subject;

    [ObservableProperty]
    private bool _isContentLoading;

    public MessageListViewModel(IMailStore store) => _store = store;

    public async Task LoadAsync(StoreFolder folder, CancellationToken ct = default)
    {
        _contentLoadCts?.Cancel();
        var cts = _contentLoadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _headers.Clear();
        _bodyByMessage.Clear();
        SearchText = string.Empty;
        SearchField = MessageSearchField.Subject;
        StatusText = folder.IsLoadable ? null : "Não foi possível carregar esta pasta.";

        var loaded = new List<MessageHeader>();
        await foreach (var header in _store.EnumerateHeadersAsync(folder, ct))
            loaded.Add(header);

        _headers = Order(loaded).ToList();
        ApplyFilter();

        ContentLoadTask = PreloadContentAsync(_headers, cts);
    }

    public void SortBySender() => ApplySortColumn(MessageSortColumn.Sender);

    public void SortByDate() => ApplySortColumn(MessageSortColumn.Date);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSearchFieldChanged(MessageSearchField value) => ApplyFilter();

    private void ApplySortColumn(MessageSortColumn column)
    {
        _sortDirection = _sortColumn == column
            ? (_sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending)
            : ListSortDirection.Ascending;
        _sortColumn = column;

        _headers = Order(_headers).ToList();
        ApplyFilter();

        UpdateColumnHeaders();
    }

    private void ApplyFilter()
    {
        IEnumerable<MessageHeader> filtered = string.IsNullOrEmpty(SearchText)
            ? _headers
            : _headers.Where(MatchesSearch);

        Messages.Clear();
        foreach (var header in filtered)
            Messages.Add(header);
    }

    private bool MatchesSearch(MessageHeader header) => SearchField switch
    {
        MessageSearchField.Sender => Contains(header.SenderName),
        MessageSearchField.Subject => Contains(header.Subject),
        MessageSearchField.Content => _bodyByMessage.TryGetValue(header.Id, out var body) && Contains(body),
        _ => false,
    };

    private bool Contains(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    // Abre e extrai o texto pesquisável de cada mensagem da pasta, uma de cada vez
    // (sequencial -- não paraleliza leituras contra o mesmo arquivo .dbx), em background.
    // Cancelado quando uma nova chamada a LoadAsync assume (ver _contentLoadCts em LoadAsync);
    // o ReferenceEquals abaixo garante que uma execução cancelada nunca mexe em
    // IsContentLoading/Messages depois que uma execução mais nova já assumiu.
    private async Task PreloadContentAsync(List<MessageHeader> headers, CancellationTokenSource ownerCts)
    {
        if (headers.Count == 0) return;

        IsContentLoading = true;
        try
        {
            int sinceRefresh = 0;
            foreach (var header in headers)
            {
                ownerCts.Token.ThrowIfCancellationRequested();
                try
                {
                    var stream = await _store.OpenMessageAsync(header.Id, ownerCts.Token);
                    await using (stream)
                    {
                        _bodyByMessage[header.Id] = await MessageRenderer.ExtractSearchableTextAsync(stream, ownerCts.Token);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Corpo ilegível (MIME corrompido/truncado, ou I/O falhou) -- degrada:
                    // mensagem só não aparece em buscas por Conteúdo, resto do app não é afetado.
                }

                if (++sinceRefresh >= ContentRefreshBatchSize && ReferenceEquals(_contentLoadCts, ownerCts))
                {
                    sinceRefresh = 0;
                    if (SearchField == MessageSearchField.Content)
                        ApplyFilter();
                }
            }
        }
        catch (OperationCanceledException)
        {
            return; // superada por um LoadAsync mais novo -- não mexe em IsContentLoading abaixo
        }

        if (ReferenceEquals(_contentLoadCts, ownerCts))
        {
            IsContentLoading = false;
            if (SearchField == MessageSearchField.Content)
                ApplyFilter(); // garante um refresh final mesmo com menos de ContentRefreshBatchSize mensagens
        }
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

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/LEC.App.Tests --filter "FullyQualifiedName~MessageListViewModelTests"`
Expected: PASS em todos — os 6 testes de sort/filtro das Tasks 1-2 continuam passando, mais os 3 novos de conteúdo.

- [ ] **Step 5: Rodar a suíte completa**

Run: `dotnet test`
Expected: PASS em tudo (nenhuma outra parte do app referencia `MessageListViewModel` de um jeito que essa mudança quebre).

- [ ] **Step 6: Commit**

```bash
git add src/LEC.App/ViewModels/MessageListViewModel.cs tests/LEC.App.Tests/MessageListViewModelTests.cs
git commit -m "Preload message bodies in background and add content search"
```

---

## Task 4: UI — campo de busca e toggles em `MainWindow.xaml`

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml:9-19` (`Window.Resources`)
- Modify: `src/LEC.App/MainWindow.xaml:104-112` (dentro do `DockPanel` que envolve `MessageListView`, entre `RecoverableBar` e a `ListView`)

**Interfaces:**
- Consumes (da Task 2/3): `MessageListViewModel.IsContentLoading` (bool, via binding).
- Produces (usado pela Task 5): elementos nomeados no XAML —
  - `SearchBox` (`TextBox`)
  - `SearchFieldSender`, `SearchFieldSubject`, `SearchFieldContent` (`RadioButton`, cada um com `Tag` igual ao nome do membro de `MessageSearchField` correspondente: `"Sender"`, `"Subject"`, `"Content"`)
  - Eventos `TextChanged` (`SearchBox`) e `Checked` (os 3 `RadioButton`) referenciando handlers que a Task 5 implementa (`SearchBox_TextChanged`, `SearchFieldRadio_Checked`) — o XAML não compila sozinho até a Task 5 adicionar esses handlers no code-behind; é esperado que o build desta tarefa falhe até lá (ver Step 2).

- [ ] **Step 1: Adicionar os recursos (conversor + estilo dos toggles)**

Em `src/LEC.App/MainWindow.xaml`, trocar (linhas 9-19):

De:
```xml
    <Window.Resources>
        <conv:LoadableToBrushConverter x:Key="LoadableToBrushConverter"/>
        <conv:SpecialToFontWeightConverter x:Key="SpecialToFontWeightConverter"/>
        <conv:HealthToVisibilityConverter x:Key="HealthToVisibilityConverter"/>
        <conv:NotLoadableTooltipConverter x:Key="NotLoadableTooltipConverter"/>
        <conv:RecoveredKindToVisibilityConverter x:Key="RecoveredKindToVisibilityConverter"/>
        <conv:RecoveredKindToLabelConverter x:Key="RecoveredKindToLabelConverter"/>
        <conv:RecoveredKindToRowBrushConverter x:Key="RecoveredKindToRowBrushConverter"/>
        <conv:AttachmentListToVisibilityConverter x:Key="AttachmentListToVisibilityConverter"/>
        <conv:SpecialToVisibilityConverter x:Key="SpecialToVisibilityConverter"/>
    </Window.Resources>
```

Para:
```xml
    <Window.Resources>
        <conv:LoadableToBrushConverter x:Key="LoadableToBrushConverter"/>
        <conv:SpecialToFontWeightConverter x:Key="SpecialToFontWeightConverter"/>
        <conv:HealthToVisibilityConverter x:Key="HealthToVisibilityConverter"/>
        <conv:NotLoadableTooltipConverter x:Key="NotLoadableTooltipConverter"/>
        <conv:RecoveredKindToVisibilityConverter x:Key="RecoveredKindToVisibilityConverter"/>
        <conv:RecoveredKindToLabelConverter x:Key="RecoveredKindToLabelConverter"/>
        <conv:RecoveredKindToRowBrushConverter x:Key="RecoveredKindToRowBrushConverter"/>
        <conv:AttachmentListToVisibilityConverter x:Key="AttachmentListToVisibilityConverter"/>
        <conv:SpecialToVisibilityConverter x:Key="SpecialToVisibilityConverter"/>
        <BooleanToVisibilityConverter x:Key="BoolToVisibilityConverter"/>

        <Style x:Key="SearchToggleButtonStyle" TargetType="RadioButton">
            <Setter Property="Margin" Value="4,0,0,0"/>
            <Setter Property="Padding" Value="10,4"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="RadioButton">
                        <Border x:Name="Border" Background="#E8E8E8" BorderBrush="#B0B0B0" BorderThickness="1"
                                CornerRadius="3" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsChecked" Value="True">
                                <Setter TargetName="Border" Property="Background" Value="#3874CB"/>
                                <Setter Property="Foreground" Value="White"/>
                            </Trigger>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Border" Property="BorderBrush" Value="#3874CB"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </Window.Resources>
```

(`BooleanToVisibilityConverter` é o conversor embutido do WPF, em `System.Windows.Controls` — já coberto pelo namespace padrão da raiz do XAML, não precisa do prefixo `conv:`.)

- [ ] **Step 2: Adicionar a linha de busca acima da lista de mensagens**

Em `src/LEC.App/MainWindow.xaml`, trocar (linhas 104-112):

De:
```xml
                    <Border x:Name="RecoverableBar" DockPanel.Dock="Top" Background="#E8F0FE" Padding="6"
                            Visibility="Collapsed">
                        <DockPanel>
                            <Button x:Name="ToggleRecoverableButton" DockPanel.Dock="Right"
                                    Click="ToggleRecoverableButton_Click"/>
                            <TextBlock x:Name="RecoverableCountText" VerticalAlignment="Center"/>
                        </DockPanel>
                    </Border>
                    <ListView x:Name="MessageListView"
```

Para:
```xml
                    <Border x:Name="RecoverableBar" DockPanel.Dock="Top" Background="#E8F0FE" Padding="6"
                            Visibility="Collapsed">
                        <DockPanel>
                            <Button x:Name="ToggleRecoverableButton" DockPanel.Dock="Right"
                                    Click="ToggleRecoverableButton_Click"/>
                            <TextBlock x:Name="RecoverableCountText" VerticalAlignment="Center"/>
                        </DockPanel>
                    </Border>
                    <DockPanel DockPanel.Dock="Top" Margin="4" LastChildFill="True">
                        <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Center">
                            <RadioButton x:Name="SearchFieldSender" Content="Remetente" GroupName="MessageSearchField" Tag="Sender"
                                         Style="{StaticResource SearchToggleButtonStyle}" Checked="SearchFieldRadio_Checked"/>
                            <RadioButton x:Name="SearchFieldSubject" Content="Assunto" GroupName="MessageSearchField" Tag="Subject" IsChecked="True"
                                         Style="{StaticResource SearchToggleButtonStyle}" Checked="SearchFieldRadio_Checked"/>
                            <RadioButton x:Name="SearchFieldContent" Content="Conteúdo" GroupName="MessageSearchField" Tag="Content"
                                         Style="{StaticResource SearchToggleButtonStyle}" Checked="SearchFieldRadio_Checked"/>
                            <TextBlock Text="Carregando conteúdo…" VerticalAlignment="Center" Margin="8,0,0,0"
                                       FontStyle="Italic" Foreground="Gray"
                                       Visibility="{Binding MessageList.IsContentLoading, Converter={StaticResource BoolToVisibilityConverter}}"/>
                        </StackPanel>
                        <TextBox x:Name="SearchBox" VerticalContentAlignment="Center" Margin="0,0,8,0"
                                 TextChanged="SearchBox_TextChanged"/>
                    </DockPanel>
                    <ListView x:Name="MessageListView"
```

- [ ] **Step 3: Tentar compilar (falha esperada nesta tarefa)**

Run: `dotnet build src/LEC.App`
Expected: erro de compilação — `SearchBox_TextChanged` e `SearchFieldRadio_Checked` referenciados no XAML ainda não existem em `MainWindow.xaml.cs`. Isso é esperado: a Task 5 adiciona os handlers. Confirmar que a única falha é essa (nenhum erro de XML/binding além disso) antes de seguir.

- [ ] **Step 4: Commit**

```bash
git add src/LEC.App/MainWindow.xaml
git commit -m "Add search box and field toggles above the message list"
```

(Commit intencionalmente com o code-behind ainda incompleto — a Task 5, na sequência imediata, resolve a falha de build do Step 3. Se preferir manter o build verde a cada commit, aplique a Task 5 antes de fazer este commit.)

---

## Task 5: Wiring em `MainWindow.xaml.cs` + reset ao trocar de pasta

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml.cs` (novos handlers + helper de seleção + `ResetMessageSearchUi`, chamado nos 4 pontos que recarregam a lista de mensagens)

**Interfaces:**
- Consumes (da Task 2/3): `MessageListViewModel.SearchText` (string, set), `MessageListViewModel.SearchField` (`MessageSearchField`, set), `MessageListViewModel.SortBySender()`/`SortByDate()` (já existentes).
- Consumes (da Task 4): elementos nomeados `SearchBox`, `SearchFieldSubject` (`RadioButton`), e os eventos `TextChanged`/`Checked` que este arquivo agora implementa.
- Produces: nada consumido por tarefas futuras — última tarefa do plano.

- [ ] **Step 1: Substituir `MessageListView_ColumnHeaderClick` e adicionar os novos handlers**

Em `src/LEC.App/MainWindow.xaml.cs`, trocar o método `MessageListView_ColumnHeaderClick` inteiro (as linhas que vão de `private void MessageListView_ColumnHeaderClick` até o `}` que o fecha) por:

```csharp
    private void MessageListView_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: not null } header) return;
        if (_viewModel.MessageList is not { } messageList) return; // no profile open

        if (header.Column != SenderColumn && header.Column != DateColumn) return;

        ApplyToMessageListPreservingSelection(() =>
        {
            if (header.Column == SenderColumn)
                messageList.SortBySender();
            else
                messageList.SortByDate();
        });
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel.MessageList is not { } messageList) return;
        ApplyToMessageListPreservingSelection(() => messageList.SearchText = SearchBox.Text);
    }

    private void SearchFieldRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.MessageList is not { } messageList) return;
        if (sender is not RadioButton { Tag: string tag }) return;
        ApplyToMessageListPreservingSelection(() => messageList.SearchField = Enum.Parse<MessageSearchField>(tag));
    }

    // MessageListViewModel.Messages.Clear() -- rodado internamente sempre que a ordenação ou
    // o filtro de busca mudam o que é exibido -- dispara um CollectionChanged Reset, que
    // derruba ListView.SelectedItems e limpa o painel de leitura via
    // MessageListView_SelectionChanged. Captura a seleção antes da mudança e restaura depois,
    // pra ordenar/filtrar ser transparente ao usuário sempre que a mensagem selecionada
    // continuar aparecendo.
    private void ApplyToMessageListPreservingSelection(Action applyChange)
    {
        var selected = MessageListView.SelectedItems.Cast<MessageHeader>().ToList();
        applyChange();
        foreach (var h in selected)
            MessageListView.SelectedItems.Add(h);
    }

    // Chamado depois de todo recarregamento da lista de mensagens (troca de pasta ou reload da
    // mesma pasta após mover/apagar/alternar recuperáveis) -- MessageListViewModel.LoadAsync já
    // reseta SearchText/SearchField internamente; isso só sincroniza os controles do XAML (que
    // não são bindados diretamente, ver Task 4) pra refletir esse reset visualmente.
    private void ResetMessageSearchUi()
    {
        SearchBox.Text = string.Empty;
        SearchFieldSubject.IsChecked = true;
    }
```

- [ ] **Step 2: Chamar `ResetMessageSearchUi()` nos 4 pontos que recarregam a lista de mensagens**

Em `src/LEC.App/MainWindow.xaml.cs`, aplicar as 4 mudanças abaixo (todas seguem o mesmo princípio: logo depois que `SelectFolderAsync` termina, sincronizar a UI de busca).

**2a.** No handler de seleção da árvore de pastas, trocar:
```csharp
                await _viewModel.SelectFolderAsync(profile, node);
                UpdateScavengeButtonVisibility(node);
```
Por:
```csharp
                await _viewModel.SelectFolderAsync(profile, node);
                ResetMessageSearchUi();
                UpdateScavengeButtonVisibility(node);
```

**2b.** Em `ToggleRecoverableButton_Click`, trocar:
```csharp
            await _viewModel.SelectFolderAsync(profile, node); // reload the list + refresh status/button state
            UpdateScavengeButtonVisibility(node);
```
Por:
```csharp
            await _viewModel.SelectFolderAsync(profile, node); // reload the list + refresh status/button state
            ResetMessageSearchUi();
            UpdateScavengeButtonVisibility(node);
```

**2c.** Este bloco aparece **duas vezes**, idêntico, em `MoveMessage_Click` e em `DeleteMessage_Click`/`DeleteMessages_Click` (a mesma sequência de 4 linhas). Aplicar a mesma troca nas **duas** ocorrências:

De:
```csharp
        if (_selectedProfile is { } profile && _selectedNode is { } node)
            await _viewModel.SelectFolderAsync(profile, node);

        NavigateToRenderedMessage(string.Empty);
```
Para:
```csharp
        if (_selectedProfile is { } profile && _selectedNode is { } node)
        {
            await _viewModel.SelectFolderAsync(profile, node);
            ResetMessageSearchUi();
        }

        NavigateToRenderedMessage(string.Empty);
```

- [ ] **Step 3: Compilar**

Run: `dotnet build src/LEC.App`
Expected: build sem erros — resolve a falha esperada no Step 3 da Task 4 (`SearchBox_TextChanged`/`SearchFieldRadio_Checked` agora existem).

- [ ] **Step 4: Rodar a suíte completa de testes**

Run: `dotnet test`
Expected: todos os testes passam (esta tarefa não adiciona testes automatizados — interação de clique/digitação na UI real não tem framework de teste no projeto hoje; a lógica que ela invoca já está coberta pelas Tasks 2 e 3).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/MainWindow.xaml.cs
git commit -m "Wire message search box and field toggles to MessageListViewModel"
```

- [ ] **Step 6: Checklist manual (GUI) — rodar o app e verificar**

Run: `dotnet run --project src/LEC.App`

1. Abrir um perfil e uma pasta com várias mensagens de remetentes, assuntos e corpos variados.
2. Digitar um texto com "Remetente" selecionado (padrão inicial é "Assunto" — trocar antes de digitar): lista filtra dinamicamente a cada tecla, só remetentes que contêm o texto aparecem.
3. Trocar pra "Assunto" sem apagar o texto: filtro reaplica na hora, agora sobre assunto.
4. Trocar pra "Conteúdo": filtro reaplica sobre o corpo das mensagens; se a pasta é grande, observar o indicador "Carregando conteúdo…" (aparece só enquanto o pré-carregamento da pasta atual não terminou) e o resultado completando conforme carrega.
5. Apagar o texto do campo: filtro some, lista completa volta.
6. Buscar em maiúsculas/minúsculas misturadas (ex. `JoAo`) dá o mesmo resultado que em minúsculas.
7. Selecionar uma mensagem (aparece no painel de leitura) e continuar digitando um texto que ainda inclui essa mensagem no resultado: seleção e painel de leitura permanecem, sem piscar/limpar.
8. Trocar de pasta com um filtro ativo: pasta nova abre com campo de busca vazio e "Assunto" selecionado (filtro não vaza entre pastas).
9. Ordenar por "De" ou "Data" (clicando no cabeçalho da coluna) com um filtro ativo: continua mostrando só as mensagens filtradas, agora reordenadas.
10. Mover ou apagar uma mensagem com um filtro ativo: a lista recarrega e a busca reseta (comportamento igual ao de trocar de pasta).
11. Pasta com uma mensagem corrompida/scavenged (se disponível no perfil de teste): buscar por conteúdo não trava nem lança erro; a mensagem simplesmente não aparece nesse tipo de busca, mas continua normal em Remetente/Assunto/sem filtro.

---

## Self-Review

**Cobertura da spec:**
- Campo de busca + 3 toggles mutuamente exclusivos acima das colunas → Task 4 (Step 2: `RadioButton` com `GroupName` compartilhado) ✓
- Filtragem dinâmica por Remetente/Assunto → Task 2 (`ApplyFilter`/`MatchesSearch`, testes `FiltersBySenderNameCaseInsensitively`/`FiltersBySubjectCaseInsensitively`) ✓
- Filtragem por Conteúdo (pré-carregamento em background, resultado completando sozinho) → Task 1 (`ExtractSearchableTextAsync`) + Task 3 (`PreloadContentAsync`, `_bodyByMessage`, teste `FiltersByContentAfterPreloadCompletes`) ✓
- Case-insensitive → Task 2 (`Contains` com `OrdinalIgnoreCase`), testado em todos os testes de filtro com texto em CAIXA ALTA contra dado em minúsculas ✓
- Trocar toggle reaplica com texto atual → Task 2 (`OnSearchFieldChanged`), teste `SwitchingSearchFieldReappliesImmediatelyWithExistingText` ✓
- Texto vazio = sem filtro → Task 2, teste `EmptySearchTextShowsAllMessages` ✓
- Estado visual do toggle ativo distinto → Task 4 (`SearchToggleButtonStyle`, `Trigger IsChecked=True`) ✓
- Filtro sobre a lista atualmente exibida, sem alterar dado original → Task 2 (`_headers` como fonte, `MessageHeader` nunca mutado) ✓
- Reaproveitar tela/estrutura existente → nenhuma tela nova; busca vive na mesma `MessageListView` existente (Task 4) ✓
- Reset ao trocar de pasta (decisão do design) → Task 2/3 (`LoadAsync` reseta `SearchText`/`SearchField`) + Task 5 (`ResetMessageSearchUi` sincroniza os controles não-bindados do XAML) ✓
- Degradação de mensagem ilegível → Task 1 (`ExtractSearchableTextAsync` nunca lança) + Task 3 (`catch` genérico em `PreloadContentAsync`), teste `ContentSearchIgnoresMessagesWhoseBodyFailsToLoad` ✓
- Preservação de seleção/painel de leitura durante filtragem (achado durante o planejamento, não estava no design doc original — `Messages.Clear()` já derrubava seleção na ordenação existente; a busca dispara `Clear()` a cada tecla, então o mesmo problema apareceria em escala maior sem essa correção) → Task 5 (`ApplyToMessageListPreservingSelection`, reaproveitado também pelo handler de sort existente) ✓
- Checklist manual → Task 5, Step 6 ✓

**Placeholders:** nenhum "TBD"/"similar to"/passo sem código — checado.

**Consistência de tipos:** `MessageSearchField` (com membros `Sender`/`Subject`/`Content`), `SearchText` (string), `SearchField` (`MessageSearchField`), `IsContentLoading` (bool), `ContentLoadTask` (Task), `MessageRenderer.ExtractSearchableTextAsync(Stream, CancellationToken) -> Task<string>` usados de forma idêntica entre a tarefa que os define (Task 1/2/3) e as que os consomem (Task 3/4/5) — nomes e assinaturas conferidos linha a linha contra os blocos de código de cada tarefa.
