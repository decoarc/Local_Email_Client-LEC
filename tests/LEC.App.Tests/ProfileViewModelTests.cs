using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class ProfileViewModelTests
{
    [Fact]
    public void ConstructorBuildsTheFolderTreeFromTheStoresFolders()
    {
        var root = new StoreFolder { Id = 1, Name = "Raiz", IsLoadable = true };
        var child = new StoreFolder { Id = 2, ParentId = 1, Name = "Filha", IsLoadable = true };
        var store = new FakeMailStore(folders: new[] { root, child });

        var profile = new ProfileViewModel("BCP", @"C:\perfis\BCP", store);

        Assert.Equal("BCP", profile.DisplayName);
        Assert.Equal(@"C:\perfis\BCP", profile.ProfileDirectory);
        Assert.Same(store, profile.Store);
        var rootNode = Assert.Single(profile.FolderTree.RootNodes);
        Assert.Equal("Raiz", rootNode.DisplayName);
        Assert.Single(rootNode.Children);
    }
}
