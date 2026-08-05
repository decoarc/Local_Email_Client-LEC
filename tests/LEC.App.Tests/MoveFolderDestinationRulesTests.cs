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

    [Fact]
    public void TheSourceFolderItselfIsInvalid()
    {
        var source = Node(1);

        Assert.False(MoveFolderDestinationRules.IsValidDestination(source, source));
    }

    [Fact]
    public void ADirectChildOfTheSourceIsInvalid()
    {
        var source = Node(1);
        var child = Node(2);
        source.Children.Add(child);

        Assert.False(MoveFolderDestinationRules.IsValidDestination(child, source));
    }

    [Fact]
    public void AGrandchildOfTheSourceIsInvalid()
    {
        var source = Node(1);
        var child = Node(2);
        var grandchild = Node(3);
        child.Children.Add(grandchild);
        source.Children.Add(child);

        Assert.False(MoveFolderDestinationRules.IsValidDestination(grandchild, source));
    }

    [Fact]
    public void AnUnrelatedFolderIsValid()
    {
        var source = Node(1);
        var other = Node(2);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(other, source));
    }

    [Fact]
    public void TheSourcesCurrentParentIsValid()
    {
        // Moving a folder back under its own current parent is a harmless no-op reorder,
        // not a cycle -- the parent is not the source and not one of its descendants.
        var parent = Node(1);
        var source = Node(2);
        parent.Children.Add(source);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(parent, source));
    }

    [Fact]
    public void ASiblingOfTheSourceIsValid()
    {
        var parent = Node(1);
        var source = Node(2);
        var sibling = Node(3);
        parent.Children.Add(source);
        parent.Children.Add(sibling);

        Assert.True(MoveFolderDestinationRules.IsValidDestination(sibling, source));
    }
}
