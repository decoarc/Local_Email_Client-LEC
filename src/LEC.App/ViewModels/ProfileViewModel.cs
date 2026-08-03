using LEC.MailStore;

namespace LEC.App.ViewModels;

/// <summary>
/// One open profile: its store, its own folder tree, and the label shown above that tree
/// in the UI. MainWindowViewModel holds a collection of these — one per profile the user
/// has opened, none of them ever replacing another (see MainWindowViewModel.OpenProfile).
/// </summary>
public sealed class ProfileViewModel
{
    public string DisplayName { get; }
    public string ProfileDirectory { get; }
    public IMailStore Store { get; }
    public FolderTreeViewModel FolderTree { get; } = new();

    public ProfileViewModel(string displayName, string profileDirectory, IMailStore store)
    {
        DisplayName = displayName;
        ProfileDirectory = profileDirectory;
        Store = store;
        FolderTree.Build(store.Folders);
    }

    public async Task<StoreFolder> CreateFolderAsync(string name, int? parentId)
    {
        var created = await Store.CreateFolderAsync(name, parentId);
        FolderTree.Build(Store.Folders);
        return created;
    }
}
