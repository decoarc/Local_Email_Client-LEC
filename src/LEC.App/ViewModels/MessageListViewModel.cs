using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
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

    /// <summary>
    /// Optional hook the view sets so every Messages mutation — whether triggered from the
    /// UI (search box, toggle, sort click) or from the background content preload — goes
    /// through the same selection-preserving wrapper. Without this, PreloadContentAsync's own
    /// ApplyFilter() calls would blank the WPF ListView's selection with no code-behind call
    /// site able to wrap them (unlike the UI-triggered paths, which Task 5 already wraps).
    /// </summary>
    public Action<Action>? WrapMessagesChange { get; set; }

    public MessageListViewModel(IMailStore store) => _store = store;

    /// <summary>
    /// Cancela o pré-carregamento de conteúdo em voo (se houver). Precisa ser chamado antes de
    /// qualquer operação que mute o store numa thread de background -- DbxStore.AddMessageAsync
    /// roda o corpo inteiro dentro de Task.Run e mexe em _openFolders/_messagesByFolder, os
    /// mesmos dicionários que o OpenMessageAsync do pré-carregamento lê ao retomar na thread de
    /// UI (ver o remarks de AddMessageAsync em DbxStore.cs, que exige serialização pelo
    /// chamador). Não é preciso religar nada depois: todo call site já refaz um
    /// SelectFolderAsync na sequência, e LoadAsync sempre dispara um pré-carregamento novo.
    /// </summary>
    public void CancelContentPreload() => _contentLoadCts?.Cancel();

    public async Task LoadAsync(StoreFolder folder, CancellationToken ct = default)
    {
        _contentLoadCts?.Cancel();
        var cts = _contentLoadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

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

    // Toda mutação de Messages passa por aqui -- inclusive as disparadas pelo próprio
    // PreloadContentAsync, que não têm call site no code-behind pra envolver. Por isso o
    // wrapper de preservação de seleção é um hook do view model (WrapMessagesChange), e não
    // só um helper chamado nos handlers da UI.
    private void ApplyFilter()
    {
        void Apply()
        {
            IEnumerable<MessageHeader> filtered = string.IsNullOrEmpty(SearchText)
                ? _headers
                : _headers.Where(MatchesSearch);

            Messages.Clear();
            foreach (var header in filtered)
                Messages.Add(header);
        }

        if (WrapMessagesChange is { } wrap)
            wrap(Apply);
        else
            Apply();
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
    // IsContentLoading/_bodyByMessage/Messages depois que uma execução mais nova já assumiu.
    //
    // IMPORTANTE: isto roda inteiramente na thread de UI, nunca em background de verdade via
    // Task.Run/thread-pool -- DbxStore não é thread-safe (muta _openFolders/_messagesByFolder
    // sem sincronização; ver o comentário de AddMessageAsync em DbxStore.cs, que documenta que
    // TODAS as chamadas contra a mesma instância, incluindo EnumerateHeadersAsync/
    // OpenMessageAsync, precisam ser serializadas pelo chamador). Rodar OpenMessageAsync numa
    // thread do pool enquanto LoadAsync chama EnumerateHeadersAsync na UI pra uma pasta
    // seguinte seria uma corrida de verdade contra esses dicionários.
    //
    // Só que OpenMessageAsync do DbxStore faz I/O síncrono disfarçado de Task.FromResult, e
    // ExtractListMetadataAsync opera sobre MemoryStream (também sempre síncrono) -- então sem
    // nenhum ponto de cessão explícito, o loop inteiro rodaria de uma vez, bloqueando a UI até
    // a pasta inteira ser lida.
    //
    // `await Task.Yield()` NÃO resolve isso, apesar de parecer que sim: numa thread de UI do
    // WPF ele vira um DispatcherSynchronizationContext.Post, que enfileira a continuação em
    // DispatcherPriority.Normal (9) -- e Normal é MAIOR prioridade que Render (7), Input (5) e
    // Background (4). Como cada Task.Yield() já reenfileira a próxima continuação em Normal
    // antes da iteração seguinte começar, sempre há um item Normal pendente durante todo o
    // pré-carregamento, e o dispatcher do WPF sempre drena prioridades mais altas primeiro --
    // então Render e Input nunca ganham a vez. Resultado: LoadAsync até retorna ao chamador,
    // mas a janela continua sem repintar e sem responder a clique/tecla até a pasta inteira
    // terminar. Por isso usamos YieldToUiAsync (abaixo) em vez de Task.Yield() puro: ele cede
    // explicitamente em DispatcherPriority.Background quando há um Dispatcher de verdade
    // rodando, que é mais baixo que Render/Input e deixa os dois rodarem antes da próxima
    // mensagem ser processada. Não "simplificar" de volta pra Task.Yield() -- é exatamente o
    // que já foi tentado e não resolvia o freeze visual.
    private async Task PreloadContentAsync(List<MessageHeader> headers, CancellationTokenSource ownerCts)
    {
        if (headers.Count == 0)
        {
            if (ReferenceEquals(_contentLoadCts, ownerCts))
                IsContentLoading = false;
            return;
        }

        IsContentLoading = true;
        await YieldToUiAsync(); // deixa LoadAsync retornar ao clique que o disparou antes de qualquer I/O

        try
        {
            int sinceRefresh = 0;
            foreach (var header in headers)
            {
                ownerCts.Token.ThrowIfCancellationRequested();
                try
                {
                    var stream = await _store.OpenMessageAsync(header.Id, ownerCts.Token);
                    string body;
                    bool hasAttachment;
                    string? to, cc;
                    await using (stream)
                    {
                        (body, hasAttachment, to, cc) = await MessageRenderer.ExtractListMetadataAsync(stream, ownerCts.Token);
                    }

                    if (ReferenceEquals(_contentLoadCts, ownerCts))
                    {
                        _bodyByMessage[header.Id] = body;
                        header.HasAttachment = hasAttachment;
                        header.To = to;
                        header.Cc = cc;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Corpo ilegível (MIME corrompido/truncado, ou I/O falhou) -- degrada:
                    // mensagem só não aparece em buscas por Conteúdo nem ganha ícone de anexo,
                    // resto do app não é afetado. HasAttachment fica null (nunca setado).
                }

                await YieldToUiAsync(); // deixa o WPF repintar/processar input entre cada mensagem

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
            // Só dá pra rodar depois que a pasta inteira terminou: DuplicateDetector compara
            // pelo campo To, que só existe a partir daqui (ver PreloadContentAsync acima).
            DuplicateDetector.MarkDuplicates(headers);

            IsContentLoading = false;
            if (SearchField == MessageSearchField.Content)
                ApplyFilter(); // garante um refresh final mesmo com menos de ContentRefreshBatchSize mensagens
        }
    }

    // Cede o controle pra UI numa prioridade baixa o bastante pra Render/Input rodarem antes da
    // continuação retomar (ver o comentário acima de PreloadContentAsync sobre por que
    // Task.Yield() puro não bastava). Sob os testes (xUnit, sem Application/Dispatcher do WPF
    // rodando), Application.Current é null, então cai pro Task.Yield() simples -- suficiente
    // pra manter a state machine assíncrona nos testes, sem precisar de um Dispatcher de
    // verdade bombeando mensagens.
    private static async Task YieldToUiAsync()
    {
        if (Application.Current?.Dispatcher is not null)
            await Dispatcher.Yield(DispatcherPriority.Background);
        else
            await Task.Yield();
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
