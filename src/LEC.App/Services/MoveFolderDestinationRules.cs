// src/LEC.App/Services/MoveFolderDestinationRules.cs
using LEC.App.ViewModels;

namespace LEC.App.Services;

/// <summary>
/// Whether a folder node is a legal destination for MainWindowViewModel/ProfileViewModel's
/// MoveFolderAsync, checked upfront so MoveFolderDialog can gray out invalid folders
/// instead of letting the user pick one and fail after the fact. Walks the already-built
/// FolderNodeViewModel tree (not DbxStore.Folders' flat ParentId list) because the dialog
/// already has that tree in memory for the source profile -- no need to re-derive ancestry
/// the way DbxStore.MoveFolderAsync's own cycle guard does on the flat list.
/// This does NOT replace DbxStore.MoveFolderAsync's own guards -- a name collision at the
/// destination still isn't cheaply knowable here and surfaces as a normal error instead.
/// </summary>
public static class MoveFolderDestinationRules
{
    public static bool IsValidDestination(FolderNodeViewModel candidate, FolderNodeViewModel sourceNode)
    {
        if (ReferenceEquals(candidate, sourceNode))
            return false;

        return !ContainsDescendant(sourceNode, candidate);
    }

    private static bool ContainsDescendant(FolderNodeViewModel node, FolderNodeViewModel candidate)
    {
        foreach (var child in node.Children)
        {
            if (ReferenceEquals(child, candidate) || ContainsDescendant(child, candidate))
                return true;
        }
        return false;
    }
}
