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
    // IsContentLoading/_bodyByMessage/Messages depois que uma execução mais nova já assumiu.
    //
    // O trabalho bloqueante por mensagem (abrir o arquivo + extrair texto) roda dentro de
    // Task.Run para sair de fato da thread de UI -- OpenMessageAsync do DbxStore faz I/O
    // síncrono disfarçado de Task.FromResult, e ExtractSearchableTextAsync opera sobre
    // MemoryStream (também sempre síncrono), então sem o Task.Run nenhum await neste método
    // jamais cede de verdade e a pasta inteira seria lida antes de LoadAsync retornar ao
    // clique que a disparou. Como nenhum ConfigureAwait(false) é usado, a continuação após
    // o Task.Run volta automaticamente pro contexto original (thread de UI), então as
    // escritas em _bodyByMessage/IsContentLoading/Messages abaixo continuam de thread única.
    private async Task PreloadContentAsync(List<MessageHeader> headers, CancellationTokenSource ownerCts)
    {
        if (headers.Count == 0)
        {
            if (ReferenceEquals(_contentLoadCts, ownerCts))
                IsContentLoading = false;
            return;
        }

        IsContentLoading = true;
        try
        {
            int sinceRefresh = 0;
            foreach (var header in headers)
            {
                ownerCts.Token.ThrowIfCancellationRequested();
                try
                {
                    string body = await Task.Run(async () =>
                    {
                        var stream = await _store.OpenMessageAsync(header.Id, ownerCts.Token);
                        await using (stream)
                        {
                            return await MessageRenderer.ExtractSearchableTextAsync(stream, ownerCts.Token);
                        }
                    }, ownerCts.Token);

                    if (ReferenceEquals(_contentLoadCts, ownerCts))
                        _bodyByMessage[header.Id] = body;
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
