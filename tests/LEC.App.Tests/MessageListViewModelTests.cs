// tests/LEC.App.Tests/MessageListViewModelTests.cs
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class MessageListViewModelTests
{
    [Fact]
    public async Task LoadsHeadersFromTheStoreForTheSelectedFolder()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true };
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
        var folder = new StoreFolder { Id = 1, Name = "Quebrada", IsLoadable = false };
        var store = new FakeMailStore(folders: new[] { folder });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        Assert.Empty(vm.Messages);
        Assert.NotNull(vm.StatusText);
    }

    [Fact]
    public async Task ClearsPreviousMessagesWhenLoadingADifferentFolder()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true };
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
}
