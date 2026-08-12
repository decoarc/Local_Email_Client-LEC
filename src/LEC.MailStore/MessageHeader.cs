using System.ComponentModel;
using LEC.DbxFormat;

namespace LEC.MailStore;

public sealed class MessageHeader : INotifyPropertyChanged
{
    public required MessageRef Id { get; init; }
    public string? Subject { get; init; }
    public string? SenderName { get; init; }
    public string? SenderAddress { get; init; }
    public DateTime? Date { get; init; }
    public uint Size { get; init; }
    public string? MessageId { get; init; }
    public StoreHealth Health { get; init; }
    public RecoveredMessageKind? RecoveredKind { get; init; } // null = normal, index-backed message

    private bool? _hasAttachment;

    /// <summary>
    /// null = ainda não verificado (o pré-carregamento de MessageListViewModel não chegou nesta
    /// mensagem ainda, ou a mensagem falhou ao parsear); true/false = resultado real de um parse
    /// MIME. Diferente dos demais campos desta classe (todos vêm prontos do índice .dbx em
    /// EnumerateHeadersAsync), este é preenchido depois, em background -- por isso é mutável
    /// (set), não init. Notifica via INotifyPropertyChanged em vez de exigir que
    /// MessageListViewModel reconstrua a ObservableCollection inteira pra refletir a mudança na
    /// UI -- reconstruir (Messages.Clear()+Add()) dispara um CollectionChanged Reset, que derruba
    /// a seleção da ListView e, por tabela, rebloqueia imagens remotas e re-renderiza o painel de
    /// leitura a cada lote do pré-carregamento (achado da revisão final desta feature).
    /// </summary>
    public bool? HasAttachment
    {
        get => _hasAttachment;
        set
        {
            if (_hasAttachment == value) return;
            _hasAttachment = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAttachment)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
