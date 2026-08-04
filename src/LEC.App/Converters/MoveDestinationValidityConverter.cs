// src/LEC.App/Converters/MoveDestinationValidityConverter.cs
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LEC.App.Services;
using LEC.App.ViewModels;
using LEC.MailStore;

namespace LEC.App.Converters;

/// <summary>
/// XAML-side adapter for MoveDestinationRules.IsValidDestination -- grays a folder's label
/// instead of disabling its TreeViewItem container, because IsEnabled coerces down to every
/// descendant in WPF (disabling an invalid parent would make its valid children
/// unreachable too, since real profiles nest every real folder under one placeholder root
/// like "Pastas locais"). MoveMessageDialog's own TreeView_SelectedItemChanged is what
/// actually enforces the rule -- this converter is purely visual. SourceProfile/
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
            return Brushes.Gray;

        bool isValid = MoveDestinationRules.IsValidDestination(folder, profile, SourceProfile, SourceFolderId);
        return isValid ? Brushes.Black : Brushes.Gray;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
