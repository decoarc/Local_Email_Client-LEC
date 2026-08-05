// tests/LEC.App.Tests/MainWindowViewModelTests.cs
using System.Linq;
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
    public async Task ClearCurrentFolderClearsMessageListReaderAndScavengeStatus()
    {
        var folder = new StoreFolder { Id = 1, Name = "X", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfil", store);

        var vm = new MainWindowViewModel();
        vm.OpenProfiles.Add(profile);
        await vm.SelectFolderAsync(profile, new FolderNodeViewModel(folder));

        vm.ClearCurrentFolder();

        Assert.Null(vm.MessageList);
        Assert.Null(vm.MessageReader);
        Assert.Null(vm.CurrentFolderScavengeStatus);
        // Unlike CloseProfile, ClearCurrentFolder leaves the profile itself open/active --
        // it only resets the message list / reading pane for whatever folder was showing.
        Assert.Same(profile, vm.ActiveProfile);
        Assert.Contains(profile, vm.OpenProfiles);
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

    [Fact]
    public async Task DeleteMessageAsyncCallsTheStoresDeleteMessageAsync()
    {
        var folder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var messageRef = new MessageRef(folder.Id, 500);
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfis\Perfil", store);

        var header = new MessageHeader { Id = messageRef, Subject = "Assunto" };
        var viewModel = new MainWindowViewModel();

        await viewModel.DeleteMessageAsync(profile, header);

        Assert.Contains(messageRef, store.DeletedMessageRefs);
    }

    [Fact]
    public async Task DeleteMessagesAsyncDeletesEachMessageInTheGivenOrder()
    {
        var folder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfis\Perfil", store);

        var ref1 = new MessageRef(folder.Id, 1);
        var ref2 = new MessageRef(folder.Id, 2);
        var ref3 = new MessageRef(folder.Id, 3);
        var messages = new List<MessageHeader>
        {
            new() { Id = ref1, Subject = "Um" },
            new() { Id = ref2, Subject = "Dois" },
            new() { Id = ref3, Subject = "Tres" },
        };

        var viewModel = new MainWindowViewModel();

        await viewModel.DeleteMessagesAsync(profile, messages);

        Assert.Contains(ref1, store.DeletedMessageRefs);
        Assert.Contains(ref2, store.DeletedMessageRefs);
        Assert.Contains(ref3, store.DeletedMessageRefs);
    }

    [Fact]
    public async Task DeleteMessagesAsyncStopsAtTheFirstFailureLeavingEarlierSuccessesInPlace()
    {
        var folder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfis\Perfil", store);

        var ref1 = new MessageRef(folder.Id, 1);
        var ref2 = new MessageRef(folder.Id, 2);
        var ref3 = new MessageRef(folder.Id, 3);
        store.MessageRefToFailOn = ref2;
        var messages = new List<MessageHeader>
        {
            new() { Id = ref1, Subject = "Um" },
            new() { Id = ref2, Subject = "Dois" },
            new() { Id = ref3, Subject = "Tres" },
        };

        var viewModel = new MainWindowViewModel();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.DeleteMessagesAsync(profile, messages));

        Assert.Contains("1 de 3", ex.Message);
        Assert.Contains(ref1, store.DeletedMessageRefs); // succeeded before the failure
        Assert.DoesNotContain(ref2, store.DeletedMessageRefs); // this one failed
        Assert.DoesNotContain(ref3, store.DeletedMessageRefs); // never attempted
    }

    [Fact]
    public async Task DeleteMessagesAsyncWithASingleMessageBehavesLikeDeleteMessageAsync()
    {
        var folder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { folder });
        var profile = new ProfileViewModel("Perfil", @"C:\perfis\Perfil", store);

        var messageRef = new MessageRef(folder.Id, 1);
        var messages = new List<MessageHeader> { new() { Id = messageRef, Subject = "Um" } };

        var viewModel = new MainWindowViewModel();

        await viewModel.DeleteMessagesAsync(profile, messages);

        Assert.Contains(messageRef, store.DeletedMessageRefs);
    }

    [Fact]
    public async Task MoveMessagesAsyncMovesEachMessageToTheSameDestination()
    {
        var sourceFolder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var ref1 = new MessageRef(sourceFolder.Id, 1);
        var ref2 = new MessageRef(sourceFolder.Id, 2);
        var sourceStore = new FakeMailStore(
            folders: new[] { sourceFolder },
            rawByRef: new Dictionary<MessageRef, byte[]>
            {
                [ref1] = "um"u8.ToArray(),
                [ref2] = "dois"u8.ToArray(),
            });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destFolder = new StoreFolder { Id = 10, Name = "Destino", IsLoadable = true, HasBackingFile = true };
        var destStore = new FakeMailStore(folders: new[] { destFolder });
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var messages = new List<MessageHeader>
        {
            new() { Id = ref1, Subject = "Um" },
            new() { Id = ref2, Subject = "Dois" },
        };
        var viewModel = new MainWindowViewModel();

        await viewModel.MoveMessagesAsync(sourceProfile, messages, destProfile, destFolder.Id);

        Assert.Equal(2, destStore.AddedMessages.Count);
        Assert.All(destStore.AddedMessages, m => Assert.Equal(destFolder.Id, m.FolderId));
        Assert.Contains(ref1, sourceStore.HiddenMessageRefs);
        Assert.Contains(ref2, sourceStore.HiddenMessageRefs);
    }

    [Fact]
    public async Task MoveMessagesAsyncStopsAtTheFirstFailureAndDoesNotHideTheRemainingMessages()
    {
        var sourceFolder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var ref1 = new MessageRef(sourceFolder.Id, 1);
        var ref2 = new MessageRef(sourceFolder.Id, 2);
        var ref3 = new MessageRef(sourceFolder.Id, 3);
        var sourceStore = new FakeMailStore(
            folders: new[] { sourceFolder },
            rawByRef: new Dictionary<MessageRef, byte[]>
            {
                [ref1] = "um"u8.ToArray(),
                [ref2] = "dois"u8.ToArray(),
                [ref3] = "tres"u8.ToArray(),
            });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destFolder = new StoreFolder { Id = 10, Name = "Destino", IsLoadable = true, HasBackingFile = true };
        var destStore = new FakeMailStore(folders: new[] { destFolder }) { SubjectToFailOn = "Dois" };
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var messages = new List<MessageHeader>
        {
            new() { Id = ref1, Subject = "Um" },
            new() { Id = ref2, Subject = "Dois" },
            new() { Id = ref3, Subject = "Tres" },
        };
        var viewModel = new MainWindowViewModel();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.MoveMessagesAsync(sourceProfile, messages, destProfile, destFolder.Id));

        Assert.Contains("1 de 3", ex.Message);
        Assert.Contains(ref1, sourceStore.HiddenMessageRefs); // succeeded before the failure
        Assert.DoesNotContain(ref2, sourceStore.HiddenMessageRefs); // this one failed to copy, so never hidden
        Assert.DoesNotContain(ref3, sourceStore.HiddenMessageRefs); // never attempted
        Assert.Single(destStore.AddedMessages); // only "Um" made it to the destination
    }

    [Fact]
    public async Task MoveMessagesAsyncWithASingleMessageBehavesLikeMoveMessageAsync()
    {
        var sourceFolder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var messageRef = new MessageRef(sourceFolder.Id, 1);
        var sourceStore = new FakeMailStore(
            folders: new[] { sourceFolder },
            rawByRef: new Dictionary<MessageRef, byte[]> { [messageRef] = "um"u8.ToArray() });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destFolder = new StoreFolder { Id = 10, Name = "Destino", IsLoadable = true, HasBackingFile = true };
        var destStore = new FakeMailStore(folders: new[] { destFolder });
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var messages = new List<MessageHeader> { new() { Id = messageRef, Subject = "Um" } };
        var viewModel = new MainWindowViewModel();

        await viewModel.MoveMessagesAsync(sourceProfile, messages, destProfile, destFolder.Id);

        Assert.Single(destStore.AddedMessages);
        Assert.Contains(messageRef, sourceStore.HiddenMessageRefs);
    }

    [Fact]
    public async Task MoveFolderAsyncWithinTheSameProfileDelegatesToProfileViewModel()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { folderA, folderB });
        var profile = new ProfileViewModel("P", @"C:\perfis\P", store);

        var viewModel = new MainWindowViewModel();

        await viewModel.MoveFolderAsync(profile, profile.FolderTree.RootNodes.Single(n => n.Folder.Id == folderA.Id),
            profile, folderB.Id);

        Assert.Contains((folderA.Id, (int?)folderB.Id), store.MovedFolders);
        Assert.Empty(store.AddedMessages);
        Assert.Empty(store.DeletedFolderIds);
    }

    [Fact]
    public async Task MoveFolderAsyncAcrossProfilesCopiesTheWholeSubtreeExcludingRecoveredItemsAndHidesTheSource()
    {
        var rootFolder = new StoreFolder { Id = 1, ParentId = null, Name = "Notas", IsLoadable = true, HasBackingFile = true };
        var childFolder = new StoreFolder { Id = 2, ParentId = 1, Name = "2026", IsLoadable = true, HasBackingFile = true };

        var normalRootRef = new MessageRef(1, 100);
        var recoveredRootRef = new MessageRef(1, 101);
        var normalChildRef = new MessageRef(2, 200);

        var sourceStore = new FakeMailStore(
            folders: new[] { rootFolder, childFolder },
            headersByFolder: new()
            {
                [1] = new()
                {
                    new MessageHeader { Id = normalRootRef, Subject = "Oi", SenderName = "Ana", MessageId = "<m1>", Date = new DateTime(2004, 1, 1) },
                    new MessageHeader { Id = recoveredRootRef, Subject = "Fragmento", RecoveredKind = RecoveredMessageKind.Recovered },
                },
                [2] = new()
                {
                    new MessageHeader { Id = normalChildRef, Subject = "Sub" },
                },
            },
            // Deliberately no entry for recoveredRootRef: if the implementation ever tried to
            // copy it, OpenMessageAsync would throw "No raw bytes registered" and fail this test.
            rawByRef: new Dictionary<MessageRef, byte[]>
            {
                [normalRootRef] = "conteudo-raiz"u8.ToArray(),
                [normalChildRef] = "conteudo-filha"u8.ToArray(),
            });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destStore = new FakeMailStore(folders: Array.Empty<StoreFolder>());
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var viewModel = new MainWindowViewModel();
        var sourceNode = sourceProfile.FolderTree.RootNodes.Single();

        await viewModel.MoveFolderAsync(sourceProfile, sourceNode, destProfile, destinationParentId: null);

        var destNotas = Assert.Single(destStore.Folders, f => f.Name == "Notas");
        var dest2026 = Assert.Single(destStore.Folders, f => f.Name == "2026");
        Assert.Equal(destNotas.Id, dest2026.ParentId);

        Assert.Equal(2, destStore.AddedMessages.Count); // recovered item excluded
        Assert.Contains(destStore.AddedMessages, m => m.FolderId == destNotas.Id && m.Subject == "Oi");
        Assert.Contains(destStore.AddedMessages, m => m.FolderId == dest2026.Id && m.Subject == "Sub");

        Assert.Contains(rootFolder.Id, sourceStore.DeletedFolderIds);
        Assert.Contains(destProfile.FolderTree.RootNodes, n => n.DisplayName == "Notas");
    }

    [Fact]
    public async Task MoveFolderAsyncAcrossProfilesRejectsASpecialSourceFolderBeforeCopyingAnything()
    {
        var specialFolder = new StoreFolder { Id = 1, Name = "Caixa de entrada", SpecialFolderKind = SpecialFolderKind.Inbox, IsLoadable = true, HasBackingFile = true };
        var sourceStore = new FakeMailStore(folders: new[] { specialFolder });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destStore = new FakeMailStore(folders: Array.Empty<StoreFolder>());
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var viewModel = new MainWindowViewModel();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.MoveFolderAsync(sourceProfile, sourceProfile.FolderTree.RootNodes.Single(), destProfile, null));

        Assert.Empty(destStore.Folders);
        Assert.Empty(sourceStore.DeletedFolderIds);
    }

    [Fact]
    public async Task MoveFolderAsyncAcrossProfilesRejectsWhenASpecialFolderIsNestedInTheSubtree()
    {
        var rootFolder = new StoreFolder { Id = 1, ParentId = null, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var specialChild = new StoreFolder { Id = 2, ParentId = 1, Name = "Enviados", SpecialFolderKind = SpecialFolderKind.SentItems, IsLoadable = true, HasBackingFile = true };
        var sourceStore = new FakeMailStore(folders: new[] { rootFolder, specialChild });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destStore = new FakeMailStore(folders: Array.Empty<StoreFolder>());
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var viewModel = new MainWindowViewModel();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.MoveFolderAsync(sourceProfile, sourceProfile.FolderTree.RootNodes.Single(), destProfile, null));

        Assert.Empty(destStore.Folders);
        Assert.Empty(sourceStore.DeletedFolderIds);
    }

    [Fact]
    public async Task MoveFolderAsyncAcrossProfilesRejectsWhenTwoDescendantsShareANameInternally()
    {
        var rootFolder = new StoreFolder { Id = 1, ParentId = null, Name = "Raiz", IsLoadable = true, HasBackingFile = true };
        var subA = new StoreFolder { Id = 2, ParentId = 1, Name = "Sub A", IsLoadable = true, HasBackingFile = true };
        var subB = new StoreFolder { Id = 3, ParentId = 1, Name = "Sub B", IsLoadable = true, HasBackingFile = true };
        var subA2026 = new StoreFolder { Id = 4, ParentId = 2, Name = "2026", IsLoadable = true, HasBackingFile = true };
        var subB2026 = new StoreFolder { Id = 5, ParentId = 3, Name = "2026", IsLoadable = true, HasBackingFile = true };
        var sourceStore = new FakeMailStore(folders: new[] { rootFolder, subA, subB, subA2026, subB2026 });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destStore = new FakeMailStore(folders: Array.Empty<StoreFolder>());
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var viewModel = new MainWindowViewModel();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.MoveFolderAsync(sourceProfile, sourceProfile.FolderTree.RootNodes.Single(), destProfile, null));

        Assert.Empty(destStore.Folders);
        Assert.Empty(sourceStore.DeletedFolderIds);
    }

    [Fact]
    public async Task MoveFolderAsyncAcrossProfilesRejectsWhenTheDestinationAlreadyHasAFolderWithThatName()
    {
        var rootFolder = new StoreFolder { Id = 1, ParentId = null, Name = "Notas", IsLoadable = true, HasBackingFile = true };
        var childFolder = new StoreFolder { Id = 2, ParentId = 1, Name = "2026", IsLoadable = true, HasBackingFile = true };
        var sourceStore = new FakeMailStore(folders: new[] { rootFolder, childFolder });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        // "2026" already exists in the destination, tucked under an unrelated parent -- the
        // constraint is profile-wide, not scoped to the chosen destination parent.
        var existingUnrelated = new StoreFolder { Id = 10, ParentId = null, Name = "Arquivo", IsLoadable = true, HasBackingFile = true };
        var existingConflict = new StoreFolder { Id = 11, ParentId = 10, Name = "2026", IsLoadable = true, HasBackingFile = true };
        var destStore = new FakeMailStore(folders: new[] { existingUnrelated, existingConflict });
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var viewModel = new MainWindowViewModel();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.MoveFolderAsync(sourceProfile, sourceProfile.FolderTree.RootNodes.Single(), destProfile, null));

        Assert.Equal(2, destStore.Folders.Count);
        Assert.Empty(sourceStore.DeletedFolderIds);
    }

    [Fact]
    public async Task MoveFolderAsyncAcrossProfilesDoesNotHideTheSourceWhenACopyStepFailsAndLeavesThePartialCopyInPlace()
    {
        var rootFolder = new StoreFolder { Id = 1, ParentId = null, Name = "Notas", IsLoadable = true, HasBackingFile = true };
        var messageRef = new MessageRef(1, 100);
        var sourceStore = new FakeMailStore(
            folders: new[] { rootFolder },
            headersByFolder: new() { [1] = new() { new MessageHeader { Id = messageRef, Subject = "Oi" } } },
            rawByRef: new Dictionary<MessageRef, byte[]> { [messageRef] = "conteudo"u8.ToArray() });
        var sourceProfile = new ProfileViewModel("Origem", @"C:\perfis\Origem", sourceStore);

        var destStore = new FakeMailStore(folders: Array.Empty<StoreFolder>())
        {
            ExceptionToThrowOnAddMessage = new InvalidOperationException("pasta de destino cheia"),
        };
        var destProfile = new ProfileViewModel("Destino", @"C:\perfis\Destino", destStore);

        var viewModel = new MainWindowViewModel();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.MoveFolderAsync(sourceProfile, sourceProfile.FolderTree.RootNodes.Single(), destProfile, null));

        // The folder itself was created before the message copy failed -- it stays behind as
        // an accepted partial duplicate; only hiding the source is refused.
        Assert.Single(destStore.Folders);
        Assert.Empty(sourceStore.DeletedFolderIds);
    }
}
