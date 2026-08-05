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
        MessageList = null;
        MessageReader = null;
        CurrentFolderScavengeStatus = null;
    }

    public async Task SelectFolderAsync(ProfileViewModel profile, FolderNodeViewModel node, CancellationToken ct = default)
    {
        if (!ReferenceEquals(ActiveProfile, profile))
        {
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
        await profile.Store.DeleteMessageAsync(message.Id, ct);
    }

    /// <summary>
    /// Moves a folder — same-profile is the existing cheap ParentId retarget
    /// (ProfileViewModel.MoveFolderAsync); cross-profile copies the whole subtree (folders +
    /// non-recovered messages) into destinationProfile, then hides it in sourceProfile via
    /// the already-shipped DeleteFolderAsync — never touches sourceProfile's Folders.dbx or
    /// any message .dbx. Nothing is hidden in the source unless the ENTIRE subtree copied
    /// successfully first.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when sourceNode's folder, or any folder in its subtree, is special
    /// (SpecialFolderKind != None) — checked before any copying starts. For a cross-profile
    /// move, also propagates whatever CreateFolderAsync/AddMessageAsync throws on the
    /// destination profile (name collision, 255-item caps, damaged files) — the source is
    /// left completely untouched in every one of those cases, though whatever was already
    /// created in the destination before the failure is not rolled back.
    /// </exception>
    public async Task MoveFolderAsync(ProfileViewModel sourceProfile, FolderNodeViewModel sourceNode,
        ProfileViewModel destinationProfile, int? destinationParentId, CancellationToken ct = default)
    {
        if (ReferenceEquals(sourceProfile, destinationProfile))
        {
            await sourceProfile.MoveFolderAsync(sourceNode.Folder.Id, destinationParentId);
            return;
        }

        if (sourceNode.Folder.SpecialFolderKind != SpecialFolderKind.None || ContainsSpecial(sourceNode))
            throw new InvalidOperationException("Pastas especiais não podem ser movidas.");

        await CopyFolderSubtreeAsync(sourceProfile, sourceNode, destinationProfile, destinationParentId, ct);

        await sourceProfile.DeleteFolderAsync(sourceNode.Folder.Id);
    }

    private static bool ContainsSpecial(FolderNodeViewModel node) =>
        node.Children.Any(c => c.Folder.SpecialFolderKind != SpecialFolderKind.None || ContainsSpecial(c));

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
}
