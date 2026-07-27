using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class MessageListViewModel : ObservableObject
{
    private readonly IMailStore _store;

    public ObservableCollection<MessageHeader> Messages { get; } = new();

    [ObservableProperty]
    private string? _statusText;

    public MessageListViewModel(IMailStore store) => _store = store;

    public async Task LoadAsync(StoreFolder folder, CancellationToken ct = default)
    {
        Messages.Clear();
        StatusText = folder.IsLoadable ? null : "Não foi possível carregar esta pasta.";

        await foreach (var header in _store.EnumerateHeadersAsync(folder, ct))
            Messages.Add(header);
    }
}
