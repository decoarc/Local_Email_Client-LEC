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
/// MoveDestinationValidityConverter (IsEnabled cascades to every descendant in WPF, so
/// disabling the source folder would also swallow its own valid siblings' subtrees if the
/// wrong node were ever picked). SourceNode is set once by MoveFolderDialog's constructor,
/// before its single TreeView first binds.
/// </summary>
public sealed class MoveFolderDestinationValidityConverter : IValueConverter
{
    public FolderNodeViewModel? SourceNode { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (SourceNode is null || value is not FolderNodeViewModel candidate)
            return Brushes.Gray;

        bool isValid = MoveFolderDestinationRules.IsValidDestination(candidate, SourceNode);
        return isValid ? Brushes.Black : Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
