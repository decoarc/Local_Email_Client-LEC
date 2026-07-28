using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LEC.MailStore;

namespace LEC.App.Converters;

public sealed class RecoveredKindToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RecoveredMessageKind ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RecoveredKindToLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RecoveredMessageKind.Recovered => "Recuperada",
        RecoveredMessageKind.Fragment => "Fragmento",
        _ => null,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RecoveredKindToRowBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RecoveredMessageKind ? Brushes.Gray : Brushes.Black;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
