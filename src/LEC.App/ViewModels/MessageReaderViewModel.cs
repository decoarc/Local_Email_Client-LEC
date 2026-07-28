using CommunityToolkit.Mvvm.ComponentModel;
using LEC.App.Services;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class MessageReaderViewModel : ObservableObject
{
    private readonly IMailStore _store;
    private MessageRef? _currentId;

    [ObservableProperty]
    private RenderedMessage? _current;

    public MessageReaderViewModel(IMailStore store) => _store = store;

    public async Task LoadAsync(MessageRef id, CancellationToken ct = default)
    {
        _currentId = id;
        var stream = await _store.OpenMessageAsync(id, ct);
        await using (stream)
        {
            Current = await MessageRenderer.RenderAsync(stream, ct);
        }
    }

    /// <summary>
    /// Relê a mensagem atualmente carregada a partir do store e salva um dos seus anexos em
    /// disco -- ver MessageRenderer.SaveAttachmentAsync pra saber por que releitura e não
    /// reaproveitamento de estado de LoadAsync.
    /// </summary>
    public async Task SaveAttachmentAsync(int attachmentIndex, string destinationPath, CancellationToken ct = default)
    {
        if (_currentId is not { } id)
        {
            throw new InvalidOperationException("Nenhuma mensagem carregada.");
        }

        var stream = await _store.OpenMessageAsync(id, ct);
        await using (stream)
        {
            await MessageRenderer.SaveAttachmentAsync(stream, attachmentIndex, destinationPath, ct);
        }
    }
}
