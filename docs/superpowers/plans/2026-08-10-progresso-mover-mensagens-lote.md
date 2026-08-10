# Progresso ao mover mensagens em lote Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the destination `.dbx` file from being reopened while a batch message move is rewriting it, by showing a modal progress window that blocks folder navigation for the duration of the move.

**Architecture:** `MainWindowViewModel.MoveMessagesAsync` gains optional `IProgress<double>`/`CancellationToken` parameters, checked/reported once per message, never mid-`Rebuild`. A new modal `MoveMessagesProgressDialog` (WPF `Window`) drives that call for batches of 2+ messages; `Window.ShowDialog()` disables the owning `MainWindow` — including the folder tree — for as long as it's open, which is what actually prevents the race. `MoveMessage_Click` branches on message count: 1 message keeps today's direct, windowless call.

**Tech Stack:** C# / .NET 9, WPF, `CommunityToolkit.Mvvm`, xUnit.

## Global Constraints

- Progress/cancellation reuse the exact pattern already used by `RunScavengeAsync`/`ScavengeButton_Click` (`IProgress<double>`, cooperative `CancellationToken`) — no new abstractions.
- `ct.ThrowIfCancellationRequested()` must only be checked between messages, never inside a single message's move — a `Rebuild` in flight must always finish or not have started, never be interrupted.
- The new dialog only appears for 2+ messages; a single-message move stays exactly as it is today (no window, no behavior change).
- Successful completion of the full batch closes the dialog automatically; a partial failure or a cancellation leaves it open showing the final count, with a "Fechar" button.
- Closing the dialog's title-bar X while the batch is still running has the same effect as clicking "Cancelar" — it must not let the window close while a `Rebuild` could still be in flight.
- No new methods on `IMailStore`/`DbxStore` — this is UI/orchestration-layer only, same boundary as the existing batch-move design.

---

### Task 1: `MoveMessagesAsync` — progress reporting and cancellation

**Files:**
- Modify: `src/LEC.App/ViewModels/MainWindowViewModel.cs:199-215` (the `MoveMessagesAsync` method)
- Test: `tests/LEC.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: existing `MoveMessageAsync(ProfileViewModel, MessageHeader, ProfileViewModel, int, CancellationToken)` (unchanged, already in the file).
- Produces: `Task MoveMessagesAsync(ProfileViewModel sourceProfile, IReadOnlyList<MessageHeader> messages, ProfileViewModel destinationProfile, int destinationFolderId, IProgress<double>? progress = null, CancellationToken ct = default)` — the two new parameters are optional and appended at the end, so every existing call site (`MainWindow.xaml.cs:701` and the three existing tests) keeps compiling unchanged.

- [ ] **Step 1: Write the failing tests**

Add to `tests/LEC.App.Tests/MainWindowViewModelTests.cs`, right after the existing `MoveMessagesAsyncWithASingleMessageBehavesLikeMoveMessageAsync` test (currently ending around line 480):

```csharp
    // System.Progress<T>'s callback is dispatched via SynchronizationContext (or
    // ThreadPool.QueueUserWorkItem when there isn't one), so it can run *after* the loop has
    // already moved on -- unusable for a test that needs to cancel deterministically between
    // two specific iterations. This reports synchronously and inline instead.
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    [Fact]
    public async Task MoveMessagesAsyncReportsProgressAfterEachMessage()
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
        var reported = new List<double>();
        var progress = new SynchronousProgress<double>(reported.Add);

        await viewModel.MoveMessagesAsync(sourceProfile, messages, destProfile, destFolder.Id, progress);

        Assert.Equal(new[] { 0.5, 1.0 }, reported);
    }

    [Fact]
    public async Task MoveMessagesAsyncStopsBetweenMessagesWhenCancelled()
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
        var cts = new CancellationTokenSource();
        var reported = new List<double>();
        // Cancel as soon as the 1st message's progress comes in, before the loop reaches the 2nd.
        var progress = new SynchronousProgress<double>(p =>
        {
            reported.Add(p);
            cts.Cancel();
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => viewModel.MoveMessagesAsync(sourceProfile, messages, destProfile, destFolder.Id, progress, cts.Token));

        Assert.Single(reported); // only the 1st message ever got to report
        Assert.Single(destStore.AddedMessages); // only the 1st message was actually moved
        Assert.Contains(ref1, sourceStore.HiddenMessageRefs);
        Assert.DoesNotContain(ref2, sourceStore.HiddenMessageRefs); // 2nd message never attempted
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LEC.App.Tests/LEC.App.Tests.csproj --filter "MoveMessagesAsyncReportsProgressAfterEachMessage|MoveMessagesAsyncStopsBetweenMessagesWhenCancelled"`
Expected: both FAIL — `MoveMessagesAsync` doesn't accept an `IProgress<double>` or honor early cancellation yet (compile error on the new overload, or wrong argument count).

- [ ] **Step 3: Implement**

Replace the `MoveMessagesAsync` method body in `src/LEC.App/ViewModels/MainWindowViewModel.cs` (currently lines 199-215):

```csharp
    /// <exception cref="InvalidOperationException">
    /// Same rewrapping as DeleteMessagesAsync, e.g. "3 de 5 mensagens movidas antes de
    /// falhar: {mensagem original}".
    /// </exception>
    /// <remarks>
    /// <paramref name="progress"/> is reported once per message, as (messages moved so
    /// far) / (total). <paramref name="ct"/> is only checked between messages -- a
    /// cancellation request never interrupts a message already in flight, so the
    /// destination's backing .dbx file is never left mid-rewrite.
    /// </remarks>
    public async Task MoveMessagesAsync(ProfileViewModel sourceProfile, IReadOnlyList<MessageHeader> messages,
        ProfileViewModel destinationProfile, int destinationFolderId,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int succeeded = 0;
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await MoveMessageAsync(sourceProfile, message, destinationProfile, destinationFolderId, ct);
                succeeded++;
                progress?.Report((double)succeeded / messages.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"{succeeded} de {messages.Count} mensagens movidas antes de falhar: {ex.Message}", ex);
            }
        }
    }
```

Also update the doc-comment summary immediately above (currently: `/// Moves multiple messages (all from the same source folder ...`) to add one sentence noting the new optional progress/cancellation parameters, e.g. append: `Optionally reports progress and honors cancellation between messages -- see MoveMessagesProgressDialog, the only current caller that passes them.`

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LEC.App.Tests/LEC.App.Tests.csproj --filter "MoveMessagesAsync"`
Expected: all `MoveMessagesAsync*` tests PASS, including the 3 pre-existing ones (signature change must stay backward compatible).

- [ ] **Step 5: Commit**

```bash
git add src/LEC.App/ViewModels/MainWindowViewModel.cs tests/LEC.App.Tests/MainWindowViewModelTests.cs
git commit -m "Add progress reporting and cooperative cancellation to MoveMessagesAsync"
```

---

### Task 2: `MoveMessagesProgressDialog` — modal progress window

**Files:**
- Create: `src/LEC.App/MoveMessagesProgressDialog.xaml`
- Create: `src/LEC.App/MoveMessagesProgressDialog.xaml.cs`

**Interfaces:**
- Consumes: `MainWindowViewModel.MoveMessagesAsync(ProfileViewModel, IReadOnlyList<MessageHeader>, ProfileViewModel, int, IProgress<double>?, CancellationToken)` from Task 1; `ProfileViewModel`, `MessageHeader` (existing types, `LEC.App.ViewModels`/`LEC.MailStore`).
- Produces: `public MoveMessagesProgressDialog(MainWindowViewModel viewModel, ProfileViewModel sourceProfile, IReadOnlyList<MessageHeader> messages, ProfileViewModel destinationProfile, int destinationFolderId)` and `public Exception? Failure { get; private set; }`, both consumed by Task 3.

No unit test for this task — it's a WPF `Window` with no injectable seam, same precedent as `MoveMessageDialog`/`MoveFolderDialog` (no automated tests). Verified manually in Task 4.

- [ ] **Step 1: Create the XAML**

Write `src/LEC.App/MoveMessagesProgressDialog.xaml`:

```xml
<!-- src/LEC.App/MoveMessagesProgressDialog.xaml -->
<Window x:Class="LEC.App.MoveMessagesProgressDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Movendo mensagens" Width="420" Height="160"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False"
        ResizeMode="NoResize" Closing="Window_Closing">
    <DockPanel Margin="16">
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button x:Name="CancelButton" Content="Cancelar" Width="80" Margin="0,0,8,0" Click="CancelButton_Click"/>
            <Button x:Name="CloseButton" Content="Fechar" Width="80" IsDefault="True" Visibility="Collapsed" Click="CloseButton_Click"/>
        </StackPanel>
        <StackPanel VerticalAlignment="Center">
            <TextBlock x:Name="StatusText" TextWrapping="Wrap" Margin="0,0,0,8"/>
            <ProgressBar x:Name="ProgressBar" Minimum="0" Maximum="1" Height="18"/>
        </StackPanel>
    </DockPanel>
</Window>
```

- [ ] **Step 2: Create the code-behind**

Write `src/LEC.App/MoveMessagesProgressDialog.xaml.cs`:

```csharp
// src/LEC.App/MoveMessagesProgressDialog.xaml.cs
using System.ComponentModel;
using System.Windows;
using LEC.App.ViewModels;
using LEC.MailStore;

namespace LEC.App;

public partial class MoveMessagesProgressDialog : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ProfileViewModel _sourceProfile;
    private readonly IReadOnlyList<MessageHeader> _messages;
    private readonly ProfileViewModel _destinationProfile;
    private readonly int _destinationFolderId;
    private readonly CancellationTokenSource _cts = new();

    private int _succeeded;
    private bool _finished;

    public Exception? Failure { get; private set; }

    public MoveMessagesProgressDialog(MainWindowViewModel viewModel, ProfileViewModel sourceProfile,
        IReadOnlyList<MessageHeader> messages, ProfileViewModel destinationProfile, int destinationFolderId)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _sourceProfile = sourceProfile;
        _messages = messages;
        _destinationProfile = destinationProfile;
        _destinationFolderId = destinationFolderId;

        StatusText.Text = $"Movendo mensagens... 0 de {_messages.Count}";
        Loaded += MoveMessagesProgressDialog_Loaded;
    }

    private async void MoveMessagesProgressDialog_Loaded(object sender, RoutedEventArgs e)
    {
        // SynchronizationContext.Post (System.Progress<T>'s delivery mechanism) marshals back
        // onto this window's dispatcher thread, exactly like every other UI update here -- safe
        // to touch ProgressBar/StatusText directly from the callback.
        var progress = new Progress<double>(p =>
        {
            _succeeded = (int)Math.Round(p * _messages.Count);
            ProgressBar.Value = p;
            StatusText.Text = $"Movendo mensagens... {_succeeded} de {_messages.Count} ({p:P0})";
        });

        try
        {
            await _viewModel.MoveMessagesAsync(_sourceProfile, _messages, _destinationProfile, _destinationFolderId, progress, _cts.Token);
            _finished = true;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            ShowFinalState($"Cancelado: {_succeeded} de {_messages.Count} mensagens movidas.");
        }
        catch (Exception ex)
        {
            Failure = ex;
            ShowFinalState(ex.Message);
        }
    }

    private void ShowFinalState(string message)
    {
        _finished = true;
        StatusText.Text = message;
        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        _cts.Cancel();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // Closing the window (title-bar X) while the move is still running must behave exactly
    // like clicking "Cancelar" -- and must not let the window actually close yet, since the
    // background Rebuild could still be mid-write. Once the Loaded handler's catch block
    // reaches ShowFinalState (or the success path calls Close() itself), _finished is true
    // and this lets the close through.
    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_finished)
            return;

        _cts.Cancel();
        e.Cancel = true;
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/LEC.App/LEC.App.csproj`
Expected: builds with no errors (this task adds no tests to run — verified visually in Task 4).

- [ ] **Step 4: Commit**

```bash
git add src/LEC.App/MoveMessagesProgressDialog.xaml src/LEC.App/MoveMessagesProgressDialog.xaml.cs
git commit -m "Add MoveMessagesProgressDialog: modal progress window for batch message moves"
```

---

### Task 3: Wire the dialog into `MoveMessage_Click`

**Files:**
- Modify: `src/LEC.App/MainWindow.xaml.cs:686-724` (the `MoveMessage_Click` method)

**Interfaces:**
- Consumes: `MoveMessagesProgressDialog` (Task 2) and the extended `MainWindowViewModel.MoveMessagesAsync` (Task 1).
- Produces: nothing new for other tasks — this is the final integration point.

No new automated test (event handler on the code-behind, same precedent as the rest of `MainWindow.xaml.cs`) — covered by the manual checklist in Task 4.

- [ ] **Step 1: Replace the method body**

In `src/LEC.App/MainWindow.xaml.cs`, replace the current `MoveMessage_Click` (lines 686-724):

```csharp
    private async void MoveMessage_Click(object sender, RoutedEventArgs e)
    {
        var messages = ResolveTargetMessages(sender);
        if (messages.Count == 0)
            return;
        if (_selectedProfile is not { } sourceProfile)
            return;

        var dialog = new MoveMessageDialog(_viewModel.OpenProfiles, sourceProfile, messages[0].Id.FolderId) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedProfile is not { } destinationProfile || dialog.SelectedFolderId is not { } destinationFolderId)
            return;

        Exception? failure = null;
        try
        {
            if (messages.Count == 1)
            {
                await _viewModel.MoveMessagesAsync(sourceProfile, messages, destinationProfile, destinationFolderId);
            }
            else
            {
                // A modal window: ShowDialog() disables this MainWindow -- including the
                // FolderTreeView -- for as long as it's open. That's what actually prevents the
                // race that used to happen when the user navigated to the destination folder
                // mid-batch, reopening a file handle that collided with AddMessageAsync's
                // Rebuild/File.Move on the same .dbx (see MainWindowViewModel.AddMessageAsync's
                // eviction comment for the half of this race it already handled).
                var progressDialog = new MoveMessagesProgressDialog(_viewModel, sourceProfile, messages, destinationProfile, destinationFolderId) { Owner = this };
                progressDialog.ShowDialog();
                failure = progressDialog.Failure;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // The moved messages -- all of them on success, or whichever ones completed before a
        // partial failure -- are now hidden from the folder currently on screen (its source),
        // so reload that folder's list unconditionally so they disappear, and clear the
        // reading pane in case it was showing one of them (same visual reset
        // CloseProfile_Click already uses for "nothing selected"). This must run even when
        // the batch above threw partway through: skipping it would leave already-moved
        // messages showing as stale rows, and retrying the same selection would move (i.e.
        // duplicate) them again.
        if (_selectedProfile is { } profile && _selectedNode is { } node)
            await _viewModel.SelectFolderAsync(profile, node);

        NavigateToRenderedMessage(string.Empty);
        ImagesBlockedBar.Visibility = Visibility.Collapsed;

        if (failure is not null)
            MessageBox.Show(this, failure.Message, "LEC — Não foi possível mover a mensagem", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build src/LEC.App/LEC.App.csproj`
Expected: builds with no errors.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`
Expected: all tests pass, including the new `MoveMessagesAsync*` tests from Task 1 and every pre-existing test in the solution.

- [ ] **Step 4: Commit**

```bash
git add src/LEC.App/MainWindow.xaml.cs
git commit -m "Show a modal progress window when moving 2+ messages at once"
```

---

### Task 4: Manual verification

**Files:** none (no code changes — this task only runs and observes the app).

- [ ] **Step 1: Launch the app**

Run: `dotnet run --project src/LEC.App/LEC.App.csproj`

- [ ] **Step 2: Reproduce the original bug is now fixed**

Open a profile with a large folder (or create one with 50+ test messages). Select a batch of messages (Ctrl/Shift-click), choose "Mover para..." into another folder in the same profile, confirm the destination in `MoveMessageDialog`. While the new progress window is showing and the bar is moving, try clicking a different folder in the tree behind it.
Expected: the folder tree does not respond to the click at all — the progress window has to be dealt with first. No `UnauthorizedAccessException` / "Access to the path is denied" message appears.

- [ ] **Step 3: Verify the progress bar and auto-close on success**

Let the batch above run to completion.
Expected: the progress bar fills to 100%, the window closes on its own (no "Fechar" click needed), and the source folder's list refreshes with the moved messages gone.

- [ ] **Step 4: Verify cancellation**

Start another batch move of 20+ messages. Click "Cancelar" partway through.
Expected: the window stops updating the bar, shows "Cancelado: N de M mensagens movidas." with N < M, and only a "Fechar" button remains. Click it, confirm the source folder list no longer shows the N messages that did move, and still shows the rest.

- [ ] **Step 5: Verify closing via the title-bar X behaves like Cancelar**

Start another batch move of 20+ messages. Click the window's title-bar X while it's still running.
Expected: the window does not close immediately — it behaves exactly like Task 4 Step 4 (shows the final cancelled count, then a click on "Fechar" — or the X again — actually closes it).

- [ ] **Step 6: Verify single-message move is unchanged**

Select exactly 1 message, move it to another folder.
Expected: no progress window appears at all — same instant behavior as before this feature.

- [ ] **Step 7: Verify partial failure still surfaces the error**

(If feasible: move a batch into a folder, then make one intermediate `AddMessageAsync` fail — e.g. by pointing the destination at a folder whose backing file was made read-only mid-batch — otherwise skip this step and note it as unverified.)
Expected: the progress window stays open, shows "N de M mensagens movidas antes de falhar: ...", and after clicking "Fechar" the `MessageBox` with the same error text appears.
