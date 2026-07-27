using CommunityToolkit.Mvvm.ComponentModel;
using LEC.App.Services;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class MessageReaderViewModel : ObservableObject
{
    private readonly IMailStore _store;

    [ObservableProperty]
    private RenderedMessage? _current;

    public MessageReaderViewModel(IMailStore store) => _store = store;

    public async Task LoadAsync(MessageRef id, CancellationToken ct = default)
    {
        var stream = await _store.OpenMessageAsync(id, ct);
        await using (stream)
        {
            Current = await MessageRenderer.RenderAsync(stream, ct);
        }
    }
}
