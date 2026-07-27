// tests/LEC.App.Tests/MainWindowViewModelTests.cs
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class MainWindowViewModelTests
{
    [Fact]
    public void OpenProfileWithANonExistentPathSetsAStatusMessageInsteadOfThrowing()
    {
        var vm = new MainWindowViewModel();

        vm.OpenProfile(Path.Combine(Path.GetTempPath(), $"lec-does-not-exist-{Guid.NewGuid():N}"));

        Assert.Null(vm.Store);
        Assert.NotNull(vm.StatusText);
    }

    [Fact]
    public async Task SelectFolderAsyncIsANoOpWhenNoProfileIsOpenYet()
    {
        var vm = new MainWindowViewModel();
        var node = new FolderNodeViewModel(new StoreFolder { Id = 1, Name = "X", IsLoadable = true });

        await vm.SelectFolderAsync(node); // must not throw

        Assert.Null(vm.MessageList);
    }
}
