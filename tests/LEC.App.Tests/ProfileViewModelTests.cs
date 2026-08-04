using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class ProfileViewModelTests
{
    [Fact]
    public void ConstructorBuildsTheFolderTreeFromTheStoresFolders()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var child = new StoreFolder { Id = 2, ParentId = 1, Name = "Filha", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { root, child });

        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        Assert.Equal("BCP", profile.DisplayName);
        Assert.Equal(@"C:\perfis\BCP", profile.ProfileDirectory);
        Assert.Same(store, profile.Store);
        var rootNode = Assert.Single(profile.FolderTree.RootNodes);
        Assert.Equal("Raiz", rootNode.DisplayName);
        Assert.Single(rootNode.Children);
    }

    [Fact]
    public async Task CreateFolderAsyncAddsTheNewFolderToTheTree()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { root });
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        var created = await profile.CreateFolderAsync("Nova", parentId: null);

        Assert.Null(created.ParentId);
        Assert.Contains(profile.FolderTree.RootNodes, n => n.Folder.Id == created.Id);
    }

    [Fact]
    public async Task CreateFolderAsyncAsASubfolderAddsItUnderTheParentNode()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { root });
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        var created = await profile.CreateFolderAsync("Filha", parentId: root.Id);

        Assert.Equal(root.Id, created.ParentId);
        var rootNode = Assert.Single(profile.FolderTree.RootNodes);
        Assert.Contains(rootNode.Children, n => n.Folder.Id == created.Id);
    }

    [Fact]
    public async Task CreateFolderAsyncPropagatesStoreExceptions()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { root })
        {
            ExceptionToThrowOnCreateFolder = new InvalidOperationException("nome inválido"),
        };
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => profile.CreateFolderAsync("X", parentId: null));
        Assert.Equal("nome inválido", ex.Message);
    }
}
