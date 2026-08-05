// src/LEC.App/Services/MoveFolderDestinationRules.cs
using LEC.App.ViewModels;

namespace LEC.App.Services;

/// <summary>
/// Whether a folder node is a legal destination for MainWindowViewModel.MoveFolderAsync,
/// checked upfront so MoveFolderDialog can gray out invalid folders instead of letting the
/// user pick one and fail after the fact. A folder in a different profile than the source is
/// always valid -- it can never be part of the source's own subtree by definition, regardless
/// of its position in that other profile's tree. Within the source's own profile, the
/// existing self/descendant exclusion still applies, walking the already-built
/// FolderNodeViewModel tree (not DbxStore.Folders' flat ParentId list) because the dialog
/// already has that tree in memory for every open profile.
/// This does NOT replace the motor's own guards (DbxStore.MoveFolderAsync's cycle/name-
/// collision checks, MainWindowViewModel.MoveFolderAsync's special-folder guard) -- those
/// still run for real when the move actually happens and surface as normal errors here.
/// </summary>
public static class MoveFolderDestinationRules
{
    public static bool IsValidDestination(FolderNodeViewModel candidate, ProfileViewModel candidateProfile,
        FolderNodeViewModel sourceNode, ProfileViewModel sourceProfile)
    {
        if (!ReferenceEquals(candidateProfile, sourceProfile))
            return true;

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
