using System.Linq;
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class FolderTreeViewModelTests
{
    private static StoreFolder Folder(int id, int? parentId, string name) => new()
    {
        Id = id,
        ParentId = parentId,
        Name = name,
        IsLoadable = true,
    };

    [Fact]
    public void BuildsANestedHierarchyFromAFlatList()
    {
        var folders = new[]
        {
            Folder(1, null, "Pastas locais"),
            Folder(2, 1, "Caixa de entrada"),
            Folder(3, 2, "Sub-pasta"),
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        var root = Assert.Single(vm.RootNodes);
        Assert.Equal("Pastas locais", root.DisplayName);
        var inbox = Assert.Single(root.Children);
        Assert.Equal("Caixa de entrada", inbox.DisplayName);
        var sub = Assert.Single(inbox.Children);
        Assert.Equal("Sub-pasta", sub.DisplayName);
    }

    [Fact]
    public void TreatsADanglingParentIdAsATopLevelNodeInsteadOfDroppingOrThrowing()
    {
        // Mirrors the real BCP/Folders.dbx: folder 73 ("seg") has ParentId = 53,
        // and no folder 53 exists.
        var folders = new[]
        {
            Folder(1, null, "Pastas locais"),
            Folder(73, 53, "seg"),
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        Assert.Equal(2, vm.RootNodes.Count);
        Assert.Contains(vm.RootNodes, n => n.DisplayName == "seg");
    }

    [Fact]
    public void FoldersInACycleBecomeRootNodesInsteadOfVanishingFromTheTree()
    {
        // Folder 1's parent is folder 2, and folder 2's parent is folder 1. Neither
        // resolves to an actual root through the normal parent/child pass -- each looks
        // like a perfectly valid child of the other -- so without an explicit reachability
        // sweep both would silently disappear from RootNodes instead of surfacing as a
        // (visibly odd, but present) folder. No such case exists in the real BCP/ fixture
        // (26 declared, 26 reachable) -- this guards the latent failure class.
        var folders = new[]
        {
            Folder(1, 2, "A"),
            Folder(2, 1, "B"),
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        Assert.Equal(2, vm.RootNodes.Count);
        Assert.Contains(vm.RootNodes, n => n.DisplayName == "A");
        Assert.Contains(vm.RootNodes, n => n.DisplayName == "B");
    }

    [Fact]
    public void ASelfReferencingFolderBecomesARootNodeInsteadOfVanishing()
    {
        var folders = new[]
        {
            Folder(1, 1, "Loop"),
            Folder(2, null, "Normal"),
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        Assert.Equal(2, vm.RootNodes.Count);
        Assert.Contains(vm.RootNodes, n => n.DisplayName == "Loop");
    }

    [Fact]
    public void FlagsSpecialFoldersButNotOrdinaryOnes()
    {
        var inbox = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, SpecialFolderKind = SpecialFolderKind.Inbox };
        var ordinary = new StoreFolder { Id = 2, Name = "Enviados 2004", IsLoadable = true };

        var vm = new FolderTreeViewModel();
        vm.Build(new[] { inbox, ordinary });

        Assert.True(vm.RootNodes.Single(n => n.DisplayName == "Caixa de entrada").IsSpecial);
        Assert.False(vm.RootNodes.Single(n => n.DisplayName == "Enviados 2004").IsSpecial);
    }
}
