// src/LEC.App/Converters/SpecialToVisibilityConverter.cs
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LEC.App.Converters;

/// <summary>
/// Hides a folder-tree context menu item for special folders (FolderNodeViewModel.IsSpecial
/// == true) -- Inbox/Sent/Drafts/Outbox/Deleted Items can never be deleted or moved (see
/// DbxStore.DeleteFolderAsync/MoveFolderAsync), so the menu never offers the option rather
/// than letting the user pick it and see an error.
/// </summary>
public sealed class SpecialToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
