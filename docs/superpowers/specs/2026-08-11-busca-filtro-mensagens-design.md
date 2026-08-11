# Busca/filtro de mensagens (remetente, assunto, conteúdo) — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `MessageListViewModel` (`src/LEC.App/ViewModels/MessageListViewModel.cs`), `MainWindow.xaml` (grade `MessageListView`), `MessageRenderer` (`src/LEC.App/Services/MessageRenderer.cs`) — nenhum contrato novo em `IMailStore`/`DbxStore`.
**Contexto:** feature pedida diretamente pelo usuário. Hoje não existe nenhuma forma de filtrar a lista de mensagens de uma pasta — só ordenar (ver `2026-08-05-ordenacao-lista-mensagens-design.md`). O usuário quer um campo de busca acima das colunas, acompanhado de 3 toggles mutuamente exclusivos (Remetente / Assunto / Conteúdo) que definem em qual campo o texto digitado é procurado.

## 1. Objetivo

Adicionar, acima do cabeçalho de colunas da lista de mensagens, um `TextBox` de busca e 3 toggles mutuamente exclusivos. Com texto digitado, a lista mostra só as mensagens cujo campo selecionado (De / Assunto / corpo) contém o texto (case-insensitive). Campo vazio = sem filtro, lista completa como hoje. A filtragem é dinâmica (a cada tecla) e não altera dado nenhum — só o que é exibido.

## 2. Decisões de escopo (confirmadas com o usuário)

- **Busca por conteúdo pré-carrega o corpo de todas as mensagens da pasta ao abri-la** (não só sob demanda quando o usuário digita) — abre e decodifica cada mensagem via `OpenMessageAsync`, mesmo que o usuário nunca use o toggle "Conteúdo". Trade-off aceito conscientemente: mais custo de I/O ao trocar de pasta, em troca de busca por conteúdo instantânea depois.
- **A lista de headers aparece imediatamente ao trocar de pasta, como hoje; o pré-carregamento do corpo roda em background**, sem bloquear a troca de pasta.
- **Se o usuário já está buscando por "Conteúdo" enquanto o pré-carregamento ainda roda, o resultado vai completando sozinho** conforme mais corpos terminam de carregar — sem indicação de erro nem bloqueio do campo, só um indicador discreto de "carregando conteúdo…" enquanto isso não termina.
- **"Remetente" compara com o mesmo texto exibido na coluna "De" (`SenderName`)**, não o endereço de e-mail (`SenderAddress`) — busca no que o usuário vê na tela.
- **Toggle padrão ao abrir uma pasta: "Assunto".**
- **Trocar de pasta (ou de perfil) sempre reseta a busca**: texto limpo, toggle volta pro padrão ("Assunto"). Evita o caso de uma pasta parecer vazia por causa de um filtro esquecido de outra pasta.
- **Sem persistência em disco** — mesmo espírito da ordenação: vive em memória, no `MessageListViewModel`, durante a sessão.
- **Mensagem cujo corpo falha ao decodificar (MIME corrompido) nunca quebra a busca** — degrada como o resto do app: some da busca por conteúdo (não é considerada correspondência), mas continua aparecendo normalmente em Remetente/Assunto/lista sem filtro.

## 3. `MessageListViewModel` — nova arquitetura interna

Hoje `Messages` (a coleção ligada à `ListView`) é ao mesmo tempo armazenamento e exibição — `ApplySortColumn` reordena a própria coleção. Isso muda:

- `_headers`: `List<MessageHeader>` privada com todos os headers da pasta atual, sempre já na ordem de sort corrente (o que `Messages` é hoje).
- `Messages`: passa a ser só a **projeção filtrada** de `_headers` — o que a `ListView` mostra de fato.
- `_bodyByMessage`: `Dictionary<MessageRef, string>` privado, populado pelo pré-carregamento de conteúdo (seção 4).

Novo estado observável:

```csharp
public enum MessageSearchField { Sender, Subject, Content }

[ObservableProperty] private string _searchText = string.Empty;
[ObservableProperty] private MessageSearchField _searchField = MessageSearchField.Subject;
[ObservableProperty] private bool _isContentLoading;
```

`OnSearchTextChanged`/`OnSearchFieldChanged` (métodos parciais gerados pelo `[ObservableProperty]`) chamam `ApplyFilter()` — mudar o texto ou trocar o toggle reaplica o filtro imediatamente, usando o texto que já está no campo (cobre o requisito "ao trocar o toggle, reaplica com o texto atual").

`ApplyFilter()`:

```csharp
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
    MessageSearchField.Sender  => Contains(header.SenderName),
    MessageSearchField.Subject => Contains(header.Subject),
    MessageSearchField.Content => _bodyByMessage.TryGetValue(header.Id, out var body) && Contains(body),
    _ => false,
};

private bool Contains(string? text) =>
    !string.IsNullOrEmpty(text) && text.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
```

Uma mensagem ainda sem corpo carregado (`_bodyByMessage` sem entrada pra ela) simplesmente não entra no resultado de "Conteúdo" ainda — é isso que faz o resultado "completar sozinho" conforme o pré-carregamento avança (seção 4 chama `ApplyFilter()` de novo a cada lote).

`LoadAsync` e `ApplySortColumn` passam a operar sobre `_headers`, chamando `ApplyFilter()` no final em vez de popular `Messages` diretamente:

```csharp
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

private void ApplySortColumn(MessageSortColumn column)
{
    // ... lógica de coluna/direção inalterada ...
    _headers = Order(_headers).ToList();
    ApplyFilter();
    UpdateColumnHeaders();
}
```

`ContentLoadTask` (propriedade pública, `Task`, default `Task.CompletedTask`) existe só para os testes poderem esperar o pré-carregamento terminar antes de checar o resultado — não é consumida pela UI.

## 4. Pré-carregamento de conteúdo (background)

```csharp
private const int RefreshBatchSize = 25;

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
                using var stream = await _store.OpenMessageAsync(header.Id, ownerCts.Token);
                _bodyByMessage[header.Id] = await MessageRenderer.ExtractSearchableTextAsync(stream, ownerCts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Corpo ilegível (MIME corrompido/truncado) -- degrada: mensagem só não
                // aparece em buscas por Conteúdo, resto do app não é afetado.
            }

            if (++sinceRefresh >= RefreshBatchSize && ReferenceEquals(_contentLoadCts, ownerCts))
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
            ApplyFilter(); // garante um refresh final mesmo com menos de RefreshBatchSize mensagens
    }
}
```

Pontos-chave:
- Mensagens são abertas **uma de cada vez, sequencialmente** (mesmo padrão de `CopyFolderSubtreeAsync` em `MainWindowViewModel`) — não paraleliza leituras contra o mesmo arquivo `.dbx`.
- Trocar de pasta cancela o pré-carregamento anterior (`_contentLoadCts?.Cancel()` em `LoadAsync`) — o `ReferenceEquals(_contentLoadCts, ownerCts)` garante que uma tarefa cancelada nunca mexe em `IsContentLoading`/`Messages` depois que uma tarefa mais nova já assumiu.
- O filtro só é reaplicado a cada `RefreshBatchSize` (25) mensagens processadas, mais uma vez ao final — evita recalcular a lista inteira a cada mensagem individual em pastas grandes.

## 5. `MessageRenderer` — extração de texto pesquisável

Novo método público, ao lado de `RenderAsync`/`SaveAttachmentAsync`, reaproveitando o mesmo padrão de "bufferizar bytes, tentar parsear, cair pro texto bruto se falhar" já usado em `RenderAsync`:

```csharp
public static async Task<string> ExtractSearchableTextAsync(Stream rawMessage, CancellationToken ct = default)
{
    byte[] raw = await ReadAllBytesAsync(rawMessage, ct); // helper extraído de RenderAsync, reaproveitado pelos dois métodos

    MimeMessage? message = TryParse(raw); // helper extraído de RenderAsync
    if (message is null)
        return Encoding.Latin1.GetString(raw); // mesmo fallback usado pra exibição

    if (message.TextBody is { } text)
        return text;

    if (message.HtmlBody is { } html)
        return WebUtility.HtmlDecode(HtmlTagRegex.Replace(html, " "));

    return string.Empty;
}

private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
```

`ReadAllBytesAsync` e `TryParse` são pequenos helpers extraídos do corpo atual de `RenderAsync` (que hoje faz os dois passos inline) — elimina a duplicação entre os dois métodos sem mudar o comportamento de `RenderAsync`.

## 6. UI (`MainWindow.xaml`)

Nova linha, acima da `ListView` existente, dentro do mesmo `DockPanel` (`Grid.Row="0"`), depois de `RecoverableBar` e antes de `MessageListView`:

```xml
<DockPanel DockPanel.Dock="Top" Margin="4" LastChildFill="True">
    <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Center">
        <RadioButton Content="Remetente" GroupName="MessageSearchField" Tag="Sender"
                     Style="{StaticResource SearchToggleButtonStyle}" Checked="SearchFieldRadio_Checked"/>
        <RadioButton Content="Assunto" GroupName="MessageSearchField" Tag="Subject" IsChecked="True"
                     Style="{StaticResource SearchToggleButtonStyle}" Checked="SearchFieldRadio_Checked"/>
        <RadioButton Content="Conteúdo" GroupName="MessageSearchField" Tag="Content"
                     Style="{StaticResource SearchToggleButtonStyle}" Checked="SearchFieldRadio_Checked"/>
        <TextBlock Text="Carregando conteúdo…" VerticalAlignment="Center" Margin="8,0,0,0"
                   FontStyle="Italic" Foreground="Gray"
                   Visibility="{Binding MessageList.IsContentLoading, Converter={StaticResource BoolToVisibilityConverter}}"/>
    </StackPanel>
    <TextBox Text="{Binding MessageList.SearchText, UpdateSourceTrigger=PropertyChanged}"
             VerticalContentAlignment="Center" Margin="0,0,8,0"/>
</DockPanel>
```

- `RadioButton` com `GroupName` compartilhado garante exclusividade nativa do WPF (sem precisar de lógica manual pra "só um toggle selecionado por vez").
- `SearchToggleButtonStyle`: novo `Style` simples baseado em `RadioButton`, com `ControlTemplate` visual de botão (sem a bolinha padrão de rádio) — fundo destacado quando `IsChecked=True`, pra diferenciar claramente o toggle ativo dos demais.
- `BoolToVisibilityConverter`: usa o conversor embutido do WPF (`System.Windows.Controls.BooleanToVisibilityConverter`), declarado como recurso — não precisa de conversor customizado.
- Handler no code-behind, análogo ao `MessageListView_ColumnHeaderClick` já existente:

```csharp
private void SearchFieldRadio_Checked(object sender, RoutedEventArgs e)
{
    if (_viewModel.MessageList is null || sender is not RadioButton { Tag: string tag }) return;
    _viewModel.MessageList.SearchField = Enum.Parse<MessageSearchField>(tag);
}
```

## 7. Testes

Em `MessageListViewModelTests.cs`, usando `FakeMailStore` (já suporta `OpenMessageAsync` via `rawByRef`):

- Filtro por Remetente: só mensagens com `SenderName` contendo o texto (case-insensitive) aparecem.
- Filtro por Assunto: idem, sobre `Subject`.
- Filtro por Conteúdo: registra bytes MIME reais (texto simples) em `rawByRef`, aguarda `vm.ContentLoadTask`, aplica `SearchField = Content` + `SearchText`, confirma que só a mensagem com aquele texto no corpo aparece.
- Texto vazio: sem filtro nenhum, lista completa (com qualquer toggle selecionado).
- Trocar de toggle sem mudar o texto reaplica o filtro imediatamente com o novo campo.
- Trocar de pasta reseta `SearchText` pra vazio e `SearchField` pra `Subject`.
- Filtro compõe com ordenação: aplicar um filtro e depois `SortBySender()` mantém só as mensagens filtradas, agora ordenadas.
- Mensagem com corpo que falha ao parsear (bytes inválidos registrados em `rawByRef`) não aparece em busca por Conteúdo, mas continua aparecendo em Remetente/Assunto e na lista sem filtro.
- `IsContentLoading` é `true` durante o pré-carregamento e `false` depois de `ContentLoadTask` completar.

Em `MessageRendererTests.cs` (se existir; senão, novo teste na classe já usada pra `RenderAsync`):
- `ExtractSearchableTextAsync` retorna `TextBody` quando presente.
- Sem `TextBody`, retorna o `HtmlBody` sem tags HTML.
- MIME corrompido: retorna os bytes brutos como texto (mesmo fallback de `RenderAsync`), sem lançar exceção.

Sem teste de UI/XAML automatizado (mesma limitação já documentada nos specs anteriores) — checklist manual cobre a interação real dos toggles/campo.

## 8. Checklist manual (GUI)

- Abrir uma pasta com várias mensagens de remetentes, assuntos e corpos variados.
- Digitar um texto com "Remetente" selecionado: lista filtra dinamicamente, só remetentes que contêm o texto aparecem.
- Trocar pra "Assunto" sem apagar o texto: filtro reaplica na hora, agora sobre assunto.
- Trocar pra "Conteúdo": filtro reaplica sobre o corpo das mensagens; se a pasta é grande, observar o indicador "carregando conteúdo…" e o resultado completando conforme carrega.
- Apagar o texto do campo: filtro some, lista completa volta.
- Buscar em maiúsculas/minúsculas misturadas (`JoAo`) dá o mesmo resultado que em minúsculas.
- Trocar de pasta com um filtro ativo: pasta nova abre com campo vazio e "Assunto" selecionado (filtro não vaza entre pastas).
- Ordenar por "De" ou "Data" com um filtro ativo: continua mostrando só as mensagens filtradas, agora reordenadas.
- Pasta com uma mensagem corrompida/scavenged: buscar por conteúdo não trava nem lança erro; a mensagem simplesmente não aparece nesse tipo de busca.

## 9. Limitações conhecidas

- Pré-carregamento de conteúdo sempre roda ao abrir qualquer pasta, mesmo sem busca por conteúdo — em pastas muito grandes (milhares de mensagens), isso consome I/O e memória (um `string` por mensagem) que hoje não é gasto. Aceito conscientemente (seção 2); pode ser revisitado depois se afetar pastas reais do usuário.
- Leituras sequenciais (uma mensagem por vez) — não paraleliza por segurança contra o mesmo arquivo `.dbx`; pastas muito grandes podem levar alguns segundos até o pré-carregamento terminar.
