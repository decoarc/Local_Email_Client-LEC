// tests/LEC.App.Tests/MoveDestinationRulesTests.cs
using LEC.App.Services;
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class MoveDestinationRulesTests
{
    private static ProfileViewModel MakeProfile(string name, params StoreFolder[] folders) =>
        new(name, $@"C:\perfis\{name}", new FakeMailStore(folders: folders));

    [Fact]
    public void FolderWithoutBackingFileIsInvalid()
    {
        var folder = new StoreFolder { Id = 1, Name = "Pastas locais", IsLoadable = true, HasBackingFile = false };
        var profile = MakeProfile("P", folder);
        var sourceProfile = MakeProfile("Origem", new StoreFolder { Id = 99, Name = "Origem", IsLoadable = true, HasBackingFile = true });

        Assert.False(MoveDestinationRules.IsValidDestination(folder, profile, sourceProfile, sourceFolderId: 99));
    }

    [Fact]
    public void FolderThatIsNotLoadableIsInvalid()
    {
        var folder = new StoreFolder { Id = 1, Name = "Quebrada", IsLoadable = false, HasBackingFile = true };
        var profile = MakeProfile("P", folder);
        var sourceProfile = MakeProfile("Origem", new StoreFolder { Id = 99, Name = "Origem", IsLoadable = true, HasBackingFile = true });

        Assert.False(MoveDestinationRules.IsValidDestination(folder, profile, sourceProfile, sourceFolderId: 99));
    }

    [Fact]
    public void TheMessagesOwnSourceFolderInTheSameProfileIsInvalid()
    {
        var sourceFolder = new StoreFolder { Id = 5, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var profile = MakeProfile("P", sourceFolder);

        Assert.False(MoveDestinationRules.IsValidDestination(sourceFolder, profile, profile, sourceFolderId: 5));
    }

    [Fact]
    public void AFolderWithTheSameIdInADifferentProfileIsValid()
    {
        // Folder IDs are only unique within one profile's Folders.dbx -- two different open
        // profiles can (and often will) both have a folder with Id == 5.
        var sourceFolder = new StoreFolder { Id = 5, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var sourceProfile = MakeProfile("Origem", sourceFolder);

        var otherFolder = new StoreFolder { Id = 5, Name = "Destino", IsLoadable = true, HasBackingFile = true };
        var otherProfile = MakeProfile("Destino", otherFolder);

        Assert.True(MoveDestinationRules.IsValidDestination(otherFolder, otherProfile, sourceProfile, sourceFolderId: 5));
    }

    [Fact]
    public void AnOrdinaryDifferentFolderInTheSameProfileIsValid()
    {
        var sourceFolder = new StoreFolder { Id = 1, Name = "Origem", IsLoadable = true, HasBackingFile = true };
        var otherFolder = new StoreFolder { Id = 2, Name = "Destino", IsLoadable = true, HasBackingFile = true };
        var profile = MakeProfile("P", sourceFolder, otherFolder);

        Assert.True(MoveDestinationRules.IsValidDestination(otherFolder, profile, profile, sourceFolderId: 1));
    }
}
