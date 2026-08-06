using System.Linq;
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

    [Fact]
    public async Task DeleteFolderAsyncRemovesTheFolderFromTheTree()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var child = new StoreFolder { Id = 2, ParentId = 1, Name = "Filha", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { root, child });
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        await profile.DeleteFolderAsync(child.Id);

        Assert.Contains(child.Id, store.DeletedFolderIds);
        var rootNode = Assert.Single(profile.FolderTree.RootNodes);
        Assert.Empty(rootNode.Children);
    }

    [Fact]
    public async Task DeleteFolderAsyncPropagatesStoreExceptions()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { root })
        {
            ExceptionToThrowOnDeleteFolder = new InvalidOperationException("pasta especial"),
        };
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => profile.DeleteFolderAsync(root.Id));
        Assert.Equal("pasta especial", ex.Message);
    }

    [Fact]
    public async Task DeleteFolderAsyncRebuildsTheTreeEvenWhenTheStoreThrowsAfterAlreadyDeleting()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var child = new StoreFolder { Id = 2, ParentId = 1, Name = "Filha", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { root, child })
        {
            ExceptionToThrowAfterDeletingFolder = new InvalidOperationException(
                "Pasta apagada, mas não foi possível remover do disco: Filha.dbx (em uso por outro processo)."),
        };
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => profile.DeleteFolderAsync(child.Id));
        Assert.Contains("Filha.dbx", ex.Message);

        // The store already removed the folder before throwing -- FolderTree must reflect
        // that even though the call surfaced as a failure to the caller.
        var rootNode = Assert.Single(profile.FolderTree.RootNodes);
        Assert.Empty(rootNode.Children);
    }

    [Fact]
    public async Task MoveFolderAsyncUpdatesTheTreeUnderTheNewParent()
    {
        var rootA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var rootB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
        var moved = new StoreFolder { Id = 3, ParentId = 1, Name = "Movida", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { rootA, rootB, moved });
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        await profile.MoveFolderAsync(moved.Id, newParentId: rootB.Id);

        Assert.Contains((moved.Id, (int?)rootB.Id), store.MovedFolders);
        var nodeB = profile.FolderTree.RootNodes.Single(n => n.Folder.Id == rootB.Id);
        Assert.Contains(nodeB.Children, n => n.Folder.Id == moved.Id);
    }

    [Fact]
    public async Task MoveFolderAsyncPropagatesStoreExceptions()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { root })
        {
            ExceptionToThrowOnMoveFolder = new InvalidOperationException("ciclo"),
        };
        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => profile.MoveFolderAsync(root.Id, newParentId: null));
        Assert.Equal("ciclo", ex.Message);
    }
}
