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
}
