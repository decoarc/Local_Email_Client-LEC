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
