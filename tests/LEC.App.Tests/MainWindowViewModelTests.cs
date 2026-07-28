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
        Assert.Empty(vm.OpenProfiles);
        Assert.NotNull(vm.StatusText);
    }

    private static string? FindRealFixtureDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LEC.sln")))
            dir = dir.Parent;

        if (dir is null)
            return null;

        string candidate = Path.Combine(dir.FullName, "BCP");
        return Directory.Exists(candidate) ? candidate : null;
    }

    [Fact]
    public void RealFixture_OpeningTheSameProfileTwiceAddsTwoIndependentEntries()
    {
        string? profileDir = FindRealFixtureDirectory();
        if (profileDir is null)
            return; // BCP/ not present on this machine — soft skip

        var vm = new MainWindowViewModel();

        vm.OpenProfile(profileDir);
        vm.OpenProfile(profileDir);

        Assert.Equal(2, vm.OpenProfiles.Count);
        Assert.NotSame(vm.OpenProfiles[0].Store, vm.OpenProfiles[1].Store);

        foreach (var profile in vm.OpenProfiles)
            profile.Store.Dispose();
    }

    [Fact]
    public async Task SelectFolderAsyncMakesTheProfileActiveAndLoadsItsMessageList()
    {
        var folder = new StoreFolder { Id = 1, Name = "X", IsLoadable = true };
        var header = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Ola" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { header } });
        var profile = new ProfileViewModel("Perfil A", @"C:\perfil-a", store);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profile);

        await vm.SelectFolderAsync(profile, new FolderNodeViewModel(folder));

        Assert.Same(store, vm.Store);
        Assert.NotNull(vm.MessageList);
        Assert.Single(vm.MessageList!.Messages);
        Assert.Equal("Ola", vm.MessageList.Messages[0].Subject);
    }

    [Fact]
    public async Task SelectingAnotherFolderInTheSameProfileReusesTheSameMessageListInstance()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true };
        var store = new FakeMailStore(folders: new[] { folderA, folderB });
        var profile = new ProfileViewModel("Perfil", @"C:\perfil", store);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profile);

        await vm.SelectFolderAsync(profile, new FolderNodeViewModel(folderA));
        var firstMessageList = vm.MessageList;

        await vm.SelectFolderAsync(profile, new FolderNodeViewModel(folderB));

        Assert.Same(firstMessageList, vm.MessageList);
    }

    [Fact]
    public async Task SelectingAFolderInADifferentProfileSwitchesTheActiveStoreAndReplacesMessageListAndReader()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true };
        var storeA = new FakeMailStore(folders: new[] { folderA });
        var storeB = new FakeMailStore(folders: new[] { folderB });
        var profileA = new ProfileViewModel("Perfil A", @"C:\a", storeA);
        var profileB = new ProfileViewModel("Perfil B", @"C:\b", storeB);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profileA);
        vm.OpenProfiles.Add(profileB);

        await vm.SelectFolderAsync(profileA, new FolderNodeViewModel(folderA));
        var messageListA = vm.MessageList;
        var messageReaderA = vm.MessageReader;

        await vm.SelectFolderAsync(profileB, new FolderNodeViewModel(folderB));

        Assert.Same(storeB, vm.Store);
        Assert.NotSame(messageListA, vm.MessageList);
        Assert.NotSame(messageReaderA, vm.MessageReader);
    }

    [Fact]
    public async Task CloseProfileRemovesItFromOpenProfilesAndDisposesItsStore()
    {
        var folder = new StoreFolder { Id = 1, Name = "X", IsLoadable = true };
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfil", store);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profile);
        await vm.SelectFolderAsync(profile, new FolderNodeViewModel(folder));

        vm.CloseProfile(profile);

        Assert.DoesNotContain(profile, vm.OpenProfiles);
        Assert.True(store.WasDisposed);
    }

    [Fact]
    public async Task CloseProfileOnTheActiveProfileClearsMessageListReaderAndScavengeStatus()
    {
        var folder = new StoreFolder { Id = 1, Name = "X", IsLoadable = true };
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfil", store);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profile);
        await vm.SelectFolderAsync(profile, new FolderNodeViewModel(folder));

        vm.CloseProfile(profile);

        Assert.Null(vm.Store);
        Assert.Null(vm.MessageList);
        Assert.Null(vm.MessageReader);
        Assert.Null(vm.CurrentFolderScavengeStatus);
    }

    [Fact]
    public async Task CloseProfileOnANonActiveProfileLeavesTheActiveProfileUntouched()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true };
        var storeA = new FakeMailStore(folders: new[] { folderA });
        var storeB = new FakeMailStore(folders: new[] { folderB });
        var profileA = new ProfileViewModel("Perfil A", @"C:\a", storeA);
        var profileB = new ProfileViewModel("Perfil B", @"C:\b", storeB);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profileA);
        vm.OpenProfiles.Add(profileB);
        await vm.SelectFolderAsync(profileA, new FolderNodeViewModel(folderA));
        var messageListA = vm.MessageList;

        vm.CloseProfile(profileB);

        Assert.Same(storeA, vm.Store);
        Assert.Same(messageListA, vm.MessageList);
        Assert.Contains(profileA, vm.OpenProfiles);
        Assert.DoesNotContain(profileB, vm.OpenProfiles);
    }
}
