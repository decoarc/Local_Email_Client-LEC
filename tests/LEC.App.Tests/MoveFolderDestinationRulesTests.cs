// tests/LEC.App.Tests/MoveFolderDestinationRulesTests.cs
using LEC.App.Services;
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class MoveFolderDestinationRulesTests
{
    private static FolderNodeViewModel Node(int id, string name = "F") =>
        new(new StoreFolder { Id = id, Name = name, IsLoadable = true, HasBackingFile = true });

    private static ProfileViewModel MakeProfile(string name) =>
        new(name, $@"C:\perfis\{name}", new FakeMailStore(folders: Array.Empty<StoreFolder>()));

    [Fact]
    public void TheSourceFolderItselfIsInvalid()
    {
        var profile = MakeProfile("P");
        var source = Node(1);

        Assert.False(MoveFolderDestinationRules.IsValidDestination(source, profile, source, profile));
    }

    [Fact]
    public void ADirectChildOfTheSourceInTheSameProfileIsInvalid()
    {
        var profile = MakeProfile("P");
        var source = Node(1);
        var child = Node(2);
        source.Children.Add(child);

        Assert.False(MoveFolderDestinationRules.IsValidDestination(child, profile, source, profile));
    }

    [Fact]
    public void AGrandchildOfTheSourceInTheSameProfileIsInvalid()
    {
        var profile = MakeProfile("P");
        var source = Node(1);
        var child = Node(2);
        var grandchild = Node(3);
        child.Children.Add(grandchild);
        source.Children.Add(child);

        Assert.False(MoveFolderDestinationRules.IsValidDestination(grandchild, profile, source, profile));
    }

    [Fact]
    public void AnUnrelatedFolderInTheSameProfileIsValid()
    {
        var profile = MakeProfile("P");
        var source = Node(1);
        var other = Node(2);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(other, profile, source, profile));
    }

    [Fact]
    public void TheSourcesCurrentParentInTheSameProfileIsValid()
    {
        // Moving a folder back under its own current parent is a harmless no-op reorder,
        // not a cycle -- the parent is not the source and not one of its descendants.
        var profile = MakeProfile("P");
        var parent = Node(1);
        var source = Node(2);
        parent.Children.Add(source);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(parent, profile, source, profile));
    }

    [Fact]
    public void ASiblingOfTheSourceInTheSameProfileIsValid()
    {
        var profile = MakeProfile("P");
        var parent = Node(1);
        var source = Node(2);
        var sibling = Node(3);
        parent.Children.Add(source);
        parent.Children.Add(sibling);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(sibling, profile, source, profile));
    }

    [Fact]
    public void AFolderInADifferentProfileIsAlwaysValidEvenIfItWouldBeADescendantInThatProfilesOwnTree()
    {
        // candidate is a "child" of source in this test's tree structure -- but candidateProfile
        // differs from sourceProfile, so the two trees are unrelated and the self/descendant
        // check must never even run. A folder in a different profile is never part of the
        // source's subtree by definition, regardless of what its own local tree looks like.
        var sourceProfile = MakeProfile("Origem");
        var otherProfile = MakeProfile("Destino");
        var source = Node(1);
        var candidateThatLooksLikeAChild = Node(2);
        source.Children.Add(candidateThatLooksLikeAChild);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(candidateThatLooksLikeAChild, otherProfile, source, sourceProfile));
    }

    [Fact]
    public void TheSameNodeReferenceInADifferentProfileIsValid()
    {
        // Degenerate case: same FolderNodeViewModel reference as the source, but tagged with a
        // different profile in this call -- profile mismatch wins over reference equality,
        // since a real dialog can never actually produce this combination (a node only ever
        // renders inside one profile's tree), but the rule itself must not special-case it
        // into a false "self" match.
        var sourceProfile = MakeProfile("Origem");
        var otherProfile = MakeProfile("Destino");
        var source = Node(1);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(source, otherProfile, source, sourceProfile));
    }
}
