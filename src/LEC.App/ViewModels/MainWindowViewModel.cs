using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.DbxFormat;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<ProfileViewModel> OpenProfiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Store))]
    private ProfileViewModel? _activeProfile;

    public IMailStore? Store => ActiveProfile?.Store;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private MessageListViewModel? _messageList;

    [ObservableProperty]
    private MessageReaderViewModel? _messageReader;

    [ObservableProperty]
    private ScavengeStatus? _currentFolderScavengeStatus;

    public void OpenProfile(string profileDirectory)
    {
        try
        {
            var store = DbxStore.Open(profileDirectory);
            string displayName = new DirectoryInfo(profileDirectory).Name;
            OpenProfiles.Add(new ProfileViewModel(displayName, profileDirectory, store));

            StatusText = store.Health == StoreHealth.Ok
                ? null
                : $"Perfil aberto com avisos (Health: {store.Health}).";
        }
        catch (Exception ex) when (ex is DbxFormatException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Não foi possível abrir o perfil: {ex.Message}";
        }
    }

    public void CloseProfile(ProfileViewModel profile)
    {
        bool wasActive = ReferenceEquals(ActiveProfile, profile);

        // The content preload reads through this profile's store one message at a time,
        // resuming on the dispatcher between messages -- cancel it BEFORE disposing that
        // store below, so a pending resume sees a clean cancellation instead of doing I/O
        // against a disposed store. Same cancel-before-dispose ordering CloseProfile_Click
        // already applies to an in-flight scavenge scan. ClearCurrentFolder() also cancels,
        // but it only runs after the Dispose below -- too late for this one iteration.
        if (wasActive)
            MessageList?.CancelContentPreload();

        OpenProfiles.Remove(profile);
        profile.Store.Dispose();

        if (wasActive)
        {
            ActiveProfile = null;
            ClearCurrentFolder();
        }
    }

    // Resets the message list / reading pane state for the currently displayed folder.
    // Used whenever that folder stops being valid to show: the owning profile was closed,
    // or the folder itself was just deleted or moved out from under the current selection.
    public void ClearCurrentFolder()
    {
        // Nothing points at the outgoing MessageList after this, but its in-flight preload
        // Task keeps a strong reference to it (and to the store it reads from) and would run
        // to completion regardless -- cancel it instead of leaving it churning against a
        // folder/profile that is no longer on screen.
        MessageList?.CancelContentPreload();
        MessageList = null;
        MessageReader = null;
        CurrentFolderScavengeStatus = null;
    }

    public async Task SelectFolderAsync(ProfileViewModel profile, FolderNodeViewModel node, CancellationToken ct = default)
    {
        if (!ReferenceEquals(ActiveProfile, profile))
        {
            // Same reasoning as ClearCurrentFolder: the instance being replaced here is
            // orphaned, but its preload isn't -- and it reads from the OLD profile's store,
            // which LoadAsync on the new instance can never supersede.
            MessageList?.CancelContentPreload();
            ActiveProfile = profile;
            MessageList = new MessageListViewModel(profile.Store);
            MessageReader = new MessageReaderViewModel(profile.Store);
        }

        CurrentFolderScavengeStatus = profile.Store.GetScavengeStatus(node.Folder);
        await MessageList!.LoadAsync(node.Folder, ct);
    }

    public async Task SelectMessageAsync(MessageHeader header, CancellationToken ct = default)
    {
        if (MessageReader is not null)
            await MessageReader.LoadAsync(header.Id, ct);
    }

    public async Task RunScavengeAsync(ProfileViewModel profile, FolderNodeViewModel node, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        CurrentFolderScavengeStatus = await profile.Store.ScavengeAsync(node.Folder, progress, ct);
    }

    /// <summary>
    /// Copies a message into another folder (possibly in a different open profile) and
    /// hides it from its source folder — never deletes or rewrites the source .dbx. Order
    /// matters: the source is only hidden after the destination write succeeds, so a
    /// failed add leaves both sides exactly as they were.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Known limitation — truncated sources are copied silently. IMailStore.OpenMessageAsync
    /// does not surface the "truncated" flag that DbxEmailFile.ReadMessageRaw computes
    /// internally, so a partially-corrupt source message is copied to the destination exactly
    /// as read, with no warning, and the (still-intact) original is then hidden. Detecting
    /// this would mean changing OpenMessageAsync's contract for every caller, so it is
    /// deliberately left for a later slice.
    /// </para>
    /// <para>
    /// Known limitation — message flags are not preserved. MessageHeader does not expose
    /// Flags, so the moved copy is always written with default flags: read/unread and any
    /// other flag state is lost across a move.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the destination is the message's own folder in the same profile.
    /// </exception>
    public async Task MoveMessageAsync(ProfileViewModel sourceProfile, MessageHeader message,
        ProfileViewModel destinationProfile, int destinationFolderId, CancellationToken ct = default)
    {
        // Rejected before any I/O: a self-move would rebuild the folder with a duplicate of
        // the message and then hide the original at an offset that same rebuild invalidated.
        // Reference equality is the right identity test here -- ProfileViewModel instances are
        // stable for as long as a profile stays open.
        if (ReferenceEquals(sourceProfile, destinationProfile) && message.Id.FolderId == destinationFolderId)
            throw new InvalidOperationException("Não é possível mover uma mensagem para a mesma pasta.");

        // AddMessageAsync below runs its whole body on a thread-pool thread (Task.Run) and
        // evicts/rewrites _openFolders/_messagesByFolder -- exactly the dictionaries the
        // message list's content preload reads via OpenMessageAsync when it resumes on the
        // dispatcher. Cancel the preload first; the SelectFolderAsync every call site runs
        // afterward starts a fresh one.
        MessageList?.CancelContentPreload();

        using var stream = await sourceProfile.Store.OpenMessageAsync(message.Id, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);

        await destinationProfile.Store.AddMessageAsync(destinationFolderId, ms.ToArray(),
            message.Subject, message.SenderName, message.SenderAddress, message.MessageId, message.Date, ct);

        await sourceProfile.Store.HideMovedMessageAsync(message.Id, ct);
    }

    /// <summary>
    /// Deletes a message — hides it from its folder via IMailStore.DeleteMessageAsync,
    /// never writes to the backing .dbx. Thin pass-through: unlike MoveMessageAsync, there's
    /// no second store to orchestrate against.
    /// </summary>
    public async Task DeleteMessageAsync(ProfileViewModel profile, MessageHeader message, CancellationToken ct = default)
    {
        // Deletes only touch the hide caches, which OpenMessageAsync doesn't read today --
        // so unlike the move path this isn't a known race. Cancelled anyway, for consistency
        // with every other store mutation here, rather than depending on that staying true.
        MessageList?.CancelContentPreload();

        await profile.Store.DeleteMessageAsync(message.Id, ct);
    }

    /// <summary>
    /// Deletes multiple messages, one at a time via the existing DeleteMessageAsync,
    /// stopping at the first failure. Messages already deleted before the failure stay
    /// deleted -- each message is its own atomic, independent operation, so there's
    /// nothing to roll back.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Whatever DeleteMessageAsync throws for the failing message, rewrapped with a prefix
    /// stating how many messages succeeded before it, e.g. "3 de 5 mensagens apagadas
    /// antes de falhar: {mensagem original}".
    /// </exception>
    public async Task DeleteMessagesAsync(ProfileViewModel profile, IReadOnlyList<MessageHeader> messages, CancellationToken ct = default)
    {
        // Once at the top, not per message: the preload can't resume between here and the
        // end of the batch without seeing the cancellation first.
        MessageList?.CancelContentPreload();

        int succeeded = 0;
        foreach (var message in messages)
        {
            try
            {
                await DeleteMessageAsync(profile, message, ct);
                succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"{succeeded} de {messages.Count} mensagens apagadas antes de falhar: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Moves multiple messages (all from the same source folder -- the message list only
    /// ever shows one folder at a time) into a single chosen destination, one at a time via
    /// the existing MoveMessageAsync, stopping at the first failure. Same "nothing to roll
    /// back" reasoning as DeleteMessagesAsync. Optionally reports progress and honors
    /// cancellation between messages -- see MoveMessagesProgressDialog, the only current
    /// caller that passes them.
    /// </summary>
    /// <remarks>
    /// <paramref name="progress"/> is reported once per message, as (messages moved so
    /// far) / (total). <paramref name="ct"/> is only checked between messages -- a
    /// cancellation request never interrupts a message already in flight, so the
    /// destination's backing .dbx file is never left mid-rewrite.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Same rewrapping as DeleteMessagesAsync, e.g. "3 de 5 mensagens movidas antes de
    /// falhar: {mensagem original}".
    /// </exception>
    public async Task MoveMessagesAsync(ProfileViewModel sourceProfile, IReadOnlyList<MessageHeader> messages,
        ProfileViewModel destinationProfile, int destinationFolderId,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // Once at the top, not per message -- see DeleteMessagesAsync. (MoveMessageAsync
        // cancels again on each item; cancelling an already-cancelled source is a no-op.)
        MessageList?.CancelContentPreload();

        int succeeded = 0;
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await MoveMessageAsync(sourceProfile, message, destinationProfile, destinationFolderId, CancellationToken.None);
                succeeded++;
                progress?.Report((double)succeeded / messages.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"{succeeded} de {messages.Count} mensagens movidas antes de falhar: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Moves a folder — same-profile is the existing cheap ParentId retarget
    /// (ProfileViewModel.MoveFolderAsync); cross-profile copies the whole subtree (folders +
    /// non-recovered messages) into destinationProfile, then permanently deletes it from
    /// sourceProfile via the already-shipped DeleteFolderAsync — sourceProfile's Folders.dbx
    /// is rewritten and its backing .dbx files are removed from disk. Nothing is deleted
    /// from the source unless the ENTIRE subtree copied successfully first.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// For a cross-profile move only (the same-profile branch returns early via
    /// DbxStore.MoveFolderAsync, which only checks the folder itself): thrown when
    /// sourceNode's folder, or any folder in its subtree, is special (SpecialFolderKind !=
    /// None); or when the subtree has two folders sharing a name (case-insensitive), or a
    /// name that already exists anywhere in destinationProfile — all checked before any
    /// copying starts. Also propagates whatever CreateFolderAsync/AddMessageAsync throws on
    /// the destination profile beyond that (255-folder cap, damaged files) — the source is
    /// left completely untouched in every one of those cases, though whatever was already
    /// created in the destination before the failure is not rolled back.
    /// </exception>
    public async Task MoveFolderAsync(ProfileViewModel sourceProfile, FolderNodeViewModel sourceNode,
        ProfileViewModel destinationProfile, int? destinationParentId, CancellationToken ct = default)
    {
        // Same race as message move/delete: both branches below mutate DbxStore on a
        // background thread (Task.Run), and the cross-profile branch's DeleteFolderAsync
        // disposes the folder's backing DbxEmailFile/mapped view -- worse than stale state if
        // a preload mid-read hits it. Cancel before either branch touches the store.
        MessageList?.CancelContentPreload();

        if (ReferenceEquals(sourceProfile, destinationProfile))
        {
            await sourceProfile.MoveFolderAsync(sourceNode.Folder.Id, destinationParentId);
            return;
        }

        if (sourceNode.Folder.SpecialFolderKind != SpecialFolderKind.None || ContainsSpecial(sourceNode))
            throw new InvalidOperationException("Pastas especiais não podem ser movidas.");

        ValidateNoNameCollisions(sourceNode, destinationProfile);

        await CopyFolderSubtreeAsync(sourceProfile, sourceNode, destinationProfile, destinationParentId, ct);

        await sourceProfile.DeleteFolderAsync(sourceNode.Folder.Id);
    }

    private static bool ContainsSpecial(FolderNodeViewModel node) =>
        node.Children.Any(c => c.Folder.SpecialFolderKind != SpecialFolderKind.None || ContainsSpecial(c));

    // DbxStore.CreateFolderAsync derives a folder's backing file as its (sanitized) name
    // flat in the profile directory, with no per-parent nesting, and refuses to create a
    // file that already exists anywhere in the profile -- not just among siblings. That
    // pre-existing constraint (out of scope here) means a cross-profile copy must be
    // rejected upfront whenever the subtree being moved would produce two same-named
    // folders anywhere in the destination profile; otherwise CreateFolderAsync throws
    // partway through the copy, after some folders/messages are already sitting in the
    // destination.
    private static void ValidateNoNameCollisions(FolderNodeViewModel sourceNode, ProfileViewModel destinationProfile)
    {
        var subtreeNames = new List<string>();
        CollectNames(sourceNode, subtreeNames);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in subtreeNames)
        {
            if (!seen.Add(name))
                throw new InvalidOperationException(
                    $"A subárvore tem mais de uma pasta chamada \"{name}\" — o perfil de destino não suporta pastas com o mesmo nome em lugares diferentes.");
        }

        var destinationNames = new HashSet<string>(
            destinationProfile.Store.Folders.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var name in subtreeNames)
        {
            if (destinationNames.Contains(name))
                throw new InvalidOperationException(
                    $"Já existe uma pasta chamada \"{name}\" no perfil de destino.");
        }
    }

    private static void CollectNames(FolderNodeViewModel node, List<string> names)
    {
        names.Add(node.DisplayName);
        foreach (var child in node.Children)
            CollectNames(child, names);
    }

    // Recreates one folder in destinationProfile (under destinationParentId), copies every
    // non-recovered message from it, then recurses into every child using the newly created
    // folder's id as the next level's parent. sourceNode.Children can never contain a cycle
    // (FolderTreeViewModel.Build already resolved any at UI-tree-build time), so no
    // visited-set is needed here.
    private async Task CopyFolderSubtreeAsync(ProfileViewModel sourceProfile, FolderNodeViewModel sourceNode,
        ProfileViewModel destinationProfile, int? destinationParentId, CancellationToken ct)
    {
        var created = await destinationProfile.CreateFolderAsync(sourceNode.DisplayName, destinationParentId);

        if (sourceNode.Folder.HasBackingFile)
        {
            await foreach (var header in sourceProfile.Store.EnumerateHeadersAsync(sourceNode.Folder, ct))
            {
                if (header.RecoveredKind is not null)
                    continue;

                using var stream = await sourceProfile.Store.OpenMessageAsync(header.Id, ct);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);

                await destinationProfile.Store.AddMessageAsync(created.Id, ms.ToArray(),
                    header.Subject, header.SenderName, header.SenderAddress, header.MessageId, header.Date, ct);
            }
        }

        foreach (var child in sourceNode.Children)
            await CopyFolderSubtreeAsync(sourceProfile, child, destinationProfile, created.Id, ct);
    }

    public Task CompactFolderAsync(ProfileViewModel profile, int folderId, CancellationToken ct = default)
    {
        // Same race as move/delete: DbxStore.CompactFolderAsync runs on Task.Run and evicts
        // _openFolders/_messagesByFolder — the dictionaries the content preload reads.
        MessageList?.CancelContentPreload();
        return profile.Store.CompactFolderAsync(folderId, ct);
    }

    public Task CompactAllPendingAsync(ProfileViewModel profile, IProgress<CompactProgress>? progress = null, CancellationToken ct = default)
    {
        MessageList?.CancelContentPreload();
        return profile.Store.CompactAllPendingAsync(progress, ct);
    }

    public Task<CompactStatus> GetCompactStatusAsync(ProfileViewModel profile, CancellationToken ct = default)
        => profile.Store.GetCompactStatusAsync(ct);

    public async Task<CompactStatus> GetAggregatedCompactStatusAsync(CancellationToken ct = default)
    {
        var all = new List<CompactFolderStatus>();
        foreach (var profile in OpenProfiles)
        {
            var status = await profile.Store.GetCompactStatusAsync(ct);
            all.AddRange(status.PendingFolders);
        }
        return new CompactStatus(all);
    }
}
