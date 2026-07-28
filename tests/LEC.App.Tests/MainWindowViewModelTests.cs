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

    [Fact]
    public void CurrentFolderScavengeStatusStartsNullWhenNoProfileIsOpen()
    {
        // MainWindowViewModel.OpenProfile takes a real profile *directory path*, not an
        // IMailStore -- there's no seam to inject FakeMailStore here at the OpenProfile
        // level (same constraint every existing MainWindowViewModel test already works
        // within). The underlying DbxStore scavenge behavior is already covered directly
        // by DbxStoreScavengeTests; this only checks the no-profile-open state.
        var vm = new MainWindowViewModel();

        Assert.Null(vm.CurrentFolderScavengeStatus);
    }
}
