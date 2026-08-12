# Coluna de indicador de anexo na lista de mensagens — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `MessageHeader` (`src/LEC.MailStore/MessageHeader.cs`), `MessageListViewModel` (`src/LEC.App/ViewModels/MessageListViewModel.cs`), `MessageRenderer` (`src/LEC.App/Services/MessageRenderer.cs`), `MainWindow.xaml` (grade `MessageListView`) — nenhum contrato novo em `IMailStore`/`DbxStore`.
**Contexto:** feature pedida diretamente pelo usuário. A ideia original era reaproveitar a coluna "!" existente, mas ela já está em uso para o aviso de `Health` (mensagem com dado parcialmente recuperado — `MainWindow.xaml:187-195`). Também não existe hoje nenhuma forma barata de saber se uma mensagem tem anexo sem abri-la: o índice `.dbx` tem um campo `Flags` (`DbxMessageInfo.Flags`) nunca decodificado neste projeto, e o padrão já estabelecido aqui (ver `DbxFolderFieldId.UnreadCount`, comentário "observado uma vez, plausível, não confirmado") é não assumir o significado de bits não confirmados.

## 1. Objetivo

Mostrar um ícone de anexo (📎) numa coluna nova da lista de mensagens, indicando que a mensagem tem pelo menos um anexo, antes mesmo dela ser aberta.

## 2. Decisões de escopo (confirmadas com o usuário)

- **Coluna nova e dedicada**, ao lado da coluna "!" — não reaproveita nem divide a coluna existente.
- **Detecção via parse MIME real, com cache em memória** — não tenta decodificar o bit de `Flags` do índice `.dbx` (não confirmado, risco de mostrar/ocultar o ícone errado silenciosamente). Trade-off aceito conscientemente: o ícone pode levar um instante pra aparecer em pastas grandes, igual ao já aceito para a busca por Conteúdo (`2026-08-11-busca-filtro-mensagens-design.md`).
- **Reaproveita o pré-carregamento de conteúdo que já existe** (`MessageListViewModel.PreloadContentAsync`, hoje só usado pela busca por "Conteúdo") em vez de criar uma segunda passada de I/O sobre a pasta — cada mensagem já é aberta e parseada uma vez nesse loop; a detecção de anexo sai do mesmo parse.
- **Sem persistência em disco** — mesmo espírito de `_bodyByMessage`: vive em memória durante a sessão, refeito a cada `LoadAsync` (troca de pasta).
- **Mensagem cujo corpo falha ao parsear (MIME corrompido/truncado) nunca quebra nada** — degrada como o resto do app: ícone não aparece (tratado como "sem anexo conhecido"), mesma política já usada pro corpo pesquisável.

## 3. `MessageHeader` — novo campo mutável

```csharp
public sealed class MessageHeader
{
    public required MessageRef Id { get; init; }
    public string? Subject { get; init; }
    // ... demais campos inalterados, todos init ...

    /// <summary>
    /// null = ainda não verificado (pré-carregamento não chegou nesta mensagem ou pasta acabou
    /// de abrir); true/false = resultado do parse MIME feito por PreloadContentAsync. Diferente
    /// dos demais campos desta classe (todos vêm prontos do índice .dbx em EnumerateHeadersAsync),
    /// este é preenchido depois, em background -- por isso é mutável (set), não init.
    /// </summary>
    public bool? HasAttachment { get; set; }
}
```

## 4. `MessageRenderer` — extração combinada (texto + anexo) num único parse

Hoje `ExtractSearchableTextAsync` faz seu próprio parse MIME e devolve só o texto. `PreloadContentAsync` (seção 5) agora também precisa saber se a mensagem tem anexo — parsear duas vezes (uma pra texto, outra pra anexo) dobraria o custo de I/O/CPU do pré-carregamento à toa. `ExtractSearchableTextAsync` é substituído por uma versão que devolve os dois resultados do mesmo parse:

```csharp
public static async Task<(string SearchableText, bool HasAttachment)> ExtractListMetadataAsync(
    Stream rawMessage, CancellationToken ct = default)
{
    byte[] raw = await ReadAllBytesAsync(rawMessage, ct);
    MimeMessage? message = await TryParseAsync(raw, ct);

    if (message is null)
        return (Encoding.Latin1.GetString(raw), false); // mesmo fallback de texto já usado; anexo desconhecido = false

    string text = message.TextBody is { } plain
        ? plain
        : message.HtmlBody is { } html
            ? WebUtility.HtmlDecode(HtmlTagRegex.Replace(html, " "))
            : string.Empty;

    return (text, message.Attachments.OfType<MimePart>().Any());
}
```

- `message.Attachments.OfType<MimePart>().Any()` usa exatamente o mesmo critério (`ContentDisposition?.IsAttachment`) e o mesmo filtro `OfType<MimePart>` que `ExtractAttachments` já usa pro painel de leitura (`MessageRenderer.cs:206-241`) — mesmas lacunas conhecidas e já documentadas ali (parte `cid:` com `Content-Disposition: attachment` conta como anexo; anexo sem `Content-Disposition` não é detectado), nenhuma heurística nova.
- `ReadAllBytesAsync`/`TryParseAsync` são os helpers já existentes, reaproveitados sem mudança.
- Os 3 testes atuais de `ExtractSearchableTextAsync` (`MessageRendererTests.cs:339-386`) são atualizados para chamar `ExtractListMetadataAsync` e checar `.SearchableText`, mais um teste novo pra `.HasAttachment`.

## 5. `MessageListViewModel` — população em background

`PreloadContentAsync` já abre e parseia cada mensagem da pasta, uma de cada vez, em background, cancelável por troca de pasta (`MessageListViewModel.cs:191-248`). Muda:

```csharp
var stream = await _store.OpenMessageAsync(header.Id, ownerCts.Token);
string body;
bool hasAttachment;
await using (stream)
{
    (body, hasAttachment) = await MessageRenderer.ExtractListMetadataAsync(stream, ownerCts.Token);
}

if (ReferenceEquals(_contentLoadCts, ownerCts))
{
    _bodyByMessage[header.Id] = body;
    header.HasAttachment = hasAttachment;
}
```

- No `catch` que já existe pra corpo ilegível (`MessageListViewModel.cs:221-225`), `header.HasAttachment` fica `null` → tratado como "sem ícone" pela UI (seção 6), igual ao comportamento de busca por Conteúdo pra mensagens que falham.
- **O refresh periódico de `Messages` (a cada `ContentRefreshBatchSize` mensagens, mais um refresh final) passa a rodar sempre**, não só quando `SearchField == Content` — é o que faz o `ListView` reavaliar as bindings de cada linha e o ícone 📎 ir aparecendo progressivamente conforme o parse avança:

```csharp
if (++sinceRefresh >= ContentRefreshBatchSize && ReferenceEquals(_contentLoadCts, ownerCts))
{
    sinceRefresh = 0;
    ApplyFilter(); // antes: só se SearchField == Content; agora sempre, pro refresh da coluna de anexo
}
```

Mesma mudança no refresh final:

```csharp
if (ReferenceEquals(_contentLoadCts, ownerCts))
{
    IsContentLoading = false;
    ApplyFilter(); // idem -- refresh final sempre, não só condicionado ao toggle de Conteúdo
}
```

- Sem loop novo, sem passada extra sobre a pasta — a coluna "pega carona" no I/O que já é pago hoje por `PreloadContentAsync`.

## 6. UI (`MainWindow.xaml`)

Nova coluna, entre "!" (`Health`) e "Origem" (`RecoveredKind`):

```xml
<GridViewColumn Header="📎" Width="24">
    <GridViewColumn.CellTemplate>
        <DataTemplate>
            <TextBlock Text="📎"
                       Visibility="{Binding HasAttachment, Converter={StaticResource HasAttachmentToVisibilityConverter}}"
                       ToolTip="Mensagem tem anexo"/>
        </DataTemplate>
    </GridViewColumn.CellTemplate>
</GridViewColumn>
```

Novo converter `HasAttachmentToVisibilityConverter` (`src/LEC.App/Converters/HasAttachmentToVisibilityConverter.cs`), no mesmo padrão de `HealthToVisibilityConverter`:

```csharp
public sealed class HasAttachmentToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

`value is true` cobre os 3 estados de `bool?` (`true` → Visible; `false`/`null` → Collapsed) sem lançar em `null`, ao contrário do `BooleanToVisibilityConverter` embutido do WPF (que não aceita `bool?`).

Registro do recurso ao lado dos demais converters em `MainWindow.xaml`:
```xml
<conv:HasAttachmentToVisibilityConverter x:Key="HasAttachmentToVisibilityConverter"/>
```

## 7. Testes

Em `MessageListViewModelTests.cs` (usando `FakeMailStore`, mesmo padrão do teste de busca por Conteúdo):
- Mensagem com anexo (bytes MIME reais com uma parte `Content-Disposition: attachment`): após `ContentLoadTask`, `HasAttachment == true`.
- Mensagem sem anexo: após `ContentLoadTask`, `HasAttachment == false`.
- Mensagem cujo corpo falha ao parsear: após `ContentLoadTask`, `HasAttachment` permanece `null`.
- Antes de `ContentLoadTask` completar (checagem síncrona logo após `LoadAsync`), `HasAttachment` é `null` para todas as mensagens.

Em `MessageRendererTests.cs`:
- `ExtractListMetadataAsync` com anexo real → `HasAttachment == true`.
- Sem anexo → `HasAttachment == false`.
- MIME corrompido → `HasAttachment == false`, `SearchableText` cai pro fallback de bytes brutos (mesmo comportamento já coberto pelos 3 testes existentes, agora sobre o novo método).

Sem teste de UI/XAML automatizado (mesma limitação já documentada nos specs anteriores) — checklist manual cobre a aparência real do ícone.

## 8. Checklist manual (GUI)

- Abrir uma pasta com mensagens com e sem anexo: ícone 📎 aparece só nas que têm anexo, coluna "!" continua funcionando normalmente ao lado.
- Pasta grande: observar o ícone aparecendo progressivamente conforme o pré-carregamento avança (não precisa esperar a pasta inteira carregar pra ver os primeiros ícones).
- Passar o mouse sobre o ícone: tooltip "Mensagem tem anexo".
- Trocar de pasta enquanto o pré-carregamento da anterior ainda roda: nenhum ícone da pasta antiga vaza pra pasta nova.
- Pasta com mensagem corrompida/scavenged: mensagem aparece na lista sem ícone de anexo (não trava, não lança erro).
- Buscar/filtrar (Remetente/Assunto/Conteúdo) com a coluna de anexo populada: ícones continuam corretos nas linhas filtradas.
- Ordenar por De/Data com ícones já populados: ícones acompanham a linha certa após reordenar.

## 9. Limitações conhecidas

- Mesma limitação já aceita pra busca por Conteúdo (seção 2 de `2026-08-11-busca-filtro-mensagens-design.md`): pré-carregamento roda pra pasta inteira ao abri-la, então pastas muito grandes podem levar alguns segundos até todos os ícones aparecerem.
- Herda as duas lacunas já documentadas em `MessageRenderer.ExtractAttachments` (comentário em `MessageRenderer.cs:197-205`): parte `cid:` também marcada como `attachment` conta como anexo (falso positivo raro); anexo sem `Content-Disposition` não é detectado (falso negativo raro). Não corrigido nesta feature — mesmo escopo do painel de leitura, que já convive com isso.
