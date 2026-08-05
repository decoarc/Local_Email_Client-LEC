// src/LEC.App/Converters/MoveFolderDestinationValidityConverter.cs
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LEC.App.Services;
using LEC.App.ViewModels;

namespace LEC.App.Converters;

/// <summary>
/// XAML-side adapter for MoveFolderDestinationRules.IsValidDestination -- grays a folder's
/// label instead of disabling its TreeViewItem container, same reasoning as
/// MoveDestinationValidityConverter (IsEnabled cascades to every descendant in WPF). Now a
/// multi-value converter (was single-value) so XAML can supply both the candidate node and
/// the ProfileViewModel of the TreeView it renders in -- needed since validity now depends on
/// whether the candidate's profile matches the source's. SourceNode/SourceProfile are set
/// once by MoveFolderDialog's constructor, before any of its TreeViews first bind.
/// </summary>
public sealed class MoveFolderDestinationValidityConverter : IMultiValueConverter
{
    public FolderNodeViewModel? SourceNode { get; set; }
    public ProfileViewModel? SourceProfile { get; set; }

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (SourceNode is null || SourceProfile is null
            || values is not [FolderNodeViewModel candidate, ProfileViewModel candidateProfile])
        {
            return Brushes.Gray;
        }

        bool isValid = MoveFolderDestinationRules.IsValidDestination(candidate, candidateProfile, SourceNode, SourceProfile);
        return isValid ? Brushes.Black : Brushes.Gray;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
