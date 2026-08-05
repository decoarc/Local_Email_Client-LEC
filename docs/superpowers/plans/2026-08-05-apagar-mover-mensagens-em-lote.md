# Apagar/mover mensagens em lote — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user Ctrl/Shift-select multiple messages in the message list and apply "Apagar" or "Mover para..." to all of them in one action, reusing the already-shipped per-message operations — no motor (`IMailStore`/`DbxStore`) changes at all.

**Architecture:** `MainWindowViewModel` gains two thin looping wrappers, `DeleteMessagesAsync`/`MoveMessagesAsync`, each calling the existing single-message `DeleteMessageAsync`/`MoveMessageAsync` once per message and stopping at the first failure (each message is its own atomic, independent operation, so a partial batch needs no rollback — whatever succeeded stays succeeded). `MessageListView` switches from the implicit default `SelectionMode="Single"` to `"Extended"` (native Ctrl/Shift multi-select). The context-menu handlers and the Delete-key handler are rewritten to resolve their target as "the current selection, or the clicked row if nothing is selected yet" instead of always reading the clicked row's own `DataContext`. The reading pane clears whenever the selection isn't exactly one message.

**Tech Stack:** .NET 9, C#, WPF, xUnit.

## Global Constraints

- No new `IMailStore`/`DbxStore` member — this plan only adds two `MainWindowViewModel` methods that loop the already-shipped `DeleteMessageAsync`/`MoveMessageAsync`.
- A batch stops at the first failure. Messages already processed before the failure stay processed (no rollback attempted, since each per-message operation is already a complete, independent unit). The exception surfaced to the UI states how many messages succeeded before the failure, e.g. `"3 de 5 mensagens apagadas antes de falhar: {mensagem original}"`.
- Right-clicking a row that is NOT part of the current selection does **not** change the selection — the context-menu action applies to whatever was already selected. The only fallback is when nothing is selected at all (e.g. the very first right-click in a freshly loaded list), in which case the clicked row becomes a 1-item target.
- The reading pane only renders content when exactly one message is selected; 0 or 2+ selected clears it.
- All of this applies to messages only — folders are untouched by this plan.
- Reference: `docs/superpowers/specs/2026-08-05-apagar-mover-mensagens-em-lote-design.md` (full design this plan implements).

---

## File Structure

```
src/LEC.App/ViewModels/
  MainWindowViewModel.cs         MODIFIED — Task 1: +DeleteMessagesAsync, +MoveMessagesAsync

src/LEC.App/
  MainWindow.xaml                MODIFIED — Task 2: MessageListView SelectionMode="Extended"
  MainWindow.xaml.cs             MODIFIED — Task 2: +ResolveTargetMessages, rewrites MessageListView_SelectionChanged/MoveMessage_Click/DeleteMessage_Click/MessageListView_KeyDown/ConfirmAndDeleteAsync

tests/LEC.App.Tests/
  FakeMailStore.cs               MODIFIED — Task 1: +MessageRefToFailOn (DeleteMessageAsync), +SubjectToFailOn (AddMessageAsync) — lets a batch test control exactly which message in a sequence fails
  MainWindowViewModelTests.cs    MODIFIED — Task 1: +6 DeleteMessagesAsync/MoveMessagesAsync tests
```

---

### Task 1: `MainWindowViewModel.DeleteMessagesAsync`/`MoveMessagesAsync`

**Files:**
- Modify: `src/LEC.App/ViewModels/MainWindowViewModel.cs`
- Modify: `tests/LEC.App.Tests/FakeMailStore.cs`
- Modify: `tests/LEC.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.DeleteMessageAsync(ProfileViewModel, MessageHeader, CancellationToken)`/`MoveMessageAsync(ProfileViewModel, MessageHeader, ProfileViewModel, int, CancellationToken)` (both existing, unchanged).
- Produces: `MainWindowViewModel.DeleteMessagesAsync(ProfileViewModel profile, IReadOnlyList<MessageHeader> messages, CancellationToken ct = default) : Task`, `MainWindowViewModel.MoveMessagesAsync(ProfileViewModel sourceProfile, IReadOnlyList<MessageHeader> messages, ProfileViewModel destinationProfile, int destinationFolderId, CancellationToken ct = default) : Task`. Consumed by Task 2's `MainWindow.xaml.cs` handlers.

- [ ] **Step 1: Add failure-injection hooks to `FakeMailStore`**

In `tests/LEC.App.Tests/FakeMailStore.cs`, replace:

```csharp
    public HashSet<MessageRef> DeletedMessageRefs { get; } = new();

    public Task DeleteMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        DeletedMessageRefs.Add(id);
        return Task.CompletedTask;
    }
```

with:

```csharp
    public HashSet<MessageRef> DeletedMessageRefs { get; } = new();

    // Lets a batch-delete test control exactly which message in a sequence fails (a blanket
    // "always throw" property wouldn't let any earlier message in the same batch succeed
    // first, which is exactly the scenario DeleteMessagesAsync's stop-on-failure behavior
    // needs to exercise).
    public MessageRef? MessageRefToFailOn { get; set; }

    public Task DeleteMessageAsync(MessageRef id, CancellationToken ct = default)
    {
        if (MessageRefToFailOn == id)
            throw new InvalidOperationException("falha simulada de apagar mensagem");

        DeletedMessageRefs.Add(id);
        return Task.CompletedTask;
    }
```

Then replace:

```csharp
    public Exception? ExceptionToThrowOnAddMessage { get; set; }
    public List<(int FolderId, byte[] RawMessage, string? Subject, string? SenderName, string? SenderAddress, string? MessageId, DateTime? DateUtc)> AddedMessages { get; } = new();

    public Task AddMessageAsync(int folderId, byte[] rawMessage, string? subject, string? senderName,
        string? senderAddress, string? messageId, DateTime? dateUtc, CancellationToken ct = default)
    {
        if (ExceptionToThrowOnAddMessage is { } ex)
            throw ex;

        AddedMessages.Add((folderId, rawMessage, subject, senderName, senderAddress, messageId, dateUtc));
        return Task.CompletedTask;
    }
```

with:

```csharp
    public Exception? ExceptionToThrowOnAddMessage { get; set; }
    // Same reasoning as MessageRefToFailOn above, but AddMessageAsync has no MessageRef to
    // key on (the destination call only carries the header fields) -- Subject is unique
    // enough in a controlled test's message set to identify which one should fail.
    public string? SubjectToFailOn { get; set; }
    public List<(int FolderId, byte[] RawMessage, string? Subject, string? SenderName, string? SenderAddress, string? MessageId, DateTime? DateUtc)> AddedMessages { get; } = new();

    public Task AddMessageAsync(int folderId, byte[] rawMessage, string? subject, string? senderName,
        string? senderAddress, string? messageId, DateTime? dateUtc, CancellationToken ct = default)
    {
        if (ExceptionToThrowOnAddMessage is { } ex)
            throw ex;
        if (subject == SubjectToFailOn)
            throw new InvalidOperationException("falha simulada de mover mensagem");

        AddedMessages.Add((folderId, rawMessage, subject, senderName, senderAddress, messageId, dateUtc));
        return Task.CompletedTask;
    }
```

- [ ] **Step 2: Write the failing tests**

In `tests/LEC.App.Tests/MainWindowViewModelTests.cs`, add these 6 tests right after the existing `DeleteMessageAsyncCallsTheStoresDeleteMessageAsync` test, before the class's closing `}`:

```csharp
    [Fact]
    public async Task DeleteMessagesAsyncDeletesEachMessageInTheGivenOrder()
    {
        var folder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfis\Perfil", store);

        var ref1 = new MessageRef(folder.Id, 1);
        var ref2 = new MessageRef(folder.Id, 2);
        var ref3 = new MessageRef(folder.Id, 3);
        var messages = new List<MessageHeader>
        {
            new() { Id = ref1, Subject = "Um" },
            new() { Id = ref2, Subject = "Dois" },
            new() { Id = ref3, Subject = "Tres" },
        };

        var viewModel = new MainWindowViewModel();

        await viewModel.DeleteMessagesAsync(profile, messages);

        Assert.Contains(ref1, store.DeletedMessageRefs);
        Assert.Contains(ref2, store.DeletedMessageRefs);
        Assert.Contains(ref3, store.DeletedMessageRefs);
    }

    [Fact]
    public async Task DeleteMessagesAsyncStopsAtTheFirstFailureLeavingEarlierSuccessesInPlace()
    {
        var folder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfis\Perfil", store);

        var ref1 = new MessageRef(folder.Id, 1);
        var ref2 = new MessageRef(folder.Id, 2);
        var ref3 = new MessageRef(folder.Id, 3);
        store.MessageRefToFailOn = ref2;
        var messages = new List<MessageHeader>
        {
            new() { Id = ref1, Subject = "Um" },
            new() { Id = ref2, Subject = "Dois" },
            new() { Id = ref3, Subject = "Tres" },
        };

        var viewModel = new MainWindowViewModel();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.DeleteMessagesAsync(profile, messages));

        Assert.Contains("1 de 3", ex.Message);
        Assert.Contains(ref1, store.DeletedMessageRefs); // succeeded before the failure
        Assert.DoesNotContain(ref2, store.DeletedMessageRefs); // this one failed
        Assert.DoesNotContain(ref3, store.DeletedMessageRefs); // never attempted
    }

    [Fact]
    public async Task DeleteMessagesAsyncWithASingleMessageBehavesLikeDeleteMessageAsync()
    {
        var folder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfis\Perfil", store);

        var messageRef = new MessageRef(folder.Id, 1);
        var messages = new List<MessageHeader> { new() { Id = messageRef, Subject = "Um" } };

        var viewModel = new MainWindowViewModel();

        await viewModel.DeleteMessagesAsync(profile, messages);

        Assert.Contains(messageRef, store.DeletedMessageRefs);
    }

    [Fact]
    public async Task MoveMessagesAsyncMovesEachMessageToTheSameDestination()
    {
        var sourceFolder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var ref1 = new MessageRef(sourceFolder.Id, 1);
        var ref2 = new MessageRef(sourceFolder.Id, 2);
        var sourceStore = new FakeMailStore(
            folders: new[] { sourceFolder },
            rawByRef: new Dictionary<MessageRef, byte[]>
            {
                [ref1] = "um"u8.ToArray(),
                [ref2] = "dois"u8.ToArray(),
            });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destFolder = new StoreFolder { Id = 10, Name = "Destino", IsLoadable = true, HasBackingFile = true };
        var destStore = new FakeMailStore(folders: new[] { destFolder });
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var messages = new List<MessageHeader>
        {
            new() { Id = ref1, Subject = "Um" },
            new() { Id = ref2, Subject = "Dois" },
        };
        var viewModel = new MainWindowViewModel();

        await viewModel.MoveMessagesAsync(sourceProfile, messages, destProfile, destFolder.Id);

        Assert.Equal(2, destStore.AddedMessages.Count);
        Assert.All(destStore.AddedMessages, m => Assert.Equal(destFolder.Id, m.FolderId));
        Assert.Contains(ref1, sourceStore.HiddenMessageRefs);
        Assert.Contains(ref2, sourceStore.HiddenMessageRefs);
    }

    [Fact]
    public async Task MoveMessagesAsyncStopsAtTheFirstFailureAndDoesNotHideTheRemainingMessages()
    {
        var sourceFolder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var ref1 = new MessageRef(sourceFolder.Id, 1);
        var ref2 = new MessageRef(sourceFolder.Id, 2);
        var ref3 = new MessageRef(sourceFolder.Id, 3);
        var sourceStore = new FakeMailStore(
            folders: new[] { sourceFolder },
            rawByRef: new Dictionary<MessageRef, byte[]>
            {
                [ref1] = "um"u8.ToArray(),
                [ref2] = "dois"u8.ToArray(),
                [ref3] = "tres"u8.ToArray(),
            });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destFolder = new StoreFolder { Id = 10, Name = "Destino", IsLoadable = true, HasBackingFile = true };
        var destStore = new FakeMailStore(folders: new[] { destFolder }) { SubjectToFailOn = "Dois" };
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var messages = new List<MessageHeader>
        {
            new() { Id = ref1, Subject = "Um" },
            new() { Id = ref2, Subject = "Dois" },
            new() { Id = ref3, Subject = "Tres" },
        };
        var viewModel = new MainWindowViewModel();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.MoveMessagesAsync(sourceProfile, messages, destProfile, destFolder.Id));

        Assert.Contains("1 de 3", ex.Message);
        Assert.Contains(ref1, sourceStore.HiddenMessageRefs); // succeeded before the failure
        Assert.DoesNotContain(ref2, sourceStore.HiddenMessageRefs); // this one failed to copy, so never hidden
        Assert.DoesNotContain(ref3, sourceStore.HiddenMessageRefs); // never attempted
        Assert.Single(destStore.AddedMessages); // only "Um" made it to the destination
    }

    [Fact]
    public async Task MoveMessagesAsyncWithASingleMessageBehavesLikeMoveMessageAsync()
    {
        var sourceFolder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var messageRef = new MessageRef(sourceFolder.Id, 1);
        var sourceStore = new FakeMailStore(
            folders: new[] { sourceFolder },
            rawByRef: new Dictionary<MessageRef, byte[]> { [messageRef] = "um"u8.ToArray() });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destFolder = new StoreFolder { Id = 10, Name = "Destino", IsLoadable = true, HasBackingFile = true };
        var destStore = new FakeMailStore(folders: new[] { destFolder });
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var messages = new List<MessageHeader> { new() { Id = messageRef, Subject = "Um" } };
        var viewModel = new MainWindowViewModel();

        await viewModel.MoveMessagesAsync(sourceProfile, messages, destProfile, destFolder.Id);

        Assert.Single(destStore.AddedMessages);
        Assert.Contains(messageRef, sourceStore.HiddenMessageRefs);
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests --filter "DeleteMessagesAsyncDeletesEachMessageInTheGivenOrder|DeleteMessagesAsyncStopsAtTheFirstFailureLeavingEarlierSuccessesInPlace|DeleteMessagesAsyncWithASingleMessageBehavesLikeDeleteMessageAsync|MoveMessagesAsyncMovesEachMessageToTheSameDestination|MoveMessagesAsyncStopsAtTheFirstFailureAndDoesNotHideTheRemainingMessages|MoveMessagesAsyncWithASingleMessageBehavesLikeMoveMessageAsync"`
Expected: FAIL to compile — `MainWindowViewModel.DeleteMessagesAsync`/`MoveMessagesAsync` don't exist yet.

- [ ] **Step 4: Implement `DeleteMessagesAsync`/`MoveMessagesAsync`**

In `src/LEC.App/ViewModels/MainWindowViewModel.cs`, add this right after the existing `DeleteMessageAsync` method, before `MoveFolderAsync`'s doc comment:

```csharp
    /// <summary>
    /// Deletes multiple messages, one at a time via the existing DeleteMessageAsync,
    /// stopping at the first failure. Messages already deleted before the failure stay
    /// deleted -- each message is its own atomic, independent operation, so there's
    /// nothing to roll back.
    /// </summary>
    /// <exception cref="Exception">
    /// Whatever DeleteMessageAsync throws for the failing message, rewrapped with a prefix
    /// stating how many messages succeeded before it, e.g. "3 de 5 mensagens apagadas
    /// antes de falhar: {mensagem original}".
    /// </exception>
    public async Task DeleteMessagesAsync(ProfileViewModel profile, IReadOnlyList<MessageHeader> messages, CancellationToken ct = default)
    {
        int succeeded = 0;
        foreach (var message in messages)
        {
            try
            {
                await DeleteMessageAsync(profile, message, ct);
                succeeded++;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{succeeded} de {messages.Count} mensagens apagadas antes de falhar: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Moves multiple messages (all from the same source folder -- the message list only
    /// ever shows one folder at a time) into a single chosen destination, one at a time via
    /// the existing MoveMessageAsync, stopping at the first failure. Same "nothing to roll
    /// back" reasoning as DeleteMessagesAsync.
    /// </summary>
    /// <exception cref="Exception">
    /// Same rewrapping as DeleteMessagesAsync, e.g. "3 de 5 mensagens movidas antes de
    /// falhar: {mensagem original}".
    /// </exception>
    public async Task MoveMessagesAsync(ProfileViewModel sourceProfile, IReadOnlyList<MessageHeader> messages,
        ProfileViewModel destinationProfile, int destinationFolderId, CancellationToken ct = default)
    {
        int succeeded = 0;
        foreach (var message in messages)
        {
            try
            {
                await MoveMessageAsync(sourceProfile, message, destinationProfile, destinationFolderId, ct);
                succeeded++;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{succeeded} de {messages.Count} mensagens movidas antes de falhar: {ex.Message}", ex);
            }
        }
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests --filter "DeleteMessagesAsyncDeletesEachMessageInTheGivenOrder|DeleteMessagesAsyncStopsAtTheFirstFailureLeavingEarlierSuccessesInPlace|DeleteMessagesAsyncWithASingleMessageBehavesLikeDeleteMessageAsync|MoveMessagesAsyncMovesEachMessageToTheSameDestination|MoveMessagesAsyncStopsAtTheFirstFailureAndDoesNotHideTheRemainingMessages|MoveMessagesAsyncWithASingleMessageBehavesLikeMoveMessageAsync"`
Expected: PASS — all 6 new tests.

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 6: Commit**

```bash
git add src/LEC.App/ViewModels/MainWindowViewModel.cs tests/LEC.App.Tests/FakeMailStore.cs tests/LEC.App.Tests/MainWindowViewModelTests.cs
git commit -m "Add MainWindowViewModel.DeleteMessagesAsync/MoveMessagesAsync for batch operations"
```

---

### Task 2: Multi-select UI wiring

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml`
- Modify: `src/LEC.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.DeleteMessagesAsync`/`MoveMessagesAsync` (Task 1).
- Produces: user-visible Ctrl/Shift multi-select on the message list, with "Apagar"/"Mover para..."/Delete-key all operating on the current selection.

No dedicated automated test — same established precedent as every other `MainWindow.xaml.cs` handler. This task ends with a manual verification checklist.

- [ ] **Step 1: Enable multi-select in the XAML**

In `src/LEC.App/MainWindow.xaml`, replace:

```xml
                    <ListView x:Name="MessageListView"
                              ItemsSource="{Binding MessageList.Messages}"
                              SelectionChanged="MessageListView_SelectionChanged"
                              KeyDown="MessageListView_KeyDown"
                              VirtualizingPanel.IsVirtualizing="True"
                              VirtualizingPanel.VirtualizationMode="Recycling">
```

with:

```xml
                    <ListView x:Name="MessageListView"
                              ItemsSource="{Binding MessageList.Messages}"
                              SelectionMode="Extended"
                              SelectionChanged="MessageListView_SelectionChanged"
                              KeyDown="MessageListView_KeyDown"
                              VirtualizingPanel.IsVirtualizing="True"
                              VirtualizingPanel.VirtualizationMode="Recycling">
```

- [ ] **Step 2: Rewrite the message-list handlers**

In `src/LEC.App/MainWindow.xaml.cs`, replace the entire block from `MessageListView_SelectionChanged` through the end of `ConfirmAndDeleteAsync` (i.e. replace `MessageListView_SelectionChanged`, `MoveMessage_Click`, `DeleteMessage_Click`, `MessageListView_KeyDown`, and `ConfirmAndDeleteAsync` together — five consecutive methods):

```csharp
    private async void MessageListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (MessageListView.SelectedItems.Count == 1 && MessageListView.SelectedItem is MessageHeader header)
            {
                if (_imageBlocker is not null)
                {
                    _imageBlocker.BlockImages = true;
                    ImagesBlockedBar.Visibility = Visibility.Visible;
                }

                await _viewModel.SelectMessageAsync(header);
                if (_viewModel.MessageReader?.Current is { } rendered)
                    NavigateToRenderedMessage(rendered.Html);
            }
            else
            {
                // 0 selected, or a batch selection (2+) -- no single message to show.
                NavigateToRenderedMessage(string.Empty);
                ImagesBlockedBar.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível exibir esta mensagem: {ex.Message}";
        }
    }

    // Resolves which messages a context-menu action or the Delete key should act on: the
    // current selection if there is one -- even if the row that triggered the menu isn't
    // part of it, right-clicking off-selection intentionally leaves the existing selection
    // alone -- or the clicked row itself as a fallback for the very first right-click in a
    // freshly loaded list, before anything has ever been selected.
    private List<MessageHeader> ResolveTargetMessages(object sender)
    {
        if (MessageListView.SelectedItems.Count > 0)
            return MessageListView.SelectedItems.Cast<MessageHeader>().ToList();

        if ((sender as MenuItem)?.DataContext is MessageHeader header)
            return new List<MessageHeader> { header };

        return new List<MessageHeader>();
    }

    private async void MoveMessage_Click(object sender, RoutedEventArgs e)
    {
        var messages = ResolveTargetMessages(sender);
        if (messages.Count == 0)
            return;
        if (_selectedProfile is not { } sourceProfile)
            return;

        try
        {
            var dialog = new MoveMessageDialog(_viewModel.OpenProfiles, sourceProfile, messages[0].Id.FolderId) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedProfile is not { } destinationProfile || dialog.SelectedFolderId is not { } destinationFolderId)
                return;

            await _viewModel.MoveMessagesAsync(sourceProfile, messages, destinationProfile, destinationFolderId);

            // The moved messages are now hidden from the folder currently on screen (its
            // source) -- reload that folder's list so they disappear, and clear the reading
            // pane in case it was showing one of them (same visual reset CloseProfile_Click
            // already uses for "nothing selected").
            if (_selectedProfile is { } profile && _selectedNode is { } node)
                await _viewModel.SelectFolderAsync(profile, node);

            NavigateToRenderedMessage(string.Empty);
            ImagesBlockedBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LEC — Não foi possível mover a mensagem", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeleteMessage_Click(object sender, RoutedEventArgs e)
    {
        var messages = ResolveTargetMessages(sender);
        if (messages.Count == 0)
            return;

        await ConfirmAndDeleteAsync(messages);
    }

    private async void MessageListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;
        if (MessageListView.SelectedItems.Count == 0)
            return;

        e.Handled = true;
        await ConfirmAndDeleteAsync(MessageListView.SelectedItems.Cast<MessageHeader>().ToList());
    }

    private async Task ConfirmAndDeleteAsync(List<MessageHeader> messages)
    {
        if (_selectedProfile is not { } sourceProfile)
            return;

        string question = messages.Count == 1
            ? "Apagar esta mensagem?"
            : $"Apagar estas {messages.Count} mensagens?";
        var result = MessageBox.Show(this, question, "LEC — Apagar mensagem",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            await _viewModel.DeleteMessagesAsync(sourceProfile, messages);

            // The deleted messages are now hidden from the folder currently on screen --
            // reload that folder's list so they disappear, and clear the reading pane in
            // case it was showing one of them (same visual reset MoveMessage_Click/
            // CloseProfile_Click already use for "nothing selected"). Re-read both
            // _selectedProfile/_selectedNode together here, exactly as MoveMessage_Click
            // does, rather than pairing a pre-await profile with a post-await node -- the
            // user's selection could in principle have moved on during the confirmation
            // dialog and the delete call.
            if (_selectedProfile is { } profile && _selectedNode is { } node)
                await _viewModel.SelectFolderAsync(profile, node);

            NavigateToRenderedMessage(string.Empty);
            ImagesBlockedBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LEC — Não foi possível apagar a mensagem", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
```

Note: `ConfirmAndDeleteAsync`'s parameter type changed from `MessageHeader header` to `List<MessageHeader> messages` — this is a breaking signature change to a private method with exactly two callers (`DeleteMessage_Click`, `MessageListView_KeyDown`), both updated above in the same replacement.

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet build`
Expected: builds cleanly.

Run: `dotnet test`
Expected: PASS — no regression in any project.

- [ ] **Step 4: Manual verification**

Run the app (`dotnet run --project src/LEC.App`) against a profile with a folder containing at least 4-5 messages, and check:

1. Click one message, then Ctrl+click two more — confirm all three show as selected (standard WPF multi-select highlight) and the reading pane goes blank (no message rendered).
2. Click a message, then Shift+click one several rows down — confirm the whole range gets selected.
3. With 3 messages selected, press Delete — confirm the confirmation says "Apagar estas 3 mensagens?", and confirming removes all three from the list.
4. With 3 messages selected, right-click a 4th message that is NOT part of the selection, choose "Apagar" — confirm the confirmation still says "3 mensagens" (not 1) and the original 3 get deleted, not the 4th.
5. With 3 messages selected, choose "Mover para...", pick a destination — confirm all 3 disappear from the current list and reappear in the destination folder.
6. Click a single message with no modifier key — confirm the reading pane renders it normally, exactly as before this change.
7. With nothing selected (e.g. right after opening a folder, before clicking anything), right-click a message and choose "Apagar" — confirm it still works as a single-message delete (the `ResolveTargetMessages` fallback path).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/MainWindow.xaml src/LEC.App/MainWindow.xaml.cs
git commit -m "Wire Ctrl/Shift multi-select in the message list to batch delete/move"
```

---

## Notes

- **Task ordering:** Task 1 has no prerequisite. Task 2 depends on Task 1 (`MainWindowViewModel.DeleteMessagesAsync`/`MoveMessagesAsync` must exist before `MainWindow.xaml.cs` can call them).
