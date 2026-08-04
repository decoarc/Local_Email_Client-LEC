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
        var folder = new StoreFolder { Id = 1, Name = "X", IsLoadable = true, HasBackingFile = true };
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
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
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
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
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
        var folder = new StoreFolder { Id = 1, Name = "X", IsLoadable = true, HasBackingFile = true };
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
        var folder = new StoreFolder { Id = 1, Name = "X", IsLoadable = true, HasBackingFile = true };
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
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
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

    [Fact]
    public async Task MoveMessageAsyncCopiesToDestinationAndHidesFromSource()
    {
        var sourceFolder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        byte[] raw = "conteudo"u8.ToArray();
        var messageRef = new MessageRef(sourceFolder.Id, 500);
        var sourceStore = new FakeMailStore(
            folders: new[] { sourceFolder },
            rawByRef: new Dictionary<MessageRef, byte[]> { [messageRef] = raw });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destFolder = new StoreFolder { Id = 10, Name = "Destino", IsLoadable = true, HasBackingFile = true };
        var destStore = new FakeMailStore(folders: new[] { destFolder });
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var header = new MessageHeader { Id = messageRef, Subject = "Assunto", SenderName = "Ana", SenderAddress = "ana@x.com", MessageId = "<m1>", Date = new DateTime(2004, 1, 1) };
        var viewModel = new MainWindowViewModel();

        await viewModel.MoveMessageAsync(sourceProfile, header, destProfile, destFolder.Id);

        var added = Assert.Single(destStore.AddedMessages);
        Assert.Equal(destFolder.Id, added.FolderId);
        Assert.Equal(raw, added.RawMessage);
        Assert.Equal("Assunto", added.Subject);
        Assert.Equal("Ana", added.SenderName);

        Assert.Contains(messageRef, sourceStore.HiddenMessageRefs);
    }

    [Fact]
    public async Task MoveMessageAsyncWorksWhenSourceAndDestinationAreTheSameProfile()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
        var messageRef = new MessageRef(folderA.Id, 500);
        var store = new FakeMailStore(
            folders: new[] { folderA, folderB },
            rawByRef: new Dictionary<MessageRef, byte[]> { [messageRef] = "x"u8.ToArray() });
        var profile = new ProfileViewModel("P", @"C:\perfis\P", store);

        var header = new MessageHeader { Id = messageRef, Subject = "S" };
        var viewModel = new MainWindowViewModel();

        await viewModel.MoveMessageAsync(profile, header, profile, folderB.Id);

        Assert.Single(store.AddedMessages);
        Assert.Contains(messageRef, store.HiddenMessageRefs);
    }

    [Fact]
    public async Task MoveMessageAsyncDoesNotHideFromSourceWhenTheDestinationAddFails()
    {
        var sourceFolder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var messageRef = new MessageRef(sourceFolder.Id, 500);
        var sourceStore = new FakeMailStore(
            folders: new[] { sourceFolder },
            rawByRef: new Dictionary<MessageRef, byte[]> { [messageRef] = "x"u8.ToArray() });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destFolder = new StoreFolder { Id = 10, Name = "Destino", IsLoadable = true, HasBackingFile = true };
        var destStore = new FakeMailStore(folders: new[] { destFolder })
        {
            ExceptionToThrowOnAddMessage = new InvalidOperationException("destino cheio"),
        };
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var header = new MessageHeader { Id = messageRef, Subject = "S" };
        var viewModel = new MainWindowViewModel();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.MoveMessageAsync(sourceProfile, header, destProfile, destFolder.Id));

        Assert.Empty(sourceStore.HiddenMessageRefs);
    }

    [Fact]
    public async Task MoveMessageAsyncIntoTheMessagesOwnFolderThrowsAndTouchesNothing()
    {
        var folder = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var messageRef = new MessageRef(folder.Id, 500);
        var store = new FakeMailStore(
            folders: new[] { folder },
            rawByRef: new Dictionary<MessageRef, byte[]> { [messageRef] = "x"u8.ToArray() });
        var profile = new ProfileViewModel("P", @"C:\perfis\P", store);

        var header = new MessageHeader { Id = messageRef, Subject = "S" };
        var viewModel = new MainWindowViewModel();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.MoveMessageAsync(profile, header, profile, folder.Id));

        // The guard must run before any I/O: a self-move would otherwise rebuild the folder
        // with a duplicate copy and then hide the original at an offset the rebuild invalidated.
        Assert.Empty(store.AddedMessages);
        Assert.Empty(store.HiddenMessageRefs);
    }
}
