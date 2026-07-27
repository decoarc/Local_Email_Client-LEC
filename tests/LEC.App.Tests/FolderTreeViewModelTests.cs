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
