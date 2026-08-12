# Coluna de indicador de anexo na lista de mensagens Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show a 📎 icon in a new, dedicated message-list column when a message has at least one real attachment, populated in the background before the user opens the message.

**Architecture:** Extend the MIME parse `MessageListViewModel.PreloadContentAsync` already does for every message in a folder (today only used to feed Content search) so it also determines attachment presence from the same parse, storing the result directly on the (now partially mutable) `MessageHeader` the row is bound to. The existing periodic `ApplyFilter()` refresh inside that same loop is widened to always run (not just when Content search is active) so the `ListView` re-evaluates row bindings and the icon appears progressively as parsing completes.

**Tech Stack:** .NET/WPF (`LEC.App`), MimeKit for MIME parsing, xUnit for tests.

## Global Constraints

- No new .dbx `Flags` bit heuristics — attachment presence is only ever derived from a real MIME parse (spec §2).
- No second background pass over the folder — attachment detection rides the existing content-preload loop (spec §2, §5).
- No disk persistence — `HasAttachment` lives in memory only, reset every `LoadAsync` (spec §2).
- Sequential message reads only — never parallelize `OpenMessageAsync` calls against the same store (established pattern already in `PreloadContentAsync`).
- No automated UI/XAML tests — this codebase has none; UI changes are verified by `dotnet build` + the manual checklist (spec §7).
- UI strings are in Brazilian Portuguese, matching the rest of `MainWindow.xaml`.

---

## Task 1: `MessageRenderer.ExtractListMetadataAsync`

**Files:**
- Modify: `src/LEC.App/Services/MessageRenderer.cs:119-121` (comment), `:169-195` (replace `ExtractSearchableTextAsync` with `ExtractListMetadataAsync`)
- Test: `tests/LEC.App.Tests/MessageRendererTests.cs:338-384` (update 3 existing tests), plus 2 new tests

**Interfaces:**
- Consumes: nothing new — reuses existing private `ReadAllBytesAsync(Stream, CancellationToken)` and `TryParseAsync(byte[], CancellationToken)` helpers already in `MessageRenderer.cs`, and the existing `HtmlTagRegex` field.
- Produces: `public static Task<(string SearchableText, bool HasAttachment)> MessageRenderer.ExtractListMetadataAsync(Stream rawMessage, CancellationToken ct = default)` — consumed by Task 2.

- [ ] **Step 1: Update the 3 existing tests to call the not-yet-existing `ExtractListMetadataAsync`, and add 2 new tests**

In `tests/LEC.App.Tests/MessageRendererTests.cs`, replace the last 3 tests in the file *and* the class's final closing brace (from `ExtractSearchableTextAsyncReturnsThePlainTextBodyWhenPresent` through the last `}` of the file, i.e. lines 338-384) with — note the replacement block below ends with two closing braces: the last test's, then the class's:

```csharp
    [Fact]
    public async Task ExtractListMetadataAsyncReturnsThePlainTextBodyWhenPresent()
    {
        string raw =
            "From: ana@example.com\r\n" +
            "Subject: Ola\r\n" +
            "\r\n" +
            "Corpo em texto puro";

        var (text, hasAttachment) = await MessageRenderer.ExtractListMetadataAsync(RawMessage(raw));

        Assert.Contains("Corpo em texto puro", text);
        Assert.False(hasAttachment);
    }

    [Fact]
    public async Task ExtractListMetadataAsyncStripsHtmlTagsWhenThereIsNoTextBody()
    {
        string raw =
            "From: ana@example.com\r\n" +
            "Subject: Ola\r\n" +
            "Content-Type: text/html\r\n" +
            "\r\n" +
            "<p>Corpo <b>em HTML</b></p>";

        var (text, hasAttachment) = await MessageRenderer.ExtractListMetadataAsync(RawMessage(raw));

        Assert.Contains("Corpo", text);
        Assert.Contains("em HTML", text);
        Assert.DoesNotContain("<p>", text);
        Assert.DoesNotContain("<b>", text);
        Assert.False(hasAttachment);
    }

    [Fact]
    public async Task ExtractListMetadataAsyncFallsBackToRawBytesWhenTheMessageFailsToParse()
    {
        // Mesmo cabeçalho usado por FallsBackToRawTextInsteadOfThrowingWhenHeadersAreNotRfcCompliant
        // acima -- header com espaço no nome do campo, que o parser estrito do MimeKit rejeita.
        string raw =
            "X-Bad Header: present so MimeKit's strict parser rejects this\r\n" +
            "\r\n" +
            "Corpo da mensagem sem parse";

        var (text, hasAttachment) = await MessageRenderer.ExtractListMetadataAsync(RawMessage(raw));

        Assert.Contains("Corpo da mensagem sem parse", text);
        Assert.False(hasAttachment);
    }

    [Fact]
    public async Task ExtractListMetadataAsyncReportsHasAttachmentTrueWhenTheMessageHasARealAttachment()
    {
        var builder = new BodyBuilder { HtmlBody = "<p>Ola</p>" };
        builder.Attachments.Add("relatorio.txt", Encoding.ASCII.GetBytes("conteudo do anexo"), ContentType.Parse("text/plain"));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ana", "ana@example.com"));
        message.Subject = "Com anexo";
        message.Body = builder.ToMessageBody();

        var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;

        var (_, hasAttachment) = await MessageRenderer.ExtractListMetadataAsync(stream);

        Assert.True(hasAttachment);
    }

    [Fact]
    public async Task ExtractListMetadataAsyncReportsHasAttachmentFalseWhenTheMessageHasOnlyACidEmbeddedImage()
    {
        var builder = new BodyBuilder { HtmlBody = "<p>Ola <img src=\"cid:logo123\"></p>" };
        var embeddedImage = (MimePart)builder.LinkedResources.Add("logo.png", new byte[] { 1, 2, 3 }, ContentType.Parse("image/png"));
        embeddedImage.ContentId = "logo123";

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ana", "ana@example.com"));
        message.Subject = "Sem anexo real";
        message.Body = builder.ToMessageBody();

        var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;

        var (_, hasAttachment) = await MessageRenderer.ExtractListMetadataAsync(stream);

        Assert.False(hasAttachment);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test tests/LEC.App.Tests --filter "FullyQualifiedName~MessageRendererTests"`
Expected: build error — `MessageRenderer.ExtractListMetadataAsync` does not exist yet.

- [ ] **Step 3: Replace `ExtractSearchableTextAsync` with `ExtractListMetadataAsync`**

In `src/LEC.App/Services/MessageRenderer.cs`, change the comment above `ReadAllBytesAsync` (lines 119-121):

From:
```csharp
    // Bufferiza os bytes brutos antes de tentar parsear -- assim, RenderAsync e
    // ExtractSearchableTextAsync conseguem cair pro texto bruto como fallback mesmo quando o
    // parse falha, sem precisar reler o stream (que pode não ser buscável/reutilizável).
```
To:
```csharp
    // Bufferiza os bytes brutos antes de tentar parsear -- assim, RenderAsync e
    // ExtractListMetadataAsync conseguem cair pro texto bruto como fallback mesmo quando o
    // parse falha, sem precisar reler o stream (que pode não ser buscável/reutilizável).
```

Then replace the doc comment and method (lines 175-195):

From:
```csharp
    /// <summary>
    /// Extrai um texto pesquisável do corpo da mensagem, pra uso da busca por "Conteúdo" em
    /// MessageListViewModel -- nunca lança: mensagem que falha ao parsear cai pro mesmo
    /// fallback de bytes brutos que RenderAsync usa pra exibição.
    /// </summary>
    public static async Task<string> ExtractSearchableTextAsync(Stream rawMessage, CancellationToken ct = default)
    {
        byte[] raw = await ReadAllBytesAsync(rawMessage, ct);
        MimeMessage? message = await TryParseAsync(raw, ct);

        if (message is null)
            return Encoding.Latin1.GetString(raw);

        if (message.TextBody is { } text)
            return text;

        if (message.HtmlBody is { } html)
            return WebUtility.HtmlDecode(HtmlTagRegex.Replace(html, " "));

        return string.Empty;
    }
```
To:
```csharp
    /// <summary>
    /// Extrai, num único parse MIME, o texto pesquisável do corpo (pra busca por "Conteúdo") e
    /// se a mensagem tem pelo menos um anexo real (pra coluna de anexo da lista) --
    /// MessageListViewModel.PreloadContentAsync usa os dois de uma vez, pra não parsear a
    /// mesma mensagem duas vezes. Nunca lança: mensagem que falha ao parsear cai pro mesmo
    /// fallback de bytes brutos que RenderAsync usa pra exibição, com HasAttachment = false
    /// (não dá pra saber -- mesma política de "trata como não sabido" do resto do app).
    /// </summary>
    public static async Task<(string SearchableText, bool HasAttachment)> ExtractListMetadataAsync(
        Stream rawMessage, CancellationToken ct = default)
    {
        byte[] raw = await ReadAllBytesAsync(rawMessage, ct);
        MimeMessage? message = await TryParseAsync(raw, ct);

        if (message is null)
            return (Encoding.Latin1.GetString(raw), false);

        string text = message.TextBody is { } plain
            ? plain
            : message.HtmlBody is { } html
                ? WebUtility.HtmlDecode(HtmlTagRegex.Replace(html, " "))
                : string.Empty;

        // Mesmo critério (ContentDisposition?.IsAttachment via MimeMessage.Attachments) e mesmo
        // filtro OfType<MimePart> que ExtractAttachments já usa pro painel de leitura, logo
        // abaixo neste arquivo -- mesmas lacunas conhecidas documentadas ali, nenhuma heurística nova.
        bool hasAttachment = message.Attachments.OfType<MimePart>().Any();

        return (text, hasAttachment);
    }
```

`MessageListViewModel.PreloadContentAsync` is the only other caller of the old method — it must be patched in this same step or the solution won't build. In `src/LEC.App/ViewModels/MessageListViewModel.cs`, change:

```csharp
                        body = await MessageRenderer.ExtractSearchableTextAsync(stream, ownerCts.Token);
```
To:
```csharp
                        (body, _) = await MessageRenderer.ExtractListMetadataAsync(stream, ownerCts.Token);
```

(This discards the attachment info for now — Task 2 revisits this exact line to actually store it on `header.HasAttachment`. Patching the call site here only keeps the build green between tasks.)

- [ ] **Step 4: Run the full test suite to verify everything still builds and passes**

Run: `dotnet test`
Expected: PASS. (Full suite, not just `MessageRendererTests` — this task also touches `MessageListViewModel.cs`, so this is what confirms the whole solution still builds.)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/Services/MessageRenderer.cs tests/LEC.App.Tests/MessageRendererTests.cs
git commit -m "Replace ExtractSearchableTextAsync with ExtractListMetadataAsync to report attachment presence from the same parse"
```

---

## Task 2: `MessageHeader.HasAttachment` + `MessageListViewModel` background population

**Files:**
- Modify: `src/LEC.MailStore/MessageHeader.cs` (add `HasAttachment` property)
- Modify: `src/LEC.App/ViewModels/MessageListViewModel.cs:174` (comment), `:191-248` (`PreloadContentAsync`)
- Test: `tests/LEC.App.Tests/MessageListViewModelTests.cs` (4 new tests)

**Interfaces:**
- Consumes (from Task 1): `MessageRenderer.ExtractListMetadataAsync(Stream, CancellationToken) -> Task<(string SearchableText, bool HasAttachment)>`.
- Produces: `MessageHeader.HasAttachment` (`bool?`, mutable `get; set;`) — consumed by Task 3's XAML binding. `null` until the background preload reaches that message (or it fails to parse); `true`/`false` once known.

- [ ] **Step 1: Write the 4 failing tests**

In `tests/LEC.App.Tests/MessageListViewModelTests.cs`, add `using MimeKit;` to the usings at the top of the file (alongside the existing `using System.Text;`), then add these 4 tests at the end of the class, just before the final closing `}`:

```csharp
    [Fact]
    public async Task PopulatesHasAttachmentTrueForAMessageWithARealAttachment()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var header = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Com anexo" };

        var builder = new BodyBuilder { HtmlBody = "<p>Ola</p>" };
        builder.Attachments.Add("relatorio.txt", Encoding.ASCII.GetBytes("conteudo"), ContentType.Parse("text/plain"));
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ana", "ana@example.com"));
        message.Body = builder.ToMessageBody();
        var rawStream = new MemoryStream();
        message.WriteTo(rawStream);

        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { header } },
            rawByRef: new() { [header.Id] = rawStream.ToArray() });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);
        await vm.ContentLoadTask;

        Assert.True(vm.Messages.Single().HasAttachment);
    }

    [Fact]
    public async Task PopulatesHasAttachmentFalseForAMessageWithoutAnAttachment()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var header = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Sem anexo" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { header } },
            rawByRef: new() { [header.Id] = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: X\r\n\r\nCorpo sem anexo") });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);
        await vm.ContentLoadTask;

        Assert.False(vm.Messages.Single().HasAttachment);
    }

    [Fact]
    public async Task HasAttachmentStaysNullForAMessageWhoseBodyFailsToLoad()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var header = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Corrompida" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { header } });
        // Nenhum rawByRef registrado para este MessageRef -- OpenMessageAsync do FakeMailStore
        // lança InvalidOperationException, simulando um corpo ilegível/corrompido.

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);
        await vm.ContentLoadTask;

        Assert.Null(vm.Messages.Single().HasAttachment);
    }

    [Fact]
    public async Task HasAttachmentIsNullForEveryMessageBeforePreloadCompletes()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var header = new MessageHeader { Id = new MessageRef(1, 1), Subject = "A" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { header } },
            rawByRef: new() { [header.Id] = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: X\r\n\r\nCorpo") });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder); // não espera ContentLoadTask de propósito

        Assert.Null(vm.Messages.Single().HasAttachment);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter "FullyQualifiedName~MessageListViewModelTests"`
Expected: build error or FAIL — `MessageHeader.HasAttachment` does not exist yet.

- [ ] **Step 3: Add `HasAttachment` to `MessageHeader`**

In `src/LEC.MailStore/MessageHeader.cs`, replace the whole file with:

```csharp
using LEC.DbxFormat;

namespace LEC.MailStore;

public sealed class MessageHeader
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

    /// <summary>
    /// null = ainda não verificado (o pré-carregamento de MessageListViewModel não chegou nesta
    /// mensagem ainda, ou a mensagem falhou ao parsear); true/false = resultado real de um parse
    /// MIME. Diferente dos demais campos desta classe (todos vêm prontos do índice .dbx em
    /// EnumerateHeadersAsync), este é preenchido depois, em background -- por isso é mutável
    /// (set), não init.
    /// </summary>
    public bool? HasAttachment { get; set; }
}
```

- [ ] **Step 4: Update `PreloadContentAsync` to populate `HasAttachment` and always refresh**

In `src/LEC.App/ViewModels/MessageListViewModel.cs`, the doc comment mentioning the old method name (around line 174) was already updated to say `ExtractListMetadataAsync` in Task 1 — nothing more to do there.

Replace the body of the `foreach` loop's `try` block plus the batch-refresh `if` right after it. After Task 1, it currently reads:

```csharp
                try
                {
                    var stream = await _store.OpenMessageAsync(header.Id, ownerCts.Token);
                    string body;
                    await using (stream)
                    {
                        (body, _) = await MessageRenderer.ExtractListMetadataAsync(stream, ownerCts.Token);
                    }

                    if (ReferenceEquals(_contentLoadCts, ownerCts))
                        _bodyByMessage[header.Id] = body;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Corpo ilegível (MIME corrompido/truncado, ou I/O falhou) -- degrada:
                    // mensagem só não aparece em buscas por Conteúdo, resto do app não é afetado.
                }

                await YieldToUiAsync(); // deixa o WPF repintar/processar input entre cada mensagem

                if (++sinceRefresh >= ContentRefreshBatchSize && ReferenceEquals(_contentLoadCts, ownerCts))
                {
                    sinceRefresh = 0;
                    if (SearchField == MessageSearchField.Content)
                        ApplyFilter();
                }
```

With:

```csharp
                try
                {
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
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Corpo ilegível (MIME corrompido/truncado, ou I/O falhou) -- degrada:
                    // mensagem só não aparece em buscas por Conteúdo nem ganha ícone de anexo,
                    // resto do app não é afetado. HasAttachment fica null (nunca setado).
                }

                await YieldToUiAsync(); // deixa o WPF repintar/processar input entre cada mensagem

                if (++sinceRefresh >= ContentRefreshBatchSize && ReferenceEquals(_contentLoadCts, ownerCts))
                {
                    sinceRefresh = 0;
                    ApplyFilter(); // sempre -- não só quando SearchField == Content -- pra refletir
                                    // header.HasAttachment (coluna de anexo) progressivamente, não só
                                    // resultados de busca por Conteúdo
                }
```

Finally, replace the final refresh block:

```csharp
        if (ReferenceEquals(_contentLoadCts, ownerCts))
        {
            IsContentLoading = false;
            if (SearchField == MessageSearchField.Content)
                ApplyFilter(); // garante um refresh final mesmo com menos de ContentRefreshBatchSize mensagens
        }
```

With:

```csharp
        if (ReferenceEquals(_contentLoadCts, ownerCts))
        {
            IsContentLoading = false;
            ApplyFilter(); // idem -- refresh final sempre, garante o último lote (< ContentRefreshBatchSize)
                            // de HasAttachment/corpo pesquisável apareça mesmo em pastas pequenas
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter "FullyQualifiedName~MessageListViewModelTests"`
Expected: PASS (all tests in the file, including the 4 new ones).

- [ ] **Step 6: Run the full test suite to catch any regression from the always-refresh change**

Run: `dotnet test`
Expected: PASS. (The always-`ApplyFilter()` change touches every existing Content-search test's code path too — this confirms nothing broke.)

- [ ] **Step 7: Commit**

```bash
git add src/LEC.MailStore/MessageHeader.cs src/LEC.App/ViewModels/MessageListViewModel.cs tests/LEC.App.Tests/MessageListViewModelTests.cs
git commit -m "Populate MessageHeader.HasAttachment from the existing content-preload pass"
```

---

## Task 3: UI — attachment column and converter

**Files:**
- Create: `src/LEC.App/Converters/HasAttachmentToVisibilityConverter.cs`
- Modify: `src/LEC.App/MainWindow.xaml:12` (register converter resource), `:187-195` (new `GridViewColumn`)

**Interfaces:**
- Consumes (from Task 2): `MessageHeader.HasAttachment` (`bool?`).
- Produces: nothing consumed by later tasks — last task of the plan.

- [ ] **Step 1: Create the converter**

Create `src/LEC.App/Converters/HasAttachmentToVisibilityConverter.cs`:

```csharp
// src/LEC.App/Converters/HasAttachmentToVisibilityConverter.cs
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LEC.App.Converters;

// value is true cobre os 3 estados de bool? (true -> Visible; false/null -> Collapsed) sem
// lançar em null, ao contrário do BooleanToVisibilityConverter embutido do WPF (que não
// aceita bool?).
public sealed class HasAttachmentToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 2: Register the converter and add the column**

In `src/LEC.App/MainWindow.xaml`, add the resource next to the other converters (line 12):

From:
```xml
        <conv:HealthToVisibilityConverter x:Key="HealthToVisibilityConverter"/>
```
To:
```xml
        <conv:HealthToVisibilityConverter x:Key="HealthToVisibilityConverter"/>
        <conv:HasAttachmentToVisibilityConverter x:Key="HasAttachmentToVisibilityConverter"/>
```

Then add the new column right after the "!" (`Health`) column and before "Origem" (lines 187-195):

From:
```xml
                                <GridViewColumn Header="!" Width="24">
                                    <GridViewColumn.CellTemplate>
                                        <DataTemplate>
                                            <TextBlock Text="{Binding Health, StringFormat={}{0}}"
                                                       Visibility="{Binding Health, Converter={StaticResource HealthToVisibilityConverter}}"
                                                       Foreground="DarkOrange" FontWeight="Bold" ToolTip="Mensagem com atributo fora do intervalo esperado — dado parcialmente recuperado."/>
                                        </DataTemplate>
                                    </GridViewColumn.CellTemplate>
                                </GridViewColumn>
                                <GridViewColumn Header="Origem" Width="80">
```
To:
```xml
                                <GridViewColumn Header="!" Width="24">
                                    <GridViewColumn.CellTemplate>
                                        <DataTemplate>
                                            <TextBlock Text="{Binding Health, StringFormat={}{0}}"
                                                       Visibility="{Binding Health, Converter={StaticResource HealthToVisibilityConverter}}"
                                                       Foreground="DarkOrange" FontWeight="Bold" ToolTip="Mensagem com atributo fora do intervalo esperado — dado parcialmente recuperado."/>
                                        </DataTemplate>
                                    </GridViewColumn.CellTemplate>
                                </GridViewColumn>
                                <GridViewColumn Header="📎" Width="24">
                                    <GridViewColumn.CellTemplate>
                                        <DataTemplate>
                                            <TextBlock Text="📎"
                                                       Visibility="{Binding HasAttachment, Converter={StaticResource HasAttachmentToVisibilityConverter}}"
                                                       ToolTip="Mensagem tem anexo"/>
                                        </DataTemplate>
                                    </GridViewColumn.CellTemplate>
                                </GridViewColumn>
                                <GridViewColumn Header="Origem" Width="80">
```

- [ ] **Step 3: Build**

Run: `dotnet build src/LEC.App`
Expected: build succeeds with no errors.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test`
Expected: PASS. (This task adds no automated tests — WPF column/converter rendering has no test framework in this project today; the underlying `HasAttachment` logic is already covered by Task 2's tests.)

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/Converters/HasAttachmentToVisibilityConverter.cs src/LEC.App/MainWindow.xaml
git commit -m "Add attachment indicator column to the message list"
```

- [ ] **Step 6: Manual checklist (GUI) — run the app and verify**

- Abrir uma pasta com mensagens com e sem anexo: ícone 📎 aparece só nas que têm anexo, coluna "!" continua funcionando normalmente ao lado.
- Pasta grande: observar o ícone aparecendo progressivamente conforme o pré-carregamento avança (não precisa esperar a pasta inteira carregar pra ver os primeiros ícones).
- Passar o mouse sobre o ícone: tooltip "Mensagem tem anexo".
- Trocar de pasta enquanto o pré-carregamento da anterior ainda roda: nenhum ícone da pasta antiga vaza pra pasta nova.
- Pasta com mensagem corrompida/scavenged: mensagem aparece na lista sem ícone de anexo (não trava, não lança erro).
- Buscar/filtrar (Remetente/Assunto/Conteúdo) com a coluna de anexo populada: ícones continuam corretos nas linhas filtradas.
- Ordenar por De/Data com ícones já populados: ícones acompanham a linha certa após reordenar.
