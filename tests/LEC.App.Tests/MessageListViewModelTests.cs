// tests/LEC.App.Tests/MessageListViewModelTests.cs
using System.Linq;
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class MessageListViewModelTests
{
    [Fact]
    public async Task LoadsHeadersFromTheStoreForTheSelectedFolder()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var header = new MessageHeader { Id = new MessageRef(1, 100), Subject = "Ola" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { header } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        Assert.Single(vm.Messages);
        Assert.Equal("Ola", vm.Messages[0].Subject);
        Assert.Null(vm.StatusText);
    }

    [Fact]
    public async Task SetsAStatusMessageForANonLoadableFolder()
    {
        var folder = new StoreFolder { Id = 1, Name = "Quebrada", IsLoadable = false, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { folder });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        Assert.Empty(vm.Messages);
        Assert.NotNull(vm.StatusText);
    }

    [Fact]
    public async Task ClearsPreviousMessagesWhenLoadingADifferentFolder()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(
            folders: new[] { folderA, folderB },
            headersByFolder: new()
            {
                [1] = new() { new MessageHeader { Id = new MessageRef(1, 1), Subject = "Da A" } },
            });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folderA);
        Assert.Single(vm.Messages);

        await vm.LoadAsync(folderB);
        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task SortsMessagesNewestFirstByDate()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var older = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Antigo", Date = new DateTime(2001, 1, 1) };
        var newer = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Novo", Date = new DateTime(2005, 1, 1) };
        var noDate = new MessageHeader { Id = new MessageRef(1, 3), Subject = "SemData", Date = null };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { older, newer, noDate } }); // deliberately out of order

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        Assert.Equal(new[] { "Novo", "Antigo", "SemData" }, vm.Messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task SortBySenderOrdersAlphabeticallyAscendingOnFirstClick()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var bravo = new MessageHeader { Id = new MessageRef(1, 1), Subject = "B", SenderName = "Bravo" };
        var alfa = new MessageHeader { Id = new MessageRef(1, 2), Subject = "A", SenderName = "alfa" };
        var charlie = new MessageHeader { Id = new MessageRef(1, 3), Subject = "C", SenderName = "Charlie" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { bravo, alfa, charlie } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SortBySender();

        Assert.Equal(new[] { "A", "B", "C" }, vm.Messages.Select(m => m.Subject));
        Assert.Equal("De ▲", vm.SenderColumnHeader);
        Assert.Equal("Data", vm.DateColumnHeader);
    }

    [Fact]
    public async Task SortBySenderTwiceReversesToDescending()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var alfa = new MessageHeader { Id = new MessageRef(1, 1), Subject = "A", SenderName = "Alfa" };
        var bravo = new MessageHeader { Id = new MessageRef(1, 2), Subject = "B", SenderName = "Bravo" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { alfa, bravo } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SortBySender();
        vm.SortBySender();

        Assert.Equal(new[] { "B", "A" }, vm.Messages.Select(m => m.Subject));
        Assert.Equal("De ▼", vm.SenderColumnHeader);
    }

    [Fact]
    public async Task SortByDateOnceFromDefaultReversesToOldestFirst()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var older = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Antigo", Date = new DateTime(2001, 1, 1) };
        var newer = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Novo", Date = new DateTime(2005, 1, 1) };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { older, newer } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder); // default: Date descendente (mais recente primeiro)

        vm.SortByDate(); // mesma coluna já ativa -> inverte

        Assert.Equal(new[] { "Antigo", "Novo" }, vm.Messages.Select(m => m.Subject));
        Assert.Equal("Data ▲", vm.DateColumnHeader);
        Assert.Equal("De", vm.SenderColumnHeader);
    }

    [Fact]
    public async Task SwitchingColumnAlwaysStartsAscendingRegardlessOfPriorDirection()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var older = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Antigo", Date = new DateTime(2001, 1, 1), SenderName = "Zeta" };
        var newer = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Novo", Date = new DateTime(2005, 1, 1), SenderName = "Alfa" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { older, newer } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SortByDate();   // Date -> ascendente (mais antiga primeiro)
        vm.SortByDate();   // Date -> descendente (mais recente primeiro)
        vm.SortBySender(); // troca de coluna -> sempre ascendente, mesmo vindo de Date descendente

        Assert.Equal(new[] { "Novo", "Antigo" }, vm.Messages.Select(m => m.Subject)); // Alfa, Zeta
        Assert.Equal("De ▲", vm.SenderColumnHeader);
        Assert.Equal("Data", vm.DateColumnHeader);
    }

    [Fact]
    public async Task SortChoicePersistsAcrossFolderReload()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(
            folders: new[] { folderA, folderB },
            headersByFolder: new()
            {
                [1] = new() { new MessageHeader { Id = new MessageRef(1, 1), Subject = "A1", SenderName = "Bravo" } },
                [2] = new()
                {
                    new MessageHeader { Id = new MessageRef(2, 1), Subject = "B-Bravo", SenderName = "Bravo" },
                    new MessageHeader { Id = new MessageRef(2, 2), Subject = "B-Alfa", SenderName = "Alfa" },
                },
            });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folderA);
        vm.SortBySender();

        await vm.LoadAsync(folderB);

        Assert.Equal(new[] { "B-Alfa", "B-Bravo" }, vm.Messages.Select(m => m.Subject));
        Assert.Equal("De ▲", vm.SenderColumnHeader);
    }
}
