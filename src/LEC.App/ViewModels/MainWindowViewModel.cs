using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.DbxFormat;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private IMailStore? _store;

    [ObservableProperty]
    private string? _statusText;

    public FolderTreeViewModel FolderTree { get; } = new();

    [ObservableProperty]
    private MessageListViewModel? _messageList;

    [ObservableProperty]
    private MessageReaderViewModel? _messageReader;

    public void OpenProfile(string profileDirectory)
    {
        try
        {
            var newStore = DbxStore.Open(profileDirectory);

            Store?.Dispose();
            Store = newStore;
            FolderTree.Build(Store.Folders);
            MessageList = new MessageListViewModel(Store);
            MessageReader = new MessageReaderViewModel(Store);
            StatusText = Store.Health == StoreHealth.Ok
                ? null
                : $"Perfil aberto com avisos (Health: {Store.Health}).";
        }
        catch (Exception ex) when (ex is DbxFormatException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Não foi possível abrir o perfil: {ex.Message}";
        }
    }

    public async Task SelectFolderAsync(FolderNodeViewModel node, CancellationToken ct = default)
    {
        if (MessageList is not null)
            await MessageList.LoadAsync(node.Folder, ct);
    }

    public async Task SelectMessageAsync(MessageHeader header, CancellationToken ct = default)
    {
        if (MessageReader is not null)
            await MessageReader.LoadAsync(header.Id, ct);
    }
}
