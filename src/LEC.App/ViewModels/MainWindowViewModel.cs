using System.Collections.ObjectModel;
using System.IO;
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
            MessageList = null;
            MessageReader = null;
            CurrentFolderScavengeStatus = null;
        }
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
}
