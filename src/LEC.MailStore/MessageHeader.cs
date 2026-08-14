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

    private string? _to;

    /// <summary>
    /// Destinatário(s) "Para" da mensagem, formatado(s) pra exibição ("Nome &lt;endereço&gt;",
    /// vários separados por vírgula). O índice .dbx não guarda a lista completa de
    /// destinatários (só um único ReceiverName/ReceiverAddress) nem CC nenhum -- por isso, assim
    /// como HasAttachment, só fica disponível depois do parse MIME completo em background.
    /// null = ainda não verificado ou sem destinatário nesse campo.
    /// </summary>
    public string? To
    {
        get => _to;
        set
        {
            if (_to == value) return;
            _to = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(To)));
        }
    }

    private string? _cc;

    /// <summary>Mesma origem e mesmas regras de <see cref="To"/>, para o campo "Cc".</summary>
    public string? Cc
    {
        get => _cc;
        set
        {
            if (_cc == value) return;
            _cc = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cc)));
        }
    }

    private bool? _isDuplicate;

    /// <summary>
    /// true = existe outra mensagem com o mesmo remetente/destinatário/assunto/data recebida
    /// antes desta; false = é a mais antiga do seu grupo (ou não tem nenhuma duplicata); null =
    /// DuplicateDetector.MarkDuplicates ainda não rodou pra pasta atual. Depende de <see cref="To"/>,
    /// então herda a mesma janela de "ainda não verificado" desse campo -- só fica definitivo
    /// (true ou false) depois que o pré-carregamento de MessageListViewModel processa a pasta
    /// inteira e chama MarkDuplicates uma vez no fim.
    /// </summary>
    public bool? IsDuplicate
    {
        get => _isDuplicate;
        set
        {
            if (_isDuplicate == value) return;
            _isDuplicate = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDuplicate)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
