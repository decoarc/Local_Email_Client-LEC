// src/LEC.App/Converters/AttachmentListToVisibilityConverter.cs
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using LEC.App.Services;

namespace LEC.App.Converters;

public sealed class AttachmentListToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IReadOnlyList<AttachmentInfo> { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
