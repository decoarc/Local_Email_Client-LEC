// src/LEC.App/Converters/MoveDestinationValidityConverter.cs
using System.Globalization;
using System.Windows.Data;
using LEC.App.Services;
using LEC.App.ViewModels;
using LEC.MailStore;

namespace LEC.App.Converters;

/// <summary>
/// XAML-side adapter for MoveDestinationRules.IsValidDestination. SourceProfile/
/// SourceFolderId are set once by MoveMessageDialog's constructor, before its TreeViews
/// first bind; the two MultiBinding values (a node's StoreFolder, and the ProfileViewModel
/// of the TreeView it's in) are re-evaluated per node as each profile's tree renders.
/// </summary>
public sealed class MoveDestinationValidityConverter : IMultiValueConverter
{
    public ProfileViewModel? SourceProfile { get; set; }
    public int SourceFolderId { get; set; }

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (SourceProfile is null || values is not [StoreFolder folder, ProfileViewModel profile])
            return false;

        return MoveDestinationRules.IsValidDestination(folder, profile, SourceProfile, SourceFolderId);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
