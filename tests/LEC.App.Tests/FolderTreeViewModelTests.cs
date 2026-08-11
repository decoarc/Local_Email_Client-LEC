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
        HasBackingFile = true,
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
        var inbox = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.Inbox };
        var ordinary = new StoreFolder { Id = 2, Name = "Enviados 2004", IsLoadable = true, HasBackingFile = true };

        var vm = new FolderTreeViewModel();
        vm.Build(new[] { inbox, ordinary });

        Assert.True(vm.RootNodes.Single(n => n.DisplayName == "Caixa de entrada").IsSpecial);
        Assert.False(vm.RootNodes.Single(n => n.DisplayName == "Enviados 2004").IsSpecial);
    }

    [Fact]
    public void RebuildingPreservesWhichExistingNodesWereExpanded()
    {
        var folders = new[]
        {
            Folder(1, null, "Pastas locais"),
            Folder(2, 1, "Caixa de entrada"),
            Folder(3, 2, "Sub-pasta"),
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        var root = vm.RootNodes.Single();
        var inbox = root.Children.Single();
        root.IsExpanded = true;
        inbox.IsExpanded = true;

        // Build() always replaces every node with a brand-new instance, even when
        // nothing about that particular folder changed -- a rename or unrelated
        // sibling edit rebuilds the whole tree. The new instances must inherit the
        // expansion state of the old ones they replace, or every subfolder the user
        // had open collapses on every folder create/delete/move.
        vm.Build(folders);

        var rebuiltRoot = vm.RootNodes.Single();
        var rebuiltInbox = rebuiltRoot.Children.Single();
        var rebuiltSub = rebuiltInbox.Children.Single();

        Assert.True(rebuiltRoot.IsExpanded);
        Assert.True(rebuiltInbox.IsExpanded);
        Assert.False(rebuiltSub.IsExpanded);
    }

    [Fact]
    public void NewlyAddedFoldersStartCollapsedEvenWhenSiblingsWereExpanded()
    {
        var folders = new[]
        {
            Folder(1, null, "Pastas locais"),
            Folder(2, 1, "Caixa de entrada"),
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);
        vm.RootNodes.Single().IsExpanded = true;

        var withNewSubfolder = new[]
        {
            Folder(1, null, "Pastas locais"),
            Folder(2, 1, "Caixa de entrada"),
            Folder(3, 1, "Nova pasta"),
        };
        vm.Build(withNewSubfolder);

        var root = vm.RootNodes.Single();
        Assert.True(root.IsExpanded);
        Assert.False(root.Children.Single(n => n.DisplayName == "Nova pasta").IsExpanded);
    }

    [Fact]
    public void SortsSpecialFoldersFirstInFixedOrderRegardlessOfInputOrder()
    {
        var folders = new[]
        {
            new StoreFolder { Id = 1, ParentId = null, Name = "Itens excluídos", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.DeletedItems },
            new StoreFolder { Id = 2, ParentId = null, Name = "Itens enviados", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.SentItems },
            new StoreFolder { Id = 3, ParentId = null, Name = "Caixa de saída", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.Outbox },
            new StoreFolder { Id = 4, ParentId = null, Name = "Rascunho", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.Drafts },
            new StoreFolder { Id = 5, ParentId = null, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.Inbox },
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        Assert.Equal(
            new[] { "Caixa de entrada", "Rascunho", "Caixa de saída", "Itens enviados", "Itens excluídos" },
            vm.RootNodes.Select(n => n.DisplayName));
    }

    [Fact]
    public void SortsOrdinaryFoldersAlphabeticallyAfterSpecials()
    {
        var folders = new[]
        {
            new StoreFolder { Id = 1, ParentId = null, Name = "Zebra", IsLoadable = true, HasBackingFile = true },
            new StoreFolder { Id = 2, ParentId = null, Name = "Árvore", IsLoadable = true, HasBackingFile = true },
            new StoreFolder { Id = 3, ParentId = null, Name = "banana", IsLoadable = true, HasBackingFile = true },
            new StoreFolder { Id = 4, ParentId = null, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.Inbox },
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        // "Caixa de entrada" ordenaria depois de "Árvore" alfabeticamente, mas é
        // especial -- o rank de especial tem prioridade sobre a ordem alfabética.
        Assert.Equal(
            new[] { "Caixa de entrada", "Árvore", "banana", "Zebra" },
            vm.RootNodes.Select(n => n.DisplayName));
    }

    [Fact]
    public void SortsNumberedFoldersNaturallyNotLexically()
    {
        var folders = new[]
        {
            new StoreFolder { Id = 1, ParentId = null, Name = "Pasta 10", IsLoadable = true, HasBackingFile = true },
            new StoreFolder { Id = 2, ParentId = null, Name = "Pasta 2", IsLoadable = true, HasBackingFile = true },
            new StoreFolder { Id = 3, ParentId = null, Name = "Pasta 1", IsLoadable = true, HasBackingFile = true },
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        Assert.Equal(
            new[] { "Pasta 1", "Pasta 2", "Pasta 10" },
            vm.RootNodes.Select(n => n.DisplayName));
    }

    [Fact]
    public void SortingAppliesToSubfoldersToo()
    {
        var folders = new[]
        {
            Folder(1, null, "Pastas locais"),
            Folder(2, 1, "Zebra"),
            Folder(3, 1, "Amora"),
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        var root = vm.RootNodes.Single();
        Assert.Equal(new[] { "Amora", "Zebra" }, root.Children.Select(n => n.DisplayName));
    }

    [Fact]
    public void CyclePromotedRootsAreSortedTogetherWithOrdinaryRoots()
    {
        var folders = new[]
        {
            Folder(1, 2, "Zebra"),
            Folder(2, 1, "Amora"),
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        Assert.Equal(new[] { "Amora", "Zebra" }, vm.RootNodes.Select(n => n.DisplayName));
    }
}
