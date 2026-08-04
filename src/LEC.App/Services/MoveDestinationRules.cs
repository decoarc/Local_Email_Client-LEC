// src/LEC.App/Services/MoveDestinationRules.cs
using LEC.App.ViewModels;
using LEC.MailStore;

namespace LEC.App.Services;

/// <summary>
/// Whether a folder is a legal destination for MainWindowViewModel.MoveMessageAsync,
/// checked upfront so MoveMessageDialog can disable invalid folders instead of letting
/// the user pick one and fail after the fact. This does NOT replace
/// DbxStore.AddMessageAsync's own runtime guards -- a damaged or already-full (255
/// message) destination still isn't cheaply knowable here (it would mean opening every
/// folder's backing file just to draw the dialog) and surfaces as a normal error instead.
/// </summary>
public static class MoveDestinationRules
{
    public static bool IsValidDestination(StoreFolder folder, ProfileViewModel profile,
        ProfileViewModel sourceProfile, int sourceFolderId)
    {
        if (!folder.HasBackingFile || !folder.IsLoadable)
            return false;

        // Folder IDs are only unique within one profile -- the same Id in a different
        // profile is a completely different folder, and is a valid destination.
        return !ReferenceEquals(profile, sourceProfile) || folder.Id != sourceFolderId;
    }
}
