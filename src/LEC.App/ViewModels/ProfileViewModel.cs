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

    public async Task DeleteFolderAsync(int folderId)
    {
        // DbxStore.DeleteFolderAsync can throw AFTER already deleting the folder for real
        // (a backing file that couldn't be removed from disk) -- the tree must reflect that
        // even when the call ends in an exception, not only on the success path.
        try
        {
            await Store.DeleteFolderAsync(folderId);
        }
        finally
        {
            FolderTree.Build(Store.Folders);
        }
    }

    public async Task MoveFolderAsync(int folderId, int? newParentId)
    {
        await Store.MoveFolderAsync(folderId, newParentId);
        FolderTree.Build(Store.Folders);
    }
}
